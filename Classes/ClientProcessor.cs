using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Diagnostics;
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
using System.Threading;
using System.Threading.Tasks;
using UpdateServer.Classes;

namespace UpdateServer.Classes
{
    public class ClientProcessor : IDisposable
    {
        #region Fields and Properties
        public string tcpipport;
        public UpdateClient _client { get; }
        private ConsoleProgressReporter pr = new ConsoleProgressReporter();
        private ClientProcessor _instanceRef;
        private bool _running;
        private int NrInQueue;
        private bool _disposed = false;
        private static System.Windows.Forms.Timer Timer99 = new System.Windows.Forms.Timer();
        private CancellationTokenSource _cts = new CancellationTokenSource();
        #endregion

        #region Thread-Safe Locks
        // Removed lock objects for thread safety
        // private static readonly object _dataToSendLock = new object();
        // private static readonly object _fileToDeleteLock = new object();
        #endregion

        #region Constructor
        public ClientProcessor(UpdateClient user)
        {
            _client = user;
            tcpipport = user.ClientIP;
            _instanceRef = this;
            Timer99.Interval = 1000;

            Timer99.Tick += (s, e) => TimerTick();

            SentrySdk.CaptureMessage("New Client Created", s =>
            {
                s.SetExtra("ClientGuid", _client._guid.ToString());
                s.SetExtra("ClientIP", _client.ClientIP);
                s.SetExtra("ClientFolder", _client.ClientFolder);
                s.SetExtra("ClientDeltaZip", _client.Clientdeltazip);
            });
        }

        private void TimerTick()
        {
          
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
            _running = true;
            PrepairClient();
        }
        #endregion

        #region Core Processing Methods
        private void PrepairClient()
        {
            SentrySdk.AddBreadcrumb("PrepairClient");
            int copycount = 0;
            
            foreach (var deltaItem in _client.MatchedDeltas)
            {
                ProcessMatchedDelta(deltaItem, ref copycount);
            }
            
            LogPrepairClientCompletion(copycount);
            
            if (_client.MatchedDeltas.Count >= _client.missmatchedFilehashes.Count || _client.TrimmedFileHashes.Count < 1)
            {
                EndThisOne();
                return;
            }
            
            CreateDeltaforClient();
        }

        private void ProcessMatchedDelta(KeyValuePair<string, string> deltaItem, ref int copycount)
        {
            string deltapath = UpdateServerEntity.DeltaStorage + "\\" + deltaItem.Key;
            string destpath = _client.ClientFolder + "\\Rust\\" + deltaItem.Value + ".octodelta";
            string deltaOutputDirectory = Path.GetDirectoryName(destpath);
            
            if (!Directory.Exists(deltaOutputDirectory))
                Directory.CreateDirectory(deltaOutputDirectory);
            
            try
            {
                File.Copy(deltapath, destpath, true);
                // Removed lock (_dataToSendLock)
                _client.dataToSend.Add(destpath);
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex, s =>
                {
                    s.SetExtra(deltapath, destpath);
                });
#if DEBUG
                throw;
#endif
            }

            copycount++;
        }

        private void LogPrepairClientCompletion(int copycount)
        {
            SentrySdk.CaptureMessage("PrepairClient completed", s =>
            {
                s.SetExtra("Copycount ", copycount);
                s.SetExtra("MatchedDeltasCount", _client.MatchedDeltas);
                s.SetExtra("MissmatchedFileHashesCount", _client.missmatchedFilehashes);
                s.SetExtra("Trimmed List", _client.TrimmedFileHashes);
            });
        }

        private void EndThisOne()
        {
            UpdateServerEntity.Puts("Await Done Ending thisone");
            _ = CreateZipFile();
        }
        #endregion

        #region Delta Creation Methods
        private async void CreateDeltaforClient()
        {
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
               // await ProcessDeltaFiles(allDeltas, transaction);

                await ProcessDeltaFilesParallel(allDeltas,transaction).ConfigureAwait(true);



                SentrySdk.CaptureMessage("DeltaFinished", s =>
                {
                    s.SetTag("ClientGuid", _client._guid.ToString());
                    s.SetExtra("DeltaFilesCount", allDeltas.Length);
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
                transaction.Finish(ex);
#if DEBUG
                throw;
#endif
            }
            finally
            {
                if (!transaction.IsFinished)
                    transaction.Finish();
            }
        }

        private async Task ProcessDeltaFiles(string[] allDeltas, ITransactionTracer transaction)
        {
            int allc = allDeltas.Length;
            int prg = 0;
            string deltaFilePath = "";

            var deltaFilesSpan = transaction.StartChild("delta.creation_loop", "Delta creation loop");

            foreach (string filePath in allDeltas)
            {
                _cts.Token.ThrowIfCancellationRequested();
                
                if (!UpdateServerEntity.server.IsClientConnected(_client._guid))
                {
                    SentrySdk.CaptureMessage("Client Disconnected during Delta Creation", s =>
                    {
                        s.SetExtra("ClientGuid", _client._guid.ToString());
                    });
                    deltaFilesSpan.Finish();
                    return;
                }

                int currentProgress = Interlocked.Increment(ref prg);
                
                if (filePath.Contains(".zip")) continue;
                
                try
                {
                    deltaFilePath = await ProcessSingleDeltaFile(filePath, currentProgress, allc, transaction);
                }
                catch (Exception e)
                {
                    HandleDeltaCreationError(e, filePath);
                }
                
                UpdateServerEntity.Puts($"Waiting for {prg} / {allc}");
            }

            deltaFilesSpan.Finish();
        }

        private async Task<string> ProcessSingleDeltaFile(string filePath, int currentProgress, int totalCount, ITransactionTracer transaction)
        {
            string relativePath = filePath.Replace(_client.ClientFolder, string.Empty);
            string newFilePath = Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\..") + relativePath.Replace(".octosig", string.Empty);
            string origfile = FixPath(filePath.Replace(_client.ClientFolder, string.Empty).Replace(".octodelta", string.Empty).Replace(".octosig", string.Empty)).Replace(@"\\", @"\").Replace(@"\Rust\", string.Empty);
            string OrigPath = origfile;

            if (!File.Exists(newFilePath)) return null;

            _client.filetoDelete.Add(filePath);
            string filename = Path.GetFileName(filePath);

            var inf = new FileInfo(filePath);
            var size = GetBytesReadable(inf.Length);
            send($"Delta Progress: {currentProgress} / {totalCount} --- {size}");
            
            string deltaFilePath = _client.ClientFolder + relativePath.Replace(".octosig", ".octodelta");
            string deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
            
            if (!Directory.Exists(deltaOutputDirectory))
                Directory.CreateDirectory(deltaOutputDirectory);

            await CreateDeltaFile(filePath, newFilePath, deltaFilePath, filename, transaction);
            await ProcessDeltaHash(deltaFilePath, origfile, OrigPath, transaction);

            // Removed lock (_dataToSendLock)
            _client.dataToSend.Add(deltaFilePath);

            return deltaFilePath;
        }

        private async Task CreateDeltaFile(string signatureFile, string newFile, string deltaFile, string filename, ITransactionTracer transaction)
        {
            var fileDeltaSpan = transaction.StartChild("delta.file", $"Delta for {filename}");
            
            try
            {
                DeltaBuilder deltaBuilder = new DeltaBuilder();
                deltaBuilder.ProgressReport = new ConsoleProgressReporter();
                using (FileStream newFileStream = new FileStream(newFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (FileStream signatureStream = new FileStream(signatureFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (FileStream deltaStream = new FileStream(deltaFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    deltaBuilder.BuildDelta(newFileStream,
                            new SignatureReader(signatureStream, null),
                            new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream)));
                }
            }
            finally
            {
                fileDeltaSpan.Finish();
            }
        }

        private async Task ProcessDeltaHash(string deltaFilePath, string origfile, string origPath, ITransactionTracer transaction)
        {
            var hashSpan = transaction.StartChild("delta.hash", $"Hash for {deltaFilePath}");
            
            try
            {
                string deltahash = await GetXxHash3Async(deltaFilePath);
                var sighash = _client.missmatchedFilehashes.FirstOrDefault(f => f.Key == origfile).Value;
                
                if (!File.Exists(UpdateServerEntity.DeltaStorage + "\\" + deltahash))
                    File.Copy(deltaFilePath, UpdateServerEntity.DeltaStorage + "\\" + deltahash);

                var deltaInfo = new Dictionary<string, string>();
                deltaInfo.Add(deltahash, origPath);
                
                if (sighash != null && !UpdateServerEntity.DeltaFileStorage.ContainsKey(sighash))
                    UpdateServerEntity.DeltaFileStorage.Add(sighash, deltaInfo);
            }
            finally
            {
                hashSpan.Finish();
            }
        }

        private void HandleDeltaCreationError(Exception e, string filePath)
        {
            send($"Error In creating Delta for {filePath}");
            Console.WriteLine($"Error in Create Delta: {e.Message}");
            SentrySdk.CaptureException(e, scope =>
            {
                scope.SetExtra("File", filePath);
                scope.SetExtra("ClientGuid", _client._guid.ToString());
            });

        }
        #endregion

        #region Parallel Delta Creation Methods
      //  private async Task CreateDeltaforClientPar()
      //  {
      //      var transaction = SentrySdk.StartTransaction(
      //          "delta_creation",
      //          "client.delta_creation",
      //          $"Creating delta files for client {_client._guid}"
      //      );
      //      
      //      SentrySdk.AddBreadcrumb("CreateDeltaFor client");
      //      
      //          _cts.Token.ThrowIfCancellationRequested();
      //          send("Starting Delta Creation");
      //
      //          string[] allDeltas = Directory.GetFiles(_client.ClientFolder, "*", SearchOption.AllDirectories);
      //          
      //          if (allDeltas.Length != _client.TrimmedFileHashes.Count)
      //          {
      //              SentrySdk.CaptureMessage(
      //                  $"allDelta processor is not same as Pre defined list--{allDeltas.Length} / {_client.TrimmedFileHashes.Count}");
      //          }
      //
      //          await ProcessDeltaFilesParallel(allDeltas, transaction);
      //          
      //          SentrySdk.CaptureMessage("DeltaFinished", s =>
      //          {
      //              s.SetTag("ClientGuid", _client._guid.ToString());
      //              s.SetExtra("DeltaFilesCount", allDeltas.Length);
      //          });
      //
      //          EndThisOne();
      //          transaction.Finish();
      //      
      //      
      //      
      //          if (!transaction.IsFinished)
      //              transaction.Finish();
      //      
      //  }

        private async Task ProcessDeltaFilesParallel(string[] allDeltas, ITransactionTracer transaction)
        {
            int allc = allDeltas.Length;
            int prg = 0;
            
            var deltaFilesSpan = transaction.StartChild("delta.creation_loop", "Delta creation loop");

          // Use SemaphoreSlim to control degree of parallelism
            int maxDegree = Math.Max(5,25);
            var semaphore = new SemaphoreSlim(maxDegree);
            
                var tasks = allDeltas.Select(async x =>
                {
                    await semaphore.WaitAsync(_cts.Token);
                    try
                    {
                        
                        string filename = Path.GetFileName(x);
                        var inf = new FileInfo(x);
                        var size = GetBytesReadable(inf.Length);
                        

                        var result = await ProcessDeltaFileAsync(x, transaction, allc);
                        int currentProgress = Interlocked.Increment(ref prg);
                        if (result.success)
                        {
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
          
                await Task.WhenAll(tasks);
            // ENd of




            deltaFilesSpan.Finish();
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
                
                string origfile = FixPath(relativePath.Replace(".octosig", string.Empty)
                    .Replace(".octodelta", string.Empty))
                    .Replace(@"\\", @"\")
                    .Replace(@"\Rust\", string.Empty);

                if (!File.Exists(newFilePath)) return (true, null);

                // Create delta file
                SentrySdk.CaptureMessage($"Creating delta for {filePath}", s =>
                {
                    s.SetExtra("RelativePath", relativePath);
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
                Console.WriteLine($"Error in Create Delta: {e.Message}");
                SentrySdk.CaptureException(e);
#if DEBUG
                throw;
#endif
                return (false, null);
            }
        }

        private async Task<string> CreateDeltaFileAsync(string signatureFilePath, string newFilePath, string relativePath, ITransactionTracer transaction)
        {
            
            try
            {
                // Removed lock (_fileToDeleteLock)
                _client.filetoDelete.Add(signatureFilePath);
                
                string filename = Path.GetFileName(signatureFilePath);
                string localDeltaFilePath = Path.Combine(_client.ClientFolder, relativePath.Replace(".octosig", ".octodelta"));
                string deltaOutputDirectory = Path.GetDirectoryName(localDeltaFilePath);
                
                if (!Directory.Exists(deltaOutputDirectory))
                    Directory.CreateDirectory(deltaOutputDirectory);

                    var deltaBuilder = new DeltaBuilder();
                    using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var signatureStream = new FileStream(signatureFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var deltaStream = new FileStream(localDeltaFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    {
                        await deltaBuilder.BuildDeltaAsync(newFileStream,
                            new SignatureReader(signatureStream, null),
                            new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream)));
                    }

                    // Removed lock (_dataToSendLock)
                    _client.dataToSend.Add(localDeltaFilePath);
                    return localDeltaFilePath;
                
              
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e);
#if DEBUG
                throw;
#endif
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
#if DEBUG
                throw;
#endif
            }
        }
        #endregion

        #region Zip File Operations
        private async Task CreateZipFile(bool additionalfiles = false)
        {
            var transaction = SentrySdk.StartTransaction(
                "zip_creation",
                additionalfiles ? "client.additionalfiles_zip" : "client.deltafiles_zip",
                $"Creating zip file for client {_client._guid} (additionalfiles={additionalfiles})"
            );
            
            try
            {
                _cts.Token.ThrowIfCancellationRequested();
                send("Making ZipFile");
                
                int allitems;
                // Removed lock (_dataToSendLock)
                allitems = _client.dataToSend.Count();
                
                string zipFileName = _client.Clientdeltazip;
                if (File.Exists(zipFileName)) File.Delete(zipFileName);
                
                SentrySdk.AddBreadcrumb("Deltazip", "Info", "", _client.missmatchedFilehashes);
                
                await CreateZipFileContent(zipFileName, allitems, transaction);
                
                // Removed lock (_fileToDeleteLock)
                _client.filetoDelete.Add(zipFileName);
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
                SentrySdk.CaptureException(ex);
                transaction.Finish(ex);
#if DEBUG
                throw;
#endif
            }
            finally
            {
                if (!transaction.IsFinished)
                    transaction.Finish();
            }
        }

        private async Task CreateZipFileContent(string zipFileName, int allitems, ITransactionTracer transaction)
        {
            int itemcount = 0;
            
            try
            {
                var deltaFilesSpan = transaction.StartChild("zip.deltafiles", "Packing delta files");
                
                using (ZipArchive zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
                {
                    List<string> dataToSendCopy;
                    // Removed lock (_dataToSendLock)
                    dataToSendCopy = new List<string>(_client.dataToSend);
                    
                    foreach (string filePath in dataToSendCopy)
                    {
                        try
                        {
                            _cts.Token.ThrowIfCancellationRequested();
                            if (!File.Exists(filePath)) continue;
                            
                            string relativePath = filePath.Replace(_client.ClientFolder + "\\Rust\\", string.Empty);
                            string fixedname = relativePath.Replace('\\', '/');
                            zip.CreateEntryFromFile(filePath, fixedname, CompressionLevel.Optimal);
                            itemcount++;

                            send($"Packing Update: {itemcount} / {allitems}");
                        }
                        catch (Exception ex)
                        {
                            SentrySdk.CaptureException(ex);
                            Console.WriteLine($"Error packing file: {ex.Message}");
#if DEBUG
                            throw;
#endif
                        }
                    }
                }
                
                deltaFilesSpan.Finish();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in pack zip: {e.Message}");
                SentrySdk.CaptureException(e);
#if DEBUG
                throw;
#endif
            }
        }

        private async Task SendZipFile(string zip, bool stored = false, CancellationToken cancellationToken = default)
        {
            var transaction = SentrySdk.StartTransaction(
                "send_zip_file",
                stored ? "client.send_stored_zip" : "client.send_delta_zip",
                $"Sending zip file for client {_client._guid} (stored={stored})"
            );
            
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                if (stored)
                {
                    await SendStoredZipFile(zip, transaction, cancellationToken);
                }
                else
                {
                    await SendDeltaZipFile(zip, transaction, cancellationToken);
                    UpdateServerEntity.EndCall(_client, this, zip);
                }
                
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

        private async Task SendStoredZipFile(string zip, ITransactionTracer transaction, CancellationToken cancellationToken)
        {
            var storedSpan = transaction.StartChild("send.stored_zip", "Sending stored zip file");
            try
            {
                using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
                {
                    await UpdateServerEntity.server.SendAsync(_client._guid, source.Length, source, token: cancellationToken);
                }
            }
            finally
            {
                storedSpan.Finish();
            }
        }

        private async Task SendDeltaZipFile(string zip, ITransactionTracer transaction, CancellationToken cancellationToken)
        {
            var deltaSpan = transaction.StartChild("send.delta_zip", "Sending delta zip file");
            try
            {
                var metadata = new Dictionary<string, object> { { "1", "2" } };
                using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
                {
                    await UpdateServerEntity.server.SendAsync(_client._guid, source.Length, source, metadata, cancellationToken);
                }
            }
            finally
            {
                deltaSpan.Finish();
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

        private void send(string msg)
        {
            Task.Run(() => UpdateServerEntity.SendProgress(_client._guid, msg), _cts.Token);
            SentrySdk.AddBreadcrumb(msg, "ServerToClient", _client._guid.ToString(), null, BreadcrumbLevel.Info);
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
            
            SentrySdk.CaptureMessage("ClientProcessor Disposed via Dispose()");
            _disposed = true;
        }

        ~ClientProcessor()
        {
            Dispose(false);
        }
        #endregion
    }
}