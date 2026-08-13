using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using TarkovMonitor;

// https://stackoverflow.com/a/61332356
class Splash : Form
{
    private float _opacity = 1.0f;
    public Bitmap? BackgroundBitmap;

    private static readonly string[] _webview2RegKeys = new[]
    {
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        @"HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    };

    private System.Windows.Forms.Timer splashTimer = new System.Windows.Forms.Timer();
    private readonly DiagnosticsService diagnostics;
    private readonly bool waitForReadiness;
    private bool minimumTimeElapsed;
    private bool readyToClose;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new float Opacity
    {
        get
        {
            return _opacity;
        }
        set
        {
            _opacity = value;
            if (BackgroundBitmap != null)
            {
				SelectBitmap(BackgroundBitmap);
			}
        }
    }

    public Splash(Bitmap bitmap, int splashTime, DiagnosticsService? diagnostics = null, bool waitForReadiness = false)
    {
        this.diagnostics = diagnostics ?? new DiagnosticsService();
        this.waitForReadiness = waitForReadiness;
        // Window settings
        this.TopMost = true;
        this.ShowInTaskbar = false;
        // Keep the branding window at the logo's native size. The splash is
        // intentionally a separate window; it must not become a full-screen
        // black canvas or cover the application while the UI is starting.
        this.Size = bitmap.Size;
        this.StartPosition = FormStartPosition.Manual;
		// Must be called before the splash is shown.
		this.BackgroundBitmap = bitmap;
		this.SelectBitmap(BackgroundBitmap);
		this.BackColor = Color.Black;

		splashTimer = new System.Windows.Forms.Timer { Interval = splashTime };
		splashTimer.Tick += (sender, e) =>
		{
			minimumTimeElapsed = true;
			splashTimer.Stop();
			if (!waitForReadiness || readyToClose)
			{
				Close();
			}
		};
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Start the minimum-display timer only after the splash is actually
        // visible. Starting it in the constructor lets WebView startup work
        // consume part of the interval and can make the splash flash away.
        if (!minimumTimeElapsed && !splashTimer.Enabled)
        {
            splashTimer.Start();
        }
    }

    public void CloseWhenReady()
    {
        readyToClose = true;
        if (!minimumTimeElapsed || IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke((MethodInvoker)CloseWhenReady);
            return;
        }

        Close();
    }

    public void SetStartupLocation(Point location)
    {
        Location = location;
        if (BackgroundBitmap != null && IsHandleCreated)
        {
            // UpdateLayeredWindow receives the native location explicitly;
            // refresh it after the launcher places the separate splash window.
            SelectBitmap(BackgroundBitmap);
        }
    }

    public static void EnsureWebView2Runtime(DiagnosticsService diagnostics)
    {
        var existing = _webview2RegKeys.Any(key => Registry.GetValue(key, "pv", null) != null);
        if (!existing)
        {
            InstallWebview2Runtime(diagnostics);
        }
    }

    private static void InstallWebview2Runtime(DiagnosticsService diagnostics)
    {
        try
        {
            using var client = new WebClient();
            client.DownloadFile("https://go.microsoft.com/fwlink/p/?LinkId=2124703", "MicrosoftEdgeWebview2Setup.exe");

            var startInfo = new ProcessStartInfo();
            startInfo.CreateNoWindow = false;
            startInfo.UseShellExecute = false;
            startInfo.FileName = "MicrosoftEdgeWebview2Setup.exe";
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.Arguments = "/install";

            // Start the process with the info we specified.
            // Call WaitForExit and then the using statement will close.
            var exeProcess = Process.Start(startInfo);
            exeProcess?.WaitForExit();
        }
        catch (Exception ex)
        {
            diagnostics.Capture(
                new DiagnosticContext("TM-STARTUP-WEBVIEW-001", "InstallWebView2Runtime", "Startup", "WebView2", "The embedded web view runtime could not be installed."),
                ex);
        }

        try
        {
            File.Delete("MicrosoftEdgeWebview2Setup.exe");
        }
        catch { }
    }

    // Sets the current bitmap
    public void SelectBitmap(Bitmap bitmap)
    {
        // Does this bitmap contain an alpha channel?   
        if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
        {
            throw new ApplicationException("The bitmap must be 32bpp with alpha-channel.");
        }
        // Get device contexts   
        IntPtr screenDc = SplashAPIHelp.GetDC(IntPtr.Zero);
        IntPtr memDc = SplashAPIHelp.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOldBitmap = IntPtr.Zero;
        try
        {
            // Get handle to the new bitmap and select it into the current device context      
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            hOldBitmap = SplashAPIHelp.SelectObject(memDc, hBitmap);
            // Set parameters for layered window update      
            SplashAPIHelp.Size newSize = new SplashAPIHelp.Size(bitmap.Width, bitmap.Height);
            // Size window to match bitmap      
            SplashAPIHelp.Point sourceLocation = new SplashAPIHelp.Point(0, 0);
            SplashAPIHelp.Point newLocation = new SplashAPIHelp.Point(this.Left, this.Top);
            // Same as this window      
            SplashAPIHelp.BLENDFUNCTION blend = new SplashAPIHelp.BLENDFUNCTION();
            blend.BlendOp = SplashAPIHelp.AC_SRC_OVER;
            // Only works with a 32bpp bitmap      
            blend.BlendFlags = 0; // Always 0   
            blend.SourceConstantAlpha = (byte)(Opacity * 255); // Set to 255 for per-pixel alpha values
            blend.AlphaFormat = SplashAPIHelp.AC_SRC_ALPHA;
            // Only works when the bitmap contains an alpha channel      
            // Update the window      
            SplashAPIHelp.UpdateLayeredWindow(Handle, screenDc, ref newLocation, ref newSize, memDc, ref sourceLocation, 0, ref blend, SplashAPIHelp.ULW_ALPHA);
        }
        finally
        {
            // Release device context      
            SplashAPIHelp.ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SplashAPIHelp.SelectObject(memDc, hOldBitmap);
                SplashAPIHelp.DeleteObject(hBitmap);
                // Remove bitmap resources      
            }
            SplashAPIHelp.DeleteDC(memDc);
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // Add the layered extended style (WS_EX_LAYERED) to this window      
            CreateParams createParams = base.CreateParams;
            createParams.ExStyle |= SplashAPIHelp.WS_EX_LAYERED;
            return createParams;
        }
    }

    // Let Windows drag this window for us (thinks its hitting the title bar of the window)
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == SplashAPIHelp.WM_NCHITTEST)
        {
            // Tell Windows that the user is on the title bar (caption)      
            message.Result = (IntPtr)SplashAPIHelp.HTCAPTION;
        }
        else
        {
            base.WndProc(ref message);
        }
    }
}
