using System.Diagnostics;
using System.Text;

namespace TarkovMonitor.Updating
{
    /// <summary>
    /// The half of the updater that runs from a temporary copy of the
    /// executable. Because it does not run from the installation directory it
    /// can overwrite every file there, which the application itself cannot do
    /// while it is running.
    /// </summary>
    internal static class UpdateApplier
    {
        private const int ExitWaitSeconds = 120;
        private const int CopyAttempts = 30;
        private static readonly TimeSpan CopyRetryDelay = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Runs the copy-and-restart sequence when the command line asks for
        /// it. Returns true when the process acted as an updater and should not
        /// continue into normal startup.
        /// </summary>
        internal static bool TryHandle(string[] args)
        {
            if (args.Length == 0 || !args.Contains(UpdateInstaller.ApplyUpdateSwitch, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var options = ParseArguments(args);
            var log = new StringBuilder();
            try
            {
                Apply(options, log);
            }
            catch (Exception exception)
            {
                log.AppendLine(exception.ToString());
                WriteLog(options, log);
                MessageBox.Show(
                    "Tarkov Monitor could not install the update, and the previous version was left in place."
                        + Environment.NewLine + Environment.NewLine
                        + exception.Message
                        + Environment.NewLine + Environment.NewLine
                        + "Download the new version manually from the releases page.",
                    "Tarkov Monitor update failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                TryRelaunch(options);
                return true;
            }

            WriteLog(options, log);
            return true;
        }

        private static void Apply(ApplyOptions options, StringBuilder log)
        {
            if (options.SourceDirectory == null || options.TargetDirectory == null)
            {
                throw new ArgumentException("The updater was started without a source and target directory.");
            }
            if (!Directory.Exists(options.SourceDirectory))
            {
                throw new DirectoryNotFoundException($"The staged update is missing: {options.SourceDirectory}");
            }

            WaitForApplicationExit(options.ProcessId, log);

            var backupDirectory = Path.Combine(
                Path.GetDirectoryName(options.SourceDirectory) ?? options.SourceDirectory,
                "backup");
            // Track what was replaced so a failure part way through can put the
            // working installation back rather than leaving a mixed one.
            var restored = new List<(string TargetPath, string BackupPath)>();

            try
            {
                foreach (var sourcePath in Directory.EnumerateFiles(options.SourceDirectory, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(options.SourceDirectory, sourcePath);
                    var targetPath = Path.Combine(options.TargetDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                    if (File.Exists(targetPath))
                    {
                        var backupPath = Path.Combine(backupDirectory, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        CopyWithRetries(targetPath, backupPath, log);
                        restored.Add((targetPath, backupPath));
                    }

                    CopyWithRetries(sourcePath, targetPath, log);
                }
            }
            catch
            {
                RollBack(restored, log);
                throw;
            }

            log.AppendLine($"Installed update into {options.TargetDirectory}.");
            TryRelaunch(options);

            // The payload and backup are only useful up to this point, and they
            // are the bulk of what staging holds.
            TryDeleteDirectory(options.SourceDirectory);
            TryDeleteDirectory(backupDirectory);
        }

        private static void WaitForApplicationExit(int? processId, StringBuilder log)
        {
            if (processId is not int pid)
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.WaitForExit(TimeSpan.FromSeconds(ExitWaitSeconds)))
                {
                    throw new TimeoutException("Tarkov Monitor did not close, so the update was not installed.");
                }
                log.AppendLine($"Process {pid} exited.");
            }
            catch (ArgumentException)
            {
                // The application had already exited before the updater looked
                // for it, which is the common case.
                log.AppendLine($"Process {pid} had already exited.");
            }
        }

        private static void CopyWithRetries(string sourcePath, string destinationPath, StringBuilder log)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                    return;
                }
                catch (Exception exception) when (attempt < CopyAttempts
                    && exception is IOException or UnauthorizedAccessException)
                {
                    // Anti-virus scanners and the just-closed process can hold
                    // a handle open for a few seconds after exit.
                    log.AppendLine($"Retrying {destinationPath} after: {exception.Message}");
                    Thread.Sleep(CopyRetryDelay);
                }
            }
        }

        private static void RollBack(List<(string TargetPath, string BackupPath)> restored, StringBuilder log)
        {
            log.AppendLine("Rolling back to the previous version.");
            foreach (var (targetPath, backupPath) in restored)
            {
                try
                {
                    File.Copy(backupPath, targetPath, overwrite: true);
                }
                catch (Exception exception)
                {
                    log.AppendLine($"Could not restore {targetPath}: {exception.Message}");
                }
            }
        }

        private static void TryRelaunch(ApplyOptions options)
        {
            if (options.LaunchPath == null || !File.Exists(options.LaunchPath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = options.LaunchPath,
                    WorkingDirectory = Path.GetDirectoryName(options.LaunchPath)!,
                    UseShellExecute = false,
                });
            }
            catch
            {
                // Nothing useful can be done from here; the user can start the
                // application from its shortcut.
            }
        }

        private static ApplyOptions ParseArguments(string[] args)
        {
            var options = new ApplyOptions();
            for (var index = 0; index < args.Length - 1; index++)
            {
                var value = args[index + 1];
                switch (args[index].ToLowerInvariant())
                {
                    case "--pid":
                        options.ProcessId = int.TryParse(value, out var pid) ? pid : null;
                        break;
                    case "--source":
                        options.SourceDirectory = value;
                        break;
                    case "--target":
                        options.TargetDirectory = value;
                        break;
                    case "--launch":
                        options.LaunchPath = value;
                        break;
                }
            }
            return options;
        }

        private static void WriteLog(ApplyOptions options, StringBuilder log)
        {
            if (options.SourceDirectory == null)
            {
                return;
            }

            try
            {
                var logDirectory = Path.GetDirectoryName(options.SourceDirectory);
                if (logDirectory == null)
                {
                    return;
                }
                Directory.CreateDirectory(logDirectory);
                File.WriteAllText(Path.Combine(logDirectory, "update.log"), log.ToString());
            }
            catch
            {
                // The log is a convenience for bug reports, never a reason to
                // fail an otherwise successful update.
            }
        }

        private static void TryDeleteDirectory(string directory)
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
                // Whatever is left behind is removed at the next startup.
            }
        }

        private sealed class ApplyOptions
        {
            public int? ProcessId { get; set; }
            public string? SourceDirectory { get; set; }
            public string? TargetDirectory { get; set; }
            public string? LaunchPath { get; set; }
        }
    }
}
