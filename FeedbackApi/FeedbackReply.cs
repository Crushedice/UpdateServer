using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UpdateServer.FeedbackApi
{
    public class FeedbackReply
    {
        public string FeedbackId { get; set; }
        public string Reply { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
