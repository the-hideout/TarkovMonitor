using System.Runtime.InteropServices;
using System.Net.Http;
using System.Diagnostics;
using MaterialSkin;
using MaterialSkin.Controls;
using System.Text.RegularExpressions;

namespace TarkovMonitor
{
    public partial class MainWindow : MaterialForm
    {
        private List<TarkovDevApi.Quest> quests;
        private List<TarkovDevApi.Map> maps;
        private List<TarkovDevApi.Item> items;
        private GameWatcher eft;
        public MainWindow()
        {
            InitializeComponent();

            var materialSkinManager = MaterialSkinManager.Instance;
            materialSkinManager.AddFormToManage(this);
            materialSkinManager.Theme = MaterialSkinManager.Themes.LIGHT;
            materialSkinManager.ColorScheme = new ColorScheme(Primary.BlueGrey800, Primary.BlueGrey900, Primary.BlueGrey500, Accent.LightBlue200, TextShade.WHITE);

            eft = new GameWatcher();
            eft.RaidExited += Eft_RaidExited;
            eft.QuestModified += Eft_QuestModified;
            eft.QueueComplete += Eft_QueueComplete;
            eft.FleaSold += Eft_FleaSold;
            eft.NewLogMessage += Eft_NewLogMessage;
            //TarkovTracker.Initialized += TarkovTracker_Initialized;
            TarkovTracker.Init();
            quests = new List<TarkovDevApi.Quest>();
            maps = new List<TarkovDevApi.Map>();
            updateQuests();
            updateMaps();
            updateItems();
            chkQueue.Checked = Properties.Settings.Default.submitQueueTime;
            if (Properties.Settings.Default.tarkovTrackerToken.Length > 0)
            {
                txtToken.Text = Properties.Settings.Default.tarkovTrackerToken;
                TarkovTracker.SetToken(Properties.Settings.Default.tarkovTrackerToken);
            }
            if (txtToken.Text.Length == 0) tabsMain.SelectedIndex = 1;
        }

        private void Eft_NewLogMessage(object? sender, LogMonitor.NewLogEventArgs e)
        {
            txtLogs.Invoke((MethodInvoker)delegate {
                txtLogs.AppendText("\n" + e.Type.ToString() + "\n" + e.NewMessage);
                txtLogs.SelectionStart = txtLogs.TextLength;
                txtLogs.ScrollToCaret();
                //txtLogs.Text += "\n" + e.Type.ToString() + "\n" + e.NewMessage;
            });
            //txtLogs.scro
        }

        private void Eft_FleaSold(object? sender, GameWatcher.FleaSoldEventArgs e)
        {
            List<string> received = new();
            foreach (var receivedId in e.ReceivedItems.Keys)
            {
                received.Add($"{e.ReceivedItems[receivedId]} {items.Find(item => item.id == receivedId).name}");
            }
            var soldItemName = items.Find(item => item.id == e.SoldItemId).name;
            logMessage($"{e.Buyer} purchesed {e.soldItemCount} {soldItemName} for {String.Join(", ", received.ToArray())}");
        }

        private async void Eft_QueueComplete(object? sender, GameWatcher.QueueEventArgs e)
        {
            var mapName = e.Map;
            var map = maps.Find(m => m.nameId == mapName);
            if (map != null) mapName = map.name;
            logMessage($"Finished queueing for {mapName} as {e.RaidType} in {e.QueueTime} seconds");
            if (!Properties.Settings.Default.submitQueueTime) return;
            try
            {
                var response = await TarkovDevApi.PostQueueTime(e.Map, (int)Math.Round(e.QueueTime), e.RaidType);
                //logMessage($")
            }
            catch (Exception ex)
            {
                logMessage($"Error submitting queue time: {ex.Message}");
            }
        }

        private async void Eft_QuestModified(object? sender, GameWatcher.QuestEventArgs e)
        {
            foreach (var quest in quests)
            {
                if (e.Status == GameWatcher.QuestStatus.Started && quest.startMessageId == e.MessageId)
                {
                    logMessage($"Started quest {quest.name}");
                    return;
                }
                if (e.Status == GameWatcher.QuestStatus.Finished && quest.successMessageId == e.MessageId)
                {
                    logMessage($"Completed quest {quest.name}");
                    if (quest.tarkovDataId != null)
                    {
                        var response = await TarkovTracker.SetQuestComplete((int)quest.tarkovDataId);
                        logMessage(response);
                    }
                    return;
                }
                if (e.Status == GameWatcher.QuestStatus.Failed && quest.failMessageId == e.MessageId)
                {
                    logMessage($"Failed quest {quest.name}");
                    return;
                }
            }
            logMessage($"{e.Status} quest");
        }

        private void Eft_RaidExited(object? sender, GameWatcher.RaidExitedEventArgs e)
        {
            try
            {
                var mapName = e.Map;
                var map = maps.Find(m => m.nameId == mapName);
                if (map != null) mapName = map.name;
                logMessage($"Exited {mapName} raid ({e.RaidId})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating log message from event: {ex.Message}");
            }
        }

        private void TarkovTracker_Initialized(object? sender, TarkovTracker.InitializedEventArgs e)
        {
            //Debug.WriteLine(e.Document);
        }

        private void logMessage(string message)
        {
            txtMessages.Invoke((MethodInvoker)delegate {
                txtMessages.AppendText("\n" + message);
                txtMessages.SelectionStart = txtMessages.TextLength;
                txtMessages.ScrollToCaret();
            });
        }

        private async Task updateQuests()
        {
            try
            {
                quests = await TarkovDevApi.GetQuests();
                logMessage($"Retrieved {quests.Count} quests from tarkov.dev");
            }
            catch (Exception ex)
            {
                logMessage($"Error updating quests: {ex.Message}");
            }
        }

        private async Task updateMaps()
        {
            try
            {
                maps = await TarkovDevApi.GetMaps();
                logMessage($"Retrieved {maps.Count} maps from tarkov.dev");
            }
            catch (Exception ex)
            {
                logMessage($"Error updating maps: {ex.Message}");
            }
        }

        private async Task updateItems()
        {
            try
            {
                items = await TarkovDevApi.GetItems();
                logMessage($"Retrieved {items.Count} items from tarkov.dev");
            }
            catch (Exception ex)
            {
                logMessage($"Error updating items: {ex.Message}");
            }
        }

        private async void btnTestToken_Click(object sender, EventArgs e)
        {
            var token = txtToken.Text;
            if (token.Length == 0)
            {
                new MaterialDialog(this, "Missing Token", "You must provide a token to test.").ShowDialog(this);
                return;
            }
            try
            {
                var tokenResponse = await TarkovTracker.TestTokenAsync(token);
                if (!tokenResponse.permissions.Contains("WP"))
                {
                    new MaterialDialog(this, "Missing Permissions", "That token does not have write permissions, which are needed to mark quests completed.").ShowDialog(this);
                    return;
                }
                new MaterialDialog(this, "Success", "Token authenticated successfully!").ShowDialog(this);
                logMessage("Token test success!");
            }
            catch (Exception ex)
            {
                logMessage(ex.Message);
            }
        }

        private void materialLabel1_Click(object sender, EventArgs e)
        {
            var url = "https://tarkovtracker.io/settings/";
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch
            {
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    url = url.Replace("&", "^&");
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
                else
                {
                    throw;
                }
            }
        }

        private void panelSettings_SaveClick(object sender, EventArgs e)
        {
            TarkovTracker.SetToken(txtToken.Text);
            Properties.Settings.Default.tarkovTrackerToken = txtToken.Text;
            Properties.Settings.Default.submitQueueTime = chkQueue.Checked;
            Properties.Settings.Default.Save();
        }

        private void panelSettings_CancelClick(object sender, EventArgs e)
        {
            txtToken.Text = Properties.Settings.Default.tarkovTrackerToken;
            chkQueue.Checked = Properties.Settings.Default.submitQueueTime;
        }
    }
}