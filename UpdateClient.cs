using System;
using System.Collections.Generic;
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

        public UpdateClient()
        {

        }

        public UpdateClient(Guid guid, string cIP, string Folder, string deltazip)
        {
            _guid = guid;
            ClientIP = cIP;
            ClientFolder = Folder;
            Clientdeltazip = deltazip;
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
    }
}