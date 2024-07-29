﻿using Newtonsoft.Json;
using StringHelp;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UpdateServer;
using UpdateServer.Classes;
using WatsonTcp;

public class UpdateServerEntity
{
    public delegate void pooldelegate();

    public delegate Task SendMsg(string a, string b, bool c);

    public delegate Task SendNet(Guid a, string b, bool c = false);

    public static bool _debug;
    public static ConsoleWriter consoleWriter = new ConsoleWriter();
    public static bool consolle = true;
    public static int currCount;
    public static Dictionary<string, Dictionary<string, string>> DeltaFileStorage =
        new Dictionary<string, Dictionary<string, string>>();
    public static string DeltaStorage = "DeltaStorage";
    public static UpdateServerEntity instance;
    public static int MaxProcessors = 3;
    public static pooldelegate poolp;
    public static string Rustfolderroot = "Rust";
    public static WatsonTcpServer server;
    private static readonly long MaxFolderSize = 53687091200;
    private static SendNet _sendNet;
    private static string certFile = string.Empty;
    private static string certPass = string.Empty;
    private static List<string> ClientFiles = new List<string>();
    private static Dictionary<Guid, UpdateClient> CurrentClients = new Dictionary<Guid, UpdateClient>();
    private static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
    private static List<ClientProcessor> Occupants = new List<ClientProcessor>();
    #if DEBUG
    private static string serverIp = "127.0.0.1";
    #else
    private static string serverIp = "51.91.214.177";
    #endif

    private static int serverPort = 9090;

    private static bool useSsl = false;

    private static Queue<ClientProcessor> WaitingClients = new Queue<ClientProcessor>();

    public UpdateServerEntity()
    {
        if (File.Exists("Debug")) _debug = true;
        Console.WriteLine("Server Start...");

        _sendNet = SendNetData;
        instance = this;
        FileHashes = Heart.FileHashes;
        foreach (string x in Directory.GetFiles(Rustfolderroot, "*", SearchOption.AllDirectories))
        {
            string trimmedpath = x.Replace(Path.GetDirectoryName(x), "");
            ClientFiles.Add(trimmedpath);
        }

        if(File.Exists("SingleDelta.json"))
            DeltaFileStorage = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string,string>>>(File.ReadAllText("SingleDelta.json"));

        Puts($"DeltaStorage has {DeltaFileStorage.Count} Files");

        Start();
    }

    public static long DirSize(DirectoryInfo d)
    {
        long size = 0;
        // Add file sizes.
        var fis = d.GetFiles();
        foreach (FileInfo fi in fis) size += fi.Length;
        // Add subdirectory sizes.
        var dis = d.GetDirectories();
        foreach (DirectoryInfo di in dis) size += DirSize(di);
        return size;
    }

    public static void EndCall(UpdateClient client, ClientProcessor pr, string zipFileName, bool abort = false)
    {
        //_CurrLock = false;
        Occupants.Remove(pr);
        UpdateClient curr = client;
        TickQueue();

        FinalizeZip(client._guid, zipFileName);
    }

    public static Task Extract(string path, UpdateClient currentUser)
    {
        SendProgress(currentUser._guid, "Extracting Zip..");
        Encoding enc = GetEncoding(path);
        string exepath = Directory.GetCurrentDirectory();
        string finalPath = exepath + "//" + currentUser.ClientFolder + "//Rust";
        try
        {
            if (Directory.Exists(currentUser.ClientFolder + "//Rust"))
                Directory.Delete(currentUser.ClientFolder + "//Rust", true);

            ZipFile.ExtractToDirectory(path, finalPath, enc);
            if (IsFileReady(path))
                File.Delete(path);
        }
        catch (Exception e)
        {
            FileLogger.CLog("Error in Extract : " + e.Message, "Errors.txt");
            // sendingMsg("ERR|Error In Extracting Zip Serverside", currentUser.ClientIP, false);
            SendProgress(currentUser._guid, "Error in Extract : " + e.Message);

            return Task.CompletedTask;
        }

        SendProgress(currentUser._guid, "Enqueue for processing...");
        ClientProcessor newprocessor = new ClientProcessor(currentUser);
        WaitingClients.Enqueue(newprocessor);
        TickQueue();

        return Task.CompletedTask;
    }

    public static void FinalizeZip(Guid id, string zip, bool stored = false)
    {
        UpdateClient client = CurrentClients[id];
        if (stored)
        {
            FileLogger.CLog("  Sent Stored Deltas : " + BytesToString(new FileInfo(zip).Length) + " to: " + id,
                "Finished.txt");
            Puts("SendStoredDelta");
        }
        else
        {
            FileLogger.CLog("  UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length) + " to: " + id,
                "Finished.txt");
            Puts("Created New Update");
        }

        // Console.WriteLine("Alldone for client :" + ip);
        // Console.WriteLine("UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length));
        // Console.WriteLine("Cleaning up....");

        if (client.SignatureHash != string.Empty)
        {
            //  Console.WriteLine("sighash not empty");
            var allfiles = new DirectoryInfo(DeltaStorage).GetFiles();
            if (!allfiles.Any(x => x.Name == client.SignatureHash + ".zip"))
                File.Copy(zip, DeltaStorage + "\\" + client.SignatureHash + ".zip");
            // Console.WriteLine("File moved");
        }

        foreach (string x in client.filetoDelete) File.Delete(x);
    }

    public static Encoding GetEncoding(string filename)
    {
        // Read the BOM
        byte[] bom = new byte[4];
        using (FileStream file = new FileStream(filename, FileMode.Open, FileAccess.Read))
        {
            file.Read(bom, 0, 4);
        }

        // Analyze the BOM
        if (bom[0] == 0x2b && bom[1] == 0x2f && bom[2] == 0x76) return Encoding.UTF7;
        if (bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) return Encoding.UTF8;
        if (bom[0] == 0xff && bom[1] == 0xfe) return Encoding.Unicode; //UTF-16LE
        if (bom[0] == 0xfe && bom[1] == 0xff) return Encoding.BigEndianUnicode; //UTF-16BE
        if (bom[0] == 0 && bom[1] == 0 && bom[2] == 0xfe && bom[3] == 0xff) return Encoding.UTF32;
        return Encoding.ASCII;
    }

    public static bool IsFileReady(string filename)
    {
        // If the file can be opened for exclusive access it means that the file
        // is no longer locked by another process.
        try
        {
            using (FileStream inputStream = File.Open(StringHelper.AddQuotesIfRequired(filename), FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite))
            {
                return inputStream.Length > 0;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Puts(string msg)
    {
        if (_debug)
            Console.WriteLine(msg);
    }

    public static async void quickhashes(Guid id)
    {
        var h = InputDictionary(FileHashes);

            


        _ =await server.SendAndWaitAsync(50000, id, "VERSION|"+Heart.Vversion);

        Thread.Sleep(500);

        _ = await server.SendAndWaitAsync(30000, id, "SOURCEHASHES|"+JsonConvert.SerializeObject(h));


    }

    public static async Task SendProgress(Guid id, string msg)
    {
            var resp = server.SendAndWaitAsync(5000, id,"REPORT|"+ msg);
    }

    public static void TickQueue()
    {
        Puts("Waitqueue Count: " + WaitingClients.Count());

        File.WriteAllText("SingleDelta.json",
            JsonConvert.SerializeObject(DeltaFileStorage, Formatting.Indented));

        Puts("Current Processors: " + Occupants.Count());

        if (WaitingClients.Count > 0)
        {
            if (Occupants.Count < 2)
            {
                Puts("Occupants less then 3. Processing commence ");
                ClientProcessor nextone = WaitingClients.Dequeue();
                Occupants.Add(nextone);
                nextone.StartupThisOne();
                //_CurrLock = true;
                // return;
            }

            foreach (ClientProcessor c in WaitingClients) c.Notify();
        }
    }

    public async Task SendNetData(Guid id, string zip, bool stored = false)
    {
        Puts("SendNetData...");
        UpdateClient client = CurrentClients[id];

        if (!stored)
        {
            var metadata = new Dictionary<string, string>();
            metadata.Add("1", "2");
            using (FileStream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                       FileOptions.Asynchronous))
            {
                _ = await server.SendAsync(id, source.Length, source, InputDictionary(metadata)).ConfigureAwait(false);
            }
        }
        else
        {
            using (FileStream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                       FileOptions.Asynchronous))
            {
                _ = await server.SendAsync(id, source.Length, source).ConfigureAwait(false);
            }
        }

        foreach (string x in client.filetoDelete) File.Delete(x);
    }

    private static string BytesToString(long byteCount)
    {
        string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
        if (byteCount == 0)
            return "0" + suf[0];
        long bytes = Math.Abs(byteCount);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return Math.Sign(byteCount) * num + suf[place];
    }

    private static void CheckforCleanup()
    {
        long cursize = DirSize(new DirectoryInfo(DeltaStorage));

        if (cursize > MaxFolderSize) DeleteOldFiles();
    }

    private static void CheckUsers()
    {
        int users = server.Connections;
        currCount = users;
        string[] AllDirs = Directory.GetDirectories("ClientFolders");
        foreach (string x in AllDirs)
        {


                
                string thisone = x.Split(Path.DirectorySeparatorChar).Last();

                if (thisone.Contains(".")) return;
    
                Guid guid = Guid.Parse(thisone);

                // Console.WriteLine("Checking User " + thisone);

               // Puts($"PathDirName: {thisone} , and \n usrip : {server.ListClients().Where(dx => dx.Guid == guid)}");
                if (!server.IsClientConnected(guid))
                    try
                    {
                        Directory.Delete(x, true);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error In deleting Folder." + e.Message);
                    }
            
        }
    }

    private static async void ClientConnected(object sender, ConnectionEventArgs e)
    {
        Puts("Client Connected");
        string fixedip = e.Client.Guid.ToString();
        // string clientfolder = @"ClientFolders\" + args.IpPort.Split(':')[0].Replace('.', '0');
        string clientfolder = @"ClientFolders\" + fixedip;
        string fullpath = new DirectoryInfo(clientfolder).FullName;

        if (!Directory.Exists(fullpath))
            Directory.CreateDirectory(fullpath);

        if (!Directory.Exists(clientfolder))
            Directory.CreateDirectory(clientfolder);
        string Clientdeltazip = clientfolder + @"\" + fixedip + ".zip";

        CurrentClients.Add(e.Client.Guid,
            new UpdateClient(e.Client.Guid, e.Client.IpPort, clientfolder, Clientdeltazip));
        currCount++;

        quickhashes(e.Client.Guid);

        TickQueue();

        CheckforCleanup();

        CheckUsers();
    }

    private static void ClientDisconnected(object sender, DisconnectionEventArgs args)
    {
        Puts("Client Disconnected");

        ClientProcessor result = Occupants.FirstOrDefault(x => x.tcpipport == args.Client.IpPort);

        if (result != null)
        {
            Occupants.Remove(result);
            Puts("ClientDisconnect recognised . Removed.");
            TickQueue();

            CurrentClients.Remove(args.Client.Guid);
        }
        else
        {
            Puts("ClientDisconnect linq result is null");
        }
    }

    private static void DeleteOldFiles()
    {
       // var last = Directory.EnumerateFiles(DeltaStorage)
       //     .Select(fileName => new FileInfo(fileName))
       //     .OrderByDescending(fileInfo => fileInfo.LastWriteTime) // or "CreationTime"
       //     .Skip(50) // Skip 50 newest files
       //     .Select(fileInfo => fileInfo.FullName);
       //
       //
       // string fileName;
       // File.Delete(fileName);
        
    }


    private static void PackAdditionalFiles(UpdateClient client)
    {
            CreateZipFile(client,true);

    }

    private static Dictionary<string, object> InputDictionary(Dictionary<string, string> _data)
    {
        var ret = new Dictionary<string, object>();
        foreach (var x in _data)
        {
            string K = x.Key;
            string V = x.Value;
            ret.Add(K, V);
        }

        return ret;
    }

    private static void Start()
    {
        server = new WatsonTcpServer(serverIp, serverPort);
        WatsonTcpKeepaliveSettings ht = new WatsonTcpKeepaliveSettings();
        ht.EnableTcpKeepAlives = true;
        server.Events.ClientConnected += ClientConnected;
        server.Events.ClientDisconnected += ClientDisconnected;
        server.Events.StreamReceived += StreamReceived;
        server.Callbacks.SyncRequestReceivedAsync = SyncRequestReceived;
        server.Keepalive = ht;
        //  server.Settings.DebugMessages = _debug;
        // if(_debug)
        // server.Settings.Logger=(s,e)=>Console.WriteLine(e);
        server.Start();
        Console.WriteLine("Server Started.");
    }
        private static async Task CreateZipFile(UpdateClient _client ,bool additionalfiles = false)
        {
            int retrycount = 0;
            // Create and open a new ZIP file
           
            int allitems = _client.dataToSend.Count();
            string zipFileName = _client.Clientdeltazip;
            string zipFileName2 = "additionalfiles.zip" ;
            if (File.Exists(zipFileName2)) File.Delete(zipFileName2);
            string clientRustFolder = _client.ClientFolder + "\\Rust\\";

            int itemcount = 0;


            if (additionalfiles)
            {
                using (ZipArchive zip = ZipFile.Open(zipFileName2, ZipArchiveMode.Create))
                {
                    foreach (string y in _client.dataToAdd)
                    {
                        var realfilepath = "Rust\\" + y;


                         if (!File.Exists(realfilepath)) continue;

                         string relativePath = y.Replace("Rust\\", string.Empty);
                         string fixedname = relativePath.Replace('\\', '/');
                         zip.CreateEntryFromFile(realfilepath, fixedname, CompressionLevel.Optimal);
                         itemcount++;

                        

                    }

                }
                _client.filetoDelete.Add(zipFileName2);
                SendZipFile(_client,zipFileName2,true);

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

            SendZipFile(_client,zipFileName);

            return;

        retrying:

            if (retrycount > 5)
            {
                //    FileLogger.CLog(DateTime.Now.ToString("MM/dd HH:mm") + "  abort of packing zip after  " + retrycount + " Retry ", "Finished.txt");
               
                return;
            }

            await Task.Delay(10000);
            retrycount++;
            goto starrt;
        }
    private static async Task SendZipFile(UpdateClient _client ,string zip, bool stored = false)
    {

        if (stored)
        {
            using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                       FileOptions.Asynchronous))
            {
                await UpdateServerEntity.server.SendAsync(_client._guid, source.Length, source);
            }

          

        }
        else
        {
            
        var metadata = new Dictionary<string, object>();
        metadata.Add("1", "2");
        using (Stream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                   FileOptions.Asynchronous))
        {
            await UpdateServerEntity.server.SendAsync(_client._guid, source.Length, source, metadata);
        }
        }
    }
    private static void StreamReceived(object sender, StreamReceivedEventArgs args)
    {
        Puts("Stream received");
        try
        {
            var Meta = args.Metadata;
            UpdateClient user = CurrentClients[args.Client.Guid];
            string userFolder = user.ClientFolder;

            string zippath = userFolder+Path.DirectorySeparatorChar+"signatures.zip";
            int bytesRead = 0;
            int bufferSize = 65536;
            byte[] buffer = new byte[bufferSize];
            long bytesRemaining = args.ContentLength;
            Stream file = new FileStream(zippath, FileMode.Create);

            while (bytesRemaining > 0)
            {
                bytesRead = args.DataStream.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    byte[] consoleBuffer = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, consoleBuffer, 0, bytesRead);
                    file.Write(consoleBuffer, 0, bytesRead);
                }

                bytesRemaining -= bytesRead;
            }

            file.Flush();
            file.Close();
            Task.Run(() => Extract(zippath, user));
        }
        catch (Exception e)
        {
            Puts("Error in StreamReceived : " + e.Message);
        }
    }

    private static async Task<SyncResponse> SyncRequestReceived(SyncRequest req)
    {
        Puts("syncrequest");
        string stringmsg = Encoding.UTF8.GetString(req.Data);
        UpdateClient _client = CurrentClients[req.Client.Guid];

        var Header = stringmsg.Split('|').First();
        var sourcemessage = stringmsg.Split('|').Last();




        if (Header =="MISS")
        {
            UpdateClient client = CurrentClients[req.Client.Guid];

            var adding = JsonConvert.DeserializeObject<Dictionary<string, string>>(sourcemessage).Keys.ToList();

                client.dataToAdd = adding;

            Task.Run(() => PackAdditionalFiles(client));

            return new SyncResponse(req, "null");
        }

        if (Header == "DIFF")
        {

            _client.AddMissMatchFilelist(JsonConvert.DeserializeObject<Dictionary<string,string>>(sourcemessage));


            var sendb = JsonConvert.SerializeObject(_client.GetTrimmedList());



            if (_client.MatchedDeltas.Count() >= _client.missmatchedFilehashes.Count())
            {
                SendProgress(_client._guid, "Enqueue for processing...");
                ClientProcessor newprocessor = new ClientProcessor(_client);
                WaitingClients.Enqueue(newprocessor);
                TickQueue();
            }

            return new SyncResponse(req, _client.MatchedDeltas.Count().ToString()+"|"+sendb);


        }

        return new SyncResponse(req, "null");
    }
}