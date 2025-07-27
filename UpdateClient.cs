using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UpdateServer
{
    public class UpdateClient
    {
        #region Public Fields
        public Guid _guid;
        public string Clientdeltazip;
        public string ClientFolder;
        public string ClientIP;
        public string SignatureHash;
        #endregion

        #region Collections
        public List<string> dataToAdd = new List<string>();
        public List<string> dataToSend = new List<string>();
        public List<string> filetoDelete = new List<string>();
        public Dictionary<string, string> filedata = new Dictionary<string, string>();
        public Dictionary<string, string> MatchedDeltas = new Dictionary<string, string>();
        public Dictionary<string, string> missmatchedFilehashes = new Dictionary<string, string>();
        public Dictionary<string, string> TrimmedFileHashes = new Dictionary<string, string>();
        #endregion

        #region Constructors
        private UpdateClient()
        {
            // Private constructor to prevent parameterless instantiation
        }

        public UpdateClient(Guid guid, string clientIP, string folder, string deltazip)
        {
            _guid = guid;
            ClientIP = clientIP;
            ClientFolder = folder;
            Clientdeltazip = deltazip;
            ClearClientFolderIfNotEmpty();
        }
        #endregion

        #region Public Methods
        public void AddMissMatchFilelist(Dictionary<string, string> mismatchedFiles)
        {
            lock (missmatchedFilehashes)
            {
                missmatchedFilehashes = mismatchedFiles;
            }
        }

        public Dictionary<string, string> GetTrimmedList()
        {
            lock (missmatchedFilehashes)
            {
                foreach (var mismatch in missmatchedFilehashes)
                {
                    if (UpdateServerEntity.DeltaFileStorage.TryGetValue(mismatch.Value, out var deltaInfo))
                    {
                        lock (MatchedDeltas)
                        {
                            MatchedDeltas.Add(deltaInfo.Keys.First(), deltaInfo.Values.First());
                        }
                    }
                    else
                    {
                        lock (TrimmedFileHashes)
                        {
                            TrimmedFileHashes.Add(mismatch.Key, mismatch.Value);
                        }
                    }
                }
            }
            return TrimmedFileHashes;
        }

        /// <summary>
        /// Checks if the ClientFolder has any files and deletes them if found
        /// </summary>
        public void ClearClientFolderIfNotEmpty()
        {
            if (string.IsNullOrEmpty(ClientFolder) || !Directory.Exists(ClientFolder))
                return;

            try
            {
                ClearFiles();
                ClearDirectories();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing ClientFolder {ClientFolder}: {ex.Message}");
            }
        }
        #endregion

        #region Private Helper Methods
        private void ClearFiles()
        {
            // Get all files in the ClientFolder and subdirectories
            string[] files = Directory.GetFiles(ClientFolder, "*", SearchOption.AllDirectories);
            
            if (files.Length > 0)
            {
                // Delete all files
                foreach (string file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        // Log the error but continue with other files
                        Console.WriteLine($"Failed to delete file {file}: {ex.Message}");
                    }
                }
            }
        }

        private void ClearDirectories()
        {
            // Delete all subdirectories
            string[] directories = Directory.GetDirectories(ClientFolder, "*", SearchOption.AllDirectories);
            
            // Order by length descending to delete deepest directories first
            foreach (string dir in directories.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, false); // Only delete if empty
                }
                catch (Exception ex)
                {
                    // Log the error but continue
                    Console.WriteLine($"Failed to delete directory {dir}: {ex.Message}");
                }
            }
        }
        #endregion
    }
}