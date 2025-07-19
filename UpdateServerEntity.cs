using Newtonsoft.Json;
using Sentry;
using StringHelp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // Use thread-safe collections for shared state
    private static readonly object CurrentClientsLock = new object();
    private static readonly object OccupantsLock = new object();
    private static readonly object WaitingClientsLock = new object();
    private static readonly object DeltaFileStorageLock = new object();
    private static Dictionary<Guid, UpdateClient> CurrentClients = new Dictionary<Guid, UpdateClient>();
    private static List<ClientProcessor> Occupants = new List<ClientProcessor>();
    private static Queue<ClientProcessor> WaitingClients = new Queue<ClientProcessor>();
    public static Dictionary<string, Dictionary<string, string>> DeltaFileStorage = new Dictionary<string, Dictionary<string, string>>();
    public static string DeltaStorage = "DeltaStorage";
    public static UpdateServerEntity instance;
    public static int MaxProcessors = 3;
    public static pooldelegate poolp;
    public static string Rustfolderroot = "Rust";
    public static WatsonTcpServer server;
    private static readonly long MaxFolderSize = 53687091200;
    public static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
    private static SendNet _sendNet = null;
    private static string certFile = string.Empty;
    private static string certPass = string.Empty;
    private static List<string> ClientFiles = new List<string>();
    
    #if DEBUG
    private static string serverIp = "127.0.0.1";
    #else
    private static string serverIp = "51.91.214.177";
    #endif

    private static int serverPort = 9090;

    private static bool useSsl = false;

    private static string BytesToString(long byteCount)
{
    string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
    if (byteCount == 0)
        return "0" + suf[0];
    long bytes = Math.Abs(byteCount);
    int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
    double num = Math.Round(bytes / Math.Pow(1024, place), 1);
    return Math.Sign(byteCount) * num + suf[place];
}

    public UpdateServerEntity()
    {
        if (File.Exists("Debug")) _debug = true;
        Console.WriteLine("Server Start...");
        SentrySdk.AddBreadcrumb("Server Start...");
        _sendNet = SendNetData;
        instance = this;
        FileHashes = Heart.FileHashes ?? new Dictionary<string, string>();
        foreach (string x in Directory.GetFiles(Rustfolderroot, "*", SearchOption.AllDirectories))
        {
            string trimmedpath = x.Replace(Path.GetDirectoryName(x), "");
            ClientFiles.Add(trimmedpath);
        }
        if(File.Exists("SingleDelta.json"))
            DeltaFileStorage = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string,string>>>(File.ReadAllText("SingleDelta.json"));
        Puts($"DeltaStorage has {DeltaFileStorage.Count} Files");
        Start();
        CheckUsers();
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
        lock (OccupantsLock)
        {
            Occupants.Remove(pr);
        }
        TickQueue();
        FinalizeZip(client._guid, zipFileName);
    }

    public static Task Extract(string path, UpdateClient currentUser)
    {
        SendProgress(currentUser._guid, "Extracting Zip..");
        Encoding enc = GetEncoding(path);
        string exepath = Directory.GetCurrentDirectory();
        string finalPath = exepath + "//" + currentUser.ClientFolder + "//Rust";
        if (Directory.Exists(currentUser.ClientFolder + "//Rust"))
            Directory.Delete(currentUser.ClientFolder + "//Rust", true);
        ZipFile.ExtractToDirectory(path, finalPath, enc);
        if (IsFileReady(path))
            File.Delete(path);
        SendProgress(currentUser._guid, "Enqueue for processing...");
        lock (WaitingClientsLock)
        {
            ClientProcessor newprocessor = new ClientProcessor(currentUser);
            WaitingClients.Enqueue(newprocessor);
        }
        TickQueue();
        return Task.CompletedTask;
    }

    public static void FinalizeZip(Guid id, string zip, bool stored = false)
    {
        UpdateClient client;
        lock (CurrentClientsLock)
        {
            if (!CurrentClients.TryGetValue(id, out client)) return;
        }

        if (stored)
        {
            FileLogger.LogInfo("  Sent Stored Deltas : " + BytesToString(new FileInfo(zip).Length) + " to: " + id);
            Puts("SendStoredDelta");
        }
        else
        {
            FileLogger.LogInfo("  UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length) + " to: " + id);
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

        foreach (string x in client.filetoDelete)
        {
            try { File.Delete(x); } catch { }
        }
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

    public static async Task quickhashes(Guid id)
    {
        try
        {
            var h = InputDictionary(FileHashes);

            _ = await server.SendAndWaitAsync(50000, id, "VERSION|" + Heart.Vversion);

            await Task.Delay(500); // Replace Thread.Sleep with Task.Delay

            _ = await server.SendAndWaitAsync(30000, id, "SOURCEHASHES|" + JsonConvert.SerializeObject(h));
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex, scope =>
            {
                StackTrace st = new StackTrace(ex, true);
                StackFrame frame = st.GetFrame(0);
                int line = frame.GetFileLineNumber();
                scope.SetExtra("Exception Line", line);
                scope.SetExtra("Exception Message", ex.Message);
                scope.SetExtra("Exception StackTrace", ex.StackTrace);

            });
            Puts($"Error in quickhashes: {ex.Message}");
        }
    }

    public static async Task SendProgress(Guid id, string msg)
    {
        var transaction = SentrySdk.StartTransaction("SendProgress", "task");
        try
        {
            var resp = await server.SendAndWaitAsync(5000, id, "REPORT|" + msg);
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex, scope =>
            {
                StackTrace st = new StackTrace(ex, true);
                StackFrame frame = st.GetFrame(0);
                int line = frame.GetFileLineNumber();
                scope.SetExtra("Exception Line", line);
                scope.SetExtra("Exception Message", ex.Message);
                scope.SetExtra("Exception StackTrace", ex.StackTrace);

            });
            transaction.Finish(SpanStatus.InternalError);
            throw;
        }
    }

    public static void TickQueue()
    {
        try
        {
            int waitingCount;
            int occupantsCount;
            lock (WaitingClientsLock) { waitingCount = WaitingClients.Count; }
            lock (OccupantsLock) { occupantsCount = Occupants.Count; }
            Puts($"Waitqueue Count: {waitingCount}");
            lock (DeltaFileStorageLock)
            {
                File.WriteAllText("SingleDelta.json", JsonConvert.SerializeObject(DeltaFileStorage, Formatting.Indented));
            }
            Puts($"Current Processors: {occupantsCount}");
            if (waitingCount > 0)
            {
                SentrySdk.AddBreadcrumb("Occupants More than 0 - ");
                if (occupantsCount < 3)
                {
                    Puts("Occupants less then 3. Processing commence ");
                    SentrySdk.CaptureMessage("Occupants less than 3, processing next client in queue.");
                    ClientProcessor nextone;
                    lock (WaitingClientsLock)
                    {
                        nextone = WaitingClients.Dequeue();
                    }
                    lock (OccupantsLock)
                    {
                        Occupants.Add(nextone);
                    }
                    nextone.StartupThisOne();
                }
                lock (WaitingClientsLock)
                {
                    foreach (ClientProcessor c in WaitingClients) c.Notify();
                }
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
        }
    }

    private static void CheckUsers()
    {
        int users = server.Connections;
        currCount = users;
        string[] AllDirs = Directory.GetDirectories("ClientFolders");
        foreach (string x in AllDirs)
        {
            try
            {
                string thisone = x.Split(Path.DirectorySeparatorChar).Last();
                if (thisone.Contains(".")) continue;
                Guid guid = Guid.Parse(thisone);
                if (!server.IsClientConnected(guid))
                {
                    lock (OccupantsLock)
                    {
                        var processorsToEnd = Occupants.Where(proc => proc._client != null && proc._client._guid == guid).ToList();
                        foreach (var proc in processorsToEnd)
                        {
                            try { proc.Dispose(); } catch (Exception ex) { SentrySdk.CaptureException(ex); }
                            Occupants.Remove(proc);
                        }
                    }
                    int maxRetries = 5;
                    int delayMs = 500;
                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try
                        {
                            if (Directory.Exists(x))
                            {
                                Directory.Delete(x, true);
                            }
                            break;
                        }
                        catch (IOException ioEx)
                        {
                            SentrySdk.CaptureException(ioEx);
                            DelayAsync(delayMs).Wait();
                        }
                        catch (UnauthorizedAccessException uaEx)
                        {
                            SentrySdk.CaptureException(uaEx);
                            DelayAsync(delayMs).Wait();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
            }
        }
    }

    private static async Task DelayAsync(int ms)
    {
        await Task.Delay(ms).ConfigureAwait(false);
    }

    private static void ClientConnected(object sender, ConnectionEventArgs e)
    {
        Puts("Client Connected");
        string fixedip = e.Client.Guid.ToString();
        string clientfolder = @"ClientFolders\" + fixedip;
        string fullpath = new DirectoryInfo(clientfolder).FullName;
        try
        {
            if (!Directory.Exists(fullpath)) Directory.CreateDirectory(fullpath);
            if (!Directory.Exists(clientfolder)) Directory.CreateDirectory(clientfolder);
            string Clientdeltazip = clientfolder + @"\" + fixedip + ".zip";
            lock (CurrentClientsLock)
            {
                CurrentClients.Add(e.Client.Guid, new UpdateClient(e.Client.Guid, e.Client.IpPort, clientfolder, Clientdeltazip));
                currCount++;
            }
            _ = Task.Run(async () => await quickhashes(e.Client.Guid));
            SentrySdk.CaptureMessage($"Client {e.Client.Guid} connected from {e.Client.IpPort} with folder {clientfolder} and {Clientdeltazip}");
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex, scope =>
            {
                StackTrace st = new StackTrace(ex, true);
                StackFrame frame = st.GetFrame(0);
                int line = frame.GetFileLineNumber();
                scope.SetExtra("Exception Line", line);
                scope.SetExtra("Exception Message", ex.Message);
                scope.SetExtra("Exception StackTrace", ex.StackTrace);

            });
        }
        TickQueue();
        CheckforCleanup();
        CheckUsers();
    }

    private static void ClientDisconnected(object sender, DisconnectionEventArgs args)
    {
        Puts("Client Disconnected");
        ClientProcessor result = null;
        lock (OccupantsLock)
        {
            result = Occupants.FirstOrDefault(x => x.tcpipport == args.Client.IpPort);
            if (result != null)
            {
                Occupants.Remove(result);
                Puts("ClientDisconnect recognised . Removed.");
            }
        }
        lock (CurrentClientsLock)
        {
            if (CurrentClients.ContainsKey(args.Client.Guid))
                CurrentClients.Remove(args.Client.Guid);
        }
        TickQueue();
    }

    private static void CheckforCleanup()
    {
        long cursize = DirSize(new DirectoryInfo(DeltaStorage));

        if (cursize > MaxFolderSize) DeleteOldFiles();
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
            int allitems = _client.dataToSend.Count();
            string zipFileName = _client.Clientdeltazip;
            string zipFileName2 =   _client.ClientFolder + "\\additionalfiles.zip";
            if (File.Exists(zipFileName)) File.Delete(zipFileName2);
            string clientRustFolder = _client.ClientFolder + "\\Rust\\";

            int itemcount = 0;
            SentrySdk.CaptureMessage("Additional Zip", scope =>
            {
                // Replace the problematic line with the following:
                SetExtrasFromList(scope, _client.dataToAdd);
                scope.AddBreadcrumb(zipFileName2);
            });


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

              
            
       
        }
    
    private static async Task SendZipFile(UpdateClient _client ,string zip, bool stored = false)
    {
        var transaction = SentrySdk.StartTransaction("SendZipFile", "task");
        try
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
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            transaction.Finish(SpanStatus.InternalError);
            throw;
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
        SentrySdk.AddBreadcrumb("New Syncrequest");
        string stringmsg = Encoding.UTF8.GetString(req.Data);
        UpdateClient _client = CurrentClients[req.Client.Guid];

        var Header = stringmsg.Split('|').First();
        var sourcemessage = stringmsg.Split('|').Last();




        if (Header =="MISS")
        {
            UpdateClient client = CurrentClients[req.Client.Guid];

            var adding = JsonConvert.DeserializeObject<Dictionary<string, string>>(sourcemessage).Keys.ToList();

                client.dataToAdd = adding;
            SentrySdk.AddBreadcrumb($"Received MISS request from client {req.Client.Guid} with {adding.Count} files to add.");
            Task.Run(() => PackAdditionalFiles(client));

            return new SyncResponse(req, "null");
        }

        if (Header == "DIFF")
        {

            _client.AddMissMatchFilelist(JsonConvert.DeserializeObject<Dictionary<string,string>>(sourcemessage));


            var sendb = JsonConvert.SerializeObject(_client.GetTrimmedList());
            SentrySdk.AddBreadcrumb($"Received DIFF request from client {_client._guid} with {sendb.Count()} files to send.");


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
    public static void SetExtrasFromList(Scope scope, List<string> dataToSend)
    {
        var extras = dataToSend.ToDictionary(item => item, item => (object?)null);
        scope.SetExtras(extras);
    }

public async Task SendNetData(Guid id, string zip, bool stored = false)
{
    SentrySdk.AddBreadcrumb($"SendNetData started for client {id}, zip: {zip}, stored: {stored}");
    var transaction = SentrySdk.StartTransaction("SendNetData", "task");
    try
    {
        Puts("SendNetData...");
        SentrySdk.AddBreadcrumb($"SendNetData called for client {id}, zip: {zip}, stored: {stored}");
        UpdateClient client;
        lock (CurrentClientsLock)
        {
            if (!CurrentClients.TryGetValue(id, out client)) return;
        }
        try
        {
            if (!stored)
            {
                var metadata = new Dictionary<string, string>();
                metadata.Add("1", "2");
                SentrySdk.AddBreadcrumb($"Sending zip file (not stored) to client {id} with metadata.");
                using (FileStream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
                {
                    _ = await server.SendAsync(id, source.Length, source, InputDictionary(metadata)).ConfigureAwait(false);
                }
            }
            else
            {
                SentrySdk.AddBreadcrumb($"Sending stored zip file to client {id}.");
                using (FileStream source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
                {
                    _ = await server.SendAsync(id, source.Length, source).ConfigureAwait(false);
                }
            }
            SentrySdk.AddBreadcrumb($"Deleting temporary files for client {id}.");
            foreach (string x in client.filetoDelete)
            {
                try { File.Delete(x); SentrySdk.AddBreadcrumb($"Deleted file: {x}"); } catch (Exception ex) { SentrySdk.CaptureMessage($"Failed to delete file {x}: {ex.Message}"); }
            }
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            transaction.Finish(SpanStatus.InternalError);
            Puts($"Exception in SendNetData: {ex.Message}");
            throw;
        }
    }
    catch (Exception ex)
    {
        SentrySdk.CaptureException(ex);
        transaction.Finish(SpanStatus.InternalError);
        throw;
    }
}
}