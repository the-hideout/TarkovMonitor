using MudBlazor.Services;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using TarkovMonitor.GroupLoadout;
using System.Globalization;
using System.ComponentModel;
using MudBlazor;
using Microsoft.Extensions.Localization;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace TarkovMonitor
{
    public partial class MainBlazorUI : Form
    {
        private const int WmNcHitTest = 0x0084;
        private const int WmNcCalcSize = 0x0083;
        private const int WmNcLButtonDown = 0x00A1;
        private const int HtCaption = 0x0002;
        private const int HtClient = 0x0001;
        private const int ResizeBorderWidth = 4;
        private const int WsThickFrame = 0x00040000;
        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;
        private const int DwmWindowCornerPreference = 33;
        private const int DwmBorderColor = 34;
        private const int DwmCaptionColor = 35;
        private const int DwmRound = 2;
        private const int TarkovBorderColor = 0x003B555F;
        private const int TarkovHeaderColor = 0x002D2F2F;

        public event EventHandler? WindowStateChanged;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style |= WsThickFrame | WsMinimizeBox | WsMaximizeBox;
                return parameters;
            }
        }

        private readonly GameWatcher eft;
        private readonly MessageLog messageLog;
        private readonly LogRepository logRepository;
        private readonly GroupManager groupManager;
        private readonly TimersManager timersManager;
        private readonly System.Timers.Timer runthroughTimer;
        private readonly System.Timers.Timer scavCooldownTimer;
        private readonly HashSet<string> reportedStatsFailures = new();
        private readonly object reportedStatsFailuresLock = new();
        private readonly SemaphoreSlim profileChangeLock = new(1, 1);
        private long profileChangeGeneration;
        private string trackerProfileId = "";
        private EftSessionMode trackerSessionMode = EftSessionMode.Unknown;
        private EftSessionMode displayedSessionMode = EftSessionMode.Unknown;
        private LocalizationService localizationService;
        private bool inRaid;

        public MainBlazorUI()
        {
            InitializeComponent();
            if (Properties.Settings.Default.upgradeRequired)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.upgradeRequired = false;
                Properties.Settings.Default.Save();
            }
            this.TopMost = Properties.Settings.Default.stayOnTop;
            inRaid = false;

            // Singleton message log used to record and display messages for TarkovMonitor
            messageLog = new MessageLog();
            messageLog.AddMessage($"TarkovMonitor v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            foreach (var storageWarning in TarkovTracker.GetStorageWarnings())
            {
                messageLog.AddMessage(storageWarning, "warning");
            }
            if (!TarkovTracker.IsLegacyService && TarkovTracker.GetPendingTokenValidations().Count > 0)
            {
                messageLog.AddMessage(
                    "Previously saved TarkovTracker API keys must be validated before they can be used.",
                    "warning",
                    "/settings#tarkov-tracker",
                    "Click here to validate saved keys.");
            }

            // Singleton log repository to record, display, and analyze logs for TarkovMonitor
            logRepository = new LogRepository();

            // Singleton Group tracker
            groupManager = new GroupManager();

			// Singleton tarkov.dev repository (to DI the results of the queries)
			//tarkovdevRepository = new TarkovDevRepository();

			eft = new GameWatcher();

            timersManager = new TimersManager(eft, messageLog);

            // Creates the dependency injection services which are the in-betweens for the Blazor interface and the rest of the C# application.
            var services = new ServiceCollection();
            services.AddWindowsFormsBlazorWebView();
            services.AddMudServices(configuration =>
            {
                configuration.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopCenter;
            });
            services.AddLocalization();
            services.AddSingleton<LocalizationService>();
            services.AddSingleton<GameWatcher>(eft);
            services.AddSingleton<MessageLog>(messageLog);
            services.AddSingleton<LogRepository>(logRepository);
            services.AddSingleton<GroupManager>(groupManager);
            services.AddSingleton<TimersManager>(timersManager);
            services.AddSingleton<MainBlazorUI>(this);

            blazorWebView1.HostPage = "wwwroot\\index.html";
            var serviceProvider = services.BuildServiceProvider();
            blazorWebView1.Services = serviceProvider;
            localizationService = serviceProvider.GetRequiredService<LocalizationService>();
            blazorWebView1.RootComponents.Add<TarkovMonitor.Blazor.App>("#app");
            //services.AddSingleton<TarkovDevRepository>(tarkovdevRepository);
            // Add event watchers
            eft.FleaSold += Eft_FleaSold;
            eft.FleaOfferExpired += Eft_FleaOfferExpired;
            eft.DebugMessage += Eft_DebugMessage;
            eft.ExceptionThrown += Eft_ExceptionThrown;
            eft.RaidStarting += Eft_RaidStarting;
            eft.RaidStarted += Eft_RaidStart;
            eft.RaidStopping += Eft_RaidStopping;
            eft.RaidExited += Eft_RaidExited;
            eft.RaidEnded += Eft_RaidEnded;
            eft.ExitedPostRaidMenus += Eft_ExitedPostRaidMenus;
            eft.TaskStarted += Eft_TaskStarted;
            eft.TaskFailed += Eft_TaskFailed;
            eft.TaskFinished += Eft_TaskFinished;
            eft.NewLogData += Eft_NewLogData;
            eft.GroupInviteAccept += Eft_GroupInviteAccept;
            eft.GroupUserLeave += Eft_GroupUserLeave;
            eft.GroupRaidSettings += Eft_GroupRaidSettings;
            eft.GroupMemberReady += Eft_GroupMemberReady;
            eft.GroupDisbanded += Eft_GroupDisbanded;
            eft.MatchingAborted += Eft_GroupStaleEvent;
            eft.GameStarted += Eft_GroupStaleEvent;
            eft.GameStarted += Eft_GameStarted;
            eft.GameStopped += Eft_GameStopped;
            eft.MapLoading += Eft_MapLoading;
            eft.MapLoading += Eft_MapLoading_NavigateToMap;
            eft.MatchFound += Eft_MatchFound;
            eft.PlayerPosition += Eft_PlayerPosition;
            eft.ProfileChanged += Eft_ProfileChanged;
            eft.ControlSettings += Eft_ControlSettings;

            eft.InitialReadComplete += Eft_InitialReadComplete;

            try
            {
                eft.Start();
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error starting game watcher: {ex.Message} {ex.StackTrace}", "exception");
            }

            Properties.Settings.Default.PropertyChanged += (object? sender, PropertyChangedEventArgs e) => {
                if (e.PropertyName == "stayOnTop")
                {
                    this.TopMost = Properties.Settings.Default.stayOnTop;
                }
                if (e.PropertyName == "customLogsPath")
                {
                    eft.LogsPath = Properties.Settings.Default.customLogsPath;
                }
            };

            TarkovTracker.ProgressRetrieved += TarkovTracker_ProgressRetrieved;

            UpdateCheck.NewVersion += UpdateCheck_NewVersion;
            UpdateCheck.Error += UpdateCheck_Error;

            SocketClient.ExceptionThrown += SocketClient_ExceptionThrown;

            UpdateCheck.CheckForNewVersion();

            blazorWebView1.WebView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;

            runthroughTimer = new System.Timers.Timer(Properties.Settings.Default.runthroughTime.TotalMilliseconds)
            {
                AutoReset = false,
                Enabled = false
            };
            runthroughTimer.Elapsed += RunthroughTimer_Elapsed;
            scavCooldownTimer = new System.Timers.Timer(TimeSpan.FromSeconds(TarkovDev.ScavCooldownSeconds()).TotalMilliseconds)
            {
                AutoReset = false,
                Enabled = false
            };
            scavCooldownTimer.Elapsed += ScavCooldownTimer_Elapsed;
        }

        public bool IsMaximized => WindowState == FormWindowState.Maximized;

        public void MinimizeWindow() => WindowState = FormWindowState.Minimized;

        public void ToggleMaximizeWindow()
        {
            WindowState = IsMaximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        public void CloseWindow() => Close();

        public void BeginWindowDrag()
        {
            if (IsMaximized)
            {
                return;
            }

            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }

        public void BeginWindowResize(int hitTest)
        {
            if (WindowState != FormWindowState.Normal || !IsResizeHit(hitTest))
            {
                return;
            }

            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)hitTest, IntPtr.Zero);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            var cornerPreference = DwmRound;
            DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));

            var borderColor = TarkovBorderColor;
            DwmSetWindowAttribute(Handle, DwmBorderColor, ref borderColor, sizeof(int));

            var captionColor = TarkovHeaderColor;
            DwmSetWindowAttribute(Handle, DwmCaptionColor, ref captionColor, sizeof(int));
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmNcCalcSize && message.WParam != IntPtr.Zero && WindowState == FormWindowState.Normal)
            {
                message.Result = IntPtr.Zero;
                return;
            }

            if (message.Msg == WmNcHitTest && WindowState == FormWindowState.Normal)
            {
                message.Result = (IntPtr)GetResizeHitTest(GetScreenPosition(message.LParam));
                return;
            }

            base.WndProc(ref message);
        }

        private static Point GetScreenPosition(IntPtr packedCoordinates)
        {
            var packedPosition = packedCoordinates.ToInt64();
            return new Point(
                unchecked((short)(packedPosition & 0xffff)),
                unchecked((short)((packedPosition >> 16) & 0xffff)));
        }

        private int GetResizeHitTest(Point screenPosition)
        {
            var cursor = PointToClient(screenPosition);
            var left = cursor.X <= ResizeBorderWidth;
            var right = cursor.X >= ClientSize.Width - ResizeBorderWidth;
            var top = cursor.Y <= ResizeBorderWidth;
            var bottom = cursor.Y >= ClientSize.Height - ResizeBorderWidth;

            return (left, right, top, bottom) switch
            {
                (true, _, true, _) => 13,
                (_, true, true, _) => 14,
                (true, _, _, true) => 16,
                (_, true, _, true) => 17,
                (true, _, _, _) => 10,
                (_, true, _, _) => 11,
                (_, _, true, _) => 12,
                (_, _, _, true) => 15,
                _ => HtClient
            };
        }

        private static bool IsResizeHit(int hitTest) => hitTest is >= 10 and <= 17;

        private void Eft_ControlSettings(object? sender, ControlSettingsEventArgs e)
        {
            try
            {
                JsonArray keyBindings = e.ControlSettings["keyBindings"].AsArray();
                JsonNode screenshotBind = keyBindings.FirstOrDefault((n) => n.AsObject()["keyName"].ToString() == "MakeScreenshot" && n.AsObject()["variants"].AsArray().Any(variant => variant.AsObject()["isAxis"]?.GetValue<bool>() == true || variant.AsObject()["keyCode"].AsArray().Count > 0));
                if (screenshotBind == null)
                {
                    messageLog.AddMessage($"Screenshot key is not bound in EFT. Using this keybind is required to update tarkov.dev map position.", "info");
                    return;
                }
                var variant = screenshotBind["variants"].AsArray().FirstOrDefault(variant => variant.AsObject()["keyCode"].AsArray().Count > 0);
                if (variant == null)
                {
                    // screenshot is bound to an axis, like mousewheel
                    return;
                }
                var keys = variant["keyCode"].AsArray().Select(n => n.GetValue<string>());
                if (keys.Any(key => key == "SysReq"))
                {
                    messageLog.AddMessage($"Screenshot key is not properly bound in EFT. Please re-bind your screenshot key in EFT for use with updating tarkov.dev map position.", "info");
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error checking screenshot keybind: {ex.Message} {ex.StackTrace}", "exception");
            }
        }

        private void Eft_InitialReadComplete(object? sender, ProfileEventArgs e)
        {
            // Event handlers must return void. Keep the asynchronous implementation
            // in a Task-returning method so every failure can be contained there.
            _ = InitializeAfterLogReadAsync(e.Profile.Snapshot());
        }

        private async Task InitializeAfterLogReadAsync(Profile profile)
        {
            try
            {
                var generation = Interlocked.Increment(ref profileChangeGeneration);

                if (!eft.IsGameRunning)
                {
                    TarkovTracker.DeactivateProfile();
                    trackerProfileId = "";
                    trackerSessionMode = EftSessionMode.Unknown;
                    messageLog.AddMessage("No current EFT session. Please launch Escape from Tarkov.", "info");
                    AddLastDetectedSessionMessage();
                    TarkovDev.StartAutoUpdates();
                    await Task.WhenAll(
                        UpdatePlayerNamesSafely(),
                        UpdateTarkovDevApiData(profile.TarkovDevDataType));
                    return;
                }

                // This is local state and must be visible immediately. Network calls
                // below are optional startup work and must never delay this message.
                AnnounceProfile(profile, force: true);

                // Migrate the original single-token setting before initializing the
                // tracker profile so an existing user keeps their saved token.
                if (TarkovTracker.IsLegacyService
                    && Properties.Settings.Default.tarkovTrackerToken != ""
                    && !TarkovTracker.IsImportableToken(Properties.Settings.Default.tarkovTrackerToken)
                    && profile.Id != "")
                {
                    try
                    {
                        TarkovTracker.SetToken(profile.Id, Properties.Settings.Default.tarkovTrackerToken);
                        Properties.Settings.Default.tarkovTrackerToken = "";
                        Properties.Settings.Default.Save();
                    }
                    catch (Exception ex)
                    {
                        messageLog.AddMessage($"Error setting token from previously saved settings {ex.Message}", "exception");
                    }
                }

                TarkovDev.StartAutoUpdates();

                // Player names are independent. Profile-bound services share the same
                // latest-wins lane used by live mode changes so startup cannot reactivate
                // a profile that EFT has already replaced.
                await Task.WhenAll(
                    UpdatePlayerNamesSafely(),
                    InitializeProfileServices(profile, generation));
            }
            catch (Exception ex)
            {
                // This final boundary protects the WinForms UI from an exception in an
                // asynchronous event path while still leaving a supportable message.
                messageLog.AddMessage($"Error completing startup services: {ex.Message}", "exception");
            }
        }

        private async Task UpdatePlayerNamesSafely()
        {
            try
            {
                await TarkovDev.UpdatePlayerNames();
            }
            catch (Exception ex)
            {
                // Player-name lookup is optional; failure must not cancel tracker or
                // tarkov.dev initialization.
                messageLog.AddMessage($"Error updating tarkov.dev player names: {ex.Message}", "exception");
            }
        }

        private async Task InitializeProfileServices(Profile profile, long generation)
        {
            await profileChangeLock.WaitAsync();
            try
            {
                if (generation != Volatile.Read(ref profileChangeGeneration)
                    || profile.Id != GameWatcher.CurrentProfile.Id
                    || profile.SessionMode != GameWatcher.CurrentProfile.SessionMode)
                {
                    return;
                }

                await InitializeProgress(profile);
                if (generation != Volatile.Read(ref profileChangeGeneration))
                {
                    return;
                }

                await UpdateTarkovDevApiData(profile.TarkovDevDataType);
            }
            finally
            {
                profileChangeLock.Release();
            }
        }

        private void AnnounceProfile(Profile profile, bool force = false)
        {
            // The visible text describes the session mode, not the internal profile ID.
            // A mode-only event is therefore announced once; the later identity marker
            // can activate services without adding a duplicate message.
            if (!force && profile.SessionMode == displayedSessionMode)
                return;

            messageLog.AddMessage(string.Format(localizationService.GetString("UsingProfile"), profile.DisplayName));
            displayedSessionMode = profile.SessionMode;
        }

        private async void Eft_ProfileChanged(object? sender, ProfileEventArgs e)
        {
            var profile = e.Profile.Snapshot();
            var generation = Interlocked.Increment(ref profileChangeGeneration);
            var trackerMustBeInactive = !profile.SupportsTarkovTrackerWrites
                || string.IsNullOrWhiteSpace(profile.Id);

            // Mode transitions are local facts and must not wait behind a previous
            // profile's network request. Announce the mode and revoke the old tracker
            // activation before entering the serialized service-update lane.
            AnnounceProfile(profile);
            if (trackerMustBeInactive)
            {
                TarkovTracker.DeactivateProfile();
                trackerProfileId = "";
                trackerSessionMode = EftSessionMode.Unknown;
            }

            await profileChangeLock.WaitAsync();
            try
            {
                // Repeated profile markers are common. If a newer one arrived while this
                // handler was waiting, only the newest snapshot should change services.
                if (generation != Volatile.Read(ref profileChangeGeneration))
                    return;

                if (trackerMustBeInactive
                    && (trackerProfileId != "" || TarkovTracker.CurrentProfileId != ""))
                {
                    // Session mode is known before EFT emits the new profile identity.
                    // Revoke the old activation during that gap, and keep unsupported
                    // Seasonal/Unknown sessions inactive after identity arrives.
                    TarkovTracker.DeactivateProfile();
                    trackerProfileId = "";
                    trackerSessionMode = EftSessionMode.Unknown;
                }

                var trackerNeedsUpdate = profile.SupportsTarkovTrackerWrites
                    && !string.IsNullOrWhiteSpace(profile.Id)
                    && (profile.Id != trackerProfileId || profile.SessionMode != trackerSessionMode);
                var tarkovDevNeedsUpdate = TarkovDev.LoadedProfileType != profile.TarkovDevDataType;
                if (!trackerNeedsUpdate && !tarkovDevNeedsUpdate)
                    return;

                if (trackerNeedsUpdate)
                {
                    try
                    {
                        await TarkovTracker.SetProfile(
                            profile,
                            forceRefresh: profile.Id == TarkovTracker.CurrentProfileId
                                && profile.SessionMode == TarkovTracker.CurrentSessionMode);
                        if (generation != Volatile.Read(ref profileChangeGeneration)
                            || profile.Id != TarkovTracker.CurrentProfileId
                            || profile.SessionMode != TarkovTracker.CurrentSessionMode)
                        {
                            return;
                        }
                        trackerProfileId = profile.Id;
                        trackerSessionMode = profile.SessionMode;
                    }
                    catch (Exception ex)
                    {
                        messageLog.AddMessage($"Error retrieving Tarkov Tracker profile: {ex.Message}", "exception");
                    }
                }

                if (generation != Volatile.Read(ref profileChangeGeneration))
                    return;

                // PVE and Regular use different Tarkov.dev datasets. Seasonal currently
                // uses the Regular compatibility route because no Seasonal read route exists.
                if (tarkovDevNeedsUpdate)
                {
                    await UpdateTarkovDevApiData(profile.TarkovDevDataType);
                }
            }
            finally
            {
                profileChangeLock.Release();
            }
        }

        private void Eft_ExitedPostRaidMenus(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.airFilterAlert && TarkovTracker.HasAirFilter())
            {
                Sound.Play("air_filter_off");
            }
        }

        private void ScavCooldownTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (!Properties.Settings.Default.scavCooldownAlert)
            {
                return;
            }
            if (!inRaid)
            {
                Sound.Play("scav_available");
            }
            messageLog.AddMessage("Player scav available", "info");
        }

        private void RunthroughTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (Properties.Settings.Default.runthroughAlert)
            {
                Sound.Play("runthrough_over");
                messageLog.AddMessage("Runthrough period over", "info");
            }
        }

        private void Delete_Screenshots(RaidInfoEventArgs e, MonitorMessage? monMessage = null, MonitorMessageButton? screenshotButton = null)
        {
            try
            {
                foreach (var filename in e.RaidInfo.Screenshots)
                {
                    File.Delete(Path.Combine(eft.ScreenshotsPath, filename));
                }
                messageLog.AddMessage($"Deleted {e.RaidInfo.Screenshots.Count} screenshots");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error deleting screenshot: {ex.Message} {ex.StackTrace}", "exception");
            }

            if (monMessage is null || screenshotButton is null)
            {
                return;
            }

            monMessage.Buttons.Remove(screenshotButton);
        }

        private void Handle_Screenshots(RaidInfoEventArgs e, MonitorMessage monMessage)
        {
            var automaticallyDelete = Properties.Settings.Default.automaticallyDeleteScreenshotsAfterRaid;
            if (automaticallyDelete)
            {
                Delete_Screenshots(e);
                return;
            }

            MonitorMessageButton screenshotButton = new($"Delete {e.RaidInfo.Screenshots.Count} Screenshots", Icons.Material.Filled.Delete);
            screenshotButton.OnClick = () =>
            {
                Delete_Screenshots(e, monMessage, screenshotButton);
            };
            screenshotButton.Timeout = TimeSpan.FromMinutes(120).TotalMilliseconds;
            monMessage.Buttons.Add(screenshotButton);
        }

        private async void Eft_RaidEnded(object? sender, RaidInfoEventArgs e)
        {
            // Capture the event-owned profile before awaiting. A later EFT session-mode
            // record can replace the global profile while media is being resumed.
            var completedRaidProfile = e.Profile.Snapshot();
            var completedRaidProgress = TarkovTracker.GetActiveProgressSnapshot(completedRaidProfile);
            inRaid = false;
            await ResumeMediaAfterRaid();
            
            //groupManager.Stale = true;
            MonitorMessage monMessage = new($"Ended {e.RaidInfo.Map?.name} raid");

            if (e.RaidInfo.Screenshots.Count > 0) {
                Handle_Screenshots(e, monMessage);
            }

            messageLog.AddMessage(monMessage);
            runthroughTimer.Stop();
            if (Properties.Settings.Default.scavCooldownAlert
                && completedRaidProfile.SupportsScavCooldown
                && (e.RaidInfo.RaidType == RaidType.Scav || e.RaidInfo.RaidType == RaidType.PVE))
            {
                scavCooldownTimer.Stop();
                scavCooldownTimer.Interval = TimeSpan.FromSeconds(
                    TarkovDev.ResetScavCoolDown(
                        completedRaidProfile.Type,
                        completedRaidProgress ?? new TarkovTracker.ProgressResponse())).TotalMilliseconds;
                scavCooldownTimer.Start();
            }
        }

        private void Eft_GroupRaidSettings(object? sender, LogContentEventArgs<GroupRaidSettingsLogContent> e)
        {
            return;
            groupManager.ClearGroup();
        }

        private void SocketClient_ExceptionThrown(object? sender, ExceptionEventArgs e)
        {
            messageLog.AddMessage($"Error {e.Context}: {e.Exception.Message}\n{e.Exception.StackTrace}", "exception");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            try
            {
                if (Properties.Settings.Default.minimizeAtStartup)
                {

                    WindowState = FormWindowState.Minimized;
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error minimizing at startup: {ex.Message} {ex.StackTrace}", "exception");
            }
        }

        private async void Eft_PlayerPosition(object? sender, PlayerPositionEventArgs e)
        {
            if (e.RaidInfo.Map == null)
            {
                return;
            }
            messageLog.AddMessage($"Player position on {e.RaidInfo.Map.name}: x: {e.Position.X}, y: {e.Position.Y}, z: {e.Position.Z}");
            List<JsonObject> socketMessages = new();
            socketMessages.Add(SocketClient.GetPlayerPositionMessage(e));
            //await SocketClient.UpdatePlayerPosition(e);
            if (Properties.Settings.Default.navigateMapOnPositionUpdate)
            {
                //SocketClient.NavigateToMap(map);
                socketMessages.Add(SocketClient.GetNavigateToMapMessage(e.RaidInfo.Map));
            }
            SocketClient.Send(socketMessages);
        }

        private void UpdateCheck_Error(object? sender, ExceptionEventArgs e)
        {
            messageLog.AddMessage($"Error {e.Context}: {e.Exception.Message}", "exception");
        }

        private void UpdateCheck_NewVersion(object? sender, NewVersionEventArgs e)
        {
            messageLog.AddMessage($"New TarkovMonitor version available ({e.Version})! Click here to open the download page. Please update to this new version before reporting any bugs.", null, e.Uri.ToString());
        }

        private async void Eft_MapLoading(object? sender, EventArgs e)
        {
            if (TarkovTracker.Progress?.data?.tasksProgress == null)
            {
                return;
            }
            try
            {
                //await AllDataLoaded();
                var failedTasks = new List<TarkovDev.Task>();
                foreach (var taskStatus in TarkovTracker.Progress.data.tasksProgress)
                {
                    if (!taskStatus.failed)
                    {
                        continue;
                    }
                    var task = TarkovDev.Tasks.Find(match: t => t.id == taskStatus.id);
                    if (task == null)
                    {
                        continue;
                    }
                    if (task.restartable)
                    {
                        failedTasks.Add(task);
                    }
                }
                if (Properties.Settings.Default.airFilterAlert && TarkovTracker.HasAirFilter())
                {
                    await Sound.Play("air_filter_on");
                }
                if (Properties.Settings.Default.questItemsAlert)
                {
                    await Sound.Play("quest_items");
                }
                if (failedTasks.Count == 0)
                {
                    return;
                }
                foreach (var task in failedTasks)
                {
                    messageLog.AddMessage($"Failed task {task.name} should be restarted", "quest", task.wikiLink);
                }
                if (Properties.Settings.Default.restartTaskAlert)
                {
                    await Sound.Play("restart_failed_tasks");
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error on matching started: {ex.Message}");
            }
        }

        private void Eft_MapLoading_NavigateToMap(object? sender, RaidInfoEventArgs e)
        {
            if (!Properties.Settings.Default.autoNavigateMap)
            {
                return;
            }
            if (e.RaidInfo.Map == null)
            {
                return;
            }
            SocketClient.NavigateToMap(e.RaidInfo.Map);
        }

        private void Eft_GroupUserLeave(object? sender, LogContentEventArgs<GroupMatchUserLeaveLogContent> e)
        {
            return;
            if (e.LogContent.Nickname != "You")
            {
                groupManager.RemoveGroupMember(e.LogContent.Nickname);
            }
            messageLog.AddMessage($"{e.LogContent.Nickname} left the group.", "group");
        }

        private void Eft_GroupInviteAccept(object? sender, LogContentEventArgs<GroupLogContent> e)
        {
            messageLog.AddMessage($"{e.LogContent.Info.Nickname} ({e.LogContent.Info.Side.ToUpper()} {e.LogContent.Info.Level}) accepted group invite.", "group");
        }

        private void Eft_GroupDisbanded(object? sender, EventArgs e)
        {
            return;
            groupManager.ClearGroup();
        }

        private void TarkovTracker_ProgressRetrieved(object? sender, TarkovTracker.ProgressRetrievedEventArgs e)
        {
            if (e.ProfileId != TarkovTracker.CurrentProfileId
                || e.SessionMode != TarkovTracker.CurrentSessionMode)
            {
                return;
            }

            messageLog.AddMessage(string.Format(localizationService.GetString("RetrievedDataFromTarkovTracker"), e.Progress.data.displayName, e.Progress.data.playerLevel, e.Progress.data.pmcFaction), "update", $"https://{Properties.Settings.Default.tarkovTrackerDomain}");
        }

        private void Eft_GroupStaleEvent(object? sender, EventArgs e)
        {
            return;
            groupManager.Stale = true;
        }

        private void Eft_GameStarted(object? sender, EventArgs e)
        {
            messageLog.AddMessage("EFT client detected. Waiting for the active session profile.", "info");
        }

        private void Eft_GameStopped(object? sender, EventArgs e)
        {
            Interlocked.Increment(ref profileChangeGeneration);
            TarkovTracker.DeactivateProfile();
            trackerProfileId = "";
            trackerSessionMode = EftSessionMode.Unknown;
            displayedSessionMode = EftSessionMode.Unknown;
            messageLog.AddMessage("No current EFT session. Please launch Escape from Tarkov.", "info");
            AddLastDetectedSessionMessage();
        }

        private void AddLastDetectedSessionMessage()
        {
            if (string.IsNullOrWhiteSpace(GameWatcher.LastDetectedSessionMode))
            {
                messageLog.AddMessage("Last detected EFT session: unavailable.", "info");
                return;
            }
            messageLog.AddMessage($"Last detected EFT session: {TarkovTracker.GetSessionDisplayName(GameWatcher.LastDetectedSessionMode)}.", "info");
        }

        private void WebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (Debugger.IsAttached) blazorWebView1.WebView.CoreWebView2.OpenDevToolsWindow();
        }

        private async Task<bool> UpdateTarkovDevApiData(ProfileType? profileType = null)
        {
            var targetProfileType = profileType ?? GameWatcher.CurrentProfile.TarkovDevDataType;
            try
            {
                await TarkovDev.UpdateApiData(targetProfileType);
                messageLog.AddMessage(string.Format(localizationService.GetString("RetrievedDataFromTarkovDev"), String.Format("{0:n0}", TarkovDev.Items.Count), TarkovDev.Maps.Count, TarkovDev.Traders.Count, TarkovDev.Tasks.Count, TarkovDev.Stations.Count), "update");
                return true;
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating tarkov.dev API data: {ex.Message}");
                return false;
            }
        }

        private async Task InitializeProgress(Profile profile)
        {
            if (!eft.IsGameRunning)
            {
                TarkovTracker.DeactivateProfile();
                trackerProfileId = "";
                trackerSessionMode = EftSessionMode.Unknown;
                return;
            }
            if (!profile.SupportsTarkovTrackerWrites || string.IsNullOrWhiteSpace(profile.Id))
            {
                TarkovTracker.DeactivateProfile();
                trackerProfileId = "";
                trackerSessionMode = EftSessionMode.Unknown;
                return;
            }

            try
            {
                await TarkovTracker.SetProfile(profile);
                if (profile.Id != TarkovTracker.CurrentProfileId
                    || profile.SessionMode != TarkovTracker.CurrentSessionMode)
                {
                    return;
                }
                trackerProfileId = profile.Id;
                trackerSessionMode = profile.SessionMode;
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error retrieving Tarkov Tracker profile: {ex.Message}");
                return;
            }
            if (TarkovTracker.GetTokenForProfile(profile) == "")
            {
                messageLog.AddMessage(localizationService.GetString("ToAutomaticallyTrackTaskProgress"));
                return;
            }
            /*try
            {
                var tokenResponse = await TarkovTracker.TestToken(TarkovTracker.GetToken(eft.CurrentProfile.Id));
                if (!tokenResponse.permissions.Contains("WP"))
                {
                    messageLog.AddMessage("Your Tarkov Tracker token is missing the required write permissions");
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating progress: {ex.Message}");
                return;
            }*/
        }

        private void Eft_MatchFound(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.matchFoundAlert)
            {
                Sound.Play("match_found");
            }
            if (e.RaidInfo.Map == null)
            {
                return;
            }
            messageLog.AddMessage($"Matching complete on {e.RaidInfo.Map.name} after {e.RaidInfo.QueueTime} seconds");
        }

        private void Eft_NewLogData(object? sender, NewLogDataEventArgs e)
        {
            TarkovDev.LastActivity = DateTime.Now;
            try
            {
                //Debug.WriteLine($"MainBlazorUI {e.Type} NewLogData");
                logRepository.AddLog(e.Data, e.Type.ToString());
            } catch (Exception ex)
            {
                messageLog.AddMessage($"{ex.GetType().Name} adding raw lag to repository: "+ex.StackTrace, "exception");
            }
        }

        private void Eft_GroupMemberReady(object? sender, LogContentEventArgs<GroupMatchRaidReadyLogContent> e)
        {
            return;
            groupManager.UpdateGroupMember(e.LogContent);
            messageLog.AddMessage($"{e.LogContent.extendedProfile.Info.Nickname} ({e.LogContent.extendedProfile.PlayerVisualRepresentation.Info.Side.ToUpper()} {e.LogContent.extendedProfile.PlayerVisualRepresentation.Info.Level}) ready.", "group");
        }

        private async void Eft_TaskFinished(object? sender, LogContentEventArgs<TaskStatusMessageLogContent> e)
        {
            //await AllDataLoaded();
            var task = TarkovDev.Tasks.Find(t => t.id == e.LogContent.TaskId);
            if (task == null)
            {
                //Debug.WriteLine($"Task with id {e.TaskId} not found");
                return;
            }

            messageLog.AddMessage($"Completed task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!CanWriteTarkovTrackerProgress(e.Profile))
            {
                return;
            }
            try
            {
                await TarkovTracker.SetTaskComplete(task.id, e.Profile.Id, e.Profile.SessionMode);
                //messageLog.AddMessage(response, "quest");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
            }
        }

        private async void Eft_TaskFailed(object? sender, LogContentEventArgs<TaskStatusMessageLogContent> e)
        {
            var task = TarkovDev.Tasks.Find(t => t.id == e.LogContent.TaskId);
            if (task == null)
            {
                return;
            }

            messageLog.AddMessage($"Failed task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!CanWriteTarkovTrackerProgress(e.Profile))
            {
                return;
            }
            try
            {
                await TarkovTracker.SetTaskFailed(task.id, e.Profile.Id, e.Profile.SessionMode);
                //messageLog.AddMessage(response, "quest");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
            }
        }

        private async void Eft_TaskStarted(object? sender, LogContentEventArgs<TaskStatusMessageLogContent> e)
        {
            var task = TarkovDev.Tasks.Find(t => t.id == e.LogContent.TaskId);
            if (task == null)
            {
                return;
            }
            messageLog.AddMessage($"Started task {task.name}", "quest", $"https://tarkov.dev/task/{task.normalizedName}");

            if (!CanWriteTarkovTrackerProgress(e.Profile))
            {
                return;
            }
            try
            {
                await TarkovTracker.SetTaskStarted(e.LogContent.TaskId, e.Profile.Id, e.Profile.SessionMode);
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating Tarkov Tracker task progression: {ex.Message}", "exception");
            }
        }

        private void Eft_FleaSold(object? sender, LogContentEventArgs<FleaSoldMessageLogContent> e)
        {
            RecordStatsSafely(
                () => Stats.AddFleaSale(e.LogContent, e.Profile),
                "TM-STATS-002",
                "Flea-market statistics");
            if (TarkovDev.Items == null)
            {
                return;
            }
            List<string> received = new();
            //await AllDataLoaded();
            foreach (var receivedId in e.LogContent.ReceivedItems.Keys)
            {
                if (receivedId == "5449016a4bdc2d6f028b456f")
                {
                    received.Add(e.LogContent.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("ru-RU")));
                    continue;
                }
                else if (receivedId == "5696686a4bdc2da3298b456a")
                {
                    received.Add(e.LogContent.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("en-US")));
                    continue;
                }
                else if (receivedId == "569668774bdc2da2298b4568")
                {
                    received.Add(e.LogContent.ReceivedItems[receivedId].ToString("C0", CultureInfo.CreateSpecificCulture("de-DE")));
                    continue;
                }
                var receivedItem = TarkovDev.Items.Find(item => item.id == receivedId);
                if (receivedItem == null)
                {
                    continue;
                }
                received.Add($"{String.Format("{0:n0}", e.LogContent.ReceivedItems[receivedId])} {receivedItem.name}");
            }
            var soldItem = TarkovDev.Items.Find(item => item.id == e.LogContent.SoldItemId);
            if (soldItem == null)
            {
                return;
            }
            messageLog.AddMessage($"{e.LogContent.Buyer} purchased {String.Format("{0:n0}", e.LogContent.SoldItemCount)} {soldItem.name} for {String.Join(", ", received.ToArray())}", "flea", soldItem.link);
        }

        private void Eft_FleaOfferExpired(object? sender, LogContentEventArgs<FleaExpiredMessageLogContent> e)
        {
            if (TarkovDev.Items == null)
            {
                return;
            }
            var unsoldItem = TarkovDev.Items.Find(item => item.id == e.LogContent.ItemId);
            if (unsoldItem == null)
            {
                return;
            }
            messageLog.AddMessage($"Your offer for {unsoldItem.name} (x{e.LogContent.ItemCount}) expired", "flea", unsoldItem.link);
        }

        private void Eft_DebugMessage(object? sender, DebugEventArgs e)
        {
            messageLog.AddMessage(e.Message, "debug");
        }

        private void Eft_ExceptionThrown(object? sender, ExceptionEventArgs e)
        {
            var message = string.IsNullOrWhiteSpace(e.UserMessage)
                ? $"Error {e.Context}: {e.Exception.Message}\n{e.Exception.StackTrace}"
                : e.UserMessage;
            messageLog.AddMessage(message, "exception");
        }

        private async void Eft_RaidStarting(object? sender, RaidInfoEventArgs e)
        {
            if (Properties.Settings.Default.raidStartAlert)
            {
                // always notify if the GameStarting event appeared
                Sound.Play("raid_starting");
            }

            await PauseMediaForRaid();
        }

        private async Task PauseMediaForRaid()
        {
            if (!Properties.Settings.Default.pauseMediaOnRaid) return;

            try
            {
                int pausedSessions = await MediaController.PauseAsync();
                messageLog.AddMessage($"Paused {pausedSessions} music session(s)", "info");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error pausing media: {ex.Message}", "exception");
            }
        }

        private async void Eft_RaidStopping(object? sender, EventArgs e)
        {
            await ResumeMediaAfterRaid();
        }

        private async Task ResumeMediaAfterRaid()
        {
            if (!Properties.Settings.Default.pauseMediaOnRaid) return;

            try
            {
                int resumedSessions = await MediaController.ResumeAsync();
                if (resumedSessions > 0)
                {
                    messageLog.AddMessage($"Resumed {resumedSessions} music session(s)", "info");
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error resuming media: {ex.Message}", "exception");
            }
        }

        private async void Eft_RaidStart(object? sender, RaidInfoEventArgs e)
        {
            inRaid = true;
            RecordStatsSafely(
                () => Stats.AddRaid(e),
                "TM-STATS-001",
                "Raid statistics");
            
            // GameStarting is not always logged for scav raids, so pause here as a fallback.
            if (e.RaidInfo.StartingTime == null)
            {
                await PauseMediaForRaid();
            }
            
            if (!e.RaidInfo.Reconnected && e.RaidInfo.RaidType != RaidType.Unknown)
            {
                MonitorMessage monMessage = new($"Starting {e.RaidInfo.RaidType} raid on {e.RaidInfo.Map?.name}");
                if (e.RaidInfo.Map != null && e.RaidInfo.StartedTime != null && e.RaidInfo.Map.HasGoons())
                {
                    AddGoonsButton(monMessage, e.RaidInfo);
                }
                else if (e.RaidInfo.Map == null)
                {
                    monMessage.Message = $"Starting {e.RaidInfo.RaidType} raid on:";
                    MonitorMessageSelect select = new();
                    foreach (var gameMap in TarkovDev.Maps)
                    {
                        select.Options.Add(new(gameMap.name, gameMap.id));
                    }
                    select.Placeholder = "Select map";
                    monMessage.Selects.Add(select);
                    MonitorMessageButton mapButton = new("Set map", Icons.Material.Filled.Map);
                    mapButton.OnClick += () => {
                        if (select.Selected == null)
                        {
                            return;
                        }
                        e.RaidInfo.Map = TarkovDev.Maps.Find(m => m.id == select.Selected.Value);
                        monMessage.Message = $"Starting {e.RaidInfo.RaidType} raid on {select.Selected.Text}";
                        monMessage.Buttons.Clear();
                        monMessage.Selects.Clear();
                        //AddGoonsButton(monMessage, e.RaidInfo); // offline raids have goons on all goons maps
                        if (Properties.Settings.Default.autoNavigateMap)
                        {
                            if (e.RaidInfo.Map == null)
                            {
                                return;
                            }
                            SocketClient.NavigateToMap(e.RaidInfo.Map);
                        }
                    };
                    monMessage.Buttons.Add(mapButton);
                }
                messageLog.AddMessage(monMessage);
                if (Properties.Settings.Default.raidStartAlert && e.RaidInfo.StartingTime == null)
                {
                    // if there was no GameStarting event in the log, play the notification sound
                    Sound.Play("raid_starting");
                }
            }
            else
            {
                messageLog.AddMessage($"Re-entering raid on {e.RaidInfo.Map?.name}");
            }
            if (Properties.Settings.Default.runthroughAlert && !e.RaidInfo.Reconnected && (e.RaidInfo.RaidType == RaidType.PMC || e.RaidInfo.RaidType == RaidType.PVE))
            {
                runthroughTimer.Stop();
                runthroughTimer.Start();
            }
            return;
            if (Properties.Settings.Default.submitQueueTime
                && e.RaidInfo.Profile.SupportsTarkovDevWrites
                && e.RaidInfo.QueueTime > 0
                && e.RaidInfo.RaidType != RaidType.Unknown)
            {
                try
                {
                    await TarkovDev.PostQueueTime(e.RaidInfo.Map.nameId, (int)Math.Round(e.RaidInfo.QueueTime), e.RaidInfo.RaidType.ToString().ToLower(), e.RaidInfo.Profile.Type);
                }
                catch (Exception ex)
                {
#if DEBUG
                    messageLog.AddMessage($"Error submitting queue time: {ex.Message}", "exception");
#endif
                }
            }
        }

        private void RecordStatsSafely(Action operation, string reportCode, string description)
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                var signature = $"{reportCode}:{ex.GetType().FullName}";
                lock (reportedStatsFailuresLock)
                {
                    if (!reportedStatsFailures.Add(signature))
                    {
                        return;
                    }
                }

                messageLog.AddMessage(
                    $"{reportCode} | {description} could not be saved | Exception: {ex.GetType().Name} | Monitoring continued. Copy this message when reporting the issue.",
                    "exception");
            }
        }

        private static bool CanWriteTarkovTrackerProgress(Profile profile)
        {
            return profile.SupportsTarkovTrackerWrites
                && !string.IsNullOrWhiteSpace(profile.Id)
                && string.Equals(profile.Id, TarkovTracker.CurrentProfileId, StringComparison.Ordinal)
                && profile.SessionMode == TarkovTracker.CurrentSessionMode
                && TarkovTracker.ValidToken;
        }

        private void AddGoonsButton(MonitorMessage monMessage, RaidInfo raidInfo)
        {
            if (!raidInfo.Profile.SupportsTarkovDevWrites)
            {
                // Tarkov.dev has no Seasonal write contract. Do not silently
                // attribute Seasonal observations to the Regular data pool.
                return;
            }

            if (raidInfo.Map != null && raidInfo.StartedTime != null && raidInfo.Map.HasGoons())
            {
                MonitorMessageButton goonsButton = new($"Report Goons", Icons.Material.Filled.Groups);
                goonsButton.OnClick = async () => {
                    try
                    {
                        await TarkovDev.PostGoonsSighting(raidInfo.Map?.nameId, (DateTime)raidInfo.StartedTime, Int32.Parse(raidInfo.Profile.AccountId), raidInfo.Profile.Type);
                        messageLog.AddMessage($"Goons reported on {raidInfo.Map?.name}", "info");
                    }
                    catch (Exception ex)
                    {
                        messageLog.AddMessage($"Error reporting goons: {ex.Message} {ex.StackTrace}", "exception");
                    }
                    monMessage.Buttons.Remove(goonsButton);
                };
                goonsButton.Confirm = new(
                    $"Report Goons on {raidInfo.Map?.name}",
                    "<p>Please only submit a report if you saw the goons in this raid.</p><p><strong>Notice:</strong> By submitting a goons report, you consent to collection of your IP address and EFT account id for report verification purposes.</p>",
                    "Submit report", "Cancel"
                );
                goonsButton.Timeout = TimeSpan.FromMinutes(120).TotalMilliseconds;
                monMessage.Buttons.Add(goonsButton);
            }
        }

        private async void Eft_RaidExited(object? sender, RaidExitedEventArgs e)
        {
            //groupManager.Stale = true;
            runthroughTimer.Stop();
            inRaid = false;
            await ResumeMediaAfterRaid();
            
            try
            {
                var mapName = e.Map;
                var map = TarkovDev.Maps.Find(m => m.nameId == mapName);
                if (map != null) mapName = map.name;
                messageLog.AddMessage($"Exited {mapName} raid", "raidleave");
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error updating log message from event: {ex.Message}", "exception");
            }
        }

        private void MainBlazorUI_Resize(object sender, EventArgs e)
        {
            WindowStateChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                if (this.WindowState == FormWindowState.Minimized && Properties.Settings.Default.minimizeToTray)
                {
                    Hide();
                    notifyIconTarkovMonitor.Visible = true;
                }
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error minimizing to tray: {ex.Message} {ex.StackTrace}", "exception");
            }
        }

        private void notifyIconTarkovMonitor_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            try
            {
                Show();
                this.WindowState = FormWindowState.Normal;
                notifyIconTarkovMonitor.Visible = false;
            }
            catch (Exception ex)
            {
                messageLog.AddMessage($"Error restoring from tray: {ex.Message} {ex.StackTrace}", "exception");
            }
        }

        private void menuItemQuit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        /*private async Task UpdatePlayerLevel()
        {
            if (!TarkovTracker.ValidToken)
            {
                return;
            }
            var level = TarkovDev.GetLevel(await TarkovDev.GetExperience(eft.AccountId));
            if (level == TarkovTracker.Progress.data.playerLevel)
            {
                return;
            }
        }*/
    }
}
