using System;
using System.Collections.Generic;
using System.Linq;

namespace UpdateServer
{
    public class UpdateClient
    {
        public string ClientIP;
        public string ClientFolder;
        public string Clientdeltazip;
        public string SignatureHash;
        public Dictionary<string, string> filedata = new Dictionary<string, string>();
        public List<string> dataToSend = new List<string>();
        public List<string> dataToAdd = new List<string>();
        public List<string> filetoDelete = new List<string>();

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
