using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;


public partial class Form1 : Form
{
    public event EventHandler<TextArgs> Feedback;

    void Logging(string msg)
    {
        if (Feedback != null)
            Feedback(null, new TextArgs(msg));

    }
    private void RaiseFeedback(string p)
    {
        EventHandler<TextArgs> handler = Feedback;
        if (handler != null)
        {
            handler(null, new TextArgs(p));
        }
    }
}



public class TextArgs : EventArgs
    {
        #region Fields
        private string szMessage;
        #endregion Fields

        #region ConstructorsH
        public TextArgs(string TextMessage)
        {
            szMessage = TextMessage;
        }
        #endregion Constructors

        #region Properties
        public string Message
        {
            get { return szMessage; }
            set { szMessage = value; }
        }
        #endregion Properties
    }


