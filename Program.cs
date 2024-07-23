using System;
using System.IO;
using System.Windows.Forms;

namespace UpdateServer
{
    public static class FileLogger
    {
        public static string filePath = "Log.txt";

        public static void CLog(string message, string Loc)
        {
            string timestamp = DateTime.Now.ToString("MM/dd HH:mm");
            using (StreamWriter streamWriter = new StreamWriter(Loc, true))
            {
                streamWriter.Write(timestamp + message + "\n");
                streamWriter.Close();
            }
        }

        public static void Log(string message)
        {
            using (StreamWriter streamWriter = new StreamWriter(filePath, true))
            {
                streamWriter.Write(message + "\n");
                streamWriter.Close();
            }
        }
    }

    internal static class Program
    {
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            FileLogger.CLog(e.ToString(), "Exception.log");
        }

        /// <summary>
        ///     The main entry point for the application.
        /// </summary>
        [STAThread]
        private static void Main(string[] args)
        {
            // Set the unhandled exception mode to force all Windows Forms errors to go through
            // our handler.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // Application.EnableVisualStyles();
            // Application.SetCompatibleTextRenderingDefault(true);
            new Heart();
            while (true)
            {
                // DO NOT INTERCEPT KEY PRESSES!
                //IF CTRL+S IS FORWARDED TO THE CONSOLE APP, WEIRD THINGS WILL HAPPEN.
                ConsoleKeyInfo input = Console.ReadKey(false);

                if (input.Modifiers != ConsoleModifiers.Control) continue;

                if (input.Key == ConsoleKey.S) break;
            }
        }

        // Handle the UI exceptions by showing a dialog box, and asking the user whether
        // or not they wish to abort execution.
        // NOTE: This exception cannot be kept from terminating the application - it can only
        // log the event, and inform the user about it.

        // Creates the error message and displays it.
    }
}