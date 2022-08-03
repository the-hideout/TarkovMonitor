using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MudBlazor.Services;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using NAudio.Wave;
using TarkovMonitor.GroupLoadout;

namespace TarkovMonitor
{
    public partial class MainBlazorUI : Form
    {
        private GameWatcher eft;
        private MessageLog messageLog;
        private LogRepository logRepository;
        private GroupManager groupManager;
        private TarkovDevRepository tarkovdevRepository;

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
            eft.QuestModified += Eft_QuestModified;
            eft.NewLogMessage += Eft_NewLogMessage;
            eft.GroupInvite += Eft_GroupInvite;

            // Singleton message log used to record and display messages for TarkovMonitor
            messageLog = new MessageLog();

            // Singleton log repository to record, display, and analyze logs for TarkovMonitor
            logRepository = new LogRepository();

            // Singleton Group tracker
            groupManager = new GroupManager();

            // Singleton tarkov.dev repository (to DI the results of the queries)
            tarkovdevRepository = new TarkovDevRepository();

            // Update tarkov.dev Repository data
            updateItems();
            updateQuests();
            updateMaps();

            // TarkovTracker initialization
            TarkovTracker.Init();

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

        private void WebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (Debugger.IsAttached) blazorWebView1.WebView.CoreWebView2.OpenDevToolsWindow();
        }

        private async Task updateItems()
        {
            try
            {
                tarkovdevRepository.Items = await TarkovDevApi.GetItems();
                messageLog.AddMessage($"Retrieved {tarkovdevRepository.Items.Count} items from tarkov.dev", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating items: {ex.Message}");
            }
        }

        private async Task updateQuests()
        {
            try
            {
                tarkovdevRepository.Quests = await TarkovDevApi.GetQuests();
                messageLog.AddMessage($"Retrieved {tarkovdevRepository.Quests.Count} quests from tarkov.dev", "update");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating quests: {ex.Message}");
            }
        }

        private async Task updateMaps()
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

        private void Eft_NewLogMessage(object? sender, LogMonitor.NewLogEventArgs e)
        {
            logRepository.AddLog(e.NewMessage, e.Type.ToString());
        }

        private void Eft_GroupInvite(object? sender, GameWatcher.GroupInviteEventArgs e)
        {
            groupManager.UpdateGroupMember(e.PlayerInfo.Nickname, new GroupMember(e.PlayerInfo.Nickname, e.PlayerLoadout));
            messageLog.AddMessage($"{e.PlayerInfo.Nickname} ({e.PlayerLoadout.Info.Side.ToUpper()} {e.PlayerLoadout.Info.Level}) accepted group invite.", "group");
        }

        private async void Eft_QuestModified(object? sender, GameWatcher.QuestEventArgs e)
        {
            foreach (var quest in tarkovdevRepository.Quests)
            {
                if (e.Status == GameWatcher.QuestStatus.Started && (quest.descriptionMessageId == e.MessageId || quest.startMessageId == e.MessageId))
                {
                    messageLog.AddMessage($"Started quest {quest.name}", "quest");
                    return;
                }
                if (e.Status == GameWatcher.QuestStatus.Finished && quest.successMessageId == e.MessageId)
                {
                    messageLog.AddMessage($"Completed quest {quest.name}", "quest");
                    if (quest.tarkovDataId != null)
                    {
                        var response = await TarkovTracker.SetQuestComplete((int)quest.tarkovDataId);
                        messageLog.AddMessage(response);
                    }
                    return;
                }
                if (e.Status == GameWatcher.QuestStatus.Failed && quest.failMessageId == e.MessageId)
                {
                    messageLog.AddMessage($"Failed quest {quest.name}", "quest");
                    return;
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
                    received.Add($"{e.ReceivedItems[receivedId]} {tarkovdevRepository.Items.Find(item => item.id == receivedId).name}");
                }
                var soldItemName = tarkovdevRepository.Items.Find(item => item.id == e.SoldItemId).name;
                messageLog.AddMessage($"{e.Buyer} purchesed {e.soldItemCount} {soldItemName} for {String.Join(", ", received.ToArray())}", "flea");
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
            messageLog.AddMessage($"Finished queueing for {mapName} as {e.RaidType} in {e.QueueTime} seconds", "queue");
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
            try
            {
                var mapName = e.Map;
                var map = tarkovdevRepository.Maps.Find(m => m.nameId == mapName);
                if (map != null) mapName = map.name;
                messageLog.AddMessage($"Exited {mapName} raid ({e.RaidId})", "raidleave");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating log message from event: {ex.Message}", "exception");
            }
        }

        private void PlaySoundFromResource(byte[] resource)
        {
            Stream stream = new MemoryStream(resource);
            var reader = new NAudio.Wave.Mp3FileReader(stream);
            var waveOut = new WaveOut();
            waveOut.Init(reader);
            waveOut.Play();
        }
    }
}
