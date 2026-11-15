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
                    ? LocalizationService.T("UpdateAvailableInstall", newVersion.Version)
                    : LocalizationService.T("UpdateAvailableDownload", newVersion.Version),
                "update",
                newVersion.Uri.ToString(),
                LocalizationService.T("ViewTheRelease"));

            if (canInstall)
            {
                message.Buttons.Add(CreateInstallButton(message, newVersion));
            }

            messageLog.AddMessage(message);
        }

        private MonitorMessageButton CreateInstallButton(MonitorMessage message, NewVersionEventArgs newVersion)
        {
            var button = new MonitorMessageButton(LocalizationService.T("InstallAndRestart"), icon: Icons.Material.Filled.Update)
            {
                Color = MudBlazor.Color.Info,
                Confirm = new MonitorMessageButtonConfirm(
                    LocalizationService.T("InstallUpdate"),
                    LocalizationService.T("InstallUpdateConfirmHtml", newVersion.Version),
                    LocalizationService.T("Install"),
                    LocalizationService.T("NotNow")),
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

            var cancelButton = new MonitorMessageButton(LocalizationService.T("Cancel"), icon: Icons.Material.Filled.Cancel)
            {
                Color = MudBlazor.Color.Default,
            };
            cancelButton.OnClick = () => cancellation.Cancel();
            message.Buttons.Remove(installButton);
            message.Buttons.Add(cancelButton);
            SetMessageText(message, LocalizationService.T("PreparingDownload", newVersion.Version));

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
                SetMessageText(message, LocalizationService.T("UpdateReadyRestarting", newVersion.Version));

                UpdateInstaller.StartApply(staged);
                requestExit();
            }
            catch (OperationCanceledException)
            {
                Reset(message, installButton, cancelButton, LocalizationService.T("UpdateCancelled"), newVersion);
            }
            catch (Exception exception)
            {
                Reset(
                    message,
                    installButton,
                    cancelButton,
                    LocalizationService.T("UpdateInstallFailedDownloadManually", newVersion.Version),
                    newVersion);
                messageLog.AddException(
                    LocalizationService.T("UpdateInstallFailedCopyDiagnostics"),
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
            SetMessageText(message, LocalizationService.T("UpdateStillAvailable", text, newVersion.Version));
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
                    => LocalizationService.T("DownloadingWithPercent", version, percent, DescribeSize(update.BytesReceived), DescribeSize(update.TotalBytes)),
                UpdateStage.Downloading
                    => LocalizationService.T("DownloadingWithSize", version, DescribeSize(update.BytesReceived)),
                UpdateStage.Verifying => LocalizationService.T("VerifyingDownload", version),
                UpdateStage.Extracting => LocalizationService.T("UnpackingUpdate", version),
                _ => LocalizationService.T("ReadyToInstall", version),
            };
        }

        private static string DescribeSize(long bytes)
        {
            return $"{bytes / 1024d / 1024d:0.#} MB";
        }
    }
}
