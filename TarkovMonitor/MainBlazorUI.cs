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

namespace TarkovMonitor
{
    public partial class MainBlazorUI : Form
    {
        private GameWatcher eft;
        private MessageLog messageLog;
        private List<TarkovDevApi.Quest> quests;
        private List<TarkovDevApi.Map> maps;
        private List<TarkovDevApi.Item> items;

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

            // Singleton message log used to record and display messages for the TarkovMonitor
            messageLog = new MessageLog();

            // Items collection
            items = new List<TarkovDevApi.Item>();
            updateItems();

            quests = new List<TarkovDevApi.Quest>();
            updateQuests();

            maps = new List<TarkovDevApi.Map>();
            updateMaps();

            // Creates the dependency injection services which are the in-betweens for the Blazor interface and the rest of the C# application.
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            services.AddMudServices();
            services.AddSingleton<GameWatcher>(eft);
            services.AddSingleton<MessageLog>(messageLog);
            services.AddSingleton<List<TarkovDevApi.Item>>(items);
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
                items = await TarkovDevApi.GetItems();
                messageLog.AddMessage($"Retrieved {items.Count} items from tarkov.dev");
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
                quests = await TarkovDevApi.GetQuests();
                messageLog.AddMessage($"Retrieved {quests.Count} quests from tarkov.dev");
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
                maps = await TarkovDevApi.GetMaps();
                messageLog.AddMessage($"Retrieved {maps.Count} maps from tarkov.dev");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating maps: {ex.Message}");
            }
        }

        private void Eft_FleaSold(object? sender, GameWatcher.FleaSoldEventArgs e)
        {
            if (items != null)
            {
                List<string> received = new();
                foreach (var receivedId in e.ReceivedItems.Keys)
                {
                    received.Add($"{e.ReceivedItems[receivedId]} {items.Find(item => item.id == receivedId).name}");
                }
                var soldItemName = items.Find(item => item.id == e.SoldItemId).name;
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
            var map = maps.Find(m => m.nameId == mapName);
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
                var map = maps.Find(m => m.nameId == mapName);
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
