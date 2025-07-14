using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Sentry;
using UpdateServer.Classes;

namespace UpdateServer.Classes
{
    /// <summary>
    /// Centralized performance monitoring service for async operations
    /// </summary>
    public static class SentryPerformanceMonitor
    {
        private static readonly ConcurrentDictionary<string, string> ActiveTransactions = 
            new ConcurrentDictionary<string, string>();
        
        private static readonly ConcurrentDictionary<string, string> ActiveSpans = 
            new ConcurrentDictionary<string, string>();

        /// <summary>
        /// Starts a new transaction for monitoring async operations
        /// </summary>
        /// <param name="name">Transaction name</param>
        /// <param name="operation">Operation type</param>
        /// <param name="description">Optional description</param>
        /// <returns>Transaction ID for tracking</returns>
        public static string StartTransaction(string name, string operation, string description = null)
        {
            var transactionId = Guid.NewGuid().ToString();
            
            ActiveTransactions[transactionId] = name;
            
            SentrySdk.AddBreadcrumb($"Started transaction: {name} ({operation})", "performance.transaction.start");
            SentrySdk.CaptureMessage($"Performance monitoring started: {name} - {operation}");
            
            return transactionId;
        }

        /// <summary>
        /// Starts a child span within a transaction
        /// </summary>
        /// <param name="transactionId">Parent transaction ID</param>
        /// <param name="operation">Span operation</param>
        /// <param name="description">Optional description</param>
        /// <returns>Span ID for tracking</returns>
        public static string StartSpan(string transactionId, string operation, string description = null)
        {
            var spanId = Guid.NewGuid().ToString();
            
            ActiveSpans[spanId] = operation;
            
            SentrySdk.AddBreadcrumb($"Started span: {operation}", "performance.span.start");
            SentrySdk.CaptureMessage($"Performance span started: {operation}");
            
            return spanId;
        }

        /// <summary>
        /// Finishes a transaction with optional status and data
        /// </summary>
        /// <param name="transactionId">Transaction ID to finish</param>
        /// <param name="status">Status of the transaction</param>
        /// <param name="data">Additional data to attach</param>
        public static void FinishTransaction(string transactionId, SpanStatus status = SpanStatus.Ok, 
            Dictionary<string, object> data = null)
        {
            if (ActiveTransactions.TryRemove(transactionId, out var transactionName))
            {
                SentrySdk.AddBreadcrumb($"Finished transaction: {transactionName}", "performance.transaction.finish");
                SentrySdk.CaptureMessage($"Performance transaction finished: {transactionName} - Status: {status}");
            }
        }

        /// <summary>
        /// Finishes a span with optional status and data
        /// </summary>
        /// <param name="spanId">Span ID to finish</param>
        /// <param name="status">Status of the span</param>
        /// <param name="data">Additional data to attach</param>
        public static void FinishSpan(string spanId, SpanStatus status = SpanStatus.Ok, 
            Dictionary<string, object> data = null)
        {
            if (ActiveSpans.TryRemove(spanId, out var spanName))
            {
                SentrySdk.AddBreadcrumb($"Finished span: {spanName}", "performance.span.finish");
                SentrySdk.CaptureMessage($"Performance span finished: {spanName} - Status: {status}");
            }
        }

        /// <summary>
        /// Monitors an async operation with automatic span creation
        /// </summary>
        /// <typeparam name="T">Return type</typeparam>
        /// <param name="operation">Operation name</param>
        /// <param name="asyncFunction">Async function to monitor</param>
        /// <param name="description">Optional description</param>
        /// <returns>Result of the async operation</returns>
        public static async Task<T> MonitorAsync<T>(string operation, Func<Task<T>> asyncFunction, 
            string description = null)
        {
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                SentrySdk.AddBreadcrumb($"Starting monitored operation: {operation}", "performance.operation.start");
                
                var result = await asyncFunction();
                
                SentrySdk.AddBreadcrumb($"Completed monitored operation: {operation} in {stopwatch.ElapsedMilliseconds}ms", 
                    "performance.operation.success");
                
                return result;
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                SentrySdk.AddBreadcrumb($"Failed monitored operation: {operation} after {stopwatch.ElapsedMilliseconds}ms", 
                    "performance.operation.error");
                
                throw;
            }
            finally
            {
                stopwatch.Stop();
            }
        }

        /// <summary>
        /// Monitors an async operation without return value
        /// </summary>
        /// <param name="operation">Operation name</param>
        /// <param name="asyncFunction">Async function to monitor</param>
        /// <param name="description">Optional description</param>
        public static async Task MonitorAsync(string operation, Func<Task> asyncFunction, 
            string description = null)
        {
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                SentrySdk.AddBreadcrumb($"Starting monitored operation: {operation}", "performance.operation.start");
                
                await asyncFunction();
                
                SentrySdk.AddBreadcrumb($"Completed monitored operation: {operation} in {stopwatch.ElapsedMilliseconds}ms", 
                    "performance.operation.success");
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                SentrySdk.AddBreadcrumb($"Failed monitored operation: {operation} after {stopwatch.ElapsedMilliseconds}ms", 
                    "performance.operation.error");
                
                throw;
            }
            finally
            {
                stopwatch.Stop();
            }
        }

        /// <summary>
        /// Records a custom performance metric
        /// </summary>
        /// <param name="metricName">Name of the metric</param>
        /// <param name="value">Metric value</param>
        /// <param name="tags">Optional tags</param>
        public static void RecordMetric(string metricName, double value, Dictionary<string, string> tags = null)
        {
            var span = SentrySdk.GetSpan();
            if (span != null)
            {
                span.SetExtra(metricName, value);
                
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        span.SetTag(tag.Key, tag.Value);
                    }
                }
            }
            
            SentrySdk.AddBreadcrumb($"Recorded metric: {metricName} = {value}", "performance.metric");
            FileLogger.LogInfo($"Performance metric recorded: {metricName} = {value}");
        }

        /// <summary>
        /// Sets user context for performance monitoring
        /// </summary>
        /// <param name="userId">User ID</param>
        /// <param name="ipAddress">IP address</param>
        /// <param name="additionalData">Additional user data</param>
        public static void SetUserContext(string userId, string ipAddress = null, 
            Dictionary<string, object> additionalData = null)
        {
            SentrySdk.ConfigureScope(scope =>
            {
                scope.User = new SentryUser
                {
                    Id = userId,
                    IpAddress = ipAddress
                };
                
                if (additionalData != null)
                {
                    foreach (var data in additionalData)
                    {
                        scope.SetExtra(data.Key, data.Value);
                    }
                }
            });
        }

        /// <summary>
        /// Gets the count of active transactions
        /// </summary>
        public static int GetActiveTransactionCount()
        {
            return ActiveTransactions.Count;
        }

        /// <summary>
        /// Gets the count of active spans
        /// </summary>
        public static int GetActiveSpanCount()
        {
            return ActiveSpans.Count;
        }

        /// <summary>
        /// Clears all active transactions and spans (for cleanup)
        /// </summary>
        public static void ClearAll()
        {
            ActiveTransactions.Clear();
            ActiveSpans.Clear();
            
            SentrySdk.AddBreadcrumb("Cleared all active performance monitoring", "performance.cleanup");
            SentrySdk.CaptureMessage("Performance monitoring cleared all active transactions and spans");
        }
    }
}
