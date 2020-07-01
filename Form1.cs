using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Syncfusion.Windows.Forms.Gauge;
using Syncfusion.Windows.Forms.Tools;

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
        public static NumericUpDownExt countter;
        private static HashGen _clientHashGen;
        public static string Vversion = "";
        public static string Hashes = "";
        public static DigitalGauge playercountersegment;
        public static Dictionary<string, string> FileHashes = new Dictionary<string, string>();
        public static sendmsg sendmessage;
        private static Thread Server;
        public static bool ConsoleEnabled = true;
        public static ListBox UpdaterGrid;
        public Form1()
        {
            CheckForIllegalCrossThreadCalls = false;
            InitializeComponent();
            Consolewindow = richTextBox1;
            Inputbox = textBox1;
            VersionLabel = label1;
            DirectoryInfo directoryInfo = new DirectoryInfo(Directory.GetCurrentDirectory());
            ConsoleEnabled = checkBox1.Checked;
            countter = numericUpDownExt1;
            playercountersegment = digitalGauge1;
            UpdaterGrid = listBox1;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            var v = "";
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

            sendmessage(fullstring,"",false);
        }


        public delegate void sendmsg(string inp,string ip,bool hashes);

        private void button5_Click(object sender, EventArgs e)
        {
            ProcessThreadCollection currentThreads = Process.GetCurrentProcess().Threads;

            foreach (ProcessThread thread in currentThreads)
            {
               
            }
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
