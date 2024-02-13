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

            var splashTime = 2000;
			if (Properties.Settings.Default.skipSplash || Properties.Settings.Default.minimizeAtStartup)
			{
				splashTime = 1;
			}
			Application.Run(new Splash(TarkovMonitor.Properties.Resources.tarkov_dev_logo, splashTime));
			Application.Run(new MainBlazorUI());
        }
    }
}