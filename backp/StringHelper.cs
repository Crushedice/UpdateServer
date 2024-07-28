using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace StringHelp
{
    static class StringHelper
    {
        public static string AddQuotesIfRequired(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                ? path.Contains(" ") && (!path.StartsWith("\"") && !path.EndsWith("\"")) ? "\"" + path + "\"" : path
                : string.Empty;
        }
    }
}

