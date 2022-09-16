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
            transaction = SentrySdk.StartTransaction("ClientProcessor","User: "+user.ClientIP);
            waitspan = transaction.StartChild("Entry.Wait");
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
            UpdateServerEntity.Puts("Await Done Ending thisone");

            _ = CreateZipFile();
        }

        private async void CreateDeltaforClient()
        {
            var span = transaction.StartChild("StartCreatingDelta");
            //Console.WriteLine("creating delta for client....");
            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Starting Delta Creation")));

            try
            {
                var allDeltas = Directory.GetFiles(_client.ClientFolder.ToString(), "*", SearchOption.AllDirectories);
                int allc = allDeltas.Count();
                int prg = 0;
                foreach (var x in allDeltas)
                {
                   
                    if (!x.Contains(".zip"))
                    {
                        _client.filetoDelete.Add(x);
                        var filename = Path.GetFileName(x);
                        var relativePath = x.Replace(_client.ClientFolder, string.Empty);
                        var SignatureFile = x.Split('\\').Last();//client.ClientFolder + filename));

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
                    prg++;
                    UpdateServerEntity.SendProgress(_client.ClientIP, $"Delta Progress: {prg} / {allc}"); 
                    UpdateServerEntity.Puts($"Waiting for {prg} / {allc}");
                }
               
               // await Task.WhenAll(tasks).ConfigureAwait(false);
              
                EndThisOne();
                span.Finish();
                //Console.WriteLine("all threads Exited.  Sending data");
                transaction.Finish();
            }
            catch (Exception e)
            {
                UpdateServerEntity.SendProgress(_client.ClientIP, "Fatal Error. Please Try again!");
                FileLogger.CLog("  Error in Create Delta:  " + e.Message, "Errors.txt");
                UpdateServerEntity.EndCall(_client, this,"", true);

                UpdateServerEntity.server.DisconnectClient(_client.ClientIP);
                SentrySdk.CaptureException(e);
            }

            
        }

        public async Task CreateZipFile()
        {
            int retrycount = 0;
            // Create and open a new ZIP file
            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Making ZipFile")));

            starrt:

            string zipFileName = _client.Clientdeltazip;
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
                    }
                    foreach (var y in _client.dataToAdd)
                    {
                        var relativePath = y.Replace("Rust\\", string.Empty);
                        var fixedname = relativePath.Replace('\\', '/');
                        zip.CreateEntryFromFile(y, fixedname, CompressionLevel.Optimal);
                    }
                }

                _client.filetoDelete.Add(zipFileName);
            }
            catch (Exception e)
            {
                FileLogger.CLog("  Error in pack zip:  " + e.Message, "Errors.txt");
                goto retrying;

            }

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
