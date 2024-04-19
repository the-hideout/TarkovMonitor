using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;
using Refit;
using System.Security.Policy;
using static TarkovMonitor.TarkovTracker;

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
        public static DateTime ScaveAvailableTime { get; set; } = DateTime.Now;

        public async static Task<List<Task>> GetTasks()
        {
            var request = new GraphQL.GraphQLRequest() {
                Query = @"
                    query TarkovMonitorTasks {
                        tasks {
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
                "
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
                    query TarkovMonitorMaps {
                        maps {
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
                "
            };
            var response = await client.SendQueryAsync<MapsResponse>(request);
            Maps = response.Data.maps;
            return Maps;
        }
        public async static Task<List<Item>> GetItems()
        {
            var request = new GraphQL.GraphQLRequest()
            {
                Query = @"
                    query TarkovMonitorItems {
                        items {
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
                "
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
                    query TarkovMonitorTraders {
                        traders {
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
                "
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
                    query TarkovMonitorHideoutStations {
                        hideoutStations {
                            id
                            name
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
                "
            };
            var response = await client.SendQueryAsync<HideoutResponse>(request);
            Stations = response.Data.hideoutStations;
            return Stations;
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

        public async static Task<DataSubmissionResponse> PostQueueTime(string mapNameId, int queueTime, string type)
        {
            try
            {
                return await api.SubmitQueueTime(new QueueTimeBody() { map = mapNameId, time = queueTime, type = type });
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

        public async static Task<DataSubmissionResponse> PostGoonsSighting(string mapNameId, DateTime date, int accountId)
        {
            try
            {
                return await api.SubmitGoonsSighting(new GoonsBody() { map = mapNameId, timestamp = ((DateTimeOffset)date).ToUnixTimeMilliseconds(), accountId = accountId });
            }
            catch (ApiException ex)
            {
                if (ex.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    throw new Exception($"Invalid Goons API response code ({ex.StatusCode}): {ex.Message}");
                }
                throw new Exception($"Goons API exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"Goons API error: {ex.Message}");
            }
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
            GetTasks();
            GetMaps();
            GetItems();
            GetTraders();
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
        }

        public class DataSubmissionResponse
        {
            public string status { get; set; }
        }

        public class GoonsBody
        {
            public string map { get; set; }
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
            decimal baseTimer = 1500;

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
            ScaveAvailableTime = DateTime.Now.AddSeconds(cooldownSeconds);
            return cooldownSeconds;
        }
    }
}
