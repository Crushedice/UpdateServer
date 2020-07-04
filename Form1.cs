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
    public partial class Form1 : Form
    {
        public UpdateServerEntity _server;
        public static RichTextBox Consolewindow;
        public static TextBox Inputbox;
        public static Button SendInputBtn;
        public static Label VersionLabel;
        public static int CurrVersion;
        public static string RustFolderLoc;
        private static DirectoryInfo RustFolder;
     
        private static HashGen _clientHashGen;
        public static string Vversion = string.Empty;
        public static string Hashes = string.Empty;
       
        public static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
        public static sendmsg sendmessage;
        private static Thread Server;
        public static bool ConsoleEnabled = true;
        public static ListBox UpdaterGrid;
        public static RichTextBox ProgressOvLk;
        public static Dictionary<string,string> CurUpdaters = new Dictionary<string, string>();
        public static List<string> ThreadList = new List<string>();
        public Form1()
        {
            CheckForIllegalCrossThreadCalls = false;
            InitializeComponent();
            Consolewindow = richTextBox1;
            Inputbox = textBox1;
            VersionLabel = label1;
            ProgressOvLk = richTextBox2;
            DirectoryInfo directoryInfo = new DirectoryInfo(Directory.GetCurrentDirectory());
            ConsoleEnabled = checkBox1.Checked;
         
            UpdaterGrid = listBox1;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            var v = string.Empty;
            if (File.Exists("Version.txt"))
            {
                v = File.ReadAllText("Version.txt");
                Vversion = v;
            }
            else
            {
                MessageBox.Show("Error - Version File Not Found!!!");
                return;
            }

            VersionLabel.Text = "-Current Online Version- \n" + v;

            if (!File.Exists("Hashes.json"))
            {
                MessageBox.Show("Error - Hashes File Not found!!!");
            }
            else
            {
                Hashes = File.ReadAllText("Hashes.json");
                FileHashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(Hashes);
            }


            if (Directory.Exists("Rust"))
            {
                RustFolder = new DirectoryInfo("Rust");

            }
            else
            {
                MessageBox.Show("Error - Rust Folder not found!!!");
                return;
            }





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
                ProgressOvLk.AppendText("Progress of :"+x.Key + " => " + x.Value + "\n");

            }



        }

        public static void ShowPoolThreads()
        {

            ProgressOvLk.Clear();
            ProgressOvLk.AppendText("Threads: \n");

            foreach (var x in ThreadList)
            {
                ProgressOvLk.AppendText(x+ "\n");

            }



        }



        public static void PoolMod(string thrd,bool add)
        {
            if(add)
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
                CurUpdaters.Add(ip,values);


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

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node.Name.EndsWith("txt"))
            {
                this.richTextBox1.Clear();
                StreamReader reader = new StreamReader(e.Node.Name);
                this.richTextBox1.Text = reader.ReadToEnd();
                reader.Close();
            }
        }




        public static void AddNewEntry(string text)
        {
            if (!ConsoleEnabled)
                goto skip;
            // StringBuilder sb = new StringBuilder(Console.Text);
            // Append the string you want, to the string builder object.
            // sb.Append($">>{text}\r\n");
            // Finally assign the string builder's current string to the Label Text
            //  Console.Text = sb.ToString();

            try
            {
                RichTextBoxExtensions.AppendText(Form1.Consolewindow,
                    $"[{DateTime.Now.ToShortTimeString()}] {text}\r\n", Color.Green);
                //Thread.Sleep(250);
                Form1.Consolewindow.ScrollToCaret();
            }
            catch
            {

            }
            return;

        skip:
            Thread.Sleep(150);

        }

        private void button3_Click(object sender, EventArgs e)
        {
            _clientHashGen = new HashGen();


        }

        private void button1_Click(object sender, EventArgs e)
        {
            string Message = Inputbox.Text;

            var fullstring = "D|" + Message;

            sendmessage(fullstring, string.Empty, false);
        }


        public delegate void sendmsg(string inp, string ip, bool hashes);

        private void button5_Click(object sender, EventArgs e)
        {
          //  AddNewEntry(Process.GetCurrentProcess().Threads.Count.ToString());
        //  var coun = UpdateServerEntity.tasks.Count;
        //    AddNewEntry(coun.ToString());
        }


        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            ConsoleEnabled = checkBox1.Checked;
            _server.changeConsole(checkBox1.Checked);

        }

        private void CleanupTimer_Tick(object sender, EventArgs e)
        {
            _server.CheckUsers();
        }

        private void button6_Click(object sender, EventArgs e)
        {
            _server.CheckUsers();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            UpdateServerEntity.RunStandalone();
        }

        private void ThreadTimer_Tick(object sender, EventArgs e)
        {

        }

        private void button7_Click(object sender, EventArgs e)
        {
            


         //   foreach (var x in UpdateServerEntity.tokenlist)
         //   {
         //       x.Cancel();
         //       
         //   }
         //  foreach (var c in UpdateServerEntity.tasks)
         //   {
         //       c.Dispose();
         //
         //
         //   }
        }

        private void button8_Click(object sender, EventArgs e)
        {
            UpdateServerEntity.BreakApp();
        }

        private void button9_Click(object sender, EventArgs e)
        {
           // foreach (var c in UpdateServerEntity.tasks)
           // {
           //     AddNewEntry(c.Status.ToString());
           //
           //
           // }
        }

        private void button10_Click(object sender, EventArgs e)
        {
          //  foreach (var c in UpdateServerEntity.tasks)
          //  {
          //      c.Start();
          //
          //
          //  }
        }
    }

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
