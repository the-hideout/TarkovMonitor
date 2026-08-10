namespace TarkovMonitor
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

			// Upgrade the previous version's user settings before any startup
			// preference is read. Otherwise a first launch after an update uses
			// the new defaults for the splash and startup window state, then only
			// restores the user's saved choices after the main window is created.
			if (Properties.Settings.Default.upgradeRequired)
			{
				Properties.Settings.Default.Upgrade();
				Properties.Settings.Default.upgradeRequired = false;
				Properties.Settings.Default.Save();
			}

			// Initialize the application behind the branding splash. The native
			// window stays hidden, but WebView2 and Blazor are allowed to render
			// immediately so the first revealed frame is complete.
			var splashTime = 1000;
			var splashEnabled = !Properties.Settings.Default.skipSplash && !Properties.Settings.Default.minimizeAtStartup;
			// Keep the native host hidden until the first complete Blazor render in
			// both modes. Skip mode has no branding window, but it should still
			// reveal the finished UI instead of exposing the temporary WebView
			// startup shell.
			var mainWindow = new MainBlazorUI(holdUntilSplashCompletes: true)
			{
				ShowInTaskbar = false
			};
			if (!splashEnabled)
			{
				mainWindow.UiReady += (_, _) => mainWindow.ReleaseSplashGate();
			}
			else
			{
				mainWindow.Shown += (_, _) =>
				{
					var startupSplash = new Splash(TarkovMonitor.Properties.Resources.tarkov_dev_logo, splashTime, waitForReadiness: true);
					var startupFallback = new System.Windows.Forms.Timer { Interval = 10000 };
					mainWindow.UiReady += (_, _) => startupSplash.CloseWhenReady();
					startupFallback.Tick += (_, _) =>
					{
						startupFallback.Stop();
						startupSplash.CloseWhenReady();
					};
					startupSplash.FormClosing += (_, _) =>
					{
						mainWindow.ReleaseSplashGate();
					};
					startupSplash.FormClosed += (_, _) => startupFallback.Dispose();
					var startupWorkArea = Screen.PrimaryScreen!.WorkingArea;
					var startupLeft = startupWorkArea.Left + Math.Max(0, (startupWorkArea.Width - startupSplash.Width) / 2);
					var startupTop = startupWorkArea.Top + Math.Max(0, (startupWorkArea.Height - startupSplash.Height) / 2);
					startupSplash.SetStartupLocation(new Point(startupLeft, startupTop));
					startupSplash.Show();
					if (mainWindow.IsUiReady)
					{
						startupSplash.CloseWhenReady();
					}
					startupFallback.Start();
				};
			}

			Application.Run(mainWindow);
        }
    }
}
