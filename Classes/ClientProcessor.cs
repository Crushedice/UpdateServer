using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Signature;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

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

        private async void CreateDeltaforClient()
        {
            //Console.WriteLine("creating delta for client....");
            send("Starting Delta Creation");

            string[] allDeltas = Directory.GetFiles(_client.ClientFolder, "*", SearchOption.AllDirectories);
            int allc = allDeltas.Count();
            int prg = 0;
            string _sighash = "";
            string _deltahash = "";
            string deltaFilePath = "";

            foreach (string x in allDeltas)
            {
                string relativePath = x.Replace(_client.ClientFolder, string.Empty);

                string newFilePath = Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\..") +
                                     relativePath.Replace(".octosig", string.Empty);

                _sighash = _client.missmatchedFilehashes.First(f => f.Key == newFilePath).Value;

                string OrigPath = _client.missmatchedFilehashes[_sighash];

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
                    FileLogger.CLog("  Error in Create Delta:  " + e.Message, "Errors.txt");
                    Console.WriteLine("  Error in Create Delta:  " + e.Message, "Errors.txt");
                }

                try
                {
                    _deltahash = await CalculateMD5(deltaFilePath);
                    File.Copy(deltaFilePath, UpdateServerEntity.DeltaStorage + "\\" + _deltahash);
                    var nee = new Dictionary<string, string>();
                    nee.Add(_deltahash, OrigPath);
                    UpdateServerEntity.DeltaFileStorage.Add(_sighash, nee);
                }
                catch (Exception e)
                {
                    if (UpdateServerEntity._debug)
                        Console.WriteLine("Md5 For Delta Failed  " + e.InnerException);
                }

                prg++;
                send($"Delta Progress: {prg} / {allc}");
                UpdateServerEntity.Puts($"Waiting for {prg} / {allc}");
            }

            // await Task.WhenAll(tasks).ConfigureAwait(false);
            Heart.savesinglefile();
            EndThisOne();

            //Console.WriteLine("all threads Exited.  Sending data");
        }

        private async Task CreateZipFile(bool additionalfiles = false)
        {
            int retrycount = 0;
            // Create and open a new ZIP file
            send("Making ZipFile");
            int allitems = _client.dataToSend.Count();
            string zipFileName = _client.Clientdeltazip;
            string zipFileName2 = "additionalfiles.zip" ;
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
                            Console.WriteLine(e.Message, "Errors.txt");
                            FileLogger.CLog(e.Message, "Errors.txt");
                        }

                   
                }

                _client.filetoDelete.Add(zipFileName);
            }
            catch (Exception e)
            {
                Console.WriteLine("  Error in pack zip:  " + e.Message, "Errors.txt");
                FileLogger.CLog("  Error in pack zip:  " + e.Message, "Errors.txt");
                goto retrying;
            }

            SendZipFile(zipFileName);

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
            foreach (var t in _client.MatchedDeltas)
            {
                string deltapath = UpdateServerEntity.DeltaStorage + "\\" + t.Key;
                string destpath = _client.ClientFolder + t.Value;
                string deltaOutputDirectory = Path.GetDirectoryName(destpath);
                if (!Directory.Exists(deltaOutputDirectory))
                    Directory.CreateDirectory(deltaOutputDirectory);

                File.Copy(deltapath, destpath);
            }

            CreateDeltaforClient();
        }

        private void send(string msg)
        {
            UpdateServerEntity.SendProgress(_client._guid, msg);
        }

        private async Task SendZipFile(string zip, bool stored = false)
        {

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