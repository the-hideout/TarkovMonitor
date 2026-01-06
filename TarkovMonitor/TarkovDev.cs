using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;
using Refit;

namespace TarkovMonitor
{
    internal class TarkovDev
    {
        private static readonly GraphQLHttpClient client = new("https://api.tarkov.dev/graphql", new SystemTextJsonSerializer());

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

        static TarkovDev()
        {
            client.HttpClient.DefaultRequestHeaders.UserAgent.TryParseAdd($"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name} {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        }

        public async static Task<List<Task>> GetTasks()
        {
            System.Diagnostics.Debug.WriteLine("GetTasks " + GameWatcher.CurrentProfile.Type.ToString().ToLower());
            var request = new GraphQL.GraphQLRequest() {
                Query = @"
                    query TarkovMonitorTasks($language: LanguageCode, $gm: GameMode) {
                        tasks(lang: $language, gameMode: $gm) {
                            id
                            name
                            normalizedName
                            wikiLink
                            restartable
                            failConditions {
                              ...on TaskObjectiveTaskStatus {
                                task {
                                  id
                                }
                                status
                              }
                            }
                        }
                    }
                ",
                Variables = new { language = Properties.Settings.Default.language, gm = GameWatcher.CurrentProfile.Type.ToString().ToLower() },
            };
            var response = await client.SendQueryAsync<TasksResponse>(request);
            Tasks = response.Data.tasks;
            return Tasks;
        }

        public async static Task<List<Map>> GetMaps()
        {
            var request = new GraphQL.GraphQLRequest()
            {
                Query = @"
                    query TarkovMonitorMaps($language: LanguageCode) {
                        maps(lang: $language) {
                            id
                            name
                            nameId
                            normalizedName
                            bosses {
                                boss {
                                    normalizedName
                                }
                                escorts {
                                    boss {
                                        normalizedName
                                    }
                                }
                            }
                        }
                    }
                ",
                Variables = new { language = Properties.Settings.Default.language },
            };
            var response = await client.SendQueryAsync<MapsResponse>(request);
            Maps = response.Data.maps;
            Maps.Sort((a, b) => a.name.CompareTo(b.name));
            return Maps;
        }
        public async static Task<List<Item>> GetItems()
        {
            var request = new GraphQL.GraphQLRequest()
            {
                Query = @"
                    query TarkovMonitorItems($language: LanguageCode) {
                        items(lang: $language) {
                            id
                            name
                            width
                            height
                            link
                            iconLink
                            gridImageLink
                            image512pxLink
                            types
                            properties {
                                ...on ItemPropertiesWeapon {
                                    defaultPreset { 
                                        iconLink 
                                        gridImageLink
                                        width
                                        height
                                    }
                                }
                            }
                        }
                    }
                ",
                Variables = new { language = Properties.Settings.Default.language },
            };
            var response = await client.SendQueryAsync<ItemsResponse>(request);
            Items = response.Data.items;
            foreach (var item in Items)
            {
                if (item.types.Contains("gun"))
                {
                    if (item.properties?.defaultPreset != null)
                    {
                        item.width = item.properties.defaultPreset.width;
                        item.height = item.properties.defaultPreset.height;
                        item.iconLink = item.properties.defaultPreset.iconLink;
                        item.gridImageLink = item.properties.defaultPreset.gridImageLink;
                    }
                }
            }
            return Items;
        }
        public async static Task<List<Trader>> GetTraders()
        {
            var request = new GraphQL.GraphQLRequest()
            {
                Query = @"
                    query TarkovMonitorTraders($language: LanguageCode) {
                        traders(lang: $language) {
                            id
                            name
                            normalizedName 
                            reputationLevels {
                                ...on TraderReputationLevelFence {
                                    minimumReputation
                                    scavCooldownModifier
                                }
                            }
                        }
                    }
                ",
                Variables = new { language = Properties.Settings.Default.language },
            };
            var response = await client.SendQueryAsync<TradersResponse>(request);
            Traders = response.Data.traders;
            return Traders;
        }
        public async static Task<List<HideoutStation>> GetHideout()
        {
            var request = new GraphQL.GraphQLRequest()
            {
                Query = @"
                    query TarkovMonitorHideoutStations($language: LanguageCode) {
                        hideoutStations(lang: $language) {
                            id
                            name
                            normalizedName
                            levels {
                                id
                                level
                                bonuses {
                                    ...on HideoutStationBonus {
                                        type
                                        name
                                        value
                                    }
                                }
                            }
                        }
                    }
                ",
                Variables = new { language = Properties.Settings.Default.language },
            };
            var response = await client.SendQueryAsync<HideoutResponse>(request);
            Stations = response.Data.hideoutStations;
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
        public async static Task<List<PlayerLevel>> GetPlayerLevels()
        {
            var request = new GraphQL.GraphQLRequest()
            {
                Query = @"
                    query TarkovMonitorPlayerLevels {
                        playerLevels {
                            level
                            exp
                        }
                    }
                "
            };
            var response = await client.SendQueryAsync<PlayerLevelsResponse>(request);
            PlayerLevels = response.Data.playerLevels;
            return PlayerLevels;
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

        public class TasksResponse
        {
            public List<Task> tasks { get; set; }
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

        public class TaskFragment
        {
            public string id { get; set; }
        }

        public class TaskFailCondition
        {
            public TaskFragment task { get; set; }
            public List<string> status { get; set; }
        }

        public class MapsResponse
        {
            public List<Map> maps { get; set; }
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
                List<string> goons = new() { "death-knight", "big-pipe", "birdeye" };
                return bosses.Any(b => goons.Contains(b.boss.normalizedName) || b.escorts.Any(e => goons.Contains(e.boss.normalizedName)));
            }
        }
        public class BossEscort
        {
            public Boss boss { get; set; }
        }
        public class BossSpawn
        {
            public Boss boss { get; set; }
            public List<BossEscort> escorts { get; set; }
        }
        public class Boss
        {
            public string normalizedName { get; set; }
        }
        public class ItemsResponse
        {
            public List<Item> items { get; set; }
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
            public ItemPropertiesDefaultPreset? defaultPreset { get; set; }
        }
        public class ItemPropertiesDefaultPreset
        {
            public string iconLink { get; set; }
            public string gridImageLink { get; set; }
            public int width { get; set; }
            public int height { get; set; }
        }

        public class TradersResponse
        {
            public List<Trader> traders { get; set; }
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

        public class HideoutResponse
        {
            public List<HideoutStation> hideoutStations { get; set; }
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

        public class PlayerLevelsResponse
        {
            public List<PlayerLevel> playerLevels { get; set; }
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
            Dictionary<ProfileType, int> ScavCooldownBaseValues = new() {
                { ProfileType.Regular, 1500 },
                { ProfileType.PVE, 1500 },
            };
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
