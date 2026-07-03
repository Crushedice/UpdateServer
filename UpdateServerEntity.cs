using Newtonsoft.Json;
using Sentry;
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
    #region Constants and Configuration
    private static readonly long MaxFolderSize = 53687091200;
    public static int MaxProcessors = 3;
    public static string DeltaStorage = "DeltaStorage";
    public static string Rustfolderroot = "Rust";
    private const string ClientFoldersRoot = "ClientFolders";

#if DEBUG
    private static string serverIp = "127.0.0.1";
#else
    private static string serverIp = "0.0.0.0";
#endif
    private static int serverPort = 9090;
    #endregion

    #region Static Fields and Properties
    public static bool _debug;
    public static WatsonTcpServer server;
    public static System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> DeltaFileStorage = new();
    #endregion

    #region Thread-Safe Collections and Locks
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, UpdateClient> CurrentClients = new();
    // Active processors keyed by their client Guid
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ClientProcessor> Occupants = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<ClientProcessor> WaitingClients = new();
    // Serializes the check-then-act between Occupants.Count and MaxProcessors,
    // which ConcurrentDictionary alone cannot make atomic.
    private static readonly object QueueLock = new();
    // Serializes writes to SingleDelta.json.
    private static readonly object DeltaPersistLock = new();
    #endregion

    #region Constructor
    public UpdateServerEntity()
    {
        if (File.Exists("Debug")) _debug = true;
        Console.WriteLine("Server Start...");
        SentrySdk.AddBreadcrumb("Server Start...");

        Directory.CreateDirectory(DeltaStorage);
        Directory.CreateDirectory(ClientFoldersRoot);

        if (File.Exists("SingleDelta.json"))
        {
            try
            {
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(
                    File.ReadAllText("SingleDelta.json"));
                if (loaded != null)
                {
                    DeltaFileStorage = new System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>>(loaded);
                }
            }
            catch (Exception ex)
            {
                // A corrupt index only costs cached deltas; it must not stop the server.
                SentrySdk.CaptureException(ex);
                Puts($"Could not load SingleDelta.json, starting with empty delta index: {ex.Message}");
            }
        }

        Puts($"DeltaStorage has {DeltaFileStorage.Count} Files");

        Start();
        CheckUsers();
    }
    #endregion

    #region Public Static Methods - Core Operations
    public static void EndCall(UpdateClient client, ClientProcessor pr, string zipFileName)
    {
        ReleaseProcessor(pr);
        FinalizeZip(client.Guid, zipFileName);
    }

    /// <summary>
    /// Removes the processor from the active slots and lets the queue advance.
    /// Safe to call multiple times for the same processor.
    /// </summary>
    public static void ReleaseProcessor(ClientProcessor processor)
    {
        var clientId = processor?._client?.Guid ?? Guid.Empty;
        if (clientId != Guid.Empty)
            Occupants.TryRemove(clientId, out _);
        TickQueue();
    }

    public static void FinalizeZip(Guid id, string zip, bool stored = false)
    {
        if (!CurrentClients.TryGetValue(id, out var client)) return;
        if (stored)
            Puts("SendStoredDelta");
        else
            Puts("Created New Update");

        if (!string.IsNullOrEmpty(client.SignatureHash) && File.Exists(zip))
        {
            string storedZip = Path.Combine(DeltaStorage, client.SignatureHash + ".zip");
            if (!File.Exists(storedZip))
            {
                // Temp file + rename so a concurrent reader never sees a partial zip.
                string tempPath = storedZip + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(zip, tempPath);
                    File.Move(tempPath, storedZip);
                }
                catch (IOException) when (File.Exists(storedZip))
                {
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                }
                finally
                {
                    if (File.Exists(tempPath))
                        try { File.Delete(tempPath); } catch (IOException) { }
                }
            }
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
            PersistDeltaStorage();

            Puts($"Waitqueue Count: {WaitingClients.Count}");
            Puts($"Current Processors: {Occupants.Count}");

            lock (QueueLock)
            {
                while (Occupants.Count < MaxProcessors && WaitingClients.TryDequeue(out var nextone))
                {
                    var clientId = nextone._client?.Guid ?? Guid.Empty;
                    if (clientId == Guid.Empty) continue;

                    // Don't burn a processing slot on a client that already left the queue.
                    if (server != null && !server.IsClientConnected(clientId))
                    {
                        try { nextone.Dispose(); } catch (Exception ex) { SentrySdk.CaptureException(ex); }
                        continue;
                    }

                    Occupants.TryAdd(clientId, nextone);
                    Puts("Occupants less then MaxProcessors. Processing commence ");
                    nextone.StartupThisOne();
                }
            }

            foreach (ClientProcessor c in WaitingClients) c.Notify();
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
#if DEBUG
            throw;
#endif
        }
    }

    private static void PersistDeltaStorage()
    {
        try
        {
            lock (DeltaPersistLock)
            {
                var deltaCopy = new Dictionary<string, Dictionary<string, string>>(DeltaFileStorage);
                File.WriteAllText("SingleDelta.json", JsonConvert.SerializeObject(deltaCopy, Formatting.Indented));
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
            Puts($"Error writing SingleDelta.json: {ex.Message}");
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

            _ = await server.SendAndWaitAsync(50000, id, "VERSION|" + Heart.Vversion).ConfigureAwait(false);

            await Task.Delay(500).ConfigureAwait(false);

            _ = await server.SendAndWaitAsync(30000, id, "SOURCEHASHES|" + JsonConvert.SerializeObject(h)).ConfigureAwait(false);
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
        SyncResponse resp = null;
        try
        {
            resp = await server.SendAndWaitAsync(5000, id, $"REPORT|{msg}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex, s => { s.SetExtras(resp?.Metadata); });
        }
    }

    public static void SetExtrasFromList(Scope scope, List<string> dataToSend)
    {
        var extras = dataToSend.ToDictionary(item => item, item => (object)null);
        scope.SetExtras(extras);
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
            using (FileStream inputStream = File.Open(filename, FileMode.Open,
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
        server.Events.ExceptionEncountered += (s, e) =>
        {
            SentrySdk.CaptureException(e.Exception);
            Puts($"WatsonTcp exception: {e.Exception.Message}");
        };
        server.Callbacks.SyncRequestReceivedAsync = SyncRequestReceived;
        server.Keepalive = ht;

        server.Start();
        Console.WriteLine("Server Started.");
    }
    #endregion

    #region File Operations
    private static Task PackAdditionalFiles(UpdateClient client)
    {
        return CreateZipFile(client);
    }

    private static async Task CreateZipFile(UpdateClient _client)
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
            // Do not send a partially written zip.
            if (File.Exists(zipFileName2))
                try { File.Delete(zipFileName2); } catch (IOException) { }
#if DEBUG
            throw;
#endif
        }
    }

    private static async Task SendZipFile(UpdateClient _client, string zip, bool stored = false)
    {
        try
        {
            using (FileStream source = new(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
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

    private static void Extract(string path, UpdateClient currentUser)
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
            SentrySdk.CaptureException(ex, s => s.SetExtra("ClientGuid", currentUser.Guid.ToString()));
            Puts($"Error extracting zip: {ex.Message}");
            _ = SendProgress(currentUser.Guid, "Server could not read the signature archive. Please reconnect to retry.");
#if DEBUG
            throw;
#endif
            // An incomplete signature set must never be processed into an update.
            return;
        }

        // Notify client of queue enqueue
        _ = SendProgress(currentUser.Guid, "Enqueue for processing...");
        ClientProcessor newprocessor = new(currentUser);
        WaitingClients.Enqueue(newprocessor);
        TickQueue();
    }
    #endregion

    #region Event Handlers
    private static void StreamReceived(object sender, StreamReceivedEventArgs args)
    {
        Puts("Stream received");
        string zippath = null;
        try
        {
            if (!CurrentClients.TryGetValue(args.Client.Guid, out var user)) return;

            string userFolder = user.ClientFolder;
            zippath = Path.Combine(userFolder, "signatures.zip");
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
                    file.Write(buffer, 0, bytesRead);
                    bytesRemaining -= bytesRead;
                }

                file.Flush();
            }
            Extract(zippath, user);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e, s => s.SetExtra("ClientGuid", args.Client.Guid.ToString()));
            Puts($"Error in StreamReceived : {e.Message}");
            // Remove a partially written signature archive so it cannot be picked up later.
            if (zippath != null && File.Exists(zippath))
                try { File.Delete(zippath); } catch (IOException) { }
#if DEBUG
            throw;
#endif
        }
    }

    private static async Task<SyncResponse> SyncRequestReceived(SyncRequest req)
    {
        try
        {
            Puts("syncrequest");
            SentrySdk.AddBreadcrumb("New Syncrequest");
            string stringmsg = Encoding.UTF8.GetString(req.Data);
            if (!CurrentClients.TryGetValue(req.Client.Guid, out var _client))
            {
                return new SyncResponse(req, "null");
            }

            // Split on the first separator only: the payload is JSON and may itself contain '|'.
            int sep = stringmsg.IndexOf('|');
            if (sep < 0)
            {
                return new SyncResponse(req, "null");
            }
            var Header = stringmsg.Substring(0, sep);
            var sourcemessage = stringmsg.Substring(sep + 1);

            if (Header == "MISS")
            {
                var missing = JsonConvert.DeserializeObject<Dictionary<string, string>>(sourcemessage);
                var adding = missing?.Keys.ToList() ?? new List<string>();
                _client.SetDataToAdd(adding);
                SentrySdk.AddBreadcrumb($"Received MISS request from client {req.Client.Guid} with {adding.Count} files to add.");
                _ = Task.Run(() => PackAdditionalFiles(_client));
                return new SyncResponse(req, "null");
            }
            if (Header == "DIFF")
            {
                var mismatched = JsonConvert.DeserializeObject<Dictionary<string, string>>(sourcemessage);
                if (mismatched == null)
                {
                    return new SyncResponse(req, "null");
                }
                _client.AddMissMatchFilelist(mismatched);
                var sendb = JsonConvert.SerializeObject(_client.GetTrimmedList());
                SentrySdk.AddBreadcrumb($"Received DIFF request from client {_client.Guid} with {sendb.Length} bytes to send.");
                if (_client.GetMatchedDeltasCount() >= _client.GetMismatchedFileHashesCount())
                {
                    await SendProgress(_client.Guid, "Enqueue for processing...").ConfigureAwait(false);
                    var newprocessor = new ClientProcessor(_client);

                    WaitingClients.Enqueue(newprocessor);

                    TickQueue();
                }
                return new SyncResponse(req, _client.GetMatchedDeltasCount().ToString() + "|" + sendb);
            }
            return new SyncResponse(req, "null");
        }
        catch (Exception ex)
        {
            // A malformed request from one client must never take down the callback
            // pipeline for everyone else.
            SentrySdk.CaptureException(ex, s => s.SetExtra("ClientGuid", req.Client.Guid.ToString()));
            Puts($"Error in SyncRequestReceived: {ex.Message}");
            return new SyncResponse(req, "null");
        }
    }

    private static void ClientConnected(object sender, ConnectionEventArgs e)
    {
        Puts("Client Connected");
        string fixedip = e.Client.Guid.ToString();
        string clientfolder = Path.Combine(ClientFoldersRoot, fixedip);
        try
        {
            Directory.CreateDirectory(clientfolder);
            string clientDeltaZip = Path.Combine(clientfolder, $"{fixedip}.zip");
            var upc = new UpdateClient(e.Client.Guid, e.Client.IpPort, clientfolder, clientDeltaZip);
            CurrentClients.TryAdd(e.Client.Guid, upc);
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
        if (Occupants.TryRemove(args.Client.Guid, out var processor) && processor != null)
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
        if (!Directory.Exists(ClientFoldersRoot)) return;

        string[] AllDirs = Directory.GetDirectories(ClientFoldersRoot);
        foreach (string x in AllDirs)
            try
            {
                string thisone = x.Split(Path.DirectorySeparatorChar).Last();
                if (!Guid.TryParse(thisone, out Guid guid)) continue;
                if (!server.IsClientConnected(guid))
                {
                    // Dispose and remove processors associated with this disconnected client
                    if (Occupants.TryRemove(guid, out var proc))
                    {
                        try
                        {
                            proc.Dispose();
                        }
                        catch (Exception disposeEx)
                        {
                            SentrySdk.CaptureException(disposeEx);
                        }

                        if (!proc.IsProcessingComplete)
                        {
                            // Still winding down; try deleting the folder on a later pass.
                            continue;
                        }
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
        try
        {
            var storage = new DirectoryInfo(DeltaStorage);
            if (!storage.Exists) return;
            long cursize = DirSize(storage);
            if (cursize > MaxFolderSize) DeleteOldFiles();
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex);
        }
    }

    private static void DeleteOldFiles()
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
    #endregion
}
