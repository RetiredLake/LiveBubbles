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
        private const string SocketIdKey = "LiveBubbles.Notifications.SocketId";
        private static Windows.Foundation.Collections.IPropertySet Values { get { return ApplicationData.Current.LocalSettings.Values; } }
        internal static string ContactNameKey(string address)
        {
            var normalized = NormalizeAddress(address);
            return string.IsNullOrWhiteSpace(normalized) ? string.Empty : NotificationBridgePrefix + "ContactName." + normalized;
        }

        private static string NotificationBridgePrefix { get { return "LiveBubbles.Notifications."; } }

        internal static string ContactName(string address)
        {
            var key = ContactNameKey(address);
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            object value;
            return Values.TryGetValue(key, out value) ? value as string ?? string.Empty : string.Empty;
        }

        private static string NormalizeAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var trimmed = value.Trim();
            if (trimmed.IndexOf('@') >= 0) return trimmed.ToLowerInvariant();
            var digits = new string(trimmed.Where(char.IsDigit).ToArray());
            if (digits.Length == 11 && digits.StartsWith("1", StringComparison.Ordinal)) return digits.Substring(1);
            if (digits.Length > 10) return digits.Substring(digits.Length - 10);
            return digits;
        }

        internal static string SocketId
        {
            get
            {
                string generation = NotificationBridge.Generation;
                object raw;
                string current = Values.TryGetValue(SocketIdKey, out raw) ? raw as string : null;
                string prefix = SocketPrefix + generation + ".";
                if (!string.IsNullOrEmpty(generation) && !string.IsNullOrEmpty(current) && current.StartsWith(prefix, StringComparison.Ordinal)) return current;
                // Keep accepting the pre-rotation ID so an upgrade can process
                // a socket that was already handed to Windows before this key
                // existed. The next reconnect rotates it before transferring.
                return SocketPrefix + generation;
            }
        }

        private static void RotateSocketId(string generation)
        {
            Values[SocketIdKey] = SocketPrefix + generation + "." + Guid.NewGuid().ToString("N");
        }

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
                string socketId = SocketId;
                SocketActivityInformation stale;
                bool hasEntry = TryGetBrokerSocket(socketId, out stale);
                StreamSocket existing = null;
                if (forceReconnect && hasEntry && stale != null)
                {
                    try { existing = stale.StreamSocket; }
                    catch (Exception) { existing = null; }
                }
                // Presence in AllSockets means the broker still owns this ID.
                // Do not read StreamSocket merely to test that state: reading it
                // reclaims ownership and would strand the socket on this path.
                if (!forceReconnect && hasEntry) return;
                if (hasEntry)
                {
                    if (existing != null) { try { existing.Dispose(); } catch { } }
                    // Never reuse an ID that Windows still knows about. A stale
                    // broker record can make TransferOwnership fail with
                    // E_ILLEGAL_METHOD_CALL even after its StreamSocket is closed.
                    RotateSocketId(generation);
                }
                else if (forceReconnect) RotateSocketId(generation);
                Uri serverUri;
                try { serverUri = ServerUri(true); }
                catch (Exception error) { throw new NotificationFailureException("server-settings", error); }
                using (var connection = await BrokerConnection.ConnectAsync(serverUri, taskId, token))
                {
                    if (!NotificationBridge.IsCurrent(generation)) return;
                    await connection.TransferAsync(SocketId, true);
                    NotificationBridge.SetStatus("Connected. Notifications are automatic.");
                }
            }
        }

        private static bool TryGetBrokerSocket(string id, out SocketActivityInformation information)
        {
            try { return SocketActivityInformation.AllSockets.TryGetValue(id, out information); }
            catch (Exception error) { throw new NotificationFailureException("socket-registry", error); }
        }

        internal static async Task HandleSocketAsync(SocketActivityTriggerDetails details, Guid taskId, string generation, CancellationToken token)
        {
            SocketActivityInformation information;
            try { information = details == null ? null : details.SocketInformation; }
            catch (Exception error) { await RecoverAfterBrokerMetadataFailureAsync(taskId, generation, token, "socket-details", error); return; }
            if (information == null) return;
            string socketId;
            try { socketId = information.Id ?? ""; }
            catch (Exception error) { await RecoverAfterBrokerMetadataFailureAsync(taskId, generation, token, "socket-id", error); return; }
            StreamSocket socket;
            try { socket = information.StreamSocket; }
            catch (Exception error) { await RecoverAfterBrokerMetadataFailureAsync(taskId, generation, token, "socket-reclaim", error); return; }
            SocketActivityTriggerReason reason;
            try { reason = details.Reason; }
            catch (Exception error) { throw new NotificationFailureException("socket-reason", error); }
            if (socket == null)
            {
                if (NotificationBridge.IsCurrent(generation) && socketId == SocketId)
                {
                    await EnsureConnectedAsync(taskId, generation, token, true);
                    await ReconcileAsync(generation, token);
                }
                return;
            }
            bool reconnect = false;
            using (await NotificationStorage.LockAsync("notification-connection", token))
            {
                using (var connection = new BrokerConnection(socket))
                {
                    if (!NotificationBridge.IsCurrent(generation) || socketId != SocketId) return;
                    if (reason == SocketActivityTriggerReason.SocketClosed)
                    {
                        reconnect = true;
                    }
                    else if (reason == SocketActivityTriggerReason.KeepAliveTimerExpired)
                    {
                        // A keep-alive callback is healthy broker activity. Send
                        // a WebSocket ping and return ownership; do not tear down
                        // the authenticated Engine.IO session.
                        await connection.Wire.SendPingAsync(token);
                        if (NotificationBridge.IsCurrent(generation))
                        {
                            connection.DetachStreams();
                            await connection.TransferAsync(SocketId, false);
                            NotificationBridge.MarkBackgroundActivity();
                        }
                    }
                    else if (reason == SocketActivityTriggerReason.SocketActivity)
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
                        if (!reconnect && NotificationBridge.IsCurrent(generation))
                        {
                            connection.DetachStreams();
                            await connection.TransferAsync(SocketId, false);
                            NotificationBridge.MarkBackgroundActivity();
                        }
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

        private static async Task RecoverAfterBrokerMetadataFailureAsync(Guid taskId, string generation, CancellationToken token, string stage, Exception original)
        {
            if (!NotificationBridge.IsCurrent(generation)) throw new NotificationFailureException(stage, original);
            try
            {
                await EnsureConnectedAsync(taskId, generation, token, true);
                await ReconcileAsync(generation, token);
            }
            catch (NotificationFailureException) { throw; }
            catch (Exception error) { throw new NotificationFailureException(stage + "-reconnect", error); }
        }

        internal static async Task ProcessEventAsync(string payload, string generation, CancellationToken token)
        {
            JsonArray parts;
            if (!JsonArray.TryParse(payload, out parts) || parts.Count < 2 || parts[0].ValueType != JsonValueType.String || parts[0].GetString() != "new-message" || parts[1].ValueType != JsonValueType.Object) return;
            var message = parts[1].GetObject();
            // Reactions, receipts, and group maintenance are not new chat messages.
            if (NotificationStorage.Number(message, "itemType") != 0 || !string.IsNullOrEmpty(NotificationStorage.String(message, "associatedMessageGuid"))) return;
            foreach (var value in NotificationStorage.Array(message, "chats"))
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var chat = value.GetObject();
                string title = ChatTitle(chat, message);
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
                    string body = "{\"with\":[\"lastmessage\",\"participants\"],\"limit\":100,\"offset\":" + offset + ",\"sort\":\"lastmessage\"}";
                    using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                    using (var response = await client.PostAsync(ServerUri(false), content, token))
                    {
                        response.EnsureSuccessStatusCode();
                        var data = NotificationStorage.Array(JsonObject.Parse(await response.Content.ReadAsStringAsync()), "data");
                        bool allOld = true;
                        foreach (var value in data)
                        {
                            if (value.ValueType != JsonValueType.Object) continue;
                            var chat = value.GetObject(); var message = NotificationStorage.Object(chat, "lastMessage");
                            if (NotificationStorage.Number(message, "dateCreated") > NotificationBridge.EnabledSince) allOld = false;
                            if (NotificationStorage.Number(message, "itemType") != 0 || !string.IsNullOrEmpty(NotificationStorage.String(message, "associatedMessageGuid"))) continue;
                            await NotificationPresenter.ObserveAsync(message, NotificationStorage.String(chat, "guid"), ChatTitle(chat, message), generation, token);
                        }
                        if (data.Count < 100 || allOld) break;
                    }
                }
            }
        }

        private static string ChatTitle(JsonObject chat, JsonObject message)
        {
            var title = NotificationStorage.String(chat, "displayName");
            var mapped = ContactName(title);
            if (!string.IsNullOrWhiteSpace(mapped)) title = mapped;
            if (!string.IsNullOrWhiteSpace(title) && !IsAddressLike(title)) return title;

            foreach (var value in NotificationStorage.Array(chat, "participants"))
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var participant = value.GetObject();
                var address = NotificationStorage.String(participant, "address");
                var name = NotificationStorage.String(participant, "displayName");
                mapped = ContactName(address);
                if (!string.IsNullOrWhiteSpace(mapped)) return mapped;
                if (!string.IsNullOrWhiteSpace(name) && !IsAddressLike(name)) return name;
                if (string.IsNullOrWhiteSpace(title) || IsAddressLike(title)) title = address;
            }

            var handle = NotificationStorage.Object(message, "handle");
            var handleAddress = NotificationStorage.String(handle, "address");
            mapped = ContactName(handleAddress);
            if (!string.IsNullOrWhiteSpace(mapped)) return mapped;
            if (string.IsNullOrWhiteSpace(title) || IsAddressLike(title)) title = handleAddress;
            return title;
        }

        private static bool IsAddressLike(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var trimmed = value.Trim();
            return trimmed.IndexOf('@') >= 0 || trimmed.StartsWith("+", StringComparison.Ordinal) || trimmed.All(character => char.IsDigit(character) || char.IsWhiteSpace(character) || character == '-' || character == '(' || character == ')');
        }

        internal static async Task CloseAsync(CancellationToken token)
        {
            using (await NotificationStorage.LockAsync("notification-connection", token))
                foreach (var info in SocketActivityInformation.AllSockets.Where(pair => pair.Key.StartsWith(SocketPrefix, StringComparison.Ordinal)).Select(pair => pair.Value).ToList())
                {
                    StreamSocket socket = null;
                    try { socket = info == null ? null : info.StreamSocket; }
                    catch { }
                    if (socket == null) continue;
                    using (socket) { await socket.CancelIOAsync(); }
                }
        }
    }
}
