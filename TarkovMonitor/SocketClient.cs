using System.Text.Json.Nodes;
using Websocket.Client;

namespace TarkovMonitor
{
    internal static class SocketClient
    {
        public static event EventHandler<ExceptionEventArgs>? ExceptionThrown;
        private static readonly string wsUrl = "wss://socket.tarkov.dev";
        //private static readonly string wsUrl = "ws://localhost:8080";
        //private static WebsocketClient? socket;

        public static async Task Send(List<JsonObject> messages)
        {
            var remoteid = Properties.Settings.Default.remoteId;
            if (remoteid == null || remoteid == "")
            {
                return;
            }
            using WebsocketClient socket = new(new Uri(wsUrl + $"?sessionid={remoteid}-tm"));
            /*socket.MessageReceived.Subscribe(msg => {
                if (msg.Text == null)
                {
                    return;
                }
                var message = JsonNode.Parse(msg.Text);
                if (message == null)
                {
                    return;
                }
                if (message["type"]?.ToString() == "ping")
                {
                    socket.Send(new JsonObject
                    {
                        ["type"] = "pong"
                    }.ToJsonString());
                }
            });*/
            await socket.Start();
            foreach (var message in messages)
            {
                message["sessionID"] = remoteid;
                await socket.SendInstant(message.ToJsonString());
            }
        }
        public static Task Send(JsonObject message)
        {
            return Send(new List<JsonObject> { message });
        }

        public static async Task UpdatePlayerPosition(PlayerPositionEventArgs e)
        {
            var map = TarkovDev.Maps.Find(m => m.nameId == e.RaidInfo.Map)?.normalizedName;
            if (map == null && e.RaidInfo.Map != null)
            {
                return;
            }
            var payload = GetPlayerPositionMessage(e);
            try
            {
                await Send(payload);
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(payload, new(ex, "updating player position"));
            }
        }

        public static async Task NavigateToMap(TarkovDev.Map map)
        {
            var payload = GetNavigateToMapMessage(map);
            try
            {
                await Send(payload);
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(payload, new(ex, $"navigating to map {map.name}"));
            }
        }

        public static JsonObject GetPlayerPositionMessage(PlayerPositionEventArgs e)
        {
            var map = TarkovDev.Maps.Find(m => m.nameId == e.RaidInfo.Map)?.normalizedName;
            if (map == null && e.RaidInfo.Map != null)
            {
                throw new Exception($"Map {e.RaidInfo.Map} not found");
            }
            return new JsonObject
            {
                ["type"] = "command",
                ["data"] = new JsonObject
                {
                    ["type"] = "playerPosition",
                    ["map"] = map,
                    ["position"] = new JsonObject
                    {
                        ["x"] = e.Position.X,
                        ["y"] = e.Position.Y,
                        ["z"] = e.Position.Z,
                    },
                    ["rotation"] = e.Rotation,
                }
            };
        }

        public static JsonObject GetNavigateToMapMessage(TarkovDev.Map map)
        {
            return new JsonObject
            {
                ["type"] = "command",
                ["data"] = new JsonObject
                {
                    ["type"] = "map",
                    ["value"] = map.normalizedName
                }
            };
        }
    }
}
