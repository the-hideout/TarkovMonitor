using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;

public class OneDriveBackupChecker
{
    public static string OneDrivePersonalFolder { 
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal");
            if (key == null)
            {
                return "";
            }
            var userFolder = key.GetValue("UserFolder") as string;
            return userFolder ?? "";
        }
    }
    public static List<string> GetOneDriveBackupFolders()
    {
        var backupFolders = new List<string>();

        try
        {
            // Check registry for OneDrive settings
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal"))
            {
                if (key != null)
                {
                    var userFolder = key.GetValue("UserFolder") as string;
                    if (!string.IsNullOrEmpty(userFolder))
                    {
                        // Common backup folders
                        var potentialFolders = new[]
                        {
                            //Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
                        };

                        foreach (var folder in potentialFolders)
                        {
                            if (IsFolderBackedUpToOneDrive(folder, userFolder))
                            {
                                backupFolders.Add(folder);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error accessing registry: {ex.Message}");
        }

        return backupFolders;
    }

    private static bool IsFolderBackedUpToOneDrive(string folderPath, string oneDriveFolder)
    {
        try
        {
            var realPath = GetRealPath(folderPath);
            System.Diagnostics.Debug.WriteLine($"folderPath: {folderPath}");
            System.Diagnostics.Debug.WriteLine($"oneDriveFolder: {oneDriveFolder}");
            System.Diagnostics.Debug.WriteLine($"realPath: {realPath}");
            return realPath.StartsWith(oneDriveFolder, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetRealPath(string path)
    {
        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path).FullName;
        }
        return path;
    }
}
