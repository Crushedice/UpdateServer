using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using Sentry;
using Sentry.Extensibility;
using UpdateServer.Classes;
using UpdateServer;

// --- Begin Sentry Feedback Link Model ---
namespace UpdateServer
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Globalization;

    public partial class Welcome
    {
        [JsonPropertyName("fields")]
        public Fields Fields { get; set; }
        [JsonPropertyName("issueId")]
        public long IssueId { get; set; }
        [JsonPropertyName("installationId")]
        public Guid InstallationId { get; set; }
        [JsonPropertyName("webUrl")]
        public Uri WebUrl { get; set; }
        [JsonPropertyName("project")]
        public Project Project { get; set; }
        [JsonPropertyName("actor")]
        public Actor Actor { get; set; }
        public static Welcome FromJson(string json) => JsonSerializer.Deserialize<Welcome>(json, Converter.Settings);
    }
    public partial class Actor
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }
        [JsonPropertyName("id")]
        public long Id { get; set; }
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }
    public partial class Fields
    {
        [JsonPropertyName("userID")]
        public Guid UserId { get; set; }
        [JsonPropertyName("reply")]
        public string Reply { get; set; }
    }
    public partial class Project
    {
        [JsonPropertyName("slug")]
        public string Slug { get; set; }
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }
    public static class Serialize
    {
        public static string ToJson(this Welcome self) => JsonSerializer.Serialize(self, Converter.Settings);
    }
    internal static class Converter
    {
        public static readonly JsonSerializerOptions Settings = new(JsonSerializerDefaults.General)
        {
            Converters =
            {
                IsoDateTimeOffsetConverter.Singleton
            },
        };
    }
    internal class IsoDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override bool CanConvert(Type t) => t == typeof(DateTimeOffset);
        private const string DefaultDateTimeFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK";
        private DateTimeStyles _dateTimeStyles = DateTimeStyles.RoundtripKind;
        private string? _dateTimeFormat;
        private CultureInfo? _culture;
        public DateTimeStyles DateTimeStyles
        {
            get => _dateTimeStyles;
            set => _dateTimeStyles = value;
        }
        public string? DateTimeFormat
        {
            get => _dateTimeFormat ?? string.Empty;
            set => _dateTimeFormat = (string.IsNullOrEmpty(value)) ? null : value;
        }
        public CultureInfo Culture
        {
            get => _culture ?? CultureInfo.CurrentCulture;
            set => _culture = value;
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            string text;
            if ((_dateTimeStyles & DateTimeStyles.AdjustToUniversal) == DateTimeStyles.AdjustToUniversal
                    || (_dateTimeStyles & DateTimeStyles.AssumeUniversal) == DateTimeStyles.AssumeUniversal)
            {
                value = value.ToUniversalTime();
            }
            text = value.ToString(_dateTimeFormat ?? DefaultDateTimeFormat, Culture);
            writer.WriteStringValue(text);
        }
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? dateText = reader.GetString();
            if (string.IsNullOrEmpty(dateText) == false)
            {
                if (!string.IsNullOrEmpty(_dateTimeFormat))
                {
                    return DateTimeOffset.ParseExact(dateText, _dateTimeFormat, Culture, _dateTimeStyles);
                }
                else
                {
                    return DateTimeOffset.Parse(dateText, Culture, _dateTimeStyles);
                }
            }
            else
            {
                return default(DateTimeOffset);
            }
        }
        public static readonly IsoDateTimeOffsetConverter Singleton = new IsoDateTimeOffsetConverter();
    }
}
// --- End Sentry Feedback Link Model ---

namespace UpdateServer
{
    internal static class Program
    {
        private static HttpListener httpListener;

        #region Entry Point
        [STAThread]
        private static void Main(string[] args)
        {
            // Load Sentry configuration
            var sentryConfig = SentryConfig.Instance;

            // Ensure Sentry events are flushed on exit
            using (SentrySdk.Init(ConfigureSentry))
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                SentrySdk.AddBreadcrumb("Application started", "app.start");

                _ = new Heart();
                
                // Start the HTTP server for API
                StartHttpServer();
                
                try
                {
                    RunApplicationLoop();
                }
                finally
                {
                    // Stop HTTP server
                    StopHttpServer();
                    // Ensure exit breadcrumb is always sent
                    SentrySdk.AddBreadcrumb("Application exiting normally", "app.exit");
                }
            }
        }
        #endregion

        #region Application Logic
        private static void RunApplicationLoop()
        {
            while (true)
            {
                ConsoleKeyInfo input = Console.ReadKey(false);
                if (input.Modifiers != ConsoleModifiers.Control) continue;
                if (input.Key == ConsoleKey.S) break;
            }
        }
        #endregion

        #region HTTP Server
        private static void StartHttpServer()
        {
            return;
            try
            {
                httpListener = new HttpListener();
                httpListener.Prefixes.Add("http://51.91.214.177:5500/");
                httpListener.Start();
                
                Console.WriteLine("Feedback API server started on http://51.91.214.177:5500/");
                SentrySdk.AddBreadcrumb("HTTP API server started", "api.start");
                
                // Start listening for requests in background
                Task.Run(HandleHttpRequests);
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                Console.WriteLine($"Failed to start HTTP server: {ex.Message}");
#if DEBUG
                throw;
#endif
            }
        }

        private static void StopHttpServer()
        {
            try
            {
                httpListener?.Stop();
                httpListener?.Close();
                Console.WriteLine("Feedback API server stopped");
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
#if DEBUG
                throw;
#endif
            }
        }

        private static async Task HandleHttpRequests()
        {
            while (httpListener.IsListening)
            {
                try
                {
                    var context = await httpListener.GetContextAsync();
                    Task.Run(() => ProcessRequest(context));
                }
                catch (HttpListenerException)
                {
                    // Expected when stopping the listener
                    break;
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
#if DEBUG
                    throw;
#endif
                }
            }
        }

        private static async Task ProcessRequest(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;
                string body = "";
                // Debug output for incoming API calls
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[API DEBUG] {DateTime.Now:HH:mm:ss} {request.HttpMethod} {request.Url}");
                if (request.HasEntityBody)
                {
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                         body = await reader.ReadToEndAsync();
                        Console.WriteLine($"Body: {body}");
                        File.AppendAllText("apilog.log", body + "\n \n");
                    }
                    if (request.InputStream.CanSeek)
                        request.InputStream.Position = 0;
                }
                Console.ResetColor();
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                string path = request.Url.AbsolutePath;
                string method = request.HttpMethod;

                // Only handle /sentry-panel/feedback-link POST
                if (path == "/sentry-panel/feedback-link" && method == "POST")
                {
                    await HandleSentryPanelLink(context, body);
                }
                // Handle GET /api/feedback/{feedbackId}/reply
                else if (method == "GET" && path.StartsWith("/api/feedback/") && path.EndsWith("/reply"))
                {
                    var feedbackId = path.Substring("/api/feedback/".Length);
                    feedbackId = feedbackId.Substring(0, feedbackId.Length - "/reply".Length);
                    await HandleFeedbackReplyGet(context, feedbackId);
                }
                else
                {
                    response.StatusCode = 404;
                    WriteResponse(response, "Not Found");
                }
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                context.Response.StatusCode = 500;
                WriteResponse(context.Response, "Internal Server Error");
#if DEBUG
                throw;
#endif
            }
        }

        private static async Task HandleSentryPanelLink(HttpListenerContext context,string body)
        {
            try
            {
               // var body = await ReadRequestBody(context.Request);
                if (string.IsNullOrWhiteSpace(body))
                {
                    Console.WriteLine("Received empty body for feedback-link POST");
                    context.Response.StatusCode = 400;
                    WriteResponse(context.Response, "Empty request body");
                    return;
                }
                Welcome welcome = null;
                try
                {
                    welcome = Welcome.FromJson(body);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    Console.WriteLine($"JSON error in feedback link: {ex.Message}");
                    context.Response.StatusCode = 400;
                    WriteResponse(context.Response, "Invalid JSON");
#if DEBUG
                    throw;
#endif
                    return;
                }
                if (welcome?.Fields == null || welcome.Fields.UserId == Guid.Empty || string.IsNullOrWhiteSpace(welcome.Fields.Reply))
                {
                    Console.WriteLine("Missing userID or reply in payload");
                    context.Response.StatusCode = 400;
                    WriteResponse(context.Response, "Missing userID or reply");
                    return;
                }
                Console.WriteLine($"Saving reply for userID {welcome.Fields.UserId}: {welcome.Fields.Reply}");
                SaveReply(welcome.Fields.UserId.ToString(), welcome.Fields.Reply);
                WriteJsonResponse(context.Response, new { message = "Antwort gespeichert!", status = "success" });
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                Console.WriteLine($"Error processing feedback link: {ex.Message}");
                context.Response.StatusCode = 500;
                WriteResponse(context.Response, "Internal Server Error");
#if DEBUG
                throw;
#endif
            }
        }

        private static async Task HandleFeedbackReplyGet(HttpListenerContext context, string feedbackId)
        {
            var replies = LoadReplies();
            var reply = replies.FirstOrDefault(r => r.FeedbackId == feedbackId);
            if (reply == null)
            {
                context.Response.StatusCode = 404;
                WriteResponse(context.Response, "Not Found");
                return;
            }
            if (!reply.Delivered)
            {
                reply.Delivered = true;
                SaveReplies(replies);
            }
            else
            {
                WriteResponse(context.Response, "Not Found");
                return;
            }
            WriteJsonResponse(context.Response, reply);
        }
        // Helper methods
        private static async Task<string> ReadRequestBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private static void WriteResponse(HttpListenerResponse response, string content)
        {
            var buffer = Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }

        private static void WriteJsonResponse(HttpListenerResponse response, object data)
        {
            response.ContentType = "application/json";
            var json = JsonSerializer.Serialize(data);
            WriteResponse(response, json);
        }
        #endregion

        #region Sentry Configuration
        private static void ConfigureSentry(SentryOptions options)
        {
            var sentryConfig = SentryConfig.Instance;
            
            options.Dsn = sentryConfig.Dsn;
            options.Environment = sentryConfig.Environment;
            options.Debug = sentryConfig.Debug;
            options.TracesSampleRate = sentryConfig.TracesSampleRate;
            options.SendClientReports = true;
            options.AutoSessionTracking = true;
            options.SendDefaultPii = true;
            options.AttachStacktrace = true;
            
            options.AddDiagnosticSourceIntegration();
            options.DiagnosticLevel = sentryConfig.Debug ? SentryLevel.Debug : SentryLevel.Error;
            options.SetBeforeSend(EnrichSentryEvent);
        }

        private static SentryEvent EnrichSentryEvent(SentryEvent @event, SentryHint hint)
        {
            if (@event.Exception == null) return @event;

            var ex = @event.Exception;

            // Exception Data
            AddExceptionData(@event, ex);
            
            // Exception Details
            AddExceptionDetails(@event, ex);

            // Inner Exceptions
            AddInnerExceptions(@event, ex);

            // Environment Info
            AddEnvironmentInfo(@event);

            // Memory Info
            AddMemoryInfo(@event);

            // Loaded Assemblies
            AddAssemblyInfo(@event);

            return @event;
        }
        #endregion

        #region Sentry Event Enrichment Methods
        private static void AddExceptionData(SentryEvent @event, Exception ex)
        {
            if (ex.Data != null)
            {
                foreach (System.Collections.DictionaryEntry de in ex.Data)
                {
                    @event.SetExtra($"Exception.Data.{de.Key}", de.Value);
                }
            }
        }

        private static void AddExceptionDetails(SentryEvent @event, Exception ex)
        {
            @event.SetExtra("Exception Type", ex.GetType().FullName);
            @event.SetExtra("Exception Message", ex.Message);
            @event.SetExtra("Exception StackTrace", ex.StackTrace);
        }

        private static void AddInnerExceptions(SentryEvent @event, Exception ex)
        {
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
        }

        private static void AddEnvironmentInfo(SentryEvent @event)
        {
            @event.SetExtra("MachineName", Environment.MachineName);
            @event.SetExtra("OSVersion", Environment.OSVersion.ToString());
            @event.SetExtra("UserName", Environment.UserName);
            @event.SetExtra("CurrentDirectory", Environment.CurrentDirectory);
            @event.SetExtra("ProcessId", Process.GetCurrentProcess().Id);
            @event.SetExtra("ProcessName", Process.GetCurrentProcess().ProcessName);
            @event.SetExtra("AppDomain", AppDomain.CurrentDomain.FriendlyName);
            @event.SetExtra("CommandLine", Environment.CommandLine);
            @event.SetExtra("CLRVersion", Environment.Version.ToString());
        }

        private static void AddMemoryInfo(SentryEvent @event)
        {
            var process = Process.GetCurrentProcess();
            @event.SetExtra("WorkingSet", process.WorkingSet64);
            @event.SetExtra("PrivateMemorySize", process.PrivateMemorySize64);
            @event.SetExtra("VirtualMemorySize", process.VirtualMemorySize64);
        }

        private static void AddAssemblyInfo(SentryEvent @event)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var loadedAssemblies = string.Join("; ", 
                assemblies.Select(a => $"{a.GetName().Name} v{a.GetName().Version}"));
            @event.SetExtra("LoadedAssemblies", loadedAssemblies);
        }
        #endregion

        private static void SaveReply(string id, string reply)
        {
            var replies = LoadReplies();
            var existing = replies.FirstOrDefault(r => r.FeedbackId == id);
            if (existing != null) replies.Remove(existing);

            replies.Add(new FeedbackReply
            {
                FeedbackId = id,
                Reply = reply,
                Timestamp = DateTime.UtcNow
            });

            SaveReplies(replies);
        }

        private static List<FeedbackReply> LoadReplies()
        {
            const string dataFile = "feedback.json";
            if (!File.Exists(dataFile))
                return new List<FeedbackReply>();

            var json = File.ReadAllText(dataFile);
            return JsonSerializer.Deserialize<List<FeedbackReply>>(json) ?? new List<FeedbackReply>();
        }

        private static void SaveReplies(List<FeedbackReply> replies)
        {
            const string dataFile = "feedback.json";
            var json = JsonSerializer.Serialize(replies, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dataFile, json);
        }
    }

    public class FeedbackReply
    {
        public string FeedbackId { get; set; }
        public string Reply { get; set; }
        public DateTime Timestamp { get; set; }
        public bool Delivered { get; set; } // New property
    }
}
