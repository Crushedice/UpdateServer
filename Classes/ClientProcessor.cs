using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Signature;
using Sentry;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateServer.Classes
{
    public class ClientProcessor : IDisposable
    {
        #region Fields and Properties
        public UpdateClient _client { get; }
        private int _disposed;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _processingTask = Task.CompletedTask;
        public bool IsProcessingComplete => _processingTask?.IsCompleted ?? true;
        #endregion

        #region Constructor
        public ClientProcessor(UpdateClient user)
        {
            _client = user;

            SentrySdk.CaptureMessage("New Client Created", s =>
            {
                s.SetExtra("ClientGuid", _client.Guid.ToString());
                s.SetExtra("ClientIP", _client.ClientIp);
                s.SetExtra("ClientFolder", _client.ClientFolder);
                s.SetExtra("ClientDeltaZip", _client.ClientDeltaZip);
            });
        }
        #endregion

        #region Public Methods
        public void Notify()
        {
            send("Waiting in Queue....");
        }

        public void StartupThisOne()
        {
            SentrySdk.AddBreadcrumb("Startupthisone");
            // Task.Run keeps the (partially synchronous) pipeline off the caller's
            // thread, which may be a WatsonTcp event thread or hold the queue lock.
            _processingTask = Task.Run(RunPipelineAsync);
        }
        #endregion

        #region Core Processing Methods
        private async Task RunPipelineAsync()
        {
            try
            {
                await PrepairClientAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SentrySdk.CaptureMessage("Client processing cancelled", s =>
                {
                    s.SetExtra("ClientGuid", _client.Guid.ToString());
                });
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex, s =>
                {
                    s.SetExtra("ClientGuid", _client.Guid.ToString());
                    s.SetExtra("ClientIP", _client.ClientIp);
                });
                send("Update failed on the server. Please reconnect to retry.");
            }
            finally
            {
                // Always free the processing slot, even when the pipeline failed;
                // otherwise a single failed client permanently occupies one of the
                // MaxProcessors slots and the queue starves.
                UpdateServerEntity.ReleaseProcessor(this);
            }
        }

        private async Task PrepairClientAsync()
        {
            SentrySdk.AddBreadcrumb("PrepairClient");
            int copycount = 0;

            foreach (var deltaItem in _client.GetMatchedDeltasSnapshot())
            {
                if (ProcessMatchedDelta(deltaItem))
                    copycount++;
            }

            LogPrepairClientCompletion(copycount);

            if (_client.GetMatchedDeltasCount() >= _client.GetMismatchedFileHashesCount() || _client.GetTrimmedFileHashesCount() < 1)
            {
                await EndThisOne().ConfigureAwait(false);
                return;
            }

            await CreateDeltaforClient().ConfigureAwait(false);
        }

        private bool ProcessMatchedDelta(KeyValuePair<string, string> deltaItem)
        {
            string deltapath = Path.Combine(UpdateServerEntity.DeltaStorage, deltaItem.Key);
            string destpath = Path.Combine(_client.ClientFolder, "Rust", deltaItem.Value + ".octodelta");
            string deltaOutputDirectory = Path.GetDirectoryName(destpath);

            if (!Directory.Exists(deltaOutputDirectory))
                Directory.CreateDirectory(deltaOutputDirectory);

            try
            {
                File.Copy(deltapath, destpath, true);
                _client.AddDataToSend(destpath);
                return true;
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex, s =>
                {
                    s.SetExtra("StoredDelta", deltapath);
                    s.SetExtra("Destination", destpath);
                    s.SetExtra("ClientGuid", _client.Guid.ToString());
                });
#if DEBUG
                throw;
#endif
                return false;
            }
        }

        private void LogPrepairClientCompletion(int copycount)
        {
            SentrySdk.CaptureMessage("PrepairClient completed", s =>
            {
                s.SetExtra("Copycount ", copycount);
                s.SetExtra("MatchedDeltasCount", _client.GetMatchedDeltasSnapshot());
                s.SetExtra("MissmatchedFileHashes", _client.GetMismatchedFileHashesSnapshot());
                s.SetExtra("Trimmed List", _client.GetTrimmedFileHashesSnapshot());
            });
        }

        private async Task EndThisOne()
        {
            UpdateServerEntity.Puts("Await Done Ending thisone");
            await CreateZipFile().ConfigureAwait(false);
        }
        #endregion

        #region Delta Creation Methods
        private async Task CreateDeltaforClient()
        {
            var transaction = SentrySdk.StartTransaction(
                "delta_creation",
                "client.delta_creation",
                $"Creating delta files for client {_client.Guid}"
            );

            SentrySdk.AddBreadcrumb("CreateDeltaFor client");

            try
            {
                _cts.Token.ThrowIfCancellationRequested();
                send("Starting Delta Creation");

                string[] allDeltas = Directory.GetFiles(_client.ClientFolder, "*", SearchOption.AllDirectories);

                await ProcessDeltaFilesParallel(allDeltas).ConfigureAwait(false);

                SentrySdk.CaptureMessage("DeltaFinished", s =>
                {
                    s.SetTag("ClientGuid", _client.Guid.ToString());
                    s.SetExtra("DeltaFilesCount", allDeltas.Length);
                });

                await EndThisOne().ConfigureAwait(false);
                transaction.Finish();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                transaction.Finish(ex);
                throw;
            }
            finally
            {
                if (!transaction.IsFinished)
                    transaction.Finish();
            }
        }

        private async Task ProcessDeltaFilesParallel(string[] allDeltas)
        {
            int allc = allDeltas.Length;
            int prg = 0;

            var deltaFilesSpan = SentrySdk.StartTransaction("delta.creation_loop", "Delta creation loop");

            // Use SemaphoreSlim to control degree of parallelism
            int maxDegree = Math.Min(Math.Max(Environment.ProcessorCount, 2), 8);
            using (var semaphore = new SemaphoreSlim(maxDegree))
            {
                var tasks = allDeltas.Select(async x =>
                {
                    await semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        var result = await ProcessDeltaFileAsync(x).ConfigureAwait(false);
                        int currentProgress = Interlocked.Increment(ref prg);
                        if (result.success)
                        {
                            var size = GetBytesReadable(new FileInfo(x).Length);
                            send($"Delta Progress: {currentProgress} / {allc} --- {size}");
                            UpdateServerEntity.Puts($"Waiting for {currentProgress} / {allc}");
                        }

                        return result;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            deltaFilesSpan.Finish();
        }

        private async Task<(bool success, string deltaFilePath)> ProcessDeltaFileAsync(string filePath)
        {
            try
            {
                _cts.Token.ThrowIfCancellationRequested();

                if (!UpdateServerEntity.server.IsClientConnected(_client.Guid))
                {
                    SentrySdk.CaptureMessage("Client Disconnected during Delta Creation", s =>
                    {
                        s.SetExtra("ClientGuid", _client.Guid.ToString());
                    });
                    return (false, null);
                }

                if (filePath.Contains(".zip")) return (true, null);

                string relativePath = filePath.Replace(_client.ClientFolder, string.Empty);
                string newFilePath = Path.Combine(
                    Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\.."),
                    relativePath.Replace(".octosig", string.Empty).TrimStart('\\', '/'));

                string origfile = FixPath(relativePath.Replace(".octosig", string.Empty)
                    .Replace(".octodelta", string.Empty))
                    .Replace(@"\\", @"\")
                    .Replace(@"\Rust\", string.Empty);

                if (!File.Exists(newFilePath)) return (true, null);

                SentrySdk.AddBreadcrumb($"Creating delta for {filePath}");

                string localDeltaFilePath = await CreateDeltaFileAsync(filePath, relativePath, newFilePath).ConfigureAwait(false);

                if (string.IsNullOrEmpty(localDeltaFilePath)) return (true, null);

                await ProcessDeltaHashAsync(localDeltaFilePath, origfile).ConfigureAwait(false);

                return (true, localDeltaFilePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                send($"Error In creating Delta for {filePath}");
                Console.WriteLine($"Error in Create Delta: {e.Message}");
                SentrySdk.CaptureException(e, scope =>
                {
                    scope.SetExtra("File", filePath);
                    scope.SetExtra("ClientGuid", _client.Guid.ToString());
                });
#if DEBUG
                throw;
#endif
                return (false, null);
            }
        }

        private async Task<string> CreateDeltaFileAsync(string signatureFilePath, string relativePath, string newFilePath)
        {
            _client.AddFileToDelete(signatureFilePath);

            string localDeltaFilePath = Path.Combine(_client.ClientFolder, relativePath.Replace(".octosig", ".octodelta").TrimStart('\\', '/'));
            string deltaOutputDirectory = Path.GetDirectoryName(localDeltaFilePath);

            if (!Directory.Exists(deltaOutputDirectory))
                Directory.CreateDirectory(deltaOutputDirectory);

            var deltaBuilder = new DeltaBuilder();
            using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var signatureStream = new FileStream(signatureFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var deltaStream = new FileStream(localDeltaFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await deltaBuilder.BuildDeltaAsync(newFileStream,
                    new SignatureReader(signatureStream, null),
                    new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream))).ConfigureAwait(false);
            }

            _client.AddDataToSend(localDeltaFilePath);
            return localDeltaFilePath;
        }

        private async Task ProcessDeltaHashAsync(string deltaFilePath, string origfile)
        {
            var hashSpan = SentrySdk.StartTransaction("delta.hash", $"Hash for {Path.GetFileName(deltaFilePath)}");

            try
            {
                string deltahash = await GetXxHash3Async(deltaFilePath).ConfigureAwait(false);
                var sighash = _client.GetMismatchedFileHash(origfile);
                string deltaStoragePath = Path.Combine(UpdateServerEntity.DeltaStorage, deltahash);

                if (!File.Exists(deltaStoragePath))
                {
                    // Publish into DeltaStorage via temp file + rename so a concurrent
                    // reader (another client being served the same stored delta) can
                    // never observe a half-written file.
                    string tempPath = deltaStoragePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.Copy(deltaFilePath, tempPath);
                        File.Move(tempPath, deltaStoragePath);
                    }
                    catch (IOException) when (File.Exists(deltaStoragePath))
                    {
                        // Another worker published the same delta first - ours is redundant.
                    }
                    finally
                    {
                        if (File.Exists(tempPath))
                            try { File.Delete(tempPath); } catch (IOException) { }
                    }
                }

                if (!string.IsNullOrEmpty(sighash))
                {
                    var deltaInfo = new Dictionary<string, string> { { deltahash, origfile } };
                    UpdateServerEntity.DeltaFileStorage.TryAdd(sighash, deltaInfo);
                }
            }
            finally
            {
                hashSpan.Finish();
            }
        }
        #endregion

        #region Zip File Operations
        private async Task CreateZipFile()
        {
            var transaction = SentrySdk.StartTransaction(
                "zip_creation",
                "client.deltafiles_zip",
                $"Creating zip file for client {_client.Guid}"
            );

            try
            {
                _cts.Token.ThrowIfCancellationRequested();
                send("Making ZipFile");

                int allitems = _client.GetDataToSendCount();

                string zipFileName = _client.ClientDeltaZip;
                if (File.Exists(zipFileName)) File.Delete(zipFileName);

                SentrySdk.AddBreadcrumb("Deltazip", "Info", "", _client.GetMismatchedFileHashesSnapshot());

                try
                {
                    CreateZipFileContent(zipFileName, allitems);
                }
                catch
                {
                    // Never leave (or send) a partially written zip behind.
                    if (File.Exists(zipFileName))
                        try { File.Delete(zipFileName); } catch (IOException) { }
                    throw;
                }

                _client.AddFileToDelete(zipFileName);
                await SendZipFile(zipFileName, _cts.Token).ConfigureAwait(false);

                transaction.Finish();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                transaction.Finish(ex);
                throw;
            }
            finally
            {
                if (!transaction.IsFinished)
                    transaction.Finish();
            }
        }

        private void CreateZipFileContent(string zipFileName, int allitems)
        {
            int itemcount = 0;
            var deltaFilesSpan = SentrySdk.StartTransaction("zip.deltafiles", "Packing delta files");

            try
            {
                using (ZipArchive zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
                {
                    foreach (string filePath in _client.GetDataToSendSnapshot())
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        if (!File.Exists(filePath)) continue;

                        // Extract relative path properly, removing ClientFolder and Rust directory
                        string entryName = filePath.Replace(_client.ClientFolder, string.Empty).TrimStart('\\', '/');
                        if (entryName.StartsWith("Rust", StringComparison.OrdinalIgnoreCase))
                        {
                            entryName = entryName.Substring(4).TrimStart('\\', '/');
                        }
                        string fixedname = entryName.Replace('\\', '/');
                        zip.CreateEntryFromFile(filePath, fixedname, CompressionLevel.Optimal);
                        itemcount++;

                        send($"Packing Update: {itemcount} / {allitems}");
                    }
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                // A zip missing entries would silently leave the client with stale
                // files, so packing errors abort the update instead of being skipped.
                Console.WriteLine($"Error in pack zip: {e.Message}");
                SentrySdk.CaptureException(e, s =>
                {
                    s.SetExtra("ZipFile", zipFileName);
                    s.SetExtra("ClientGuid", _client.Guid.ToString());
                });
                throw;
            }
            finally
            {
                deltaFilesSpan.Finish();
            }
        }

        private async Task SendZipFile(string zip, CancellationToken cancellationToken)
        {
            var transaction = SentrySdk.StartTransaction(
                "send_zip_file",
                "client.send_delta_zip",
                $"Sending zip file for client {_client.Guid}"
            );

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var metadata = new Dictionary<string, object> { { "1", "2" } };
                using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
                {
                    await UpdateServerEntity.server.SendAsync(_client.Guid, source.Length, source, metadata, cancellationToken).ConfigureAwait(false);
                }

                UpdateServerEntity.EndCall(_client, this, zip);
                transaction.Finish();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                transaction.Finish(ex);
                throw;
            }
            finally
            {
                if (!transaction.IsFinished)
                    transaction.Finish();
            }
        }
        #endregion

        #region Utility Methods
        private static async Task<string> GetXxHash3Async(string filename)
        {
            var transaction = SentrySdk.StartTransaction(
                "xxhash3_calculation",
                "client.xxhash3",
                $"Calculating xxHash3 for file {filename}"
            );

            try
            {
                using (FileStream inputStream = await OpenFileForSharedReadAsync(filename).ConfigureAwait(false))
                {
                    var xxHash = new XxHash128();
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(2097152);

                    try
                    {
                        int bytesRead;
                        var readSpan = transaction.StartChild("xxhash3.read", "Reading file for hash");
                        while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            xxHash.Append(buffer.AsSpan(0, bytesRead));
                        }
                        readSpan.Finish();

                        byte[] hash = xxHash.GetHashAndReset();

                        transaction.Finish();
                        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                transaction.Finish(ex);
                throw;
            }
            finally
            {
                if (!transaction.IsFinished)
                    transaction.Finish();
            }
        }

        private static async Task<FileStream> OpenFileForSharedReadAsync(string filename)
        {
            const int maxAttempts = 5;
            const int bufferSize = 2097152;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return new FileStream(
                        filename,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize,
                        useAsync: true);
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(150 * attempt).ConfigureAwait(false);
                }
            }

            return new FileStream(
                filename,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize,
                useAsync: true);
        }

        private string FixPath(string path)
        {
            char[] separators = { '\\', '/' };
            int index = path.IndexOfAny(separators);
            if (index < 0) return path;
            path = path[index] == '\\' ? path.Replace('/', '\\') : path.Replace('\\', '/');
            return path;
        }

        private void send(string msg)
        {
            // Fire-and-forget: SendProgress catches and logs its own failures, so a
            // slow or disconnected client cannot stall the processing pipeline.
            _ = UpdateServerEntity.SendProgress(_client.Guid, msg);
        }

        /// <summary>
        /// Returns the human-readable file size for an arbitrary, 64-bit file size.
        /// The default format is "0.### XB", e.g. "4.2 KB" or "1.434 GB"
        /// </summary>
        public string GetBytesReadable(long i)
        {
            // Get absolute value
            long absolute_i = (i < 0 ? -i : i);
            // Determine the suffix and readable value
            string suffix;
            double readable;

            if (absolute_i >= 0x1000000000000000) // Exabyte
            {
                suffix = "EB";
                readable = (i >> 50);
            }
            else if (absolute_i >= 0x4000000000000) // Petabyte
            {
                suffix = "PB";
                readable = (i >> 40);
            }
            else if (absolute_i >= 0x10000000000) // Terabyte
            {
                suffix = "TB";
                readable = (i >> 30);
            }
            else if (absolute_i >= 0x40000000) // Gigabyte
            {
                suffix = "GB";
                readable = (i >> 20);
            }
            else if (absolute_i >= 0x100000) // Megabyte
            {
                suffix = "MB";
                readable = (i >> 10);
            }
            else if (absolute_i >= 0x400) // Kilobyte
            {
                suffix = "KB";
                readable = i;
            }
            else
            {
                return i.ToString("0 B"); // Byte
            }

            // Divide by 1024 to get fractional value
            readable = (readable / 1024);
            // Return formatted number with suffix
            return readable.ToString("0.### ") + suffix;
        }
        #endregion

        #region Dispose Pattern
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (_processingTask?.IsCompleted ?? true)
            {
                _cts.Dispose();
            }
            else
            {
                _ = _processingTask.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
            }

            SentrySdk.AddBreadcrumb("ClientProcessor Disposed", "dispose", _client.Guid.ToString());
        }
        #endregion
    }
}
