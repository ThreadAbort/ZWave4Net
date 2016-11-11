using System;

namespace ZWave.Channel
{
    public class LogEventArgs : EventArgs
    {
        public readonly DateTime EventTime;
        public readonly String LogText;

        public LogEventArgs(String LogText)
        {
            this.EventTime = DateTime.UtcNow;
            this.LogText = LogText;
        }
    }
}