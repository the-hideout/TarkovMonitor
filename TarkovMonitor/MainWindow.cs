using System.Runtime.InteropServices;
using System.Net.Http;
using System.Diagnostics;
using MaterialSkin;
using MaterialSkin.Controls;
using System.Text.Json;
using System.Media;
using NAudio.Wave;

namespace TarkovMonitor
{
    public partial class MainWindow : MaterialForm
    {
        private List<TarkovDevApi.Quest> quests;
        private List<TarkovDevApi.Map> maps;
        private List<TarkovDevApi.Item> items;
        private GameWatcher eft;
        private bool staleGroupList = true;
        private Task questsTask;
        private Task mapsTask;
        private Task itemsTask;
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
            eft.GroupInviteAccepted += Eft_GroupInviteAccepted;
            eft.RaidLoaded += Eft_RaidLoaded;
            eft.FleaSold += Eft_FleaSold;
            eft.NewLogMessage += Eft_NewLogMessage;
            //TarkovTracker.Initialized += TarkovTracker_Initialized;
            TarkovTracker.Init();
            quests = new List<TarkovDevApi.Quest>();
            maps = new List<TarkovDevApi.Map>();
            questsTask = updateQuests();
            mapsTask = updateMaps();
            itemsTask = updateItems();
            chkQueue.Checked = Properties.Settings.Default.submitQueueTime;
            chkRaidStartAlert.Checked = Properties.Settings.Default.raidStartAlert;
            if (Properties.Settings.Default.tarkovTrackerToken.Length > 0)
            {
                txtToken.Text = Properties.Settings.Default.tarkovTrackerToken;
                TarkovTracker.SetToken(Properties.Settings.Default.tarkovTrackerToken);
            }
            if (txtToken.Text.Length == 0) tabsMain.SelectedIndex = 1;
            //test();
        }

        private void Eft_GroupInviteAccepted(object? sender, GameWatcher.GroupInviteAcceptedEventArgs e)
        {
            comboGroupMembers.Invoke((MethodInvoker)delegate {
                if (staleGroupList)
                {
                    comboGroupMembers.Items.Clear();
                    staleGroupList = false;
                }
                var added = false;
                foreach (GroupMatchInviteAccept loadout in comboGroupMembers.Items)
                {
                    if (loadout.Info.Nickname == e.PlayerLoadout.Info.Nickname)
                    {
                        var index = comboGroupMembers.Items.IndexOf(loadout);
                        comboGroupMembers.Items.Insert(index, e.PlayerLoadout);
                        added = true;
                    }
                }
                if (!added)
                {
                    comboGroupMembers.Items.Add(e.PlayerLoadout);
                    if (comboGroupMembers.SelectedIndex == -1)
                    {
                        comboGroupMembers.SelectedIndex = 0;
                    }
                }
            });
            logMessage($"{e.PlayerLoadout.Info.Nickname} ({e.PlayerLoadout.Info.Side.ToUpper()} {e.PlayerLoadout.Info.Level}) accepted group invite.");
        }

        private async Task test()
        {
            //Task.WaitAll(questsTask, mapsTask, itemsTask);
            await itemsTask;
            var testDataPath = Path.Join(Directory.GetCurrentDirectory(), "..", "..", "..", "test data", "GroupMatchInviteAccept.log");
            var testData = eft.getJsonStrings(File.ReadAllText(testDataPath));
            foreach (var item in testData)
            {
                var message = JsonSerializer.Deserialize<GroupMatchInviteAccept>(item);
                Debug.WriteLine(message.Info.Nickname);
                Eft_GroupInviteAccepted(eft, new GameWatcher.GroupInviteAcceptedEventArgs { PlayerLoadout = message });
            }
        }

        private void Eft_NewLogMessage(object? sender, LogMonitor.NewLogEventArgs e)
        {
            txtLogs.Invoke((MethodInvoker)delegate {
                txtLogs.AppendText("\n" + e.Type.ToString() + "\n" + e.NewMessage);
                txtLogs.SelectionStart = txtLogs.TextLength;
                txtLogs.ScrollToCaret();
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

        private async void Eft_RaidLoaded(object? sender, GameWatcher.RaidLoadedEventArgs e)
        {
            if (Properties.Settings.Default.raidStartAlert) PlaySoundFromResource(Properties.Resources.raid_starting);
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
            staleGroupList = true;
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
            //logMessage($"{e.Status} quest");
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
            }
            catch (Exception ex)
            {
                logMessage(ex.Message);
            }
        }

        private void panelSettings_SaveClick(object sender, EventArgs e)
        {
            TarkovTracker.SetToken(txtToken.Text);
            Properties.Settings.Default.tarkovTrackerToken = txtToken.Text;
            Properties.Settings.Default.submitQueueTime = chkQueue.Checked;
            Properties.Settings.Default.raidStartAlert = chkRaidStartAlert.Checked;
            Properties.Settings.Default.Save();
        }

        private void panelSettings_CancelClick(object sender, EventArgs e)
        {
            txtToken.Text = Properties.Settings.Default.tarkovTrackerToken;
            chkQueue.Checked = Properties.Settings.Default.submitQueueTime;
            chkRaidStartAlert.Checked = Properties.Settings.Default.raidStartAlert;
        }

        private void btnTarkovTrackerLink_Click(object sender, EventArgs e)
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

        private void PlaySoundFromResource(byte[] resource)
        {
            Stream stream = new MemoryStream(resource);
            var reader = new NAudio.Wave.Mp3FileReader(stream);
            var waveOut = new WaveOut();
            waveOut.Init(reader);
            waveOut.Play();
        }

        private void btnPlayRaidSound_Click(object sender, EventArgs e)
        {
            PlaySoundFromResource(Properties.Resources.raid_starting);
        }

        private TarkovDevApi.Item GetItemData(string id)
        {
            foreach (TarkovDevApi.Item item in items)
            {
                if (item.id == id) return item;
            }
            throw new Exception($"Item with id {id} not found");
        }

        private void comboGroupMembers_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboGroupMembers.SelectedIndex == -1) return;
            var loadout = comboGroupMembers.SelectedItem as GroupMatchInviteAccept;
            listBoxLoadout.Items.Clear();
            //var inventoryTpl = "62ddc8e72f8bb3af180e59c9";
            var pocketsTpl = "627a4e6b255f7527fb05a0f6";
            var inventoryId = loadout.PlayerVisualRepresentation.Equipment.Id;
            var pocketsId = "";
            foreach (LoadoutItem item in loadout.PlayerVisualRepresentation.Equipment.Items)
            {
                if (item._id == inventoryId)
                {
                    item.name = "Inventory";
                    continue;
                }
                if (item._tpl == pocketsTpl)
                {
                    pocketsId = item._id;
                    item.name = "Pockets";
                    continue;
                }
                try
                {
                    var itemData = GetItemData(item._tpl);
                    item.name = itemData.name;
                    //var displayName = itemData.name;
                    //if (item.upd?.StackObjectsCount > 1) displayName += $" x{item.upd.StackObjectsCount}";
                    var listBoxItem = new MaterialSkin.MaterialListBoxItem(item.ToString());
                    listBoxItem.SecondaryText = "secondary text";
                    listBoxItem.Tag = "tag";
                    var card = new MaterialCard();
                    card.Show();
                    listBoxLoadout.Items.Add(listBoxItem);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
            }
        }
    }
}