using Octodiff.Core;
using Octodiff.Diagnostics;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using StringHelp;
using WatsonTcp;


namespace UpdateServer
{
    public class UpdateServerEntity
    {
        private static string serverIp = "145.239.130.132";
        private static int serverPort = 9000;
        private static bool useSsl = false;
        private static WatsonTcpServer server = null;
        private static string certFile = "";
        private static string certPass = "";
        private static string Rustfolderroot = "Rust";
        private static string DeltaStorage = "DeltaStorage";
        private static bool acceptInvalidCerts = true;
        private static bool mutualAuthentication = true;
        private static bool success = false;
        public static bool consolle = true;
        private static List<string> clients;
        private static Dictionary<string, List<StreamStore>> TempStreamStorage = new Dictionary<string, List<StreamStore>>();
        private static Thread _zipThread = null;
        public static Dictionary<string, UpdateClient> CurrentClients = new Dictionary<string, UpdateClient>();
        public static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
        public static Dictionary<string, string> UpdaterGrid = new Dictionary<string, string>();
        public static int currCount = 0;
        public static UpdateServerEntity instance = null;
        public static int deltaprogress = 0;
        public static MakeDelta mkdelta;
        public delegate void pooldelegate();
     //   public static string _Zippath = "//" + "signatures" + args.IpPort.Split(':')[0].Replace('.', '0') + ".zip";

        public static pooldelegate poolp;
        public UpdateServerEntity()
        {
            serverIp = InputString("Server IP:", serverIp, false);
            serverPort = InputInteger("Server port:", serverPort, true, false);
            useSsl = InputBoolean("Use SSL:", false);
            AddNewEntry("Server Start...");
             mkdelta = CreateDeltaforClient;
            instance = this;
            FileHashes = Form1.FileHashes;
            poolp = PoolProgress;
            // _serverThread.Start();
            Form1.UpdaterGrid.DataSource = null;
            Start();
        }
        [assembly: InternalsVisibleTo("WatsonTcp")]
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
                server.Logger = s => AddNewEntry(s);
                server.DebugMessages = true;

            }

           
            server.ClientConnected += ClientConnected;
            server.ClientDisconnected += ClientDisconnected;
            server.StreamReceived += StreamReceived;
            server.SyncRequestReceived = SyncRequestReceived;


            // using lambda expression..could use method like other answers on here



            // server.MessageReceived = MessageReceived;

            server.StartAsync();
          
            AddNewEntry("Command [? for help]: ");

            AddNewEntry("Available commands:");
            AddNewEntry("  ?          help (this menu)");
            AddNewEntry("  q          quit");
            AddNewEntry("  cls        clear screen");
            AddNewEntry("  list       list clients");
            AddNewEntry("  send       send message to a client");
            AddNewEntry("  sendasync  send message to a client asynchronously");
            AddNewEntry("  remove     disconnect client");
            AddNewEntry("  psk        set preshared key");
            
        }
        private static void lloogg(string msg) { AddNewEntry(msg); }
        #region MainMethods
        static  void Feedback_Received(object sender,EventArgs e)
        {
            AddNewEntry(e.ToString());
        }

        public void changeConsole(bool state)
        {

            consolle = state;

        }

        private static void SendNewCommand()
        {
            string ipPort;
            string CommandToSend = Form1.Inputbox.Text;
            AddNewEntry(CommandToSend);

            switch (CommandToSend)
            {
                case "cls":
                    Console.Clear();
                    break;

                case "list":
                    clients = server.ListClients().ToList();
                    if (clients != null && clients.Count > 0)
                    {
                        AddNewEntry("Clients");
                        foreach (string curr in clients)
                        {
                            AddNewEntry("  " + curr);
                        }
                    }
                    else
                    {
                        AddNewEntry("None");
                    }
                    break;

                case "send":
                    AddNewEntry("IP:Port: ");
                    ipPort = Console.ReadLine();
                    if (String.IsNullOrEmpty(ipPort)) break;
                    AddNewEntry("Data: ");
                    FileStream Fs = new FileStream("Version.txt", FileMode.Open, FileAccess.Read);
                    success = server.Send(ipPort, (int)Fs.Length, Fs);
                    AddNewEntry(success.ToString());
                    break;

                case "sendasync":
                    AddNewEntry("IP:Port: ");
                    ipPort = Console.ReadLine();
                    if (String.IsNullOrEmpty(ipPort)) break;
                    AddNewEntry("Data: ");
                    FileStream Fss = new FileStream("Version.txt", FileMode.Open, FileAccess.Read);
                    success = server.SendAsync(ipPort, (int)Fss.Length, Fss).Result;
                    AddNewEntry(success.ToString());
                    break;

                case "remove":
                    Console.Write("IP:Port: ");
                    ipPort = Console.ReadLine();
                    server.DisconnectClient(ipPort);
                    break;

                case "psk":
                    server.PresharedKey = InputString("Preshared key:", "1234567812345678", false);
                    break;

               
            }
        }

        private static async void ClientConnected(object sender, ClientConnectedEventArgs args)
        {
            AddNewEntry("Client connected: " + args.IpPort);
            AddNewEntry("Sending Current DB Info to Client...");

            string msg = "";

            // string clientfolder = @"ClientFolders\" + args.IpPort.Split(':')[0].Replace('.', '0');
            string clientfolder = @"ClientFolders\" + args.IpPort.Replace(':', '-');
            var fullpath = new DirectoryInfo(clientfolder).FullName;

            if (!Directory.Exists(fullpath))
                Directory.CreateDirectory(clientfolder);

            CurrentClients.Add(args.IpPort, new UpdateClient(args.IpPort, clientfolder));
           CurrentClients[args.IpPort].Clientdeltazip = clientfolder + "\\Deltas" + args.IpPort.Replace(':', '-') + ".zip";
            currCount++;
            Form1.playercountersegment.Value = currCount.ToString();
            //Task.Run(() => SendMessage("V|" + Form1.Vversion, args.IpPort,false));
            await SendMessage("V|" + Form1.Vversion + "|", args.IpPort, false);
            Task.Delay(1000);
            await SendMessage("H|"+Form1.Hashes , args.IpPort, false);
           // await SendMessage("H|" , args.IpPort, true);
            //  SendMessage("H| ", args.IpPort,true);
        }

        private static void ClientDisconnected(object sender, ClientDisconnectedEventArgs args)
        {
            AddNewEntry("Client disconnected: " + args.IpPort + ": " + args.Reason.ToString());
            CurrentClients.Remove(args.IpPort);
            currCount = currCount - 1;
            Form1.playercountersegment.Value = currCount.ToString();
        }

        public static async Task SendMessage(string userInput,string ipPort, bool SendHashes)
        {
            try
            {
                AddNewEntry("\n \n ---New SendMessage--- \n"+ userInput + "\n\n");
            }
            catch 
            {
               
            }
            
            byte[] data = null;
            MemoryStream ms = null;
            Dictionary<object, object> metadata;
            if (SendHashes)
            {

             
                AddNewEntry("Sending Hashes.. \n");
                metadata = InputDictionaryT();
                if (String.IsNullOrEmpty(userInput)) return;
                 data = Encoding.UTF8.GetBytes(userInput);
                 ms = new MemoryStream(data);
                AddNewEntry("SendingMeta Datalength:  " + ms.Length + "/ " + data.Length +"\n  MetaLength: " );
                // var success = await server.SendAsync(ipPort,metadata, data.Length, ms);
                var success = server.SendAsync(ipPort, metadata, userInput);
                //bool success = server.Send(ipPort, Encoding.UTF8.GetBytes(message));

            }
            else
            {
                // AddNewEntry("IP:Port: "+ipPort);
               AddNewEntry("Sending Only Message... \n");
                //  AddNewEntry("Data: "+userInput);

             
                data = Encoding.UTF8.GetBytes(userInput); 
                ms = new MemoryStream(data);

                AddNewEntry("SendingMsgDatalength:  " + ms.Length + "/ " + data.Length + "\n"  );
                // await server.SendAsync(ipPort, ms);

                AddNewEntry("Sending message:" + data);
                var success = server.SendAsync(ipPort, userInput);
                if (userInput == "STORE|true")
                {
                    var client = CurrentClients[ipPort];
                    var filen = DeltaStorage + "\\" + client.SignatureHash + ".zip";
                    Task.Run(() => (SendNetData(ipPort, filen, true)));
                }

            }
        }

        public static async Task SendNetData(string ip, string zip,bool stored = false)
        {
            var client = CurrentClients[ip];
           

            Dictionary<object, object> metadata = new Dictionary<object, object>();

           // try
            //{
                //  metadata.Add(zip, zip);
                metadata.Add("1", "2");

                if (stored)
                {
                    while (!IsFileReady(zip))
                    {
                        Task.Delay(1500);

                    }

                    Task.Delay(5000);

                }



                using (var source = new FileStream(zip, FileMode.Open, FileAccess.Read))
                {
                    await server.SendAsync(ip, metadata, source.Length, source);
                }


                if (stored)
                {
                    FileLogger.CLog("Sent Stored Deltas : " + BytesToString(new FileInfo(zip).Length), "Finished.txt");
                   
                }
                else
                {
                FileLogger.CLog("UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length), "Finished.txt");
            }

          //  }
          //  catch (Exception e )
          //  {
          //
          //      AddNewEntry(e.Message);
          //  }
            AddNewEntry("Alldone for client :" + ip);
            AddNewEntry("UpdateCreation Finished : " + BytesToString(new FileInfo(zip).Length));
            AddNewEntry("Cleaning up....");
            

            if (client.SignatureHash != string.Empty)
            {
                AddNewEntry("sighash not empty");
                var allfiles = new DirectoryInfo(DeltaStorage).GetFiles();
                if (!allfiles.Any(x => x.Name == client.SignatureHash + ".zip"))
                {
                    File.Move(zip, DeltaStorage + "\\" + client.SignatureHash + ".zip");
                    AddNewEntry("File moved");
                }
            }

            foreach (var x in client.filetoDelete)
            {
                File.Delete(x);
            }

            try
            {
                foreach (var x in Directory.GetFiles(client.ClientFolder))
                {
                    File.Delete(x);
                }
            }
            catch 
            {
                
            }
            Form1.ProcessUpdaterList(ip, 2);
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
        private static void Loggers(string msg) { AddNewEntry(msg); }
        private static Dictionary<object, object> InputDictionaryT()
        {
            var ret = new Dictionary<object, object>();


            if (FileHashes.Count > 0)
                foreach (var x in FileHashes)
                    ret.Add(x.Key, x.Value);
            //mss.Add(x.Key);

            return ret;
        }

        private async  void CreateDeltaforClient(UpdateClient client)
        {
            Task.Delay(500);
           // Thread.Sleep(500);
            //var client = CurrentClients[Ip];
            AddNewEntry("creating delta for client....");


            //if (File.Exists(Path.GetFullPath(client.Clientdeltazip)))
            //    goto skip;

           

            var allDeltas = Directory.GetFiles(client.ClientFolder.ToString(), "*", SearchOption.AllDirectories);
            List<Task> tasks = new List<Task>();
            foreach (var x in allDeltas)
            {

                if (!x.Contains(".zip"))
                {
                    deltaprogress++;
                    Task t = Task.Run(() => DeltaPool(x, client));
                    tasks.Add(t);
                }
            }
            
           // Form1.ProcessUpdaterList(client.ClientIP,3,"Creating Delta for "+tasks.Count +" files");

            await Waitforall(tasks);
           


            AddNewEntry("all threads Exited.  Sending data");
            CreateZipFile(client);
            return;

            skip:
            SendNetData(client.ClientIP, client.Clientdeltazip);

           

        }

        public void PoolProgress()
        {
            int i = 0;
            if ((i % 3) == 0) 
                AddNewEntry(i + "Threads reported as done");



        }

        


        public static async Task<bool> Waitforall(List<Task> tasklist)
        {

            
               AddNewEntry("Taskamount: "+ tasklist.Count());
            Task.WaitAll(tasklist.ToArray());




            return true;

        }

        public async Task DeltaPool(string x , UpdateClient client)
        {
            try
            {

                client.filetoDelete.Add(x);
                var filename = Path.GetFileName(x);
                var relativePath = x.Replace(client.ClientFolder, string.Empty);
                var SignatureFile = x.Split('\\').Last();//client.ClientFolder + filename));

                var newFilePath = Path.GetFullPath(Rustfolderroot + "\\..") + relativePath.Replace(".octosig", "");
                if (!File.Exists(newFilePath)) return;

                var deltaFilePath = client.ClientFolder + relativePath.Replace(".octosig", "") + ".octodelta";
                var deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
                if (!Directory.Exists(deltaOutputDirectory))
                    Directory.CreateDirectory(deltaOutputDirectory);
                var deltaBuilder = new DeltaBuilder();
                using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var signatureFileStream =
                    new FileStream(x, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var deltaStream = new FileStream(deltaFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    deltaBuilder.BuildDelta(newFileStream,
                        new SignatureReader(signatureFileStream, new ConsoleProgressReporter()),
                        new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream)));
                }

                client.dataToSend.Add(deltaFilePath);
            }
            catch 
            {

                SendMessage("ERR|Error In Creating DeltaFile", client.ClientIP, false);
            }
         
            // poolp();


        }

       static async Task<bool> fileexisting(UpdateClient client,string msg)
        {
            client.SignatureHash = msg;
            var allfiles = new DirectoryInfo(DeltaStorage).GetFiles();
            if (allfiles.Any(x => x.Name == msg + ".zip"))
            {
                var filen = DeltaStorage + "\\" + client.SignatureHash + ".zip";
                Task.Run(() => (SendNetData(client.ClientIP, filen, true)));
                return true;
            }

            return false;
        }

        private static SyncResponse SyncRequestReceived(SyncRequest req)
        {
           
            try
            {
                var client = CurrentClients[req.IpPort];
                string Messagee = "";
                var dict = new Dictionary<object, object>();
                if (req.Metadata != null && req.Metadata.Count > 0)
                {
                    Messagee = req.Metadata.First().Key.ToString();
                    
                    bool ex = fileexisting(client,Messagee).Result;

                    if(ex)
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
            catch
            {
                var dict = new Dictionary<object, object>();
                dict.Add("false", "bar");
                AddNewEntry("Error with SyncResponse");
                return new SyncResponse(req,dict,"null");
            }
        }
        private static void StreamReceived(object sender, StreamReceivedFromClientEventArgs args)
        {
       
            //if (args.Metadata.Count <1)
            //{
            //    string Msg = "";
            //    var client = CurrentClients[args.IpPort];
            //    var bytesRead = 0;
            //    var bufferSize = 65536;
            //    var buffer = new byte[bufferSize];
            //    var bytesRemaining = args.ContentLength;
            //   
            //
            //    while (bytesRemaining > 0)
            //    {
            //        bytesRead = args.DataStream.Read(buffer, 0, buffer.Length);
            //
            //        if (bytesRead > 0)
            //        {
            //            var consoleBuffer = new byte[bytesRead];
            //            Buffer.BlockCopy(buffer, 0, consoleBuffer, 0, bytesRead);
            //            Msg += Encoding.UTF8.GetString(consoleBuffer);
            //        }
            //
            //        bytesRemaining -= bytesRead;
            //        // AddNewEntry("bytesread 0 , looping around.");
            //    }
            //
            //    client.SignatureHash = Msg;
            //    var allfiles = new DirectoryInfo(DeltaStorage).GetFiles();
            //    if (allfiles.Any(x => x.Name == client.SignatureHash + ".zip"))
            //    {
            //        var filen = DeltaStorage + "\\" + client.SignatureHash + ".zip";
            //      //  Task.Run(() => (SendNetData(client.ClientIP, filen, true)));
            //        Task.Run(() => (SendMessage("STORE|true", client.ClientIP, false)));
            //
            //        return;
            //    }
            //    else
            //    {
            //        Task.Run(() => (SendMessage("STORE|false", client.ClientIP, false)));
            //      //  SendMessage("STORE|false", client.ClientIP, false);
            //        return;
            //    }
            //
            //}
            //else
            //{




                try
                {
                    var Meta = args.Metadata;
                    var user = CurrentClients[args.IpPort];
                    var userFolder = user.ClientFolder;
                    var zippath = userFolder + @"\\" + "signatures" + args.IpPort.Replace(':', '-') + ".zip";
                    AddNewEntry("New Stream");


                    AddNewEntry("Start Processing");
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
                        // AddNewEntry("bytesread 0 , looping around.");
                    }


                    AddNewEntry("Processing Meta if avalible");
                    if (args.Metadata != null && args.Metadata.Count > 0)
                    {
                        // Console.WriteLine("Metadata:");
                        foreach (KeyValuePair<object, object> curr in args.Metadata)
                        {

                            user.dataToAdd.Add("Rust\\" + curr.Key.ToString());
                        }

                        AddNewEntry("Missing files from client: " + user.dataToAdd.Count);
                    }

                    AddNewEntry("Finishing File,  Flush and Run Extract Task");
                    file.Flush();
                    file.Close();
                    Task.Run(() => Extract(zippath, user));
                    // Task.Run(() => AddMissingFiles(zippath, user, xtradata));
                    // Stream st = resp.GetResponseStream();
                    // FileStream f = new FileStream(@"f:\pic\samp\rt.jpg", FileMode.Create);
                    // byte[] b = new byte[10000];
                    //
                    // st.Read(b, 0, b.Length);
                    //
                    // st.Close();
                    // f.Write(b, 0, b.Length);
                    // f.Flush();
                    // f.Close();
                    // DirectoryInfo di = Directory.CreateDirectory(userFolder + "//" + additional);
                    // int bytesRead = 0;
                    // int bufferSize = 131072;
                    // byte[] buffer = new byte[bufferSize];
                    // long bytesRemaining = contentLength;
                    // if (stream != null && stream.CanRead)
                    // {
                    //     while (bytesRemaining > 0)
                    //     {
                    //         bytesRead = stream.Read(buffer, 0, buffer.Length);
                    //
                    //         if (bytesRead > 0)
                    //         {
                    //             byte[] consoleBuffer = new byte[bytesRead];
                    //             Buffer.BlockCopy(buffer, 0, consoleBuffer, 0, bytesRead);
                    //             file = File.OpenWrite(zippath);
                    //
                    //         }
                    //         bytesRemaining -= bytesRead;
                    //     }
                    // }
                    // else
                    // {
                    //     AddNewEntry("[null]");
                    // }
                    //
                    // AddNewEntry("Done!");
                    AddNewEntry("Finished!");

                }
                catch (Exception)
                {
                    AddNewEntry("Exception thrown");
                    throw;
                }
            
        }

        public static async Task AddMissingFiles(string path, UpdateClient currentUser,Dictionary<object, object> xtradata)
        {
            //try
            //{

            



            // }
           // catch
           // {
           //
           //     AddNewEntry("Error with received List");
           // }
           
        }

        public static async Task Extract(string path, UpdateClient currentUser )
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
            catch 
            {

              SendMessage("ERR|Error In Extracting Zip Serverside",currentUser.ClientIP,false);
              Task.Delay(1000);
              server.DisconnectClient(currentUser.ClientIP);
              return;
            }
            mkdelta(currentUser);
        }


        public delegate void MakeDelta(UpdateClient client);
        #endregion MainMethods

        #region Helper
        private static void SendAndWait()
        {
            string ipPort = InputString("IP:port:", null, false);
            string userInput = InputString("Data:", null, false);
            int timeoutMs = InputInteger("Timeout (milliseconds):", 5000, true, false);

            try
            {
                SyncResponse resp = server.SendAndWait(ipPort, timeoutMs, userInput);
                if (resp.Metadata != null && resp.Metadata.Count > 0)
                {
                    Console.WriteLine("Metadata:");
                    foreach (KeyValuePair<object, object> curr in resp.Metadata)
                    {
                        Console.WriteLine("  " + curr.Key.ToString() + ": " + curr.Value.ToString());
                    }
                }

                Console.WriteLine("Response: " + Encoding.UTF8.GetString(resp.Data));
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e.ToString());
            }
        }

        private static void SendAndWaitEmpty()
        {
            string ipPort = InputString("IP:port:", null, false);
            int timeoutMs = InputInteger("Timeout (milliseconds):", 5000, true, false);

            Dictionary<object, object> dict = new Dictionary<object, object>();
            dict.Add("foo", "bar");

            try
            {
                SyncResponse resp = server.SendAndWait(ipPort, dict, timeoutMs);
                if (resp.Metadata != null && resp.Metadata.Count > 0)
                {
                    Console.WriteLine("Metadata:");
                    foreach (KeyValuePair<object, object> curr in resp.Metadata)
                    {
                        Console.WriteLine("  " + curr.Key.ToString() + ": " + curr.Value.ToString());
                    }
                }

                Console.WriteLine("Response: " + Encoding.UTF8.GetString(resp.Data));
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e.ToString());
            }
        }
        public void CheckUsers()
        {
            
                var users = server.Connections;
                Form1.playercountersegment.Value = users.ToString();
                currCount = users;

                var AllDirs = Directory.GetDirectories(@"ClientFolders");
                foreach (var x in AllDirs)
                {
                    var userIpp = x.Replace("-", ":");
                    var thisone = userIpp.Replace(@"ClientFolders\", "");
                    AddNewEntry("Checking User " + thisone);
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
        {
            // Create and open a new ZIP file

            string zipFileName = client.Clientdeltazip;
            var zip = ZipFile.Open(zipFileName, ZipArchiveMode.Create);
            AddNewEntry("ToSend FileCount: " + client.dataToSend.Count);
            foreach (var x in client.dataToSend)
            {
                var relativePath = x.Replace(client.ClientFolder+"\\Rust\\", string.Empty);
                var fixedname = relativePath.Replace('\\', '/');
               
                // var leadingPath = fixedname.TrimStart(RustFolder.FullName.ToCharArray());
               
                // Add the entry for each file
                zip.CreateEntryFromFile(x, fixedname, CompressionLevel.Optimal);
            }
            AddNewEntry("ToAdd FileCount: " + client.dataToAdd.Count);
            foreach (var y in client.dataToAdd)
            {
                var relativePath = y.Replace("Rust\\", string.Empty);
                var fixedname = relativePath.Replace('\\', '/');
                zip.CreateEntryFromFile(y, fixedname, CompressionLevel.Optimal);
            }
            // Dispose of the object when we are done
            zip.Dispose();
            client.filetoDelete.Add(zipFileName);

            if(server.IsClientConnected(client.ClientIP))
            SendNetData(client.ClientIP, zipFileName);
            else
            {
                AddNewEntry("Abort. Client not connected anymore");
            }

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
                    AddNewEntry("Please enter a valid integer.");
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
                        AddNewEntry("Please enter a value greater than zero.");
                        continue;
                    }
                }

                return ret;
            }
        }

        private static void LogException(string method, Exception e)
        {
            AddNewEntry("");
            AddNewEntry("An exception was encountered.");
            AddNewEntry("   Method        : " + method);
            AddNewEntry("   Type          : " + e.GetType().ToString());
            AddNewEntry("   Data          : " + e.Data);
            AddNewEntry("   Inner         : " + e.InnerException);
            AddNewEntry("   Message       : " + e.Message);
            AddNewEntry("   Source        : " + e.Source);
            AddNewEntry("   StackTrace    : " + e.StackTrace);
            AddNewEntry("");
        }

        public static void AddNewEntry(string text)
        {
        
           if (!consolle) 
               goto skip;

            try
            {
                RichTextBoxExtensions.AppendText(Form1.Consolewindow,
                    $"[{DateTime.Now.ToShortTimeString()}] {text}\r\n", Color.Green);
                 Thread.Sleep(150);
                Form1.Consolewindow.ScrollToCaret();
            }
            catch
            {
            }

            return;

            skip:
            Thread.Sleep(150); 
            


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

        #endregion Helper
    }

    public static class RichTextBoxExtensions
    {
        public static void AppendText(this RichTextBox box, string text, Color color)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;

            box.SelectionColor = color;
            box.AppendText(text);
            box.SelectionColor = box.ForeColor;
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
}