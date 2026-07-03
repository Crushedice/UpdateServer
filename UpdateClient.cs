using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UpdateServer
{
    public class UpdateClient
    {
        private readonly object _dataToAddLock = new object();
        private readonly object _dataToSendLock = new object();
        private readonly object _fileToDeleteLock = new object();
        private readonly object _matchedDeltasLock = new object();
        private readonly object _mismatchedFileHashesLock = new object();
        private readonly object _trimmedFileHashesLock = new object();

        #region Public Fields
        /// <summary>
        /// Unique identifier of the client. Exposed as Guid instead of mutable field.
        /// </summary>
        public Guid Guid { get; private set; }

        /// <summary>
        /// The path where the delta ZIP file will be written for this client.
        /// </summary>
        public string ClientDeltaZip { get; private set; }

        /// <summary>
        /// Folder assigned to this client on the server.
        /// </summary>
        public string ClientFolder { get; private set; }

        /// <summary>
        /// Remote client IP/port string.
        /// </summary>
        public string ClientIp { get; private set; }

        /// <summary>
        /// Optional signature hash used to cache previously generated deltas.
        /// </summary>
        public string SignatureHash { get; set; } = string.Empty;
        #endregion

        #region Collections
        // All access goes through the locked accessor methods below.
        private List<string> dataToAdd = new List<string>();
        private readonly List<string> dataToSend = new List<string>();
        private readonly List<string> filetoDelete = new List<string>();
        private readonly Dictionary<string, string> MatchedDeltas = new Dictionary<string, string>();
        private readonly Dictionary<string, string> missmatchedFilehashes = new Dictionary<string, string>();
        private readonly Dictionary<string, string> TrimmedFileHashes = new Dictionary<string, string>();
        #endregion

        #region Constructors
        private UpdateClient()
        {
            // Private constructor to prevent parameterless instantiation

        }

        public UpdateClient(Guid guid, string clientIp, string folder, string deltaZip)
        {
            Guid = guid;
            ClientIp = clientIp;
            ClientFolder = folder;
            ClientDeltaZip = deltaZip;
            ClearClientFolderIfNotEmpty();
        }
        #endregion

        #region Public Methods
        public void AddMissMatchFilelist(Dictionary<string, string> mismatchedFiles)
        {
            if (mismatchedFiles == null)
                throw new ArgumentNullException(nameof(mismatchedFiles));

            lock (_mismatchedFileHashesLock)
            {
                missmatchedFilehashes.Clear();
                foreach (var kvp in mismatchedFiles)
                {
                    missmatchedFilehashes[kvp.Key] = kvp.Value;
                }
            }
        }

        public Dictionary<string, string> GetTrimmedList()
        {
            Dictionary<string, string> localCopy;
            lock (_mismatchedFileHashesLock)
            {
                localCopy = new Dictionary<string, string>(missmatchedFilehashes);
            }
            lock (_matchedDeltasLock)
            {
                MatchedDeltas.Clear();
            }
            lock (_trimmedFileHashesLock)
            {
                TrimmedFileHashes.Clear();
            }
            foreach (var kvp in localCopy)
            {
                bool matched = false;
                if (UpdateServerEntity.DeltaFileStorage.TryGetValue(kvp.Value, out var deltaInfo) && deltaInfo.Count > 0)
                {
                    var key = deltaInfo.Keys.First();
                    var value = deltaInfo.Values.First();
                    if (File.Exists(Path.Combine(UpdateServerEntity.DeltaStorage, key)))
                    {
                        lock (_matchedDeltasLock)
                        {
                            if (!MatchedDeltas.ContainsKey(key))
                            {
                                MatchedDeltas.Add(key, value);
                            }
                        }
                        matched = true;
                    }
                    else
                    {
                        // The stored delta was purged from disk (e.g. by the size cleanup)
                        // but the index still points at it. Drop the stale entry and fall
                        // through so the delta gets regenerated instead of silently skipped.
                        UpdateServerEntity.DeltaFileStorage.TryRemove(kvp.Value, out _);
                    }
                }

                if (!matched)
                {
                    lock (_trimmedFileHashesLock)
                    {
                        if (!TrimmedFileHashes.ContainsKey(kvp.Key))
                        {
                            TrimmedFileHashes.Add(kvp.Key, kvp.Value);
                        }
                    }
                }
            }
            lock (_trimmedFileHashesLock)
            {
                return new Dictionary<string, string>(TrimmedFileHashes);
            }
        }

        public Dictionary<string, string> GetMatchedDeltasSnapshot()
        {
            lock (_matchedDeltasLock)
            {
                return new Dictionary<string, string>(MatchedDeltas);
            }
        }

        public int GetMatchedDeltasCount()
        {
            lock (_matchedDeltasLock)
            {
                return MatchedDeltas.Count;
            }
        }

        public int GetMismatchedFileHashesCount()
        {
            lock (_mismatchedFileHashesLock)
            {
                return missmatchedFilehashes.Count;
            }
        }

        public int GetTrimmedFileHashesCount()
        {
            lock (_trimmedFileHashesLock)
            {
                return TrimmedFileHashes.Count;
            }
        }

        public Dictionary<string, string> GetTrimmedFileHashesSnapshot()
        {
            lock (_trimmedFileHashesLock)
            {
                return new Dictionary<string, string>(TrimmedFileHashes);
            }
        }

        public Dictionary<string, string> GetMismatchedFileHashesSnapshot()
        {
            lock (_mismatchedFileHashesLock)
            {
                return new Dictionary<string, string>(missmatchedFilehashes);
            }
        }

        public string GetMismatchedFileHash(string key)
        {
            lock (_mismatchedFileHashesLock)
            {
                return missmatchedFilehashes.TryGetValue(key, out var value) ? value : null;
            }
        }

        public void SetDataToAdd(IEnumerable<string> items)
        {
            lock (_dataToAddLock)
            {
                dataToAdd = items?.ToList() ?? new List<string>();
            }
        }

        public List<string> GetDataToAddSnapshot()
        {
            lock (_dataToAddLock)
            {
                return new List<string>(dataToAdd);
            }
        }

        public void AddDataToSend(string path)
        {
            lock (_dataToSendLock)
            {
                dataToSend.Add(path);
            }
        }

        public List<string> GetDataToSendSnapshot()
        {
            lock (_dataToSendLock)
            {
                return new List<string>(dataToSend);
            }
        }

        public int GetDataToSendCount()
        {
            lock (_dataToSendLock)
            {
                return dataToSend.Count;
            }
        }

        public void AddFileToDelete(string path)
        {
            lock (_fileToDeleteLock)
            {
                filetoDelete.Add(path);
            }
        }

        public List<string> GetFilesToDeleteSnapshot()
        {
            lock (_fileToDeleteLock)
            {
                return new List<string>(filetoDelete);
            }
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
#if DEBUG
                throw;
#endif
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
#if DEBUG
                        throw;
#endif
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
#if DEBUG
                    throw;
#endif
                }
            }
        }
        #endregion
    }
}
