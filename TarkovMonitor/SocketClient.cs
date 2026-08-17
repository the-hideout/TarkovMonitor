using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovMonitor
{
    internal static class SocketClient
    {
        // Background transport interruptions are telemetry only. The UI is
        // notified when a user-required send cannot complete, not when an
        // otherwise recoverable receive loop loses its peer.
        public static event EventHandler<SocketConnectionIncidentEventArgs>? ConnectionInterrupted;

        private const string wsUrl = "wss://socket.tarkov.dev";
        private const int ReceiveBufferSize = 4096;
        private const int MaxMessageBytes = 64 * 1024;
        private const int MaxReconnectBackoffSeconds = 60;
        private static readonly TimeSpan[] ReconnectBackoff =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(MaxReconnectBackoffSeconds),
        };

        // The production endpoint remains private so runtime configuration
        // cannot redirect credentials or telemetry to an arbitrary host.
        // The ignored sandbox replaces it by reflection in a separate process.
        private static Uri socketEndpoint = new(wsUrl);

        private static readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private static readonly SemaphoreSlim sendBatchGate = new(1, 1);
        private static readonly SemaphoreSlim socketSendGate = new(1, 1);
        private static readonly object stateLock = new();
        private static readonly System.Timers.Timer idleTimer = new()
        {
            AutoReset = false,
            Interval = TimeSpan.FromMinutes(30).TotalMilliseconds,
        };

        private static ConnectionState? currentState;
        private static SocketIncident? activeIncident;
        private static int nextGeneration;
        private static int connectFailureCount;
        private static DateTimeOffset nextConnectAllowedUtc = DateTimeOffset.MinValue;
        private static bool stopping;
        private static CancellationTokenSource? pendingConnectCancellation;

        static SocketClient()
        {
            idleTimer.Elapsed += async (_, _) => await CloseIdleAsync();
        }

        public static async Task StartClient()
        {
            lock (stateLock)
            {
                stopping = false;
            }

            await EnsureConnectedAsync(Properties.Settings.Default.remoteId ?? "");
        }

        public static Task VerifyClient()
        {
            return EnsureConnectedAsync(Properties.Settings.Default.remoteId ?? "");
        }

        public static async Task Send(List<JsonObject> messages)
        {
            var remoteId = Properties.Settings.Default.remoteId;
            if (string.IsNullOrWhiteSpace(remoteId) || messages.Count == 0)
            {
                return;
            }

            await sendBatchGate.WaitAsync().ConfigureAwait(false);
            ConnectionState? sendState = null;
            try
            {
                sendState = await EnsureConnectedAsync(remoteId).ConfigureAwait(false);

                // Each item is sent once. There is deliberately no pending
                // queue or replay after a partial batch failure: replaying an
                // item already accepted by the server could duplicate data.
                foreach (var message in messages)
                {
                    message["sessionID"] = remoteId;
                    await SendSocketMessageAsync(sendState, message).ConfigureAwait(false);
                }

                ResetIdleTimer();
                MarkRecovered();
            }
            catch (OperationCanceledException) when (IsStopping())
            {
                // Application shutdown is an expected transport cancellation.
            }
            catch (Exception exception)
            {
                var incidentId = MarkSendFailure(sendState, exception);
                throw new SocketSendException(incidentId, exception);
            }
            finally
            {
                sendBatchGate.Release();
            }
        }

        public static Task Send(JsonObject message)
        {
            return Send(new List<JsonObject> { message });
        }

        public static Task UpdatePlayerPosition(PlayerPositionEventArgs e)
        {
            if (e.RaidInfo.Map == null)
            {
                return Task.CompletedTask;
            }

            return Send(GetPlayerPositionMessage(e));
        }

        public static Task NavigateToMap(TarkovDev.Map map)
        {
            return Send(GetNavigateToMapMessage(map));
        }

        public static async Task StopAsync()
        {
            Task? receiveTask;
            ConnectionState? state;
            CancellationTokenSource? connectCancellation;

            // Signal shutdown before waiting for the lifecycle gate. A
            // ConnectAsync in progress owns that gate and must be cancelled
            // by StopAsync rather than forcing form-close to wait on the
            // platform's connection timeout.
            lock (stateLock)
            {
                stopping = true;
                connectCancellation = pendingConnectCancellation;
            }
            CancelPendingConnect(connectCancellation);

            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (stateLock)
                {
                    state = currentState;
                    connectCancellation = pendingConnectCancellation;
                    currentState = null;
                    if (state != null)
                    {
                        state.ExpectedClose = true;
                    }
                    receiveTask = state?.ReceiveTask;
                }

                idleTimer.Stop();
                CancelPendingConnect(connectCancellation);
                DisposeState(state);
            }
            finally
            {
                lifecycleGate.Release();
            }

            if (receiveTask == null)
            {
                return;
            }

            try
            {
                await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            }
            catch
            {
                // Shutdown must not create a second failure while the process
                // is closing.
            }
        }

        public static string GetEndpointForDiagnostics()
        {
            var builder = new UriBuilder(socketEndpoint)
            {
                Query = "",
            };
            return builder.Uri.ToString();
        }

        public static string? GetIncidentId(Exception exception)
        {
            return exception is SocketSendException sendException
                ? sendException.IncidentId
                : null;
        }

        private static async Task<ConnectionState> EnsureConnectedAsync(string remoteId)
        {
            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsStopping())
                {
                    throw new OperationCanceledException("The socket client is stopping.");
                }

                ConnectionState? oldState;
                DateTimeOffset retryAt;
                lock (stateLock)
                {
                    if (currentState is { } openState
                        && IsOpen(openState.Socket)
                        && !openState.ExpectedClose)
                    {
                        return openState;
                    }

                    oldState = currentState;
                    currentState = null;
                    if (oldState != null)
                    {
                        oldState.ExpectedClose = true;
                    }

                    retryAt = nextConnectAllowedUtc;
                }

                DisposeState(oldState);

                if (DateTimeOffset.UtcNow < retryAt)
                {
                    throw new SocketReconnectThrottledException(retryAt);
                }

                var client = new ClientWebSocket();
                var connectCancellation = new CancellationTokenSource();
                var endpoint = CreateSocketEndpoint(socketEndpoint, remoteId);
                var clientIdentity = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}/{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}";
                client.Options.SetRequestHeader("User-Agent", clientIdentity);
                client.Options.SetRequestHeader("origin", clientIdentity);

                var stopRequestedBeforeConnect = false;
                lock (stateLock)
                {
                    if (stopping)
                    {
                        stopRequestedBeforeConnect = true;
                    }
                    else
                    {
                        pendingConnectCancellation = connectCancellation;
                    }
                }

                if (stopRequestedBeforeConnect)
                {
                    client.Dispose();
                    connectCancellation.Dispose();
                    throw new OperationCanceledException("The socket client is stopping.");
                }

                try
                {
                    await client.ConnectAsync(endpoint, connectCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception) when (IsStopping())
                {
                    client.Dispose();
                    throw;
                }
                catch (Exception exception)
                {
                    client.Dispose();
                    RegisterConnectFailure(exception);
                    throw;
                }
                finally
                {
                    lock (stateLock)
                    {
                        if (ReferenceEquals(pendingConnectCancellation, connectCancellation))
                        {
                            pendingConnectCancellation = null;
                        }
                    }

                    connectCancellation.Dispose();
                }

                ConnectionState state;
                lock (stateLock)
                {
                    state = new ConnectionState(client, new CancellationTokenSource(), ++nextGeneration);
                    if (stopping)
                    {
                        state.ExpectedClose = true;
                    }
                    else
                    {
                        currentState = state;
                        nextConnectAllowedUtc = DateTimeOffset.MinValue;
                    }
                }

                if (state.ExpectedClose)
                {
                    DisposeState(state);
                    throw new OperationCanceledException("The socket client is stopping.");
                }

                state.ReceiveTask = ReceiveLoopAsync(state);
                ResetIdleTimer();
                return state;
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private static async Task ReceiveLoopAsync(ConnectionState state)
        {
            var buffer = new byte[ReceiveBufferSize];
            using var payload = new MemoryStream();

            try
            {
                while (IsCurrentOpenState(state))
                {
                    var result = await state.Socket.ReceiveAsync(buffer, state.Cancellation.Token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CompleteRemoteCloseAsync(state).ConfigureAwait(false);
                        MarkConnectionBroken(
                            state,
                            new WebSocketException("The remote WebSocket closed the connection."),
                            immediateReconnect: true,
                            operation: "receiving socket data",
                            notifyBackground: true);
                        break;
                    }

                    var discardMessage = result.MessageType != WebSocketMessageType.Text
                        || !TryAppendPayload(payload, buffer, result.Count);
                    while (!result.EndOfMessage)
                    {
                        result = await state.Socket.ReceiveAsync(buffer, state.Cancellation.Token).ConfigureAwait(false);
                        if (result.MessageType != WebSocketMessageType.Text
                            || !TryAppendPayload(payload, buffer, result.Count))
                        {
                            discardMessage = true;
                        }
                    }

                    if (!discardMessage
                        && !await ProcessServerMessageAsync(state, payload).ConfigureAwait(false))
                    {
                        break;
                    }

                    payload.SetLength(0);
                    payload.Position = 0;
                }
            }
            catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested || IsStopping())
            {
                // Explicit stop, idle close, or replacement of an old socket.
            }
            catch (WebSocketException exception) when (state.ExpectedClose || IsStopping())
            {
                // A close handshake can race disposal. It is still expected.
            }
            catch (Exception exception)
            {
                MarkConnectionBroken(
                    state,
                    exception,
                    immediateReconnect: true,
                    operation: "receiving socket data",
                    notifyBackground: true);
            }
            finally
            {
                CleanupCompletedState(state);
            }
        }

        private static bool TryAppendPayload(MemoryStream payload, byte[] buffer, int count)
        {
            if (count < 0 || payload.Length + count > MaxMessageBytes)
            {
                return false;
            }

            payload.Write(buffer, 0, count);
            return true;
        }

        private static async Task<bool> ProcessServerMessageAsync(ConnectionState state, MemoryStream payload)
        {
            if (payload.Length == 0)
            {
                return true;
            }

            try
            {
                var message = JsonNode.Parse(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
                if (message?["type"]?.ToString() != "ping")
                {
                    return true;
                }

                try
                {
                    await SendSocketMessageAsync(state, new JsonObject { ["type"] = "pong" }).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // A failed pong is a transport incident, not an unhandled
                    // receive-loop exception. Stop this stale loop cleanly.
                    MarkConnectionBroken(
                        state,
                        exception,
                        immediateReconnect: true,
                        operation: "sending socket pong",
                        notifyBackground: true);
                    return false;
                }
            }
            catch (JsonException)
            {
                // One malformed server record is isolated to that record. It
                // must not tear down the transport or freeze the UI.
            }

            return true;
        }

        private static async Task SendSocketMessageAsync(ConnectionState state, JsonNode payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            await socketSendGate.WaitAsync(state.Cancellation.Token).ConfigureAwait(false);
            try
            {
                if (!IsCurrentOpenState(state))
                {
                    throw new WebSocketException("The Tarkov.dev socket is no longer open.");
                }

                await state.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, state.Cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                socketSendGate.Release();
            }
        }

        private static async Task CompleteRemoteCloseAsync(ConnectionState state)
        {
            try
            {
                if (state.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // A peer that already closed (or a local idle-close where
                    // the peer is not reading) must not hold the lifecycle
                    // gate indefinitely waiting for a close handshake.
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await state.Socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        closeTimeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // The peer already closed or the OS released the connection.
            }
        }

        private static async Task CloseIdleAsync()
        {
            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ConnectionState? state;
                lock (stateLock)
                {
                    state = currentState;
                    currentState = null;
                    if (state != null)
                    {
                        state.ExpectedClose = true;
                    }
                }

                if (state == null)
                {
                    return;
                }

                await CompleteRemoteCloseAsync(state).ConfigureAwait(false);
                DisposeState(state);
            }
            catch
            {
                // Idle cleanup is intentionally silent. The next required
                // send will establish a fresh connection if necessary.
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private static string MarkSendFailure(ConnectionState? state, Exception exception)
        {
            if (state != null)
            {
                return MarkConnectionBroken(
                    state,
                    exception,
                    immediateReconnect: false,
                    operation: "sending socket data",
                    notifyBackground: false);
            }

            return EnsureIncident(exception, "connecting socket", notifyBackground: false);
        }

        private static string MarkConnectionBroken(
            ConnectionState? state,
            Exception exception,
            bool immediateReconnect,
            string operation,
            bool notifyBackground)
        {
            var incidentId = EnsureIncident(exception, operation, notifyBackground);
            var ownsState = false;

            lock (stateLock)
            {
                if (state != null && ReferenceEquals(currentState, state))
                {
                    currentState = null;
                    ownsState = true;
                    if (immediateReconnect)
                    {
                        nextConnectAllowedUtc = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        connectFailureCount = Math.Min(connectFailureCount + 1, ReconnectBackoff.Length - 1);
                        nextConnectAllowedUtc = DateTimeOffset.UtcNow + GetReconnectBackoff(connectFailureCount);
                    }
                }
            }

            if (ownsState)
            {
                DisposeState(state);
            }

            return incidentId;
        }

        private static string RegisterConnectFailure(Exception exception)
        {
            var incidentId = EnsureIncident(exception, "connecting socket", notifyBackground: false);
            lock (stateLock)
            {
                connectFailureCount = Math.Min(connectFailureCount + 1, ReconnectBackoff.Length - 1);
                nextConnectAllowedUtc = DateTimeOffset.UtcNow + GetReconnectBackoff(connectFailureCount);
            }

            return incidentId;
        }

        private static string EnsureIncident(Exception exception, string operation, bool notifyBackground)
        {
            SocketConnectionIncidentEventArgs? notification = null;
            string incidentId;
            lock (stateLock)
            {
                if (activeIncident == null)
                {
                    activeIncident = new SocketIncident(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
                    if (notifyBackground && !stopping)
                    {
                        notification = new SocketConnectionIncidentEventArgs(
                            activeIncident.Id,
                            operation,
                            exception,
                            GetEndpointForDiagnostics());
                    }
                }

                activeIncident.LastOperation = operation;
                activeIncident.LastException = exception;
                incidentId = activeIncident.Id;
            }

            if (notification != null)
            {
                RaiseConnectionInterrupted(notification);
            }

            return incidentId;
        }

        private static void RaiseConnectionInterrupted(SocketConnectionIncidentEventArgs args)
        {
            foreach (EventHandler<SocketConnectionIncidentEventArgs> handler in ConnectionInterrupted?.GetInvocationList()
                ?? Array.Empty<Delegate>())
            {
                try
                {
                    handler(null, args);
                }
                catch
                {
                    // Transport telemetry must never become a new application
                    // failure or interrupt socket cleanup.
                }
            }
        }

        private static TimeSpan GetReconnectBackoff(int failureCount)
        {
            var index = Math.Clamp(failureCount, 0, ReconnectBackoff.Length - 1);
            return ReconnectBackoff[index];
        }

        private static void MarkRecovered()
        {
            lock (stateLock)
            {
                activeIncident = null;
                connectFailureCount = 0;
                nextConnectAllowedUtc = DateTimeOffset.MinValue;
            }
        }

        private static bool IsCurrentOpenState(ConnectionState state)
        {
            lock (stateLock)
            {
                return currentState?.Generation == state.Generation
                    && ReferenceEquals(currentState, state)
                    && IsOpen(state.Socket)
                    && !state.ExpectedClose
                    && !stopping;
            }
        }

        private static bool IsOpen(ClientWebSocket client)
        {
            return client.State == WebSocketState.Open;
        }

        private static bool IsStopping()
        {
            lock (stateLock)
            {
                return stopping;
            }
        }

        private static void ResetIdleTimer()
        {
            idleTimer.Stop();
            if (!IsStopping())
            {
                idleTimer.Start();
            }
        }

        private static Uri CreateSocketEndpoint(Uri endpoint, string remoteId)
        {
            var builder = new UriBuilder(endpoint);
            var queryParts = builder.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.StartsWith("sessionid=", StringComparison.OrdinalIgnoreCase))
                .ToList();
            queryParts.Add($"sessionid={Uri.EscapeDataString(remoteId + "-tm")}");
            builder.Query = string.Join('&', queryParts);
            return builder.Uri;
        }

        private static void CancelPendingConnect(CancellationTokenSource? connectCancellation)
        {
            try
            {
                connectCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A completed connection may have disposed its cancellation
                // source between the snapshot and shutdown request.
            }
        }

        private static void CleanupCompletedState(ConnectionState state)
        {
            var wasCurrent = false;
            lock (stateLock)
            {
                if (ReferenceEquals(currentState, state))
                {
                    currentState = null;
                    wasCurrent = true;
                }
            }

            if (wasCurrent)
            {
                idleTimer.Stop();
            }

            DisposeState(state);
        }

        private static void DisposeState(ConnectionState? state)
        {
            if (state == null)
            {
                return;
            }

            try
            {
                state.Cancellation.Cancel();
            }
            catch
            {
            }

            if (Interlocked.Exchange(ref state.Disposed, 1) != 0)
            {
                return;
            }

            state.Socket.Dispose();
            state.Cancellation.Dispose();
        }

        public static JsonObject GetPlayerPositionMessage(PlayerPositionEventArgs e)
        {
            if (e.RaidInfo.Map == null)
            {
                throw new Exception("Map not found");
            }

            return new JsonObject
            {
                ["type"] = "command",
                ["data"] = new JsonObject
                {
                    ["type"] = "playerPosition",
                    ["map"] = e.RaidInfo.Map.normalizedName,
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

        private sealed class ConnectionState
        {
            public ClientWebSocket Socket { get; }
            public CancellationTokenSource Cancellation { get; }
            public int Generation { get; }
            public bool ExpectedClose { get; set; }
            public Task? ReceiveTask { get; set; }
            public int Disposed;

            public ConnectionState(ClientWebSocket socket, CancellationTokenSource cancellation, int generation)
            {
                Socket = socket;
                Cancellation = cancellation;
                Generation = generation;
            }
        }

        private sealed class SocketIncident
        {
            public SocketIncident(string id, DateTimeOffset startedUtc)
            {
                Id = id;
                StartedUtc = startedUtc;
            }

            public string Id { get; }
            public DateTimeOffset StartedUtc { get; }
            public string LastOperation { get; set; } = "";
            public Exception? LastException { get; set; }
        }

        private sealed class SocketReconnectThrottledException : IOException
        {
            public SocketReconnectThrottledException(DateTimeOffset retryAt)
                : base($"The Tarkov.dev connection is temporarily unavailable; retry after {retryAt:O}.")
            {
            }
        }

        private sealed class SocketSendException : IOException
        {
            public SocketSendException(string incidentId, Exception innerException)
                : base("The Tarkov.dev message could not be sent.", innerException)
            {
                IncidentId = incidentId;
            }

            public string IncidentId { get; }
        }
    }

    internal sealed class SocketConnectionIncidentEventArgs : EventArgs
    {
        public SocketConnectionIncidentEventArgs(string incidentId, string operation, Exception exception, string endpoint)
        {
            IncidentId = incidentId;
            Operation = operation;
            Exception = exception;
            Endpoint = endpoint;
        }

        public string IncidentId { get; }
        public string Operation { get; }
        public Exception Exception { get; }
        public string Endpoint { get; }
    }
}
