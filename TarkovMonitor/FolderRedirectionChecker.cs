using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public class FolderRedirectionChecker
{
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr pszPath);

    public static string GetActualFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            var knownFolder = GetKnownFolderGuid(folder);
            if (knownFolder != Guid.Empty)
            {
                if (SHGetKnownFolderPath(knownFolder, 0, IntPtr.Zero, out IntPtr path) == 0)
                {
                    var result = Marshal.PtrToStringUni(path);
                    Marshal.FreeCoTaskMem(path);
                    return result;
                }
            }
        }
        catch { }

        return Environment.GetFolderPath(folder);
    }

    private static Guid GetKnownFolderGuid(Environment.SpecialFolder folder)
    {
        return folder switch
        {
            Environment.SpecialFolder.Desktop => new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"),
            Environment.SpecialFolder.MyDocuments => new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
            Environment.SpecialFolder.MyPictures => new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"),
            _ => Guid.Empty
        };
    }

    public static bool IsFolderRedirectedToOneDrive(Environment.SpecialFolder folder)
    {
        var actualPath = GetActualFolderPath(folder);
        return actualPath.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
    }
}