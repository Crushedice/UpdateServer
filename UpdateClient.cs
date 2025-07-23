using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UpdateServer
{
    public class UpdateClient
    {
        public Guid _guid;
        public string Clientdeltazip;
        public string ClientFolder;
        public string ClientIP;
        public List<string> dataToAdd = new List<string>();
        public List<string> dataToSend = new List<string>();
        public Dictionary<string, string> filedata = new Dictionary<string, string>();
        public List<string> filetoDelete = new List<string>();
        public Dictionary<string, string> MatchedDeltas = new Dictionary<string, string>();
        public Dictionary<string, string> missmatchedFilehashes = new Dictionary<string, string>();
        public string SignatureHash;
        public Dictionary<string, string> TrimmedFileHashes = new Dictionary<string, string>();

        UpdateClient()
        {

        }

        public UpdateClient(Guid guid, string cIP, string Folder, string deltazip)
        {
            _guid = guid;
            ClientIP = cIP;
            ClientFolder = Folder;
            Clientdeltazip = deltazip;
            ClearClientFolderIfNotEmpty();

        }

        public void AddMissMatchFilelist(Dictionary<string, string> nes)
        {
            lock (missmatchedFilehashes)
            {
                missmatchedFilehashes = nes;
            }
        }

        public Dictionary<string, string> GetTrimmedList()
        {
            lock (missmatchedFilehashes)
            {
                foreach (var m in missmatchedFilehashes)
                {
                    if (UpdateServerEntity.DeltaFileStorage.TryGetValue(m.Value, out var kk))
                    {
                        lock (MatchedDeltas)
                        {
                            MatchedDeltas.Add(kk.Keys.First(), kk.Values.First());
                        }
                    }
                    else
                    {
                        lock (TrimmedFileHashes)
                        {
                            TrimmedFileHashes.Add(m.Key, m.Value);
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

                    // Delete all subdirectories
                    string[] directories = Directory.GetDirectories(ClientFolder, "*", SearchOption.AllDirectories);
                    foreach (string dir in directories.OrderByDescending(d => d.Length)) // Delete deepest directories first
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing ClientFolder {ClientFolder}: {ex.Message}");
            }
        }
    }
}