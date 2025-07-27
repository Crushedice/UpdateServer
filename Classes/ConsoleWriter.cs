using System;
using System.IO;
using System.Text;

namespace UpdateServer.Classes
{
    public class ConsoleWriter : TextWriter
    {
        #region Properties
        public override Encoding Encoding => Encoding.UTF8;
        #endregion

        #region Events
        public event EventHandler<ConsoleWriterEventArgs> WriteEvent;
        public event EventHandler<ConsoleWriterEventArgs> WriteLineEvent;
        #endregion

        #region TextWriter Overrides
        public override void Write(string value)
        {
            WriteEvent?.Invoke(this, new ConsoleWriterEventArgs(value));
            base.Write(value);
        }

        public override void WriteLine(string value)
        {
            WriteLineEvent?.Invoke(this, new ConsoleWriterEventArgs(value));
            base.WriteLine(value);
        }
        #endregion
    }

    public class ConsoleWriterEventArgs : EventArgs
    {
        #region Properties
        public string Value { get; private set; }
        #endregion

        #region Constructor
        public ConsoleWriterEventArgs(string value)
        {
            Value = value;
        }
        #endregion
    }
}