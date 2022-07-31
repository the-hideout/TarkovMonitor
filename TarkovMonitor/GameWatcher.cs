using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMonitor
{
    internal class GameWatcher
    {
        private Process? process;
        private System.Timers.Timer processTimer;
        private FileSystemWatcher watcher;
        private Dictionary<LogType, bool> initialRead;
        //private event EventHandler<NewLogEventArgs> NewLog;
        private Dictionary<LogType, LogMonitor> monitors;
        private string lastLoadedMap = "";
        private string lastQueueType = "scav";
        private bool lastLoadedOnline = false;
        private float lastQueueTime = 0;
        public event EventHandler<LogMonitor.NewLogEventArgs> NewLogMessage;
        public event EventHandler<RaidExitedEventArgs> RaidExited;
        public event EventHandler<QuestEventArgs> QuestModified;
        public event EventHandler<GroupInviteEventArgs> GroupInvite;
        public event EventHandler<RaidLoadedEventArgs> RaidLoaded;
        public event EventHandler MatchingAborted;
        public event EventHandler<FleaSoldEventArgs> FleaSold;
        public event EventHandler<ExceptionEventArgs> ExceptionThrown;
        public event EventHandler<DebugEventArgs> DebugMessage;
        public GameWatcher()
        {
            initialRead = new();
            initialRead.Add(LogType.Application, false);
            initialRead.Add(LogType.Notifications, false);
            monitors = new();
            processTimer = new System.Timers.Timer(30000)
            {
                AutoReset = true,
                Enabled = false
            };
            processTimer.Elapsed += ProcessTimer_Elapsed;
            watcher = new FileSystemWatcher { 
                Filter = "*.log",
                IncludeSubdirectories = true,
                EnableRaisingEvents = false,
            };
            watcher.Created += Watcher_Created;
            updateProcess();
        }

        public void Start()
        {
            updateProcess();
            processTimer.Enabled = true;
        }

        private void Watcher_Created(object sender, FileSystemEventArgs e)
        {
            if (e.Name.Contains("application.log"))
            {
                StartNewMonitor(e.FullPath);
            }
            if (e.Name.Contains("notifications.log"))
            {
                StartNewMonitor(e.FullPath);
            }
        }

        private void GameWatcher_NewLog(object? sender, LogMonitor.NewLogEventArgs e)
        {
            try
            {
                NewLogMessage?.Invoke(this, e);
                if (e.NewMessage.Contains("Got notification | UserMatchOver"))
                {
                    var rx = new Regex("\"location\": \"(?<map>[^\"]+)\"");
                    var match = rx.Match(e.NewMessage);
                    var map = match.Groups["map"].Value;
                    rx = new Regex("\"shortId\": \"(?<raidId>[^\"]+)\"");
                    match = rx.Match(e.NewMessage);
                    var raidId = match.Groups["raidId"].Value;
                    RaidExited?.Invoke(this, new RaidExitedEventArgs { Map = map, RaidId = raidId });
                }
                if (e.NewMessage.Contains("quest finished"))
                {
                    var rx = new Regex("\"templateId\": \"(?<messageId>[^\"]+)\"");
                    var match = rx.Match(e.NewMessage);
                    var id = match.Groups["messageId"].Value;
                    QuestModified?.Invoke(this, new QuestEventArgs { MessageId = id, Status = QuestStatus.Finished });
                }
                if (e.NewMessage.Contains("quest failed"))
                {
                    var rx = new Regex("\"templateId\": \"(?<messageId>[^\"]+)\"");
                    var match = rx.Match(e.NewMessage);
                    var id = match.Groups["messageId"].Value;
                    QuestModified?.Invoke(this, new QuestEventArgs { MessageId = id, Status = QuestStatus.Failed });
                }
                if (e.NewMessage.Contains("quest started"))
                {
                    var rx = new Regex("\"templateId\": \"(?<messageId>[^\"]+)\"");
                    var match = rx.Match(e.NewMessage);
                    var id = match.Groups["messageId"].Value;
                    QuestModified?.Invoke(this, new QuestEventArgs { MessageId = id, Status = QuestStatus.Started });
                }
                if (e.NewMessage.Contains("GroupMatchInviteAccept"))
                {
                    var jsonStrings = getJsonStrings(e.NewMessage);
                    foreach (var jsonString in jsonStrings)
                    {
                        //var loadout = JsonSerializer.Deserialize<GroupMatchInviteAccept>(jsonString);
                        var loadout = JsonNode.Parse(jsonString);
                        GroupInvite?.Invoke(this, new GroupInviteEventArgs(loadout));
                    }
                }
                if (e.NewMessage.Contains("GroupMatchInviteSend"))
                {
                    var jsonStrings = getJsonStrings(e.NewMessage);
                    foreach (var jsonString in jsonStrings)
                    {
                        //var loadout = JsonSerializer.Deserialize<GroupMatchInviteSend>(jsonString);
                        var loadout = JsonNode.Parse(jsonString);
                        GroupInvite?.Invoke(this, new GroupInviteEventArgs(loadout));
                    }
                }
                if (e.NewMessage.Contains("GamePrepared") && e.Type == LogType.Application)
                {
                    var rx = new Regex("GamePrepared:[0-9.]+ real:(?<queueTime>[0-9.]+)");
                    var match = rx.Match(e.NewMessage);
                    lastQueueTime = float.Parse(match.Groups["queueTime"].Value);
                    lastQueueType = "scav";
                }
                if (e.NewMessage.Contains("NetworkGameCreate profileStatus") && e.Type == LogType.Application)
                {
                    lastLoadedOnline = false;
                    lastLoadedMap = new Regex("Location: (?<map>[^,]+)").Match(e.NewMessage).Groups["map"].Value;
                    if (e.NewMessage.Contains("RaidMode: Online"))
                    {
                        lastLoadedOnline = true;
                    }
                }
                if (e.NewMessage.Contains("application|GameStarting"))
                {
                    lastQueueType = "pmc";
                    if (lastLoadedOnline)
                    {
                        RaidLoaded?.Invoke(this, new RaidLoadedEventArgs { Map = lastLoadedMap, QueueTime = lastQueueTime, RaidType = lastQueueType });
                    }
                    lastLoadedMap = "";
                    lastQueueType = "scav";
                    lastQueueTime = 0;
                }
                else if (e.NewMessage.Contains("application|GameStarted") && e.Type == LogType.Application)
                {
                    if (lastLoadedOnline && lastQueueTime > 0)
                    {
                        RaidLoaded?.Invoke(this, new RaidLoadedEventArgs { Map = lastLoadedMap, QueueTime = lastQueueTime, RaidType = lastQueueType });
                    }
                    lastLoadedMap = "";
                    lastQueueType = "scav";
                    lastQueueTime = 0;
                }
                if (e.NewMessage.Contains("Network game matching aborted"))
                {
                    MatchingAborted?.Invoke(this, new EventArgs());
                    lastLoadedMap = "";
                    lastQueueType = "scav";
                    lastQueueTime = 0;
                }
                if (e.NewMessage.Contains("Got notification | ChatMessageReceived") && e.NewMessage.Contains("5bdac0b686f7743e1665e09e")) {
                    var transactions = getJsonStrings(e.NewMessage);
                    foreach (var json in transactions)
                    {
                        if (!json.Contains("buyerNickname")) continue;
                        var message = JsonSerializer.Deserialize<FleaSoldNewMessage>(json);
                        var args = new FleaSoldEventArgs
                        {
                            Buyer = message.message.systemData.buyerNickname,
                            SoldItemId = message.message.systemData.soldItem,
                            soldItemCount = message.message.systemData.itemCount,
                            ReceivedItems = new Dictionary<string, int>()
                        };
                        if (message.message.hasRewards)
                        {
                            foreach (var item in message.message.items.data)
                            {
                                args.ReceivedItems.Add(item._tpl, item.upd.StackObjectsCount);
                            }
                        }
                        FleaSold?.Invoke(this, args);
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex));
            }
        }

        public List<string> getJsonStrings(string log)
        {
            List<string> result = new List<string>();
            var matches = new Regex(@"^{[\s\S]+?^}", RegexOptions.Multiline).Matches(log);
            foreach (Match match in matches)
            {
                result.Add(match.Value);
            }
            return result;
        }

        private void ProcessTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            updateProcess();
        }

        private void updateProcess()
        {
            if (process != null)
            {
                if (!process.HasExited)
                {
                    return;
                }
                //DebugMessage?.Invoke(this, new DebugEventArgs("EFT exited."));
                process = null;
            }
            var processes = Process.GetProcessesByName("EscapeFromTarkov");
            if (processes.Length == 0) {
                DebugMessage?.Invoke(this, new DebugEventArgs("EFT not running."));
                process = null;
                return;
            }
            process = processes.First();
            var exePath = GetProcessFilename.GetFilename(process);
            var path = exePath.Substring(0, exePath.LastIndexOf(Path.DirectorySeparatorChar));
            var logsPath = System.IO.Path.Combine(path, "Logs");
            watcher.Path = logsPath;
            watcher.EnableRaisingEvents = true;
            var logFolders = System.IO.Directory.GetDirectories(logsPath);
            var latestDate = new DateTime(0);
            var latestLogFolder = logFolders.Last();
            foreach (var logFolder in logFolders)
            {
                var dateTimeString = new Regex(@"log_(?<timestamp>\d+\.\d+\.\d+_\d+-\d+-\d+)").Match(logFolder).Groups["timestamp"].Value;
                var logDate = DateTime.ParseExact(dateTimeString, "yyyy.MM.dd_H-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
                if (logDate > latestDate)
                {
                    latestDate = logDate;
                    latestLogFolder = logFolder;
                }
            }
            initialRead = new();
            initialRead.Add(LogType.Application, false);
            initialRead.Add(LogType.Notifications, false);
            var files = System.IO.Directory.GetFiles(latestLogFolder);
            foreach (var file in files)
            {
                if (file.Contains("notifications.log"))
                {
                    StartNewMonitor(file);
                }
                if (file.Contains("application.log"))
                {
                    StartNewMonitor(file);
                }
                /*if (file.Contains("traces.log"))
                {
                    StartNewMonitor(file);
                }*/
            }
        }

        private void StartNewMonitor(string path)
        {
            LogType? newType = null;
            if (path.Contains("application.log"))
            {
                newType = LogType.Application;
            }
            if (path.Contains("notifications.log"))
            {
                newType = LogType.Notifications;
            }
            if (path.Contains("traces.log"))
            {
                newType = LogType.Traces;
            }
            if (newType != null)
            {
                //Debug.WriteLine($"Starting new {newType} monitor at {path}");
                if (monitors.ContainsKey((LogType)newType))
                {
                    monitors[(LogType)newType].Stop();
                }
                var newMon = new LogMonitor(path, (LogType)newType);
                newMon.NewLog += GameWatcher_NewLog;
                newMon.Start();
                monitors[(LogType)newType] = newMon;
            }
        }

        public enum LogType
        {
            Application,
            Notifications,
            Traces
        }
        public enum QuestStatus
        {
            Started,
            Failed,
            Finished
        }
        public enum GroupInviteType
        {
            Accepted,
            Sent
        }
        public class RaidExitedEventArgs : EventArgs
        {
            public string Map { get; set; }   
            public string RaidId { get; set; }
        }
        public class QuestEventArgs : EventArgs
        {
            public string MessageId { get; set; }
            public QuestStatus Status { get; set; }
        }
        public class GroupInviteEventArgs : EventArgs
        {
            public GroupInviteType GroupInviteType { get; set; }
            public PlayerInfo PlayerInfo { get; set; }
            public PlayerLoadout PlayerLoadout { get; set; }
            public GroupInviteEventArgs(GroupMatchInviteAccept inviteAccept)
            {
                this.GroupInviteType = GroupInviteType.Accepted;
                this.PlayerInfo = inviteAccept.Info;
                this.PlayerLoadout = inviteAccept.PlayerVisualRepresentation;
            }
            public GroupInviteEventArgs(GroupMatchInviteSend inviteSend)
            {
                this.GroupInviteType = GroupInviteType.Sent;
                this.PlayerInfo = inviteSend.fromProfile.Info;
                this.PlayerLoadout = inviteSend.fromProfile.PlayerVisualRepresentation;
            }
            public GroupInviteEventArgs(JsonNode node)
            {
                this.GroupInviteType = GroupInviteType.Accepted;
                if (node["fromProfile"] != null)
                {
                    this.GroupInviteType = GroupInviteType.Sent;
                    node = node["fromProfile"];
                }
                this.PlayerInfo = new PlayerInfo(node["Info"]);
                this.PlayerLoadout = new PlayerLoadout(node["PlayerVisualRepresentation"]);
            }
            public override string ToString()
            {
                return $"{this.PlayerInfo.Nickname} ({this.PlayerLoadout.Info.Side}, {this.PlayerLoadout.Info.Level})";
            }
        }
        public class RaidLoadedEventArgs : EventArgs
        {
            public string Map { get; set; }
            public float QueueTime { get; set; }
            public string RaidType { get; set; }
        }
        public class FleaSoldEventArgs : EventArgs
        {
            public string Buyer { get; set; }
            public string SoldItemId { get; set; }
            public int soldItemCount { get; set; }
            public Dictionary<string, int> ReceivedItems { get; set; }
        }
        public class ExceptionEventArgs : EventArgs
        {
            public Exception Exception { get; set; }
            public ExceptionEventArgs(Exception ex)
            {
                this.Exception = ex;
            }
        }
        public class DebugEventArgs : EventArgs
        {
            public string Message { get; set; }
            public DebugEventArgs(string message)
            {
                this.Message = message;
            }
        }
    }
}
