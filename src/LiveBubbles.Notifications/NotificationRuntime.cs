using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Security.Credentials;
using Windows.Storage;

namespace LiveBubbles.Notifications
{
    internal static class NotificationRuntime
    {
        internal const string SocketPrefix = "LiveBubbles.Notifications.";
        internal static string SocketId { get { return SocketPrefix + NotificationBridge.Generation; } }

        internal static Uri ServerUri(bool websocket)
        {
            object raw;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue("ServerAddress", out raw)) throw new InvalidOperationException("Sign in first.");
            string root = (raw as string ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Sign in first.");
            if (!root.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !root.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) root = "http://" + root;
            if (root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) root = root.Substring(0, root.Length - 7);
            var credential = new PasswordVault().Retrieve("WpBlueBubbles.Server", "guid"); credential.RetrievePassword();
            string route = websocket ? "/socket.io/?EIO=4&transport=websocket&guid=" : "/api/v1/chat/query?guid=";
            return new Uri(root + route + Uri.EscapeDataString(credential.Password));
        }

        internal static async Task EnsureConnectedAsync(Guid taskId, string generation, CancellationToken token, bool forceReconnect = false)
        {
            if (!NotificationBridge.IsCurrent(generation)) return;
            using (await NotificationStorage.LockAsync("notification-connection", token))
            {
                if (!NotificationBridge.IsCurrent(generation)) return;
                if (!forceReconnect && SocketActivityInformation.AllSockets.ContainsKey(SocketId)) return;
                if (forceReconnect)
                {
                    SocketActivityInformation stale;
                    if (SocketActivityInformation.AllSockets.TryGetValue(SocketId, out stale) && stale.StreamSocket != null)
                    {
                        try { stale.StreamSocket.Dispose(); } catch { }
                    }
                }
                using (var connection = await BrokerConnection.ConnectAsync(ServerUri(true), taskId, token))
                {
                    if (!NotificationBridge.IsCurrent(generation)) return;
                    await connection.TransferAsync(SocketPrefix + generation);
                    NotificationBridge.SetStatus("Connected. Notifications are automatic.");
                }
            }
        }

        internal static async Task HandleSocketAsync(SocketActivityTriggerDetails details, Guid taskId, string generation, CancellationToken token)
        {
            if (details.SocketInformation == null) return;
            if (details.SocketInformation.StreamSocket == null)
            {
                if (NotificationBridge.IsCurrent(generation))
                {
                    await EnsureConnectedAsync(taskId, generation, token, true);
                    await ReconcileAsync(generation, token);
                }
                return;
            }
            bool reconnect = false;
            using (await NotificationStorage.LockAsync("notification-connection", token))
            {
                using (var connection = new BrokerConnection(details.SocketInformation.StreamSocket))
                {
                    if (!NotificationBridge.IsCurrent(generation) || details.SocketInformation.Id != SocketPrefix + generation) return;
                    if (details.Reason == SocketActivityTriggerReason.SocketClosed)
                    {
                        reconnect = true;
                    }
                    else if (details.Reason == SocketActivityTriggerReason.KeepAliveTimerExpired)
                    {
                        // A keep-alive callback is healthy broker activity. Send
                        // a WebSocket ping and return ownership; do not tear down
                        // the authenticated Engine.IO session.
                        await connection.Wire.SendPingAsync(token);
                        if (NotificationBridge.IsCurrent(generation)) await connection.TransferAsync(SocketPrefix + generation);
                    }
                    else if (details.Reason == SocketActivityTriggerReason.SocketActivity)
                    {
                        string packet;
                        using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            readDeadline.CancelAfter(TimeSpan.FromSeconds(6));
                            try { packet = await connection.Wire.ReadTextAsync(readDeadline.Token); }
                            catch { token.ThrowIfCancellationRequested(); packet = "1"; }
                        }
                        if (packet == null) { /* WebSocket ping/pong handled; return ownership promptly. */ }
                        else if (packet.StartsWith("2", StringComparison.Ordinal)) await connection.Wire.SendTextAsync("3" + packet.Substring(1), token);
                        else if (packet.StartsWith("42", StringComparison.Ordinal)) await ProcessEventAsync(packet.Substring(2), generation, token);
                        else if (packet == "1" || packet.StartsWith("41", StringComparison.Ordinal) || packet.StartsWith("44", StringComparison.Ordinal)) reconnect = true;
                        if (!reconnect && NotificationBridge.IsCurrent(generation)) await connection.TransferAsync(SocketPrefix + generation);
                    }
                    else
                    {
                        reconnect = true;
                    }
                }
            }
            if (reconnect && NotificationBridge.IsCurrent(generation))
            {
                await EnsureConnectedAsync(taskId, generation, token, true);
                await ReconcileAsync(generation, token);
            }
        }

        internal static async Task ProcessEventAsync(string payload, string generation, CancellationToken token)
        {
            JsonArray parts;
            if (!JsonArray.TryParse(payload, out parts) || parts.Count < 2 || parts[0].ValueType != JsonValueType.String || parts[0].GetString() != "new-message" || parts[1].ValueType != JsonValueType.Object) return;
            var message = parts[1].GetObject();
            // Reactions, receipts, and group maintenance are not new chat messages.
            if (NotificationStorage.Number(message, "itemType") != 0 || !string.IsNullOrEmpty(NotificationStorage.String(message, "associatedMessageGuid"))) return;
            foreach (var value in message.GetNamedArray("chats", new JsonArray()))
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var chat = value.GetObject();
                string title = NotificationStorage.String(chat, "displayName");
                if (string.IsNullOrWhiteSpace(title)) title = NotificationStorage.String(NotificationStorage.Object(message, "handle"), "address");
                await NotificationPresenter.ObserveAsync(message, NotificationStorage.String(chat, "guid"), title, generation, token);
            }
        }

        internal static async Task ReconcileAsync(string generation, CancellationToken token)
        {
            // Recovery checks metadata only. No attachments are downloaded or decoded.
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) })
            {
                for (int offset = 0; offset < 10000; offset += 100)
                {
                    if (!NotificationBridge.IsCurrent(generation)) return;
                    string body = "{\"with\":[\"lastmessage\"],\"limit\":100,\"offset\":" + offset + ",\"sort\":\"lastmessage\"}";
                    using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                    using (var response = await client.PostAsync(ServerUri(false), content, token))
                    {
                        response.EnsureSuccessStatusCode();
                        var data = JsonObject.Parse(await response.Content.ReadAsStringAsync()).GetNamedArray("data", new JsonArray());
                        bool allOld = true;
                        foreach (var value in data)
                        {
                            if (value.ValueType != JsonValueType.Object) continue;
                            var chat = value.GetObject(); var message = NotificationStorage.Object(chat, "lastMessage");
                            if (NotificationStorage.Number(message, "dateCreated") > NotificationBridge.EnabledSince) allOld = false;
                            if (NotificationStorage.Number(message, "itemType") != 0 || !string.IsNullOrEmpty(NotificationStorage.String(message, "associatedMessageGuid"))) continue;
                            await NotificationPresenter.ObserveAsync(message, NotificationStorage.String(chat, "guid"), NotificationStorage.String(chat, "displayName"), generation, token);
                        }
                        if (data.Count < 100 || allOld) break;
                    }
                }
            }
        }

        internal static async Task CloseAsync(CancellationToken token)
        {
            using (await NotificationStorage.LockAsync("notification-connection", token))
                foreach (var info in SocketActivityInformation.AllSockets.Where(pair => pair.Key.StartsWith(SocketPrefix, StringComparison.Ordinal)).Select(pair => pair.Value).ToList())
                {
                    if (info.StreamSocket == null) continue;
                    using (var socket = info.StreamSocket) { await socket.CancelIOAsync(); }
                }
        }
    }
}
