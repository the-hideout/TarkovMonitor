using Onova;
using Onova.Services;

namespace TarkovMonitor
{
    internal sealed class UpdateService : IDisposable
    {
        private const string RepositoryOwner = "the-hideout";
        private const string RepositoryName = "TarkovMonitor";
        private const string ReleaseAssetName = "TarkovMonitor.zip";

        // Local testing packages must contain a version in their filename:
        // TarkovMonitor-2.1.1.0.zip
        private const string LocalAssetPattern = "TarkovMonitor-*.zip";
        private const string LocalSourceEnvironmentVariable =
            "TARKOVMONITOR_UPDATE_SOURCE";

        private readonly UpdateManager updateManager;
        private readonly System.Timers.Timer updateTimer;
        private readonly SemaphoreSlim checkLock = new(1, 1);
        private bool disposed;

        public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;
        public event EventHandler<ExceptionEventArgs>? Error;

        public UpdateService()
        {
            var localSource = Environment.GetEnvironmentVariable(LocalSourceEnvironmentVariable);

            IPackageResolver resolver = string.IsNullOrWhiteSpace(localSource)
                ? new GithubPackageResolver(
                    RepositoryOwner,
                    RepositoryName,
                    ReleaseAssetName)
                : new LocalPackageResolver(
                    localSource,
                    LocalAssetPattern);

            updateManager = new UpdateManager(
                resolver,
                new ZipPackageExtractor());

            updateTimer = new System.Timers.Timer(
                TimeSpan.FromDays(1).TotalMilliseconds)
            {
                AutoReset = true,
                Enabled = false
            };

            updateTimer.Elapsed += UpdateTimer_Elapsed;
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            updateTimer.Start();
            _ = CheckForUpdatesAsync();
        }

        public async Task CheckForUpdatesAsync(
            CancellationToken cancellationToken = default)
        {
            if (!await checkLock.WaitAsync(0, cancellationToken))
            {
                return;
            }

            try
            {
                var result =
                    await updateManager.CheckForUpdatesAsync(cancellationToken);

                if (result.CanUpdate && result.LastVersion is not null)
                {
                    UpdateAvailable?.Invoke(
                        this,
                        new UpdateAvailableEventArgs(result.LastVersion));
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(
                    this,
                    new ExceptionEventArgs(
                        ex,
                        "checking for application update"));
            }
            finally
            {
                checkLock.Release();
            }
        }

        public Task PrepareUpdateAsync(Version version, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            return updateManager.PrepareUpdateAsync(
                version,
                progress,
                cancellationToken);
        }

        public void LaunchUpdater(Version version)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            updateManager.LaunchUpdater(
                version,
                restart: true,
                restartArguments: string.Empty);
        }

        private async void UpdateTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            await CheckForUpdatesAsync();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            updateTimer.Stop();
            updateTimer.Elapsed -= UpdateTimer_Elapsed;
            updateTimer.Dispose();
            updateManager.Dispose();
        }
    }

    internal sealed class UpdateAvailableEventArgs : EventArgs
    {
        public Version Version { get; }

        public UpdateAvailableEventArgs(Version version)
        {
            Version = version;
        }
    }
}