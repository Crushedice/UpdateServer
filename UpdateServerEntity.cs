using FastRsync.Core;
using FastRsync.Delta;
using FastRsync.Diagnostics;
using FastRsync.Signature;
using Newtonsoft.Json;
using StringHelp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using UpdateServer;
using WatsonTcp;



public class UpdateServerEntity
{
    private static string serverIp = "0.0.0.0";
    private static int serverPort = 9000;
    private static bool useSsl = false;
   // public static List<Task> tasks = new List<Task>();
    private static WatsonTcpServer server = null;
    private static string certFile = string.Empty;
    private static string certPass = string.Empty;
    private static string Rustfolderroot = "Rust";
    private static readonly long MaxFolderSize = 53687091200;
    private static string DeltaStorage = "DeltaStorage";
    private static List<string> StoredDeltas = new List<string>();
    private static bool acceptInvalidCerts = true;
    private static bool mutualAuthentication = true;
    public static bool consolle = true;
    public static Dictionary<string, UpdateClient> CurrentClients = new Dictionary<string, UpdateClient>();
    public static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
    public static Dictionary<string, string> UpdaterGrid = new Dictionary<string, string>();
    public static int currCount = 0;
    public static UpdateServerEntity instance = null;
    public static int deltaprogress = 0;
    public static MakeDelta mkdelta;
    public delegate void pooldelegate();
    //public static List<CancellationTokenSource> tokenlist = new List<CancellationTokenSource>();
    public static ConsoleWriter consoleWriter = new ConsoleWriter();
    //   public static string _Zippath = "//" + "signatures" + args.IpPort.Split(':')[0].Replace('.', '0') + ".zip";

    public static pooldelegate poolp;
    public UpdateServerEntity()
    {
     
        Console.WriteLine("Server Start...");
        mkdelta = CreateDeltaforClient;
        CheckDeltas();
        instance = this;
        FileHashes = Heart.FileHashes;
        poolp = PoolProgress;
        Start();
    }

    private static void Start()
    {
        if (!useSsl)
        {
            server = new WatsonTcpServer(serverIp, serverPort);
        }
        else
        {
            certFile = InputString("Certificate file:", "test.pfx", false);
            certPass = InputString("Certificate password:", "password", false);
            acceptInvalidCerts = InputBoolean("Accept Invalid Certs:", true);
            mutualAuthentication = InputBoolean("Mutually authenticate:", true);

            server = new WatsonTcpServer(serverIp, serverPort, certFile, certPass);
            server.AcceptInvalidCertificates = acceptInvalidCerts;
            server.MutuallyAuthenticate = mutualAuthentication;
            server.Logger = s => Console.WriteLine(s);
            server.DebugMessages = false;

        }


        server.ClientConnected += ClientConnected;
        server.ClientDisconnected += ClientDisconnected;
        server.StreamReceived += StreamReceived;
        server.SyncRequestReceived = SyncRequestReceived;
        server.IdleClientTimeoutSeconds = 600;
        


        // using lambda expression..could use method like other answers on here



        // server.MessageReceived = MessageReceived;

        server.StartAsync();

        Console.WriteLine("Command [? for help]: ");

        Console.WriteLine("Available commands:");
        Console.WriteLine("  ?          help (this menu)");
        Console.WriteLine("  q          quit");
        Console.WriteLine("  cls        clear screen");
        Console.WriteLine("  list       list clients");
        Console.WriteLine("  send       send message to a client");
        Console.WriteLine("  sendasync  send message to a client asynchronously");
        Console.WriteLine("  remove     disconnect client");
        Console.WriteLine("  psk        set preshared key");

    }
    #region MainMethods
   

    private static async void ClientConnected(object sender, ClientConnectedEventArgs args)
    {

        string msg = string.Empty;
        var fixedip = args.IpPort.Replace(':', '-');
        // string clientfolder = @"ClientFolders\" + args.IpPort.Split(':')[0].Replace('.', '0');
        string clientfolder = @"ClientFolders\" + fixedip;
        var fullpath = new DirectoryInfo(clientfolder).FullName;

        if (!Directory.Exists(fullpath))
            Directory.CreateDirectory(clientfolder);

        CurrentClients.Add(args.IpPort, new UpdateClient(args.IpPort, clientfolder));
        CurrentClients[args.IpPort].Clientdeltazip = clientfolder + "\\Deltas" + args.IpPort.Replace(':', '-') + ".zip";
        currCount++;

        //     SendMessage("V|" + Heart.Vversion + "|", args.IpPort, true);
        SendMessage(Heart.Vversion, args.IpPort, true);

        CheckforCleanup();
    }

    private static void CheckforCleanup()
    {

        var cursize = DirSize(new DirectoryInfo(DeltaStorage));

        if(cursize>MaxFolderSize)
        {
            DeleteOldFiles();
		}

	}

    private static void ClientDisconnected(object sender, ClientDisconnectedEventArgs args)
    {
       // Console.WriteLine("Client disconnected: " + args.IpPort + ": " + args.Reason.ToString());
        CurrentClients.Remove(args.IpPort);
        currCount = currCount - 1;

    }

    public static async Task SendMessage(string userInput, string ipPort, bool SendHashes)
    {
      

        byte[] data = null;
        MemoryStream ms = null;
        Dictionary<object, object> metadata;
        if (SendHashes)
        {
            Dictionary<object, object> metad = new Dictionary<object, object>();
            metad.Add(Heart.Vversion, string.Empty);

           // Console.WriteLine("Sending Hashes.. \n");
            var hashstring = JsonConvert.SerializeObject(FileHashes);//InputDictionaryT();
            if (String.IsNullOrEmpty(userInput)) return;
            data = Encoding.UTF8.GetBytes(userInput);
            ms = new MemoryStream(data);
            //Console.WriteLine("SendingMeta Datalength:  " + ms.Length + "/ " + data.Length + "\n  MetaLength: ");
            // var success = await server.SendAsync(ipPort,metadata, data.Length, ms);
            var success = server.SendAsync(ipPort, metad, hashstring);
            //bool success = server.Send(ipPort, Encoding.UTF8.GetBytes(message));

        }
        else
        {
            // Console.WriteLine("IP:Port: "+ipPort);
           // Console.WriteLine("Sending Only Message... \n");
            //  Console.WriteLine("Data: "+userInput);


            data = Encoding.UTF8.GetBytes(userInput);
            ms = new MemoryStream(data);

           // Console.WriteLine("SendingMsgDatalength:  " + ms.Length + "/ " + data.Length + "\n");
            // await server.SendAsync(ipPort, ms);

          //  Console.WriteLine("Sending message:" + data);
            var success = server.SendAsync(ipPort, userInput);
            if (userInput == "STORE|true")
            {
                var client = CurrentClients[ipPort];
                var filen = DeltaStorage + "\\" + client.SignatureHash + ".zip";
                Task.Run(() => (SendNetData(ipPort, filen, true)));
            }

        }
    }

    public static async Task SendNetData(string ip, string zip, bool stored = false)
    {
        var client = CurrentClients[ip];
        Dictionary<object, object> metadata = new Dictionary<object, object>();
        // try
        //{
        //  metadata.Add(zip, zip);
        metadata.Add("1", "2");

        //if (stored)
        //{
        //    while (!IsFileReady(zip))
        //    {
        //        Task.Delay(1500);
        //       // Console.WriteLine("Waiting for file Ready");
        //    }
        //
        //    Task.Delay(5000);
        //
        //}

        using (var source = new FileStream(zip, FileMode.Open, FileAccess.Read))
        {
            await server.SendAsync(ip, metadata, source.Length, source).ConfigureAwait(false);
        }


        if (stored)
        {
            FileLogger.CLog("  Sent Stored Deltas : " + BytesToString(new FileInfo(zip).Length) + " to: " + ip, "Finished.txt");

        }
        else
        {
            FileLogger.CLog( "  UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length) + " to: " + ip, "Finished.txt");
        }

      
       // Console.WriteLine("Alldone for client :" + ip);
       // Console.WriteLine("UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length));
       // Console.WriteLine("Cleaning up....");


        if (client.SignatureHash != string.Empty)
        {
          //  Console.WriteLine("sighash not empty");
            var allfiles = new DirectoryInfo(DeltaStorage).GetFiles();
            if (!allfiles.Any(x => x.Name == client.SignatureHash + ".zip"))
            {
                File.Move(zip, DeltaStorage + "\\" + client.SignatureHash + ".zip");
               // Console.WriteLine("File moved");
            }
        }

        foreach (var x in client.filetoDelete)
        {
            File.Delete(x);
        }

       
    }
    static String BytesToString(long byteCount)
    {
        string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
        if (byteCount == 0)
            return "0" + suf[0];
        long bytes = Math.Abs(byteCount);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return (Math.Sign(byteCount) * num).ToString() + suf[place];
    }

    public async void CreateDeltaforClient(UpdateClient client)
    {

        //Console.WriteLine("creating delta for client....");
        try
        {
            List<CancellationTokenSource> tokenlist = new List<CancellationTokenSource>();
            List<Task> tasks = new List<Task>();

            var allDeltas = Directory.GetFiles(client.ClientFolder.ToString(), "*", SearchOption.AllDirectories);
            foreach (var x in allDeltas)
            {
                var tokenSource = new CancellationTokenSource();
                var token = tokenSource.Token;
                if (!x.Contains(".zip"))
                {
                    deltaprogress++;
                    Task t = Task.Run(() => DeltaPool(x, client), token);
                    tasks.Add(t);
                    tokenlist.Add(tokenSource);
                }
            }
            await Waitforall(tasks).ConfigureAwait(false);
            //Console.WriteLine("all threads Exited.  Sending data");
            
        }
        catch (Exception e)
        {
            FileLogger.CLog("  Error in Create Delta:  " + e.Message, "Errors.txt");
            server.DisconnectClient(client.ClientIP);
           
        }
CreateZipFile(client);

            return;
    skip:
        SendNetData(client.ClientIP, client.Clientdeltazip);



    }

    private async void DeltaForClientDiff(UpdateClient client)
    {

        Console.WriteLine("creating delta for client....");
        try
        {
            //if (File.Exists(Path.GetFullPath(client.Clientdeltazip)))
            //    goto skip;
           
            int CurCount = 0;


            var allDeltas = Directory.GetFiles(client.ClientFolder.ToString(), "*", SearchOption.AllDirectories);
              List<Task> tasks = new List<Task>();
            var allcount = allDeltas.Count();
            //   Heart.ModifyUpdateDic(client.ClientIP, CurCount + "/" + allcount.ToString());

            foreach (var x in allDeltas)
            {

               
                   
                    if (!x.Contains(".zip"))
                    {

                        //  Heart.PoolMod(x,true);
                        deltaprogress++;

                        await Task.Run( () => DeltaPool(x, client)).ConfigureAwait(false);
                     

                      
                     
                    }
                   // Console.WriteLine("Processing "+ deltaprogress.ToString() + x.Split('/').Last());
                    CurCount++;
                //  Heart.ModifyUpdateDic(client.ClientIP, CurCount + "/" + allcount.ToString());
            }



           

        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message);
        }

       // Console.WriteLine("all threads Exited.  Sending data");
        CreateZipFile(client);
        // Heart.ModifyUpdateDic(client.ClientIP, "", true);
    }

    public void PoolProgress()
    {
        int i = 0;
        if ((i % 3) == 0);
            //Console.WriteLine(i + "Threads reported as done");

    }




    public static async Task<bool> Waitforall(List<Task> tasklist)
    {

        //int timeout = 120000;
        //Console.WriteLine("Taskamount: " + tasklist.Count());
        await Task.WhenAll(tasklist.ToArray()).ConfigureAwait(false);





        return true;

    }

    public Task DeltaPool(string x, UpdateClient client)
    {
      
        //Console.WriteLine("Processing " + x );
            client.filetoDelete.Add(x);
            var filename = Path.GetFileName(x);
            var relativePath = x.Replace(client.ClientFolder, string.Empty);
            var SignatureFile = x.Split('\\').Last();//client.ClientFolder + filename));

            var newFilePath = Path.GetFullPath(Rustfolderroot + "\\..") + relativePath.Replace(".octosig", string.Empty);
            if (!File.Exists(newFilePath)) return Task.CompletedTask;

            var deltaFilePath = client.ClientFolder + relativePath.Replace(".octosig", string.Empty) + ".octodelta";
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

            client.dataToSend.Add(deltaFilePath);
       

        // poolp();
        // Heart.PoolMod(x, false);
        return Task.CompletedTask;
       
    }

    public static void CheckDeltas()
    {
        var allfiles = new DirectoryInfo(DeltaStorage).GetFiles();
        foreach(var x in allfiles)
        {
           var delhash = x.Name.Replace(".zip","");
           if(!StoredDeltas.Contains(delhash))
           {
            StoredDeltas.Add(delhash);
		   }
           
        }

    }


    static async Task<bool> fileexisting(UpdateClient client, string msg)
    {
        client.SignatureHash = msg;
        if (StoredDeltas.Contains(client.SignatureHash))
        {
            var filen = DeltaStorage + "\\" + client.SignatureHash + ".zip";
            Task.Run(() => (SendNetData(client.ClientIP, filen, true)));
            return true;
        }

        return false;
    }

    private static  SyncResponse SyncRequestReceived(SyncRequest req)
    {

        try
        {
            var client = CurrentClients[req.IpPort];
            string Messagee = string.Empty;
            var dict = new Dictionary<object, object>();
            if (req.Metadata != null && req.Metadata.Count > 0)
            {
                Messagee = req.Metadata.First().Key.ToString();

                var ex = fileexisting(client, Messagee).Result;

                if (ex)
                {


                    dict.Add("true", "bar");
                    return new SyncResponse(req, dict, "null");
                }
                else
                {
                    dict.Add("false", "bar");
                    return new SyncResponse(req, dict, "null");
                }
            }
            else
            {



                ///Dictionary<object, object> retMetadata = new Dictionary<object, object>();
                //   retMetadata.Add("foo", "bar");
                //   retMetadata.Add("bar", "baz");

                // Uncomment to test timeout
                // Task.Delay(10000).Wait();
                dict.Add("false", "bar");
                return new SyncResponse(req, dict, "null");
            }
            dict.Add("false", "bar");
            return new SyncResponse(req, dict, "null");
        }
        catch(Exception e)
        {
            FileLogger.CLog( "Error in SyncResponse : " + e.Message, "Errors.txt");
            var dict = new Dictionary<object, object>();
            dict.Add("false", "bar");
            //Console.WriteLine("Error with SyncResponse");
            return new SyncResponse(req, dict, "null");
        }
    }
    private static void StreamReceived(object sender, StreamReceivedFromClientEventArgs args)
    {

        try
        {
            var Meta = args.Metadata;
            var user = CurrentClients[args.IpPort];
            var userFolder = user.ClientFolder;
            var fixedname = args.IpPort.Replace(':', '-');
            

            var zippath = userFolder + @"\\" + "signatures" + fixedname + ".zip";
           // Console.WriteLine("New Stream");


          //  Console.WriteLine("Start Processing");
            var bytesRead = 0;
            var bufferSize = 65536;
            var buffer = new byte[bufferSize];
            var bytesRemaining = args.ContentLength;
            Stream file = new FileStream(zippath, FileMode.Create);

            while (bytesRemaining > 0)
            {
                bytesRead = args.DataStream.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    var consoleBuffer = new byte[bytesRead];
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
                foreach (KeyValuePair<object, object> curr in args.Metadata)
                {

                    user.dataToAdd.Add("Rust\\" + curr.Key.ToString());
                }

            
            }

            file.Flush();
            file.Close();
            Task.Run(() => Extract(zippath, user));
          

        }
        catch (Exception e)
        {
            // Console.WriteLine("Exception thrown");
            FileLogger.CLog("Error in StreamReceived : " + e.Message, "Errors.txt");
        }

    }

    public static async Task AddMissingFiles(string path, UpdateClient currentUser, Dictionary<object, object> xtradata)
    {
        //try
        //{





        // }
        // catch
        // {
        //
        //     Console.WriteLine("Error with received List");
        // }

    }

    public static void RunStandalone()
    {
        var zippath = "signatures.zip";
        var ip = "127.0.0.1:1337";
        var fixedip = ip.Replace(':', '0');
        var user = new UpdateClient();
        user.SignatureHash = "a2695e1c54dd4b549f8c1dd26606aab7";
        user.ClientIP = ip;
        string clientfolder = @"ClientFolders\" + fixedip.Replace('.','0');
        var fullpath = new DirectoryInfo(clientfolder).FullName;
        user.ClientFolder = clientfolder;
       
        if (!Directory.Exists(fullpath))
            Directory.CreateDirectory(clientfolder);

        Extract(zippath, user);
        //   Task.Run(() => Extract(zippath, user));
    }

    public static async Task Extract(string path, UpdateClient currentUser)
    {

        var enc = GetEncoding(path);
        var exepath = Directory.GetCurrentDirectory();
        var finalPath = exepath + "//" + currentUser.ClientFolder + "//Rust";

        try
        {
            if (Directory.Exists(currentUser.ClientFolder + "//Rust"))
                Directory.Delete(currentUser.ClientFolder + "//Rust", true);



            if (!File.Exists(finalPath))
                ZipFile.ExtractToDirectory(path, finalPath, enc);



            if (IsFileReady(path))
                File.Delete(path);
        }
        catch(Exception e)
        {
            FileLogger.CLog("Error in Extract : " + e.Message, "Errors.txt");
            SendMessage("ERR|Error In Extracting Zip Serverside", currentUser.ClientIP, false);
            Task.Delay(1000);
            server.DisconnectClient(currentUser.ClientIP);
            return;
        }
        mkdelta(currentUser);
    }


    public delegate void MakeDelta(UpdateClient client);
    #endregion MainMethods

    #region Helper
    public void CheckUsers()
    {
        
        var users = server.Connections;

        currCount = users;

        var AllDirs = Directory.GetDirectories(@"ClientFolders");
        foreach (var x in AllDirs)
        {

            var userIpp = x.Replace("-", ":");
            var thisone = userIpp.Replace(@"ClientFolders\", string.Empty);
           // Console.WriteLine("Checking User " + thisone);
            if (!server.IsClientConnected(thisone))
            {


                try
                {
                    Directory.Delete(x, true);
                }
                catch
                {


                }

            }

        }









    }


    public static void BreakApp()
    {

        var x = "";

    }


    public static Encoding GetEncoding(string filename)
    {
        // Read the BOM
        var bom = new byte[4];
        using (var file = new FileStream(filename, FileMode.Open, FileAccess.Read))
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

    private static bool InputBoolean(string question, bool yesDefault)
    {
        Console.Write(question);

        if (yesDefault) Console.Write(" [Y/n]? ");
        else Console.Write(" [y/N]? ");

        string userInput = Console.ReadLine();

        if (String.IsNullOrEmpty(userInput))
        {
            if (yesDefault) return true;
            return false;
        }

        userInput = userInput.ToLower();

        if (yesDefault)
        {
            if (
                (String.Compare(userInput, "n") == 0)
                || (String.Compare(userInput, "no") == 0)
               )
            {
                return false;
            }

            return true;
        }
        else
        {
            if (
                (String.Compare(userInput, "y") == 0)
                || (String.Compare(userInput, "yes") == 0)
               )
            {
                return true;
            }

            return false;
        }
    }


    public async Task CreateZipFile(UpdateClient client)
    { int retrycount = 0;
        // Create and open a new ZIP file
        starrt:
       
            string zipFileName = client.Clientdeltazip;
        try
        {
          
            var zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create);
            foreach (var x in client.dataToSend)
            {
                var relativePath = x.Replace(client.ClientFolder + "\\Rust\\", string.Empty);
                var fixedname = relativePath.Replace('\\', '/');
                zip.CreateEntryFromFile(x, fixedname, CompressionLevel.Optimal);
            }
            foreach (var y in client.dataToAdd)
            {
                var relativePath = y.Replace("Rust\\", string.Empty);
                var fixedname = relativePath.Replace('\\', '/');
                zip.CreateEntryFromFile(y, fixedname, CompressionLevel.Optimal);
            }

            zip.Dispose();
            client.filetoDelete.Add(zipFileName);
        }
        catch (Exception e)
        {
            FileLogger.CLog("  Error in pack zip:  " + e.Message, "Errors.txt");
            goto retrying;

        }

        if (server.IsClientConnected(client.ClientIP))
            SendNetData(client.ClientIP, zipFileName);
        else
        {
            if (client.SignatureHash != string.Empty)
            {
                if (!StoredDeltas.Contains(client.SignatureHash))
                {
                    StoredDeltas.Add(client.SignatureHash);
                
                    File.Move(zipFileName, DeltaStorage + "\\" + client.SignatureHash + ".zip");
                }
            }
        }

        return;

        retrying:

        if (retrycount > 5)
        {
        //    FileLogger.CLog(DateTime.Now.ToString("MM/dd HH:mm") + "  abort of packing zip after  " + retrycount + " Retry ", "Finished.txt");
            server.DisconnectClient(client.ClientIP);
            return;
        }
        await Task.Delay(15000);
        retrycount++;
        goto starrt;



    }

    private static string InputString(string question, string defaultAnswer, bool allowNull)
    {
        while (true)
        {
            Console.Write(question);

            if (!String.IsNullOrEmpty(defaultAnswer))
            {
                Console.Write(" [" + defaultAnswer + "]");
            }

            Console.Write(" ");

            string userInput = Console.ReadLine();

            if (String.IsNullOrEmpty(userInput))
            {
                if (!String.IsNullOrEmpty(defaultAnswer)) return defaultAnswer;
                if (allowNull) return null;
                else continue;
            }

            return userInput;
        }
    }

    private static int InputInteger(string question, int defaultAnswer, bool positiveOnly, bool allowZero)
    {
        while (true)
        {
            Console.Write(question);
            Console.Write(" [" + defaultAnswer + "] ");

            string userInput = Console.ReadLine();

            if (String.IsNullOrEmpty(userInput))
            {
                return defaultAnswer;
            }

            int ret = 0;
            if (!Int32.TryParse(userInput, out ret))
            {
                Console.WriteLine("Please enter a valid integer.");
                continue;
            }

            if (ret == 0)
            {
                if (allowZero)
                {
                    return 0;
                }
            }

            if (ret < 0)
            {
                if (positiveOnly)
                {
                    Console.WriteLine("Please enter a value greater than zero.");
                    continue;
                }
            }

            return ret;
        }
    }

    
    public static bool IsFileReady(string filename)
    {
        // If the file can be opened for exclusive access it means that the file
        // is no longer locked by another process.
        try
        {
            using (var inputStream = File.Open(StringHelper.AddQuotesIfRequired(filename), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                return inputStream.Length > 0;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static long DirSize(DirectoryInfo d)
    {
        long size = 0;
        // Add file sizes.
        FileInfo[] fis = d.GetFiles();
        foreach (FileInfo fi in fis)
        {
            size += fi.Length;
        }
        // Add subdirectory sizes.
        DirectoryInfo[] dis = d.GetDirectories();
        foreach (DirectoryInfo di in dis)
        {
            size += DirSize(di);
        }
        return size;
    }


    private static void DeleteOldFiles()
    {
        var last = Directory.EnumerateFiles(DeltaStorage)
        .Select(fileName => new FileInfo(fileName))
        .OrderByDescending(fileInfo => fileInfo.LastWriteTime) // or "CreationTime"
        .Skip(50) // Skip 50 newest files
        .Select(fileInfo => fileInfo.FullName);

        foreach (var fileName in last)
        {
			try
			{
				File.Delete(fileName);
			}
			catch 
			{

				
			}
		}
           

    }

    #endregion Helper
}

public static class RichTextBoxExtensions
{
    public static void AppendText(this RichTextBox box, string text, Color color)
    {
        try
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;

            box.SelectionColor = color;
            box.AppendText(text);
            box.SelectionColor = box.ForeColor;
        }
        catch (Exception)
        {

            throw;
        }
    }
}

public class StreamStore
{
    public Dictionary<object, object> StreamData;
    public string userIP;
    public long ContentLength;
    public Stream sStream;

    public StreamStore(string ipPort, Dictionary<object, object> xtradata, long contentLength, Stream stream)
    {
        userIP = ipPort;
        StreamData = xtradata;
        ContentLength = contentLength;
        sStream = stream;
    }
}
