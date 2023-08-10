using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text;

namespace TarkovMonitor
{
    internal class GameWatcher
    {
        private Process? process;
        private readonly System.Timers.Timer processTimer;
        private readonly FileSystemWatcher watcher;
        private readonly FileSystemWatcher screenshotWatcher;
        private string screenshotPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + Path.DirectorySeparatorChar + "Escape From Tarkov" + Path.DirectorySeparatorChar + "Screenshots";
        //private event EventHandler<NewLogEventArgs> NewLog;
        internal readonly Dictionary<GameLogType, LogMonitor> Monitors;
        private RaidInfo raidInfo;
        private string lastKnownMap;
        public event EventHandler<NewLogDataEventArgs> NewLogData;
        public event EventHandler<ExceptionEventArgs> ExceptionThrown;
        public event EventHandler<DebugEventArgs> DebugMessage;
        public event EventHandler GameStarted;
        public event EventHandler<GroupInviteSendEventArgs> GroupInviteSend;
        public event EventHandler<GroupInviteAcceptEventArgs> GroupInviteAccept;
        public event EventHandler<GroupReadyEventArgs> GroupReady;
        public event EventHandler GroupDisbanded;
        public event EventHandler<GroupUserLeaveEventArgs> GroupUserLeave;
        public event EventHandler MapLoading;
        public event EventHandler<MatchingStartedEventArgs> MatchingStarted;
        public event EventHandler<MatchFoundEventArgs> MatchFound; // only fires on initial load into a raid
        public event EventHandler<MatchFoundEventArgs> MapLoaded; // fires on initial and subsequent loads into a raid
        public event EventHandler<MatchingCancelledEventArgs> MatchingAborted;
        public event EventHandler<RaidLoadedEventArgs> RaidLoaded;
        public event EventHandler<RaidExitedEventArgs> RaidExited;
        public event EventHandler<TaskModifiedEventArgs> TaskModified;
        public event EventHandler<TaskEventArgs> TaskStarted;
        public event EventHandler<TaskEventArgs> TaskFailed;
        public event EventHandler<TaskEventArgs> TaskFinished;
        public event EventHandler<FleaSoldEventArgs> FleaSold;
        public event EventHandler<FleaOfferExpiredEventArgs> FleaOfferExpired;
        public event EventHandler<PlayerPositionEventArgs> PlayerPosition;
        public string LogsPath { get; set; } = "";

        public GameWatcher()
        {
            Monitors = new();
            raidInfo = new RaidInfo();
            processTimer = new System.Timers.Timer(TimeSpan.FromSeconds(30).TotalMilliseconds)
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
            screenshotWatcher = new FileSystemWatcher();
            SetupScreenshotWatcher();
        }

        public void SetupScreenshotWatcher()
        {
            bool screensPathExists = Directory.Exists(screenshotPath);
            string watchPath = screensPathExists ? screenshotPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            screenshotWatcher.Path = watchPath;
            screenshotWatcher.IncludeSubdirectories = !screensPathExists;
            screenshotWatcher.Created -= ScreenshotWatcher_Created;
            screenshotWatcher.Created -= ScreenshotWatcher_FolderCreated;
            if (screensPathExists)
            {
                screenshotWatcher.Filter = "*.png";
                screenshotWatcher.Created += ScreenshotWatcher_Created;
            }
            else
            {
                screenshotWatcher.Created += ScreenshotWatcher_FolderCreated;
            }
            screenshotWatcher.EnableRaisingEvents = true;
        }

        private void ScreenshotWatcher_FolderCreated(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath == screenshotPath)
            {
                SetupScreenshotWatcher();
            }
        }
        private void ScreenshotWatcher_Created(object sender, FileSystemEventArgs e)
        {
            var match = Regex.Match(e.Name, @"\d{4}-\d{2}-\d{2}\[\d{2}-\d{2}\]_(?<position>.+) \(\d\)\.png");
            if (!match.Success)
            {
                return;
            }
            var position = Regex.Match(match.Groups["position"].Value, @"(?<x>-?[\d.]+), (?<y>-?[\d.]+), (?<z>-?[\d.]+)_.*");
            if (!position.Success)
            {
                return;
            }
            if (lastKnownMap == null)
            {
                //return;
            }
            PlayerPosition?.Invoke(this, new(lastKnownMap, new Position(position.Groups["x"].Value, position.Groups["y"].Value, position.Groups["z"].Value)));
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

        internal void GameWatcher_NewLogData(object? sender, NewLogDataEventArgs e)
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
                    if (eventLine.Contains("Got notification | GroupMatchInviteSend"))
                    {
                        GroupInviteSend?.Invoke(this, new(jsonNode));
                    }
                    if (eventLine.Contains("Got notification | GroupMatchInviteAccept"))
                    {
                        // GroupMatchInviteAccept occurs when someone you send an invite accepts
                        // GroupMatchInviteSend occurs when you receive an invite and either accept or decline
                        GroupInviteAccept?.Invoke(this, new(jsonNode));
                    }
                    if (eventLine.Contains("Got notification | GroupMatchUserLeave"))
                    {
                        // User left the group
                        GroupUserLeave?.Invoke(this, new(jsonNode));
                    }
					if (eventLine.Contains("Got notification | GroupMatchWasRemoved"))
                    {
                        // When the group is disbanded
                        GroupDisbanded?.Invoke(this, new());
                    }
					if (eventLine.Contains("Got notification | GroupMatchRaidReady"))
                    {
                        // Occurs for each other member of the group when ready
                        GroupReady?.Invoke(this, new GroupReadyEventArgs(jsonNode));
                    }
                    if (eventLine.Contains("application|Matching with group id"))
                    {
                        MapLoading?.Invoke(this, new());
                    }
                    if (eventLine.Contains("application|LocationLoaded"))
                    {
						// The map has been loaded and the game is searching for a match
						raidInfo = new()
						{
							MapLoadTime = float.Parse(Regex.Match(eventLine, @"LocationLoaded:[0-9.]+ real:(?<loadTime>[0-9.]+)").Groups["loadTime"].Value)
						};
						MatchingStarted?.Invoke(this, new(raidInfo));
					}
					if (eventLine.Contains("application|MatchingCompleted"))
					{
						// Matching is complete and we are locked to a server with other players
						// Just the queue time is available so far
						// Occurs on initial raid load and when the user cancels matching
                        // Does not occur when the user re-connects to a raid in progress
						var queueTimeMatch = Regex.Match(eventLine, @"MatchingCompleted:[0-9.]+ real:(?<queueTime>[0-9.]+)");
						raidInfo.QueueTime = float.Parse(queueTimeMatch.Groups["queueTime"].Value);
					}
                    if (eventLine.Contains("application|TRACE-NetworkGameCreate profileStatus"))
                    {
                        // Immediately after matching is complete
                        // Sufficient information is available to raise the MatchFound event
                        raidInfo.Map = Regex.Match(eventLine, "Location: (?<map>[^,]+)").Groups["map"].Value;
                        lastKnownMap = raidInfo.Map;
                        raidInfo.Online = eventLine.Contains("RaidMode: Online");
                        raidInfo.RaidId = Regex.Match(eventLine, @"shortId: (?<raidId>[A-Z0-9]{6})").Groups["raidId"].Value;
                        if (raidInfo.Online && raidInfo.QueueTime > 0)
                        {
                            // Raise the MatchFound event only if we queued; not if we are re-loading back into a raid
                            MatchFound?.Invoke(this, new(raidInfo));
                        }
                        MapLoaded?.Invoke(this, new(raidInfo));
                    }
                    if (eventLine.Contains("application|GameStarting"))
                    {
                        // The raid start countdown begins. Only happens for PMCs.
                        raidInfo.RaidType = RaidType.PMC;
                        if (raidInfo.Online)
                        {
                            RaidLoaded?.Invoke(this, new(raidInfo));
                        }
                    }
                    if (eventLine.Contains("application|GameStarted"))
                    {
                        // Raid begins, either at the end of the countdown for PMC, or immediately as a scav
                        if (raidInfo.RaidType == RaidType.Unknown && raidInfo.QueueTime > 0)
                        {
                            // RaidType was not set previously for PMC, and we spent time matching, so we must be a scav
                            raidInfo.RaidType = RaidType.Scav;
                        }
                        if (raidInfo.Online && raidInfo.RaidType != RaidType.PMC)
                        {
                            // We already raised the RaidLoaded event for PMC, so only raise here if not PMC
                            RaidLoaded?.Invoke(this, new(raidInfo));
                        }
                        raidInfo = new();
                    }
                    if (eventLine.Contains("application|Network game matching aborted") || eventLine.Contains("application|Network game matching cancelled"))
                    {
                        // User cancelled matching
                        MatchingAborted?.Invoke(this, new(raidInfo));
                        raidInfo = new();
                    }
                    if (eventLine.Contains("Got notification | ChatMessageReceived"))
                    {
                        var messageText = jsonNode["message"]["text"].ToString();
                        var messageType = jsonNode["message"]["type"].GetValue<int>();

                        if (messageType == 4)
						{
							var templateId = jsonNode["message"]["templateId"].ToString();
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
						}
                        if (Enum.IsDefined(typeof(TaskStatus), messageType))
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
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "parsing log data"));
            }
        }

        private void ProcessTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            UpdateProcess();
        }

        public Dictionary<DateTime, string> GetLogFolders()
        {
            // Find all of the log folders in the Logs directory
            if (LogsPath != "")
            {
                Dictionary<DateTime, string> folderDictionary = new();
                var logFolders = Directory.GetDirectories(LogsPath);
                // For each log folder, get the timestamp from the folder name
                foreach (string folderName in logFolders)
                {
                    var dateTimeString = new Regex(@"log_(?<timestamp>\d+\.\d+\.\d+_\d+-\d+-\d+)").Match(folderName).Groups["timestamp"].Value;
                    DateTime folderDate = DateTime.ParseExact(dateTimeString, "yyyy.MM.dd_H-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
                    folderDictionary.Add(folderDate, folderName);
                }
                // Return the dictionary sorted by the timestamp
                return folderDictionary.OrderByDescending(key => key.Key).ToDictionary(x => x.Key, x => x.Value);
            } else
            {
                return new Dictionary<DateTime, string>();
            }
            
        }

        // Process the log files in the specified folder
        public void ProcessLogs(string folderPath)
        {
            var logFiles = Directory.GetFiles(folderPath);
            // TODO: This could be improved by processing lines in the order they were created
            // rather than a full file at a time, this could be valuable for future features
            foreach (string logFile in logFiles)
            {
                bool validType = false;
                GameLogType logType = new();
                // Check which type of log file this is by the filename
                if (logFile.Contains("application.log"))
                {
                    logType = GameLogType.Application;
                    validType = true;
                } else if (logFile.Contains("notifications.log"))
                {
                    logType = GameLogType.Notifications;
                    validType = true;
                } else if (logFile.Contains("traces.log"))
                {
                    logType = GameLogType.Traces;
                    validType = false;
                    // Traces are not currently used, so skip them
                    continue;
                } else
                {
                    // We're not a known log type, so skip this file
                    continue;
                }

                // Read the file into memory using UTF-8 encoding
                var fileContents = File.ReadAllText(logFile, Encoding.UTF8);

                GameWatcher_NewLogData(this, new NewLogDataEventArgs { Type = logType, Data = fileContents });
            }
        }

        private void UpdateProcess()
        {
            try
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
                raidInfo = new();
                var processes = Process.GetProcessesByName("EscapeFromTarkov");
                if (processes.Length == 0)
                {
                    //DebugMessage?.Invoke(this, new DebugEventArgs("EFT not running."));
                    process = null;
                    return;
                }
                GameStarted?.Invoke(this, new EventArgs());
                process = processes.First();
                var exePath = GetProcessFilename.GetFilename(process);
                var path = exePath[..exePath.LastIndexOf(Path.DirectorySeparatorChar)];
                LogsPath = System.IO.Path.Combine(path, "Logs");
                watcher.Path = LogsPath;
                watcher.EnableRaisingEvents = true;
                var logFolders = System.IO.Directory.GetDirectories(LogsPath);
                var latestDate = new DateTime(0);
                var latestLogFolder = logFolders.Last();
                foreach (var logFolder in logFolders)
                {
                    var dateTimeString = Regex.Match(logFolder, @"log_(?<timestamp>\d+\.\d+\.\d+_\d+-\d+-\d+)").Groups["timestamp"].Value;
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

            } catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new(ex, "watching for EFT process"));
            }
        }

        private void StartNewMonitor(string path)
        {
            GameLogType? newType = null;
            if (path.Contains("application.log"))
            {
                newType = GameLogType.Application;
            }
            if (path.Contains("notifications.log"))
            {
                newType = GameLogType.Notifications;
            }
            if (path.Contains("traces.log"))
            {
                newType = GameLogType.Traces;
            }
            if (newType != null)
            {
                //Debug.WriteLine($"Starting new {newType} monitor at {path}");
                if (Monitors.ContainsKey((GameLogType)newType))
                {
                    Monitors[(GameLogType)newType].Stop();
                }
                var newMon = new LogMonitor(path, (GameLogType)newType);
                newMon.NewLogData += GameWatcher_NewLogData;
                newMon.Start();
                Monitors[(GameLogType)newType] = newMon;
            }
        }
	}
	public enum GameLogType
	{
		Application,
		Notifications,
		Traces
	}
    public enum MessageType
	{
		PlayerMessage = 1,
		Started = 10,
		Failed = 11,
		Finished = 12
	}
	public enum TaskStatus
	{
		Started = 10,
		Failed = 11,
		Finished = 12
	}
	public enum RaidType
	{
		Unknown,
		PMC,
		Scav
	}
	public enum GroupInviteType
	{
		Accepted,
		Sent
    }
    public class RaidInfo
    {
        public string Map { get; set; }
        public string RaidId { get; set; }
        public bool Online { get; set; }
        public float MapLoadTime { get; set; }
        public float QueueTime { get; set; }
        public RaidType RaidType { get; set; }
        public RaidInfo()
        {
            Map = "";
            Online = false;
            RaidId = "";
            MapLoadTime = 0;
            QueueTime = 0;
            RaidType = RaidType.Unknown;
        }
    }
    public class Position
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public Position(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
        public Position(string x, string y, string z)
        {
            X = float.Parse(x);
            Y = float.Parse(y);
            Z = float.Parse(z);
        }
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
			TaskId = node["message"]["templateId"].ToString().Split(' ')[0];
			Status = (TaskStatus)node["message"]["type"].GetValue<int>();
		}
	}
	public class TaskEventArgs : EventArgs
	{
		public string TaskId { get; set; }
	}
    public class GroupInviteSendEventArgs : EventArgs
    {
        public List<GroupMemberInfo> Members { get; set; }
        public GroupInviteSendEventArgs(JsonNode node)
        {
            Members = new();
            foreach (JsonNode member in node["members"].AsArray())
            {
                Members.Add(new GroupMemberInfo(member));
            }
        }
    }
    public class GroupInviteAcceptEventArgs : EventArgs
    {
        public PlayerInfo PlayerInfo { get; set; }
        public GroupInviteType InviteType { get; set; }
        public GroupInviteAcceptEventArgs(JsonNode node)
        {
            if (node["type"].ToString() == "groupMatchInviteAccept")
            {
                InviteType = GroupInviteType.Accepted;
            } else
            {
                InviteType = GroupInviteType.Sent;
            }
            PlayerInfo = new PlayerInfo(node["Info"]);
        }
    }
    public class GroupUserLeaveEventArgs : EventArgs
    {
        public string Nickname { get; set; }
        public GroupUserLeaveEventArgs(JsonNode node)
        {
            Nickname = node["Nickname"].ToString();
        }
    }
	public class GroupReadyEventArgs : EventArgs
	{
		public PlayerInfo PlayerInfo { get; set; }
		public PlayerLoadout PlayerLoadout { get; set; }
		public GroupReadyEventArgs(JsonNode node)
		{
			this.PlayerInfo = new PlayerInfo(node["extendedProfile"]["Info"]);
			this.PlayerLoadout = new PlayerLoadout(node["extendedProfile"]["PlayerVisualRepresentation"]);
		}
		public override string ToString()
		{
			return $"{this.PlayerInfo.Nickname} ({this.PlayerLoadout.Info.Side}, {this.PlayerLoadout.Info.Level})";
		}
	}
    public class MatchingStartedEventArgs : EventArgs
    {
        public float MapLoadTime { get; set; }
        public MatchingStartedEventArgs(RaidInfo raidInfo)
        {
            MapLoadTime = raidInfo.MapLoadTime;
        }
    }
    public class MatchingCancelledEventArgs : MatchingStartedEventArgs
    {
        public float QueueTime { get; set; }
        public MatchingCancelledEventArgs(RaidInfo raidInfo) : base(raidInfo)
        {
            QueueTime = raidInfo.QueueTime;
        }
    }
	public class MatchFoundEventArgs : MatchingStartedEventArgs
    {
		public string Map { get; set; }
		public string RaidId { get; set; }
		public float QueueTime { get; set; }
        public MatchFoundEventArgs(RaidInfo raidInfo) : base(raidInfo)
        {
            Map = raidInfo.Map;
            RaidId = raidInfo.RaidId;
            QueueTime = raidInfo.QueueTime;
        }
    }
	public class RaidLoadedEventArgs : MatchFoundEventArgs
    {
		public RaidType RaidType { get; set; }
        public RaidLoadedEventArgs(RaidInfo raidInfo) : base(raidInfo)
        {
            RaidType = raidInfo.RaidType;
        }
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
        public string Context { get; set; }
		public ExceptionEventArgs(Exception ex, string context)
		{
			this.Exception = ex;
            Context = context;
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
    public class PlayerPositionEventArgs : EventArgs
    {
        public Position Position { get; set; }
        public string? Map { get; set; }
        public PlayerPositionEventArgs(string map, Position position)
        {
            this.Map = map;
            this.Position = position;
        }
    }
}
