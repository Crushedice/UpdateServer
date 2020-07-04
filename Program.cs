using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace UpdateServer
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        // [SecurityPermission(SecurityAction.Demand, Flags = SecurityPermissionFlag.ControlAppDomain)]
        static void Main()
        {
            CosturaUtility.Initialize();

            // Set the unhandled exception mode to force all Windows Forms errors to go through
            // our handler.

#if DEBUG
          //  Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
          //  AppDomain.CurrentDomain.UnhandledException += 
          //      new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
#endif

            Application.EnableVisualStyles();
           // Application.SetCompatibleTextRenderingDefault(true);
            Application.Run(new Form1());
        }

        // Handle the UI exceptions by showing a dialog box, and asking the user whether
        // or not they wish to abort execution.
        // NOTE: This exception cannot be kept from terminating the application - it can only 
        // log the event, and inform the user about it. 
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            //  try
            //  {
            //      Exception ex = (Exception)e.ExceptionObject;
            //      string errorMsg = "An application error occurred. Please contact the adminstrator " +
            //          "with the following information:\n\n";
            //
            //      // Since we can't prevent the app from terminating, log this to the event log.
            //      if (!EventLog.SourceExists("ThreadException"))
            //      {
            //          EventLog.CreateEventSource("ThreadException", "Application");
            //      }
            //
            //      // Create an EventLog instance and assign its source.
            //      FileLogger.Log(errorMsg + ex.Message + "\n\nStack Trace:\n" + ex.StackTrace);
            //  
            //  }
            //  catch 
            //  {
            //      
            //  }
        }

        // Creates the error message and displays it.
        private static DialogResult ShowThreadExceptionDialog(string title, Exception e)
        {
            string errorMsg = "An application error occurred. Please contact the adminstrator " +
                "with the following information:\n\n";
            errorMsg = errorMsg + e.Message + "\n\nStack Trace:\n" + e.StackTrace;
            return MessageBox.Show(errorMsg, title, MessageBoxButtons.AbortRetryIgnore,
                MessageBoxIcon.Stop);
        }
    }
    public static class FileLogger
    {
        public static string filePath = "Log.txt";

        public static void Log(string message)
        {
            using (var streamWriter = new StreamWriter(filePath, true))
            {
                streamWriter.Write(message + "\n");
                streamWriter.Close();
            }
        }
        public static void CLog(string message, string Loc)
        {
            using (var streamWriter = new StreamWriter(Loc, true))
            {
                streamWriter.Write(message + "\n");
                streamWriter.Close();
            }
        }
    }
}
