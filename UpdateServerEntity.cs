using Newtonsoft.Json;
using StringHelp;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using UpdateServer;
using UpdateServer.Classes;
using WatsonTcp;

public class StreamStore
{
    public long ContentLength;
    public Stream sStream;
    public Dictionary<object, object> StreamData;
    public string userIP;

    public StreamStore(string ipPort, Dictionary<object, object> xtradata, long contentLength, Stream stream)
    {
        userIP = ipPort;
        StreamData = xtradata;
        ContentLength = contentLength;
        sStream = stream;
    }
}

public class UpdateServerEntity
{
    public delegate void pooldelegate();

    public delegate Task SendMsg(string a, string b, bool c);

    public delegate Task SendNet(string a, string b, bool c = false);

    public static bool _debug;

    public static SendNet _sendNet;

    public static List<string> ClientFiles = new List<string>();

    public static ConsoleWriter consoleWriter = new ConsoleWriter();

    public static bool consolle = true;

    public static int currCount;

    public static Dictionary<string, UpdateClient> CurrentClients = new Dictionary<string, UpdateClient>();

    public static string DeltaStorage = "DeltaStorage";
    public static string SingleStorage = "\\SingleStorage\\";

    public static Dictionary<string, string> FileHashes = new Dictionary<string, string>();

    /// <summary>
    /// singleStoredDelta for matching hash on octosigs to octodeltas for client
    /// Keys = Octosig Values =  Octodelta (Which is also the filename)
    /// </summary>
    public static  Dictionary<string, string> singleStoredDelta = new Dictionary<string, string>();

    public static UpdateServerEntity instance;

    public static int MaxProcessors = 3;

    //public static bool _CurrLock = false;
    //public static ClientProcessor Occupant;
    public static List<ClientProcessor> Occupants = new List<ClientProcessor>();

    // public static SendMsg sendingMsg;
    public static pooldelegate poolp;

    public static string Rustfolderroot = "Rust";

    // public static List<Task> tasks = new List<Task>();
    public static WatsonTcpServer server;

    public static List<string> StoredDeltas = new List<string>();

    public static Queue<ClientProcessor> WaitingClients = new Queue<ClientProcessor>();

    private static readonly long MaxFolderSize = 53687091200;

    private static string certFile = string.Empty;

    private static string certPass = string.Empty;

    private static string serverIp = "0.0.0.0";

    private static int serverPort = 9000;

    private static bool useSsl = false;

    public UpdateServerEntity()
    {
        if (File.Exists("Debug")) _debug = true;
        Console.WriteLine("Server Start...");
        CheckDeltas();
        //sendingMsg = SendMessage;
        _sendNet = SendNetData;
        instance = this;
        FileHashes = Heart.FileHashes;
        var ashes = File.ReadAllText("SingleDelta.json");
        singleStoredDelta = JsonConvert.DeserializeObject<Dictionary<string, string>>(ashes);


        foreach (string x in Directory.GetFiles(Rustfolderroot, "*", SearchOption.AllDirectories))
        {
            string trimmedpath = x.Replace(Path.GetDirectoryName(x), "");
            ClientFiles.Add(trimmedpath);
        }

        Start();
    }

    public static void CheckDeltas()
    {
        var allfiles = new DirectoryInfo(DeltaStorage).GetFiles();
        foreach (FileInfo x in allfiles)
        {
            string delhash = x.Name.Replace(".zip", "");
            if (!StoredDeltas.Contains(delhash)) StoredDeltas.Add(delhash);
        }
    }

    public static void CheckUsers()
    {
        int users = server.Connections;
        currCount = users;
        string[] AllDirs = Directory.GetDirectories("ClientFolders");
        foreach (string x in AllDirs)
        {
            string thisone = x.Split(Path.DirectorySeparatorChar).Last();
            string userIpp = thisone.Replace("-", ":"); //userIpp.Replace(@"ClientFolders\", string.Empty);
            // Console.WriteLine("Checking User " + thisone);

            Puts($"PathDirName: {thisone} , and \n usrip : {userIpp}.");
            if (!server.IsClientConnected(userIpp))
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

        FinalizeZip(client.ClientIP, zipFileName);

        return;
        if (server.IsClientConnected(client.ClientIP))
        {
            Task.Run(() => _sendNet(client.ClientIP, zipFileName));
        }
        else
        {
            if (client.SignatureHash != string.Empty)
                if (!StoredDeltas.Contains(client.SignatureHash))
                {
                    StoredDeltas.Add(client.SignatureHash);

                    File.Move(zipFileName, DeltaStorage + "\\" + client.SignatureHash + ".zip");
                }
        }
    }

    public static Task Extract(string path, UpdateClient currentUser)
    {
        SendProgress(currentUser.ClientIP, "Extracting Zip..");
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
            Task.Delay(1000);
            server.DisconnectClient(currentUser.ClientIP);
            return Task.CompletedTask;
        }

        SendProgress(currentUser.ClientIP, "Enqueue for processing...");
        ClientProcessor newprocessor = new ClientProcessor(currentUser);
        WaitingClients.Enqueue(newprocessor);
        TickQueue();

        return Task.CompletedTask;
    }

    public static void FinalizeZip(string ip, string zip, bool stored = false)
    {
        UpdateClient client = CurrentClients[ip];
        if (stored)
        {
            FileLogger.CLog("  Sent Stored Deltas : " + BytesToString(new FileInfo(zip).Length) + " to: " + ip,
                "Finished.txt");
            Puts("SendStoredDelta");
        }
        else
        {
            FileLogger.CLog("  UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length) + " to: " + ip,
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
                File.Move(zip, DeltaStorage + "\\" + client.SignatureHash + ".zip");
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

    public static bool IsFileInClient(string filename)
    {
        string fixeds = "";
        if (filename.Contains("octosig"))
        {
            fixeds = filename.Replace(".octosig", "");

            return ClientFiles.Contains(fixeds);
        }

        return ClientFiles.Contains(filename);
    }

    public static bool IsFileReady(string filename)
    {
        // If the file can be opened for exclusive access it means that the file
        // is no longer locked by another process.
        try
        {
            using (FileStream inputStream = File.Open(StringHelper.AddQuotesIfRequired(filename), FileMode.Open,
                       FileAccess.Read, FileShare.Read))
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

    public static async void quickhashes(string ipPort)
    {
        try
        {
            var metad = new Dictionary<object, object>();
            metad.Add(Heart.Vversion, string.Empty);
            string hashstring = JsonConvert.SerializeObject(FileHashes);
            _ = await server.SendAsync(ipPort, hashstring, metad).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Puts("Exception: " + e.Message);
            // _ = sendingMsg(Heart.Vversion, ipPort, true);
        }
    }

    public static async Task SendProgress(string ip, string msg)
    {
        try
        {
            SyncResponse resp = server.SendAndWait(500, ip, msg);
        }
        catch
        {
            Puts("SendProgress Error");
        }
    }

    public static void TickQueue()
    {
        Puts("Waitqueue Count: " + WaitingClients.Count());

        File.WriteAllText("SingleDelta.json", JsonConvert.SerializeObject(singleStoredDelta, Formatting.Indented));

        Puts("Current Processors: " + Occupants.Count());

        if (WaitingClients.Count > 0)
        {
            if (Occupants.Count < 3)
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

    public async Task SendNetData(string ip, string zip, bool stored = false)
    {
        Puts("SendNetData...");
        UpdateClient client = CurrentClients[ip];
        var metadata = new Dictionary<object, object>();
        metadata.Add("1", "2");

        using (FileStream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                   FileOptions.Asynchronous))
        {
            _ = await server.SendAsync(ip, source.Length, source, metadata).ConfigureAwait(false);
        }

        if (stored)
        {
            FileLogger.CLog("  Sent Stored Deltas : " + BytesToString(new FileInfo(zip).Length) + " to: " + ip,
                "Finished.txt");
            Puts("SendStoredDelta");
        }
        else
        {
            FileLogger.CLog("  UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length) + " to: " + ip,
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
                File.Move(zip, DeltaStorage + "\\" + client.SignatureHash + ".zip");
            // Console.WriteLine("File moved");
        }

        foreach (string x in client.filetoDelete) File.Delete(x);
    }

    //  public async Task SendMessage(string userInput, string ipPort, bool SendHashes)
    //  {
    //      ITransaction transaction = SentrySdk.StartTransaction(
    //          "SendMessage Async",
    //          ipPort, userInput
    //      );
    //
    //      byte[] data = null;
    //      MemoryStream ms = null;
    //      Dictionary<object, object> metadata;
    //      if (SendHashes)
    //      {
    //          ISpan span1 = transaction.StartChild("SendHashes", ipPort);
    //          Puts("SendHashes");
    //          var metad = new Dictionary<object, object>();
    //          metad.Add(Heart.Vversion, string.Empty);
    //          Puts("VersionSend: " + Heart.Vversion);
    //          // Console.WriteLine("Sending Hashes.. \n");
    //          string hashstring = JsonConvert.SerializeObject(FileHashes); //InputDictionaryT();
    //          if (string.IsNullOrEmpty(userInput)) return;
    //          data = Encoding.UTF8.GetBytes(userInput);
    //          ms = new MemoryStream(data);
    //          //Console.WriteLine("SendingMeta Datalength:  " + ms.Length + "/ " + data.Length + "\n  MetaLength: ");
    //          // var success = await server.SendAsync(ipPort,metadata, data.Length, ms);
    //          await server.SendAsync(ipPort, hashstring, metad).ConfigureAwait(false);
    //          //bool success = server.Send(ipPort, Encoding.UTF8.GetBytes(message));
    //          span1.Finish();
    //      }
    //      else
    //      {
    //          // Console.WriteLine("IP:Port: "+ipPort);
    //          // Console.WriteLine("Sending Only Message... \n");
    //          //  Console.WriteLine("Data: "+userInput);
    //          Puts("NoHashes");
    //          ISpan span2 = transaction.StartChild("Send Stored Delta", ipPort);
    //          //  data = Encoding.UTF8.GetBytes(userInput);
    //          //  ms = new MemoryStream(data);
    //
    //          // Console.WriteLine("SendingMsgDatalength:  " + ms.Length + "/ " + data.Length + "\n");
    //          // await server.SendAsync(ipPort, ms);
    //
    //          //  Console.WriteLine("Sending message:" + data);
    //          var success = server.SendAsync(ipPort, userInput);
    //          if (userInput == "STORE|true")
    //          {
    //              UpdateClient client = CurrentClients[ipPort];
    //              string filen = DeltaStorage + "\\" + client.SignatureHash + ".zip";
    //              _ = _sendNet(ipPort, filen, true);
    //          }
    //
    //          span2.Finish();
    //      }
    //  }
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

    private static async void ClientConnected(object sender, ConnectionEventArgs e)
    {
        Puts("Client Connected");
        string msg = string.Empty;
        string fixedip = e.IpPort.Replace(':', '-');
        // string clientfolder = @"ClientFolders\" + args.IpPort.Split(':')[0].Replace('.', '0');
        string clientfolder = @"ClientFolders\" + fixedip;
        string fullpath = new DirectoryInfo(clientfolder).FullName;

        if (!Directory.Exists(fullpath))
            Directory.CreateDirectory(fullpath);

        if (!Directory.Exists(clientfolder))
            Directory.CreateDirectory(clientfolder);

        CurrentClients.Add(e.IpPort, new UpdateClient(e.IpPort, clientfolder));

        CurrentClients[e.IpPort].Clientdeltazip = clientfolder + @"\" + e.IpPort.Replace(':', '-') + ".zip";
        currCount++;

        //     SendMessage("V|" + Heart.Vversion + "|", args.IpPort, true);

        // sendingMsg(Heart.Vversion, e.IpPort, true);
        quickhashes(e.IpPort);
        TickQueue();
        CheckforCleanup();
        CheckUsers();
    }

    private static void ClientDisconnected(object sender, DisconnectionEventArgs args)
    {
        Puts("Client Disconnected");

        var ox = from v in Occupants
                 where v.tcpipport == args.IpPort
                 select v;

        if (ox != null)
        {
            Occupants.Remove(ox.First());

            TickQueue();
        }

        CurrentClients.Remove(args.IpPort);
    }

    private static void DeleteOldFiles()
    {
        var last = Directory.EnumerateFiles(DeltaStorage)
            .Select(fileName => new FileInfo(fileName))
            .OrderByDescending(fileInfo => fileInfo.LastWriteTime) // or "CreationTime"
            .Skip(50) // Skip 50 newest files
            .Select(fileInfo => fileInfo.FullName);

        foreach (string fileName in last)
            try
            {
                File.Delete(fileName);
            }
            catch
            {
            }
    }

    private static async Task<bool> fileexisting(UpdateClient client, string msg)
    {
        client.SignatureHash = msg;
        if (StoredDeltas.Contains(client.SignatureHash))
            // Task.Run(() => SendStoredUpdate(client, msg));
            return true;

        return false;
    }

    private static async Task SendStoredUpdate(UpdateClient client, string msg)
    {
        string filen = DeltaStorage + "\\" + client.SignatureHash + ".zip";
        // "STORE|true"
        await _sendNet(client.ClientIP, filen, true).ConfigureAwait(false);
    }

    private static void Start()
    {
        server = new WatsonTcpServer(serverIp, serverPort);
        WatsonTcpKeepaliveSettings ht = new WatsonTcpKeepaliveSettings();
        ht.EnableTcpKeepAlives = true;
        server.Events.ClientConnected += ClientConnected;
        server.Events.ClientDisconnected += ClientDisconnected;
        server.Events.StreamReceived += StreamReceived;
        server.Callbacks.SyncRequestReceived = SyncRequestReceived;
        server.Keepalive = ht;
        //  server.Settings.DebugMessages = _debug;
        // if(_debug)
        // server.Settings.Logger=(s,e)=>Console.WriteLine(e);
        server.Start();
        Console.WriteLine("Server Started.");
    }

    private static void StreamReceived(object sender, StreamReceivedEventArgs args)
    {
        Puts("Stream received");
        try
        {
            var Meta = args.Metadata;
            UpdateClient user = CurrentClients[args.IpPort];
            string userFolder = user.ClientFolder;
            string fixedname = args.IpPort.Replace(':', '-');

            string zippath = userFolder + @"\\" + "signatures" + fixedname + ".zip";
            // Console.WriteLine("New Stream");

            //  Console.WriteLine("Start Processing");
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
                // Console.WriteLine("bytesread 0 , looping around.");
            }

            //Console.WriteLine("Processing Meta if avalible");
            if (args.Metadata != null && args.Metadata.Count > 0)
            {
                // Console.WriteLine("Metadata:");
                foreach (var curr in args.Metadata)
                {
                    user.dataToAdd.Add("Rust\\" + curr.Key);
                    File.AppendAllText("DataToAdd.txt", curr.Key + "__" + curr.Value);
                }

                File.AppendAllText("DataToAdd.txt", "\n \n \n");
            }

            file.Flush();
            file.Close();
            Task.Run(() => Extract(zippath, user));
        }
        catch (Exception e)
        {
            FileLogger.CLog("Error in stream : " + e.Message, "Errors.txt");
            // Console.WriteLine("Exception thrown");
            Puts("Error in StreamReceived : " + e.Message);
        }
    }

    private static SyncResponse SyncRequestReceived(SyncRequest req)
    {
        Puts("syncrequest");
        try
        {
            UpdateClient client = CurrentClients[req.IpPort];
            string Messagee = string.Empty;
            var dict = new Dictionary<object, object>();
            if (req.Metadata != null && req.Metadata.Count > 0)
            {
                Messagee = req.Metadata.First().Key.ToString();

                bool ex = fileexisting(client, Messagee).Result;
                Puts("Looking for Hash : " + Messagee);
                if (ex)
                {
                    dict.Add("true", "bar");
                    Task.Factory.StartNew(() => { SendStoredUpdate(client, string.Empty); });

                    return new SyncResponse(req, dict, "null");
                }

                dict.Add("false", "bar");
                return new SyncResponse(req, dict, "null");
            }
            ///Dictionary<object, object> retMetadata = new Dictionary<object, object>();
            //   retMetadata.Add("foo", "bar");
            //   retMetadata.Add("bar", "baz");

            // Uncomment to test timeout
            // Task.Delay(10000).Wait();
            dict.Add("false", "bar");
            return new SyncResponse(req, dict, "null");
            dict.Add("false", "bar");
            return new SyncResponse(req, dict, "null");
        }
        catch (Exception e)
        {
            Puts("Error in SyncResponse : " + e.Message);
            var dict = new Dictionary<object, object>();
            dict.Add("false", "bar");
            //Console.WriteLine("Error with SyncResponse");
            return new SyncResponse(req, dict, "null");
        }
    }
}