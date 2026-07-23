using System.Runtime.InteropServices;

namespace TarkovMonitor
{
    /// <summary>
    /// Controls system media playback (play/pause) for background applications like Spotify, YouTube, etc.
    /// </summary>
    internal static class MediaController
    {
        private const int KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        /// <summary>
        /// Sends a media play/pause key event to toggle media playback
        /// </summary>
        public static void TogglePlayPause()
        {
            // Press the key
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            // Release the key
            keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>
        /// Pauses media playback by sending play/pause command
        /// Note: This toggles play/pause state. The media must be playing for this to pause it.
        /// </summary>
        public static void Pause()
        {
            TogglePlayPause();
        }

        /// <summary>
        /// Resumes media playback by sending play/pause command
        /// Note: This toggles play/pause state. The media must be paused for this to resume it.
        /// </summary>
        public static void Play()
        {
            TogglePlayPause();
        }
    }
}