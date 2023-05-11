using MudBlazor.Services;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using NAudio.Wave;
using TarkovMonitor.GroupLoadout;
using System.Text.Json;

namespace TarkovMonitor
{
    public partial class MainBlazorUI : Form
    {
        private readonly GameWatcher eft;
        private readonly MessageLog messageLog;
        private readonly LogRepository logRepository;
        private readonly GroupManager groupManager;
        private readonly TarkovDevRepository tarkovdevRepository;
        private TarkovTracker.ProgressResponse progress = new ();

        public MainBlazorUI()
        {
            InitializeComponent();
            eft = new GameWatcher();
            eft.Start();

            // Add event watchers
            eft.FleaSold += Eft_FleaSold;
            eft.DebugMessage += Eft_DebugMessage;
            eft.ExceptionThrown += Eft_ExceptionThrown;
            eft.RaidLoaded += Eft_RaidLoaded;
            eft.RaidExited += Eft_RaidExited;
            eft.TaskStarted += Eft_TaskStarted;
            eft.TaskFailed += Eft_TaskFailed;
            eft.TaskFinished += Eft_TaskFinished;
            eft.NewLogMessage += Eft_NewLogMessage;
            eft.GroupInvite += Eft_GroupInvite;
            eft.MatchingAborted += Eft_GroupStaleEvent;
            eft.GameStarted += Eft_GroupStaleEvent;
            eft.MatchFound += Eft_MatchFound;
            eft.MatchingStarted += Eft_MatchingStarted;

            // Singleton message log used to record and display messages for TarkovMonitor
            messageLog = new MessageLog();

            // Singleton log repository to record, display, and analyze logs for TarkovMonitor
            logRepository = new LogRepository();

            // Singleton Group tracker
            groupManager = new GroupManager();

            // Singleton tarkov.dev repository (to DI the results of the queries)
            tarkovdevRepository = new TarkovDevRepository();

            // Update tarkov.dev Repository data
            UpdateItems();
            UpdateTasks();
            UpdateMaps();

            // TarkovTracker initialization
            TarkovTracker.Init();

            UpdateProgress();

            // Creates the dependency injection services which are the in-betweens for the Blazor interface and the rest of the C# application.
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            services.AddMudServices();
            services.AddSingleton<GameWatcher>(eft);
            services.AddSingleton<MessageLog>(messageLog);
            services.AddSingleton<LogRepository>(logRepository);
            services.AddSingleton<GroupManager>(groupManager);
            services.AddSingleton<TarkovDevRepository>(tarkovdevRepository);
            blazorWebView1.HostPage = "wwwroot\\index.html";
            blazorWebView1.Services = services.BuildServiceProvider();
            blazorWebView1.RootComponents.Add<TarkovMonitor.Blazor.App>("#app");

            blazorWebView1.WebView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;
        }

        private void Eft_MatchingStarted(object? sender, EventArgs e)
        {
            try
            {
                var failedTasks = new List<TarkovDevApi.Task>();
                foreach (var taskStatus in progress.data.tasksProgress)
                {
                    Debug.WriteLine(JsonSerializer.Serialize(taskStatus));
                    if (taskStatus.failed)
                    {
                        var task = tarkovdevRepository.Tasks.Find(match: t => t.id == taskStatus.id);
                        if (task == null)
                        {
                            continue;
                        }
                        if (task.restartable)
                        {
                            failedTasks.Add(task);
                        }
                    }
                }
                if (failedTasks.Count > 0)
                {
                    if (Properties.Settings.Default.restartTaskAlert) PlaySoundFromResource(Properties.Resources.restart_failed_tasks);
                    foreach (var task in failedTasks)
                    {
                        messageLog.AddMessage($"Failed task {task.name} should be restarted", "quest", task.wikiLink);
                    }
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error on matching started: {ex.Message}");
                Debug.WriteLine(ex.ToString());
            }
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
                tarkovdevRepository.Items = await TarkovDevApi.GetItems();
                messageLog.AddMessage($"Retrieved {String.Format("{0:n0}", tarkovdevRepository.Items.Count)} items from tarkov.dev", "update");
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
                tarkovdevRepository.Tasks = await TarkovDevApi.GetTasks();
                messageLog.AddMessage($"Retrieved {tarkovdevRepository.Tasks.Count} tasks from tarkov.dev", "update");
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
                tarkovdevRepository.Maps = await TarkovDevApi.GetMaps();
                messageLog.AddMessage($"Retrieved {tarkovdevRepository.Maps.Count} maps from tarkov.dev", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating maps: {ex.Message}");
            }
        }

        private async Task UpdateProgress()
        {
            if (Properties.Settings.Default.tarkovTrackerToken == "")
            {
                return;
            }
            try
            {
                progress = await TarkovTracker.GetProgress();
                messageLog.AddMessage($"Retrieved level {progress.data.playerLevel} progress from Tarkov Tracker", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating maps: {ex.Message}");
            }
        }

        private void Eft_MatchFound(object? sender, GameWatcher.MatchFoundEventArgs e)
        {
            if (Properties.Settings.Default.matchFoundAlert) PlaySoundFromResource(Properties.Resources.match_found);
        }

        private void Eft_NewLogMessage(object? sender, LogMonitor.NewLogEventArgs e)
        {
            logRepository.AddLog(e.NewMessage, e.Type.ToString());
        }

        private void Eft_GroupInvite(object? sender, GameWatcher.GroupInviteEventArgs e)
        {
            groupManager.UpdateGroupMember(e.PlayerInfo.Nickname, new GroupMember(e.PlayerInfo.Nickname, e.PlayerLoadout));
            messageLog.AddMessage($"{e.PlayerInfo.Nickname} ({e.PlayerLoadout.Info.Side.ToUpper()} {e.PlayerLoadout.Info.Level}) accepted group invite.", "group");
        }

        private void Eft_TaskFinished(object? sender, GameWatcher.TaskEventArgs e)
        {
            tarkovdevRepository.Tasks.ForEach(async task => {
                if (task.id == e.TaskId)
                {
                    messageLog.AddMessage($"Completed task {task.name}", "quest");
                    if (Properties.Settings.Default.tarkovTrackerToken.Length > 0)
                    {
                        try
                        {
                            var response = await TarkovTracker.SetTaskComplete(task.id);
                            //messageLog.AddMessage(response, "quest");
                        }
                        catch (Exception ex)
                        {
                            messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
                        }
                    }
                    return;
                }
                foreach (var failCondition in task.failConditions)
                {
                    if (failCondition.task == null)
                    {
                        continue;
                    }
                    if (failCondition.task.id == e.TaskId && failCondition.status.Contains("complete"))
                    {
                        //Eft_TaskFailed(sender, new GameWatcher.TaskEventArgs { TaskId = task.id });
                        foreach (var taskStatus in progress.data.tasksProgress) { 
                            if (taskStatus.id == failCondition.task.id)
                            {
                                taskStatus.failed = true;
                                break;
                            }
                        }
                        break;
                    }
                }
            });
        }

        private async void Eft_TaskFailed(object? sender, GameWatcher.TaskEventArgs e)
        {
            var task = tarkovdevRepository.Tasks.Find(t => t.id == e.TaskId);
            if (task == null)
            {
                return;
            }

            messageLog.AddMessage($"Failed task {task.name}", "quest", task.wikiLink);
            if (!task.restartable)
            {
                try
                {
                    var response = await TarkovTracker.SetTaskFailed(task.id);
                    //messageLog.AddMessage(response, "quest");
                }
                catch (Exception ex)
                {
                    messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
                }
            }
            foreach (var taskStatus in progress.data.tasksProgress)
            {
                if (taskStatus.id == e.TaskId)
                {
                    taskStatus.failed = true;
                    TarkovTracker.SetTaskFailed(e.TaskId);
                    break;
                }
            }
        }

        private void Eft_TaskStarted(object? sender, GameWatcher.TaskEventArgs e)
        {
            var task = tarkovdevRepository.Tasks.Find(t => t.id == e.TaskId);
            if (task == null)
            {
                return;
            }

            messageLog.AddMessage($"Started task {task.name}", "quest", task.wikiLink);
            foreach (var taskStatus in progress.data.tasksProgress)
            {
                if (taskStatus.id == e.TaskId)
                {
                    if (taskStatus.failed)
                    {
                        taskStatus.failed = false;
                        TarkovTracker.SetTaskUncomplete(e.TaskId);
                    }
                    break;
                }
            }
        }

        private void Eft_FleaSold(object? sender, GameWatcher.FleaSoldEventArgs e)
        {
            if (tarkovdevRepository.Items != null)
            {
                List<string> received = new();
                foreach (var receivedId in e.ReceivedItems.Keys)
                {
                    received.Add($"{String.Format("{0:n0}", e.ReceivedItems[receivedId])} {tarkovdevRepository.Items.Find(item => item.id == receivedId).name}");
                }
                var soldItemName = tarkovdevRepository.Items.Find(item => item.id == e.SoldItemId).name;
                messageLog.AddMessage($"{e.Buyer} purchased {String.Format("{0:n0}", e.SoldItemCount)} {soldItemName} for {String.Join(", ", received.ToArray())}", "flea");
            }
        }
        private void Eft_DebugMessage(object? sender, GameWatcher.DebugEventArgs e)
        {
            messageLog.AddMessage(e.Message, "debug");
        }

        private void Eft_ExceptionThrown(object? sender, GameWatcher.ExceptionEventArgs e)
        {
            messageLog.AddMessage($"Error watching logs: {e.Exception.Message}\n{e.Exception.StackTrace}", "exception");
        }

        private async void Eft_RaidLoaded(object? sender, GameWatcher.RaidLoadedEventArgs e)
        {
            if (Properties.Settings.Default.raidStartAlert) PlaySoundFromResource(Properties.Resources.raid_starting);
            var mapName = e.Map;
            var map = tarkovdevRepository.Maps.Find(m => m.nameId == mapName);
            if (map != null) mapName = map.name;
            messageLog.AddMessage($"Starting raid on {mapName} as {e.RaidType} after matching for {e.QueueTime} seconds");
            if (!Properties.Settings.Default.submitQueueTime) return;
            try
            {
                var response = await TarkovDevApi.PostQueueTime(e.Map, (int)Math.Round(e.QueueTime), e.RaidType);
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error submitting queue time: {ex.Message}", "exception");
            }
        }

        private void Eft_RaidExited(object? sender, GameWatcher.RaidExitedEventArgs e)
        {
            groupManager.Stale = true;
            try
            {
                var mapName = e.Map;
                var map = tarkovdevRepository.Maps.Find(m => m.nameId == mapName);
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
    }
}
