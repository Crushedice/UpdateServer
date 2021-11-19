using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateServer.Classes
{
    public class OST
    {
        int seconds;
        Action MethodtoCall;
        int Repeat;
        TimerType _type;
        private CancellationTokenSource ts = new CancellationTokenSource();
        private CancellationToken ct;

        /// <summary>
        /// The OneShotTimer. Set amount of time in Seconds, and give a Method to execute.
        /// Method gets Executed after set amount of time.
        /// </summary>
        /// <param name="time"></param>
        /// <param name="method"></param>
        public OST(int time, Action method)
        {
            seconds = time * 1000;
            MethodtoCall = method;
            _type = TimerType.Once;

            Task.Run(() => Execute());
        }
        /// <summary>
        /// Starts a repeating timer for X times, in addition to the set interval for execution. 
        /// </summary>
        /// <param name="time"></param>
        /// <param name="Repeats"></param>
        /// <param name="method"></param>
        public OST(int time, int Repeats, Action method)
        {
            seconds = time * 1000;
            MethodtoCall = method;
            Repeat = Repeats;
            _type = TimerType.Repeated;
            Task.Run(() => Execute());
        }
        /// <summary>
        /// Starts a Continous timer with unlimited repeats on set interval.
        /// Use Cancellation Token to kill timer if needed.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="intervals"></param>
        public OST(Action method, int intervals)
        {
            seconds = intervals * 1000;
            MethodtoCall = method;
            _type = TimerType.Continous;
            ct = ts.Token;
            Task.Run(() => Execute(), ct);
        }

        /// <summary>
        /// If destroying the continous timer is needed.
        /// </summary>
        public void StopExecution()
        {
            ts.Cancel();
        }

        private async Task Execute()
        {
            switch (_type)
            {
                case TimerType.Once:
                    await Task.Delay(seconds);
                    MethodtoCall();
                    break;
                case TimerType.Repeated:
                    int count = 0;
                start:
                    await Task.Delay(seconds);
                    MethodtoCall();
                    count++;
                    if (count < Repeat)
                        goto start;
                    break;
                case TimerType.Continous:
                restart:
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    await Task.Delay(seconds, ct);
                    MethodtoCall();
                    goto restart;
            }
        }

        private enum TimerType
        {
            Once = 1,
            Repeated = 2,
            Continous = 3
        }
    }
}
