using MudBlazor.Services;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using NAudio.Wave;
using TarkovMonitor.GroupLoadout;
using System.Globalization;

namespace TarkovMonitor
{
    public partial class MainBlazorUI : Form
    {
        private readonly GameWatcher eft;
        private readonly MessageLog messageLog;
        private readonly LogRepository logRepository;
        private readonly GroupManager groupManager;

        public MainBlazorUI()
        {
            InitializeComponent();
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
            eft.RaidLoaded += Eft_RaidLoaded;
            eft.RaidExited += Eft_RaidExited;
            eft.TaskStarted += Eft_TaskStarted;
            eft.TaskFailed += Eft_TaskFailed;
            eft.TaskFinished += Eft_TaskFinished;
            eft.NewLogData += Eft_NewLogData;
            eft.GroupInviteSend += Eft_GroupInviteSend;
            eft.GroupInviteAccept += Eft_GroupInviteAccept;
            eft.GroupUserLeave += Eft_GroupUserLeave;
            eft.GroupReady += Eft_GroupReady;
			eft.GroupDisbanded += Eft_GroupDisbanded;
            eft.MatchingAborted += Eft_GroupStaleEvent;
            eft.GameStarted += Eft_GroupStaleEvent;
            eft.MapLoading += Eft_MapLoading;
            eft.MatchFound += Eft_MatchFound;
            TarkovTracker.ProgressRetrieved += TarkovTracker_ProgressRetrieved;

            // Update tarkov.dev Repository data
            UpdateItems();
            UpdateTasks();
            UpdateMaps();
            TarkovDev.StartAutoUpdates();

            InitializeProgress();

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
        }

        private void Eft_MapLoading(object? sender, EventArgs e)
        {
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
                if (Properties.Settings.Default.restartTaskAlert)
                {
                    PlaySoundFromResource(Properties.Resources.restart_failed_tasks);
                }
                foreach (var task in failedTasks)
                {
                    messageLog.AddMessage($"Failed task {task.name} should be restarted", "quest", task.wikiLink);
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error on matching started: {ex.Message}");
            }
        }

        private void Eft_GroupInviteSend(object? sender, GroupInviteSendEventArgs e)
        {
            List<string> memberNames = new();
            foreach (var member in e.Members)
            {
                memberNames.Add($"{member.PlayerInfo.Nickname} ({member.PlayerInfo.Level} {member.PlayerInfo.Side.ToUpper()})");
            }
            messageLog.AddMessage($"Group invite: {String.Join(", ", memberNames.ToArray())}", "group");
        }

        private void Eft_GroupUserLeave(object? sender, GroupUserLeaveEventArgs e)
        {
            groupManager.RemoveGroupMember(e.Nickname);
            messageLog.AddMessage($"{e.Nickname} left the group.", "group");
        }

        private void Eft_GroupInviteAccept(object? sender, GroupInviteAcceptEventArgs e)
        {
            var verb = "accepted";
            if (e.InviteType == GroupInviteType.Sent) {
                verb = "sent";
            }
            messageLog.AddMessage($"{e.PlayerInfo.Nickname} ({e.PlayerInfo.Side.ToUpper()} {e.PlayerInfo.Level}) {verb} group invite.", "group");
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

        private void Eft_MatchFound(object? sender, MatchFoundEventArgs e)
        {
            if (Properties.Settings.Default.matchFoundAlert) 
            { 
                PlaySoundFromResource(Properties.Resources.match_found);
            }
            var mapName = e.Map;
            var map = TarkovDev.Maps.Find(m => m.nameId == mapName);
            if (map != null) mapName = map.name;
            messageLog.AddMessage($"Matching complete on {mapName} after {e.QueueTime} seconds");
        }

        private void Eft_NewLogData(object? sender, NewLogDataEventArgs e)
        {
            logRepository.AddLog(e.Data, e.Type.ToString());
        }

        private void Eft_GroupReady(object? sender, GroupReadyEventArgs e)
        {
            groupManager.UpdateGroupMember(e.PlayerInfo.Nickname, new GroupMember(e.PlayerInfo.Nickname, e.PlayerLoadout));
            messageLog.AddMessage($"{e.PlayerInfo.Nickname} ({e.PlayerLoadout.Info.Side.ToUpper()} {e.PlayerLoadout.Info.Level}) ready.", "group");
        }

        private async void Eft_TaskFinished(object? sender, TaskEventArgs e)
        {
            //await AllDataLoaded();
            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            var task = TarkovDev.Tasks.Find(t => t.id == e.TaskId);
            if (task == null)
            {
                //Debug.WriteLine($"Task with id {e.TaskId} not found");
                return;
            }

            messageLog.AddMessage($"Completed task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");
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

        private async void Eft_TaskFailed(object? sender, TaskEventArgs e)
        {
            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            var task = TarkovDev.Tasks.Find(t => t.id == e.TaskId);
            if (task == null)
            {
                return;
            }

            messageLog.AddMessage($"Failed task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");
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

        private async void Eft_TaskStarted(object? sender, TaskEventArgs e)
        {
            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            var task = TarkovDev.Tasks.Find(t => t.id == e.TaskId);
            if (task == null)
            {
                return;
            }
            
            messageLog.AddMessage($"Started task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");
            try
            {
                await TarkovTracker.SetTaskUncomplete(e.TaskId);
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
            }
        }

        private async void Eft_FleaSold(object? sender, FleaSoldEventArgs e)
        {
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

        private void Eft_FleaOfferExpired(object? sender, FleaOfferExpiredEventArgs e)
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
            messageLog.AddMessage($"Error watching logs: {e.Exception.Message}\n{e.Exception.StackTrace}", "exception");
        }

        private async void Eft_RaidLoaded(object? sender, RaidLoadedEventArgs e)
        {
            if (Properties.Settings.Default.raidStartAlert) PlaySoundFromResource(Properties.Resources.raid_starting);
            var mapName = e.Map;
            var map = TarkovDev.Maps.Find(m => m.nameId == mapName);
            if (map != null) mapName = map.name;
            if (e.RaidType != RaidType.Unknown)
			{
				messageLog.AddMessage($"Starting {e.RaidType} raid on {mapName}");
			} else
            {
				messageLog.AddMessage($"Re-entering raid on {mapName}");
			}
            if (!Properties.Settings.Default.submitQueueTime)
            {
                return;
            }
            if (e.QueueTime == 0 || e.RaidType == RaidType.Unknown)
            {
                return;
            }
            try
            {
                await TarkovDev.PostQueueTime(e.Map, (int)Math.Round(e.QueueTime), e.RaidType.ToString().ToLower());
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error submitting queue time: {ex.Message}", "exception");
            }
        }

        private void Eft_RaidExited(object? sender, RaidExitedEventArgs e)
        {
            groupManager.Stale = true;
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

        private static void PlaySoundFromResource(byte[] resource)
        {
            Stream stream = new MemoryStream(resource);
            var reader = new NAudio.Wave.Mp3FileReader(stream);
            var waveOut = new WaveOut();
            waveOut.Init(reader);
            waveOut.Play();
        }

        private async Task<bool> AllDataLoaded()
        {
            while (TarkovDev.Items.Count == 0 || TarkovDev.Maps.Count == 0 || TarkovDev.Tasks.Count == 0 || TarkovTracker.Progress == null)
            {
                Thread.Sleep(500);
            }
            return true;
        }
    }
}
