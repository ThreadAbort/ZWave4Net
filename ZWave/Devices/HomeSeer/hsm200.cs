using System;
using System.Collections.Generic;
using System.Text;
using ZWave.CommandClasses;

namespace ZWave.Devices.HomeSeer
{
    public class HSM200 : Device
    {
        public event EventHandler<EventArgs> MotionDetected;
        public event EventHandler<EventArgs> MotionCancelled;
        public HSM200(Node node) : base(node)
        {
            node.GetCommandClass<Basic>().Changed += Basic_Changed;

        }

        private void Basic_Changed(object sender, ReportEventArgs<BasicReport> e)
        {
            if (e.Report.Value == 0x00)
            {
                OnMotionCancelled(EventArgs.Empty);
                return;
            }
            if (e.Report.Value == 0xFF)
            {
                OnMotionDetected(EventArgs.Empty);
                return;
            }
        }

        protected virtual void OnMotionDetected(EventArgs e)
        {
            MotionDetected?.Invoke(this, e);
        }

        protected virtual void OnMotionCancelled(EventArgs e)
        {
            MotionCancelled?.Invoke(this, e);
        }

    }
}
