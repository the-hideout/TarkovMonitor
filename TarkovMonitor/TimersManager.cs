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
        public event EventHandler? RaidActiveChanged;
        public event EventHandler? FloatingPanelSettingChanged;

        private TimeSpan RunThroughRemainingTime;
        private TimeSpan TimeInRaidTime;
        private TimeSpan ScavCooldownTime;
        private readonly Stopwatch raidStopwatch = new();
        private System.Threading.Timer timerRaid;
        private System.Threading.Timer timerRunThrough;
        private System.Threading.Timer timerScavCooldown;
        private CancellationTokenSource cancellationTokenSource = new();
        private readonly GameWatcher eft;
        private readonly MessageLog messageLog;

        public bool IsRaidActive { get; private set; }
        public TimeSpan TimeInRaid => TimeInRaidTime;
        public TimeSpan RunThroughRemaining => RunThroughRemainingTime;
        public bool FloatingPanelEnabled
        {
            get => Properties.Settings.Default.floatingTimerPanelEnabled;
            set
            {
                if (Properties.Settings.Default.floatingTimerPanelEnabled == value)
                    return;

                Properties.Settings.Default.floatingTimerPanelEnabled = value;
                Properties.Settings.Default.Save();
                FloatingPanelSettingChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public bool FloatingPanelShowTimeInRaid
        {
            get => Properties.Settings.Default.floatingTimerPanelShowTimeInRaid;
            set
            {
                if (!value && !FloatingPanelShowRunThrough)
                    return;
                SaveFloatingPanelTimerSetting(nameof(Properties.Settings.Default.floatingTimerPanelShowTimeInRaid), value);
            }
        }
        public bool FloatingPanelShowRunThrough
        {
            get => Properties.Settings.Default.floatingTimerPanelShowRunThrough;
            set
            {
                if (!value && !FloatingPanelShowTimeInRaid)
                    return;
                SaveFloatingPanelTimerSetting(nameof(Properties.Settings.Default.floatingTimerPanelShowRunThrough), value);
            }
        }

        private void SaveFloatingPanelTimerSetting(string settingName, bool value)
        {
            if ((bool)Properties.Settings.Default[settingName] == value)
                return;

            Properties.Settings.Default[settingName] = value;
            Properties.Settings.Default.Save();
            FloatingPanelSettingChanged?.Invoke(this, EventArgs.Empty);
        }

        public TimersManager(GameWatcher eft, MessageLog messageLog)
        {
            this.eft = eft;
            this.messageLog = messageLog;

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
            IsRaidActive = true;
            raidStopwatch.Restart();

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

            RaidActiveChanged?.Invoke(this, EventArgs.Empty);

        }

        private void Eft_RaidEnded(object? sender, RaidInfoEventArgs e)
        {
            var completedRaidTime = raidStopwatch.Elapsed;
            var shouldRecordRaidTime = IsRaidActive;
            IsRaidActive = false;
            raidStopwatch.Stop();
            TimeInRaidTime = completedRaidTime;
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

            RaidActiveChanged?.Invoke(this, EventArgs.Empty);

            if (shouldRecordRaidTime)
                messageLog.AddMessage($"Raid completed — total time in raid: {completedRaidTime:hh\\:mm\\:ss}.", "info");
        }

        private void TimerRaid_Elapsed(object state)
        {
            if (cancellationTokenSource.IsCancellationRequested)
                return;

            TimeInRaidTime = raidStopwatch.Elapsed;

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
