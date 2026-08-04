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
        private readonly object raidCompletionLock = new();
        private string activeRaidId = "";
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
            TarkovTracker.ProgressRetrieved += TarkovTracker_ProgressRetrieved;

            RunThroughRemainingTime = Properties.Settings.Default.runthroughTime;
            
            this.eft.RaidStarted += Eft_RaidStarted;
            this.eft.RaidStopping += Eft_RaidStopping;
            this.eft.RaidExited += Eft_RaidExited;
            this.eft.RaidEnded += Eft_RaidEnded;

            timerRaid = new System.Threading.Timer(TimerRaid_Elapsed, null, Timeout.Infinite, 1000);
            timerRunThrough = new System.Threading.Timer(TimerRunThrough_Elapsed, null, Timeout.Infinite, 1000);
            timerScavCooldown = new System.Threading.Timer(timerScavCooldown_Elapsed, null, Timeout.Infinite, 1000);
        }

        private void TarkovTracker_ProgressRetrieved(object? sender, TarkovTracker.ProgressRetrievedEventArgs e)
        {
            var profile = GameWatcher.CurrentProfile.Snapshot();
            if (e.ProfileId != profile.Id || !profile.SupportsScavCooldown)
            {
                return;
            }

            ScavCooldownTime = TimeSpan.FromSeconds(TarkovDev.ScavCooldownSeconds(profile.Type, e.Progress));
            Debug.WriteLine($"ScavCooldownTime: {ScavCooldownTime}");

            // A named handler can actually be removed; subtracting a new lambda (the
            // previous implementation) leaves the original subscription attached.
            TarkovTracker.ProgressRetrieved -= TarkovTracker_ProgressRetrieved;
        }

        private void Eft_RaidStarted(object? sender, RaidInfoEventArgs e)
        {
            if (e.RaidInfo.Reconnected)
                return;

            lock (raidCompletionLock)
            {
                TimeInRaidTime = TimeSpan.Zero;
                RunThroughRemainingTime = Properties.Settings.Default.runthroughTime;
                IsRaidActive = true;
                activeRaidId = e.RaidInfo.RaidId;
                raidStopwatch.Restart();

                timerRaid.Change(0, 1000);
                timerRunThrough.Change(0, 1000);
            }

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

        private void Eft_RaidStopping(object? sender, EventArgs e)
        {
            CompleteActiveRaid();
        }

        private void Eft_RaidExited(object? sender, RaidExitedEventArgs e)
        {
            CompleteActiveRaid(e.RaidId, requireMatchingRaidId: true);
        }

        private void Eft_RaidEnded(object? sender, RaidInfoEventArgs e)
        {
            // Local Seasonal flows do not emit the output-log stopping marker,
            // so the profile-return event remains the required fallback.
            CompleteActiveRaid(e.RaidInfo.RaidId, requireMatchingRaidId: true);

            Debug.WriteLine($"Eft_RaidEnded: {e.RaidInfo.RaidType}");

            if (!e.RaidInfo.Reconnected
                && e.RaidInfo.Profile.SupportsScavCooldown
                && (e.RaidInfo.RaidType == RaidType.Scav || e.RaidInfo.RaidType == RaidType.PVE))
            {
                timerScavCooldown.Change(0, 1000);
            }

            ScavCooldownTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = ScavCooldownTime
            });
        }

        private bool CompleteActiveRaid(string? completedRaidId = null, bool requireMatchingRaidId = false)
        {
            TimeSpan completedRaidTime;
            lock (raidCompletionLock)
            {
                if (!IsRaidActive)
                {
                    return false;
                }

                if (requireMatchingRaidId)
                {
                    var activeRaidHasId = !string.IsNullOrWhiteSpace(activeRaidId);
                    var completedRaidHasId = !string.IsNullOrWhiteSpace(completedRaidId);
                    var idsMatch = activeRaidHasId
                        ? completedRaidHasId && string.Equals(activeRaidId, completedRaidId, StringComparison.OrdinalIgnoreCase)
                        : !completedRaidHasId;
                    if (!idsMatch)
                    {
                        return false;
                    }
                }

                completedRaidTime = raidStopwatch.Elapsed;
                IsRaidActive = false;
                activeRaidId = "";
                raidStopwatch.Stop();
                TimeInRaidTime = completedRaidTime;
                RunThroughRemainingTime = TimeSpan.Zero;
                timerRunThrough.Change(Timeout.Infinite, Timeout.Infinite);
                timerRaid.Change(Timeout.Infinite, Timeout.Infinite);
            }

            RunThroughTimerChanged?.Invoke(this, new TimerChangedEventArgs()
            {
                TimerValue = RunThroughRemainingTime
            });

            RaidActiveChanged?.Invoke(this, EventArgs.Empty);
            messageLog.AddMessage($"Raid completed — total time in raid: {completedRaidTime:hh\\:mm\\:ss}.", "info");
            return true;
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
