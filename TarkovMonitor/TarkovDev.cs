using Newtonsoft.Json.Linq;
using Refit;
using Newtonsoft.Json;

namespace TarkovMonitor
{
    public class TarkovDev
    {
        public static event EventHandler<ExceptionEventArgs>? ExceptionThrown;
        private static readonly HttpClient jsonClient = new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                | System.Net.DecompressionMethods.Deflate
                | System.Net.DecompressionMethods.Brotli,
        })
        {
            BaseAddress = new Uri("https://json.tarkov.dev"),
            DefaultRequestHeaders = {
                { "User-Agent", $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}/{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}" },
            }
        };

        private static readonly HttpClient managerClient = new HttpClient {
            BaseAddress = new Uri("https://manager.tarkov.dev/api"),
            DefaultRequestHeaders = { 
                { "User-Agent", $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}/{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}" },
            }
        };

        private static readonly HttpClient playersClient = new HttpClient
        {
            BaseAddress = new Uri("https://players.tarkov.dev"),
            DefaultRequestHeaders = {
                { "User-Agent", $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}/{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}" },
            }
        };

        internal interface ITarkovDevAPI
        {
            [Post("/queue")]
            Task<DataSubmissionResponse> SubmitQueueTime([Body] QueueTimeBody body);
            [Post("/goons")]
            Task<DataSubmissionResponse> SubmitGoonsSighting([Body] GoonsBody body);
        }
        private static ITarkovDevAPI api = RestService.For<ITarkovDevAPI>(managerClient);

        /*internal interface ITarkovDevPlayersAPI
        {
            [Get("/name/{name}")]
            Task<List<PlayerSearchResult>> SearchName(string name);
            [Get("/account/{accountId}")]
            Task<PlayerProfileResult> GetProfile(int accountId);
        }
        private static ITarkovDevPlayersAPI playersApi = RestService.For<ITarkovDevPlayersAPI>("https://player.tarkov.dev");*/

        internal interface ITarkovDevPlayerJsonAPI
        {
            [Get("/profile/index.json")]
            Task<Dictionary<string, string>> GetPlayerNames();

            [Get("/{gameMode}/{accountId}.json")]
            Task<PlayerProfileResult> GetPlayerProfile(string gameMode, string accountId);
        }
        private static ITarkovDevPlayerJsonAPI playerJsonApi = RestService.For<ITarkovDevPlayerJsonAPI>(playersClient);

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
        public static Dictionary<ProfileType, Dictionary<string, string>> PlayerNames { get; private set; } = new();

        private static Dictionary<ProfileType, int> ScavCooldownBaseValues = new();

        static TarkovDev()
        {
            foreach (ProfileType profileType in Enum.GetValues<ProfileType>())
            {
                ScavCooldownBaseValues[profileType] = 1500;
                PlayerNames.Add(profileType, new());
            }
        }

        private static readonly JsonSerializerSettings jsonSerializerSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
        };

        private async static Task<T> GetJson<T>(string path)
        {
            using var response = await jsonClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(reader);
            var serializer = JsonSerializer.Create(jsonSerializerSettings);
            return serializer.Deserialize<T>(jsonReader)
                ?? throw new InvalidDataException($"The tarkov.dev response for '{path}' was empty.");
        }

        private async static Task<T> JsonApiRequest<T>(string path, string? lang = null) where T : class
        {
            if (string.IsNullOrWhiteSpace(lang))
            {
                lang = null;
            }

            var dataTask = GetJson<JsonApiEnvelope<T>>(path);
            if (lang == null)
            {
                var response = await dataTask;
                return RequireApiData(response.data, path);
            }

            var langDataTask = GetJson<JsonApiEnvelope<Dictionary<string, string>>>($"{path}_{lang}");
            var langDataFallbackTask = lang != "en"
                ? GetJson<JsonApiEnvelope<Dictionary<string, string>>>($"{path}_en")
                : System.Threading.Tasks.Task.FromResult<JsonApiEnvelope<Dictionary<string, string>>>(null!);
            await System.Threading.Tasks.Task.WhenAll(dataTask, langDataTask, langDataFallbackTask);

            var data = dataTask.Result;
            var baseData = RequireApiData(data.data, path);
            var langData = langDataTask.Result.data ?? new();
            var langDataFallback = langDataFallbackTask.Result?.data ?? new();
            return ApplyTranslations(baseData, data.translations, langData, langDataFallback);
        }

        internal static T ApplyTranslations<T>(T data, List<string>? paths,
            Dictionary<string, string> langData, Dictionary<string, string> langDataFallback)
            where T : class
        {
            if (data == null || paths == null || paths.Count == 0)
            {
                return data;
            }

            // Materialize only the already-projected model, not the full response.
            var projected = JObject.FromObject(data);
            foreach (var jPath in paths)
            {
                var projectedPath = jPath.StartsWith("$.data.", StringComparison.Ordinal)
                    ? "$." + jPath[7..]
                    : jPath;
                foreach (JValue translationTarget in projected.SelectTokens(projectedPath))
                {
                    var translatedValue = translationTarget.Value<string>();
                    if (string.IsNullOrWhiteSpace(translatedValue))
                    {
                        continue;
                    }
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
            return projected.ToObject<T>() ?? data;
        }

        public async static Task<List<Task>> GetTasks()
        {
            var response = await JsonApiRequest<TasksResponse>($"{GameWatcher.CurrentProfile.Type.ToApiString()}/tasks", Properties.Settings.Default.language);
            Tasks = response.tasks.Values.ToList();
            return Tasks;
        }

        public async static Task<List<Map>> GetMaps()
        {
            var response = await JsonApiRequest<MapsResponse>($"{GameWatcher.CurrentProfile.Type.ToApiString()}/maps", Properties.Settings.Default.language);
            Maps = response.maps.Values.ToList();
            return Maps;
        }
        public async static Task<List<Item>> GetItems()
        {
            var response = await JsonApiRequest<ItemsResponse>($"{GameWatcher.CurrentProfile.Type.ToApiString()}/items", Properties.Settings.Default.language);
            Items = response.items.Values.ToList();
            foreach (var item in Items)
            {
                if (item.types?.Contains("gun") == true)
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
            PlayerLevels = response.playerLevels;
            ScavCooldownBaseValues[GameWatcher.CurrentProfile.Type] = response.settings.scavCooldownSeconds;
            return Items;
        }
        public async static Task<List<Trader>> GetTraders()
        {
            var response = await JsonApiRequest<Dictionary<string, Trader>>($"{GameWatcher.CurrentProfile.Type.ToApiString()}/traders", Properties.Settings.Default.language);
            Traders = response.Values.ToList();
            return Traders;
        }
        public async static Task<List<HideoutStation>> GetHideout()
        {
            var response = await JsonApiRequest<Dictionary<string, HideoutStation>>($"{GameWatcher.CurrentProfile.Type.ToApiString()}/hideout", Properties.Settings.Default.language);
            Stations = response.Values.ToList();
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
                return await api.SubmitQueueTime(new QueueTimeBody() { map = mapNameId, time = queueTime, type = type, gameMode = gameMode.ToApiString() });
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    throw new Exception($"Invalid Queue API response code ({ex.StatusCode}).", ex);
                }
                throw new Exception("Queue API exception.", ex);
            }
            catch (Exception ex)
            {
                throw new Exception("Queue API error.", ex);
            }
        }

        public async static Task<DataSubmissionResponse> PostGoonsSighting(string mapNameId, DateTime date, int accountId, ProfileType profileType)
        {
            try
            {
                return await api.SubmitGoonsSighting(new GoonsBody() { map = mapNameId, gameMode = profileType.ToApiString(), timestamp = ((DateTimeOffset)date).ToUnixTimeMilliseconds(), accountId = accountId });
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    throw new Exception($"Invalid Goons API response code ({ex.StatusCode}).", ex);
                }
                throw new Exception("Goons API exception.", ex);
            }
            catch (Exception ex)
            {
                throw new Exception("Goons API error.", ex);
            }
        }

        public async static Task<string> GetPlayerName(Profile profile)
        {
            if (PlayerNames[profile.Type].ContainsKey(profile.AccountId))
            {
                return PlayerNames[profile.Type][profile.AccountId];
            }
            try
            {
                var p = await playerJsonApi.GetPlayerProfile(profile.Type.ToPlayersApiString(), profile.AccountId);
                PlayerNames[profile.Type].Add(profile.AccountId, p.info.nickname);
                return p.info.nickname;
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(null, new ExceptionEventArgs(ex, "player profile lookup"));
            }
            return profile.AccountId;
        }

        /*public async static Task<int> GetExperience(int accountId)
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
                    throw new Exception($"Invalid Players API response code ({ex.StatusCode}).", ex);
                }
                throw new Exception("Players API exception.", ex);
            }
            catch (Exception ex)
            {
                throw new Exception("Players API error.", ex);
            }
        }*/

        public static int GetLevel(int experience)
        {
            return GetLevel(PlayerLevels, experience);
        }

        internal static int GetLevel(IReadOnlyList<PlayerLevel> playerLevels, int experience)
        {
            if (experience == 0)
            {
                return 0;
            }

            if (playerLevels.Count == 0)
            {
                return 0;
            }

            var totalExp = 0;
            for (var i = 0; i < playerLevels.Count; i++)
            {
                var levelData = playerLevels[i];
                totalExp += levelData.exp;
                if (totalExp == experience)
                {
                    return levelData.level;
                }
                if (totalExp > experience)
                {
                    return i == 0 ? levelData.level : playerLevels[i - 1].level;
                }
            }
            return playerLevels[^1].level;
        }

        public static void StartAutoUpdates()
        {
            updateTimer.Enabled = true;
            updateTimer.Elapsed += UpdateTimer_Elapsed;
        }

        private static async void UpdateTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (DateTime.Now.Subtract(LastActivity).TotalMinutes > 5)
            {
                return;
            }
            try
            {
                await UpdateApiData();
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(null, new ExceptionEventArgs(ex, "auto-updating tarkov.dev data"));
            }
        }

        internal static T RequireApiData<T>(T? data, string path) where T : class
        {
            return data
                ?? throw new InvalidDataException($"The tarkov.dev response for '{path}' did not contain data.");
        }

        internal class JsonApiEnvelope<T>
        {
            public T? data { get; set; }
            public List<string>? translations { get; set; }
        }

        public class TasksResponse
        {
            public Dictionary<string, Task> tasks { get; set; } = new();
        }

        public class Task
        {
            public string id { get; set; }
            public string name { get; set; }
            public string normalizedName { get; set; }
            public string? wikiLink { get; set; }
            public bool restartable { get; set; }
            public List<TaskFailCondition> failConditions { get; set; } = new();
        }

        public class TaskFailCondition
        {
            public string? task { get; set; }
            public List<string>? status { get; set; }
        }

        public class MapsResponse
        {
            public Dictionary<string, Map> maps { get; set; } = new();
        }

        public class Map
        {
            public string id { get; set; }
            public string name { get; set; }
            public string nameId { get; set; }
            public string normalizedName { get; set; }
            public string scenePath { get; set; }
            public List<BossSpawn> bosses { get; set; } = new();
            public bool HasGoons()
            {
                List<string> goons = new() { "bossKnight", "followerBigPipe", "followerBirdEye" };
                return bosses.Any(spawn => goons.Contains(spawn.mob)
                    || (spawn.escorts?.Any(e => goons.Contains(e.mob)) == true));
            }
        }
        public class BossEscort
        {
            public string mob { get; set; }
        }
        public class BossSpawn
        {
            public string mob { get; set; }
            public List<BossEscort> escorts { get; set; } = new();
        }
        public class ItemsResponse
        {
            public Dictionary<string, Item> items { get; set; } = new();
            public List<PlayerLevel> playerLevels { get; set; } = new();
            public GameSettings settings { get; set; } = new();
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
            public List<string> types { get; set; } = new();
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

        public class Trader
        {
            public string id { get; set; }
            public string name { get; set; }
            public string normalizedName { get; set; }
            public List<TraderReputationLevel> reputationLevels { get; set; } = new();
        }
        public class TraderReputationLevel
        {
            public int minimumReputation { get; set; }
            public decimal scavCooldownModifier { get; set; }
        }

        public class HideoutStation
        {
            public string id { get; set; }
            public string name { get; set; }
            public string normalizedName { get; set; }
            public List<StationLevel> levels { get; set; } = new();
        }
        public class StationLevel
        {
            public string id { get; set; }
            public int level { get; set; }
            public List<StationBonus> bonuses { get; set; } = new();
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

        public class PlayerProfileResult
        {
            public int aid { get; set; }
            public PlayerProfileInfo info { get; set; }
        }
        public class PlayerProfileInfo
        {
            public string nickname { get; set; }
            public string side { get; set; }
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
