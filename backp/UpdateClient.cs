using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UpdateServer
{
    public class UpdateClient
    {
        public string ClientIP;
        public string ClientFolder;
        public Dictionary<string,string> filedata = new Dictionary<string, string>();
        public Dictionary<string,string> dataToSend = new Dictionary<string, string>();

        public UpdateClient()
        {
        }

        public UpdateClient(string cIP,string Folder)
        {

            ClientIP = cIP;
            ClientFolder = Folder;

        }
    }
}
