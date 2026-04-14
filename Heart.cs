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
        private static readonly object CurUpdatersLock = new();
        private static readonly object FileHashesLock = new();
        // Locks retained for compound operations on the following concurrent collections.
        private static readonly object SingleStoredDeltaLock = new();
        private static readonly object ThreadListLock = new();
        #endregion

        #region Static Properties and Fields
        // Use thread‑safe collections for shared mutable state.
        public static System.Collections.Concurrent.ConcurrentDictionary<string, string> CurUpdaters = new();
        public static System.Collections.Concurrent.ConcurrentDictionary<string, string> FileHashes = new();
        public static System.Collections.Concurrent.ConcurrentDictionary<string, string> singleStoredDelta = new();
        public static System.Collections.Concurrent.ConcurrentQueue<string> ThreadList = new();
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

        public Heart(bool isNewUpdate) : this()
        {
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
                var deserialized = JsonConvert.DeserializeObject<Dictionary<string, string>>(Hashes);
                // Replace the concurrent dictionary with a new concurrent instance based on the
                // deserialized data. If deserialized is null (e.g. empty file) fall back to empty
                // dictionary.
                FileHashes = deserialized != null
                    ? new System.Collections.Concurrent.ConcurrentDictionary<string, string>(deserialized)
                    : new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            }
        }
        #endregion

        #region Static Methods - Thread Management
        public static void PoolMod(string thread, bool add)
        {
            // Add or remove a thread name in a thread‑safe manner. ConcurrentQueue does not support
            // removal directly, so removal is performed by recreating the queue. A lock is used only
            // during the swap to avoid races with other removals.
            if (add)
            {
                ThreadList.Enqueue(thread);
            }
            else
            {
                lock (ThreadListLock)
                {
                    var newQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    while (ThreadList.TryDequeue(out var item))
                    {
                        if (!string.Equals(item, thread, StringComparison.OrdinalIgnoreCase))
                        {
                            newQueue.Enqueue(item);
                        }
                    }
                    ThreadList = newQueue;
                }
            }
            ShowPoolThreads();
        }

        public static void ShowPoolThreads()
        {
            // If a UI control is bound, clear it; then write thread names to the console. Because
            // ConcurrentQueue preserves insertion order, this enumerates threads in the order they
            // were added. Note: enumeration is snapshot based and safe across threads.
            ProgressOvLk?.Clear();
            Console.WriteLine("Threads:\n");
            foreach (var thread in ThreadList)
            {
                Console.WriteLine(thread + "\n");
            }
        }
        #endregion

        #region Static Methods - Delta Management
        public static void addsingledelta(string key, string value)
        {
            // Add or update the value for the given key. Using AddOrUpdate ensures that
            // concurrent writers do not throw. The existing value will be replaced if the
            // key already exists.
            singleStoredDelta.AddOrUpdate(key, value, (_, __) => value);
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
