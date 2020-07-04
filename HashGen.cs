using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UpdateServer
{


    class HashGen
    {
        private static DirectoryInfo RustFolder;
        private static int files;
        private static BackgroundWorker BackgroundWorker1;
        static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
        static Dictionary<string, string> Errs = new Dictionary<string, string>();
        static Stopwatch timr;
        static HashGen()
        {
            timr = new Stopwatch();
            if (Directory.Exists("Rust"))
            {
                RustFolder = new DirectoryInfo("Rust");

                Run();


            }
            else
            {
                Form1.AddNewEntry("Rust Folder not Found");

                return;


            }

            Form1.AddNewEntry(string.Empty);



        }

        public static void Run()
        {
            timr.Start();
            int i = 0;
            int errs = 0;
            var executingPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            int skip = 0;
            List<string> errors = new List<string>();
            var allfiles = Directory.GetFiles(RustFolder.ToString(), "*", SearchOption.AllDirectories);
            var test = Directory.GetFileSystemEntries(RustFolder.ToString(), "*", SearchOption.AllDirectories);
            files = allfiles.Count();



            foreach (var x in allfiles)
            {


                string hash;
                string path;

                var wpath = x.Replace(RustFolder.ToString() + "\\", string.Empty);
                path = wpath.Replace("\\", "/");
                hash = CalculateMD5(x);



                if (!FileHashes.ContainsKey(path))
                {
                    FileHashes.Add(path, hash);
                }
                else
                {
                    errs++;

                    errors.Add(x);
                    try
                    {
                        Errs.Add(path, hash);
                    }
                    catch
                    {

                    }
                }


                Form1.AddNewEntry("Progress " + i + "  File:  " + hash + "|" + path);
                i++;

            }


            File.WriteAllText("Hashes.json", JsonConvert.SerializeObject(FileHashes, Formatting.Indented));
            File.WriteAllText("Errors.json", JsonConvert.SerializeObject(Errs, Formatting.Indented));
            timr.Stop();
            Form1.AddNewEntry("Errors: " + errs.ToString() + "\n" + "Skipped : " + skip + "\n" + "Seconds : " + (timr.ElapsedMilliseconds) / 1000);



        }

        static string CalculateMD5(string filename)
        {

            byte[] hash;
            using (var inputStream = File.OpenRead(filename))
            {
                var md5 = System.Security.Cryptography.MD5.Create();

                hash = md5.ComputeHash(inputStream);

            }
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
