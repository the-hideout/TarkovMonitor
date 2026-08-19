using MudBlazor;

namespace TarkovMonitor.Updating
{
    /// <summary>
    /// Turns a new-version notice into an actionable message: the user is told
    /// an update exists, and can install it without leaving the application.
    /// Nothing is downloaded until the user asks for it.
    /// </summary>
    internal sealed class UpdateCoordinator
    {
        private const int CleanupAttempts = 6;
        private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromSeconds(5);

        private readonly MessageLog messageLog;
        private readonly Action requestExit;
        private readonly object gate = new();
        private Version? announcedVersion;
        private bool updateStarted;

        internal UpdateCoordinator(MessageLog messageLog, Action requestExit)
        {
            this.messageLog = messageLog;
            this.requestExit = requestExit;
        }

        /// <summary>
        /// Discards staging folders from previous updates, which hold a full
        /// copy of the application and are worth reclaiming promptly. The
        /// launcher that applied the last update exits moments after starting
        /// this process, so the first attempt usually cannot delete it.
        /// </summary>
        internal void CleanStaleStaging()
        {
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < CleanupAttempts; attempt++)
                {
                    lock (gate)
                    {
                        // A download started in the meantime owns a staging
                        // folder that must not be deleted underneath it.
                        if (updateStarted)
                        {
                            return;
                        }
                    }

                    UpdateInstaller.CleanStaleStaging();
                    if (!UpdateInstaller.HasStaging)
                    {
                        return;
                    }
                    await Task.Delay(CleanupRetryDelay);
                }
            });
        }

        internal void Announce(NewVersionEventArgs newVersion)
        {
            lock (gate)
            {
                // The check runs again every day, and a user who is not ready
                // to restart should not collect a message per day.
                if (announcedVersion == newVersion.Version)
                {
                    return;
                }
                announcedVersion = newVersion.Version;
            }

            var canInstall = newVersion.Asset != null && UpdateInstaller.IsSupportedInstallation;
            var message = new MonitorMessage(
                canInstall
                    ? $"Tarkov Monitor {newVersion.Version} is available. Install it before reporting a bug."
                    : $"Tarkov Monitor {newVersion.Version} is available. Download it before reporting a bug.",
                "update",
                newVersion.Uri.ToString(),
                "View the release");

            if (canInstall)
            {
                message.Buttons.Add(CreateInstallButton(message, newVersion));
            }

            messageLog.AddMessage(message);
        }

        private MonitorMessageButton CreateInstallButton(MonitorMessage message, NewVersionEventArgs newVersion)
        {
            var button = new MonitorMessageButton("Install and restart", icon: Icons.Material.Filled.Update)
            {
                Color = MudBlazor.Color.Info,
                Confirm = new MonitorMessageButtonConfirm(
                    "Install update",
                    $"Tarkov Monitor will download version {newVersion.Version}, close, and reopen to finish installing."
                        + "<br /><br />Do not do this while you are in a raid.",
                    "Install",
                    "Not now"),
            };
            button.OnClick = () => BeginUpdate(message, button, newVersion);
            return button;
        }

        private void BeginUpdate(MonitorMessage message, MonitorMessageButton installButton, NewVersionEventArgs newVersion)
        {
            var asset = newVersion.Asset;
            if (asset == null)
            {
                return;
            }

            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (updateStarted)
                {
                    return;
                }
                updateStarted = true;
                cancellation = new CancellationTokenSource();
            }

            var cancelButton = new MonitorMessageButton("Cancel", icon: Icons.Material.Filled.Cancel)
            {
                Color = MudBlazor.Color.Default,
            };
            cancelButton.OnClick = () => cancellation.Cancel();
            message.Buttons.Remove(installButton);
            message.Buttons.Add(cancelButton);
            SetMessageText(message, $"Preparing to download Tarkov Monitor {newVersion.Version}...");

            _ = Task.Run(() => RunUpdateAsync(message, installButton, cancelButton, newVersion, asset, cancellation));
        }

        private async Task RunUpdateAsync(
            MonitorMessage message,
            MonitorMessageButton installButton,
            MonitorMessageButton cancelButton,
            NewVersionEventArgs newVersion,
            UpdateCheck.ReleaseAsset asset,
            CancellationTokenSource cancellation)
        {
            var startedUtc = DateTime.UtcNow;
            try
            {
                var progress = new Progress<UpdateProgress>(update => SetMessageText(
                    message,
                    DescribeProgress(newVersion.Version, update)));

                var staged = await UpdateInstaller.StageAsync(
                    newVersion.Version,
                    asset,
                    progress,
                    cancellation.Token);

                message.Buttons.Remove(cancelButton);
                SetMessageText(message, $"Tarkov Monitor {newVersion.Version} is ready. Restarting...");

                UpdateInstaller.StartApply(staged);
                requestExit();
            }
            catch (OperationCanceledException)
            {
                Reset(message, installButton, cancelButton, "The update was cancelled.", newVersion);
            }
            catch (Exception exception)
            {
                Reset(
                    message,
                    installButton,
                    cancelButton,
                    $"Tarkov Monitor {newVersion.Version} could not be installed. Use the link above to download it.",
                    newVersion);
                messageLog.AddException(
                    "Installing the update failed; copy diagnostics for details.",
                    "TM-UPDATE-003",
                    "InstallUpdate",
                    exception,
                    "UpdateCheck",
                    "Install",
                    asset.browser_download_url,
                    DiagnosticsService.ElapsedMilliseconds(startedUtc));
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        private void Reset(
            MonitorMessage message,
            MonitorMessageButton installButton,
            MonitorMessageButton cancelButton,
            string text,
            NewVersionEventArgs newVersion)
        {
            lock (gate)
            {
                updateStarted = false;
            }
            message.Buttons.Remove(cancelButton);
            // The original button carries the confirmation dialog and the
            // closure over this release, so it can simply be put back.
            message.Buttons.Add(installButton);
            SetMessageText(message, $"{text} Tarkov Monitor {newVersion.Version} is still available.");
        }

        private static void SetMessageText(MonitorMessage message, string text)
        {
            message.Message = text;
            message.NotifyChanged();
        }

        private static string DescribeProgress(Version version, UpdateProgress update)
        {
            return update.Stage switch
            {
                UpdateStage.Downloading when update.Percent is int percent
                    => $"Downloading Tarkov Monitor {version}... {percent}% ({DescribeSize(update.BytesReceived)} of {DescribeSize(update.TotalBytes)})",
                UpdateStage.Downloading
                    => $"Downloading Tarkov Monitor {version}... {DescribeSize(update.BytesReceived)}",
                UpdateStage.Verifying => $"Verifying the Tarkov Monitor {version} download...",
                UpdateStage.Extracting => $"Unpacking Tarkov Monitor {version}...",
                _ => $"Tarkov Monitor {version} is ready to install.",
            };
        }

        private static string DescribeSize(long bytes)
        {
            return $"{bytes / 1024d / 1024d:0.#} MB";
        }
    }
}
