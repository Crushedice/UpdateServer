using Octodiff.Core;
using Octodiff.Diagnostics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Syncfusion.Windows.Forms.Tools;
using WatsonTcp;
using System.IO.Compression;

namespace UpdateServer
{
    public class UpdateServerEntity
    {
        private static string serverIp = "127.0.0.1";
        private static int serverPort = 9000;
        private static bool useSsl = false;
        private static WatsonTcpServer server = null;
        private static string certFile = "";
        private static string certPass = "";
        private static string Rustfolderroot = "Rust";
        private static bool acceptInvalidCerts = true;
        private static bool mutualAuthentication = true;
        private static bool success = false;
    
        private static List<string> clients;
       // private static List<StreamStore> TempStreamStorage = new List<StreamStore>();
        private static Dictionary<string,List<StreamStore>> TempStreamStorage = new Dictionary<string,List<StreamStore>>();
        private static Thread _zipThread = null;
        public static Dictionary<string, UpdateClient> CurrentClients = new Dictionary<string, UpdateClient>();
        public static int currCount = 0;
        public static UpdateServerEntity instance = null;

        public UpdateServerEntity()
        {
            serverIp = InputString("Server IP:", "127.0.0.1", false);
            serverPort = InputInteger("Server port:", 9000, true, false);
            useSsl = InputBoolean("Use SSL:", false);
            AddNewEntry("Server Start...");
            
            instance = this;
           
           // _serverThread.Start();
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
            }

            Form1.sendmessage = SendMessage;
            server.ClientConnected = ClientConnected;
            server.ClientDisconnected = ClientDisconnected;
            server.StreamReceivedWithMetadata = StreamReceivedex;
            server.StreamReceived = StreamReceived;
            // server.MessageReceived = MessageReceived;
            server.Debug = true;
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
            AddNewEntry("  debug      enable/disable debug (currently " + server.Debug + ")");
        }

        #region MainMethods

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

                case "debug":
                    server.Debug = !server.Debug;
                    AddNewEntry("Debug set to: " + server.Debug);
                    break;
            }
        }

        private static async Task ClientConnected(string ipPort)
        {
            AddNewEntry("Client connected: " + ipPort);
            AddNewEntry("Sending Current DB Info to Client...");

            string msg = "";

            string clientfolder = @"ClientFolders\" + ipPort.Split(':')[0].Replace('.', '0');
            var fullpath = new DirectoryInfo(clientfolder).FullName;

            if (!Directory.Exists(fullpath))
                Directory.CreateDirectory(clientfolder);

            CurrentClients.Add(ipPort, new UpdateClient(ipPort, clientfolder));

            currCount++;
            Form1.playercountersegment.Value = currCount.ToString();
            SendMessage("V|" + Form1.Vversion, ipPort);
            Thread.Sleep(200);
            SendMessage("H| \n " + Form1.FileHashes, ipPort);
        }

        private static async Task ClientDisconnected(string ipPort, DisconnectReason reason)
        {
            AddNewEntry("Client disconnected: " + ipPort + ": " + reason.ToString());
            CurrentClients.Remove(ipPort);
            currCount -= 1;
            Form1.playercountersegment.Value = currCount.ToString();
        }

        public static void SendMessage(string userInput, string ipPort = "")
        {
            if (ipPort == "")
            {
                foreach (var x in CurrentClients)
                {
                    var data = Encoding.UTF8.GetBytes(userInput);
                    var ms = new MemoryStream(data);
                    var success = server.Send(x.Key, data.Length, ms);
                    //bool success = server.Send(ipPort, Encoding.UTF8.GetBytes(message));
                    AddNewEntry("Send Message response : " + success);
                }
            }
            else
            {
                // AddNewEntry("IP:Port: "+ipPort);
                if (String.IsNullOrEmpty(ipPort)) return;
                //  AddNewEntry("Data: "+userInput);

                if (String.IsNullOrEmpty(userInput)) return;
                var data = Encoding.UTF8.GetBytes(userInput);
                var ms = new MemoryStream(data);
                var success = server.Send(ipPort, data.Length, ms);
                //bool success = server.Send(ipPort, Encoding.UTF8.GetBytes(message));
                AddNewEntry("Send Message response : " + success);
            }
        }

        private static void SendNetData(string ip)
        {
            var client = CurrentClients[ip];

            foreach (var x in client.dataToSend)
            {
                Dictionary<object, object> metadata = new Dictionary<object, object>();

                metadata.Add(x.Key, x.Value);

                FileStream Fss = new FileStream(x.Value, FileMode.Open, FileAccess.Read);
                var success = server.SendAsync(ip, metadata, Fss.Length, Fss).Result;
                //MessageBox.Show(success.ToString());
            }
            AddNewEntry("Alldone for client :" + ip);
        }

        private static void GetNetData()
        {
        }

        private static async Task MessageReceived(string ipPort, byte[] data)
        {
            string msg = "";
            if (data != null && data.Length > 0)
            {
                msg = Encoding.UTF8.GetString(data);
            }

            if (msg == "done")
            {
                AddNewEntry("Got DONE " );
              //  ExtractreceivedZip();
            }
        }

        private static void CreateDeltaforClient(string Ip)
        {
            var client = CurrentClients[Ip];
            AddNewEntry("creating delta for client....");

            foreach (var x in client.filedata)
            {
                var SignatureFile = x.Value;

                var newFilePath = Rustfolderroot + x.Key;
                var deltaFilePath = client.ClientFolder + x.Key + ".octodelta";
                var deltaOutputDirectory = Path.GetDirectoryName(deltaFilePath);
                if (!Directory.Exists(deltaOutputDirectory))
                    Directory.CreateDirectory(deltaOutputDirectory);
                var deltaBuilder = new DeltaBuilder();
                using (var newFileStream = new FileStream(newFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var signatureFileStream =
                    new FileStream(x.Value, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var deltaStream =
                    new FileStream(deltaFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    deltaBuilder.BuildDelta(newFileStream,
                        new SignatureReader(signatureFileStream, new ConsoleProgressReporter()),
                        new AggregateCopyOperationsDecorator(new BinaryDeltaWriter(deltaStream)));
                }

                client.dataToSend.Add(x.Key, deltaFilePath);
            }

            SendNetData(Ip);
        }

        private static async Task StreamReceived(string ipPort, long contentLength, Stream stream)
        {
            AddNewEntry("Stream received without Metadata");
            string received = "";

            try
            {
                AddNewEntry("Stream without metadata [" + contentLength + " bytes]: ");

                int bytesRead = 0;
                int bufferSize = 131072;
                byte[] buffer = new byte[bufferSize];
                long bytesRemaining = contentLength;

                if (stream != null && stream.CanRead)
                {
                    while (bytesRemaining > 0)
                    {
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                        AddNewEntry("Read " + bytesRead);

                        if (bytesRead > 0)
                        {
                            byte[] consoleBuffer = new byte[bytesRead];
                            Buffer.BlockCopy(buffer, 0, consoleBuffer, 0, bytesRead);

                            //File.WriteAllBytes("Foo.txt", consoleBuffer);
                            received = Encoding.UTF8.GetString(consoleBuffer);
                        }

                        bytesRemaining -= bytesRead;
                    }

                    AddNewEntry("donee");
                    if (received == "done")
                    {
                        ProcStreamList(ipPort);
                        //ThreadPool.QueueUserWorkItem(
                        //    new WaitCallback(delegate (object state)
                        //        { CreateDeltaforClient(ipPort); }), null);
                    }
                    else
                    {
                        AddNewEntry("Received Message: " + received);
                    }
                }
                else
                {
                    AddNewEntry("[null]");
                }
            }
            catch (Exception e)
            {
                LogException("StreamReceived", e);
            }
        }

        static async Task ProcStreamList(string ip)
        {





        }


        private static Task StreamReceivedexss(string ipPort, Dictionary<object, object> xtradata, long contentLength, Stream stream)
        {
            //   ProcessFileStream(ipPort, xtradata, stream, "");

            var Filename = xtradata.Values.First().ToString();//.Split('/').Last();
            AddNewEntry("Received File. Name : " + Filename +"  " + contentLength
                         );

            //Task.Run(() => ProcessFileStream(ipPort,xtradata,stream,""));
            var user = CurrentClients[ipPort];
            var userFolder = user.ClientFolder;
            var FileName = xtradata.Values.First().ToString().Split('/').Last();
            var addFolder = xtradata.Values.First().ToString().TrimEnd(FileName.ToCharArray());
           // try
           // {
                
                    AddNewEntry("Saving Stream as file...");
                    //CopyStream(stream, file);
                    int bytesRead = 0;
                    int bufferSize = 65536;
                    byte[] buffer = new byte[bufferSize];
                    long bytesRemaining = contentLength;
                    while (bytesRemaining > 0)
                    {
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                     //   Console.WriteLine("Read " + bytesRead);

                        if (bytesRead > 0)
                        {
                            byte[] consoleBuffer = new byte[bytesRead];
                            Buffer.BlockCopy(buffer, 0, consoleBuffer, 0, bytesRead);
                            using (Stream file = File.Create(userFolder + "//" + addFolder + FileName))
                            {
                                file.Write(consoleBuffer, 0, bytesRead);
                            }
                        }

                        bytesRemaining -= bytesRead;
                    }
                    user.filedata.Add(xtradata.Keys.First().ToString(), userFolder + Filename);
                 //   file.Dispose();
                  
                
                    AddNewEntry("File Done");

                    Task.Delay(5000);
                return Task.CompletedTask;
                

                //}
                //catch (Exception)
                //{
                //    AddNewEntry("Failed to save File.. ");
                //    using (Stream file = File.Create("Failed!"))
                //    {
                //        CopyStream(stream, file);
                //    }
                //}

        }

        private static async Task StreamReceivedex(string ipPort, Dictionary<object, object> xtradata, long contentLength, Stream stream)
        {
            // StreamStore current = new StreamStore(ipPort,xtradata,contentLength,stream);
            // if (!TempStreamStorage.ContainsKey(ipPort))
            // {
            //     var n = new List<StreamStore>();
            //     n.Add(current);
            //     TempStreamStorage.Add(ipPort,n);
            //
            // }
            // else
            // {
            //     TempStreamStorage[ipPort].Add(current);
            // }
            //
            // Form1.countter.Value = TempStreamStorage.Count();
            AddNewEntry("Stream received...");
            var user = CurrentClients[ipPort];
            var userFolder = user.ClientFolder;
            var FileName = xtradata.Values.First().ToString().Split('/').Last();
            var addFolder = xtradata.Values.First().ToString().TrimEnd(FileName.ToCharArray());
            var additional = xtradata.Keys.First().ToString().TrimEnd(FileName.ToCharArray());
            var zippath = userFolder + "//" + addFolder + FileName;
            Stream file;
             int bytesRead = 0;
             int bufferSize = 131072;
             byte[] buffer = new byte[bufferSize];
             long bytesRemaining = contentLength;
            AddNewEntry("Start Copy Stream");
            file = new FileStream(zippath, FileMode.Create);

                while (bytesRemaining > 0)
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);
                   
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
               
               
                Task.Run(() => ExtractreceivedZip(zippath, user));
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


        static async Task ExtractreceivedZip(string path, UpdateClient currentUser)
        {

            var enc = GetEncoding(path);
            var exepath = Directory.GetCurrentDirectory();
            var finalPath = exepath + "//" + currentUser.ClientFolder + "//Rust";

            ZipFile.ExtractToDirectory(path, finalPath,enc);
            


        }

        #endregion MainMethods

        #region Helper

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

        public static void CopyStream(Stream input, Stream output)
        {
            AddNewEntry("start in copy method while loop");
            byte[] buffer = new byte[8 * 1024];
            int len;
            while ((len = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, len);
            }

            
            AddNewEntry("While loop exited");
           
        }

        private static string GetTextFromStream(Stream stream, long contentLength)
        {
            int bytesRead = 0;
            int bufferSize = 65536;
            byte[] buffer = new byte[bufferSize];
            long bytesRemaining = contentLength;
            AddNewEntry("FirstPart Content length = " + contentLength);
            if (stream != null && stream.CanRead)
            {
                while (bytesRemaining > 0)
                {
                    bytesRead = stream.Read(buffer, 0, buffer.Length);

                    if (bytesRead > 0)
                    {
                        byte[] consoleBuffer = new byte[bytesRead];
                        Buffer.BlockCopy(buffer, 0, consoleBuffer, 0, bytesRead); AddNewEntry(Encoding.UTF8.GetString(consoleBuffer));
                        return Encoding.UTF8.GetString(consoleBuffer);
                    }

                    bytesRemaining -= bytesRead;
                }
            }

            return "";
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
            // StringBuilder sb = new StringBuilder(Console.Text);
            // Append the string you want, to the string builder object.
            // sb.Append($">>{text}\r\n");
            // Finally assign the string builder's current string to the Label Text
            //  Console.Text = sb.ToString();
            try
            {
                RichTextBoxExtensions.AppendText(Form1.Consolewindow,
                    $"[{DateTime.Now.ToShortTimeString()}] {text}\r\n", Color.Green);
                // Thread.Sleep(250);
                Form1.Consolewindow.ScrollToCaret();
            }
            catch
            {
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