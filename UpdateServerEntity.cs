using Newtonsoft.Json;
using Sentry;
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
    #region Delegates
    public delegate void pooldelegate();
    public delegate Task SendMsg(string a, string b, bool c);
    public delegate Task SendNet(Guid a, string b, bool c = false);
    #endregion

    #region Constants and Configuration
    private static readonly long MaxFolderSize = 53687091200;
    public static int MaxProcessors = 3;
    public static string DeltaStorage = "DeltaStorage";
    public static string Rustfolderroot = "Rust";

#if DEBUG
    private static string serverIp = "127.0.0.1";
#else
    private static string serverIp = "0.0.0.0";
#endif
    private static int serverPort = 9090;
    private static bool useSsl = false;
    private static string certFile = string.Empty;
    private static string certPass = string.Empty;
    #endregion

    #region Static Fields and Properties
    public static bool _debug;
    public static ConsoleWriter consoleWriter = new();
    public static bool consolle = true;

    public static int currCount;
    public static UpdateServerEntity instance;
    public static pooldelegate poolp;
    public static WatsonTcpServer server;
    public static System.Collections.Concurrent.ConcurrentDictionary<string, string> FileHashes = new();
    public static System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> DeltaFileStorage = new();
    private static List<string> ClientFiles = new();
    // Use a concurrent dictionary for tracking threads per client. It is rarely used in the
    // current implementation, but concurrent access is safer when adding/removing entries.
    private static System.Collections.Concurrent.ConcurrentDictionary<string, Thread> threads = new();
    private static SendNet _sendNet;
    #endregion

    #region Thread-Safe Collections and Locks
    // Thread-safe collections
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, UpdateClient> CurrentClients = new();
    // Active processors keyed by their client Guid
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ClientProcessor> Occupants = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<ClientProcessor> WaitingClients = new();
    #endregion

    #region Constructor
    public UpdateServerEntity()
    {
        if (File.Exists("Debug")) _debug = true;
        Console.WriteLine("Server Start...");
        SentrySdk.AddBreadcrumb("Server Start...");

        _sendNet = SendNetData;
        instance = this;
        // Initialize file hashes from Heart. Use ConcurrentDictionary for thread safety.
        FileHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(Heart.FileHashes);

        foreach (string x in Directory.GetFiles(Rustfolderroot, "*", SearchOption.AllDirectories))
        {
            string trimmedpath = x.Replace(Path.GetDirectoryName(x), "");
            ClientFiles.Add(trimmedpath);
        }

        if (File.Exists("SingleDelta.json"))
        {
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(
                File.ReadAllText("SingleDelta.json"));
            if (loaded != null)
            {
                DeltaFileStorage = new System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>>(loaded);
            }
        }

        Puts($"DeltaStorage has {DeltaFileStorage.Count} Files");

        Start();
        CheckUsers();
    }


    #endregion

    #region Public Static Methods - Core Operations
    public static void EndCall(UpdateClient client, ClientProcessor pr, string zipFileName, bool abort = false)
    {
        // Remove processor from the active list
        if (pr != null && pr._client != null)
        {
            var clientId = pr._client.Guid;
            Occupants.TryRemove(clientId, out _);
        }
        TickQueue();
        FinalizeZip(client.Guid, zipFileName);
    }

    public static void FinalizeZip(Guid id, string zip, bool stored = false)
    {
        if (!CurrentClients.TryGetValue(id, out var client)) return;
        if (stored)
            Puts("SendStoredDelta");
        else
            Puts("Created New Update");

        if (client.SignatureHash != string.Empty)
        {
            var allfiles = new DirectoryInfo(DeltaStorage).GetFiles();
            if (!allfiles.Any(x => x.Name == client.SignatureHash + ".zip"))
                File.Copy(zip, DeltaStorage + "\\" + client.SignatureHash + ".zip");
        }

        foreach (string x in client.GetFilesToDeleteSnapshot())
            try
            {
                File.Delete(x);
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
#if DEBUG
                throw;
#endif
            }
    }

    public static void TickQueue()
    {
        try
        {
            int waitingCount;
            int occupantsCount;
            // Removed lock (WaitingClientsLock)
            waitingCount = WaitingClients.Count;
            // Removed lock (OccupantsLock)
            occupantsCount = Occupants.Count;

            Puts($"Waitqueue Count: {waitingCount}");
            Dictionary<string, Dictionary<string, string>> deltaCopy;
            // Removed lock (DeltaFileStorageLock)
            deltaCopy = new Dictionary<string, Dictionary<string, string>>(DeltaFileStorage);
            try
            {
                File.WriteAllText("SingleDelta.json", JsonConvert.SerializeObject(deltaCopy, Formatting.Indented));
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                Puts($"Error writing SingleDelta.json: {ex.Message}");
#if DEBUG
                throw;
#endif
            }

            Puts($"Current Processors: {occupantsCount}");
            if (waitingCount > 0)
            {
                SentrySdk.AddBreadcrumb("Occupants More than 0 - ");
                if (occupantsCount < MaxProcessors)
                {
                    Puts("Occupants less then MaxProcessors. Processing commence ");
                    SentrySdk.CaptureMessage("Occupants less than MaxProcessors, processing next client in queue.");
                    if (WaitingClients.TryDequeue(out var nextone))
                    {
                        // Add the processor keyed by its client Guid
                        var clientId = nextone._client?.Guid ?? Guid.Empty;
                        if (clientId != Guid.Empty)
                        {
                            Occupants.TryAdd(clientId, nextone);
                        }
                        nextone.StartupThisOne();
                    }
                }
                // Removed lock (WaitingClientsLock)
                foreach (ClientProcessor c in WaitingClients) c.Notify();
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
#if DEBUG
            throw;
#endif
        }
    }

    public static async Task quickhashes(Guid id)
    {
        try
        {
            var h = InputDictionary(Heart.FileHashes);

            _ = await server.SendAndWaitAsync(50000, id, "VERSION|" + Heart.Vversion);

            await Task.Delay(500); // Replace Thread.Sleep with Task.Delay

            _ = await server.SendAndWaitAsync(30000, id, "SOURCEHASHES|" + JsonConvert.SerializeObject(h));
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            Puts($"Error in quickhashes: {ex.Message}");
#if DEBUG
            throw;
#endif
        }
    }

    public static async Task SendProgress(Guid id, string msg)
    {
        try
        {
            if (!server.IsClientConnected(id)) return;

            _ = await server.SendAndWaitAsync(5000,id, $"REPORT|{msg}", null, 4096, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
#if DEBUG
            throw;
#endif
        }
    }

    public static Task Extract(string path, UpdateClient currentUser)
    {
        // Notify client of extraction start
        _ = SendProgress(currentUser.Guid, "Extracting Zip..");
        Encoding enc = GetEncoding(path);
        string exepath = Directory.GetCurrentDirectory();
        string finalPath = Path.Combine(exepath, currentUser.ClientFolder, "Rust");
        string rustFolder = Path.Combine(currentUser.ClientFolder, "Rust");
        try
        {
            if (Directory.Exists(rustFolder))
                Directory.Delete(rustFolder, true);
            ZipFile.ExtractToDirectory(path, finalPath, enc);
            if (IsFileReady(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            Puts($"Error extracting zip: {ex.Message}");
#if DEBUG
            throw;
#endif
        }

        // Notify client of queue enqueue
        _ = SendProgress(currentUser.Guid, "Enqueue for processing...");
        // Removed lock (WaitingClientsLock)
        ClientProcessor newprocessor = new(currentUser);
        WaitingClients.Enqueue(newprocessor);
        TickQueue();
        return Task.CompletedTask;
    }

    public static void SetExtrasFromList(Scope scope, List<string> dataToSend)
    {
        var extras = dataToSend.ToDictionary(item => item, item => (object)null);
        scope.SetExtras(extras);
    }
    #endregion

    #region Public Instance Methods
    public async Task SendNetData(Guid id, string zip, bool stored = false)
    {
        SentrySdk.AddBreadcrumb($"SendNetData started for client {id}, zip: {zip}, stored: {stored}");

        try
        {
            Puts("SendNetData...");
            SentrySdk.AddBreadcrumb($"SendNetData called for client {id}, zip: {zip}, stored: {stored}");
            if (!CurrentClients.TryGetValue(id, out var client)) return;

            try
            {
                using (FileStream source = new(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                           FileOptions.Asynchronous))
                {
                    if (!stored)
                    {
                        var metadata = new Dictionary<string, string> { { "1", "2" } };
                        SentrySdk.AddBreadcrumb($"Sending zip file (not stored) to client {id} with metadata.");
                        await server.SendAsync(id, source.Length, source, InputDictionary(metadata))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        SentrySdk.AddBreadcrumb($"Sending stored zip file to client {id}.");
                        await server.SendAsync(id, source.Length, source).ConfigureAwait(false);
                    }
                }

                SentrySdk.AddBreadcrumb($"Deleting temporary files for client {id}.");
                foreach (string x in client.GetFilesToDeleteSnapshot())
                    try
                    {
                        File.Delete(x);
                        SentrySdk.AddBreadcrumb($"Deleted file: {x}");
                    }
                    catch (Exception ex)
                    {
                        SentrySdk.CaptureMessage($"Failed to delete file {x}: {ex.Message}");
#if DEBUG
                        throw;
#endif
                    }

                SentrySdk.AddBreadcrumb($"Finished SendNetData for client {id}.", "performance.operation.finish");
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                SentrySdk.AddBreadcrumb($"Error in SendNetData for client {id}: {ex.Message}", "performance.operation.error");
                Puts($"Exception in SendNetData: {ex.Message}");
#if DEBUG
                throw;
#endif
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            SentrySdk.AddBreadcrumb($"Error in SendNetData for client {id}: {ex.Message}", "performance.operation.error");
#if DEBUG
            throw;
#endif
            throw;
        }
    }
    #endregion

    #region Utility Methods
    public static void Puts(string msg)
    {
        if (_debug)
            Console.WriteLine(msg);
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

    public static Encoding GetEncoding(string filename)
    {
        // Read the BOM
        byte[] bom = new byte[4];
        using (FileStream file = new(filename, FileMode.Open, FileAccess.Read))
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

    private static Dictionary<string, object> InputDictionary(IDictionary<string, string> data)
    {
        var ret = new Dictionary<string, object>();
        if (data == null) return ret;
        foreach (var kvp in data)
        {
            ret[kvp.Key] = kvp.Value;
        }
        return ret;
    }

    private static async Task DelayAsync(int ms)
    {
        await Task.Delay(ms).ConfigureAwait(false);
    }
    #endregion

    #region Server Initialization and Management
    private static void Start()
    {
        server = new WatsonTcpServer(serverIp, serverPort);
        WatsonTcpKeepaliveSettings ht = new();
        ht.EnableTcpKeepAlives = true;
        server.Events.ClientConnected += ClientConnected;
        server.Events.ClientDisconnected += ClientDisconnected;
        server.Events.StreamReceived += StreamReceived;
        server.Callbacks.SyncRequestReceivedAsync = SyncRequestReceived;
        server.Keepalive = ht;

        server.Start();
        Console.WriteLine("Server Started.");
    }
    #endregion

    #region File Operations
    private static Task PackAdditionalFiles(UpdateClient client)
    {
        return CreateZipFile(client, true);
    }

    private static async Task CreateZipFile(UpdateClient _client, bool additionalfiles = false)
    {
        string zipFileName2 = Path.Combine(_client.ClientFolder, "additionalfiles.zip");
        try
        {
            if (File.Exists(zipFileName2)) File.Delete(zipFileName2);
            SentrySdk.CaptureMessage("Additional Zip", scope =>
            {
                SetExtrasFromList(scope, _client.GetDataToAddSnapshot());
                scope.AddBreadcrumb(zipFileName2);
            });
            using (ZipArchive zip = ZipFile.Open(zipFileName2, ZipArchiveMode.Create))
            {
                foreach (string y in _client.GetDataToAddSnapshot())
                {
                    string realfilepath = Path.Combine("Rust", y);
                    if (!File.Exists(realfilepath)) continue;
                    string relativePath = y.Replace("Rust\\", string.Empty);
                    string fixedname = relativePath.Replace('\\', '/');
                    zip.CreateEntryFromFile(realfilepath, fixedname, CompressionLevel.Optimal);
                }
            }

            _client.AddFileToDelete(zipFileName2);
            await SendZipFile(_client, zipFileName2, true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            Puts($"Error in CreateZipFile: {ex.Message}");
#if DEBUG
            throw;
#endif
        }
    }

    private static async Task SendZipFile(UpdateClient _client, string zip, bool stored = false)
    {

        try
        {
            using (FileStream source = new(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                       FileOptions.Asynchronous))
            {
                if (stored)
                {
                    await server.SendAsync(_client.Guid, source.Length, source).ConfigureAwait(false);
                }
                else
                {
                    var metadata = new Dictionary<string, object> { { "1", "2" } };
                    await server.SendAsync(_client.Guid, source.Length, source, metadata).ConfigureAwait(false);
                }
            }

            SentrySdk.AddBreadcrumb($"Finished SendZipFile for client {_client.Guid}.", "performance.operation.finish");
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            SentrySdk.AddBreadcrumb($"Error in SendZipFile for client {_client.Guid}: {ex.Message}", "performance.operation.error");
            Puts($"Error in SendZipFile: {ex.Message}");
#if DEBUG
            throw;
#endif
        }
    }
    #endregion

    #region Event Handlers
    private static void StreamReceived(object sender, StreamReceivedEventArgs args)
    {
        Puts("Stream received");
        try
        {
            var Meta = args.Metadata;
            if (!CurrentClients.TryGetValue(args.Client.Guid, out var user)) return;

            string userFolder = user.ClientFolder;
            string zippath = Path.Combine(userFolder, "signatures.zip");
            int bufferSize = 65536;
            byte[] buffer = new byte[bufferSize];
            long bytesRemaining = args.ContentLength;
            using (FileStream file = new(zippath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (bytesRemaining > 0)
                {
                    int bytesRead = args.DataStream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                    {
                        throw new EndOfStreamException(
                            $"Client {args.Client.Guid} disconnected before sending the full signature archive.");
                    }
                    if (bytesRead > 0) file.Write(buffer, 0, bytesRead);
                    bytesRemaining -= bytesRead;
                }

                file.Flush();
            }
            Extract(zippath, user);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            Puts($"Error in StreamReceived : {e.Message}");
#if DEBUG
            throw;
#endif
        }
    }

    private static async Task<SyncResponse> SyncRequestReceived(SyncRequest req)
    {
        Puts("syncrequest");
        SentrySdk.AddBreadcrumb("New Syncrequest");
        string stringmsg = Encoding.UTF8.GetString(req.Data);
        UpdateClient _client;
        if (!CurrentClients.TryGetValue(req.Client.Guid, out _client))
        {
            return new SyncResponse(req, "null");
        }

        var Header = stringmsg.Split('|').First();
        var sourcemessage = stringmsg.Split('|').Last();
        if (Header == "MISS")
        {
            if (!CurrentClients.TryGetValue(req.Client.Guid, out var client))
            {
                return new SyncResponse(req, "null");
            }

            var adding = JsonConvert.DeserializeObject<Dictionary<string, string>>(sourcemessage).Keys.ToList();
            client.SetDataToAdd(adding);
            SentrySdk.AddBreadcrumb($"Received MISS request from client {req.Client.Guid} with {adding.Count} files to add.");
            _ = Task.Run(() => PackAdditionalFiles(client));
            return new SyncResponse(req, "null");
        }
        if (Header == "DIFF")
        {
            _client.AddMissMatchFilelist(JsonConvert.DeserializeObject<Dictionary<string, string>>(sourcemessage));
            var sendb = JsonConvert.SerializeObject(_client.GetTrimmedList());
            SentrySdk.AddBreadcrumb($"Received DIFF request from client {_client.Guid} with {sendb.Length} bytes to send.");
            if (_client.GetMatchedDeltasCount() >= _client.GetMismatchedFileHashesCount())
            {
                await SendProgress(_client.Guid, "Enqueue for processing...").ConfigureAwait(false);
                var newprocessor = new ClientProcessor(_client);

                WaitingClients.Enqueue(newprocessor);

                TickQueue();
            }
            return new SyncResponse(req, _client.MatchedDeltas.Count.ToString() + "|" + sendb);
        }
        return new SyncResponse(req, "null");
    }

    private static void ClientConnected(object sender, ConnectionEventArgs e)
    {
        Puts("Client Connected");
        string fixedip = e.Client.Guid.ToString();
        string clientfolder = Path.Combine("ClientFolders", fixedip);
        string fullpath = new DirectoryInfo(clientfolder).FullName;
        try
        {
            if (!Directory.Exists(fullpath)) Directory.CreateDirectory(fullpath);
            if (!Directory.Exists(clientfolder)) Directory.CreateDirectory(clientfolder);
            string clientDeltaZip = Path.Combine(clientfolder, $"{fixedip}.zip");
            var upc = new UpdateClient(e.Client.Guid, e.Client.IpPort, clientfolder, clientDeltaZip);
            CurrentClients.TryAdd(e.Client.Guid, upc);
            currCount++;
            _ = quickhashes(e.Client.Guid);
            SentrySdk.CaptureMessage(
                $"Client {e.Client.Guid} connected from {e.Client.IpPort} with folder {clientfolder} and {clientDeltaZip}");
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
#if DEBUG
            throw;
#endif
        }
        TickQueue();
        CheckforCleanup();
        CheckUsers();
    }

    private static void ClientDisconnected(object sender, DisconnectionEventArgs args)
    {
        Puts("Client Disconnected");
        // Find the processor matching this client IP
        if (Occupants.TryRemove(args.Client.Guid, out var processor) && processor?._client != null)
        {
            try
            {
                processor.Dispose();
                Puts("ClientDisconnect recognised. Removed.");
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
            }
        }
        // Remove client from current clients if present
        CurrentClients.TryRemove(args.Client.Guid, out _);
        TickQueue();
    }
    #endregion

    #region Cleanup and Maintenance
    private static void CheckUsers()
    {
        int users = server.Connections;
        currCount = users;
        string[] AllDirs = Directory.GetDirectories("ClientFolders");
        foreach (string x in AllDirs)
            try
            {
                string thisone = x.Split(Path.DirectorySeparatorChar).Last();
                if (thisone.Contains(".")) continue;
                Guid guid = Guid.Parse(thisone);
                if (!server.IsClientConnected(guid))
                {
                    // Dispose and remove processors associated with this disconnected client
                    var processorsToEnd = Occupants.Values
                        .Where(proc => proc._client != null && proc._client.Guid == guid)
                        .ToList();
                    foreach (var proc in processorsToEnd)
                    {
                        try
                        {
                            proc.Dispose();
                        }
                        catch (Exception disposeEx)
                        {
                            SentrySdk.CaptureException(disposeEx);
                        }
                        // Remove any thread handle tracked for this client
                        if (threads.TryRemove(guid.ToString(), out var _))
                        {
                            // No explicit abort or interrupt; rely on cancellation tokens.
                        }
                        // Remove processor from occupants
                        if (proc._client != null)
                        {
                            Occupants.TryRemove(proc._client.Guid, out _);
                        }
                    }

                    if (processorsToEnd.Any(proc => !proc.IsProcessingComplete))
                    {
                        continue;
                    }

                    TryDeleteClientDirectory(x, guid);
                    // Remove from current clients dictionary
                    CurrentClients.TryRemove(guid, out _);
                }
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
#if DEBUG
                throw;
#endif
            }
    }

    private static void TryDeleteClientDirectory(string path, Guid guid)
    {
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, true);
                return;
            }
            catch (IOException ioEx) when (IsSharingViolation(ioEx))
            {
                SentrySdk.AddBreadcrumb(
                    $"Skipping cleanup for busy client folder {path} on attempt {attempt}.",
                    "cleanup",
                    guid.ToString());
                Thread.Sleep(150 * attempt);
            }
            catch (UnauthorizedAccessException uaEx)
            {
                SentrySdk.CaptureException(uaEx, s =>
                {
                    s.SetExtra("ClientGuid", guid.ToString());
                    s.SetExtra("ClientFolder", path);
                });
                return;
            }
            catch (IOException ioEx)
            {
                SentrySdk.CaptureException(ioEx, s =>
                {
                    s.SetExtra("ClientGuid", guid.ToString());
                    s.SetExtra("ClientFolder", path);
                });
                return;
            }
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int errorCode = ex.HResult & 0xFFFF;
        return errorCode == 32 || errorCode == 33;
    }

    private static void CheckforCleanup()
    {
        long cursize = DirSize(new DirectoryInfo(DeltaStorage));
        if (cursize > MaxFolderSize) DeleteOldFiles();
    }

    private static void DeleteOldFiles()
    {
        {
            // Delete oldest files in the DeltaStorage directory until the folder size is below
            // the maximum configured size. Errors during deletion are logged but do not
            // propagate, ensuring cleanup continues.
            try
            {
                var directory = new DirectoryInfo(DeltaStorage);
                if (!directory.Exists)
                {
                    return;
                }
                // Get all files ordered by last write time ascending (oldest first)
                var files = directory.GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
                long totalSize = DirSize(directory);
                foreach (var file in files)
                {
                    if (totalSize <= MaxFolderSize)
                    {
                        break;
                    }
                    try
                    {
                        long fileSize = file.Length;
                        file.Delete();
                        totalSize -= fileSize;
                    }
                    catch (Exception ex)
                    {
                        // Log but continue attempting to delete other files
                        SentrySdk.CaptureException(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log and silently ignore unexpected exceptions in cleanup
                SentrySdk.CaptureException(ex);
            }
        }
    }
    #endregion
}
