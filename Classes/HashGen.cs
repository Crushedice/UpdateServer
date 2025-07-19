using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Sentry;

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

        public Task Run()
        {
            timr.Start();
            int i = 0;
            int errs = 0;
            int skip = 0;
            var errors = new List<string>();
            string[] allfiles = Directory.GetFiles(RustFolder.ToString(), "*", SearchOption.AllDirectories);
            files = allfiles.Count();
            foreach (string x in allfiles)
            {
                string hash = string.Empty;
                string path = GetRelativePath(x, RustFolder.FullName);
                try
                {
                    hash = CalculateMD5(x);
                    if (!FileHashes.ContainsKey(path)) FileHashes.Add(path, hash);
                }
                catch (Exception ex)
                {
                    Errs.Add(x, ex.Message);
                    SentrySdk.CaptureException(ex);
                    skip++;
                }
                Console.WriteLine("Progress " + i + "  File:  " + hash + "|" + path);
                i++;
            }
            File.WriteAllText("Hashes.json", JsonConvert.SerializeObject(FileHashes, Formatting.Indented));
            File.WriteAllText("Errors.json", JsonConvert.SerializeObject(Errs, Formatting.Indented));
            timr.Stop();
            Console.WriteLine("Errors: " + errs + "\n" + "Skipped : " + skip + "\n" + "Seconds : " + timr.ElapsedMilliseconds / 1000);
            return Task.CompletedTask;
        }

        private static string CalculateMD5(string filename)
        {
            byte[] hash;
            try
            {
                using (FileStream inputStream = File.Open(filename, FileMode.Open))
                {
                    MD5 md5 = MD5.Create();
                    hash = md5.ComputeHash(inputStream);
                }
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                throw;
            }
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private string GetRelativePath(string filespec, string folder)
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