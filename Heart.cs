using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Sentry;

namespace UpdateServer
{
    public class Heart
    {
        #region Delegates
        public delegate void sendmsg(string inp, string ip, bool hashes);
        #endregion

        #region Thread-Safe Locks
        private static readonly object CurUpdatersLock = new object();
        private static readonly object FileHashesLock = new object();
        private static readonly object SingleStoredDeltaLock = new object();
        private static readonly object ThreadListLock = new object();
        #endregion

        #region Static Properties and Fields
        public static Dictionary<string, string> CurUpdaters = new Dictionary<string, string>();
        public static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
        public static Dictionary<string, string> singleStoredDelta = new Dictionary<string, string>();
        public static List<string> ThreadList = new List<string>();
        public static string Hashes = string.Empty;
        public static string Vversion = string.Empty;
        
        // UI Components (if used)
        public static TextBox Inputbox;
        public static RichTextBox ProgressOvLk;
        public static ListBox UpdaterGrid;
        public static Label VersionLabel;
        public static sendmsg sendmessage;
        
        // Internal
        private static Thread Server;
        #endregion

        #region Instance Fields
        public UpdateServerEntity _server;
        #endregion

        #region Constructor
        public Heart()
        {
            InitializeVersion();
            StartServer();
        }
        #endregion

        #region Initialization Methods
        private void InitializeVersion()
        {
            DirectoryInfo directoryInfo = new DirectoryInfo(Directory.GetCurrentDirectory());

            if (File.Exists("Version.txt"))
            {
                string version = File.ReadAllText("Version.txt");
                Vversion = version;
            }
            else
            {
                Console.WriteLine("Error - Version File Not Found!!!");
                Vversion = "2318";
            }
        }

        private async void StartServer()
        {
            await InitializeHashes();
            Console.WriteLine("Heart Started. Starting update server...");
            _server = new UpdateServerEntity();
        }

        private async Task InitializeHashes()
        {
            if (!File.Exists("Hashes.json"))
            {
                await new HashGen().Run();
                LoadHashesFromFile();
            }
            else
            {
                LoadHashesFromFile();
            }
        }

        private void LoadHashesFromFile()
        {
            lock (FileHashesLock)
            {
                Hashes = File.ReadAllText("Hashes.json");
                FileHashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(Hashes);
            }
        }
        #endregion

        #region Static Methods - Thread Management
        public static void PoolMod(string thread, bool add)
        {
            lock (ThreadListLock)
            {
                if (add)
                    ThreadList.Add(thread);
                else
                    ThreadList.Remove(thread);
            }
            ShowPoolThreads();
        }

        public static void ShowPoolThreads()
        {
            ProgressOvLk?.Clear();
            Console.WriteLine("Threads: \n");

            foreach (string thread in ThreadList) 
                Console.WriteLine(thread + "\n");
        }
        #endregion

        #region Static Methods - Delta Management
        public static void addsingledelta(string key, string value)
        {
            lock (SingleStoredDeltaLock)
            {
                singleStoredDelta.Add(key, value);
            }
        }
        #endregion

        #region Static Methods - UI Management
        public static void ProcessUpdaterList(string ipPort, int indicator, string files = "")
        {
            if (UpdaterGrid == null) return;

            string name = ipPort.Split(':')[0].Replace('.', '0');

            switch (indicator)
            {
                case 1: // Client starts updating
                    if (!UpdaterGrid.Items.Contains(name)) 
                        UpdaterGrid.Items.Add(name);
                    UpdaterGrid.Refresh();
                    break;

                case 2: // Client done updating / Client got delta files
                    if (UpdaterGrid.Items.Contains(name)) 
                        UpdaterGrid.Items.Remove(name);
                    UpdaterGrid.Refresh();
                    break;

                case 3: // Update the UI
                    if (UpdaterGrid.Items.Contains(name))
                    {
                        // UpdaterGrid[name] = files;
                    }
                    UpdaterGrid.Refresh();
                    break;
            }
        }
        #endregion

        #region Private Methods - Tree Building
        private void BuildTree(DirectoryInfo directoryInfo, TreeNodeCollection addInMe)
        {
            TreeNode curNode = addInMe.Add(directoryInfo.Name);

            foreach (FileInfo file in directoryInfo.GetFiles()) 
                curNode.Nodes.Add(file.FullName, file.Name);

            foreach (DirectoryInfo subdir in directoryInfo.GetDirectories()) 
                BuildTree(subdir, curNode.Nodes);
        }
        #endregion

        #region Nested Classes
        public class NodeSorter : IComparer
        {
            /// <summary>
            /// Compare the length of the strings, or the strings themselves, if they are the same length.
            /// </summary>
            public int Compare(object x, object y)
            {
                TreeNode tx = x as TreeNode;
                TreeNode ty = y as TreeNode;

                // Compare the length of the strings, returning the difference.
                if (tx.Text.Length != ty.Text.Length)
                    return tx.Text.Length - ty.Text.Length;

                // If they are the same length, call Compare.
                return string.Compare(tx.Text, ty.Text);
            }
        }
        #endregion
    }
}