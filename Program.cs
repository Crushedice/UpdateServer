using Sentry;
using Sentry.Extensibility;
using System;
using System.Diagnostics;
using System.Windows.Forms;
using UpdateServer.Classes;

namespace UpdateServer
{
    internal static class Program
    {
        

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // Get stack trace for the exception with source file information
            Exception ex = e.ExceptionObject as Exception;
           // StackTrace st = new StackTrace(e.ExceptionObject as Exception, true);
          //  StackFrame frame = st.GetFrame(0);
          //  int line = frame.GetFileLineNumber();
            
            
            SentrySdk.CaptureException(ex, scope =>
            {
                StackTrace st = new StackTrace(e.ExceptionObject as Exception, true);
                StackFrame frame = st.GetFrame(0);
                int line = frame.GetFileLineNumber();
                scope.SetExtra("Exception Line", line);
                scope.SetExtra("Exception Message", ex.Message);
                scope.SetExtra("Exception StackTrace", ex.StackTrace);

            });



        }

        // [DebuggerStepThrough]
        [STAThread]
        private static void Main(string[] args)
        {
            // Initialize Sentry SDK
            SentrySdk.Init(o =>
            {
                // Tells which project in Sentry to send events to:
                o.Dsn = "https://f1a2aed8eae385969e335e8f83a5e7f8@logging.rusticaland.ovh/2";
                //o.Dsn = "http://37fb8cce191246fd8e59806e5fdb1510:3fc984c23fdc4d1bbfe3aa1dfe5ff674@100.83.135.101:9000/3";
                // When configuring for the first time, to see what the SDK is doing:
                o.Debug = false;
                // Set TracesSampleRate to 1.0 to capture 100% of transactions for performance monitoring.
                // We recommend adjusting this value in production.
                o.TracesSampleRate = 1.0;
                o.SendClientReports = true;
                o.AutoSessionTracking = true;
                o.SendDefaultPii = true;
                o.IsGlobalModeEnabled = true;
                o.AddDiagnosticSourceIntegration();
                o.ProfilesSampleRate = 1.0;
                o.AttachStacktrace = true;
                o.DiagnosticLevel = SentryLevel.Debug;
            });


            // Configure WinForms to throw exceptions so Sentry can capture them.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            //SentrySdk.CauseCrash(CrashType.Managed); 
            // Add breadcrumb to track application start
            SentrySdk.AddBreadcrumb("Application started", "app.start");
            SentrySdk.CaptureMessage("Application started", SentryLevel.Info);

            new Heart();
            while (true)
            {

                ConsoleKeyInfo input = Console.ReadKey(false);

                if (input.Modifiers != ConsoleModifiers.Control) continue;

                if (input.Key == ConsoleKey.S) break;
            }
            

                // Add breadcrumb when application exits normally
                SentrySdk.AddBreadcrumb("Application exiting normally", "app.exit");
                
            
        }
    }
}
