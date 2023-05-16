using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TarkovMonitor
{
    internal class GameWatcher
    {
        private Process? process;
        private readonly System.Timers.Timer processTimer;
        private readonly FileSystemWatcher watcher;
        //private event EventHandler<NewLogEventArgs> NewLog;
        private readonly Dictionary<LogType, LogMonitor> monitors;
        private string lastLoadedMap = "";
        private bool lastLoadedOnline = false;
        private float lastQueueTime = 0;
        public event EventHandler<LogMonitor.NewLogDataEventArgs> NewLogData;
        public event EventHandler<ExceptionEventArgs> ExceptionThrown;
        public event EventHandler<DebugEventArgs> DebugMessage;
        public event EventHandler GameStarted;
        public event EventHandler<GroupInviteEventArgs> GroupInvite;
        public event EventHandler MatchingStarted;
        public event EventHandler<MatchFoundEventArgs> MatchFound;
        public event EventHandler MatchingAborted;
        public event EventHandler<RaidLoadedEventArgs> RaidLoaded;
        public event EventHandler<RaidExitedEventArgs> RaidExited;
        public event EventHandler<TaskModifiedEventArgs> TaskModified;
        public event EventHandler<TaskEventArgs> TaskStarted;
        public event EventHandler<TaskEventArgs> TaskFailed;
        public event EventHandler<TaskEventArgs> TaskFinished;
        public event EventHandler<FleaSoldEventArgs> FleaSold;
        public event EventHandler<FleaOfferExpiredEventArgs> FleaOfferExpired;
        public GameWatcher()
        {
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
            UpdateProcess();
        }

        public void Start()
        {
            UpdateProcess();
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

        private void GameWatcher_NewLogData(object? sender, LogMonitor.NewLogDataEventArgs e)
        {
            try
            {
                //DebugMessage?.Invoke(this, new DebugEventArgs(e.NewMessage));
                NewLogData?.Invoke(this, e);
                var logPattern = @"(?<message>^\d{4}-\d{2}-\d{2}.+$)\s*(?<json>^{[\s\S]+?^})*";
                var logMessages = Regex.Matches(e.Data, logPattern, RegexOptions.Multiline);
                /*Debug.WriteLine("===log chunk start===");
                Debug.WriteLine(e.NewMessage);
                Debug.WriteLine("===log chunk end===");*/
                foreach (Match logMessage in logMessages)
                {
                    var eventLine = logMessage.Groups["message"].Value;
                    var jsonString = "{}";
                    if (logMessage.Groups["json"].Success)
                    {
                        jsonString = logMessage.Groups["json"].Value;
                    }
                    /*Debug.WriteLine("logged message");
                    Debug.WriteLine(eventLine);
                    Debug.WriteLine("logged json");
                    Debug.WriteLine(jsonString);*/
                    var jsonNode = JsonNode.Parse(jsonString);
                    if (eventLine.Contains("Got notification | UserMatchOver"))
                    {
                        RaidExited?.Invoke(this, new RaidExitedEventArgs { Map = jsonNode["location"].ToString(), RaidId = jsonNode["shortId"]?.ToString() });
                    }
                    if (eventLine.Contains("Got notification | GroupMatchInviteAccept"))
                    {
                        GroupInvite?.Invoke(this, new GroupInviteEventArgs(jsonNode));
                    }
                    if (eventLine.Contains("Got notification | GroupMatchInviteSend"))
                    {
                        GroupInvite?.Invoke(this, new GroupInviteEventArgs(jsonNode));
                    }
                    if (eventLine.Contains("Got notification | GroupMatchRaidReady"))
                    {
                        GroupInvite?.Invoke(this, new GroupInviteEventArgs(jsonNode));
                    }
                    if (eventLine.Contains("GamePrepared") && e.Type == LogType.Application)
                    {
                        var rx = new Regex("GamePrepared:[0-9.]+ real:(?<queueTime>[0-9.]+)");
                        var match = rx.Match(eventLine);
                        lastQueueTime = float.Parse(match.Groups["queueTime"].Value);
                    }
                    if (eventLine.Contains("NetworkGameCreate profileStatus") && e.Type == LogType.Application)
                    {
                        // Confirm we are starting to queue for an online raid and get the map
                        lastLoadedOnline = false;
                        lastLoadedMap = new Regex("Location: (?<map>[^,]+)").Match(eventLine).Groups["map"].Value;
                        if (eventLine.Contains("RaidMode: Online"))
                        {
                            lastLoadedOnline = true;
                            MatchingStarted?.Invoke(this, new EventArgs());
                        }
                    }
                    if (eventLine.Contains("application|MatchingCompleted"))
                    {
                        // When matching is complete, you have been locked to a server with other players
                        MatchFound?.Invoke(this, new MatchFoundEventArgs { });
                    }
                    if (eventLine.Contains("application|GameStarting"))
                    {
                        // When the raid start countdown begins. Only happens for PMCs.
                        if (lastLoadedOnline)
                        {
                            RaidLoaded?.Invoke(this, new RaidLoadedEventArgs { Map = lastLoadedMap, QueueTime = lastQueueTime, RaidType = "pmc" });
                        }
                        lastLoadedMap = "";
                        lastQueueTime = 0;
                    }
                    else if (eventLine.Contains("application|GameStarted") && e.Type == LogType.Application)
                    {
                        // Raid begins, either at the end of the countdown for PMC (no event raised), or immediately as a scav
                        if (lastLoadedOnline && lastQueueTime > 0)
                        {
                            RaidLoaded?.Invoke(this, new RaidLoadedEventArgs { Map = lastLoadedMap, QueueTime = lastQueueTime, RaidType = "scav" });
                        }
                        lastLoadedMap = "";
                        lastQueueTime = 0;
                    }
                    if (eventLine.Contains("Network game matching aborted"))
                    {
                        MatchingAborted?.Invoke(this, new EventArgs());
                        lastLoadedMap = "";
                        lastQueueTime = 0;
                    }
                    if (eventLine.Contains("Got notification | ChatMessageReceived"))
                    {
                        var templateId = jsonNode["message"]["templateId"].ToString();
                        var messageText = jsonNode["message"]["text"].ToString();
                        if (templateId == "5bdabfb886f7743e152e867e 0")
                        {
                            FleaSold?.Invoke(this, new FleaSoldEventArgs(jsonNode));
                            continue;
                        }
                        if (templateId == "5bdabfe486f7743e1665df6e 0")
                        {
                            FleaOfferExpired?.Invoke(this, new FleaOfferExpiredEventArgs(jsonNode));
                            continue;
                        }
                        if (messageText == "quest started" || messageText == "quest finished" || messageText == "quest failed")
                        {
                            var args = new TaskModifiedEventArgs(jsonNode);

                            TaskModified?.Invoke(this, args);
                            if (args.Status == TaskStatus.Started)
                            {
                                TaskStarted?.Invoke(this, new TaskEventArgs { TaskId = args.TaskId });
                            }
                            if (args.Status == TaskStatus.Failed)
                            {
                                TaskFailed?.Invoke(this, new TaskEventArgs { TaskId = args.TaskId });
                            }
                            if (args.Status == TaskStatus.Finished)
                            {
                                TaskFinished?.Invoke(this, new TaskEventArgs { TaskId = args.TaskId });
                            }
                        }
                    }
                    
                }
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex));
            }
        }

        private void ProcessTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            UpdateProcess();
        }

        private void UpdateProcess()
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
                //DebugMessage?.Invoke(this, new DebugEventArgs("EFT not running."));
                process = null;
                return;
            }
            GameStarted?.Invoke(this, new EventArgs());
            process = processes.First();
            var exePath = GetProcessFilename.GetFilename(process);
            var path = exePath[..exePath.LastIndexOf(Path.DirectorySeparatorChar)];
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
                newMon.NewLogData += GameWatcher_NewLogData;
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
        public enum TaskStatus
        {
            Started = 10,
            Failed = 11,
            Finished = 12
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
        public class TaskModifiedEventArgs : EventArgs
        {
            public string TaskId { get; set; }
            public TaskStatus Status { get; set; }
            public TaskModifiedEventArgs(JsonNode node)
            {
                TaskId = node["message"]["templateId"].ToString();
                Status = (TaskStatus)node["message"]["type"].GetValue<int>();
            }
        }
        public class TaskEventArgs : EventArgs
        {
            public string TaskId { get; set; }
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

        public class MatchFoundEventArgs : EventArgs { }
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
            public int SoldItemCount { get; set; }
            public Dictionary<string, int> ReceivedItems { get; set; }
            public FleaSoldEventArgs(JsonNode node)
            {
                Buyer = node["message"]["systemData"]["buyerNickname"].ToString();
                SoldItemId = node["message"]["systemData"]["soldItem"].ToString();
                SoldItemCount = node["message"]["systemData"]["itemCount"].GetValue<int>();
                ReceivedItems = new Dictionary<string, int>();
                if (node["message"]["hasRewards"] != null && node["message"]["hasRewards"].GetValue<bool>())
                {
                    foreach (var item in node["message"]["items"]["data"].AsArray())
                    {
                        ReceivedItems.Add(item["_tpl"].ToString(), item["upd"]["StackObjectsCount"].GetValue<int>());
                    }
                }
            }
        }
        public class FleaOfferExpiredEventArgs : EventArgs
        {
            public string ItemId { get; set; }
            public int ItemCount { get; set; }
            public FleaOfferExpiredEventArgs(JsonNode node)
            {
                var item = node["message"]["items"]["data"].AsArray()[0];
                ItemId = item["_tpl"].ToString();
                ItemCount = item["upd"]["StackObjectsCount"].GetValue<int>();
            }
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
