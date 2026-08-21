using System.Diagnostics;
using System.IO.Compression;

namespace TarkovMonitor.Updating
{
    internal enum UpdateStage
    {
        Downloading,
        Verifying,
        Extracting,
        Ready,
    }

    internal readonly record struct UpdateProgress(UpdateStage Stage, long BytesReceived, long TotalBytes)
    {
        /// <summary>
        /// Null while the server does not report a content length, which keeps
        /// the caller from displaying a percentage it cannot compute.
        /// </summary>
        public int? Percent => TotalBytes > 0
            ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes, 0, 100)
            : null;
    }

    /// <summary>
    /// A staged update that has been downloaded and unpacked, and is ready to
    /// replace the files of the running installation.
    /// </summary>
    internal sealed record StagedUpdate(Version Version, string StagingDirectory, string PayloadDirectory);

    /// <summary>
    /// Downloads a GitHub release archive, unpacks it under the user profile,
    /// and hands control to a temporary copy of the running executable. The
    /// copy can overwrite the installation directory because it does not run
    /// from it; see <see cref="UpdateApplier"/> for the other half.
    /// </summary>
    internal static class UpdateInstaller
    {
        internal const string ApplyUpdateSwitch = "--apply-update";
        internal const string ExecutableName = "TarkovMonitor.exe";
        private const string StagingFolderName = "updates";
        private const string PayloadFolderName = "payload";
        private const string LauncherFolderName = "launcher";
        private const string ArchiveFileName = "update.zip";

        private static readonly HttpClient downloadClient = CreateDownloadClient();

        /// <summary>
        /// Staging lives under the local profile so an installation in a
        /// read-only location can still download; only the final copy needs
        /// write access to the application directory.
        /// </summary>
        internal static string StagingRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovMonitor",
            StagingFolderName);

        internal static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        internal static string InstallExecutablePath => Path.Combine(InstallDirectory, ExecutableName);

        /// <summary>
        /// True when the running application is laid out the way the release
        /// archive is built. A development run out of the build output has no
        /// matching executable name and must not be self-updated.
        /// </summary>
        internal static bool IsSupportedInstallation => File.Exists(InstallExecutablePath);

        /// <summary>
        /// Downloads and unpacks the archive attached to the release. The
        /// returned staging folder is left on disk for the launcher to consume.
        /// </summary>
        internal static async Task<StagedUpdate> StageAsync(
            Version version,
            UpdateCheck.ReleaseAsset asset,
            IProgress<UpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stagingDirectory = Path.Combine(StagingRoot, version.ToString());
            // A previous attempt may have been cancelled or interrupted part
            // way through, so never trust what is already there.
            DeleteDirectory(stagingDirectory);
            Directory.CreateDirectory(stagingDirectory);

            var archivePath = Path.Combine(stagingDirectory, ArchiveFileName);
            try
            {
                await DownloadAsync(asset, archivePath, progress, cancellationToken);

                progress?.Report(new UpdateProgress(UpdateStage.Verifying, asset.size, asset.size));
                VerifyArchive(archivePath);

                progress?.Report(new UpdateProgress(UpdateStage.Extracting, asset.size, asset.size));
                var payloadDirectory = Path.Combine(stagingDirectory, PayloadFolderName);
                // ExtractToDirectory rejects entries that resolve outside the
                // destination, so a malformed archive cannot write elsewhere.
                ZipFile.ExtractToDirectory(archivePath, payloadDirectory, overwriteFiles: true);

                if (!File.Exists(Path.Combine(payloadDirectory, ExecutableName)))
                {
                    throw new InvalidDataException($"The downloaded archive did not contain {ExecutableName}.");
                }

                // The archive is no longer needed and is the largest thing in
                // staging, so reclaim the space before the restart.
                TryDeleteFile(archivePath);

                progress?.Report(new UpdateProgress(UpdateStage.Ready, asset.size, asset.size));
                return new StagedUpdate(version, stagingDirectory, payloadDirectory);
            }
            catch
            {
                DeleteDirectory(stagingDirectory);
                throw;
            }
        }

        /// <summary>
        /// Copies the running executable out of the installation directory and
        /// starts it in apply mode. The caller is expected to exit immediately
        /// afterwards; the copy waits for this process to end before replacing
        /// any files.
        /// </summary>
        internal static void StartApply(StagedUpdate staged)
        {
            var launcherDirectory = Path.Combine(staged.StagingDirectory, LauncherFolderName);
            Directory.CreateDirectory(launcherDirectory);
            var launcherPath = Path.Combine(launcherDirectory, ExecutableName);

            // The currently running build applies the update rather than the
            // downloaded one, so the command line contract is always the one
            // this version was compiled against.
            File.Copy(Environment.ProcessPath ?? InstallExecutablePath, launcherPath, overwrite: true);

            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = launcherDirectory,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(ApplyUpdateSwitch);
            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(staged.PayloadDirectory);
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(InstallDirectory);
            startInfo.ArgumentList.Add("--launch");
            startInfo.ArgumentList.Add(InstallExecutablePath);

            if (!IsDirectoryWritable(InstallDirectory))
            {
                // An installation under Program Files needs an elevated copy
                // step. UseShellExecute is required to request the prompt.
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
            }

            Process.Start(startInfo);
        }

        internal static bool HasStaging => Directory.Exists(StagingRoot)
            && Directory.EnumerateDirectories(StagingRoot).Any();

        /// <summary>
        /// Removes staging folders left behind by earlier updates. The launcher
        /// that applied the last update is usually still exiting, so its own
        /// folder can survive the first attempt.
        /// </summary>
        internal static void CleanStaleStaging()
        {
            if (!Directory.Exists(StagingRoot))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(StagingRoot))
            {
                DeleteDirectory(directory);
            }
        }

        private static async Task DownloadAsync(
            UpdateCheck.ReleaseAsset asset,
            string destinationPath,
            IProgress<UpdateProgress>? progress,
            CancellationToken cancellationToken)
        {
            using var response = await downloadClient.GetAsync(
                asset.browser_download_url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? asset.size;
            progress?.Report(new UpdateProgress(UpdateStage.Downloading, 0, totalBytes));

            using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            var lastReported = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                // Reporting every chunk would redraw the notification thousands
                // of times for a release archive this size.
                if (received - lastReported < 1_000_000 && received != totalBytes)
                {
                    continue;
                }
                lastReported = received;
                progress?.Report(new UpdateProgress(UpdateStage.Downloading, received, totalBytes));
            }
        }

        private static void VerifyArchive(string archivePath)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var hasExecutable = archive.Entries.Any(entry => string.Equals(
                entry.FullName,
                ExecutableName,
                StringComparison.OrdinalIgnoreCase));
            if (!hasExecutable)
            {
                throw new InvalidDataException($"The downloaded archive did not contain {ExecutableName}.");
            }
        }

        private static bool IsDirectoryWritable(string directory)
        {
            var probePath = Path.Combine(directory, $".tarkovmonitor-write-probe-{Guid.NewGuid():N}");
            try
            {
                using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void DeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Staging cleanup is opportunistic; a locked folder is retried
                // the next time the application starts.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // See DeleteDirectory.
            }
        }

        private static HttpClient CreateDownloadClient()
        {
            var client = new HttpClient
            {
                // Release archives are large and users are frequently on slow
                // connections, so the default 100 second timeout is not enough.
                Timeout = TimeSpan.FromMinutes(30),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tarkov-monitor");
            return client;
        }
    }
}
