using Sentry;
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
		static void Main(string[] args)
		{


            // Set the unhandled exception mode to force all Windows Forms errors to go through
            // our handler.
            SentrySdk.Init(o =>
            {
                // NOTE: Change the URL below to your own DSN. Get it on sentry.io in the project settings (or when you create a new project):
                o.Dsn = "https://620a6481f9d042c6936edd0dc6f7901b@sentry.rusticaland.net/4";
                // When configuring for the first time, to see what the SDK is doing:
                o.Debug = false;
                // Set traces_sample_rate to 1.0 to capture 100% of transactions for performance monitoring.
                // We recommend adjusting this value in production.
                o.TracesSampleRate = 1.0;
                o.AttachStacktrace = true;
                o.AddDiagnosticSourceIntegration();
                o.AutoSessionTracking = true;
                o.SampleRate = (float?)1;
                o.MaxBreadcrumbs = 150;
                o.SendDefaultPii = true;
                o.StackTraceMode = StackTraceMode.Enhanced;
                o.DiagnosticLevel = SentryLevel.Debug;
                o.Release = "1.2.1.5";
               
            });

          
            


            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

            Application.ThreadException += Application_ThreadException;
            // Application.EnableVisualStyles();
            // Application.SetCompatibleTextRenderingDefault(true);
            new Heart();
		   while (true)
		   {
			   // DO NOT INTERCEPT KEY PRESSES! 
			   //IF CTRL+S IS FORWARDED TO THE CONSOLE APP, WEIRD THINGS WILL HAPPEN.
			   ConsoleKeyInfo input = Console.ReadKey(false);

			   if (input.Modifiers != ConsoleModifiers.Control)
			   {
				   continue;
			   }

			   if (input.Key == ConsoleKey.S)
			   {
				   break;
			   }
		   }
        }

        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            SentrySdk.CaptureException(e.Exception);
        }

        // Handle the UI exceptions by showing a dialog box, and asking the user whether
        // or not they wish to abort execution.
        // NOTE: This exception cannot be kept from terminating the application - it can only 
        // log the event, and inform the user about it. 
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            SentrySdk.CaptureException((Exception)e.ExceptionObject);
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
            var timestamp = DateTime.Now.ToString("MM/dd HH:mm");
            using (var streamWriter = new StreamWriter(Loc, true))
            {
                streamWriter.Write(timestamp + message + "\n");
                streamWriter.Close();
            }
        }
        
    }
}
