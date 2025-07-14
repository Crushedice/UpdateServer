using System;
using System.IO;
using System.Threading;

namespace UpdateServer.Classes
{
    /// <summary>
    /// Simple file logging utility
    /// </summary>
    public static class FileLogger
    {
        private static readonly object _lockObject = new object();
        private static readonly string _logFileName = "UpdateServer.log";
        private static readonly string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _logFileName);

        /// <summary>
        /// Logs an informational message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogInfo(string message)
        {
            LogMessage("INFO", message);
        }

        /// <summary>
        /// Logs a warning message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogWarning(string message)
        {
            LogMessage("WARN", message);
        }

        /// <summary>
        /// Logs an error message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogError(string message)
        {
            LogMessage("ERROR", message);
        }

        /// <summary>
        /// Logs an exception
        /// </summary>
        /// <param name="exception">Exception to log</param>
        /// <param name="message">Optional additional message</param>
        public static void LogException(Exception exception, string message = null)
        {
            var fullMessage = string.IsNullOrEmpty(message) 
                ? $"Exception: {exception.Message}\nStack Trace: {exception.StackTrace}"
                : $"{message}\nException: {exception.Message}\nStack Trace: {exception.StackTrace}";
            
            LogMessage("ERROR", fullMessage);
        }

        /// <summary>
        /// Logs a debug message
        /// </summary>
        /// <param name="message">Message to log</param>
        public static void LogDebug(string message)
        {
            LogMessage("DEBUG", message);
        }

        /// <summary>
        /// Core logging method
        /// </summary>
        /// <param name="level">Log level</param>
        /// <param name="message">Message to log</param>
        private static void LogMessage(string level, string message)
        {
            try
            {
                lock (_lockObject)
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var threadId = Thread.CurrentThread.ManagedThreadId;
                    var logEntry = $"[{timestamp}] [{level}] [Thread-{threadId}] {message}";

                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                // Fallback to console if file logging fails
                Console.WriteLine($"File logging failed: {ex.Message}");
                Console.WriteLine($"Original message: [{level}] {message}");
            }
        }

        /// <summary>
        /// Clears the log file
        /// </summary>
        public static void ClearLog()
        {
            try
            {
                lock (_lockObject)
                {
                    if (File.Exists(_logFilePath))
                    {
                        File.Delete(_logFilePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clear log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the log file path
        /// </summary>
        public static string GetLogFilePath()
        {
            return _logFilePath;
        }
    }
}
