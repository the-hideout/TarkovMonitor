using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Websocket.Client;

namespace TarkovMonitor
{
    internal static class SocketClient
    {
        public static event EventHandler<ExceptionEventArgs>? ExceptionThrown;
        private static readonly string wsUrl = "wss://socket.tarkov.dev";
        //private static readonly string wsUrl = "ws://localhost:8080";
        private static WebsocketClient? socket;

        static SocketClient()
        {
            Properties.Settings.Default.PropertyChanged += SettingChanged;

            Application.ApplicationExit += Application_ApplicationExit;
        }

        private static void Application_ApplicationExit(object? sender, EventArgs e)
        {
            if (socket != null && socket.IsRunning)
            {
                socket.Stop(WebSocketCloseStatus.NormalClosure, "Application closed");
            }
        }

        private static string GetId(int length)
        {
            string result = "";
            string characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

            Random r = new Random();
            int rInt = r.Next(0, characters.Length);

            for (int i = 0; i < length; i++)
            {
                result += characters.Substring(r.Next(0, characters.Length), 1);
            }
            return result;
        }

        public static async Task Connect()
        {
            if (socket != null)
            {
                try
                {

                    if (socket.IsRunning)
                    {
                        await socket.Stop(WebSocketCloseStatus.NormalClosure, "Reconnecting");
                    }
                    socket.Dispose();
                } 
                catch (Exception)
                {
                    // don't error on stopping old client
                }
            }
            try {
                var remoteId = Properties.Settings.Default.remoteId;
                if (remoteId?.Length != 4)
                {
                    return;
                }
                //var source = new CancellationTokenSource();
                //source.CancelAfter(5000);
                socket = new(new Uri(wsUrl+$"?sessionid={remoteId}-tm"));
                socket.MessageReceived.Subscribe(msg => {
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
                });
                socket.DisconnectionHappened.Subscribe(disconnectionInfo =>
                {
                    if (disconnectionInfo.CloseStatus == WebSocketCloseStatus.NormalClosure)
                    {
                        return;
                    }
                    //ExceptionThrown?.Invoke(socket, new(new("Map remote control connection closed unexpectedly"), "running"));
                    Connect();
                });
                await socket.Start();
            }
            catch (Exception ex) {
                ExceptionThrown?.Invoke(null, new(ex, $"Connecting with id {Properties.Settings.Default.remoteId}"));
            }
            
        }

        private static void SettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "remoteId")
            {
                return;
            }
            Connect();
        }

        public static async Task Send(JsonObject message)
        {
            var remoteid = Properties.Settings.Default.remoteId;
            if (remoteid == null || remoteid == "")
            {
                return;
            }
            if (socket == null || !socket.IsRunning)
            {
                return;
            }
            message["sessionID"] = remoteid;
            await Task.Run(() => socket.Send(message.ToJsonString()));
        }

        public static async Task UpdatePlayerPosition(PlayerPositionEventArgs e)
        {
            var map = TarkovDev.Maps.Find(m => m.nameId == e.RaidInfo.Map)?.normalizedName;
            if (map == null && e.RaidInfo.Map != null)
            {
                return;
            }
            var payload = new JsonObject
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
                        ["z"] = e.Position.Z
                    }
                }
            };
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
            var payload = new JsonObject
            {
                ["type"] = "command",
                ["data"] = new JsonObject
                {
                    ["type"] = "map",
                    ["value"] = map.normalizedName
                }
            };
            try
            {
                await Send(payload);
            }
            catch (Exception ex)
            {
                ExceptionThrown?.Invoke(payload, new(ex, $"navigating to map {map.name}"));
            }
        }
    }
}
