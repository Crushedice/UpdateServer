using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Signature;
using Sentry;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UpdateServer.Classes;

namespace UpdateServer.Classes
{
    public class ClientProcessor
    {
        private ClientProcessor _instanceRef;
        private bool _running;
        private int NrInQueue;
        public string tcpipport;

        public ClientProcessor(UpdateClient user)
        {
            _client = user;
            tcpipport = user.ClientIP;
            _instanceRef = this;
            SentrySdk.CaptureMessage("New Client Created");
        }

        private UpdateClient _client { get; }

        public void Notify()
        {
            send("Waiting in Queue....");
        }

        public void StartupThisOne()
        {
            _running = true;
            PrepairClient();
        }

        private Task<string> CalculateMD5(string filename)
        {
            byte[] hash;
            using (FileStream inputStream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                MD5 md5 = MD5.Create();

                hash = md5.ComputeHash(inputStream);
            }

            return Task.FromResult(BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant());
        }

        private string FixPath(string path)
        {
            char[] separators = { '\\','/'};
            int index = path.IndexOfAny(separators);        
            path = path[index].ToString() == "\\" ? path.Replace('/', '\\') : path.Replace('\\', '/');
            return path;
        }
        private async void CreateDeltaforClient()
        {
            SentrySdk.AddBreadcrumb("CreateDeltaFor client");
            var transactionId = SentryPerformanceMonitor.StartTransaction(
                "delta_creation",
                "client.processing",
                $"Creating delta files for client {_client._guid}");
            try
            {
                send("Starting Delta Creation");
                SentryPerformanceMonitor.SetUserContext(_client._guid.ToString(), _client.ClientIP);
                string[] allDeltas = Directory.GetFiles(_client.ClientFolder, "*", SearchOption.AllDirectories);
                int allc = allDeltas.Count();
                int prg = 0;

                string _deltahash = "";
                string deltaFilePath = "";
                SentryPerformanceMonitor.RecordMetric("delta_files_count", allc);
                var deltaProcessingSpanId = SentryPerformanceMonitor.StartSpan(
                    transactionId,
                    "delta.file_processing",
                    $"Processing {allc} delta files");
                foreach (string x in allDeltas)
                {
                    if (x.Contains(".zip")) continue;
                    string relativePath = x.Replace(_client.ClientFolder, string.Empty);

                    string newFilePath = Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\..") +
                                         relativePath.Replace(".octosig", string.Empty);
                    string origfile= FixPath(x.Replace(_client.ClientFolder, string.Empty).Replace(".octodelta", string.Empty).Replace(".octosig", string.Empty)).Replace(@"\\",@"\").Replace(@"\Rust\",string.Empty);

                    string OrigPath = origfile;

                    try
                    {
                        if (!x.Contains(".zip"))
                        {
                            _client.filetoDelete.Add(x);
                            string filename = Path.GetFileName(x);

                            if (!File.Exists(newFilePath)) continue;
                            deltaFilePath = _client.ClientFolder + relativePath.Replace(".octosig", ".octodelta");
                            string deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
                            if (!Directory.Exists(deltaOutputDirectory))
                                Directory.CreateDirectory(deltaOutputDirectory);
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

                            _client.dataToSend.Add(deltaFilePath);
                        }
                    }
                    catch (Exception e)
                    {
                        send($"Error In creating Delta for {x}");
                        FileLogger.LogError($"Error in Create Delta: {e.Message}");
                        Console.WriteLine($"Error in Create Delta: {e.Message}");
                        SentrySdk.CaptureException(e);
                    }

                    try
                    {
                        _deltahash = await CalculateMD5(deltaFilePath);
                        var _sighash = _client.missmatchedFilehashes.FirstOrDefault(f => f.Key == origfile).Value;
                        if (!File.Exists(UpdateServerEntity.DeltaStorage + "\\" + _deltahash))
                            File.Copy(deltaFilePath, UpdateServerEntity.DeltaStorage + "\\" + _deltahash);

                        var nee = new Dictionary<string, string>();
                        nee.Add(_deltahash, OrigPath);
                        if (_sighash != null);
                            if (!UpdateServerEntity.DeltaFileStorage.ContainsKey(_sighash))
                                UpdateServerEntity.DeltaFileStorage.Add(_sighash, nee);
                    }
                    catch (Exception d)
                    {
                        SentrySdk.CaptureException(d);
                        Console.WriteLine(d.InnerException);
                    }

                    prg++;
                    send($"Delta Progress: {prg} / {allc}");
                    UpdateServerEntity.Puts($"Waiting for {prg} / {allc}");
                }
                SentryPerformanceMonitor.FinishSpan(deltaProcessingSpanId, SpanStatus.Ok,
               new Dictionary<string, object> { { "processed_files", prg } });
                EndThisOne();
                SentryPerformanceMonitor.FinishTransaction(transactionId, SpanStatus.Ok,
               new Dictionary<string, object>
               {
                    { "total_files", allc },
                    { "processed_files", prg },
                    { "client_id", _client._guid.ToString() }
               });

            }
            catch (Exception ex)
            {
                SentryPerformanceMonitor.FinishTransaction(transactionId, SpanStatus.InternalError,
                    new Dictionary<string, object> { { "error", ex.Message } });
                SentrySdk.CaptureException(ex);
                FileLogger.LogError("Error in CreateDeltaforClient");
                
            }
        }

        public async Task CreateZipFile(bool additionalfiles = false)
        {
            try
            {
                int retrycount = 0;
                send("Making ZipFile");
                int allitems = _client.dataToSend.Count();
                string zipFileName = _client.Clientdeltazip;
                string zipFileName2 = _client.ClientFolder+"\\additionalfiles.zip" ;
                if (File.Exists(zipFileName)) File.Delete(zipFileName);
                string clientRustFolder = _client.ClientFolder + "\\Rust\\";

                int itemcount = 0;

                if (additionalfiles)
                {
                    using (ZipArchive zip = ZipFile.Open(zipFileName2, ZipArchiveMode.Create))
                    {
                        foreach (string y in _client.dataToAdd)
                        {
                            if (!File.Exists(y)) continue;

                            string relativePath = y.Replace("Rust\\", string.Empty);
                            string fixedname = relativePath.Replace('\\', '/');
                            zip.CreateEntryFromFile(y, fixedname, CompressionLevel.Optimal);
                            itemcount++;

                            send($"Packing Update: {itemcount} / {allitems}");
                        }
                    }
                    _client.filetoDelete.Add(zipFileName2);
                    SendZipFile(zipFileName2,true);
                    return;
                }
            starrt:
                try
                {
                    using (ZipArchive zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
                    {
                        foreach (string x in _client.dataToSend)
                            try
                            {
                                if (!File.Exists(x)) continue;
                                string relativePath = x.Replace(_client.ClientFolder + "\\Rust\\", string.Empty);
                                string fixedname = relativePath.Replace('\\', '/');
                                zip.CreateEntryFromFile(x, fixedname, CompressionLevel.Optimal);
                                itemcount++;

                                send($"Packing Update: {itemcount} / {allitems}");
                            }
                            catch (Exception e)
                            {
                                SentrySdk.CaptureException(e);
                                Console.WriteLine($"Error packing file: {e.Message}");
                                FileLogger.LogError($"Error packing file: {e.Message}");
                            }
                    }
                    _client.filetoDelete.Add(zipFileName);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error in pack zip: {e.Message}");
                    FileLogger.LogError($"Error in pack zip: {e.Message}");
                    SentrySdk.CaptureException(e);
                    goto retrying;
                }
                SendZipFile(zipFileName);
                return;
            retrying:
                if (retrycount > 5)
                {
                    UpdateServerEntity.EndCall(_client, this, zipFileName, true);
                    return;
                }
                await Task.Delay(10000);
                retrycount++;
                goto starrt;
            }
            catch (Exception ex)
            {
                FileLogger.LogError("Error in CreateZipFile");
                SentrySdk.CaptureException(ex);
                throw;
            }
        }

        private void EndThisOne()
        {
            UpdateServerEntity.Puts("Await Done Ending thisone");
            _ = CreateZipFile();
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
            foreach (var t in _client.MatchedDeltas)
            {
                string deltapath = UpdateServerEntity.DeltaStorage + "\\" + t.Key;
                string destpath = _client.ClientFolder + "\\Rust\\"+ t.Value+".octodelta";
                string deltaOutputDirectory = Path.GetDirectoryName(destpath);
                if (!Directory.Exists(deltaOutputDirectory))
                    Directory.CreateDirectory(deltaOutputDirectory);

                File.Copy(deltapath, destpath);
                _client.dataToSend.Add(destpath);
            }

            if (_client.MatchedDeltas.Count >= _client.missmatchedFilehashes.Count)
            {
                EndThisOne();
                return;
            }

            CreateDeltaforClient();
        }

        private void send(string msg)
        {
            Task.Run(() =>  UpdateServerEntity.SendProgress(_client._guid, msg));
            SentrySdk.AddBreadcrumb(msg,"ServerToClient",_client._guid.ToString(),null,BreadcrumbLevel.Info);
        }

        private async Task SendZipFile(string zip, bool stored = false)
        {
            var fileInfo = new FileInfo(zip);
            if (stored)
            {
                using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                           FileOptions.Asynchronous))
                {
                    await UpdateServerEntity.server.SendAsync(_client._guid, source.Length, source);
                }
                return;
            }
            var metadata = new Dictionary<string, object>();
            metadata.Add("1", "2");
            using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                       FileOptions.Asynchronous))
            {
                await UpdateServerEntity.server.SendAsync(_client._guid, source.Length, source, metadata);
            }
            UpdateServerEntity.EndCall(_client, this, zip);
        }
    }
}