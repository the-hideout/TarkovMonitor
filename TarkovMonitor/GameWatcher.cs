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
        private const string GameStoppingMarker = "EFT.NetworkGame`1:GameStopping()";
        private string outputLogTail = "";
        private readonly System.Timers.Timer processTimer;
        private readonly FileSystemWatcher logFileCreateWatcher;
        private readonly FileSystemWatcher screenshotWatcher;
        private string _logsPath = "";
        private bool _logsPathResolutionFailed;
        private readonly object initialReadGate = new();
        private readonly object monitorsGate = new();
        private readonly HashSet<LogMonitor> pendingInitialReads = new();
        private bool applicationInitialReadComplete;
        private bool logWatcherRecoveryInProgress;
        private readonly HashSet<string> reportedSessionModeFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly object reportedSessionModeFailuresLock = new();
        private const string SteamEftAppId = "3932890";
        private static readonly string[] EftUninstallRegistryPaths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 3932890",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 3932890",
        };
        public static Profile CurrentProfile { get; set; } = new();
        public static bool ReadingPastLogs = false;
        public bool InitialLogsRead { get; private set; } = false;
        public string LogsPath { 
            get
            {
                if (!string.IsNullOrWhiteSpace(_logsPath))
                {
                    return _logsPath;
                }
                if (!string.IsNullOrWhiteSpace(Properties.Settings.Default.customLogsPath))
                {
                    _logsPathResolutionFailed = false;
                    _logsPath = Properties.Settings.Default.customLogsPath.Trim();
                    return _logsPath;
                }
                if (_logsPathResolutionFailed)
                {
                    return "";
                }
                try
                {
                    _logsPath = GetDefaultLogsFolder();
                    _logsPathResolutionFailed = false;
                }
                catch (Exception ex)
                {
                    _logsPathResolutionFailed = true;
                    ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "getting logs path"));
                }
                return _logsPath;
            }
            set
            {
                _logsPath = value?.Trim() ?? "";
                _logsPathResolutionFailed = false;
                if (!logFileCreateWatcher.EnableRaisingEvents)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(_logsPath))
                {
                    logFileCreateWatcher.EnableRaisingEvents = false;
                    ResetLogMonitoring();
                    var defaultLogsPath = LogsPath;
                    if (_logsPathResolutionFailed || string.IsNullOrWhiteSpace(defaultLogsPath))
                    {
                        return;
                    }
                    try
                    {
                        ConfigureLogWatcher(defaultLogsPath);
                    }
                    catch (Exception ex)
                    {
                        ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "updating game watcher logs path"));
                    }
                    return;
                }

                if (!Directory.Exists(_logsPath))
                {
                    logFileCreateWatcher.EnableRaisingEvents = false;
                    ResetLogMonitoring();
                    ExceptionThrown?.Invoke(this, new ExceptionEventArgs(
                        new DirectoryNotFoundException($"The configured EFT logs folder does not exist: {_logsPath}"),
                        "updating game watcher logs path"));
                    return;
                }

                try
                {
                    ConfigureLogWatcher(_logsPath);
                }
                catch (Exception ex)
                {
                    ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "updating game watcher logs path"));
                }

            }
        }
        public string CurrentLogsFolder {
            get
            {
                LogMonitor? monitor;
                lock (monitorsGate)
                {
                    if (!Monitors.TryGetValue(GameLogType.Application, out monitor))
                    {
                        return "";
                    }
                }
                try
                {
                    var logInfo = new FileInfo(monitor.Path);
                    return logInfo.DirectoryName ?? "";
                }
                catch { }
                return "";
                
            }
        }
        private readonly Dictionary<string, RaidInfo> Raids = new();
        private bool matchingStatusPublished;
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
        public event EventHandler<LogContentEventArgs<GroupLogContent>>? GroupInviteAccept;
        public event EventHandler<LogContentEventArgs<GroupRaidSettingsLogContent>>? GroupRaidSettings;
        public event EventHandler<LogContentEventArgs<GroupMatchRaidReadyLogContent>>? GroupMemberReady;
        public event EventHandler? GroupDisbanded;
        public event EventHandler<LogContentEventArgs<GroupMatchUserLeaveLogContent>>? GroupUserLeave;
        public event EventHandler<RaidInfoEventArgs>? MapLoading;
        public event EventHandler<RaidInfoEventArgs>? MatchingStarted;
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
        public event EventHandler<ProfileEventArgs> ProfileChanged;
        public event EventHandler<ProfileEventArgs> InitialReadComplete;
        public event EventHandler<ControlSettingsEventArgs> ControlSettings;

        private static string logPatternPrefix = @"(?<date>^\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2}\.\d{3})(?<tzoffset> [+-]\d{2}:\d{2})?\|";
        private static string logPattern = @$"{logPatternPrefix}(?<message>.+$)\s*(?<json>^{{[\s\S]+?^}})?";

        public static string GetDefaultLogsFolder()
        {
            foreach (var installPath in GetRegistryInstallLocations())
            {
                var logsPath = GetLogsFolder(installPath);
                if (logsPath != null)
                {
                    return logsPath;
                }
            }
            foreach (var libraryPath in GetSteamLibraries())
            {
                var installPath = GetSteamEftInstallPath(libraryPath);
                var logsPath = installPath == null ? null : GetLogsFolder(installPath);
                if (logsPath != null)
                {
                    return logsPath;
                }
            }

            throw new DirectoryNotFoundException("No Escape from Tarkov logs folder was found in the installed game locations.");
        }

        private static string? GetLogsFolder(string installPath)
        {
            if (string.IsNullOrWhiteSpace(installPath))
            {
                return null;
            }

            foreach (var logsPath in new[]
            {
                Path.Combine(installPath, "Logs"),
                Path.Combine(installPath, "build", "Logs")
            })
            {
                if (Directory.Exists(logsPath))
                {
                    return Path.GetFullPath(logsPath);
                }
            }

            return null;
        }

        private static IEnumerable<string> GetRegistryInstallLocations()
        {
            var installLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            var views = new[] { RegistryView.Default, RegistryView.Registry32, RegistryView.Registry64 };

            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var registryPath in EftUninstallRegistryPaths)
                    {
                        using var uninstallKey = baseKey.OpenSubKey(registryPath);
                        var installPath = uninstallKey?.GetValue("InstallLocation")?.ToString();
                        if (!string.IsNullOrWhiteSpace(installPath))
                        {
                            installLocations.Add(installPath);
                        }
                    }
                }
            }

            return installLocations;
        }

        private static IEnumerable<string> GetSteamInstallRoots()
        {
            var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
            var views = new[] { RegistryView.Default, RegistryView.Registry32, RegistryView.Registry64 };

            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var steamKey = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
                    foreach (var valueName in new[] { "SteamPath", "InstallPath" })
                    {
                        var steamPath = steamKey?.GetValue(valueName)?.ToString();
                        if (!string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath))
                        {
                            steamRoots.Add(Path.GetFullPath(steamPath));
                        }
                    }
                }
            }

            return steamRoots;
        }

        private static IEnumerable<string> GetSteamLibraries()
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var steamRoot in GetSteamInstallRoots())
            {
                libraries.Add(steamRoot);
                var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libraryFoldersPath))
                {
                    continue;
                }

                try
                {
                    var contents = File.ReadAllText(libraryFoldersPath);
                    var matches = Regex.Matches(contents, @"""path""\s+""(?<path>[^""]+)""", RegexOptions.IgnoreCase);
                    foreach (Match match in matches)
                    {
                        var libraryPath = match.Groups["path"].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(libraryPath))
                        {
                            libraries.Add(Path.GetFullPath(libraryPath));
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return libraries;
        }

        private static string? GetSteamEftInstallPath(string libraryPath)
        {
            var candidates = new List<string>();
            var manifestPath = Path.Combine(libraryPath, "steamapps", $"appmanifest_{SteamEftAppId}.acf");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var contents = File.ReadAllText(manifestPath);
                    var match = Regex.Match(contents, @"""installdir""\s+""(?<directory>[^""]+)""", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        candidates.Add(Path.Combine(libraryPath, "steamapps", "common", match.Groups["directory"].Value));
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            candidates.Add(Path.Combine(libraryPath, "steamapps", "common", "Escape from Tarkov"));
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

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

        public bool Start()
        {
			try
			{
                var logsPath = LogsPath;
                if (_logsPathResolutionFailed || string.IsNullOrWhiteSpace(logsPath))
                {
                    return false;
                }
                if (!Directory.Exists(logsPath))
                {
                    logFileCreateWatcher.EnableRaisingEvents = false;
                    processTimer.Enabled = false;
                    ResetLogMonitoring();
                    ExceptionThrown?.Invoke(this, new ExceptionEventArgs(
                        new DirectoryNotFoundException($"The configured EFT logs folder does not exist: {logsPath}"),
                        "starting game watcher"));
                    return false;
                }

				ConfigureLogWatcher(logsPath);
				processTimer.Elapsed -= ProcessTimer_Elapsed;
				processTimer.Elapsed += ProcessTimer_Elapsed;
				UpdateProcess();
				SetupScreenshotWatcher();
				processTimer.Enabled = true;
				return true;
			}
			catch (Exception ex)
			{
				logFileCreateWatcher.EnableRaisingEvents = false;
				processTimer.Enabled = false;
				ResetLogMonitoring();
				ExceptionThrown?.Invoke(this, new(ex, "starting game watcher"));
				return false;
			}
        }

        private void ConfigureLogWatcher(string logsPath)
        {
            if (!Directory.Exists(logsPath))
            {
                throw new DirectoryNotFoundException($"The configured EFT logs folder does not exist: {logsPath}");
            }

            ResetLogMonitoring();
            logFileCreateWatcher.EnableRaisingEvents = false;
            logFileCreateWatcher.Path = logsPath;
            logFileCreateWatcher.Created -= LogFileCreateWatcher_Created;
            logFileCreateWatcher.Created += LogFileCreateWatcher_Created;
            logFileCreateWatcher.Error -= LogFileCreateWatcher_Error;
            logFileCreateWatcher.Error += LogFileCreateWatcher_Error;
            logFileCreateWatcher.EnableRaisingEvents = true;

            var latestLogFolder = GetLatestLogFolder();
            if (!string.IsNullOrWhiteSpace(latestLogFolder))
            {
                WatchLogsFolder(latestLogFolder);
            }
        }

        private void LogFileCreateWatcher_Error(object? sender, ErrorEventArgs e)
        {
            var exception = e.GetException() ?? new IOException("The EFT logs watcher reported an unspecified error.");
            ExceptionThrown?.Invoke(this, new ExceptionEventArgs(exception, "watching EFT logs folder"));

            if (!Directory.Exists(_logsPath))
            {
                logFileCreateWatcher.EnableRaisingEvents = false;
                ResetLogMonitoring();
                return;
            }

            RecoverLogWatcher();
        }

        private void RecoverLogWatcher()
        {
            lock (initialReadGate)
            {
                if (logWatcherRecoveryInProgress)
                {
                    return;
                }
                logWatcherRecoveryInProgress = true;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_logsPath) || !Directory.Exists(_logsPath))
                {
                    return;
                }

                ConfigureLogWatcher(_logsPath);
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "recovering EFT logs watcher"));
            }
            finally
            {
                lock (initialReadGate)
                {
                    logWatcherRecoveryInProgress = false;
                }
            }
        }

        private void LogFileCreateWatcher_Created(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (GetLogType(e.FullPath) != null)
                {
                    StartNewMonitor(e.FullPath);
                }
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, "starting EFT log monitor"));
            }
        }

        private void ReportUnsupportedSessionMode(string rawSessionMode)
        {
            lock (reportedSessionModeFailuresLock)
            {
                if (!reportedSessionModeFailures.Add(rawSessionMode))
                {
                    return;
                }
            }

            var exception = new System.IO.InvalidDataException($"EFT reported an unsupported session mode '{rawSessionMode}'.");
            ExceptionThrown?.Invoke(this, new ExceptionEventArgs(exception, "parsing session mode"));
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
                    var eventDate = new DateTime();
                    DateTime.TryParseExact(logMessage.Groups["date"].Value + " " + logMessage.Groups["time"].Value.Split(" ")[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out eventDate);
                    var eventLine = logMessage.Groups["message"].Value;
                    //System.Diagnostics.Debug.WriteLine(eventLine);
                    if (eventLine.Contains("Session mode:", StringComparison.Ordinal))
                    {
                        var modeMatch = Regex.Match(eventLine, @"Session mode:\s*(?<mode>[^\s|]+)");
                        var rawSessionMode = modeMatch.Success
                            ? modeMatch.Groups["mode"].Value
                            : "(missing)";
                        var transition = ProfileIdentity.ApplyMode(CurrentProfile, rawSessionMode);
                        raidInfo.Profile = CurrentProfile.Snapshot();
                        if (!transition.Recognized)
                        {
                            ReportUnsupportedSessionMode(rawSessionMode);
                        }
                        if (!e.InitialRead && transition.Changed)
                        {
                            ProfileChanged?.Invoke(this, new(CurrentProfile.Snapshot()));
                        }
                        continue;
                    }
                    // Profile selection messages have changed names across EFT versions.
                    if (eventLine.Contains("SelectProfile ProfileId:")
                        || eventLine.Contains("SelectedProfile ProfileId:")
                        || eventLine.Contains("PrepareSelectedProfileLocally ProfileId:"))
                    {
                        var profileIdMatch = Regex.Match(eventLine, @"(?:Select(?:ed)?Profile|PrepareSelectedProfileLocally) ProfileId:(?<profileId>\w+) AccountId:(?<accountId>\d+)");
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
                            System.Diagnostics.Debug.WriteLine("PROFILE CHANGED");
                            ProfileChanged?.Invoke(this, new(CurrentProfile.Snapshot()));
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
                            Profile = CurrentProfile,
                        };
                        matchingStatusPublished = false;
                        var scenePathMatch = Regex.Match(eventLine, @"scene preset path:(?<scenePath>maps\/[a-zA-Z0-9_]+\.bundle)");
                        if (scenePathMatch.Success)
                        {
                            var scenePath = scenePathMatch.Groups["scenePath"].Value;
                            var map = TarkovDev.Maps.Find((map) => map.scenePath == scenePath);
                            if (map != null)
                            {
                                raidInfo.Map = map;
                                MapLoading?.Invoke(this, new(raidInfo, CurrentProfile));
                            }
                        }
                    }
                    if (eventLine.Contains("application|LocationLoaded"))
                    {
                        // The map has been loaded and the game is searching for a match
                        raidInfo.MapLoadTime = float.Parse(Regex.Match(eventLine, @"LocationLoaded:[0-9.,]+ real:(?<loadTime>[0-9.,]+)").Groups["loadTime"].Value.Replace(",", "."), CultureInfo.InvariantCulture);
                        PublishMatchingStarted(e.InitialRead);
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
                        raidInfo.Map = TarkovDev.Maps.Find(map => map.nameId == mapNameId);
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
                            PublishMatchingStarted(e.InitialRead, allowCompletedFallback: true);
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
                        matchingStatusPublished = false;
                        raidInfo = new()
                        {
                            Profile = CurrentProfile,
                        };
                    }
                    if (eventLine.Contains("Got notification | UserMatchOver"))
                    {
                        RaidExited?.Invoke(this, new RaidExitedEventArgs { Map = jsonNode?["location"]?.ToString() ?? throw new Exception("Error parsing raid location"), RaidId = jsonNode?["shortId"]?.ToString() });
                        raidInfo = new()
                        {
                            Profile = CurrentProfile,
                        };
                    }
                    if (eventLine.Contains("application|Init: pstrGameVersion: "))
                    {
                        if (raidInfo.EndedTime != null)
                        {
                            ExitedPostRaidMenus?.Invoke(this, new(raidInfo, CurrentProfile));
                            raidInfo = new()
                            {
                                Profile = CurrentProfile,
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
                            var eventProfile = CurrentProfile.Snapshot();
                            TaskModified?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = eventProfile });
                            if (args.Status == TaskStatus.Started)
                            {
                                TaskStarted?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = eventProfile });
                            }
                            if (args.Status == TaskStatus.Failed)
                            {
                                TaskFailed?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = eventProfile });
                            }
                            if (args.Status == TaskStatus.Finished)
                            {
                                TaskFinished?.Invoke(this, new LogContentEventArgs<TaskStatusMessageLogContent>() { LogContent = args, Profile = eventProfile });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(this, new ExceptionEventArgs(ex, $"parsing {e.Type} log data"));
            }
        }

        private void ProcessTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            UpdateProcess();
        }

        public Dictionary<DateTime, string> GetLogFolders()
        {
			Dictionary<DateTime, string> folderDictionary = new();
            if (string.IsNullOrWhiteSpace(LogsPath) || !Directory.Exists(LogsPath))
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

        public List<LogDetails> GetLogDetails(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                return new();
            }
            return TaskLifecycle.GetLogDetails(ReadLifecycleSources(new[] { folderPath }, applicationOnly: true));
        }

        public List<LogDetails> GetLogBreakpoints(string profileId, ProfileType? profileType = null)
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
                    if (breakpoint.Profile.Id != profileId
                        || breakpoint.Profile.Type == ProfileType.Unknown
                        || (profileType != null && breakpoint.Profile.Type != profileType))
                    {
                        continue;
                    }
                    var matchingBreakpoint = breakpoints.FirstOrDefault(bp =>
                        bp.Version == breakpoint.Version
                        && bp.Profile.Id == breakpoint.Profile.Id
                        && bp.Profile.Type == breakpoint.Profile.Type);
                    if (matchingBreakpoint == null)
                    {
                        breakpoints.Add(breakpoint);
                    }
                }
            }
            return breakpoints;
        }

        public TaskLifecycleReplayResult ProcessLogsFromBreakpoint(LogDetails breakpoint)
        {
            if (string.IsNullOrWhiteSpace(LogsPath) || !Directory.Exists(LogsPath))
            {
                return new TaskLifecycleReplayResult();
            }
            return TaskLifecycle.Replay(
                ReadLifecycleSources(Directory.GetDirectories(LogsPath), applicationOnly: false),
                TaskLifecycle.ToLocalOffset(breakpoint.Date));
        }

        private static List<TaskLifecycleLogSource> ReadLifecycleSources(
            IEnumerable<string> folders,
            bool applicationOnly)
        {
            var sources = new List<TaskLifecycleLogSource>();
            foreach (var folder in folders.OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var path in Directory.GetFiles(folder, "*.log").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var filename = Path.GetFileName(path);
                    var isApplication = filename.StartsWith("application", StringComparison.OrdinalIgnoreCase);
                    var isNotifications = filename.StartsWith("notifications", StringComparison.OrdinalIgnoreCase);
                    if (!isApplication && (applicationOnly || !isNotifications))
                    {
                        continue;
                    }
                    using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var textReader = new StreamReader(fileStream, Encoding.UTF8);
                    sources.Add(new TaskLifecycleLogSource(folder, path, textReader.ReadToEnd()));
                }
            }
            return sources;
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
            if (!Directory.Exists(LogsPath))
            {
                return "";
            }

            var logFolders = System.IO.Directory.GetDirectories(LogsPath);
            if (logFolders.Length == 0)
            {
                return "";
            }

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

        private void PublishMatchingStarted(bool initialRead, bool allowCompletedFallback = false)
        {
            if (!MatchingNotificationPolicy.ShouldPublish(
                    initialRead,
                    ReadingPastLogs,
                    matchingStatusPublished,
                    raidInfo.MapLoadTime,
                    raidInfo.QueueTime,
                    raidInfo.StartingTime,
                    allowCompletedFallback))
            {
                return;
            }

            matchingStatusPublished = true;
            MatchingStarted?.Invoke(this, new(raidInfo, CurrentProfile));
        }

        private void WatchLogsFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            var files = System.IO.Directory.GetFiles(folderPath);
            foreach (var file in files)
            {
                if (GetLogType(file) != null)
                {
                    StartNewMonitor(file);
                }
            }
        }

        private LogMonitor? StartNewMonitor(string path)
        {
            var newType = GetLogType(path);
            if (newType == null)
            {
                return null;
            }

            if (newType == GameLogType.Application)
            {
                CurrentProfile = new();
                raidInfo.Profile = CurrentProfile.Snapshot();
                ProfileChanged?.Invoke(this, new(CurrentProfile.Snapshot()));
            }
            if (newType == GameLogType.Output)
            {
                outputLogTail = "";
            }
            //Debug.WriteLine($"Starting new {newType} monitor at {path}");
            var newMon = new LogMonitor(path, newType.Value);
            newMon.NewLogData += GameWatcher_NewLogData;
            newMon.Exception += (sender, e) => {
                ExceptionThrown?.Invoke(sender, e);
            };
            LogMonitor? existingMonitor;
            lock (monitorsGate)
            {
                Monitors.TryGetValue(newType.Value, out existingMonitor);
                if (existingMonitor != null)
                {
                    existingMonitor.InitialReadComplete -= LogMonitor_InitialReadComplete;
                    lock (initialReadGate)
                    {
                        pendingInitialReads.Remove(existingMonitor);
                    }
                }

                if (!InitialLogsRead)
                {
                    lock (initialReadGate)
                    {
                        pendingInitialReads.Add(newMon);
                        if (newMon.Type == GameLogType.Application)
                        {
                            applicationInitialReadComplete = false;
                        }
                    }
                    newMon.InitialReadComplete += LogMonitor_InitialReadComplete;
                }
                Monitors[newType.Value] = newMon;
                newMon.Start();
            }
            existingMonitor?.Stop();
            return newMon;
        }

        private static GameLogType? GetLogType(string path)
        {
            var filename = Path.GetFileName(path);
            if (filename.EndsWith("application.log", StringComparison.OrdinalIgnoreCase)
                || filename.EndsWith("application_000.log", StringComparison.OrdinalIgnoreCase))
            {
                return GameLogType.Application;
            }
            if (filename.EndsWith("notifications.log", StringComparison.OrdinalIgnoreCase)
                || filename.EndsWith("notifications_000.log", StringComparison.OrdinalIgnoreCase))
            {
                return GameLogType.Notifications;
            }
            if (filename.EndsWith("output.log", StringComparison.OrdinalIgnoreCase)
                || filename.EndsWith("output_000.log", StringComparison.OrdinalIgnoreCase))
            {
                return GameLogType.Output;
            }
            if (filename.EndsWith("traces.log", StringComparison.OrdinalIgnoreCase)
                || filename.EndsWith("traces_000.log", StringComparison.OrdinalIgnoreCase))
            {
                return GameLogType.Traces;
            }
            return null;
        }

        private void LogMonitor_InitialReadComplete(object? sender, EventArgs e)
        {
            if (sender is not LogMonitor monitor)
            {
                return;
            }

            var shouldPublish = false;
            lock (initialReadGate)
            {
                if (!pendingInitialReads.Remove(monitor))
                {
                    return;
                }

                if (monitor.Type == GameLogType.Application)
                {
                    applicationInitialReadComplete = true;
                }

                if (!InitialLogsRead && applicationInitialReadComplete && pendingInitialReads.Count == 0)
                {
                    InitialLogsRead = true;
                    shouldPublish = true;
                }
            }

            if (shouldPublish)
            {
                PublishMatchingStarted(false);
                InitialReadComplete?.Invoke(this, new(CurrentProfile.Snapshot()));
            }
        }

        private void ResetLogMonitoring()
        {
            List<LogMonitor> monitors;
            lock (monitorsGate)
            {
                monitors = Monitors.Values.Distinct().ToList();
                Monitors.Clear();
                lock (initialReadGate)
                {
                    pendingInitialReads.Clear();
                    applicationInitialReadComplete = false;
                    InitialLogsRead = false;
                }
            }

            foreach (var monitor in monitors)
            {
                monitor.InitialReadComplete -= LogMonitor_InitialReadComplete;
                monitor.Stop();
            }
        }
	}
	public enum GameLogType
	{
		Application,
		Notifications,
		Output,
		Traces
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
                if (this.Profile.Type == ProfileType.PVE)
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
