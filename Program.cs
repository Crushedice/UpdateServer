using System;
using System.IO;
using System.Windows.Forms;

namespace UpdateServer
{
    public static class FileLogger
    {
        public static void CLog(string message, string Loc)
        {
            string timestamp = DateTime.Now.ToString("MM/dd HH:mm");
            using (StreamWriter streamWriter = new StreamWriter(Loc, true))
            {
                streamWriter.Write(timestamp + message + "\n");
                streamWriter.Close();
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            new Heart();
            while (true)
            {
                ConsoleKeyInfo input = Console.ReadKey(false);

                if (input.Modifiers != ConsoleModifiers.Control) continue;

                if (input.Key == ConsoleKey.S) break;
            }
        }
    }
}