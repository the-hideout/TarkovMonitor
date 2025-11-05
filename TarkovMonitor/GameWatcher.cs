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
        private readonly FileSystemWatcher logFileCreateWatcher;
        private readonly FileSystemWatcher screenshotWatcher;
        private string _logsPath = "";
        public static Profile CurrentProfile { get; set; } = new();
        public static bool ReadingPastLogs = false;
        public bool InitialLogsRead { get; private set; } = false;
        public string LogsPath { 
            get
            {
                if (_logsPath != "")
                {
                    return _logsPath;
                }
                if (Properties.Settings.Default.customLogsPath != null && Properties.Settings.Default.customLogsPath != "")
                {
                    _logsPath = Properties.Settings.Default.customLogsPath;
                    return _logsPath;
                }
                try
                {
                    _logsPath = GetDefaultLogsFolder();
                }
                catch (Exception ex)
                {
                    ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "getting logs path"));
                }
                return _logsPath;
            }
            set
            {
                _logsPath = value;
                if (logFileCreateWatcher.EnableRaisingEvents)
                {
                    logFileCreateWatcher.Path = LogsPath;
                    WatchLogsFolder(GetLatestLogFolder());
                }

            }
        }
        public string CurrentLogsFolder {
            get
            {
                if (Monitors.Count == 0)
                {
                    return "";
                }
                try
                {
                    var logInfo = new FileInfo(Monitors[0].Path);
                    return logInfo.DirectoryName ?? "";
                }
                catch { }
                return "";
                
            }
        }
        private readonly Dictionary<string, RaidInfo> Raids = new();
        public string ScreenshotsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Escape From Tarkov", "Screenshots");
            }
        }
        private int _accountId = 0;
        public int AccountId
        {
            get
            {
                if (_accountId > 0)
                {
                    return _accountId;
                }
                List<LogDetails> details = GetLogDetails(GetLatestLogFolder());
                if (details.Count == 0)
                {
                    return 0;
                }
                _accountId = details[^1].AccountId;
                return details[^1].AccountId;
            }
        }
        //private event EventHandler<NewLogEventArgs> NewLog;
        internal readonly Dictionary<GameLogType, LogMonitor> Monitors;
        private RaidInfo raidInfo;
        public event EventHandler<NewLogDataEventArgs>? NewLogData;
        public event EventHandler<ExceptionEventArgs>? ExceptionThrown;
        public event EventHandler<DebugEventArgs>? DebugMessage;
        public event EventHandler? GameStarted;
        public event EventHandler<LogContentEventArgs<GroupLogContent>>? GroupInviteAccept;
        public event EventHandler<LogContentEventArgs<GroupRaidSettingsLogContent>>? GroupRaidSettings;
        public event EventHandler<LogContentEventArgs<GroupMatchRaidReadyLogContent>>? GroupMemberReady;
        public event EventHandler? GroupDisbanded;
        public event EventHandler<LogContentEventArgs<GroupMatchUserLeaveLogContent>>? GroupUserLeave;
        public event EventHandler<RaidInfoEventArgs>? MapLoading;
        //public event EventHandler<RaidInfoEventArgs>? MatchingStarted;
        public event EventHandler<RaidInfoEventArgs>? MatchFound; // only fires on initial load into a raid
        public event EventHandler<RaidInfoEventArgs>? MapLoaded; // fires on initial and subsequent loads into a raid
        public event EventHandler<RaidInfoEventArgs>? MatchingAborted;
        public event EventHandler<RaidInfoEventArgs>? RaidStarting;
        public event EventHandler<RaidInfoEventArgs>? RaidStarted;
        public event EventHandler<RaidExitedEventArgs>? RaidExited;
        public event EventHandler<RaidInfoEventArgs>? RaidEnded;
        public event EventHandler<RaidInfoEventArgs>? ExitedPostRaidMenus;
        public event EventHandler<LogContentEventArgs<TaskStatusMessageLogContent>>? TaskModified;
        public event EventHandler<LogContentEventArgs<TaskStatusMessageLogContent>>? TaskStarted;
        public event EventHandler<LogContentEventArgs<TaskStatusMessageLogContent>>? TaskFailed;
        public event EventHandler<LogContentEventArgs<TaskStatusMessageLogContent>>? TaskFinished;
        public event EventHandler<LogContentEventArgs<FleaSoldMessageLogContent>>? FleaSold;
        public event EventHandler<LogContentEventArgs<FleaExpiredMessageLogContent>>? FleaOfferExpired;
        public event EventHandler<PlayerPositionEventArgs>? PlayerPosition;
        public event EventHandler<ProfileEventArgs> ProfileChanged;
        public event EventHandler<ProfileEventArgs> InitialReadComplete;
        public event EventHandler<ControlSettingsEventArgs> ControlSettings;

        public static string GetDefaultLogsFolder()
        {
            using RegistryKey key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\EscapeFromTarkov") ?? throw new Exception("EFT install registry entry not found");
            return Path.Combine(key.GetValue("InstallLocation")?.ToString() ?? throw new Exception("InstallLocation registry value not found"), "Logs");
        }

        public static Dictionary<string, string> MapBundles = new() {
            { "city_preset", "TarkovStreets" },
            { "customs_preset", "bigmap" },
            { "factory_day_preset", "factory4_day" },
            { "factory_night_preset", "factory4_night" },
            { "laboratory_preset", "laboratory" },
            { "labyrinth_preset", "Labyrinth" },
            { "lighthouse_preset", "Lighthouse" },
            { "rezerv_base_preset", "RezervBase" },
            { "sandbox_preset", "Sandbox" },
            { "sandbox_high_preset", "Sandbox_high" },
            { "shopping_mall", "Interchange" },
            { "shoreline_preset", "Shoreline" },
            { "woods_preset", "Woods" },
        };

        public GameWatcher()
		{
			Monitors = new();
			raidInfo = new RaidInfo();
            logFileCreateWatcher = new FileSystemWatcher
			{
				Filter = "*.log",
				IncludeSubdirectories = true,
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
                if (!screensPathExists)
                {
                    //DebugMessage?.Invoke(this, new($"EFT screenshots folder not found; {ScreenshotsPath}"));
                }
                else
                {
                    //DebugMessage?.Invoke(this, new($"Watching EFT screenshots folder: {ScreenshotsPath}"));
                }
                string watchPath = screensPathExists ? ScreenshotsPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                screenshotWatcher.Path = watchPath;
                screenshotWatcher.IncludeSubdirectories = !screensPathExists;
                screenshotWatcher.Created -= ScreenshotWatcher_Created;
                screenshotWatcher.Created -= ScreenshotWatcher_FolderCreated;
                screenshotWatcher.Renamed -= ScreenshotWatcher_FolderCreated;
                if (screensPathExists)
                {
                    screenshotWatcher.Filter = "*.png";
                    screenshotWatcher.Created += ScreenshotWatcher_Created;
                }
                else
                {
                    screenshotWatcher.Created += ScreenshotWatcher_FolderCreated;
                    screenshotWatcher.Renamed += ScreenshotWatcher_FolderCreated;
                }
                screenshotWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "initializing screenshot watcher"));
            }
        }

        private void ScreenshotWatcher_FolderCreated(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath.ToLower() == ScreenshotsPath.ToLower())
            {
                SetupScreenshotWatcher();
            }
        }
        private void ScreenshotWatcher_Created(object sender, FileSystemEventArgs e)
        {
            try
            {
                string filename = e.Name ?? "";
                var match = Regex.Match(filename, @"\d{4}-\d{2}-\d{2}\[\d{2}-\d{2}\]_?(?<position>.+) \(\d\)\.png");
                if (!match.Success)
                {
                    return;
                }
                var position = Regex.Match(match.Groups["position"].Value, @"(?<x>-?[\d]+\.[\d]{2}), (?<y>-?[\d]+\.[\d]{2}), (?<z>-?[\d]+\.[\d]{2})_?(?<rx>-?[\d.]{1}\.[\d]{1,5}), (?<ry>-?[\d.]{1}\.[\d]{1,5}), (?<rz>-?[\d.]{1}\.[\d]{1,5}), (?<rw>-?[\d.]{1}\.[\d]{1,5})");
                if (!position.Success)
                {
                    return;
                }
                var raid = raidInfo;
                if ((raid.Map == null || raid.Map == "") && Properties.Settings.Default.customMap != "")
                {
                    raid = new()
                    {
                        Map = Properties.Settings.Default.customMap,
                    };
                }
                if (raid.Map == null)
                {
                    return;
                }

                var rotation = QuarternionsToYaw(float.Parse(position.Groups["rx"].Value, CultureInfo.InvariantCulture), float.Parse(position.Groups["ry"].Value, CultureInfo.InvariantCulture), float.Parse(position.Groups["rz"].Value, CultureInfo.InvariantCulture), float.Parse(position.Groups["rw"].Value, CultureInfo.InvariantCulture));
                PlayerPosition?.Invoke(this, new(raid, CurrentProfile, new Position(position.Groups["x"].Value, position.Groups["y"].Value, position.Groups["z"].Value), rotation, filename));
                raid.Screenshots.Add(filename);
            } catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, $"parsing screenshot {e.Name}"));
            }
        }

        private float QuarternionsToYaw(float x, float z, float y, float w)
        {
            // Calculate singularity test
            // Roll (x-axis rotation)
            /*float sinr_cosp = 2.0f * (w * x + y * z);
            float cosr_cosp = 1.0f - 2.0f * (x * x + y * y);
            float roll = (float)Math.Atan2(sinr_cosp, cosr_cosp);

            // Pitch (y-axis rotation)
            float sinp = 2.0f * (w * y - z * x);
            float pitch;
            if (Math.Abs(sinp) >= 1)
                pitch = Math.Sign(sinp) * (float)Math.PI / 2;  // Pitch is 90 degrees if out of range
            else
                pitch = (float)Math.Asin(sinp);*/

            // Yaw (z-axis rotation)
            float siny_cosp = 2.0f * (w * z + x * y);
            float cosy_cosp = 1.0f - 2.0f * (y * y + z * z);
            float yaw = (float)Math.Atan2(siny_cosp, cosy_cosp);

            // Convert radians to degrees
            //roll *= (180f / (float)Math.PI);
            //pitch *= (180f / (float)Math.PI);
            yaw *= (180f / (float)Math.PI);

            //System.Diagnostics.Debug.WriteLine($"roll: {roll}, pitch: {pitch}, yaw: {yaw}");

            return yaw;
        }

        public void Start()
        {
			try
			{
                logFileCreateWatcher.Path = LogsPath;
				logFileCreateWatcher.Created += LogFileCreateWatcher_Created;
				logFileCreateWatcher.EnableRaisingEvents = true;
				processTimer.Elapsed += ProcessTimer_Elapsed;
				UpdateProcess();
				SetupScreenshotWatcher();
				processTimer.Enabled = true;
				if (Monitors.Count == 0)
				{
					WatchLogsFolder(GetLatestLogFolder());
				}
			}
			catch (Exception ex)
			{
                ExceptionThrown?.Invoke(this, new(ex, "starting game watcher"));
			}
        }

        private void LogFileCreateWatcher_Created(object sender, FileSystemEventArgs e)
        {
            string filename = e.Name ?? "";
            if (filename.Contains("application.log"))
            {
                StartNewMonitor(e.FullPath);
                _accountId = 0;
            }
            if (filename.Contains("notifications.log"))
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
                var logPattern = @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\|(?<message>.+$)\s*(?<json>^{[\s\S]+?^})?";
                //var logPattern = @"(?<message>^\d{4}-\d{2}-\d{2}.+$)\s*(?<json>^{[\s\S]+?^})?";
                //var logPattern = @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})\|(?<logLevel>[^|]+)\|(?<logType>[^|]+)\|(?<message>.+$)\s*(?<json>^{[\s\S]+?^})?";
                var logMessages = Regex.Matches(e.Data, logPattern, RegexOptions.Multiline);

#if DEBUG                
                //Debug.WriteLine("===log chunk start===");
                //Debug.WriteLine(e.Data);
                //Debug.WriteLine("===log chunk end===");
#endif

                foreach (Match logMessage in logMessages)
                {
                    var eventDate = new DateTime();
                    DateTime.TryParseExact(logMessage.Groups["date"].Value + " " + logMessage.Groups["time"].Value.Split(" ")[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out eventDate);
                    var eventLine = logMessage.Groups["message"].Value;
                    if (eventLine.Contains("Session mode: "))
                    {
                        var modeMatch = Regex.Match(eventLine, @"Session mode: (?<mode>\w+)");
                        if (!modeMatch.Success)
                        {
                            continue;
                        }
                        CurrentProfile.Type = Enum.Parse<ProfileType>(modeMatch.Groups["mode"].Value, true);
                        raidInfo.ProfileType = CurrentProfile.Type;
                        continue;
                    }
                    if (eventLine.Contains("SelectProfile ProfileId:"))
                    {
                        var profileIdMatch = Regex.Match(eventLine, @"SelectProfile ProfileId:(?<profileId>\w+) AccountId:(?<accountId>\d+)");
                        if (!profileIdMatch.Success)
                        {
                            continue;
                        }
                        CurrentProfile.Id = profileIdMatch.Groups["profileId"].Value;
                        CurrentProfile.AccountId = profileIdMatch.Groups["accountId"].Value;
                        if (!e.InitialRead)
                        {
                            if (raidInfo.StartedTime != null && raidInfo.EndedTime == null)
                            {
                                raidInfo.EndedTime = eventDate;
                                RaidEnded?.Invoke(this, new(raidInfo, CurrentProfile));
                            }
                            else
                            {
                                ProfileChanged?.Invoke(this, new(CurrentProfile));
                            }
                        }
                        continue;
                    }
                    if (eventLine.Contains("Control settings:"))
                    {
                        if (!logMessage.Groups["json"].Success)
                        {
                            continue;                            
                        }
                        var node = JsonNode.Parse(logMessage.Groups["json"].Value);
                        if (node == null)
                        {
                            continue;
                        }
                        ControlSettings?.Invoke(this, new ControlSettingsEventArgs() { ControlSettings = node });
                    }
                    if (e.InitialRead)
                    {
                        continue;
                    }
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
                        GroupInviteAccept?.Invoke(this, new LogContentEventArgs<GroupLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<GroupLogContent>() ?? throw new Exception("Error parsing GroupEventArgs"), Profile = CurrentProfile });
                    }
                    if (eventLine.Contains("Got notification | GroupMatchUserLeave"))
                    {
                        // User left the group
                        GroupUserLeave?.Invoke(this, new LogContentEventArgs<GroupMatchUserLeaveLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<GroupMatchUserLeaveLogContent>() ?? throw new Exception("Error parsing GroupMatchUserLeaveEventArgs"), Profile = CurrentProfile });
                    }
					if (eventLine.Contains("Got notification | GroupMatchWasRemoved"))
                    {
                        // When the group is disbanded
                        GroupDisbanded?.Invoke(this, new());
                    }
                    if (eventLine.Contains("Got notification | GroupMatchRaidSettings"))
                    {
                        // Occurs when group leader invites members to be ready
                        GroupRaidSettings?.Invoke(this, new LogContentEventArgs<GroupRaidSettingsLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<GroupRaidSettingsLogContent>() ?? throw new Exception("Error parsing GroupRaidSettingsEventArgs"), Profile = CurrentProfile });
                    }
                    if (eventLine.Contains("Got notification | GroupMatchRaidReady"))
                    {
                        // Occurs for each other member of the group when ready
                        GroupMemberReady?.Invoke(this, new LogContentEventArgs<GroupMatchRaidReadyLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<GroupMatchRaidReadyLogContent>() ?? throw new Exception("Error parsing GroupMatchRaidReadyEventArgs"), Profile = CurrentProfile });
                    }
                    /*if (eventLine.Contains("application|Matching with group id"))
                    {
                        MapLoading?.Invoke(this, new());
                    }*/
                    if (eventLine.Contains("application|scene preset path:"))
                    {
                        // When a map starts loading
                        raidInfo = new()
                        {
                            ProfileType = CurrentProfile.Type,
                        };
                        var bundleMatch = Regex.Match(eventLine, @"scene preset path:maps\/(?<mapBundleName>[a-zA-Z0-9_]+)\.bundle");
                        if (bundleMatch.Success)
                        {
                            var mapBundle = bundleMatch.Groups["mapBundleName"].Value;
                            if (MapBundles.ContainsKey(mapBundle))
                            {
                                string mapId = MapBundles[mapBundle];
                                raidInfo.Map = mapId;
                                MapLoading?.Invoke(this, new(raidInfo, CurrentProfile));
                            }
                        }
                    }
                    if (eventLine.Contains("application|LocationLoaded"))
                    {
                        // The map has been loaded and the game is searching for a match
                        raidInfo.MapLoadTime = float.Parse(Regex.Match(eventLine, @"LocationLoaded:[0-9.,]+ real:(?<loadTime>[0-9.,]+)").Groups["loadTime"].Value.Replace(",", "."), CultureInfo.InvariantCulture);
						//MatchingStarted?.Invoke(this, new(raidInfo, CurrentProfile));
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
                        var mapUnknown = raidInfo.Map == "" || raidInfo.Map == null;
                        raidInfo.Map = Regex.Match(eventLine, "Location: (?<map>[^,]+)").Groups["map"].Value;
                        raidInfo.Online = eventLine.Contains("RaidMode: Online");
                        raidInfo.RaidId = Regex.Match(eventLine, @"shortId: (?<raidId>[A-Z0-9]{6})").Groups["raidId"].Value;
                        if (Raids.ContainsKey(raidInfo.RaidId)) {
                            raidInfo = Raids[raidInfo.RaidId];
                            raidInfo.Reconnected = true;
                        }
                        else
                        {
                            Raids.Add(raidInfo.RaidId, raidInfo);
                        }
                        if (!raidInfo.Reconnected && raidInfo.Online && raidInfo.QueueTime > 0)
                        {
                            // Raise the MatchFound event only if we queued; not if we are re-loading back into a raid
                            MatchFound?.Invoke(this, new(raidInfo, CurrentProfile));
                        }
                        if (mapUnknown)
                        {
                            MapLoading?.Invoke(this, new(raidInfo, CurrentProfile));
                        }
                        MapLoaded?.Invoke(this, new(raidInfo, CurrentProfile));
                    }
                    if (eventLine.Contains("application|GameStarting"))
                    {
                        // GameStarting always happens for PMCs and sometimes happens for scavs.
                        // For PMCs, it corresponds with the start of the countdown timer.
                        if (!raidInfo.Reconnected)
                        {
                            raidInfo.StartingTime = eventDate;
                        }
                        RaidStarting?.Invoke(this, new(raidInfo, CurrentProfile));
                    }
                    if (eventLine.Contains("application|GameStarted"))
                    {
                        // Raid begins, either at the end of the countdown for PMC, or immediately as a scav
                        if (!raidInfo.Reconnected)
                        {
                            raidInfo.StartedTime = eventDate;
                        }
                        RaidStarted?.Invoke(this, new(raidInfo, CurrentProfile));
                        //raidInfo = new();
                    }
                    if (eventLine.Contains("application|Network game matching aborted") || eventLine.Contains("application|Network game matching cancelled"))
                    {
                        // User cancelled matching
                        MatchingAborted?.Invoke(this, new(raidInfo, CurrentProfile));
                        raidInfo = new()
                        {
                            ProfileType = CurrentProfile.Type,
                        };
                    }
                    if (eventLine.Contains("Got notification | UserMatchOver"))
                    {
                        RaidExited?.Invoke(this, new RaidExitedEventArgs { Map = jsonNode?["location"]?.ToString() ?? throw new Exception("Error parsing raid location"), RaidId = jsonNode?["shortId"]?.ToString() });
                        raidInfo = new()
                        {
                            ProfileType = CurrentProfile.Type,
                        };
                    }
                    if (eventLine.Contains("application|Init: pstrGameVersion: "))
                    {
                        if (raidInfo.EndedTime != null)
                        {
                            ExitedPostRaidMenus?.Invoke(this, new(raidInfo, CurrentProfile));
                            raidInfo = new()
                            {
                                ProfileType = CurrentProfile.Type,
                            };
                        }
                    }
                    if (eventLine.Contains("Got notification | ChatMessageReceived"))
                    {
                        var messageEvent = jsonNode?.AsObject().Deserialize<ChatMessageLogContent>() ?? throw new Exception("Error parsing ChatMessageLogContent");
                        if (messageEvent.message.type == MessageType.PlayerMessage)
                        {
                            continue;
                        }
                        var systemMessageEvent = jsonNode?.AsObject().Deserialize<SystemChatMessageLogContent>() ?? throw new Exception ("Error parsing SystemChatMessageLogContent");
                        if (messageEvent.message.type == MessageType.FleaMarket)
						{
							if (systemMessageEvent.message.templateId == "5bdabfb886f7743e152e867e 0")
							{
								FleaSold?.Invoke(this, new LogContentEventArgs<FleaSoldMessageLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<FleaSoldMessageLogContent>() ?? throw new Exception("Error parsing FleaSoldMessageLogContent"), Profile = CurrentProfile });
								continue;
							}
							if (systemMessageEvent.message.templateId == "5bdabfe486f7743e1665df6e 0")
							{
								FleaOfferExpired?.Invoke(this, new LogContentEventArgs<FleaExpiredMessageLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<FleaExpiredMessageLogContent>() ?? throw new Exception("Error parsing FleaExpiredMessageLogContent"), Profile = CurrentProfile });
								continue;
							}
						}
                        if (systemMessageEvent.message.type >= MessageType.TaskStarted && systemMessageEvent.message.type <= MessageType.TaskFinished)
                        {
                            var args = jsonNode?.AsObject().Deserialize<TaskStatusMessageLogContent>() ?? throw new Exception("Error parsing TaskStatusMessageLogContent");
                            TaskModified?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = CurrentProfile });
                            if (args.Status == TaskStatus.Started)
                            {
                                TaskStarted?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = CurrentProfile });
                            }
                            if (args.Status == TaskStatus.Failed)
                            {
                                TaskFailed?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = CurrentProfile });
                            }
                            if (args.Status == TaskStatus.Finished)
                            {
                                TaskFinished?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = CurrentProfile });
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
			Dictionary<DateTime, string> folderDictionary = new();
            if (LogsPath == "")
            {
                return folderDictionary;
			}

			// Find all of the log folders in the Logs directory
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
        }

        // Process the log files in the specified folder
        public void ProcessLogs(LogDetails target, List<LogDetails> profiles)
        {
            for (var i = 0; i < profiles.Count; i++)
            {
                var logProfile = profiles[i];
                if (logProfile.Profile.Id != target.Profile.Id)
                {
                    continue;
                }
                var endDate = DateTime.Now.AddYears(1);
                if (profiles.Count > 1 && i + 1 < profiles.Count)
                {
                    endDate = profiles[i + 1].Date;
                }
                var logFiles = Directory.GetFiles(logProfile.Folder);
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
                    using var fileStream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var textReader = new StreamReader(fileStream, Encoding.UTF8);
                    var fileContents = textReader.ReadToEnd();

                    var logPattern = @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3}) (?<timeOffset>[+-]\d{2}:\d{2})\|(?<message>.+$)\s*(?<json>^{[\s\S]+?^})?";
                    var logMessages = Regex.Matches(fileContents, logPattern, RegexOptions.Multiline);

                    foreach (Match match in logMessages)
                    {
                        var dateTimeString = match.Groups["date"].Value + " " + match.Groups["time"].Value;
                        DateTime logMessageDate = DateTime.ParseExact(dateTimeString, "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

                        if (logMessageDate < logProfile.Date || logMessageDate >= endDate)
                        {
                            continue;
                        }

                        GameWatcher_NewLogData(this, new NewLogDataEventArgs { Type = logType, Data = match.Value });
                    }
                }
            }
        }

        public List<LogDetails> GetLogDetails(string folderPath)
        {
            List<LogDetails> logDetails = new();
            if (!Directory.Exists(folderPath))
            {
                return logDetails;
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
                return logDetails;
            }
            using var fileStream = new FileStream(appLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var textReader = new StreamReader(fileStream, Encoding.UTF8);
            var applicationLog = textReader.ReadToEnd();
            var matches = Regex.Matches(applicationLog, @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3}) (?<timeOffset>[+-]\d{2}:\d{2})\|(?<version>\d+\.\d+\.\d+\.\d+)\.\d+\|(?<logLevel>[^|]+)\|(?<logType>[^|]+)\|SelectProfile ProfileId:(?<profileId>[a-f0-9]+) AccountId:(?<accountId>\d+)", RegexOptions.Multiline);
            if (matches.Count == 0)
            {
                return logDetails;
            }
            var profileTypeMatches = Regex.Matches(applicationLog, @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3}) (?<timeOffset>[+-]\d{2}:\d{2})\|(?<version>\d+\.\d+\.\d+\.\d+)\.\d+\|(?<logLevel>[^|]+)\|(?<logType>[^|]+)\|Session mode: (?<profileType>\w+)", RegexOptions.Multiline);
            for (var i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                var dateTimeString = match.Groups["date"].Value + " " + match.Groups["time"].Value;
                DateTime profileDate = DateTime.ParseExact(dateTimeString, "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                ProfileType profileType = ProfileType.Regular;
                if (matches.Count == profileTypeMatches.Count)
                {
                    profileType = Enum.Parse<ProfileType>(profileTypeMatches[i].Groups["profileType"].Value, true);
                }
                logDetails.Add(new LogDetails()
                {
                    Profile = new() { Id = match.Groups["profileId"].Value, Type = profileType },
                    AccountId = Int32.Parse(match.Groups["accountId"].Value),
                    Date = profileDate,
                    Version = new Version(match.Groups["version"].Value),
                    Folder = folderPath,
                });
            }
            return logDetails;
        }

        public List<LogDetails> GetLogBreakpoints(string profileId)
        {
            List<LogDetails> breakpoints = new();
            if (profileId == "")
            {
                return breakpoints;
            }
            foreach (var kvp in GetLogFolders().OrderBy(key => key.Key).ToDictionary(x => x.Key, x => x.Value))
            {
                List<LogDetails> folderBreakpoints = GetLogDetails(kvp.Value);
                foreach(var breakpoint in folderBreakpoints)
                {
                    if (breakpoint.Profile.Id != profileId)
                    {
                        continue;
                    }
                    var matchingBreakpoint = breakpoints.Where((bp) => bp.Version == breakpoint.Version && bp.Profile.Id == breakpoint.Profile.Id).FirstOrDefault();
                    if (matchingBreakpoint == null)
                    {
                        breakpoints.Add(breakpoint);
                    }
                }
            }
            return breakpoints;
        }

        public void ProcessLogsFromBreakpoint(LogDetails breakpoint)
        {
            List<List<LogDetails>> logDetails = new();
            var logFolders = Directory.GetDirectories(LogsPath);
            // For each log folder, get the details
            foreach (string folderName in logFolders)
            {
                var details = GetLogDetails(folderName);
                if (details.Count == 0)
                {
                    continue;
                }
                if (!details.Any(d => d.Profile.Id == breakpoint.Profile.Id))
                {
                    continue;
                }
                if (!details.Any(d => d.Date >= breakpoint.Date))
                {
                    continue;
                }
                logDetails.Add(details);
            }
            logDetails = logDetails.OrderBy(det => det[0].Date).ToList();
            foreach (var details in logDetails)
            {
                ProcessLogs(breakpoint, details);
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

            } catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new(ex, "watching for EFT process"));
            }
        }

        private string GetLatestLogFolder()
        {
            var logFolders = System.IO.Directory.GetDirectories(LogsPath);
            var latestDate = new DateTime(0);
            var latestLogFolder = logFolders.Last();
            foreach (var logFolder in logFolders)
            {
                var dateTimeMatch = Regex.Match(logFolder, @"log_(?<timestamp>\d+\.\d+\.\d+_\d+-\d+-\d+)").Groups["timestamp"];
                if (!dateTimeMatch.Success)
                {
                    continue;
                }
                var dateTimeString = dateTimeMatch.Value;

                var logDate = DateTime.ParseExact(dateTimeString, "yyyy.MM.dd_H-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
                if (logDate > latestDate)
                {
                    latestDate = logDate;
                    latestLogFolder = logFolder;
                }
            }
            return latestLogFolder ?? "";
        }

        private void WatchLogsFolder(string folderPath)
        {
            var files = System.IO.Directory.GetFiles(folderPath);
            var monitorsStarted = 0;
            var monitorsCompletedInitialRead = 0;
            List<string> monitoringLogs = new() { "notifications.log", "application.log" };
            foreach (var file in files)
            {
                foreach (var logType in monitoringLogs)
                {
                    monitorsStarted++;
                    if (!file.Contains(logType))
                    {
                        monitorsCompletedInitialRead++;
                        continue;
                    }
                    var monitor = StartNewMonitor(file);
                    if (monitor == null || InitialLogsRead)
                    {
                        monitorsCompletedInitialRead++;
                        break;
                    }
                    monitor.InitialReadComplete += (object? sender, EventArgs e) => {
                        monitorsCompletedInitialRead++;
                        if (monitorsCompletedInitialRead == monitorsStarted)
                        {
                            InitialLogsRead = true;
                            InitialReadComplete?.Invoke(this, new(CurrentProfile));
                        }
                    };
                    break;
                }
            }
        }

        private LogMonitor? StartNewMonitor(string path)
        {
            GameLogType? newType = null;
            if (path.Contains("application.log"))
            {
                newType = GameLogType.Application;
                CurrentProfile = new();
            }
            if (path.Contains("notifications.log"))
            {
                newType = GameLogType.Notifications;
            }
            if (path.Contains("traces.log"))
            {
                newType = GameLogType.Traces;
            }
            if (newType == null)
            {
                return null;
            }
            //Debug.WriteLine($"Starting new {newType} monitor at {path}");
            if (Monitors.ContainsKey((GameLogType)newType))
            {
                Monitors[(GameLogType)newType].Stop();
            }
            var newMon = new LogMonitor(path, (GameLogType)newType);
            newMon.NewLogData += GameWatcher_NewLogData;
            newMon.Exception += (sender, e) => {
                ExceptionThrown?.Invoke(sender, e);
            };
            newMon.Start();
            Monitors[(GameLogType)newType] = newMon;
            return newMon;
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
		Scav,
        PVE,
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
        public bool Reconnected { get; set; }
        public RaidType RaidType { 
            get
            {
                if (this.ProfileType == ProfileType.PVE)
                {
                    return RaidType.PVE;
                }
                // if raid hasn't started, we don't have enough info to know what type it is
                if (StartedTime == null)
                {
                    return RaidType.Unknown;
                }

                // if GameStarting appeared, could be PMC or scav
                // check time elapsed between the two to account for the PMC countdown
                if (StartingTime != null && (StartedTime - StartingTime)?.TotalSeconds > 3)
                {
                    return RaidType.PMC;
                }

                // not PMC, so must be scav
                return RaidType.Scav;
            }
        }
        public DateTime? StartingTime { get; set; }
        public DateTime? StartedTime { get; set; }
        public DateTime? EndedTime { get; set; }
        public List<string> Screenshots { get; set; } = new();
        public ProfileType ProfileType { get; set; } = ProfileType.Regular;
        public RaidInfo()
        {
            Map = "";
            Online = false;
            RaidId = "";
            MapLoadTime = 0;
            QueueTime = 0;
            Reconnected = false;
            //RaidType = RaidType.Unknown;
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
		public string? RaidId { get; set; }
	}
    public class RaidInfoEventArgs : EventArgs
    {
        public RaidInfo RaidInfo { get; set; }
        public Profile Profile { get; set; }
        public RaidInfoEventArgs(RaidInfo raidInfo, Profile profile)
        {
            RaidInfo = raidInfo;
            Profile = profile;
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
        public float Rotation { get; set; }
        public string Filename { get; set; }
        public PlayerPositionEventArgs(RaidInfo raidInfo, Profile profile, Position position, float rotation, string filename) : base(raidInfo, profile)
        {
            this.Position = position;
            this.Rotation = rotation;
            this.Filename = filename;
        }
    }

    public class LogDetails
    {
        public Profile Profile { get; set; }
        public int AccountId { get; set; }
        public DateTime Date { get; set; }
        public Version Version { get; set; }
        public string Folder { get; set; }
    }

    public enum ProfileType
    {
        PVE,
        Regular,
    }

    public class Profile
    {
        public string Id { get; set; } = "";
        public ProfileType Type { get; set; } = ProfileType.Regular;
        public string AccountId { get; set; } = "";
    }

    public class ProfileEventArgs : EventArgs
    {
        public Profile Profile { get; set; }
        public ProfileEventArgs(Profile profile)
        {
            Profile = profile;
        }
    }

    public class LogContentEventArgs<T> : EventArgs where T : JsonLogContent
    {
        public T LogContent { get; set; }
        public Profile Profile { get; set; }

    }

    public class ControlSettingsEventArgs : EventArgs
    {
        public JsonNode ControlSettings { get; set; }
    }
}
