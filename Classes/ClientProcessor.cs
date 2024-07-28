using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Signature;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Threading.Tasks;

namespace UpdateServer.Classes
{
    public class ClientProcessor
    {
        public string tcpipport;
        private ClientProcessor _instanceRef;
        private bool _running;
        private int NrInQueue;

        

        private UpdateClient _client { get; }

        public ClientProcessor(UpdateClient user)
        {
            _client = user;
            tcpipport = user.ClientIP;
            _instanceRef = this;
        }

        public async Task CreateZipFile()
        {
            int retrycount = 0;
            // Create and open a new ZIP file
            Task.Run(() => UpdateServerEntity.SendProgress(_client.ClientIP, "Making ZipFile"));
            int allitems = _client.dataToAdd.Count() + _client.dataToSend.Count();
            int itemcount = 0;
        starrt:

            string zipFileName = _client.Clientdeltazip;
            if (File.Exists(zipFileName)) File.Delete(zipFileName);
            string clientRustFolder = _client.ClientFolder + "\\Rust\\";
            try
            {
                // var zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create);
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
                            Task.Run(() =>
                                UpdateServerEntity.SendProgress(_client.ClientIP,
                                    $"Packing Update: {itemcount} / {allitems}"));
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message, "Errors.txt");
                            FileLogger.CLog(e.Message, "Errors.txt");
                        }

                    foreach (string y in _client.dataToAdd)
                        try
                        {
                            if (!File.Exists(y)) continue;

                            string relativePath = y.Replace("Rust\\", string.Empty);
                            string fixedname = relativePath.Replace('\\', '/');
                            zip.CreateEntryFromFile(y, fixedname, CompressionLevel.Optimal);
                            itemcount++;
                            Task.Run(() =>
                                UpdateServerEntity.SendProgress(_client.ClientIP,
                                    $"Packing Update: {itemcount} / {allitems}"));
                        }
                        catch (Exception r)
                        {
                            Console.WriteLine(r.Message, "Errors.txt");
                            FileLogger.CLog(r.Message, "Errors.txt");
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

            Task.Run(() => SendZipFile(zipFileName));

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

        public void Notify()
        {
            Task.Run(() => UpdateServerEntity.SendProgress(_client.ClientIP, "Waiting in Queue...."));
        }

        public void StartupThisOne()
        {
            _running = true;
            Task.Run(() => CreateDeltaforClient());
        }

        private async void CreateDeltaforClient()
        {
            //Console.WriteLine("creating delta for client....");
            Task.Run(() => UpdateServerEntity.SendProgress(_client.ClientIP, "Starting Delta Creation"));

            string[] allDeltas = Directory.GetFiles(_client.ClientFolder, "*", SearchOption.AllDirectories);
            int allc = allDeltas.Count();
            int prg = 0;
            string _sighash = "";
            string _deltahash = "";
            string deltaFilePath = "";

            foreach (string x in allDeltas)
            {

                if (!x.Contains(".zip"))
                {
                    _sighash = await CalculateMD5(x);
                    if (Heart.singleStoredDelta.ContainsKey(_sighash))
                    {
                        Console.WriteLine("Server found matching stored sighash! WOOP");

                    }
                    else
                    {
                        if (UpdateServerEntity._debug)
                        {
                            Console.WriteLine($"{_sighash} was not found in Storage. Sad.");
                        }

                    } 
                }

                try
                {
                    if (!x.Contains(".zip"))
                    {
                        _client.filetoDelete.Add(x);
                        string filename = Path.GetFileName(x);
                        string relativePath = x.Replace(_client.ClientFolder, string.Empty);

                        string newFilePath = Path.GetFullPath(UpdateServerEntity.Rustfolderroot + "\\..") +
                                             relativePath.Replace(".octosig", string.Empty);
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
                    UpdateServerEntity.SendProgress(_client.ClientIP, $"Error In creating Delta for {x}");
                    FileLogger.CLog("  Error in Create Delta:  " + e.Message, "Errors.txt");
                    Console.WriteLine("  Error in Create Delta:  " + e.Message, "Errors.txt");
                }

                if (File.Exists(deltaFilePath))
                {
                    try
                    {
                        _deltahash = await CalculateMD5(deltaFilePath);
                        File.Copy(deltaFilePath, UpdateServerEntity.SingleStorage + "\\" + _deltahash);
                         Heart.addsingledelta(_sighash, _deltahash);
                    }
                    catch (Exception e)
                    {
                        if (UpdateServerEntity._debug)
                            Console.WriteLine("Md5 For Delta Failed  " + e.InnerException);
                    }
                }
                else
                {

                    if (UpdateServerEntity._debug)
                        Console.WriteLine("Deltafile for Md5 not found??");

                }

                prg++;
                UpdateServerEntity.SendProgress(_client.ClientIP, $"Delta Progress: {prg} / {allc}");
                UpdateServerEntity.Puts($"Waiting for {prg} / {allc}");
            }

            // await Task.WhenAll(tasks).ConfigureAwait(false);
            Task.Run(() => Heart.savesinglefile());
            EndThisOne();

            //Console.WriteLine("all threads Exited.  Sending data");
        }

        private void EndThisOne()
        {
            UpdateServerEntity.Puts("Await Done Ending thisone");

            _ = CreateZipFile();
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

        private async Task SendZipFile(string zip, bool stored = false)
        {
            var metadata = new Dictionary<object, object>();
            metadata.Add("1", "2");

            using (FileStream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                       FileOptions.Asynchronous))
            {
                await UpdateServerEntity.server.SendAsync(_client.ClientIP, source.Length, source, metadata)
                    .ConfigureAwait(false);
            }

            UpdateServerEntity.EndCall(_client, this, zip);
        }
    }
}