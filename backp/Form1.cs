using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Syncfusion.Windows.Forms.Gauge;
using Syncfusion.Windows.Forms.Tools;

namespace UpdateServer
{
    public partial class Form1 : Form
    {
        public static UpdateServerEntity _server;
        public static RichTextBox Consolewindow;
        public static TextBox Inputbox;
        public static Button SendInputBtn;
        public static Label VersionLabel;
        public static string CurrVersion;
        public static string RustFolderLoc;
        private static DirectoryInfo RustFolder;
        public static NumericUpDownExt countter;
        private static HashGen _clientHashGen;
        public static string Vversion = "";
        public static string FileHashes = "";
        public static DigitalGauge playercountersegment;
        public static sendmsg sendmessage;
        private static Thread Server;

        public Form1()
        {
            CheckForIllegalCrossThreadCalls = false;
            InitializeComponent();
            Consolewindow = richTextBox1;
            Inputbox = textBox1;
            VersionLabel = label1;
            DirectoryInfo directoryInfo = new DirectoryInfo(Directory.GetCurrentDirectory());
            if (directoryInfo.Exists)
            {
                treeView1.AfterSelect += treeView1_AfterSelect;
                BuildTree(directoryInfo, treeView1.Nodes);
            }

            playercountersegment = digitalGauge1;
            treeView1.TreeViewNodeSorter = new NodeSorter();
            countter = numericUpDownExt1;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            var v = "";
            if (File.Exists("Version.txt"))
            {
                v = File.ReadAllText("Version.txt");
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
                FileHashes = File.ReadAllText("Hashes.json");

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

            Vversion = v;



            _server = new UpdateServerEntity();



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
        }

        private void button3_Click(object sender, EventArgs e)
        {
           _clientHashGen = new HashGen();

           
        }

        private void button1_Click(object sender, EventArgs e)
        {
            string Message = Inputbox.Text;

            var fullstring = "D|" + Message;

            sendmessage(fullstring,"");
        }


        public delegate void sendmsg(string inp,string ip);
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
