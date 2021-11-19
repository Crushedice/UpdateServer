using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Diagnostics;
using FastRsync.Signature;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateServer.Classes
{
    public class ClientProcessor
    {
        UpdateClient _client { get; }
        int NrInQueue;
        ClientProcessor _instanceRef;
        private bool _running = false;



        public ClientProcessor(UpdateClient user)
        {
            _client = user;
            _instanceRef = this;
        }

        public void Notify()
        {
            Task.Run((() => UpdateServerEntity.SendProgress(_client.ClientIP, "Waiting in Queue....")));
        }


        public void StartupThisOne()
        {
            _running = true;
            Task.Run(()=>CreateDeltaforClient());


        }

        private void EndThisOne()
        {
            UpdateServerEntity.Puts("Await Done Ending thisone");
            _running = false;
            UpdateServerEntity.EndCall(_client);
        }

        private async void CreateDeltaforClient()
        {

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
                        if (!File.Exists(newFilePath)) return;
                        var deltaFilePath = _client.ClientFolder + relativePath.Replace(".octosig", string.Empty) + ".octodelta";
                        var deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
                        if (!Directory.Exists(deltaOutputDirectory))
                            Directory.CreateDirectory(deltaOutputDirectory);
                        var deltaBuilder = new DeltaBuilder();
                        using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        using (var signatureFileStream =
                            new FileStream(x, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        using (var deltaStream = new FileStream(deltaFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
                        {
                            deltaBuilder.BuildDelta(newFileStream,
                                new SignatureReader(signatureFileStream, new ConsoleProgressReporter()),
                                new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream)));
                        }

                        _client.dataToSend.Add(deltaFilePath);


                    }
                    prg++;
                    UpdateServerEntity.SendProgress(_client.ClientIP, $"Delta Progress: {prg} / {allc}");
                }
                UpdateServerEntity.Puts("Waiting for ");
               // await Task.WhenAll(tasks).ConfigureAwait(false);
              
                EndThisOne();

                //Console.WriteLine("all threads Exited.  Sending data");

            }
            catch (Exception e)
            {
                UpdateServerEntity.SendProgress(_client.ClientIP, "Fatal Error. Please Try again!");
                FileLogger.CLog("  Error in Create Delta:  " + e.Message, "Errors.txt");
                UpdateServerEntity.server.DisconnectClient(_client.ClientIP);
                EndThisOne();
            }

            
        }

        private async Task DeltaPool(string x)
        {

            //Console.WriteLine("Processing " + x );
            _client.filetoDelete.Add(x);
            var filename = Path.GetFileName(x);
            var relativePath = x.Replace(_client.ClientFolder, string.Empty);
            var SignatureFile = x.Split('\\').Last();//client.ClientFolder + filename));

            var newFilePath = Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\..") + relativePath.Replace(".octosig", string.Empty);
            if (!File.Exists(newFilePath)) return;
            var deltaFilePath = _client.ClientFolder + relativePath.Replace(".octosig", string.Empty) + ".octodelta";
            var deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
            if (!Directory.Exists(deltaOutputDirectory))
                Directory.CreateDirectory(deltaOutputDirectory);
            var deltaBuilder = new DeltaBuilder();
            using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            using (var signatureFileStream =
                new FileStream(x, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            using (var deltaStream = new FileStream(deltaFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                deltaBuilder.BuildDelta(newFileStream,
                    new SignatureReader(signatureFileStream, new ConsoleProgressReporter()),
                    new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream)));
            }

            _client.dataToSend.Add(deltaFilePath);


            // poolp();
            // Heart.PoolMod(x, false);
            return;

        }


    }
}
