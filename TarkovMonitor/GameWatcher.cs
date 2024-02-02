using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using System.Globalization;
using Microsoft.Win32;

namespace TarkovMonitor
{
    internal class GameWatcher
    {
        private Process? process;
        private readonly System.Timers.Timer processTimer;
        private readonly FileSystemWatcher watcher;
        private readonly FileSystemWatcher screenshotWatcher;
        public string ScreenshotsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Escape From Tarkov", "Screenshots");
            }
        }
        //private event EventHandler<NewLogEventArgs> NewLog;
        internal readonly Dictionary<GameLogType, LogMonitor> Monitors;
        private RaidInfo raidInfo;
        public event EventHandler<NewLogDataEventArgs> NewLogData;
        public event EventHandler<ExceptionEventArgs> ExceptionThrown;
        public event EventHandler<DebugEventArgs> DebugMessage;
        public event EventHandler GameStarted;
        public event EventHandler<GroupEventArgs> GroupInviteAccept;
        public event EventHandler<GroupRaidSettingsEventArgs> GroupRaidSettings;
        public event EventHandler<GroupMatchRaidReadyEventArgs> GroupMemberReady;
        public event EventHandler GroupDisbanded;
        public event EventHandler<GroupMatchUserLeaveEventArgs> GroupUserLeave;
        public event EventHandler MapLoading;
        public event EventHandler<RaidInfoEventArgs> MatchingStarted;
        public event EventHandler<RaidInfoEventArgs> MatchFound; // only fires on initial load into a raid
        public event EventHandler<RaidInfoEventArgs> MapLoaded; // fires on initial and subsequent loads into a raid
        public event EventHandler<RaidInfoEventArgs> MatchingAborted;
        public event EventHandler<RaidInfoEventArgs> RaidCountdown;
        public event EventHandler<RaidInfoEventArgs> RaidStarted;
        public event EventHandler<RaidExitedEventArgs> RaidExited;
        public event EventHandler<RaidInfoEventArgs> RaidEnded;
        public event EventHandler<TaskStatusMessageEventArgs> TaskModified;
        public event EventHandler<TaskStatusMessageEventArgs> TaskStarted;
        public event EventHandler<TaskStatusMessageEventArgs> TaskFailed;
        public event EventHandler<TaskStatusMessageEventArgs> TaskFinished;
        public event EventHandler<FleaSoldMessageEventArgs> FleaSold;
        public event EventHandler<FleaExpiredeMessageEventArgs> FleaOfferExpired;
        public event EventHandler<PlayerPositionEventArgs> PlayerPosition;
        public string LogsPath { get; set; } = "";

        public GameWatcher()
        {
            Monitors = new();
            raidInfo = new RaidInfo();
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EscapeFromTarkov"))
            {
                if (key != null)
                {
                    LogsPath = System.IO.Path.Combine(key.GetValue("InstallLocation").ToString(), "Logs");
                }
            }
            watcher = new FileSystemWatcher
            {
                Filter = "*.log",
                IncludeSubdirectories = true,
                Path = LogsPath,
            };
            processTimer = new System.Timers.Timer(TimeSpan.FromSeconds(30).TotalMilliseconds)
            {
                AutoReset = true,
                Enabled = false
            };
            screenshotWatcher = new FileSystemWatcher();
        }

        public void SetupScreenshotWatcher()
        {
            try
            {
                bool screensPathExists = Directory.Exists(ScreenshotsPath);
                string watchPath = screensPathExists ? ScreenshotsPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "initialzing screenshot watcher"));
            }
        }

        private void ScreenshotWatcher_FolderCreated(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath == ScreenshotsPath)
            {
                SetupScreenshotWatcher();
            }
        }
        private void ScreenshotWatcher_Created(object sender, FileSystemEventArgs e)
        {
            try
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
                if (raidInfo.Map == "")
                {
                    return;
                }
                PlayerPosition?.Invoke(this, new(raidInfo, new Position(position.Groups["x"].Value, position.Groups["y"].Value, position.Groups["z"].Value), e.Name));
            } catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, $"parsing screenshot {e.Name}"));
            }
        }

        public void Start()
        {

            watcher.EnableRaisingEvents = true;
            processTimer.Elapsed += ProcessTimer_Elapsed;
            watcher.Created += Watcher_Created;
            UpdateProcess();
            SetupScreenshotWatcher();
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
                var logPattern = @"(?<message>^\d{4}-\d{2}-\d{2}.+$)\s*(?<json>^{[\s\S]+?^})?";
                //var logPattern = @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\|(?<logLevel>[^|]+)\|(?<logType>[^|]+)\|(?<message>.+$)\s*(?<json>^{[\s\S]+?^})?";
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
                    if (eventLine.Contains("Got notification | GroupMatchInviteAccept"))
                    {
                        // GroupMatchInviteAccept occurs when someone you send an invite accepts
                        // GroupMatchInviteSend occurs when you receive an invite and either accept or decline
                        GroupInviteAccept?.Invoke(this, jsonNode.AsObject().Deserialize<GroupEventArgs>());
                    }
                    if (eventLine.Contains("Got notification | GroupMatchUserLeave"))
                    {
                        // User left the group
                        GroupUserLeave?.Invoke(this, jsonNode.AsObject().Deserialize<GroupMatchUserLeaveEventArgs>());
                    }
					if (eventLine.Contains("Got notification | GroupMatchWasRemoved"))
                    {
                        // When the group is disbanded
                        GroupDisbanded?.Invoke(this, new());
                    }
                    if (eventLine.Contains("Got notification | GroupMatchRaidSettings"))
                    {
                        // Occurs when group leader invites members to be ready
                        GroupRaidSettings?.Invoke(this, jsonNode.AsObject().Deserialize<GroupRaidSettingsEventArgs>());
                    }
                    if (eventLine.Contains("Got notification | GroupMatchRaidReady"))
                    {
                        // Occurs for each other member of the group when ready
                        GroupMemberReady?.Invoke(this, jsonNode.AsObject().Deserialize<GroupMatchRaidReadyEventArgs>());
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
							MapLoadTime = float.Parse(Regex.Match(eventLine, @"LocationLoaded:[0-9.,]+ real:(?<loadTime>[0-9.,]+)").Groups["loadTime"].Value.Replace(",", "."), CultureInfo.InvariantCulture)
						};
						MatchingStarted?.Invoke(this, new(raidInfo));
					}
					if (eventLine.Contains("application|MatchingCompleted"))
					{
						// Matching is complete and we are locked to a server with other players
						// Just the queue time is available so far
						// Occurs on initial raid load and when the user cancels matching
                        // Does not occur when the user re-connects to a raid in progress
						var queueTimeMatch = Regex.Match(eventLine, @"MatchingCompleted:[0-9.,]+ real:(?<queueTime>[0-9.,]+)");
						raidInfo.QueueTime = float.Parse(queueTimeMatch.Groups["queueTime"].Value.Replace(",", "."), CultureInfo.InvariantCulture);
					}
                    if (eventLine.Contains("application|TRACE-NetworkGameCreate profileStatus"))
                    {
                        // Immediately after matching is complete
                        // Sufficient information is available to raise the MatchFound event
                        raidInfo.Map = Regex.Match(eventLine, "Location: (?<map>[^,]+)").Groups["map"].Value;
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
                        RaidCountdown?.Invoke(this, new(raidInfo));
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
                            //RaidStarted?.Invoke(this, new(raidInfo));
                        }
                        RaidStarted?.Invoke(this, new(raidInfo));
                        //raidInfo = new();
                    }
                    if (eventLine.Contains("application|Network game matching aborted") || eventLine.Contains("application|Network game matching cancelled"))
                    {
                        // User cancelled matching
                        MatchingAborted?.Invoke(this, new(raidInfo));
                        raidInfo = new();
                    }
                    if (eventLine.Contains("Got notification | UserMatchOver"))
                    {
                        RaidExited?.Invoke(this, new RaidExitedEventArgs { Map = jsonNode["location"].ToString(), RaidId = jsonNode["shortId"]?.ToString() });
                        raidInfo = new();
                    }
                    if (eventLine.Contains("Init: pstrGameVersion: Escape from Tarkov"))
                    {
                        if (raidInfo.RaidType != RaidType.Unknown) {
                            RaidEnded?.Invoke(this, new(raidInfo));
                        }
                        raidInfo = new();
                    }
                    if (eventLine.Contains("Got notification | ChatMessageReceived"))
                    {
                        var messageEvent = jsonNode.AsObject().Deserialize<ChatMessageEventArgs>();
                        if (messageEvent.message.type == MessageType.PlayerMessage)
                        {
                            continue;
                        }
                        var systemMessageEvent = jsonNode.AsObject().Deserialize<SystemChatMessageEventArgs>();
                        if (messageEvent.message.type == MessageType.FleaMarket)
						{
							if (systemMessageEvent.message.templateId == "5bdabfb886f7743e152e867e 0")
							{
								FleaSold?.Invoke(this, jsonNode.AsObject().Deserialize<FleaSoldMessageEventArgs>());
								continue;
							}
							if (systemMessageEvent.message.templateId == "5bdabfe486f7743e1665df6e 0")
							{
								FleaOfferExpired?.Invoke(this, jsonNode.AsObject().Deserialize<FleaExpiredeMessageEventArgs>());
								continue;
							}
						}
                        if (systemMessageEvent.message.type >= MessageType.TaskStarted && systemMessageEvent.message.type <= MessageType.TaskFinished)
                        {
                            var args = jsonNode.AsObject().Deserialize<TaskStatusMessageEventArgs>();
                            TaskModified?.Invoke(this, args);
                            if (args.Status == TaskStatus.Started)
                            {
                                TaskStarted?.Invoke(this, args);
                            }
                            if (args.Status == TaskStatus.Failed)
                            {
                                TaskFailed?.Invoke(this, args);
                            }
                            if (args.Status == TaskStatus.Finished)
                            {
                                TaskFinished?.Invoke(this, args);
                            }
                        }
                    }
                    
                }
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, $"parsing {e.Type} log data {e.Data}"));
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
                GameLogType logType;
                // Check which type of log file this is by the filename
                if (logFile.Contains("application.log"))
                {
                    logType = GameLogType.Application;
                }
                else if (logFile.Contains("notifications.log"))
                {
                    logType = GameLogType.Notifications;
                }
                else if (logFile.Contains("traces.log"))
                {
                    // logType = GameLogType.Traces;
                    // Traces are not currently used, so skip them
                    continue;
                }
                else
                {
                    // We're not a known log type, so skip this file
                    continue;
                }

                // Read the file into memory using UTF-8 encoding
                var fileContents = File.ReadAllText(logFile, Encoding.UTF8);

                GameWatcher_NewLogData(this, new NewLogDataEventArgs { Type = logType, Data = fileContents });
            }
        }

        public LogDetails? GetLogDetails(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                return null;
            }
            var appLogPath = "";
            foreach (var file in Directory.GetFiles(folderPath))
            {
                if (file.EndsWith("application.log"))
                {
                    appLogPath = file;
                    break;
                }
            }
            if (appLogPath == "")
            {
                return null;
            }
            var applicationLog = File.ReadAllText(appLogPath);
            var match = Regex.Match(applicationLog, @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\|(?<version>\d+\.\d+\.\d+\.\d+)\.\d+\|(?<logLevel>[^|]+)\|(?<logType>[^|]+)\|SelectProfile ProfileId:(?<profileId>[a-f0-9]+) AccountId:(?<accountId>\d+)", RegexOptions.Multiline);
            if (!match.Success)
            {
                return null;
            }
            var dateTimeString = new Regex(@"log_(?<timestamp>\d+\.\d+\.\d+_\d+-\d+-\d+)").Match(folderPath).Groups["timestamp"].Value;
            DateTime folderDate = DateTime.ParseExact(dateTimeString, "yyyy.MM.dd_H-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
            return new LogDetails()
            {
                ProfileId = match.Groups["profileId"].Value,
                AccountId = Int32.Parse(match.Groups["accountId"].Value),
                Date = folderDate,
                Version = new Version(match.Groups["version"].Value),
                Folder = folderPath,
            };
        }

        public List<LogDetails> GetLogBreakpoints()
        {
            List<LogDetails> breakpoints = new();
            foreach (var kvp in GetLogFolders().OrderBy(key => key.Key).ToDictionary(x => x.Key, x => x.Value))
            {
                LogDetails breakpoint = GetLogDetails(kvp.Value);
                if (breakpoint == null)
                {
                    continue;
                }
                var matchingBreakpoint = breakpoints.Where((bp) => bp.Version == breakpoint.Version && bp.ProfileId == breakpoint.ProfileId).FirstOrDefault();
                if (matchingBreakpoint == null)
                {
                    breakpoints.Add(breakpoint);
                }
            }
            return breakpoints;
        }

        public void ProcessLogsFromBreakpoint(LogDetails breakpoint)
        {
            List<LogDetails> logDetails = new();
            var logFolders = Directory.GetDirectories(LogsPath);
            // For each log folder, get the details
            foreach (string folderName in logFolders)
            {
                var deets = GetLogDetails(folderName);
                if (deets == null)
                {
                    continue;
                }
                logDetails.Add(deets);
            }
            logDetails = logDetails.OrderBy(det => det.Date).ToList();
            foreach (var details in logDetails)
            {
                if (details.Date < breakpoint.Date)
                {
                    continue;
                }
                if (details.ProfileId != breakpoint.ProfileId)
                {
                    continue;
                }

                ProcessLogs(details.Folder);
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
        Insurance = 2,
        FleaMarket = 4,
        InsuranceReturn = 8,
		TaskStarted = 10,
		TaskFailed = 11,
		TaskFinished = 12,
        TwitchDrop = 13,
	}
	public enum TaskStatus
	{
        None = 0,
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
            X = float.Parse(x, CultureInfo.InvariantCulture);
            Y = float.Parse(y, CultureInfo.InvariantCulture);
            Z = float.Parse(z, CultureInfo.InvariantCulture);
        }
    }
    public class RaidExitedEventArgs : EventArgs
	{
		public string Map { get; set; }
		public string RaidId { get; set; }
	}
    public class RaidInfoEventArgs : EventArgs
    {
        public RaidInfo RaidInfo { get; set; }
        public RaidInfoEventArgs(RaidInfo raidInfo)
        {
            RaidInfo = raidInfo;
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
    public class PlayerPositionEventArgs : RaidInfoEventArgs
    {
        public Position Position { get; set; }
        public string Filename { get; set; }
        public PlayerPositionEventArgs(RaidInfo raidInfo, Position position, string filename) : base(raidInfo)
        {
            this.Position = position;
            this.Filename = filename;
        }
    }

    public class LogDetails
    {
        public string ProfileId { get; set; }
        public int AccountId { get; set; }
        public DateTime Date { get; set; }
        public Version Version { get; set; }
        public string Folder { get; set; }
    }
}
