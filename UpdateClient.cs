using System.Collections.Generic;

namespace UpdateServer
{
    public class UpdateClient
    {
        public string Clientdeltazip;
        public string ClientFolder;
        public string ClientIP;
        public List<string> dataToAdd = new List<string>();
        public List<string> dataToSend = new List<string>();
        public Dictionary<string, string> filedata = new Dictionary<string, string>();
        public List<string> filetoDelete = new List<string>();
        public string SignatureHash;

        public UpdateClient()
        {
        }

        public UpdateClient(string cIP, string Folder)
        {
            ClientIP = cIP;
            ClientFolder = Folder;
        }
    }
}