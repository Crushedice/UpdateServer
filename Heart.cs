using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Sentry;

namespace UpdateServer
{
    public class Heart
    {
        #region Thread-Safe Locks
        private static readonly object FileHashesLock = new();
        #endregion

        #region Static Properties and Fields
        // Use thread‑safe collections for shared mutable state.
        public static System.Collections.Concurrent.ConcurrentDictionary<string, string> FileHashes = new();
        public static string Vversion = string.Empty;
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
            if (File.Exists("Version.txt"))
            {
                Vversion = File.ReadAllText("Version.txt").Trim();
            }
            else
            {
                Console.WriteLine("Error - Version File Not Found!!!");
                Vversion = "2318";
            }
        }

        private void StartServer()
        {
            // Startup is deliberately synchronous: the update server must not begin
            // accepting clients before the hash list is fully loaded, and startup
            // failures must surface instead of dying inside an async void method.
            try
            {
                InitializeHashes();
                Console.WriteLine("Heart Started. Starting update server...");
                _server = new UpdateServerEntity();
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                Console.WriteLine($"Fatal error during server startup: {ex.Message}");
                throw;
            }
        }

        private void InitializeHashes()
        {
            if (!File.Exists("Hashes.json"))
            {
                new HashGen().Run().GetAwaiter().GetResult();
            }

            LoadHashesFromFile();
        }

        private void LoadHashesFromFile()
        {
            lock (FileHashesLock)
            {
                if (!File.Exists("Hashes.json"))
                {
                    Console.WriteLine("Hashes.json not found - starting with an empty hash list.");
                    FileHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                    return;
                }

                var hashes = File.ReadAllText("Hashes.json");
                var deserialized = JsonConvert.DeserializeObject<Dictionary<string, string>>(hashes);
                // Replace the concurrent dictionary with a new concurrent instance based on the
                // deserialized data. If deserialized is null (e.g. empty file) fall back to empty
                // dictionary.
                FileHashes = deserialized != null
                    ? new System.Collections.Concurrent.ConcurrentDictionary<string, string>(deserialized)
                    : new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            }
        }
        #endregion
    }
}
