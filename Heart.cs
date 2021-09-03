using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace UpdateServer
{
	public class Heart
	{
		public UpdateServerEntity _server;
		public static TextBox Inputbox;
		public static Label VersionLabel;
		public static string Hashes = string.Empty;
		public static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
		public static sendmsg sendmessage;
		private static Thread Server;

		public static ListBox UpdaterGrid;
		public static RichTextBox ProgressOvLk;
		public static string Vversion = string.Empty;
		public static Dictionary<string, string> CurUpdaters = new Dictionary<string, string>();
		public static List<string> ThreadList = new List<string>();

		public  Heart()
		{
			DirectoryInfo directoryInfo = new DirectoryInfo(Directory.GetCurrentDirectory());

			var v = string.Empty;
			if (File.Exists("Version.txt"))
			{
				v = File.ReadAllText("Version.txt");
				Vversion = v;
			}
			else
			{
				//Console.WriteLine("Error - Version File Not Found!!!");
				//return;
				Vversion = "2314";
			}


			beat();
			
		}
		private async void beat()
		{
			if (!File.Exists("Hashes.json"))
			{
				await new HashGen().Run();

				Hashes = File.ReadAllText("Hashes.json");
				FileHashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(Hashes);


			}
			else
			{
				Hashes = File.ReadAllText("Hashes.json");
				FileHashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(Hashes);
			}

			Console.WriteLine("Heart Started. Starting update server...");
			_server = new UpdateServerEntity();
		}

		public static void ProcessUpdaterList(string ipPort, int indecator, string PFiles = "")
		{
			var Name = ipPort.Split(':')[0].Replace('.', '0');

			switch (indecator)
			{

				//Case 1 =  Client starts updating.
				case 1:
					if (!UpdaterGrid.Items.Contains(Name))
					{
						UpdaterGrid.Items.Add(Name);
					}

					UpdaterGrid.Refresh();
					break;

				//Case 2 = Client done updating / Client got his delta files.
				case 2:
					if (UpdaterGrid.Items.Contains(Name))
					{
						UpdaterGrid.Items.Remove(Name);
					}

					UpdaterGrid.Refresh();
					break;


				//Case 3 = Update the UI
				case 3:
					if (UpdaterGrid.Items.Contains(Name))
					{
						// UpdaterGrid[Name] = PFiles;

					}

					UpdaterGrid.Refresh();
					break;

			}

		}

		public static void ShowUpdateThreads()
		{

			ProgressOvLk.Clear();
			foreach (var x in CurUpdaters)
			{
				Console.WriteLine("Progress of :" + x.Key + " => " + x.Value + "\n");

			}



		}

		public static void ShowPoolThreads()
		{

			ProgressOvLk.Clear();
			Console.WriteLine("Threads: \n");

			foreach (var x in ThreadList)
			{
				Console.WriteLine(x + "\n");

			}



		}



		public static void PoolMod(string thrd, bool add)
		{
			if (add)
				ThreadList.Add(thrd);
			else
			{
				ThreadList.Remove(thrd);
			}

			ShowPoolThreads();
		}

		public static void ModifyUpdateDic(string ip, string values, bool Remove = false)
		{

			if (Remove)
			{
				CurUpdaters.Remove(ip);
				ShowUpdateThreads();
				return;
			}

			if (!CurUpdaters.ContainsKey(ip))
			{
				CurUpdaters.Add(ip, values);


			}
			else
			{
				CurUpdaters[ip] = values;
			}

			ShowUpdateThreads();

		}

		private void BuildTree(DirectoryInfo directoryInfo, TreeNodeCollection addInMe)
		{
			TreeNode curNode = addInMe.Add(directoryInfo.Name);

			foreach (FileInfo file in directoryInfo.GetFiles())
			{
				curNode.Nodes.Add(file.FullName, file.Name);
			}

			foreach (DirectoryInfo subdir in directoryInfo.GetDirectories())
			{
				BuildTree(subdir, curNode.Nodes);
			}
		}


		public delegate void sendmsg(string inp, string ip, bool hashes);

		public class NodeSorter : IComparer
		{
			// Compare the length of the strings, or the strings
			// themselves, if they are the same length.
			public int Compare(object x, object y)
			{
				TreeNode tx = x as TreeNode;
				TreeNode ty = y as TreeNode;

				// Compare the length of the strings, returning the difference.
				if (tx.Text.Length != ty.Text.Length)
					return tx.Text.Length - ty.Text.Length;

				// If they are the same length, call Compare.
				return string.Compare(tx.Text, ty.Text);
			}
		}
	}
}
