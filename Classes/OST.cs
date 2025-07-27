using System;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateServer.Classes
{
    public class OST
    {
        #region Enums
        private enum TimerType
        {
            Once = 1,
            Repeated = 2,
            Continous = 3
        }
        #endregion

        #region Fields
        private TimerType _type;
        private CancellationToken ct;
        private Action MethodtoCall;
        private int Repeat;
        private int seconds;
        private CancellationTokenSource ts = new CancellationTokenSource();
        #endregion

        #region Constructors
        /// <summary>
        /// The OneShotTimer. Set amount of time in Seconds, and give a Method to execute.
        /// Method gets Executed after set amount of time.
        /// </summary>
        /// <param name="time">Time in seconds</param>
        /// <param name="method">Method to execute</param>
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
        /// <param name="time">Time interval in seconds</param>
        /// <param name="repeats">Number of times to repeat</param>
        /// <param name="method">Method to execute</param>
        public OST(int time, int repeats, Action method)
        {
            seconds = time * 1000;
            MethodtoCall = method;
            Repeat = repeats;
            _type = TimerType.Repeated;
            Task.Run(() => Execute());
        }

        /// <summary>
        /// Starts a Continous timer with unlimited repeats on set interval.
        /// Use Cancellation Token to kill timer if needed.
        /// </summary>
        /// <param name="method">Method to execute</param>
        /// <param name="intervals">Interval in seconds</param>
        public OST(Action method, int intervals)
        {
            seconds = intervals * 1000;
            MethodtoCall = method;
            _type = TimerType.Continous;
            ct = ts.Token;
            Task.Run(() => Execute(), ct);
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// If destroying the continous timer is needed.
        /// </summary>
        public void StopExecution()
        {
            ts.Cancel();
        }
        #endregion

        #region Private Methods
        private async Task Execute()
        {
            switch (_type)
            {
                case TimerType.Once:
                    await ExecuteOnce();
                    break;

                case TimerType.Repeated:
                    await ExecuteRepeated();
                    break;

                case TimerType.Continous:
                    await ExecuteContinuous();
                    break;
            }
        }

        private async Task ExecuteOnce()
        {
            await Task.Delay(seconds);
            MethodtoCall();
        }

        private async Task ExecuteRepeated()
        {
            int count = 0;
            while (count < Repeat)
            {
                await Task.Delay(seconds);
                MethodtoCall();
                count++;
            }
        }

        private async Task ExecuteContinuous()
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(seconds, ct);
                if (!ct.IsCancellationRequested)
                {
                    MethodtoCall();
                }
            }
        }
        #endregion
    }
}