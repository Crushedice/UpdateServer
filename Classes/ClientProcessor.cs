using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Signature;
using Sentry;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UpdateServer.Classes;

namespace UpdateServer.Classes
{
    public class ClientProcessor : IDisposable
    {
        public string tcpipport;
        private ClientProcessor _instanceRef;
        private bool _running;
        private int NrInQueue;
        public UpdateClient _client { get; }

        private bool _disposed = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        private static readonly object _dataToSendLock = new object();
        private static readonly object _fileToDeleteLock = new object();

        public ClientProcessor(UpdateClient user)
        {
            _client = user;
            tcpipport = user.ClientIP;
            _instanceRef = this;
            SentrySdk.CaptureMessage("New Client Created", s =>
            {
                s.SetExtra("ClientGuid", _client._guid.ToString());
                s.SetExtra("ClientIP", _client.ClientIP);
                s.SetExtra("ClientFolder", _client.ClientFolder);
                s.SetExtra("ClientDeltaZip", _client.Clientdeltazip);
            });
        }

        private async Task CreateZipFile(bool additionalfiles = false)
        {
            // Start Sentry transaction for zip creation
            var transaction = SentrySdk.StartTransaction(
                "zip_creation",
                additionalfiles ? "client.additionalfiles_zip" : "client.deltafiles_zip",
                $"Creating zip file for client {_client._guid} (additionalfiles={additionalfiles})"
            );
            try
            {
                _cts.Token.ThrowIfCancellationRequested();
                int retrycount = 0;
                send("Making ZipFile");
                int allitems;
                lock (_dataToSendLock) { allitems = _client.dataToSend.Count(); }
                string zipFileName = _client.Clientdeltazip;
                if (File.Exists(zipFileName)) File.Delete(zipFileName);
                string clientRustFolder = _client.ClientFolder + "\\Rust\\";
                SentrySdk.CaptureMessage("Deltazip", scope =>
                {
                    // Replace the problematic line with the following:
                    UpdateServerEntity.SetExtrasFromList(scope, _client.dataToSend);
                    scope.AddBreadcrumb(zipFileName);

                });
                int itemcount = 0;
                try
                {
                    var deltaFilesSpan = transaction.StartChild("zip.deltafiles", "Packing delta files");
                    using (ZipArchive zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
                    {
                        List<string> dataToSendCopy;
                        lock (_dataToSendLock) { dataToSendCopy = new List<string>(_client.dataToSend); }
                        foreach (string x in dataToSendCopy)
                            try
                            {
                                _cts.Token.ThrowIfCancellationRequested();
                                if (!File.Exists(x)) continue;
                                string relativePath = x.Replace(_client.ClientFolder + "\\Rust\\", string.Empty);
                                string fixedname = relativePath.Replace('\\', '/');
                                zip.CreateEntryFromFile(x, fixedname, CompressionLevel.Optimal);
                                itemcount++;

                                send($"Packing Update: {itemcount} / {allitems}");
                            }
                            catch (Exception ex)
                            {
                                SentrySdk.CaptureException(ex);
                                Console.WriteLine($"Error packing file: {ex.Message}");
                                FileLogger.LogError($"Error packing file: {ex.Message}");
                            }
                    }
                    deltaFilesSpan.Finish();
                    lock (_fileToDeleteLock) { _client.filetoDelete.Add(zipFileName); }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error in pack zip: {e.Message}");
                    FileLogger.LogError($"Error in pack zip: {e.Message}");
                    SentrySdk.CaptureException(e);
                }
                await SendZipFile(zipFileName, false, _cts.Token);
                transaction.Finish();
            }
            catch (OperationCanceledException)
            {
                SentrySdk.CaptureMessage("CreateZipFile cancelled");
                transaction.Finish();
            }
            catch (Exception ex)
            {
                FileLogger.LogError("Error in CreateZipFile");
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

        public void Notify()
        {
            send("Waiting in Queue....");
        }

        public void StartupThisOne()
        {
            SentrySdk.AddBreadcrumb("Startupthisone");
            _running = true;
            PrepairClient();
        }

        private static async Task<string> GetXxHash3Async(string filename)
        {
            // Start Sentry transaction for hash calculation
            var transaction = SentrySdk.StartTransaction(
                "xxhash3_calculation",
                "client.xxhash3",
                $"Calculating xxHash3 for file {filename}"
            );
            try
            {
                using (FileStream inputStream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read,
                           bufferSize: 2097152, useAsync: true))
                {
                    var xxHash = new XxHash128();
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(2097152);

                    try
                    {
                        int bytesRead;
                        var readSpan = transaction.StartChild("xxhash3.read", "Reading file for hash");
                        while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            xxHash.Append(buffer.AsSpan(0, bytesRead));
                        }
                        readSpan.Finish();

                        var hashSpan = transaction.StartChild("xxhash3.compute", "Computing hash");
                        byte[] hash = xxHash.GetHashAndReset();
                        hashSpan.Finish();

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

        // Replace the method signature and implementation for CreateDeltaforClient
        private async void CreateDeltaforClient()
        {
            // Start Sentry transaction for delta creation
            var transaction = SentrySdk.StartTransaction(
                "delta_creation",
                "client.delta_creation",
                $"Creating delta files for client {_client._guid}"
            );
            SentrySdk.AddBreadcrumb("CreateDeltaFor client");
            try
            {
                _cts.Token.ThrowIfCancellationRequested();
                send("Starting Delta Creation");

                string[] allDeltas = Directory.GetFiles(_client.ClientFolder, "*", SearchOption.AllDirectories);
                int allc = allDeltas.Count();
                int prg = 0;
                string _deltahash = "";
                string deltaFilePath = "";

                var deltaFilesSpan = transaction.StartChild("delta.creation_loop", "Delta creation loop");

                foreach (string x in allDeltas)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    if (!UpdateServerEntity.server.IsClientConnected(this._client._guid))
                    {
                        SentrySdk.CaptureMessage("Client Disconnected during Delta Creation", s =>
                        {
                            s.SetExtra("ClientGuid", _client._guid.ToString());
                        });
                        deltaFilesSpan.Finish();
                        transaction.Finish();
                        return;
                    }
                    int currentProgress = Interlocked.Increment(ref prg);
                    
                    if (x.Contains(".zip")) continue;
                    string relativePath = x.Replace(_client.ClientFolder, string.Empty);
                    string newFilePath = Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\..") + relativePath.Replace(".octosig", string.Empty);
                    string origfile = FixPath(x.Replace(_client.ClientFolder, string.Empty).Replace(".octodelta", string.Empty).Replace(".octosig", string.Empty)).Replace(@"\\", @"\").Replace(@"\Rust\", string.Empty);
                    string OrigPath = origfile;

                    try
                    {
                        if (!x.Contains(".zip"))
                        {
                            _client.filetoDelete.Add(x);
                            string filename = Path.GetFileName(x);

                            if (!File.Exists(newFilePath)) continue;
                            var inf = new FileInfo(x);
                            var size = GetBytesReadable(inf.Length);
                            send($"Delta Progress: {currentProgress} / {allc} --- {size}");
                            deltaFilePath = _client.ClientFolder + relativePath.Replace(".octosig", ".octodelta");
                            string deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
                            if (!Directory.Exists(deltaOutputDirectory))
                                Directory.CreateDirectory(deltaOutputDirectory);

                            var fileDeltaSpan = transaction.StartChild("delta.file", $"Delta for {filename}");
                            DeltaBuilder deltaBuilder = new DeltaBuilder();
                            using (FileStream newFileStream =
                                   new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (FileStream signatureStream =
                                   new FileStream(x, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            using (FileStream deltaStream = new FileStream(deltaFilePath, FileMode.Create, FileAccess.Write,
                                       FileShare.ReadWrite))
                            {
                                await deltaBuilder.BuildDeltaAsync(newFileStream,
                                        new SignatureReader(signatureStream, null),
                                        new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream)))
                                    .ConfigureAwait(false);
                            }
                            fileDeltaSpan.Finish();

                            lock (_dataToSendLock) { _client.dataToSend.Add(deltaFilePath); }
                        }
                    }
                    catch (Exception e)
                    {
                        send($"Error In creating Delta for {x}");
                        FileLogger.LogError($"Error in Create Delta: {e.Message}");
                        Console.WriteLine($"Error in Create Delta: {e.Message}");
                        SentrySdk.CaptureException(e, scope =>
                        {
                            scope.SetExtra("File", x);
                            scope.SetExtra("ClientGuid", _client._guid.ToString());
                        });
                    }
                    try
                    {
                        var hashSpan = transaction.StartChild("delta.hash", $"Hash for {deltaFilePath}");
                        _deltahash = await GetXxHash3Async(deltaFilePath);
                        var _sighash = _client.missmatchedFilehashes.FirstOrDefault(f => f.Key == origfile).Value;
                        if (!File.Exists(UpdateServerEntity.DeltaStorage + "\\" + _deltahash))
                            File.Copy(deltaFilePath, UpdateServerEntity.DeltaStorage + "\\" + _deltahash);

                        var nee = new Dictionary<string, string>();
                        nee.Add(_deltahash, OrigPath);
                        if (_sighash != null) ;
                        if (!UpdateServerEntity.DeltaFileStorage.ContainsKey(_sighash))
                            UpdateServerEntity.DeltaFileStorage.Add(_sighash, nee);
                        hashSpan.Finish();
                    }
                    catch (Exception d)
                    {
                        SentrySdk.CaptureException(d, scope =>
                        {
                            scope.SetExtra("DeltaFilePath", deltaFilePath);
                            scope.SetExtra("ClientGuid", _client._guid.ToString());
                        });
                        Console.WriteLine(d.InnerException);
                    }

                    // prg++;
                    
                    UpdateServerEntity.Puts($"Waiting for {prg} / {allc}");
                }

                deltaFilesSpan.Finish();

                SentrySdk.CaptureMessage("DeltaFinished", s =>
                {
                    s.SetTag("ClientGuid", _client._guid.ToString());
                    s.SetExtra("DeltaFilesCount", allc);
                    s.SetExtra("DeltaFilePath", deltaFilePath);
                });

                EndThisOne();
                transaction.Finish();

            }
            catch (OperationCanceledException)
            {
                SentrySdk.CaptureMessage("CreateDeltaforClient cancelled");
                transaction.Finish();
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                FileLogger.LogError("Error in CreateDeltaforClient");
                transaction.Finish(ex);
            }
            finally
            {
                if (!transaction.IsFinished)
                    transaction.Finish();
            }
        }
        private async Task CreateDeltaforClientPar()
        {
            // Start Sentry transaction for delta creation
            var transaction = SentrySdk.StartTransaction(
                "delta_creation",
                "client.delta_creation",
                $"Creating delta files for client {_client._guid}"
            );
            SentrySdk.AddBreadcrumb("CreateDeltaFor client");
            
            try
            {
                _cts.Token.ThrowIfCancellationRequested();
                send("Starting Delta Creation");

                string[] allDeltas = Directory.GetFiles(_client.ClientFolder, "*", SearchOption.AllDirectories);
                if (allDeltas.Length != _client.TrimmedFileHashes.Count)
                {
                    SentrySdk.CaptureMessage(
                        $"allDelta processor is not same as Pre defined list--{allDeltas.Length} / {_client.TrimmedFileHashes.Count}");
                }

                int allc = allDeltas.Length;
                int prg = 0;
                string deltaFilePath = "";
                
                var deltaFilesSpan = transaction.StartChild("delta.creation_loop", "Delta creation loop");

                // Use SemaphoreSlim to control degree of parallelism
                int maxDegree = Math.Max(Environment.ProcessorCount / 2, 2);
                using (var semaphore = new SemaphoreSlim(maxDegree))
                {
                    var tasks = allDeltas.Select(async x =>
                    {
                        await semaphore.WaitAsync(_cts.Token);
                        try
                        {
                            int currentProgress = Interlocked.Increment(ref prg);
                            var inf = new FileInfo(x);
                            double fileSizeMB = inf.Length / (1024.0 * 1024.0);
                            send($"Delta Progress: {currentProgress} / {allc} | {fileSizeMB}MB");
                            var result = await ProcessDeltaFileAsync(x, transaction, allc);
                            if (!string.IsNullOrEmpty(result.deltaFilePath))
                            {
                                deltaFilePath = result.deltaFilePath;
                            }
                            if (result.success)
                            {
                                
                                
                                UpdateServerEntity.Puts($"Waiting for {currentProgress} / {allc}");
                            }
                            return result;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    await Task.WhenAll(tasks);
                }

                deltaFilesSpan.Finish();

                SentrySdk.CaptureMessage("DeltaFinished", s =>
                {
                    s.SetTag("ClientGuid", _client._guid.ToString());
                    s.SetExtra("DeltaFilesCount", allc);
                    s.SetExtra("DeltaFilePath", deltaFilePath);
                });

                EndThisOne();
                transaction.Finish();
            }
            catch (OperationCanceledException)
            {
                SentrySdk.CaptureMessage("CreateDeltaforClient cancelled");
                transaction.Finish();
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                FileLogger.LogError("Error in CreateDeltaforClient");
                transaction.Finish(ex);
                throw;
            }
            finally
            {
                if (!transaction.IsFinished)
                    transaction.Finish();
            }
        }

        private async Task<(bool success, string deltaFilePath)> ProcessDeltaFileAsync(string filePath, ITransactionTracer transaction, int totalCount)
        {
            try
            {
                _cts.Token.ThrowIfCancellationRequested();
                
                if (!UpdateServerEntity.server.IsClientConnected(_client._guid))
                {
                    SentrySdk.CaptureMessage("Client Disconnected during Delta Creation", s =>
                    {
                        s.SetExtra("ClientGuid", _client._guid.ToString());
                    });
                    return (false, null);
                }

                if (filePath.Contains(".zip")) return (true, null);

                string relativePath = filePath.Replace(_client.ClientFolder, string.Empty);
                string newFilePath = Path.Combine(
                    Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\.."),
                    relativePath.Replace(".octosig", string.Empty).TrimStart('\\', '/'));
                
                string origfile = FixPath(relativePath.Replace(".octodelta", string.Empty)
                    .Replace(".octosig", string.Empty))
                    .Replace(@"\\", @"\")
                    .Replace(@"\Rust\", string.Empty);

                if (!File.Exists(newFilePath)) return (true, null);

                // Create delta file
                SentrySdk.CaptureMessage($"Creating delta for {filePath}", s =>
                {
                    s.SetExtra("RelativePath",relativePath);
                    s.SetExtra("NewFilePath", newFilePath);
                    s.SetExtra("OriginalFile", origfile);
                });
                string localDeltaFilePath = await CreateDeltaFileAsync(filePath, newFilePath, relativePath, transaction);
                if (string.IsNullOrEmpty(localDeltaFilePath)) return (true, null);

                // Process hash and storage
                await ProcessDeltaHashAsync(localDeltaFilePath, origfile, transaction);

                return (true, localDeltaFilePath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                send($"Error In creating Delta for {filePath}");
                FileLogger.LogError($"Error in Create Delta: {e.Message}");
                Console.WriteLine($"Error in Create Delta: {e.Message}");
                SentrySdk.CaptureException(e);
                return (false, null);
            }
        }

        private async Task<string> CreateDeltaFileAsync(string signatureFilePath, string newFilePath, string relativePath, ITransactionTracer transaction)
        {
            try
            {
                lock (_fileToDeleteLock) { _client.filetoDelete.Add(signatureFilePath); }
                
                string filename = Path.GetFileName(signatureFilePath);
                string localDeltaFilePath = Path.Combine(_client.ClientFolder, relativePath.Replace(".octosig", ".octodelta"));
                string deltaOutputDirectory = Path.GetDirectoryName(localDeltaFilePath);
                
                if (!Directory.Exists(deltaOutputDirectory))
                    Directory.CreateDirectory(deltaOutputDirectory);

                var fileDeltaSpan = transaction.StartChild("delta.file", $"Delta for {filename}");
                
                try
                {
                    var deltaBuilder = new DeltaBuilder();
                    using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var signatureStream = new FileStream(signatureFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var deltaStream = new FileStream(localDeltaFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    {
                        await deltaBuilder.BuildDeltaAsync(newFileStream,
                            new SignatureReader(signatureStream, null),
                            new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream)));
                    }

                    lock (_dataToSendLock) { _client.dataToSend.Add(localDeltaFilePath); }
                    return localDeltaFilePath;
                }
                finally
                {
                    fileDeltaSpan.Finish();
                }
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e);
                return null;
            }
        }

        private async Task ProcessDeltaHashAsync(string deltaFilePath, string origfile, ITransactionTracer transaction)
        {
            try
            {
                var hashSpan = transaction.StartChild("delta.hash", $"Hash for {Path.GetFileName(deltaFilePath)}");
                
                try
                {
                    string deltahash = await GetXxHash3Async(deltaFilePath);
                    var sighash = _client.missmatchedFilehashes.FirstOrDefault(f => f.Key == origfile).Value;
                    string deltaStoragePath = Path.Combine(UpdateServerEntity.DeltaStorage, deltahash);
                    
                    if (!File.Exists(deltaStoragePath))
                        File.Copy(deltaFilePath, deltaStoragePath);

                    if (!string.IsNullOrEmpty(sighash))
                    {
                        var deltaInfo = new Dictionary<string, string> { { deltahash, origfile } };
                        lock (UpdateServerEntity.DeltaFileStorage)
                        {
                            if (!UpdateServerEntity.DeltaFileStorage.ContainsKey(sighash))
                                UpdateServerEntity.DeltaFileStorage.Add(sighash, deltaInfo);
                        }
                    }
                }
                finally
                {
                    hashSpan.Finish();
                }
            }
            catch (Exception d)
            {
                SentrySdk.CaptureException(d, scope =>
                {
                    scope.SetExtra("DeltaFilePath", deltaFilePath);
                    scope.SetExtra("ClientGuid", _client._guid.ToString());
                });
                Console.WriteLine(d.InnerException?.Message ?? d.Message);
            }
        }

        private void EndThisOne()
        {
            UpdateServerEntity.Puts("Await Done Ending thisone");
            _ = CreateZipFile();
        }

        private string FixPath(string path)
        {
            char[] separators = { '\\', '/' };
            int index = path.IndexOfAny(separators);
            path = path[index].ToString() == "\\" ? path.Replace('/', '\\') : path.Replace('\\', '/');
            return path;
        }

        private string GetRelativePath(string filespec, string folder)
        {
            filespec = Path.GetFullPath(filespec);
            Uri pathUri = new Uri(filespec);
            // Folders must end in a slash
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString())) folder += Path.DirectorySeparatorChar;
            Uri folderUri = new Uri(folder);
            return Uri.UnescapeDataString(
                folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        private void PrepairClient()
        {
            SentrySdk.AddBreadcrumb("PrepairClient");
            int copycount= 0;
            foreach (var t in _client.MatchedDeltas)
            {
                string deltapath = UpdateServerEntity.DeltaStorage + "\\" + t.Key;
                string destpath = _client.ClientFolder + "\\Rust\\" + t.Value + ".octodelta";
                string deltaOutputDirectory = Path.GetDirectoryName(destpath);
                if (!Directory.Exists(deltaOutputDirectory))
                    Directory.CreateDirectory(deltaOutputDirectory);
                try
                {
                    File.Copy(deltapath, destpath);
                    lock (_dataToSendLock)
                    {
                        _client.dataToSend.Add(destpath);
                    }
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    FileLogger.LogError($"Error copying delta file: {ex.Message}");
                }

                copycount++;
            }
            SentrySdk.CaptureMessage("PrepairClient completed", s =>
            {
                s.SetExtra("Copycount ", copycount);
                s.SetExtra("MatchedDeltasCount", _client.MatchedDeltas);
                s.SetExtra("MissmatchedFileHashesCount", _client.missmatchedFilehashes);
                s.SetExtra("Trimmed List",_client.TrimmedFileHashes);
            });
            if (_client.MatchedDeltas.Count >= _client.missmatchedFilehashes.Count||_client.TrimmedFileHashes.Count<1)
            {
                EndThisOne();
                return;
            }
            CreateDeltaforClient();
        }

        private void send(string msg)
        {
            Task.Run(() => UpdateServerEntity.SendProgress(_client._guid, msg), _cts.Token);
            SentrySdk.AddBreadcrumb(msg, "ServerToClient", _client._guid.ToString(), null, BreadcrumbLevel.Info);
        }

        private async Task SendZipFile(string zip, bool stored = false, CancellationToken cancellationToken = default)
        {
            // Start Sentry transaction for sending zip file
            var transaction = SentrySdk.StartTransaction(
                "send_zip_file",
                stored ? "client.send_stored_zip" : "client.send_delta_zip",
                $"Sending zip file for client {_client._guid} (stored={stored})"
            );
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(zip);
                if (stored)
                {
                    var storedSpan = transaction.StartChild("send.stored_zip", "Sending stored zip file");
                    using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                               FileOptions.Asynchronous))
                    {
                        await UpdateServerEntity.server.SendAsync(_client._guid, source.Length, source, token: cancellationToken);
                    }
                    storedSpan.Finish();
                    transaction.Finish();
                    return;
                }
                var metadata = new Dictionary<string, object>();
                metadata.Add("1", "2");
                var deltaSpan = transaction.StartChild("send.delta_zip", "Sending delta zip file");
                using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                           FileOptions.Asynchronous))
                {
                    await UpdateServerEntity.server.SendAsync(_client._guid, source.Length, source, metadata, cancellationToken);
                }
                deltaSpan.Finish();
                UpdateServerEntity.EndCall(_client, this, zip);
                transaction.Finish();
            }
            catch (OperationCanceledException)
            {
                SentrySdk.CaptureMessage("SendZipFile cancelled");
                transaction.Finish();
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

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            if (disposing)
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                }
            }
            // Dispose unmanaged resources here if any
            SentrySdk.CaptureMessage("ClientProcessor Disposed via Dispose()");
            _disposed = true;
        }

        ~ClientProcessor()
        {
            Dispose(false);
        }
        // Returns the human-readable file size for an arbitrary, 64-bit file size 
        // The default format is "0.### XB", e.g. "4.2 KB" or "1.434 GB"
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
    }
}