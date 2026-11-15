using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;

namespace TarkovMonitor
{
    public class LocalizationService
    {
        private readonly IStringLocalizerFactory _localizerFactory;
        private readonly IStringLocalizer _localizer;
        private ResourceManager _resourceManager;

        public event EventHandler? LanguageChanged;

        public LocalizationService(IStringLocalizerFactory localizerFactory)
        {
            _localizerFactory = localizerFactory;
            _localizer = _localizerFactory.Create("Strings", "TarkovMonitor");
            _resourceManager = Properties.Strings.ResourceManager;
            
            SetCulture(Properties.Settings.Default.language);
        }

        public string GetString(string key)
        {
            try
            {
                var localizedString = _localizer[key];
                if (localizedString.ResourceNotFound)
                {
                    // Fallback to resource manager
                    return _resourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
                }
                return localizedString.Value;
            }
            catch
            {
                return key;
            }
        }

        public string GetString(string key, params object?[] args)
        {
            return string.Format(CultureInfo.CurrentUICulture, GetString(key), args);
        }

        // Static lookup for code without DI access (static classes, plain C# types).
        // Reads the same compiled resources as GetString; missing keys fall back to English.
        public static string T(string key, params object?[] args)
        {
            var value = Properties.Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;
            if (args.Length > 0)
            {
                return string.Format(CultureInfo.CurrentUICulture, value, args);
            }
            return value;
        }

        public void SetCulture(string cultureName)
        {
            try
            {
                var culture = new CultureInfo(cultureName);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                
                Properties.Settings.Default.language = cultureName;
                Properties.Settings.Default.Save();

                LanguageChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (CultureNotFoundException)
            {
                // Fallback to English if culture not found
                SetCulture("en");
            }
        }

        public string CurrentCulture => CultureInfo.CurrentUICulture.Name;

        public List<(string Code, string Name)> GetAvailableLanguages()
        {
            return new List<(string Code, string Name)>
            {
                ("de", GetString("German")),
                ("en", GetString("English")),
                ("es", GetString("Spanish")),
                ("fr", GetString("French")),
                ("pl", GetString("Polish")),
                ("pt", GetString("Portuguese")),
                ("ru", GetString("Russian")),
                ("zh", GetString("Chinese")),
            };
        }
    }
}
