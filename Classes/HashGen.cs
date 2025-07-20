using Newtonsoft.Json;
using Sentry;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateServer
{
    public class HashGen
    {
        private static BackgroundWorker BackgroundWorker1;
        private static Dictionary<string, string> Errs = new Dictionary<string, string>();
        private static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
        private static int files;
        private static DirectoryInfo RustFolder;
        private static Stopwatch timr;

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

        public async Task Run()
        {
            timr.Start();
            int errs = 0;
            int skip = 0;
            var errors = new List<string>();
            string[] allfiles = Directory.GetFiles(RustFolder.ToString(), "*", SearchOption.AllDirectories);
            files = allfiles.Count();

            // Use concurrent collections for thread safety
            var concurrentFileHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            var concurrentErrs = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

            // Process files in parallel with controlled concurrency
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2); // Limit concurrent operations
            var tasks = allfiles.Select(async (file, index) =>
            {
                await semaphore.WaitAsync();
                try
                {
                    string hash = string.Empty;
                    string path = GetRelativePath(file, RustFolder.FullName);
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
                        Interlocked.Increment(ref skip);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Convert concurrent collections back to regular dictionaries
            FileHashes = new Dictionary<string, string>(concurrentFileHashes);
            Errs = new Dictionary<string, string>(concurrentErrs);

            File.WriteAllText("Hashes.json", JsonConvert.SerializeObject(FileHashes, Formatting.Indented));
            File.WriteAllText("Errors.json", JsonConvert.SerializeObject(Errs, Formatting.Indented));
            timr.Stop();
            Console.WriteLine("Errors: " + errs + "\n" + "Skipped : " + skip + "\n" + "Seconds : " + timr.ElapsedMilliseconds / 1000);
        }

        private static async Task<string> CalculateMD5Async(string filename)
        {
            try
            {
                using (FileStream inputStream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read,
                           bufferSize: 1048576, useAsync: true))
                using (MD5 md5 = MD5.Create())
                {
                    byte[] hash =  md5.ComputeHash(inputStream);
                    return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                throw;
            }
        }



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
                throw;
            }
        }

        private static string GetRelativePath(string filespec, string folder)
        {
            filespec = Path.GetFullPath(filespec);

            Uri pathUri = new Uri(filespec);
            // Folders must end in a slash
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString())) folder += Path.DirectorySeparatorChar;
            Uri folderUri = new Uri(folder);
            return Uri.UnescapeDataString(
                folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
    }
}