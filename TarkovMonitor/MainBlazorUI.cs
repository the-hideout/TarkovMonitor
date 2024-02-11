using MudBlazor.Services;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using TarkovMonitor.GroupLoadout;
using System.Globalization;
using System.ComponentModel;

namespace TarkovMonitor
{
    public partial class MainBlazorUI : Form
    {
        private readonly GameWatcher eft;
        private readonly MessageLog messageLog;
        private readonly LogRepository logRepository;
        private readonly GroupManager groupManager;
        private readonly System.Timers.Timer runthroughTimer;
        private readonly System.Timers.Timer scavCooldownTimer;

        public MainBlazorUI()
        {
            InitializeComponent();
            if (Properties.Settings.Default.upgradeRequired)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.upgradeRequired = false;
                Properties.Settings.Default.Save();
            }
            this.TopMost = Properties.Settings.Default.stayOnTop;
            Properties.Settings.Default.PropertyChanged += (object sender, PropertyChangedEventArgs e) => {
                if (e.PropertyName != "stayOnTop")
                {
                    return;
                }
                this.TopMost = Properties.Settings.Default.stayOnTop;
            };
            eft = new GameWatcher();
            eft.Start();

            // Singleton message log used to record and display messages for TarkovMonitor
            messageLog = new MessageLog();
            messageLog.AddMessage($"TarkovMonitor v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()}");

            // Singleton log repository to record, display, and analyze logs for TarkovMonitor
            logRepository = new LogRepository();

            // Singleton Group tracker
            groupManager = new GroupManager();

            // Singleton tarkov.dev repository (to DI the results of the queries)
            //tarkovdevRepository = new TarkovDevRepository();

            // Add event watchers
            eft.FleaSold += Eft_FleaSold;
            eft.FleaOfferExpired += Eft_FleaOfferExpired;
            eft.DebugMessage += Eft_DebugMessage;
            eft.ExceptionThrown += Eft_ExceptionThrown;
            eft.RaidStarting += Eft_RaidStarting;
            eft.RaidStarted += Eft_RaidStart;
            eft.RaidExited += Eft_RaidExited;
            eft.RaidEnded += Eft_RaidEnded;
            eft.ExitedPostRaidMenus += Eft_ExitedPostRaidMenus;
            eft.TaskStarted += Eft_TaskStarted;
            eft.TaskFailed += Eft_TaskFailed;
            eft.TaskFinished += Eft_TaskFinished;
            eft.NewLogData += Eft_NewLogData;
            eft.GroupInviteAccept += Eft_GroupInviteAccept;
            eft.GroupUserLeave += Eft_GroupUserLeave;
            eft.GroupRaidSettings += Eft_GroupRaidSettings;
            eft.GroupMemberReady += Eft_GroupMemberReady;
            eft.GroupDisbanded += Eft_GroupDisbanded;
            eft.MatchingAborted += Eft_GroupStaleEvent;
            eft.GameStarted += Eft_GroupStaleEvent;
            eft.MapLoading += Eft_MapLoading;
            eft.MatchFound += Eft_MatchFound;
            eft.MapLoaded += Eft_MapLoaded;
            eft.PlayerPosition += Eft_PlayerPosition;

            TarkovTracker.ProgressRetrieved += TarkovTracker_ProgressRetrieved;

            UpdateCheck.NewVersion += UpdateCheck_NewVersion;
            UpdateCheck.Error += UpdateCheck_Error;

            SocketClient.ExceptionThrown += SocketClient_ExceptionThrown;

            // Update tarkov.dev Repository data
            UpdateItems();
            UpdateTasks();
            UpdateMaps();
            UpdateTraders();
            UpdateHideoutStations();
            TarkovDev.StartAutoUpdates();

            InitializeProgress();
            UpdateCheck.CheckForNewVersion();

            // Creates the dependency injection services which are the in-betweens for the Blazor interface and the rest of the C# application.
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            services.AddMudServices();
            services.AddSingleton<GameWatcher>(eft);
            services.AddSingleton<MessageLog>(messageLog);
            services.AddSingleton<LogRepository>(logRepository);
            services.AddSingleton<GroupManager>(groupManager);
            //services.AddSingleton<TarkovDevRepository>(tarkovdevRepository);
            blazorWebView1.HostPage = "wwwroot\\index.html";
            blazorWebView1.Services = services.BuildServiceProvider();
            blazorWebView1.RootComponents.Add<TarkovMonitor.Blazor.App>("#app");

            blazorWebView1.WebView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;

            runthroughTimer = new System.Timers.Timer(TimeSpan.FromMinutes(7).TotalMilliseconds+TimeSpan.FromSeconds(10).TotalMilliseconds)
            {
                AutoReset = false,
                Enabled = false
            };
            runthroughTimer.Elapsed += RunthroughTimer_Elapsed;
            scavCooldownTimer = new System.Timers.Timer(TimeSpan.FromSeconds(TarkovDev.ScavCooldownSeconds()).TotalMilliseconds)
            {
                AutoReset = false,
                Enabled = false
            };
            scavCooldownTimer.Elapsed += ScavCooldownTimer_Elapsed;
        }

        private void Eft_ExitedPostRaidMenus(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.airFilterAlert && TarkovTracker.HasAirFilter())
            {
                Sound.Play("air_filter_off");
            }
        }

        private void ScavCooldownTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (Properties.Settings.Default.scavCooldownAlert)
            {
                Sound.Play("scav_available");
                messageLog.AddMessage("Player scav available", "info");
            }
        }

        private void RunthroughTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (Properties.Settings.Default.runthroughAlert)
            {
                Sound.Play("runthrough_over");
            }
        }

        private void Eft_RaidEnded(object? sender, RaidInfoEventArgs e)
        {
            groupManager.Stale = true;
            var mapName = e.RaidInfo.Map;
            var map = TarkovDev.Maps.Find(m => m.nameId == mapName);
            if (map != null) mapName = map.name;
            messageLog.AddMessage($"Ended {mapName} raid", "raidleave");
            runthroughTimer.Stop();
            if (e.RaidInfo.RaidType == RaidType.Scav && Properties.Settings.Default.scavCooldownAlert)
            {
                scavCooldownTimer.Stop();
                scavCooldownTimer.Interval = TimeSpan.FromSeconds(TarkovDev.ResetScavCoolDown()).TotalMilliseconds;
                scavCooldownTimer.Start();
            }
        }

        private void Eft_GroupRaidSettings(object? sender, GroupRaidSettingsEventArgs e)
        {
            groupManager.ClearGroup();
        }

        private void SocketClient_ExceptionThrown(object? sender, ExceptionEventArgs e)
        {
            messageLog.AddMessage($"Error {e.Context}: {e.Exception.Message}\n{e.Exception.StackTrace}", "exception");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (Properties.Settings.Default.minimizeAtStartup)
            {

                WindowState = FormWindowState.Minimized;
            }
        }

        private void Eft_MapLoaded(object? sender, RaidInfoEventArgs e)
        {
            if (!Properties.Settings.Default.autoNavigateMap)
            {
                return;
            }
            var map = TarkovDev.Maps.Find(m => m.nameId == e.RaidInfo.Map);
            if (map == null)
            {
                return;
            }
            SocketClient.NavigateToMap(map);
        }

        private void Eft_PlayerPosition(object? sender, PlayerPositionEventArgs e)
        {
            var map = TarkovDev.Maps.Find(m => m.nameId == e.RaidInfo.Map);
            if (map == null)
            {
                return;
            }
            messageLog.AddMessage($"Player position on {map.name}: x: {e.Position.X}, y: {e.Position.Y}, z: {e.Position.Z}");
            SocketClient.UpdatePlayerPosition(e);
            if (Properties.Settings.Default.navigateMapOnPositionUpdate)
            {
                SocketClient.NavigateToMap(map);
            }
        }

        private void UpdateCheck_Error(object? sender, UpdateCheckErrorEventArgs e)
        {
            messageLog.AddMessage($"Error checking for new version: {e.Exception.Message}");
        }

        private void UpdateCheck_NewVersion(object? sender, NewVersionEventArgs e)
        {
            messageLog.AddMessage($"New TarkovMonitor version available ({e.Version})!", null, e.Uri.ToString());
        }

        private async void Eft_MapLoading(object? sender, EventArgs e)
        {
            if (TarkovTracker.Progress == null)
            {
                return;
            }
            try
            {
                //await AllDataLoaded();
                var failedTasks = new List<TarkovDev.Task>();
                foreach (var taskStatus in TarkovTracker.Progress.data.tasksProgress)
                {
                    if (!taskStatus.failed)
                    {
                        continue;
                    }
                    var task = TarkovDev.Tasks.Find(match: t => t.id == taskStatus.id);
                    if (task == null)
                    {
                        continue;
                    }
                    if (task.restartable)
                    {
                        failedTasks.Add(task);
                    }
                }
                if (failedTasks.Count == 0)
                {
                    return;
                }
                foreach (var task in failedTasks)
                {
                    messageLog.AddMessage($"Failed task {task.name} should be restarted", "quest", task.wikiLink);
                }
                if (Properties.Settings.Default.restartTaskAlert)
                {
                    await Sound.Play("restart_failed_tasks");
                }
                if (Properties.Settings.Default.airFilterAlert && TarkovTracker.HasAirFilter())
                {
                    Sound.Play("air_filter_on");
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error on matching started: {ex.Message}");
            }
        }

        private void Eft_GroupUserLeave(object? sender, GroupMatchUserLeaveEventArgs e)
        {
            if (e.Nickname != "You")
            {
                groupManager.RemoveGroupMember(e.Nickname);
            }
            messageLog.AddMessage($"{e.Nickname} left the group.", "group");
        }

        private void Eft_GroupInviteAccept(object? sender, GroupEventArgs e)
        {
            messageLog.AddMessage($"{e.Info.Nickname} ({e.Info.Side.ToUpper()} {e.Info.Level}) accepted group invite.", "group");
        }

        private void Eft_GroupDisbanded(object? sender, EventArgs e)
        {
            groupManager.ClearGroup();
        }

        private void TarkovTracker_ProgressRetrieved(object? sender, EventArgs e)
        {
            messageLog.AddMessage($"Retrieved {TarkovTracker.Progress.data.displayName} level {TarkovTracker.Progress.data.playerLevel} {TarkovTracker.Progress.data.pmcFaction} progress from Tarkov Tracker", "update");
        }

        private void Eft_GroupStaleEvent(object? sender, EventArgs e)
        {
            groupManager.Stale = true;
        }

        private void WebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (Debugger.IsAttached) blazorWebView1.WebView.CoreWebView2.OpenDevToolsWindow();
        }

        private async Task UpdateItems()
        {
            try
            {
                await TarkovDev.GetItems();
                messageLog.AddMessage($"Retrieved {String.Format("{0:n0}", TarkovDev.Items.Count)} items from tarkov.dev", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating items: {ex.Message}");
            }
        }

        private async Task UpdateTasks()
        {
            try
            {
                await TarkovDev.GetTasks();
                messageLog.AddMessage($"Retrieved {TarkovDev.Tasks.Count} tasks from tarkov.dev", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating tasks: {ex.Message}");
            }
        }

        private async Task UpdateMaps()
        {
            try
            {
                await TarkovDev.GetMaps();
                messageLog.AddMessage($"Retrieved {TarkovDev.Maps.Count} maps from tarkov.dev", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating maps: {ex.Message}");
            }
        }

        private async Task UpdateTraders()
        {
            try
            {
                await TarkovDev.GetTraders();
                messageLog.AddMessage($"Retrieved {TarkovDev.Traders.Count} traders from tarkov.dev", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating traders: {ex.Message}");
            }
        }

        private async Task UpdateHideoutStations()
        {
            try
            {
                await TarkovDev.GetHideout();
                messageLog.AddMessage($"Retrieved {TarkovDev.Stations.Count} hideout stations from tarkov.dev", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating hideout stations: {ex.Message}");
            }
        }

        private async Task InitializeProgress()
        {
            if (Properties.Settings.Default.tarkovTrackerToken == "")
            {
                messageLog.AddMessage("To automatically track task progress, set your Tarkov Tracker token in Settings");
                return;
            }
            try
            {
                var tokenResponse = await TarkovTracker.TestToken(Properties.Settings.Default.tarkovTrackerToken);
                if (!tokenResponse.permissions.Contains("WP"))
                {
                    messageLog.AddMessage("Your Tarkov Tracker token is missing the required write permissions");
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating progress: {ex.Message}");
            }
        }

        private void Eft_MatchFound(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.matchFoundAlert)
            {
                Sound.Play("match_found");
            }
            var mapName = e.RaidInfo.Map;
            var map = TarkovDev.Maps.Find(m => m.nameId == mapName);
            if (map != null) mapName = map.name;
            messageLog.AddMessage($"Matching complete on {mapName} after {e.RaidInfo.QueueTime} seconds");
        }

        private void Eft_NewLogData(object? sender, NewLogDataEventArgs e)
        {
            try
            {
                //Debug.WriteLine($"MainBlazorUI {e.Type} NewLogData");
                logRepository.AddLog(e.Data, e.Type.ToString());
            } catch (Exception ex)
            {
                messageLog.AddMessage($"{ex.GetType().Name} adding raw lag to repository: "+ex.StackTrace, "exception");
            }
        }

        private void Eft_GroupMemberReady(object? sender, GroupMatchRaidReadyEventArgs e)
        {
            groupManager.UpdateGroupMember(e);
            messageLog.AddMessage($"{e.extendedProfile.Info.Nickname} ({e.extendedProfile.PlayerVisualRepresentation.Info.Side.ToUpper()} {e.extendedProfile.PlayerVisualRepresentation.Info.Level}) ready.", "group");
        }

        private async void Eft_TaskFinished(object? sender, TaskStatusMessageEventArgs e)
        {
            //await AllDataLoaded();
            var task = TarkovDev.Tasks.Find(t => t.id == e.TaskId);
            if (task == null)
            {
                //Debug.WriteLine($"Task with id {e.TaskId} not found");
                return;
            }

            messageLog.AddMessage($"Completed task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            try
            {
                await TarkovTracker.SetTaskComplete(task.id);
                //messageLog.AddMessage(response, "quest");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
            }
        }

        private async void Eft_TaskFailed(object? sender, TaskStatusMessageEventArgs e)
        {
            var task = TarkovDev.Tasks.Find(t => t.id == e.TaskId);
            if (task == null)
            {
                return;
            }

            messageLog.AddMessage($"Failed task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            try
            {
                await TarkovTracker.SetTaskFailed(task.id);
                //messageLog.AddMessage(response, "quest");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
            }
        }

        private async void Eft_TaskStarted(object? sender, TaskStatusMessageEventArgs e)
        {
            var task = TarkovDev.Tasks.Find(t => t.id == e.TaskId);
            if (task == null)
            {
                return;
            }
            messageLog.AddMessage($"Started task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            try
            {
                await TarkovTracker.SetTaskUncomplete(e.TaskId);
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
            }
        }

        private async void Eft_FleaSold(object? sender, FleaSoldMessageEventArgs e)
        {
            Stats.AddFleaSale(e);
            if (TarkovDev.Items == null)
            {
                return;
            }
            List<string> received = new();
            //await AllDataLoaded();
            foreach (var receivedId in e.ReceivedItems.Keys)
            {
                if (receivedId == "5449016a4bdc2d6f028b456f")
                {
                    received.Add(e.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("ru-RU")));
                    continue;
                }
                else if (receivedId == "5696686a4bdc2da3298b456a")
                {
                    received.Add(e.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("en-US")));
                    continue;
                }
                else if (receivedId == "569668774bdc2da2298b4568")
                {
                    received.Add(e.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("de-DE")));
                    continue;
                }
                var receivedItem = TarkovDev.Items.Find(item => item.id == receivedId);
                if (receivedItem == null)
                {
                    continue;
                }
                received.Add($"{String.Format("{0:n0}", e.ReceivedItems[receivedId])} {receivedItem.name}");
            }
            var soldItem = TarkovDev.Items.Find(item => item.id == e.SoldItemId);
            if (soldItem == null)
            {
                return;
            }
            messageLog.AddMessage($"{e.Buyer} purchased {String.Format("{0:n0}", e.SoldItemCount)} {soldItem.name} for {String.Join(", ", received.ToArray())}", "flea", soldItem.link);
        }

        private void Eft_FleaOfferExpired(object? sender, FleaExpiredeMessageEventArgs e)
        {
            if (TarkovDev.Items == null)
            {
                return;
            }
            var unsoldItem = TarkovDev.Items.Find(item => item.id == e.ItemId);
            if (unsoldItem == null)
            {
                return;
            }
            messageLog.AddMessage($"Your offer for {unsoldItem.name} (x{e.ItemCount}) expired", "flea", unsoldItem.link);
        }

        private void Eft_DebugMessage(object? sender, DebugEventArgs e)
        {
            messageLog.AddMessage(e.Message, "debug");
        }

        private void Eft_ExceptionThrown(object? sender, ExceptionEventArgs e)
        {
            messageLog.AddMessage($"Error {e.Context}: {e.Exception.Message}\n{e.Exception.StackTrace}", "exception");
        }

        private void Eft_RaidStarting(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.raidStartAlert)
            {
                // always notify if the GameStarting event appeared
                Sound.Play("raid_starting");
            }
        }

        private async void Eft_RaidStart(object? sender, RaidInfoEventArgs e)
        {
            Stats.AddRaid(e);
            var mapName = e.RaidInfo.Map;
            var map = TarkovDev.Maps.Find(m => m.nameId == mapName);
            if (map != null) mapName = map.name;
            if (!e.RaidInfo.Reconnected && e.RaidInfo.RaidType != RaidType.Unknown)
            {
                messageLog.AddMessage($"Starting {e.RaidInfo.RaidType} raid on {mapName}");
                if (Properties.Settings.Default.raidStartAlert && e.RaidInfo.StartingTime == null)
                {
                    // if there was no GameStarting event in the log, play the notification sound
                    Sound.Play("raid_starting");
                }
            }
            else
            {
                messageLog.AddMessage($"Re-entering raid on {mapName}");
            }
            if (!Properties.Settings.Default.submitQueueTime)
            {
                return;
            }
            if (e.RaidInfo.Reconnected || !e.RaidInfo.Online || e.RaidInfo.QueueTime == 0 || e.RaidInfo.RaidType == RaidType.Unknown)
            {
                return;
            }
            if (Properties.Settings.Default.runthroughAlert && e.RaidInfo.RaidType == RaidType.PMC)
            {
                runthroughTimer.Stop();
                runthroughTimer.Start();
            }
            try
            {
                await TarkovDev.PostQueueTime(e.RaidInfo.Map, (int)Math.Round(e.RaidInfo.QueueTime), e.RaidInfo.RaidType.ToString().ToLower());
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error submitting queue time: {ex.Message}", "exception");
            }
        }

        private void Eft_RaidExited(object? sender, RaidExitedEventArgs e)
        {
            groupManager.Stale = true;
            runthroughTimer.Stop();
            try
            {
                var mapName = e.Map;
                var map = TarkovDev.Maps.Find(m => m.nameId == mapName);
                if (map != null) mapName = map.name;
                messageLog.AddMessage($"Exited {mapName} raid", "raidleave");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating log message from event: {ex.Message}", "exception");
            }
        }

        private void MainBlazorUI_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                Hide();
                notifyIconTarkovMonitor.Visible = true;
            }
        }

        private void notifyIconTarkovMonitor_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            Show();
            this.WindowState = FormWindowState.Normal;
            notifyIconTarkovMonitor.Visible = false;
        }

        private void menuItemQuit_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
