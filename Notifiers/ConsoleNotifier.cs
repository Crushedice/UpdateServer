using System;
using System.Collections.Generic;
using UpdateServer.Interfaces;

namespace UpdateServer.Notifiers
{
    public class ConsoleNotifier : IUINotifier
    {
        public void UpdateClientStatus(string ipPort, int indicator, string PFiles = "")
        {
            string name = ipPort.Split(':')[0].Replace('.', '0');
            string status = "";

            switch (indicator)
            {
                case 1:
                    status = "started updating";
                    break;
                case 2:
                    status = "finished updating";
                    break;
                case 3:
                    status = $"progress: {PFiles}";
                    break;
            }
            Console.WriteLine($"Client {name} {status}");
        }

        public void LogMessage(string message)
        {
            Console.WriteLine(message);
        }

        public void ShowPoolThreads(List<string> threadList)
        {
            Console.WriteLine("Threads in Pool:");
            foreach (string thread in threadList)
            {
                Console.WriteLine($"- {thread}");
            }
        }
    }
}