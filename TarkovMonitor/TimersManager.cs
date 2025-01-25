using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace TarkovMonitor
{
    internal class TimersManager
    {
        public event EventHandler<TimerChangedEventArgs> RaidTimerChanged;
        public event EventHandler<TimerChangedEventArgs> RunThroughTimerChanged;
        public event EventHandler<TimerChangedEventArgs> ScavCooldownTimerChanged;

        private TimeSpan RunThroughRemainingTime;
        private TimeSpan TimeInRaidTime;
        private TimeSpan ScavCooldownTime;
        private DateTime? RaidStartTime;
        private System.Threading.Timer timerRaid;
        private System.Threading.Timer timerRunThrough;
        private System.Threading.Timer timerScavCooldown;
        private CancellationTokenSource cancellationTokenSource = new();
        private readonly GameWatcher eft;

        public TimersManager(GameWatcher eft)
        {
            this.eft = eft;

            // Get Scav cooldown time from TarkovTracker but ensuring the API has been called and hydrated at least once.
            // without this, the scav cooldown time will be 25.
            // We only need to run this the first time the app starts.
            TarkovTracker.ProgressRetrieved += (sender, e) =>
            {
                ScavCooldownTime = TimeSpan.FromSeconds(TarkovDev.ScavCooldownSeconds());
                Debug.WriteLine($"ScavCooldownTime: {ScavCooldownTime}");
                
                TarkovTracker.ProgressRetrieved -= (sender, e) => { };
            };

            RunThroughRemainingTime = Properties.Settings.Default.runthroughTime;
            
            this.eft.RaidStarted += Eft_RaidStarted;
            this.eft.RaidEnded += Eft_RaidEnded;

            timerRaid = new System.Threading.Timer(TimerRaid_Elapsed, null, Timeout.Infinite, 1000);
            timerRunThrough = new System.Threading.Timer(TimerRunThrough_Elapsed, null, Timeout.Infinite, 1000);
            timerScavCooldown = new System.Threading.Timer(timerScavCooldown_Elapsed, null, Timeout.Infinite, 1000);
        }

        private async void Eft_RaidStarted(object? sender, RaidInfoEventArgs e)
        {
            if (e.RaidInfo.Reconnected)
                return;

            TimeInRaidTime = TimeSpan.Zero;
            RunThroughRemainingTime = Properties.Settings.Default.runthroughTime;

            timerRaid.Change(0, 1000);
            timerRunThrough.Change(0, 1000);

            RaidTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = TimeInRaidTime
            });

            RunThroughTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = RunThroughRemainingTime
            });

        }

        private void Eft_RaidEnded(object? sender, RaidInfoEventArgs e)
        {
            RunThroughRemainingTime = TimeSpan.Zero;
            timerRunThrough.Change(Timeout.Infinite, Timeout.Infinite);
            timerRaid.Change(Timeout.Infinite, Timeout.Infinite);

            Debug.WriteLine($"Eft_RaidEnded: {e.RaidInfo.RaidType}");

            if (!e.RaidInfo.Reconnected && (e.RaidInfo.RaidType == RaidType.Scav || e.RaidInfo.RaidType == RaidType.PVE))
            {
                timerScavCooldown.Change(0, 1000);
            }

            RunThroughTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = RunThroughRemainingTime
            });

            ScavCooldownTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = ScavCooldownTime
            });
        }

        private void TimerRaid_Elapsed(object state)
        {
            if (cancellationTokenSource.IsCancellationRequested)
                return;

            TimeInRaidTime += TimeSpan.FromSeconds(1);

            RaidTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = TimeInRaidTime
            });
        }

        private void TimerRunThrough_Elapsed(object state)
        {
            if (cancellationTokenSource.IsCancellationRequested)
                return;

            if (RunThroughRemainingTime > TimeSpan.Zero)
            {
                RunThroughRemainingTime -= TimeSpan.FromSeconds(1);
            }
            else
            {
                timerRunThrough.Change(Timeout.Infinite, Timeout.Infinite);
            }

            RunThroughTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = RunThroughRemainingTime
            });
        }

        private async void timerScavCooldown_Elapsed(object state)
        {
            if (cancellationTokenSource.IsCancellationRequested)
                return;

            if (ScavCooldownTime > TimeSpan.Zero)
            {
                ScavCooldownTime -= TimeSpan.FromSeconds(1);
            }
            else
            {
                timerScavCooldown.Change(Timeout.Infinite, Timeout.Infinite);
                ScavCooldownTime = TimeSpan.FromSeconds(TarkovDev.ScavCooldownSeconds());
            }

            ScavCooldownTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = ScavCooldownTime
            });
        }
    }

    public class TimerChangedEventArgs : EventArgs
    {
        public TimeSpan TimerValue { get; set; }
    }
}
