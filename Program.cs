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
        
        // [DebuggerStepThrough]
        [STAThread]
        private static void Main(string[] args)
        {
            // Initialize Sentry SDK
            SentrySdk.Init(o =>
            {
                
                o.Dsn = "https://f1a2aed8eae385969e335e8f83a5e7f8@logging.rusticaland.ovh/2";
                o.Debug = false;
                o.TracesSampleRate = 1.0;
                o.SendClientReports = true;
                o.AutoSessionTracking = true;
                o.SendDefaultPii = true;
                o.IsGlobalModeEnabled = true;
                o.AddDiagnosticSourceIntegration();
                o.ProfilesSampleRate = 1.0;
                o.AttachStacktrace = true;
                o.DiagnosticLevel = SentryLevel.Debug;
                o.SetBeforeSend((@event, _) =>
                    {
                        // Drop an event altogether:

                        if (@event.Exception != null)
                        {
                            var ex = @event.Exception;
                            StackTrace st = new StackTrace(ex, true);
                            StackFrame frame = st.GetFrame(0);
                            int line = frame.GetFileLineNumber();
                            SentrySdk.ConfigureScope(s =>
                            {
                                s.SetExtra("Exception Type", ex.GetType().Name);
                                s.SetExtra("Exception Message", ex.Message);
                                s.SetExtra("Exception StackTrace", ex.StackTrace);
                            });


                        }

                        return @event;
                    }
                );
            });
           

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            SentrySdk.AddBreadcrumb("Application started", "app.start");
            SentrySdk.CaptureMessage("Application started", SentryLevel.Info);

            _=new Heart();
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
