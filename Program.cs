using Sentry;
using Sentry.Extensibility;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using UpdateServer.Classes;

namespace UpdateServer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Load Sentry configuration
            var sentryConfig = SentryConfig.Instance;

            // Ensure Sentry events are flushed on exit
            using (SentrySdk.Init(o =>
            {
                o.Dsn = sentryConfig.Dsn;
                
                o.Environment = sentryConfig.Environment;
                //o.Release = sentryConfig.Release;
                o.Debug = sentryConfig.Debug;
                o.TracesSampleRate = sentryConfig.TracesSampleRate;
                o.SendClientReports = true;
                o.AutoSessionTracking = true;
                o.SendDefaultPii = true;
                o.AddDiagnosticSourceIntegration();
                o.AttachStacktrace = true;
                
                o.DiagnosticLevel = sentryConfig.Debug ? SentryLevel.Debug : SentryLevel.Error;
                o.SetBeforeSend((@event, _) =>
                {
                    if (@event.Exception != null)
                    {
                        var ex = @event.Exception;

                        // Exception Data
                        if (ex.Data != null)
                        {
                            foreach (System.Collections.DictionaryEntry de in ex.Data)
                            {
                                @event.SetExtra($"Exception.Data.{de.Key}", de.Value);
                            }
                        }
                        
                        // Exception Type, Message, StackTrace
                        @event.SetExtra("Exception Type", ex.GetType().FullName);
                        @event.SetExtra("Exception Message", ex.Message);
                        @event.SetExtra("Exception StackTrace", ex.StackTrace);

                        // Inner Exception(s)
                        var inner = ex.InnerException;
                        int innerLevel = 1;
                        while (inner != null)
                        {
                            @event.SetExtra($"InnerException[{innerLevel}].Type", inner.GetType().FullName);
                            @event.SetExtra($"InnerException[{innerLevel}].Message", inner.Message);
                            @event.SetExtra($"InnerException[{innerLevel}].StackTrace", inner.StackTrace);
                            if (inner.Data != null)
                            {
                                foreach (System.Collections.DictionaryEntry de in inner.Data)
                                {
                                    @event.SetExtra($"InnerException[{innerLevel}].Data.{de.Key}", de.Value);
                                }
                            }
                            inner = inner.InnerException;
                            innerLevel++;
                        }

                        // Environment Info
                        @event.SetExtra("MachineName", Environment.MachineName);
                        @event.SetExtra("OSVersion", Environment.OSVersion.ToString());
                        @event.SetExtra("UserName", Environment.UserName);
                        @event.SetExtra("CurrentDirectory", Environment.CurrentDirectory);
                        @event.SetExtra("ProcessId", Process.GetCurrentProcess().Id);
                        @event.SetExtra("ProcessName", Process.GetCurrentProcess().ProcessName);
                        @event.SetExtra("AppDomain", AppDomain.CurrentDomain.FriendlyName);
                        @event.SetExtra("CommandLine", Environment.CommandLine);
                        @event.SetExtra("CLRVersion", Environment.Version.ToString());

                        // Memory Info
                        @event.SetExtra("WorkingSet", Process.GetCurrentProcess().WorkingSet64);
                        @event.SetExtra("PrivateMemorySize", Process.GetCurrentProcess().PrivateMemorySize64);
                        @event.SetExtra("VirtualMemorySize", Process.GetCurrentProcess().VirtualMemorySize64);

                        // Loaded Assemblies
                        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                        var loadedAssemblies = string.Join("; ", assemblies.Select(a => $"{a.GetName().Name} v{a.GetName().Version}"));
                        @event.SetExtra("LoadedAssemblies", loadedAssemblies);
                    }
                    return @event;
                });
            }))
            {
                
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                SentrySdk.AddBreadcrumb("Application started", "app.start");

                _ = new Heart();
                try
                {
                    while (true)
                    {
                        ConsoleKeyInfo input = Console.ReadKey(false);
                        if (input.Modifiers != ConsoleModifiers.Control) continue;
                        if (input.Key == ConsoleKey.S) break;
                    }
                }
                finally
                {
                    // Ensure exit breadcrumb is always sent
                    SentrySdk.AddBreadcrumb("Application exiting normally", "app.exit");
                }
            }
        }
    }
}
