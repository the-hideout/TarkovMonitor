using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace TarkovMonitor
{
    internal class TarkovTracker
    {
        static readonly string apiUrl = "https://tarkovtracker.io/api/v1";
        static string token = "";
        static readonly HttpClient client = new();

        static OpenApiDocument? docs;

        public static event EventHandler<InitializedEventArgs> Initialized;

        public static async Task<OpenApiDocument> Init()
        {
            docs = new OpenApiStreamReader().Read(await client.GetStreamAsync("https://tarkovtracker.io/openapi.yaml"), out var diagnostic);
            Initialized?.Invoke(typeof(TarkovTracker), new InitializedEventArgs { Document = docs });
            return docs;
        }

        private static HttpRequestMessage GetRequest(string path, string apiToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl + path);
            request.Headers.Add("Authorization", "Bearer " + apiToken);
            return request;
        }

        private static HttpRequestMessage GetRequest(string path)
        {
            return GetRequest(path, token);
        }

        public static async Task<string> SetQuestComplete(int questId)
        {
            var path = "/progress/quest/{id}";
            var request = GetRequest(path.Replace("{id}", questId.ToString()));
            request.Method = HttpMethod.Post;
            var payload = @$"{{""complete"":true,""timeComplete"":{DateTime.Now.Ticks}}}";
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            Debug.WriteLine(await request.Content.ReadAsStringAsync());
            Debug.WriteLine(request.RequestUri.ToString());
            HttpResponseMessage response = await client.SendAsync(request);
            var code = ((int)response.StatusCode).ToString();
            try
            {
                response.EnsureSuccessStatusCode();
                return docs.Paths[path].Operations[OperationType.Post].Responses[code].Description;
            }
            catch (Exception ex)
            {   
                var responses = docs.Paths[path].Operations[OperationType.Post].Responses;
                if (responses.ContainsKey(code))
                {
                    throw new Exception(responses[code].Description);
                }
                throw new Exception($"Invalid response code ({code})");
            }
        }

        public static async Task<TokenResponse> TestTokenAsync(string apiToken)
        {
            var path = "/token";
            var request = GetRequest(path, apiToken);
            HttpResponseMessage response = client.Send(request);
            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                var code = ((int)response.StatusCode).ToString();
                var responses = docs.Paths[path].Operations[OperationType.Get].Responses;
                if (responses.ContainsKey(code))
                {
                    throw new Exception(responses[code].Description);
                }
                throw new Exception($"Invalid response code ({code})");
            }
            string responseBody = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<TokenResponse>(responseBody);
        }

        public static void SetToken(string apiToken)
        {
            token = apiToken;
        }

        public class InitializedEventArgs : EventArgs
        {
            public OpenApiDocument Document { get; set; }
        }

        public class TokenResponse
        {
            public Dictionary<string, int> createdAt { get; set; }
            public string[] permissions { get; set; }
            public string token { get; set; }
            public int calls { get; set; }
        }
    }
}
