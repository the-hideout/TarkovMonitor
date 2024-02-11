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
            if (!Properties.Settings.Default.skipSplash && !Properties.Settings.Default.minimizeAtStartup)
            {
                Application.Run(new Splash(TarkovMonitor.Properties.Resources.tarkov_dev_logo));
            }
            Application.Run(new MainBlazorUI());
        }
    }
}