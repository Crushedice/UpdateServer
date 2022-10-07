using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Diagnostics;
using FastRsync.Signature;
using Sentry;
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
        private ITransaction transaction;
        private ISpan waitspan;



        public ClientProcessor(UpdateClient user)
        {
            _client = user;
            tcpipport = user.ClientIP;
            _instanceRef = this;
            transaction = SentrySdk.StartTransaction("ClientProcessor", _client.ClientIP, _client.SignatureHash);
            waitspan = transaction.StartChild("Entry.Wait", _client.ClientIP);
        }

        public void Notify()
        {
            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Waiting in Queue....")));
        }


        public void StartupThisOne()
        {
            waitspan.Finish();
            _running = true;
            Task.Run(()=>CreateDeltaforClient());


        }

        private void EndThisOne()
        {
            UpdateServerEntity.Puts("Await Done. Making Zip");
            SentrySdk.AddBreadcrumb("Await done . making zip", _client.ClientIP);
            Task.Run(() => CreateZipFile());
        }

        private async void CreateDeltaforClient()
        {
            var span = transaction.StartChild("StartCreatingDelta",_client.ClientIP);
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
                            var iriginal = x;
                            var filename = x;
                            var relativePath = x.Replace(_client.ClientFolder, string.Empty);
                            var SignatureFile = x.Split('\\').Last();//client.ClientFolder + filename));

                            var newFilePath = Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\..") + relativePath.Replace(".octosig", string.Empty);
                            if (!File.Exists(newFilePath)) continue;
                            if (!File.Exists(x)) continue;
                            
                            var deltaFilePath = filename.Replace(".octosig", ".octodelta");

                            if (File.Exists(deltaFilePath)) continue;

                            var deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
                            if (!Directory.Exists(deltaOutputDirectory))
                                Directory.CreateDirectory(deltaOutputDirectory);
                            var deltaBuilder = new DeltaBuilder();
                            using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
                            using (var signatureStream = new FileStream(x, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
                            using (var deltaStream = new FileStream(deltaFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous))
                            {
                                await deltaBuilder.BuildDeltaAsync(newFileStream, new SignatureReader(signatureStream, null), new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream))).ConfigureAwait(false);
                            }
                            _client.dataToSend.Add(deltaFilePath);
                            _client.filetoDelete.Add(iriginal);

                    }
                    }
                    catch (Exception e)
                    {
                        Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Error In Delta File. Skipping...")));
                        FileLogger.CLog("  Error in Create Delta:  " + e.Message, "Errors.txt");
                        SentrySdk.CaptureException(e);
                      
                    }
                    prg++;
                    SentrySdk.AddBreadcrumb("DeltaProgress: " + x, _client.ClientIP);
                Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, $"Delta Progress: {prg} / {allc}"))); 
                    UpdateServerEntity.Puts($"Waiting for {prg} / {allc}");
                }
               
               // await Task.WhenAll(tasks).ConfigureAwait(false);
              
                EndThisOne();
                span.Finish();
                //Console.WriteLine("all threads Exited.  Sending data");
               
            
            

            
        }

        public async Task CreateZipFile()
        {
            int retrycount = 0;
            var sppp = transaction.StartChild("Packing Zip", _client.ClientIP);
            // Create and open a new ZIP file
            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Making ZipFile")));
            var allitems = _client.dataToAdd.Count() + _client.dataToSend.Count();
            int itemcount = 0;
string zipFileName = _client.Clientdeltazip;
SentrySdk.AddBreadcrumb(_client.Clientdeltazip, _client.ClientIP);

            starrt:

            
            if (File.Exists(zipFileName)) File.Delete(zipFileName);

            try
            {

                // var zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create);
                using (var zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create))
                {
                    foreach (var x in _client.dataToSend)
                    {
                        var relativePath = x.Replace(_client.ClientFolder + "\\Rust\\", string.Empty);
                        var fixedname = relativePath.Replace('\\', '/');
                        zip.CreateEntryFromFile(x, fixedname, CompressionLevel.Optimal);
                        itemcount++;
                        Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, $"Packing Update: {itemcount} / {allitems}")));
                        SentrySdk.AddBreadcrumb("ZipProgress: " +x, _client.ClientIP);
                    }
                    foreach (var y in _client.dataToAdd)
                    {
                        var relativePath = y.Replace("Rust\\", string.Empty);
                        var fixedname = relativePath.Replace('\\', '/');
                        zip.CreateEntryFromFile(y, fixedname, CompressionLevel.Optimal);
                        itemcount++;
                        Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, $"Packing Update: {itemcount} / {allitems}")));
                        SentrySdk.AddBreadcrumb("ZipProgress: " + y, _client.ClientIP);
                    }
                }

                _client.filetoDelete.Add(zipFileName);
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e);
                FileLogger.CLog("  Error in pack zip:  " + e.Message, "Errors.txt");
                goto retrying;

            }

            sppp.Finish();
            transaction.Finish();
            UpdateServerEntity.EndCall(_client, this, zipFileName);

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
    }
}
