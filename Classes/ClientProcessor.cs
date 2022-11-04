using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Diagnostics;
using FastRsync.Signature;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateServer.Classes
{
    public class ClientProcessor
    {
        UpdateClient _client { get; }
        public string tcpipport;
        int NrInQueue;
        ClientProcessor _instanceRef;
        private bool _running = false;



        public ClientProcessor(UpdateClient user)
        {
            _client = user;
            tcpipport = user.ClientIP;
            _instanceRef = this;
        }

        public void Notify()
        {
            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Waiting in Queue....")));
        }


        public void StartupThisOne()
        {
            _running = true;
            Task.Run(() => CreateDeltaforClient());


        }

        private void EndThisOne()
        {
            UpdateServerEntity.Puts("Await Done Ending thisone");

            _ = CreateZipFile();
        }

        private async void CreateDeltaforClient()
        {

            //Console.WriteLine("creating delta for client....");
            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Starting Delta Creation")));

           
                var allDeltas = Directory.GetFiles(_client.ClientFolder.ToString(), "*", SearchOption.AllDirectories);
                int allc = allDeltas.Count();
                int prg = 0;
                foreach (var x in allDeltas)
                {
 try
            {
                    if (!x.Contains(".zip"))
                    {
                        _client.filetoDelete.Add(x);
                        var filename = Path.GetFileName(x);
                        var relativePath = x.Replace(_client.ClientFolder, string.Empty);
                      

                        var newFilePath = Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\..") + relativePath.Replace(".octosig", string.Empty);
                        if (!File.Exists(newFilePath)) continue;
                        var deltaFilePath = _client.ClientFolder + relativePath.Replace(".octosig", ".octodelta");
                        var deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
                        if (!Directory.Exists(deltaOutputDirectory))
                            Directory.CreateDirectory(deltaOutputDirectory);
                        var deltaBuilder = new DeltaBuilder();
                        using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var signatureStream = new FileStream(x, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var deltaStream = new FileStream(deltaFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            await deltaBuilder.BuildDeltaAsync(newFileStream, new SignatureReader(signatureStream, null), new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream))).ConfigureAwait(false);
                        }
                        _client.dataToSend.Add(deltaFilePath);

                    }
                    
            }
 catch (Exception e)
 {
     UpdateServerEntity.SendProgress(_client.ClientIP, $"Error In creating Delta for {x}");
     FileLogger.CLog("  Error in Create Delta:  " + e.Message, "Errors.txt");
    

 }
                prg++;
                    UpdateServerEntity.SendProgress(_client.ClientIP, $"Delta Progress: {prg} / {allc}");
                    UpdateServerEntity.Puts($"Waiting for {prg} / {allc}");
                }

                // await Task.WhenAll(tasks).ConfigureAwait(false);

                EndThisOne();

                //Console.WriteLine("all threads Exited.  Sending data");

           


        }

        async Task SendZipFile(string zip, bool stored = false)
        {
            
            Dictionary<object, object> metadata = new Dictionary<object, object>();
            metadata.Add("1", "2");

            using (var source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                 await UpdateServerEntity.server.SendAsync(_client.ClientIP, source.Length, source, metadata).ConfigureAwait(false);
            }

            UpdateServerEntity.EndCall(_client, this, zip);

        }

        public async Task CreateZipFile()
        {
            int retrycount = 0;
            // Create and open a new ZIP file
            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Making ZipFile")));
            var allitems = _client.dataToAdd.Count() + _client.dataToSend.Count();
            int itemcount = 0;
        starrt:

            string zipFileName = _client.Clientdeltazip;
            if (File.Exists(zipFileName)) File.Delete(zipFileName);
            string clientRustFolder = _client.ClientFolder + "\\Rust\\";
            try
            {

                // var zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create);
                using (var zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
                {
                    foreach (var x in _client.dataToSend)
                    {
                        try
                        {
                            if (!File.Exists(x))
                            {
                                continue;
                            }

                            var relativePath = x.Replace(_client.ClientFolder + "\\Rust\\", string.Empty);
                            var fixedname = relativePath.Replace('\\', '/');
                            zip.CreateEntryFromFile(x, fixedname, CompressionLevel.Optimal);
                            itemcount++;
                            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, $"Packing Update: {itemcount} / {allitems}")));
                        }
                        catch (Exception e)
                        {
                            FileLogger.CLog(e.Message, "Errors.txt");

                        }
                      
                    }
                    foreach (var y in _client.dataToAdd)
                    {
                        try
                        {
                            if (!File.Exists(y))
                            {
                                continue;
                            }

                            var relativePath = y.Replace("Rust\\", string.Empty);
                            var fixedname = relativePath.Replace('\\', '/');
                            zip.CreateEntryFromFile(y, fixedname, CompressionLevel.Optimal);
                            itemcount++;
                            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, $"Packing Update: {itemcount} / {allitems}")));
                        }
                        catch (Exception r)
                        {
                            FileLogger.CLog(r.Message,"Errors.txt");
                        }
                    }
                }

                _client.filetoDelete.Add(zipFileName);
            }
            catch (Exception e)
            {
                FileLogger.CLog("  Error in pack zip:  " + e.Message, "Errors.txt");
                goto retrying;

            }

            Task.Run((() => SendZipFile(zipFileName)));

            return;

        retrying:

            if (retrycount > 5)
            {
                //    FileLogger.CLog(DateTime.Now.ToString("MM/dd HH:mm") + "  abort of packing zip after  " + retrycount + " Retry ", "Finished.txt");
                UpdateServerEntity.EndCall(_client, this, zipFileName, true);
                return;
            }
            await Task.Delay(10000);
            retrycount++;
            goto starrt;



        }
        string GetRelativePath(string filespec, string folder)
        {
            filespec = Path.GetFullPath(filespec);
            Uri pathUri = new Uri(filespec);
            // Folders must end in a slash
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                folder += Path.DirectorySeparatorChar;
            }
            Uri folderUri = new Uri(folder);
            return Uri.UnescapeDataString(
                folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

    }
}
