using System;
using System.IO;
using System.Text;

public class ConsoleWriter : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public event EventHandler<ConsoleWriterEventArgs> WriteEvent;

    public event EventHandler<ConsoleWriterEventArgs> WriteLineEvent;

    public override void Write(string value)
    {
        if (WriteEvent != null) WriteEvent(this, new ConsoleWriterEventArgs(value));
        base.Write(value);
    }

    public override void WriteLine(string value)
    {
        if (WriteLineEvent != null) WriteLineEvent(this, new ConsoleWriterEventArgs(value));
        base.WriteLine(value);
    }
}

public class ConsoleWriterEventArgs : EventArgs
{
    public ConsoleWriterEventArgs(string value)
    {
        Value = value;
    }

    public string Value { get; private set; }
}