using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using System.Globalization;
using System.Reflection;
using Microsoft.Win32;

namespace TarkovMonitor
{
    internal class GameWatcher
    {
        private enum LogParsingStage
        {
            Batch,
            Entry,
            SessionMode,
            Profile,
            ControlSettings,
            Group,
            MapLoading,
            LocationLoaded,
            MatchingCompleted,
            NetworkGameCreate,
            RaidExit,
            ChatMessage,
        }

        private Process? process;
        private const string GameStoppingMarker = "EFT.NetworkGame`1:GameStopping()";
        private string outputLogTail = "";
        private readonly HashSet<string> reportedLogParsingFailures = new();
        private readonly object reportedLogParsingFailuresLock = new();
        private readonly System.Timers.Timer processTimer;
        private readonly FileSystemWatcher logFileCreateWatcher;
        private readonly FileSystemWatcher screenshotWatcher;
        private readonly bool historicalReplay;
        private Profile parsingProfile = new();
        private Profile ActiveProfile => historicalReplay ? parsingProfile : CurrentProfile;
        private bool observedGameRunning;
        private string _logsPath = "";
        public static Profile CurrentProfile { get; set; } = new();
        public static string LastDetectedSessionMode { get; private set; } = Properties.Settings.Default.lastTarkovSessionMode;
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
        public bool IsGameRunning
        {
            get
            {
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        return true;
                    }

                    // The 30-second watcher may not have observed a just-launched client
                    // yet, so confirm against the process list before historical parsing.
                    var processes = Process.GetProcessesByName("EscapeFromTarkov");
                    try
                    {
                        return processes.Length > 0;
                    }
                    finally
                    {
                        foreach (var candidate in processes)
                        {
                            candidate.Dispose();
                        }
                    }
                }
                catch
                {
                    // If process state cannot be read, fail closed. Historical parsing
                    // must not risk replacing a live session identity.
                    return true;
                }
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

        //private event EventHandler<NewLogEventArgs> NewLog;
        internal readonly Dictionary<GameLogType, LogMonitor> Monitors;
        private RaidInfo raidInfo;
        public event EventHandler<NewLogDataEventArgs>? NewLogData;
        public event EventHandler<ExceptionEventArgs>? ExceptionThrown;
        public event EventHandler<DebugEventArgs>? DebugMessage;
        public event EventHandler? GameStarted;
        public event EventHandler? GameStopped;
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
        public event EventHandler? RaidStopping;
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
        public event EventHandler<ProfileChangingEventArgs>? ProfileChanging;
        public event EventHandler<ProfileEventArgs> ProfileChanged;
        public event EventHandler<ProfileEventArgs> InitialReadComplete;
        public event EventHandler<ControlSettingsEventArgs> ControlSettings;

        private static string logPatternPrefix = @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3})(?<tzoffset> [+-]\d{2}:\d{2})?\|";
        private static string logPattern = @$"{logPatternPrefix}(?<message>.+$)\s*(?<json>^{{[\s\S]+?^}})?";
        private const string ProfileSelectionMarkerPattern = @"\b(?:Select(?:ed)?Profile|PrepareSelectedProfileLocally|CompleteSelectedProfile) ProfileId:";
        private const string ProfileSelectionPattern = ProfileSelectionMarkerPattern + @"(?<profileId>\w+) AccountId:(?<accountId>\d+)";

        public static string GetDefaultLogsFolder()
        {
            string[] paths = {
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 3932890"
            };
            foreach (var path in paths)
            {
                using RegistryKey? regKey = Registry.LocalMachine.OpenSubKey(path);
                if (regKey == null)
                {
                    continue;
                }
                var installPath = regKey.GetValue("InstallLocation")?.ToString();
                if (installPath == null)
                {
                    continue;
                }
                var logsPath = Path.Combine(installPath, "Logs");
                if (!Directory.Exists(logsPath))
                {
                    logsPath = Path.Combine(installPath, "build", "Logs");
                }
                if (Directory.Exists(logsPath))
                {
                    return logsPath;
                }
            }
		    throw new Exception("No Tarkov install path found");
		}

        public GameWatcher(bool historicalReplay = false)
		{
			this.historicalReplay = historicalReplay;
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
            //DebugMessage?.Invoke(this, new($"Watching screenshot folder {screenshotWatcher.Path}"));
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
                if ((raid.Map == null) && Properties.Settings.Default.customMap != "")
                {
                    raid = new()
                    {
                        Map = TarkovDev.Maps.Find(m => m.nameId == Properties.Settings.Default.customMap),
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
            if (filename.Contains("application.log") || filename.Contains("application_000.log"))
            {
                StartNewMonitor(e.FullPath);
            }
            if (filename.Contains("notifications.log") || filename.Contains("notifications_000.log"))
            {
                StartNewMonitor(e.FullPath);
            }
            if (filename.Contains("output.log") || filename.Contains("output_000.log"))
            {
                StartNewMonitor(e.FullPath);
            }
        }

        internal static EftSessionMode ResolveSessionMode(string? rawSessionMode)
        {
            if (string.Equals(rawSessionMode, "Pve", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawSessionMode, "PVE", StringComparison.OrdinalIgnoreCase))
            {
                return EftSessionMode.PVE;
            }
            if (string.Equals(rawSessionMode, "Regular", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawSessionMode, "PVP", StringComparison.OrdinalIgnoreCase))
            {
                return EftSessionMode.Regular;
            }
            if (string.Equals(rawSessionMode, "PvpSeason", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawSessionMode, "Seasonal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawSessionMode, "SN1", StringComparison.OrdinalIgnoreCase))
            {
                return EftSessionMode.Seasonal;
            }

            return EftSessionMode.Unknown;
        }

        internal static ProfileType ResolveProfileType(EftSessionMode sessionMode)
        {
            return sessionMode switch
            {
                EftSessionMode.PVE => ProfileType.PVE,
                EftSessionMode.Regular => ProfileType.Regular,
                EftSessionMode.Seasonal => ProfileType.PvpSeason,
                _ => ProfileType.Unknown,
            };
        }

        private static LogParsingStage GetLogParsingStage(string eventLine)
        {
            if (eventLine.Contains("Session mode: ")) return LogParsingStage.SessionMode;
            if (Regex.IsMatch(eventLine, ProfileSelectionMarkerPattern)) return LogParsingStage.Profile;
            if (eventLine.Contains("Control settings:")) return LogParsingStage.ControlSettings;
            if (eventLine.Contains("Got notification | Group")) return LogParsingStage.Group;
            if (eventLine.Contains("scene preset path:")) return LogParsingStage.MapLoading;
            if (eventLine.Contains("application|LocationLoaded")) return LogParsingStage.LocationLoaded;
            if (eventLine.Contains("application|MatchingCompleted")) return LogParsingStage.MatchingCompleted;
            if (eventLine.Contains("application|TRACE-NetworkGameCreate profileStatus")) return LogParsingStage.NetworkGameCreate;
            if (eventLine.Contains("Got notification | UserMatchOver")) return LogParsingStage.RaidExit;
            if (eventLine.Contains("Got notification | ChatMessageReceived")) return LogParsingStage.ChatMessage;
            return LogParsingStage.Entry;
        }

        private void ReportLogParsingFailure(Exception exception, GameLogType logType, LogParsingStage stage)
        {
            var scope = stage == LogParsingStage.Batch ? "batch" : "entry";
            var signature = $"{scope}:{logType}:{stage}:{exception.GetType().FullName}";
            lock (reportedLogParsingFailuresLock)
            {
                if (!reportedLogParsingFailures.Add(signature))
                {
                    return;
                }
            }

            var reportCode = stage == LogParsingStage.Batch ? "TM-EFT-LOG-002" : "TM-EFT-LOG-001";
            var assembly = typeof(GameWatcher).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            var userMessage = $"{reportCode} | EFT {logType} log {scope} could not be processed | Stage: {stage} | Exception: {exception.GetType().Name} | Tarkov Monitor: {version} | Monitoring continued. No EFT log contents are included. Copy this message when reporting the issue.";
            ExceptionThrown?.Invoke(this, new ExceptionEventArgs(exception, $"processing {logType} log {scope}", userMessage));
        }

        internal void GameWatcher_NewLogData(object? sender, NewLogDataEventArgs e)
        {
            try
            {
                //DebugMessage?.Invoke(this, new DebugEventArgs(e.NewMessage));
                NewLogData?.Invoke(this, e);
                if (e.Type == GameLogType.Output)
                {
                    string outputData = outputLogTail + e.Data;
                    if (outputData.Contains(GameStoppingMarker))
                    {
                        RaidStopping?.Invoke(this, EventArgs.Empty);
                    }

                    int tailLength = Math.Min(outputData.Length, GameStoppingMarker.Length - 1);
                    outputLogTail = outputData[^tailLength..];

                    // output.log repeats messages that are already handled by the
                    // dedicated application and notification log monitors. It is
                    // watched only for the earlier GameStopping marker.
                    return;
                }
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
                    var parsingStage = LogParsingStage.Entry;
                    try
                    {
                        var eventDate = new DateTime();
                        DateTime.TryParseExact(logMessage.Groups["date"].Value + " " + logMessage.Groups["time"].Value.Split(" ")[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out eventDate);
                        var eventLine = logMessage.Groups["message"].Value;
                        parsingStage = GetLogParsingStage(eventLine);
                    //System.Diagnostics.Debug.WriteLine(eventLine);
                    if (eventLine.Contains("Session mode: "))
                    {
                        var modeMatch = Regex.Match(eventLine, @"Session mode: (?<mode>\w+)");
                        if (!modeMatch.Success)
                        {
                            continue;
                        }
                        var sessionMode = ResolveSessionMode(modeMatch.Groups["mode"].Value);
                        var modeChanged = ActiveProfile.SessionMode != sessionMode;
                        if (modeChanged)
                        {
                            // Let consumers revoke status derived from the old profile
                            // before any live identity field changes.
                            var nextProfile = ActiveProfile.Snapshot();
                            nextProfile.Id = "";
                            nextProfile.AccountId = "";
                            nextProfile.SessionMode = sessionMode;
                            nextProfile.Type = ResolveProfileType(sessionMode);
                            ProfileChanging?.Invoke(
                                this,
                                new(
                                    ProfileTransitionKind.SessionMode,
                                    ActiveProfile,
                                    nextProfile));
                            // The mode record arrives before EFT identifies the matching profile.
                            // Do not carry the previous mode's identity through that transition.
                            ActiveProfile.Id = "";
                            ActiveProfile.AccountId = "";
                        }
                        ActiveProfile.SessionMode = sessionMode;
                        ActiveProfile.Type = ResolveProfileType(sessionMode);
                        if (raidInfo.StartedTime == null || raidInfo.EndedTime != null)
                        {
                            raidInfo.Profile = ActiveProfile.Snapshot();
                        }
                        if (modeChanged && !e.InitialRead)
                        {
                            ProfileChanged?.Invoke(this, new(ActiveProfile));
                        }
                        continue;
                    }
                    // Profile selection messages have changed names across EFT versions.
                    // The word boundary prevents CompleteSelectedProfile from being
                    // accidentally suffix-matched as SelectedProfile. It is listed
                    // explicitly because it can also be a valid standalone identity
                    // or post-raid completion marker.
                    if (Regex.IsMatch(eventLine, ProfileSelectionMarkerPattern))
                    {
                        var profileIdMatch = Regex.Match(eventLine, ProfileSelectionPattern);
                        if (!profileIdMatch.Success)
                        {
                            continue;
                        }
                        var selectedProfileId = profileIdMatch.Groups["profileId"].Value;
                        var selectedAccountId = profileIdMatch.Groups["accountId"].Value;
                        var profileIdentityChanged = ActiveProfile.Id != selectedProfileId
                            || ActiveProfile.AccountId != selectedAccountId;
                        var completedRaidProfile = raidInfo.Profile.Snapshot();
                        var raidWasActive = raidInfo.StartedTime != null && raidInfo.EndedTime == null;
                        if (!e.InitialRead && raidWasActive)
                        {
                            raidInfo.EndedTime = eventDate;
                            RaidEnded?.Invoke(this, new(raidInfo, completedRaidProfile));
                        }

                        // RaidEnded synchronously captures the completed raid's tracker
                        // progress before the old activation is revoked. Publication is
                        // still blocked before the live profile identity mutates.
                        if (profileIdentityChanged)
                        {
                            var nextProfile = ActiveProfile.Snapshot();
                            nextProfile.Id = selectedProfileId;
                            nextProfile.AccountId = selectedAccountId;
                            ProfileChanging?.Invoke(
                                this,
                                new(
                                    ProfileTransitionKind.Identity,
                                    ActiveProfile,
                                    nextProfile));
                        }
                        ActiveProfile.Id = selectedProfileId;
                        ActiveProfile.AccountId = selectedAccountId;
                        if (!historicalReplay)
                        {
                            // A mode-only record is emitted while EFT is still showing
                            // the profile selector. Remember the mode only after EFT
                            // supplies the selected profile identity.
                            RememberSessionMode(ActiveProfile.SessionMode);
                        }
                        if (!e.InitialRead && profileIdentityChanged)
                        {
                            if (!raidWasActive)
                            {
                                System.Diagnostics.Debug.WriteLine("PROFILE CHANGED");
                            }
                            ProfileChanged?.Invoke(this, new(ActiveProfile));
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
                        GroupInviteAccept?.Invoke(this, new LogContentEventArgs<GroupLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<GroupLogContent>() ?? throw new Exception("Error parsing GroupEventArgs"), Profile = ActiveProfile });
                    }
                    if (eventLine.Contains("Got notification | GroupMatchUserLeave"))
                    {
                        // User left the group
                        GroupUserLeave?.Invoke(this, new LogContentEventArgs<GroupMatchUserLeaveLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<GroupMatchUserLeaveLogContent>() ?? throw new Exception("Error parsing GroupMatchUserLeaveEventArgs"), Profile = ActiveProfile });
                    }
					if (eventLine.Contains("Got notification | GroupMatchWasRemoved"))
                    {
                        // When the group is disbanded
                        GroupDisbanded?.Invoke(this, new());
                    }
                    if (eventLine.Contains("Got notification | GroupMatchRaidSettings"))
                    {
                        // Occurs when group leader invites members to be ready
                        GroupRaidSettings?.Invoke(this, new LogContentEventArgs<GroupRaidSettingsLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<GroupRaidSettingsLogContent>() ?? throw new Exception("Error parsing GroupRaidSettingsEventArgs"), Profile = ActiveProfile });
                    }
                    if (eventLine.Contains("Got notification | GroupMatchRaidReady"))
                    {
                        // Occurs for each other member of the group when ready
                        GroupMemberReady?.Invoke(this, new LogContentEventArgs<GroupMatchRaidReadyLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<GroupMatchRaidReadyLogContent>() ?? throw new Exception("Error parsing GroupMatchRaidReadyEventArgs"), Profile = ActiveProfile });
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
                            Profile = ActiveProfile.Snapshot(),
                        };
                        var scenePathMatch = Regex.Match(eventLine, @"scene preset path:(?<scenePath>maps\/[a-zA-Z0-9_]+\.bundle)");
                        if (scenePathMatch.Success)
                        {
                            var scenePath = scenePathMatch.Groups["scenePath"].Value;
                            var map = TarkovDev.Maps.Find(map => string.Equals(map.scenePath, scenePath, StringComparison.OrdinalIgnoreCase));
                            if (map != null)
                            {
                                raidInfo.Map = map;
                                MapLoading?.Invoke(this, new(raidInfo, ActiveProfile));
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
                        var mapUnknown = raidInfo.Map == null;
                        var mapNameId = Regex.Match(eventLine, "Location: (?<map>[^,]+)").Groups["map"].Value;
                        var networkMap = TarkovDev.Maps.Find(map => string.Equals(map.nameId, mapNameId, StringComparison.OrdinalIgnoreCase));
                        if (networkMap != null)
                        {
                            // Keep a map already resolved from the scene path when EFT sends an
                            // unknown identifier. Seasonal currently emits mixed-case identifiers.
                            raidInfo.Map = networkMap;
                        }
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
                            MatchFound?.Invoke(this, new(raidInfo, ActiveProfile));
                        }
                        if (mapUnknown)
                        {
                            MapLoading?.Invoke(this, new(raidInfo, ActiveProfile));
                        }
                        MapLoaded?.Invoke(this, new(raidInfo, ActiveProfile));
                    }
                    if (eventLine.Contains("application|GameStarting"))
                    {
                        // GameStarting always happens for PMCs and sometimes happens for scavs.
                        // For PMCs, it corresponds with the start of the countdown timer.
                        if (!raidInfo.Reconnected)
                        {
                            raidInfo.StartingTime = eventDate;
                        }
                        RaidStarting?.Invoke(this, new(raidInfo, ActiveProfile));
                    }
                    if (eventLine.Contains("application|GameStarted"))
                    {
                        // Raid begins, either at the end of the countdown for PMC, or immediately as a scav
                        if (!raidInfo.Reconnected)
                        {
                            raidInfo.StartedTime = eventDate;
                        }
                        RaidStarted?.Invoke(this, new(raidInfo, ActiveProfile));
                        //raidInfo = new();
                    }
                    if (eventLine.Contains("application|Network game matching aborted") || eventLine.Contains("application|Network game matching cancelled"))
                    {
                        // User cancelled matching
                        MatchingAborted?.Invoke(this, new(raidInfo, ActiveProfile));
                        raidInfo = new()
                        {
                            Profile = ActiveProfile.Snapshot(),
                        };
                    }
                    if (eventLine.Contains("Got notification | UserMatchOver"))
                    {
                        var exitedRaidId = jsonNode?["shortId"]?.ToString();
                        var currentRaidHasId = !string.IsNullOrWhiteSpace(raidInfo.RaidId);
                        var exitHasId = !string.IsNullOrWhiteSpace(exitedRaidId);
                        var isCurrentRaid = currentRaidHasId
                            ? exitHasId && string.Equals(raidInfo.RaidId, exitedRaidId, StringComparison.OrdinalIgnoreCase)
                            : !exitHasId;

                        // EFT also emits UserMatchOver when a Seasonal queue is cancelled before
                        // NetworkGameCreate/GameStarted. That is not a raid exit.
                        if (raidInfo.StartedTime == null || !isCurrentRaid)
                        {
                            continue;
                        }

                        RaidExited?.Invoke(this, new RaidExitedEventArgs { Map = jsonNode?["location"]?.ToString() ?? throw new Exception("Error parsing raid location"), RaidId = exitedRaidId });
                        raidInfo = new()
                        {
                            Profile = ActiveProfile.Snapshot(),
                        };
                    }
                    if (eventLine.Contains("application|Init: pstrGameVersion: "))
                    {
                        if (raidInfo.EndedTime != null)
                        {
                            ExitedPostRaidMenus?.Invoke(this, new(raidInfo, ActiveProfile));
                            raidInfo = new()
                            {
                                Profile = ActiveProfile.Snapshot(),
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
								FleaSold?.Invoke(this, new LogContentEventArgs<FleaSoldMessageLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<FleaSoldMessageLogContent>() ?? throw new Exception("Error parsing FleaSoldMessageLogContent"), Profile = ActiveProfile });
								continue;
							}
							if (systemMessageEvent.message.templateId == "5bdabfe486f7743e1665df6e 0")
							{
								FleaOfferExpired?.Invoke(this, new LogContentEventArgs<FleaExpiredMessageLogContent>() { LogContent = jsonNode?.AsObject().Deserialize<FleaExpiredMessageLogContent>() ?? throw new Exception("Error parsing FleaExpiredMessageLogContent"), Profile = ActiveProfile });
								continue;
							}
						}
                        if (systemMessageEvent.message.type >= MessageType.TaskStarted && systemMessageEvent.message.type <= MessageType.TaskFinished)
                        {
                            var args = jsonNode?.AsObject().Deserialize<TaskStatusMessageLogContent>() ?? throw new Exception("Error parsing TaskStatusMessageLogContent");
                            TaskModified?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = ActiveProfile });
                            if (args.Status == TaskStatus.Started)
                            {
                                TaskStarted?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = ActiveProfile });
                            }
                            if (args.Status == TaskStatus.Failed)
                            {
                                TaskFailed?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = ActiveProfile });
                            }
                            if (args.Status == TaskStatus.Finished)
                            {
                                TaskFinished?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = ActiveProfile });
                            }
                        }
                    }
                    }
                    catch (Exception ex)
                    {
                        ReportLogParsingFailure(ex, e.Type, parsingStage);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportLogParsingFailure(ex, e.Type, LogParsingStage.Batch);
            }
        }

        private static void RememberSessionMode(EftSessionMode sessionMode)
        {
            if (sessionMode == EftSessionMode.Unknown)
            {
                return;
            }
            var persistedMode = sessionMode.ToString();
            LastDetectedSessionMode = persistedMode;
            if (Properties.Settings.Default.lastTarkovSessionMode == persistedMode)
            {
                return;
            }
            Properties.Settings.Default.lastTarkovSessionMode = persistedMode;
            Properties.Settings.Default.Save();
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
            profiles = profiles.OrderBy(profile => profile.Date).ToList();
            for (var i = 0; i < profiles.Count; i++)
            {
                var logProfile = profiles[i];
                if (logProfile.Profile.Id != target.Profile.Id
                    || logProfile.Profile.SessionMode != target.Profile.SessionMode)
                {
                    continue;
                }
                var endDate = DateTime.Now.AddYears(1);
                if (profiles.Count > 1 && i + 1 < profiles.Count)
                {
                    endDate = profiles[i + 1].Date;
                }
                var startDate = logProfile.Date > target.Date ? logProfile.Date : target.Date;
                if (endDate <= startDate)
                {
                    continue;
                }
                parsingProfile = logProfile.Profile.Snapshot();
                var logFiles = Directory.GetFiles(logProfile.Folder);
                var replayEntries = new List<(DateTime Date, string Data)>();
                foreach (string logFile in logFiles)
                {
                    if (!logFile.Contains("notifications.log") && !logFile.Contains("notifications_000.log"))
                    {
                        continue;
                    }

                    // Read the file into memory using UTF-8 encoding
                    using var fileStream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var textReader = new StreamReader(fileStream, Encoding.UTF8);
                    var fileContents = textReader.ReadToEnd();

                    var logMessages = Regex.Matches(fileContents, logPattern, RegexOptions.Multiline);

                    foreach (Match match in logMessages)
                    {
                        var dateTimeString = match.Groups["date"].Value + " " + match.Groups["time"].Value;
                        DateTime logMessageDate = DateTime.ParseExact(dateTimeString, "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

                        if (logMessageDate < startDate || logMessageDate >= endDate)
                        {
                            continue;
                        }

                        replayEntries.Add((logMessageDate, match.Value));
                    }
                }
                foreach (var replayEntry in replayEntries.OrderBy(entry => entry.Date))
                {
                    GameWatcher_NewLogData(this, new NewLogDataEventArgs
                    {
                        Type = GameLogType.Notifications,
                        Data = replayEntry.Data,
                    });
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
                if (file.EndsWith("application.log") || file.EndsWith("application_000.log"))
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
            var matches = Regex.Matches(applicationLog, @$"{logPatternPrefix}(?<version>\d+\.\d+\.\d+\.\d+)\.\d+\|(?<logLevel>[^|]+)\|(?<logType>[^|]+)\|(?:Select(?:ed)?Profile|PrepareSelectedProfileLocally|CompleteSelectedProfile) ProfileId:(?<profileId>[a-f0-9]+) AccountId:(?<accountId>\d+)", RegexOptions.Multiline);
            if (matches.Count == 0)
            {
                return logDetails;
            }
            var profileTypeMatches = Regex.Matches(applicationLog, @$"{logPatternPrefix}(?<version>\d+\.\d+\.\d+\.\d+)\.\d+\|(?<logLevel>[^|]+)\|(?<logType>[^|]+)\|Session mode: (?<profileType>\w+)", RegexOptions.Multiline);
            for (var i = 0; i < matches.Count; i++)
            {
                Match match = matches[i];
                var dateTimeString = match.Groups["date"].Value + " " + match.Groups["time"].Value;
                DateTime profileDate = DateTime.ParseExact(dateTimeString, "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                ProfileType profileType = ProfileType.Unknown;
                EftSessionMode sessionMode = EftSessionMode.Unknown;
                Match? sessionModeMatch = null;
                foreach (Match candidate in profileTypeMatches)
                {
                    var candidateDateTimeString = candidate.Groups["date"].Value + " " + candidate.Groups["time"].Value;
                    if (!DateTime.TryParseExact(candidateDateTimeString, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var candidateDate))
                    {
                        continue;
                    }
                    if (candidateDate > profileDate)
                    {
                        break;
                    }
                    sessionModeMatch = candidate;
                }
                if (sessionModeMatch != null)
                {
                    sessionMode = ResolveSessionMode(sessionModeMatch.Groups["profileType"].Value);
                    profileType = ResolveProfileType(sessionMode);
                }
                logDetails.Add(new LogDetails()
                {
                    Profile = new()
                    {
                        Id = match.Groups["profileId"].Value,
                        Type = profileType,
                        SessionMode = sessionMode,
                        AccountId = match.Groups["accountId"].Value,
                    },
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
                    var matchingBreakpoint = breakpoints.Where((bp) => bp.Version == breakpoint.Version
                        && bp.Profile.Id == breakpoint.Profile.Id
                        && bp.Profile.SessionMode == breakpoint.Profile.SessionMode).FirstOrDefault();
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
            if (!historicalReplay)
            {
                throw new InvalidOperationException("Past logs must be processed by an isolated historical watcher.");
            }
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
                if (!details.Any(d => d.Profile.Id == breakpoint.Profile.Id
                    && d.Profile.SessionMode == breakpoint.Profile.SessionMode))
                {
                    continue;
                }
                if (!details.Any(d => d.Date >= breakpoint.Date)
                    && details.Max(d => d.Date) < breakpoint.Date)
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
                var wasGameRunning = observedGameRunning;
                if (process != null)
                {
                    if (!process.HasExited)
                    {
                        observedGameRunning = true;
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
                    observedGameRunning = false;
                    if (wasGameRunning)
                    {
                        GameStopped?.Invoke(this, EventArgs.Empty);
                    }
                    return;
                }
                process = processes.First();
                observedGameRunning = true;
                if (!wasGameRunning)
                {
                    GameStarted?.Invoke(this, EventArgs.Empty);
                }

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
            List<string> monitoringLogs = new() { "notifications.log", "application.log", "output.log", "notifications_000.log", "application_000.log", "output_000.log" };
            var files = System.IO.Directory.GetFiles(folderPath)
                .Where(file => monitoringLogs.Any(logType => file.Contains(logType, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var monitorsStarted = files.Length;
            var monitorsCompletedInitialRead = 0;

            if (InitialLogsRead)
            {
                foreach (var file in files)
                {
                    StartNewMonitor(file);
                }
                return;
            }

            if (monitorsStarted == 0)
            {
                InitialLogsRead = true;
                InitialReadComplete?.Invoke(this, new(CurrentProfile));
                return;
            }

            void MonitorInitialReadCompleted()
            {
                if (Interlocked.Increment(ref monitorsCompletedInitialRead) == monitorsStarted)
                {
                    InitialLogsRead = true;
                    InitialReadComplete?.Invoke(this, new(CurrentProfile));
                }
            }

            foreach (var file in files)
            {
                EventHandler? initialReadHandler = null;
                initialReadHandler = (object? sender, EventArgs e) =>
                {
                    if (sender is LogMonitor completedMonitor)
                    {
                        completedMonitor.InitialReadComplete -= initialReadHandler;
                    }
                    MonitorInitialReadCompleted();
                };

                var monitor = StartNewMonitor(file, initialReadHandler);
                if (monitor == null)
                {
                    MonitorInitialReadCompleted();
                }
            }
        }

        private LogMonitor? StartNewMonitor(string path, EventHandler? initialReadComplete = null)
        {
            GameLogType? newType = null;
            if (path.Contains("application.log") || path.Contains("application_000.log"))
            {
                newType = GameLogType.Application;
                ProfileChanging?.Invoke(
                    this,
                    new(
                        ProfileTransitionKind.SessionReset,
                        CurrentProfile,
                        new Profile()));
                CurrentProfile = new();
            }
            if (path.Contains("notifications.log") || path.Contains("notifications_000.log"))
            {
                newType = GameLogType.Notifications;
            }
            if (path.Contains("output.log") || path.Contains("output_000.log"))
            {
                newType = GameLogType.Output;
                outputLogTail = "";
            }
            if (path.Contains("traces.log") || path.Contains("traces_000.log"))
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
            if (initialReadComplete != null)
            {
                newMon.InitialReadComplete += initialReadComplete;
            }
            Monitors[(GameLogType)newType] = newMon;
            _ = newMon.Start();
            return newMon;
        }
	}
	public enum GameLogType
	{
		Application,
		Notifications,
		Output,
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
        public TarkovDev.Map Map { get; set; }
        public string RaidId { get; set; }
        public bool Online { get; set; }
        public float MapLoadTime { get; set; }
        public float QueueTime { get; set; }
        public bool Reconnected { get; set; }
        public Profile Profile { get; set; }
        public RaidType RaidType { 
            get
            {
                if (this.Profile.SessionMode == EftSessionMode.PVE)
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
        public RaidInfo()
        {
            Map = null;
            Online = false;
            RaidId = "";
            MapLoadTime = 0;
            QueueTime = 0;
            Reconnected = false;
            Profile = new();
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
            Profile = profile.Snapshot();
        }
    }
	public class ExceptionEventArgs : EventArgs
	{
		public Exception Exception { get; set; }
        public string Context { get; set; }
        public string? UserMessage { get; set; }
		public ExceptionEventArgs(Exception ex, string context, string? userMessage = null)
		{
			this.Exception = ex;
            Context = context;
            UserMessage = userMessage;
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
        Unknown,
        PVE,
        Regular,
        PvpSeason,
    }

    public enum EftSessionMode
    {
        Unknown,
        PVE,
        Regular,
        Seasonal,
    }

    public enum ProfileTransitionKind
    {
        SessionMode,
        Identity,
        SessionReset,
    }

    public static class ProfileTypeExtensions
    {
        public static string ToApiString(this ProfileType profileType) => profileType switch
        {
            ProfileType.PvpSeason => "pvp-season",
            _ => profileType.ToString().ToLower(),
        };
    }

    public class Profile
    {
        public string Id { get; set; } = "";
        public ProfileType Type { get; set; } = ProfileType.Unknown;
        public EftSessionMode SessionMode { get; set; } = EftSessionMode.Unknown;
        public string AccountId { get; set; } = "";
        public string DisplayName => SessionMode switch
        {
            EftSessionMode.PVE => "PVE",
            EftSessionMode.Regular => "Regular (PVP)",
            EftSessionMode.Seasonal => "Seasonal",
            _ => "Unknown",
        };

        public bool SupportsTarkovDevWrites => SessionMode is EftSessionMode.PVE or EftSessionMode.Regular;
        public bool SupportsTarkovTrackerWrites => SessionMode is EftSessionMode.PVE or EftSessionMode.Regular;
        public bool SupportsScavCooldown => SessionMode is EftSessionMode.PVE or EftSessionMode.Regular;
        public bool HasIdentity => !string.IsNullOrWhiteSpace(AccountId);
        public bool HasTarkovDevPlayerRoute => HasIdentity
            && SessionMode is EftSessionMode.PVE or EftSessionMode.Regular;
        // Keep service routing separate from the raw EFT session identity. Seasonal
        // reads now use tarkov.dev's explicit pvp-season route, while tracker writes
        // remain disabled until the live EFT/TarkovTracker release gate is cleared.
        public ProfileType TarkovDevDataType => SessionMode switch
        {
            EftSessionMode.PVE => ProfileType.PVE,
            EftSessionMode.Seasonal => ProfileType.PvpSeason,
            _ => ProfileType.Regular,
        };

        public Profile Snapshot()
        {
            return new Profile
            {
                Id = Id,
                Type = Type,
                SessionMode = SessionMode,
                AccountId = AccountId,
            };
        }
    }

    public class ProfileEventArgs : EventArgs
    {
        public Profile Profile { get; set; }
        public ProfileEventArgs(Profile profile)
        {
            Profile = profile.Snapshot();
        }
    }

    public class ProfileChangingEventArgs : EventArgs
    {
        public ProfileTransitionKind Kind { get; }
        public Profile PreviousProfile { get; }
        public Profile NextProfile { get; }

        public ProfileChangingEventArgs(
            ProfileTransitionKind kind,
            Profile previousProfile,
            Profile nextProfile)
        {
            Kind = kind;
            PreviousProfile = previousProfile.Snapshot();
            NextProfile = nextProfile.Snapshot();
        }
    }

    public class LogContentEventArgs<T> : EventArgs where T : JsonLogContent
    {
        public T LogContent { get; set; }
        private Profile profile = new();
        public Profile Profile
        {
            get => profile;
            set => profile = value.Snapshot();
        }

    }

    public class ControlSettingsEventArgs : EventArgs
    {
        public JsonNode ControlSettings { get; set; }
    }
}
