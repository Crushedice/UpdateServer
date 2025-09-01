using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Sentry;

namespace UpdateServer
{
    public class HashGen
    {
        #region Private Fields
        private static Dictionary<string, string> Errs = new Dictionary<string, string>();
        private static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
        private static int files;
        private static DirectoryInfo RustFolder;
        private static Stopwatch timr;
        #endregion

        #region Constructor
        public HashGen()
        {
            timr = new Stopwatch();
            
            if (Directory.Exists("Rust"))
            {
                RustFolder = new DirectoryInfo("Rust");
            }
            else
            {
                Console.WriteLine("Rust Folder not Found");
                return;
            }

            Console.WriteLine(string.Empty);
        }
        #endregion

        #region Public Methods
        public async Task Run()
        {
            timr.Start();
            
            string[] allfiles = Directory.GetFiles(RustFolder.ToString(), "*", SearchOption.AllDirectories);
            files = allfiles.Count();

            // Use concurrent collections for thread safety
            var concurrentFileHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            var concurrentErrs = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

            int skip = await ProcessFilesAsync(allfiles, concurrentFileHashes, concurrentErrs);
            
            // Convert concurrent collections back to regular dictionaries
            FileHashes = new Dictionary<string, string>(concurrentFileHashes);
            Errs = new Dictionary<string, string>(concurrentErrs);

            SaveResults();
            
            timr.Stop();
            LogResults(skip);
        }
        #endregion

        #region Private Processing Methods
        private async Task<int> ProcessFilesAsync(
            string[] allfiles, 
            System.Collections.Concurrent.ConcurrentDictionary<string, string> concurrentFileHashes,
            System.Collections.Concurrent.ConcurrentDictionary<string, string> concurrentErrs)
        {
            // Process files in parallel with controlled concurrency
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2); // Limit concurrent operations
            int totalSkipped = 0;
            
            var tasks = allfiles.Select(async (file, index) =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await ProcessSingleFileAsync(file, index, concurrentFileHashes, concurrentErrs);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);
            
            // Sum up all the skipped files
            foreach (var skipped in results)
            {
                totalSkipped += skipped;
            }
            
            return totalSkipped;
        }

        private async Task<int> ProcessSingleFileAsync(
            string file, 
            int index, 
            System.Collections.Concurrent.ConcurrentDictionary<string, string> concurrentFileHashes,
            System.Collections.Concurrent.ConcurrentDictionary<string, string> concurrentErrs)
        {
            string hash = string.Empty;
            string path = GetRelativePath(file, RustFolder.FullName);
            int skipped = 0;
            
            try
            {
                hash = await GetXxHash3Async(file);
                concurrentFileHashes.TryAdd(path, hash);
                Console.WriteLine($"Progress {index + 1}/{files}  File:  {hash}|{path}");
            }
            catch (Exception ex)
            {
                concurrentErrs.TryAdd(file, ex.Message);
                SentrySdk.CaptureException(ex);
                skipped = 1;
#if DEBUG
                throw;
#endif
            }
            
            return skipped;
        }

        private void SaveResults()
        {
            // Use synchronous File operations for .NET Framework 4.8
            File.WriteAllText("Hashes.json", JsonConvert.SerializeObject(FileHashes, Formatting.Indented));
            File.WriteAllText("Errors.json", JsonConvert.SerializeObject(Errs, Formatting.Indented));
        }

        private void LogResults(int skip)
        {
            Console.WriteLine($"Errors: {Errs.Count}");
            Console.WriteLine($"Skipped: {skip}");
            Console.WriteLine($"Seconds: {timr.ElapsedMilliseconds / 1000}");
        }
        #endregion

        #region Static Utility Methods
        private static async Task<string> GetXxHash3Async(string filename)
        {
            try
            {
                using (FileStream inputStream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: 2097152, useAsync: true))
                {
                    var xxHash = new XxHash128();
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(2097152);

                    try
                    {
                        int bytesRead;
                        while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            xxHash.Append(buffer.AsSpan(0, bytesRead));
                        }

                        byte[] hash = xxHash.GetHashAndReset();
                        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
#if DEBUG
                throw;
#endif
                throw;
            }
        }

        private static string GetRelativePath(string filespec, string folder)
        {
            filespec = Path.GetFullPath(filespec);

            Uri pathUri = new Uri(filespec);
            // Folders must end in a slash
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString())) 
                folder += Path.DirectorySeparatorChar;
            
            Uri folderUri = new Uri(folder);
            return Uri.UnescapeDataString(
                folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
        #endregion
    }
}