using Newtonsoft.Json.Linq;
using Refit;
using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace TarkovMonitor
{
    public class TarkovDev
    {
        public static event EventHandler<ExceptionEventArgs>? ExceptionThrown;
        public static event EventHandler? ApiDataLoaded;
        private static readonly object playerNameCacheLock = new();
        private static readonly ConcurrentDictionary<(ProfileType ProfileType, string AccountId), Lazy<Task<string>>> playerNameLookups = new();
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
        private static readonly object updateTimerLock = new();
        private static bool updateTimerHandlerAttached;

        internal sealed class ApiDataSnapshot
        {
            internal ApiDataSnapshot(
                ProfileType profileType,
                List<Task> tasks,
                List<Map> maps,
                List<Item> items,
                List<Trader> traders,
                List<HideoutStation> stations,
                List<PlayerLevel> playerLevels,
                int scavCooldownSeconds)
            {
                ProfileType = profileType;
                Tasks = tasks;
                Maps = maps;
                Items = items;
                Traders = traders;
                Stations = stations;
                PlayerLevels = playerLevels;
                ScavCooldownSeconds = scavCooldownSeconds;
            }

            internal ProfileType ProfileType { get; }
            internal List<Task> Tasks { get; }
            internal List<Map> Maps { get; }
            internal List<Item> Items { get; }
            internal List<Trader> Traders { get; }
            internal List<HideoutStation> Stations { get; }
            internal List<PlayerLevel> PlayerLevels { get; }
            internal int ScavCooldownSeconds { get; }

            internal static ApiDataSnapshot Empty => new(
                ProfileType.Unknown,
                new(),
                new(),
                new(),
                new(),
                new(),
                new(),
                1500);
        }

        private sealed class ItemsData
        {
            internal ItemsData(List<Item> items, List<PlayerLevel> playerLevels, int scavCooldownSeconds)
            {
                Items = items;
                PlayerLevels = playerLevels;
                ScavCooldownSeconds = scavCooldownSeconds;
            }

            internal List<Item> Items { get; }
            internal List<PlayerLevel> PlayerLevels { get; }
            internal int ScavCooldownSeconds { get; }
        }

        private static ApiDataSnapshot apiData = ApiDataSnapshot.Empty;
        private static Profile? lastLoadedProfile;
        private static ApiDataSnapshot CurrentApiData => Volatile.Read(ref apiData);

        public static List<Task> Tasks => CurrentApiData.Tasks;
        public static List<Map> Maps => CurrentApiData.Maps;
        public static List<Item> Items => CurrentApiData.Items;
        public static List<Trader> Traders => CurrentApiData.Traders;
        public static List<HideoutStation> Stations => CurrentApiData.Stations;
        public static List<PlayerLevel> PlayerLevels => CurrentApiData.PlayerLevels;
        public static ProfileType LoadedProfileType => CurrentApiData.ProfileType;
        public static Profile? LastLoadedProfile => Volatile.Read(ref lastLoadedProfile)?.Snapshot();
        public static DateTime ScavAvailableTime { get; set; } = DateTime.Now;
        public static DateTime LastActivity { get; set; } = DateTime.MinValue;
        public static Dictionary<ProfileType, Dictionary<string, string>> PlayerNames { get; private set; } = new();

        static TarkovDev()
        {
            foreach (ProfileType profileType in Enum.GetValues<ProfileType>())
            {
                PlayerNames.Add(profileType, new());
            }
        }

        private static readonly JsonSerializerSettings jsonSerializerSettings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
        };

        private async static Task<T> GetJson<T>(string path, CancellationToken cancellationToken = default)
        {
            using var response = await jsonClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(reader);
            var serializer = JsonSerializer.Create(jsonSerializerSettings);
            return serializer.Deserialize<T>(jsonReader)
                ?? throw new InvalidDataException($"The tarkov.dev response for '{path}' was empty.");
        }

        private async static Task<T> JsonApiRequest<T>(string path, string? lang = null, CancellationToken cancellationToken = default) where T : class
        {
            if (string.IsNullOrWhiteSpace(lang))
            {
                lang = null;
            }

            var dataTask = GetJson<JsonApiEnvelope<T>>(path, cancellationToken);
            if (lang == null)
            {
                var response = await dataTask;
                return RequireApiData(response.data, path);
            }

            var langDataTask = GetJson<JsonApiEnvelope<Dictionary<string, string>>>($"{path}_{lang}", cancellationToken);
            var langDataFallbackTask = lang != "en"
                ? GetJson<JsonApiEnvelope<Dictionary<string, string>>>($"{path}_en", cancellationToken)
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
            return await GetTasks(GameWatcher.CurrentProfile.Type);
        }

        public async static Task<List<Task>> GetTasks(ProfileType profileType, CancellationToken cancellationToken = default)
        {
            var response = await JsonApiRequest<TasksResponse>($"{profileType.ToApiString()}/tasks", Properties.Settings.Default.language, cancellationToken);
            return response.tasks.Values.ToList();
        }

        public async static Task<List<Map>> GetMaps()
        {
            return await GetMaps(GameWatcher.CurrentProfile.Type);
        }

        public async static Task<List<Map>> GetMaps(ProfileType profileType, CancellationToken cancellationToken = default)
        {
            var response = await JsonApiRequest<MapsResponse>($"{profileType.ToApiString()}/maps", Properties.Settings.Default.language, cancellationToken);
            return response.maps.Values.ToList();
        }

        public async static Task<List<Item>> GetItems()
        {
            var data = await GetItemsData(GameWatcher.CurrentProfile.Type);
            return data.Items;
        }

        public async static Task<List<Item>> GetItems(ProfileType profileType, CancellationToken cancellationToken = default)
        {
            var data = await GetItemsData(profileType, cancellationToken);
            return data.Items;
        }

        private async static Task<ItemsData> GetItemsData(ProfileType profileType, CancellationToken cancellationToken = default)
        {
            var response = await JsonApiRequest<ItemsResponse>($"{profileType.ToApiString()}/items", Properties.Settings.Default.language, cancellationToken);
            var items = response.items.Values.ToList();
            foreach (var item in items)
            {
                if (item.types?.Contains("gun") == true)
                {
                    if (item.properties?.defaultPreset != null)
                    {
                        var defaultPreset = items.Find(i => i.id == item.properties.defaultPreset);
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
            return new ItemsData(items, response.playerLevels, response.settings.scavCooldownSeconds);
        }

        public async static Task<List<Trader>> GetTraders()
        {
            return await GetTraders(GameWatcher.CurrentProfile.Type);
        }

        public async static Task<List<Trader>> GetTraders(ProfileType profileType, CancellationToken cancellationToken = default)
        {
            var response = await JsonApiRequest<Dictionary<string, Trader>>($"{profileType.ToApiString()}/traders", Properties.Settings.Default.language, cancellationToken);
            return response.Values.ToList();
        }

        public async static Task<List<HideoutStation>> GetHideout()
        {
            return await GetHideout(GameWatcher.CurrentProfile.Type);
        }

        public async static Task<List<HideoutStation>> GetHideout(ProfileType profileType, CancellationToken cancellationToken = default)
        {
            var response = await JsonApiRequest<Dictionary<string, HideoutStation>>($"{profileType.ToApiString()}/hideout", Properties.Settings.Default.language, cancellationToken);
            return response.Values.ToList();
        }

        internal async static System.Threading.Tasks.Task<ApiDataSnapshot> LoadApiData(ProfileType profileType, CancellationToken cancellationToken = default)
        {
            if (profileType == ProfileType.Unknown)
            {
                throw new InvalidOperationException("Tarkov.dev data cannot be loaded for an unknown EFT session.");
            }

            var tasksTask = GetTasks(profileType, cancellationToken);
            var mapsTask = GetMaps(profileType, cancellationToken);
            var itemsTask = GetItemsData(profileType, cancellationToken);
            var tradersTask = GetTraders(profileType, cancellationToken);
            var hideoutTask = GetHideout(profileType, cancellationToken);
            await System.Threading.Tasks.Task.WhenAll(tasksTask, mapsTask, itemsTask, tradersTask, hideoutTask);
            var items = await itemsTask;
            return new ApiDataSnapshot(
                profileType,
                await tasksTask,
                await mapsTask,
                items.Items,
                await tradersTask,
                await hideoutTask,
                items.PlayerLevels,
                items.ScavCooldownSeconds);
        }

        internal static void PublishApiData(ApiDataSnapshot snapshot, Profile? loadedProfile = null)
        {
            Volatile.Write(ref apiData, snapshot);
            if (loadedProfile != null)
            {
                Volatile.Write(ref lastLoadedProfile, loadedProfile.Snapshot());
            }
            ApiDataLoaded?.Invoke(null, EventArgs.Empty);
        }

        internal static void ClearApiData()
        {
            Volatile.Write(ref apiData, ApiDataSnapshot.Empty);
        }

        public async static System.Threading.Tasks.Task<bool> UpdateApiData(ProfileType profileType, CancellationToken cancellationToken = default)
        {
            if (profileType == ProfileType.Unknown)
            {
                return false;
            }

            var snapshot = await LoadApiData(profileType, cancellationToken);
            var currentProfile = GameWatcher.CurrentProfile.Snapshot();
            if (!currentProfile.HasTarkovDevPlayerRoute
                || currentProfile.Type != profileType)
            {
                return false;
            }

            PublishApiData(snapshot, currentProfile);
            return true;
        }

        private async static System.Threading.Tasks.Task<bool> UpdateApiData(Profile expectedProfile, CancellationToken cancellationToken = default)
        {
            if (!expectedProfile.HasTarkovDevPlayerRoute
                || expectedProfile.Type == ProfileType.Unknown)
            {
                return false;
            }

            var snapshot = await LoadApiData(expectedProfile.Type, cancellationToken);
            var currentProfile = GameWatcher.CurrentProfile.Snapshot();
            if (!currentProfile.HasTarkovDevPlayerRoute
                || currentProfile.Type != expectedProfile.Type
                || currentProfile.SessionMode != expectedProfile.SessionMode
                || !string.Equals(currentProfile.AccountId, expectedProfile.AccountId, StringComparison.Ordinal)
                || !string.Equals(currentProfile.Id, expectedProfile.Id, StringComparison.Ordinal))
            {
                return false;
            }

            PublishApiData(snapshot, expectedProfile);
            return true;
        }

        public async static System.Threading.Tasks.Task UpdateApiData()
        {
            await UpdateApiData(GameWatcher.CurrentProfile.Type);
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

        public static async System.Threading.Tasks.Task<string> GetPlayerName(Profile profile)
        {
            if (profile.Type == ProfileType.Unknown || string.IsNullOrWhiteSpace(profile.AccountId))
            {
                return profile.AccountId;
            }

            lock (playerNameCacheLock)
            {
                if (PlayerNames[profile.Type].TryGetValue(profile.AccountId, out var cachedName))
                {
                    return cachedName;
                }
            }

            var lookupKey = (profile.Type, profile.AccountId);
            var lookup = playerNameLookups.GetOrAdd(
                lookupKey,
                static key => new Lazy<Task<string>>(
                    () => LoadPlayerName(key.ProfileType, key.AccountId),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return await lookup.Value;
            }
            finally
            {
                var pair = new KeyValuePair<(ProfileType ProfileType, string AccountId), Lazy<Task<string>>>(lookupKey, lookup);
                ((ICollection<KeyValuePair<(ProfileType ProfileType, string AccountId), Lazy<Task<string>>>>)playerNameLookups).Remove(pair);
            }
        }

        private static async Task<string> LoadPlayerName(ProfileType profileType, string accountId)
        {
            var startedUtc = DateTime.UtcNow;
            try
            {
                var p = await playerJsonApi.GetPlayerProfile(profileType.ToPlayersApiString(), accountId);
                var nickname = p.info.nickname;
                lock (playerNameCacheLock)
                {
                    if (!PlayerNames[profileType].TryGetValue(accountId, out var cachedName))
                    {
                        PlayerNames[profileType][accountId] = nickname;
                        cachedName = nickname;
                    }
                    return cachedName;
                }
            }
            catch (Refit.ApiException ex)
            {
                // A missing public profile is expected and should use the account ID.
                if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return accountId;
                }
                ExceptionThrown?.Invoke(null, new ExceptionEventArgs(
                    ex,
                    "player profile lookup",
                    "https://players.tarkov.dev",
                    DiagnosticsService.ElapsedMilliseconds(startedUtc)));
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(null, new ExceptionEventArgs(
                    ex,
                    "player profile lookup",
                    "https://players.tarkov.dev",
                    DiagnosticsService.ElapsedMilliseconds(startedUtc)));
            }
            return accountId;
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
            lock (updateTimerLock)
            {
                if (!updateTimerHandlerAttached)
                {
                    updateTimer.Elapsed += UpdateTimer_Elapsed;
                    updateTimerHandlerAttached = true;
                }
                updateTimer.Enabled = true;
            }
        }

        public static void StopAutoUpdates()
        {
            lock (updateTimerLock)
            {
                updateTimer.Enabled = false;
            }
        }

        private static async void UpdateTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (DateTime.Now.Subtract(LastActivity).TotalMinutes > 5)
            {
                return;
            }
            var profile = GameWatcher.CurrentProfile.Snapshot();
            if (!profile.HasTarkovDevPlayerRoute || profile.Type == ProfileType.Unknown)
            {
                return;
            }
            var profileType = profile.Type;
            var startedUtc = DateTime.UtcNow;
            try
            {
                await UpdateApiData(profile);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (GameWatcher.CurrentProfile.Type != profileType
                    || GameWatcher.CurrentProfile.SessionMode != profile.SessionMode
                    || GameWatcher.CurrentProfile.AccountId != profile.AccountId
                    || GameWatcher.CurrentProfile.Id != profile.Id)
                {
                    return;
                }
                ExceptionThrown?.Invoke(null, new ExceptionEventArgs(
                    ex,
                    "auto-updating tarkov.dev data",
                    "https://json.tarkov.dev",
                    DiagnosticsService.ElapsedMilliseconds(startedUtc)));
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
            decimal baseTimer = Convert.ToDecimal(CurrentApiData.ScavCooldownSeconds);

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
