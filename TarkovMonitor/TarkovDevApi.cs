using System.Text;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;

namespace TarkovMonitor
{
    internal class TarkovDevApi
    {
        private static readonly GraphQLHttpClient client = new("https://api.tarkov.dev/graphql", new SystemTextJsonSerializer());
        private static readonly HttpClient httpClient = new();
        private static readonly System.Timers.Timer updateTimer = new() {
            AutoReset = true,
            Enabled = false, 
            Interval = TimeSpan.FromMinutes(20).TotalMilliseconds
        };

        public static List<Task> Tasks { get; private set; } = new();
        public static List<Map> Maps { get; private set; } = new();
        public static List<Item> Items { get; private set; } = new();

        public async static Task<List<Task>> GetTasks()
        {
            var request = new GraphQL.GraphQLRequest() {
                Query = @"
                    query TarkovMonitorTasks {
                        tasks {
                            id
                            name
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
                        }
                    }
                "
            };
            var response = await client.SendQueryAsync<ItemsResponse>(request);
            Items = response.Data.items;
            return Items;
        }

        public async static Task<string> PostQueueTime(string mapNameId, int queueTime, string type)
        {
            var queueApiUrl = "https://manager.tarkov.dev/api/queue";
            //var queueApiUrl = "http://localhost:4000/api/queue";
            var payload = $"{{\"map\":\"{mapNameId}\",\"time\":{queueTime}, \"type\": \"{type}\"}}";
            var request = new HttpRequestMessage(HttpMethod.Post, queueApiUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var response = await httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            try
            {
                response.EnsureSuccessStatusCode();
                return content;
            }
            catch (Exception ex)
            {
                throw new Exception($"{ex.Message}: {content}");
            }
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
        }

        public class TasksResponse
        {
            public List<Task> tasks { get; set; }
        }

        public class Task
        {
            public string id { get; set; }
            public string name { get; set; }
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
        }
    }
}
