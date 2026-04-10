using Newtonsoft.Json.Linq;
using Refit;

namespace TarkovMonitor
{
    internal class TarkovDev
    {
        private static readonly HttpClient client = new()
        {
            BaseAddress = new Uri("https://json.tarkov.dev"),
        };

        internal interface ITarkovDevAPI
        {
            [Post("/queue")]
            Task<DataSubmissionResponse> SubmitQueueTime([Body] QueueTimeBody body);
            [Post("/goons")]
            Task<DataSubmissionResponse> SubmitGoonsSighting([Body] GoonsBody body);
        }
        private static ITarkovDevAPI api = RestService.For<ITarkovDevAPI>("https://manager.tarkov.dev/api");

        internal interface ITarkovDevPlayersAPI
        {
            [Get("/name/{name}")]
            Task<List<PlayerSearchResult>> SearchName(string name);
            [Get("/account/{accountId}")]
            Task<PlayerProfileResult> GetProfile(int accountId);
        }
        private static ITarkovDevPlayersAPI playersApi = RestService.For<ITarkovDevPlayersAPI>("https://player.tarkov.dev");

        internal interface ITarkovDevPlayerJsonAPI
        {
            [Get("/profile/index.json")]
            Task<Dictionary<string, string>> GetPlayerNames();
        }
        private static ITarkovDevPlayerJsonAPI playerJsonApi = RestService.For<ITarkovDevPlayerJsonAPI>("https://players.tarkov.dev");

        private static readonly System.Timers.Timer updateTimer = new() {
            AutoReset = true,
            Enabled = false, 
            Interval = TimeSpan.FromMinutes(20).TotalMilliseconds
        };

        public static List<Task> Tasks { get; private set; } = new();
        public static List<Map> Maps { get; private set; } = new();
        public static List<Item> Items { get; private set; } = new();
        public static List<Trader> Traders { get; private set; } = new();
        public static List<HideoutStation> Stations { get; private set; } = new();
        public static List<PlayerLevel> PlayerLevels { get; private set; } = new();
        public static DateTime ScavAvailableTime { get; set; } = DateTime.Now;
        public static DateTime LastActivity { get; set; } = DateTime.MinValue;
        public static Dictionary<string, string> PlayerNames { get; private set; } = new();

        private static Dictionary<ProfileType, int> ScavCooldownBaseValues = new() {
            { ProfileType.Regular, 1500 },
            { ProfileType.PVE, 1500 },
        };

        static TarkovDev()
        {
            client.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}/{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        }

        private async static Task<JObject> GetJObject(string path) {
            var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            string responseBody = await response.Content.ReadAsStringAsync();
            return JObject.Parse(responseBody);
        }

        private async static Task<T> JsonApiRequest<T>(string path, string lang = null)
        {
            JObject data = null;
            Dictionary<string, string> langData = new();
            Dictionary<string, string> langDataFallback = new();
            var dataTask = GetJObject(path);
            var langDataTask = System.Threading.Tasks.Task.FromResult(new JObject());
            var langDataFallbackTask = System.Threading.Tasks.Task.FromResult(new JObject());
            if (lang != null)
            {
                langDataTask = GetJObject($"{path}_{lang}");
                if (lang != "en")
                {
                    langDataFallbackTask = GetJObject($"{path}_en");
                }
            }
            await System.Threading.Tasks.Task.WhenAll(dataTask, langDataTask, langDataFallbackTask);
            if (dataTask.IsFaulted)
            {
                throw dataTask.Exception.InnerException;
            }
            data = dataTask.Result;
            if (lang == null || !data.ContainsKey("translations"))
            {
                return data.ToObject<T>();
            }
            if (langDataTask.IsFaulted)
            {
                throw langDataTask.Exception.InnerException;
            }
            langData = langDataTask.Result.ToObject<LocalizationResponse>().data;
            if (lang != "en")
            {
                if (langDataFallbackTask.IsFaulted)
                {
                    throw langDataFallbackTask.Exception.InnerException;
                }
                langDataFallback = langDataFallbackTask.Result.ToObject<LocalizationResponse>().data;
            }
            foreach (var jPath in data["translations"].ToObject<string[]>())
            {
                foreach (JValue translationTarget in data.SelectTokens(jPath))
                {
                    var translatedValue = translationTarget.Value<string>();
                    if (langData.ContainsKey(translatedValue))
                    {
                        translatedValue = langData[translatedValue];
                    }
                    else if (langDataFallback.ContainsKey(translatedValue))
                    {
                        translatedValue = langDataFallback[translatedValue];
                    }
                    else
                    {
                        continue;
                    }
                    translationTarget.Value = translatedValue;
                }
            }
            return data.ToObject<T>();
        }

        public async static Task<List<Task>> GetTasks()
        {
            var response = await JsonApiRequest<TasksResponse>($"{GameWatcher.CurrentProfile.Type.ToString().ToLower()}/tasks", Properties.Settings.Default.language);
            Tasks = response.data.tasks.Values.ToList();
            return Tasks;
        }

        public async static Task<List<Map>> GetMaps()
        {
            var response = await JsonApiRequest<MapsResponse>($"{GameWatcher.CurrentProfile.Type.ToString().ToLower()}/maps", Properties.Settings.Default.language);
            Maps = response.data.maps.Values.ToList();
            return Maps;
        }
        public async static Task<List<Item>> GetItems()
        {
            var response = await JsonApiRequest<ItemsResponse>($"{GameWatcher.CurrentProfile.Type.ToString().ToLower()}/items", Properties.Settings.Default.language);
            Items = response.data.items.Values.ToList();
            foreach (var item in Items)
            {
                if (item.types.Contains("gun"))
                {
                    if (item.properties?.defaultPreset != null)
                    {
                        var defaultPreset = Items.Find(i => i.id == item.properties.defaultPreset);
                        if (defaultPreset == null)
                        {
                            continue;
                        }
                        item.width = defaultPreset.width;
                        item.height = defaultPreset.height;
                        item.iconLink = defaultPreset.iconLink;
                        item.gridImageLink = defaultPreset.gridImageLink;
                    }
                }
            }
            PlayerLevels = response.data.playerLevels;
            ScavCooldownBaseValues[GameWatcher.CurrentProfile.Type] = response.data.settings.scavCooldownSeconds;
            return Items;
        }
        public async static Task<List<Trader>> GetTraders()
        {
            var response = await JsonApiRequest<TradersResponse>($"{GameWatcher.CurrentProfile.Type.ToString().ToLower()}/traders", Properties.Settings.Default.language);
            Traders = response.data.Values.ToList();
            return Traders;
        }
        public async static Task<List<HideoutStation>> GetHideout()
        {
            var response = await JsonApiRequest<HideoutResponse>($"{GameWatcher.CurrentProfile.Type.ToString().ToLower()}/hideout", Properties.Settings.Default.language);
            Stations = response.data.Values.ToList();
            return Stations;
        }
        public async static System.Threading.Tasks.Task UpdateApiData()
        {
            List<System.Threading.Tasks.Task> tasks = new() { 
                GetTasks(),
                GetMaps(),
                GetItems(),
                GetTraders(),
                GetHideout(),
            };
            await System.Threading.Tasks.Task.WhenAll(tasks);
        }

        public async static Task<DataSubmissionResponse> PostQueueTime(string mapNameId, int queueTime, string type, ProfileType gameMode)
        {
            try
            {
                return await api.SubmitQueueTime(new QueueTimeBody() { map = mapNameId, time = queueTime, type = type, gameMode = gameMode.ToString().ToLower() });
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    throw new Exception($"Invalid Queue API response code ({ex.StatusCode}): {ex.Message}");
                }
                throw new Exception($"Queue API exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Queue API error: {ex.Message}");
            }
        }

        public async static Task<DataSubmissionResponse> PostGoonsSighting(string mapNameId, DateTime date, int accountId, ProfileType profileType)
        {
            try
            {
                return await api.SubmitGoonsSighting(new GoonsBody() { map = mapNameId, gameMode = profileType.ToString().ToLower(), timestamp = ((DateTimeOffset)date).ToUnixTimeMilliseconds(), accountId = accountId });
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    throw new Exception($"Invalid Goons API response code ({ex.StatusCode}): {ex.Content}");
                }
                throw new Exception($"Goons API exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Goons API error: {ex.Message}");
            }
        }

        public async static Task<Dictionary<string, string>> UpdatePlayerNames()
        {
            PlayerNames = await playerJsonApi.GetPlayerNames();
            return PlayerNames;
        }

        public static string GetPlayerName(Profile profile)
        {
            if (PlayerNames.ContainsKey(profile.AccountId))
            {
                return PlayerNames[profile.AccountId];
            }
            return profile.AccountId;
        }

        public async static Task<int> GetExperience(int accountId)
        {
            try
            {
                var profile = await playersApi.GetProfile(accountId);
                if (profile.err != null)
                {
                    throw new Exception(profile.errmsg);
                }
                if (profile?.Info == null)
                {
                    return 0;
                }
                return profile.Info.experience;
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    throw new Exception($"Invalid Players API response code ({ex.StatusCode}): {ex.Message}");
                }
                throw new Exception($"Players API exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Players API error: {ex.Message}");
            }
        }

        public static int GetLevel(int experience)
        {
            if (experience == 0)
            {
                return 0;
            }
            var totalExp = 0;
            for (var i = 0; i < PlayerLevels.Count; i++)
            {
                var levelData = PlayerLevels[i];
                totalExp += levelData.exp;
                if (totalExp == experience)
                {
                    return levelData.level;
                }
                if (totalExp > experience)
                {
                    return PlayerLevels[i - 1].level;
                }
            }
            return PlayerLevels[PlayerLevels.Count - 1].level;
        }

        public static void StartAutoUpdates()
        {
            updateTimer.Enabled = true;
            updateTimer.Elapsed += UpdateTimer_Elapsed;
        }

        private static void UpdateTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (DateTime.Now.Subtract(LastActivity).TotalMinutes > 5)
            {
                return;
            }
            UpdateApiData();
        }

        public class JsonApiResponse
        {
            public object data { get; set; }
            public List<string>? translations { get; set; }
        }

        public class LocalizationResponse : JsonApiResponse
        {
            public Dictionary<string, string> data { get; set; }
        }

        public class TasksResponse : JsonApiResponse
        {
            public TasksJsonContent data {  get; set; }
        }

        public class TasksJsonContent
        {
            public Dictionary<string, Task> tasks { get; set; }
        }

        public class Task
        {
            public string id { get; set; }
            public string name { get; set; }
            public string normalizedName { get; set; }
            public string? wikiLink { get; set; }
            public bool restartable { get; set; }
            public List<TaskFailCondition> failConditions { get; set; }
        }

        public class TaskFailCondition
        {
            public string? task { get; set; }
            public List<string>? status { get; set; }
        }

        public class MapsResponse : JsonApiResponse
        {
            public MapsJsonContent data { get; set; }
        }

        public class MapsJsonContent
        {
            public Dictionary<string, Map> maps { get; set; }
        }

        public class Map
        {
            public string id { get; set; }
            public string name { get; set; }
            public string nameId { get; set; }
            public string normalizedName { get; set; }
            public List<BossSpawn> bosses { get; set; }
            public bool HasGoons()
            {
                List<string> goons = new() { "bossKnight", "followerBigPipe", "followerBirdEye" };
                return bosses.Any(spawn => goons.Contains(spawn.mob) || spawn.escorts.Any(e => goons.Contains(e.mob)));
            }
        }
        public class BossEscort
        {
            public string mob { get; set; }
        }
        public class BossSpawn
        {
            public string mob { get; set; }
            public List<BossEscort> escorts { get; set; }
        }
        public class ItemsResponse : JsonApiResponse
        {
            public ItemsJsonContent data { get; set; }
        }

        public class ItemsJsonContent
        {
            public Dictionary<string, Item> items { get; set; }
            public List<PlayerLevel> playerLevels { get; set; }
            public GameSettings settings { get; set; }
        }
        public class Item
        {
            public string id { get; set; }
            public string name { get; set; }
            public int width { get; set; }
            public int height { get; set; }
			public string link { get; set; }
			public string iconLink { get; set; }
            public string gridImageLink { get; set; }
            public string image512pxLink { get; set; }
            public List<string> types { get; set; }
            public ItemProperties? properties { get; set; }
        }
        public class ItemProperties
        {
            public string? defaultPreset { get; set; }
        }

        public class GameSettings
        {
            public int scavCooldownSeconds { get; set; }
        }

        public class TradersResponse : JsonApiResponse
        {
            public Dictionary<string, Trader> data { get; set; }
        }
        public class Trader
        {
            public string id { get; set; }
            public string name { get; set; }
            public string normalizedName { get; set; }
            public List<TraderReputationLevel> reputationLevels { get; set; }
        }
        public class TraderReputationLevel
        {
            public int minimumReputation { get; set; }
            public decimal scavCooldownModifier { get; set; }
        }

        public class HideoutResponse : JsonApiResponse
        {
            public Dictionary<string, HideoutStation> data { get; set; }
        }
        public class HideoutStation
        {
            public string id { get; set; }
            public string name { get; set; }
            public string normalizedName { get; set; }
            public List<StationLevel> levels { get; set; }
        }
        public class StationLevel
        {
            public string id { get; set; }
            public int level { get; set; }
            public List<StationBonus> bonuses { get; set; }
        }
        public class StationBonus
        {
            public string type { get; set; }
            public string name { get; set; }
            public decimal value { get; set; }
        }
        public class PlayerLevel
        {
            public int level { get; set; }
            public int exp { get; set; }
        }

        public class QueueTimeBody
        {
            public string map { get; set; }
            public int time { get; set; }
            public string type { get; set; }
            public string gameMode { get; set; }
        }

        public class DataSubmissionResponse
        {
            public string status { get; set; }
        }

        public class GoonsBody
        {
            public string map { get; set; }
            public string gameMode { get; set; }
            public long timestamp { get; set; }
            public int accountId { get; set; }
        }

        public class PlayerApiResponse
        {
            public int? err { get; set; }
            public string? errmsg { get; set; }
        }

        public class PlayerSearchResult
        {
            public int aid { get; set; }
            public string name { get; set; }
        }

        public class PlayerProfileResult
        {
            public int? err { get; set; }
            public string? errmsg { get; set; }
            public PlayerProfileInfo? Info { get; set; }
        }
        public class PlayerProfileInfo
        {
            public int experience { get; set; }
        }

        public static int ScavCooldownSeconds()
        {
            decimal baseTimer = Convert.ToDecimal(ScavCooldownBaseValues[GameWatcher.CurrentProfile.Type]);

            decimal hideoutBonus = 0;
            foreach (var station in Stations)
            {
                foreach (var level in station.levels)
                {
                    var cooldownBonus = level.bonuses.Find(b => b.type == "ScavCooldownTimer");
                    if (cooldownBonus == null)
                    {
                        continue;
                    }
                    if (TarkovTracker.Progress == null)
                    {
                        continue;
                    }
                    var built = TarkovTracker.Progress.data.hideoutModulesProgress.Find(m => m.id == level.id && m.complete);
                    if (built == null)
                    {
                        continue;
                    }
                    hideoutBonus += Math.Abs(cooldownBonus.value);
                }
            }

            decimal karmaBonus = 1;
            foreach (var trader in Traders)
            {
                foreach (var repLevel in trader.reputationLevels)
                {
                    if (Properties.Settings.Default.scavKarma >= repLevel.minimumReputation)
                    {
                        karmaBonus = repLevel.scavCooldownModifier;
                    }
                }
            }

            decimal coolDown = baseTimer * karmaBonus;

            //System.Diagnostics.Debug.WriteLine($"{hideoutBonus} {karmaBonus} {coolDown}");
            return (int)Math.Round(coolDown - (coolDown * hideoutBonus));
        }

        public static int ResetScavCoolDown()
        {
            var cooldownSeconds = ScavCooldownSeconds();
            ScavAvailableTime = DateTime.Now.AddSeconds(cooldownSeconds);
            return cooldownSeconds;
        }
    }
}
