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
            var diagnostics = new DiagnosticsService();
            Application.ThreadException += (_, args) => diagnostics.Capture(
                new DiagnosticContext("TM-APP-001", "UnhandledUiException", "Application", "Thread", "The application encountered an unexpected UI failure."),
                args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception)
                {
                    diagnostics.Capture(
                        new DiagnosticContext("TM-APP-002", "UnhandledException", "Application", "Process", "The application encountered an unexpected failure."),
                        exception);
                }
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                diagnostics.Capture(
                    new DiagnosticContext("TM-APP-003", "UnobservedTaskException", "Application", "BackgroundTask", "A background task failed without an observed handler."),
                    args.Exception);
                args.SetObserved();
            };

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            var splashTime = 2000;
			if (Properties.Settings.Default.skipSplash || Properties.Settings.Default.minimizeAtStartup)
			{
				splashTime = 1;
			}
			Application.Run(new Splash(TarkovMonitor.Properties.Resources.tarkov_dev_logo, splashTime, diagnostics));
			Application.Run(new MainBlazorUI());
        }
    }
}
