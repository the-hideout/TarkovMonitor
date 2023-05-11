using System.Text;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;

namespace TarkovMonitor
{
    internal class TarkovDevApi
    {
        static readonly GraphQLHttpClient client = new("https://api.tarkov.dev/graphql", new SystemTextJsonSerializer());
        static readonly HttpClient httpClient = new();

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
            return response.Data.tasks;
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
            return response.Data.maps;
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
            return response.Data.items;
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
