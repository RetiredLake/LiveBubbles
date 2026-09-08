using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using WpBlueBubbles.Models;

namespace WpBlueBubbles.Services
{
    public sealed class BlueBubblesClient : IDisposable
    {
        private readonly HttpClient _http = new HttpClient();
        private readonly string _apiRoot;
        private readonly string _serverRoot;
        private readonly string _password;
        private JsonObject _serverInfoSource;

        public BlueBubblesClient(string address, string password)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Server address is required.");
            var root = address.Trim().TrimEnd('/');
            if (!root.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !root.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                root = "http://" + root;
            _serverRoot = root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ? root.Substring(0, root.Length - 7) : root;
            _apiRoot = _serverRoot + "/api/v1";
            _password = password == null ? string.Empty : password.Trim();
            _http.Timeout = TimeSpan.FromSeconds(20);
        }

        public async Task TestConnectionAsync()
        {
            await GetRootAsync("ping");
        }

        public async Task<ServerCapabilities> GetServerCapabilitiesAsync()
        {
            var source = await GetServerInfoSourceAsync();
            return new ServerCapabilities
            {
                PrivateApiEnabled = JsonValueReader.Boolean(source, "private_api"),
                HelperConnected = JsonValueReader.Boolean(source, "helper_connected")
            };
        }

        public async Task<ServerCapabilities> RefreshServerCapabilitiesAsync()
        {
            _serverInfoSource = null;
            return await GetServerCapabilitiesAsync();
        }

        public async Task<string> GetRegisteredPhoneNumberAsync()
        {
            var source = await GetServerInfoSourceAsync();
            foreach (var key in new[] { "phoneNumber", "phone_number", "registeredPhoneNumber", "registered_phone_number", "imessagePhoneNumber" })
            {
                var number = JsonValueReader.String(source, key);
                if (!string.IsNullOrWhiteSpace(number)) return number;
            }
            return "BlueBubbles";
        }

        public async Task<IReadOnlyList<ChatItem>> GetChatsAsync(int timeframeDays = 0)
        {
            const int pageSize = 200;
            const int maximumChats = 10000;
            var result = new List<ChatItem>();
            for (var offset = 0; offset < maximumChats; offset += pageSize)
            {
                var body = new JsonObject
                {
                    ["with"] = new JsonArray { JsonValue.CreateStringValue("participants"), JsonValue.CreateStringValue("lastmessage") },
                    ["offset"] = JsonValue.CreateNumberValue(offset),
                    ["limit"] = JsonValue.CreateNumberValue(pageSize),
                    ["sort"] = JsonValue.CreateStringValue("lastmessage")
                };
                var root = await PostRootAsync("chat/query", body, true);
                var data = GetDataArray(root);
                foreach (var value in data) if (value.ValueType == JsonValueType.Object) result.Add(ChatItem.FromJson(value.GetObject()));
                if (data.Count < pageSize) break;
            }
            if (timeframeDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-timeframeDays).ToUnixTimeMilliseconds();
                result = result.FindAll(chat => chat.LastMessageTimestamp >= cutoff);
            }
            return result;
        }

        public async Task<IReadOnlyList<MessageItem>> GetMessagesAsync(string chatGuid, int limit, int timeframeDays = 0)
        {
            var route = "chat/" + Uri.EscapeDataString(chatGuid) + "/message?sort=DESC&offset=0&limit=" + limit + "&with=attachments,handle";
            if (timeframeDays > 0)
            {
                var after = DateTimeOffset.UtcNow.AddDays(-timeframeDays).ToUnixTimeMilliseconds();
                route += "&after=" + after;
            }
            var root = await GetRootAsync(route);
            var data = GetDataArray(root);
            var result = new List<MessageItem>();
            foreach (var value in data) if (value.ValueType == JsonValueType.Object) result.Add(MessageItem.FromJson(value.GetObject()));
            if (timeframeDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-timeframeDays).ToUnixTimeMilliseconds();
                result = result.FindAll(message => message.Timestamp >= cutoff);
            }
            result.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return result;
        }

        public string GetAttachmentDownloadUri(string attachmentGuid)
        {
            return string.IsNullOrWhiteSpace(attachmentGuid) ? string.Empty : BuildUrl("attachment/" + Uri.EscapeDataString(attachmentGuid) + "/download");
        }

        public async Task<byte[]> DownloadAttachmentAsync(string attachmentGuid)
        {
            if (string.IsNullOrWhiteSpace(attachmentGuid)) throw new ArgumentException("Attachment is required.");
            var response = await _http.GetAsync(BuildUrl("attachment/" + Uri.EscapeDataString(attachmentGuid) + "/download"));
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Could not download the attachment (HTTP " + (int)response.StatusCode + ").");
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task<MessageItem> SendTextAsync(string chatGuid, string text)
        {
            var body = new JsonObject
            {
                ["chatGuid"] = JsonValue.CreateStringValue(chatGuid),
                ["tempGuid"] = JsonValue.CreateStringValue("temp-" + Guid.NewGuid().ToString()),
                ["message"] = JsonValue.CreateStringValue(text)
            };
            var root = await PostRootAsync("message/text", body);
            JsonObject data;
            return JsonValueReader.TryObject(root, "data", out data) ? MessageItem.FromJson(data) : null;
        }

        public async Task<bool> GetIMessageAvailabilityAsync(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("An address is required.");
            var root = await GetRootAsync("handle/availability/imessage?address=" + Uri.EscapeDataString(address.Trim()));
            JsonObject data;
            return JsonValueReader.TryObject(root, "data", out data) && JsonValueReader.Boolean(data, "available");
        }

        public async Task MarkChatReadAsync(string chatGuid)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) throw new ArgumentException("Chat is required.");
            await PostRootAsync("chat/" + Uri.EscapeDataString(chatGuid) + "/read", new JsonObject());
        }

        public async Task StartTypingAsync(string chatGuid)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) throw new ArgumentException("Chat is required.");
            await PostRootAsync("chat/" + Uri.EscapeDataString(chatGuid) + "/typing", new JsonObject());
        }

        public async Task StopTypingAsync(string chatGuid)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) return;
            using (var socket = new MessageWebSocket())
            {
                socket.Control.MessageType = SocketMessageType.Utf8;
                var completed = new TaskCompletionSource<bool>();
                socket.MessageReceived += async (sender, args) =>
                {
                    try
                    {
                        string message;
                        using (var reader = args.GetDataReader())
                        {
                            reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                            message = reader.ReadString(reader.UnconsumedBufferLength);
                        }
                        if (message.StartsWith("0", StringComparison.Ordinal))
                        {
                            await SendSocketTextAsync(sender, "40");
                        }
                        else if (message.StartsWith("40", StringComparison.Ordinal))
                        {
                            var payload = new JsonArray
                            {
                                JsonValue.CreateStringValue("stopped-typing"),
                                new JsonObject { ["chatGuid"] = JsonValue.CreateStringValue(chatGuid) }
                            };
                            await SendSocketTextAsync(sender, "42" + payload.Stringify());
                        }
                        else if (message.StartsWith("2", StringComparison.Ordinal))
                        {
                            await SendSocketTextAsync(sender, "3" + message.Substring(1));
                        }
                        else if (message.IndexOf("stopped-typing-sent", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            completed.TrySetResult(true);
                        }
                        else if (message.IndexOf("stopped-typing-error", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            completed.TrySetException(new InvalidOperationException("LiveBubbles could not stop the typing indicator."));
                        }
                    }
                    catch (Exception ex) { completed.TrySetException(ex); }
                };
                socket.Closed += (sender, args) => completed.TrySetException(new InvalidOperationException("The LiveBubbles typing connection closed unexpectedly."));
                var socketRoot = _serverRoot.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? "wss://" + _serverRoot.Substring(8)
                    : _serverRoot.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + _serverRoot.Substring(7) : _serverRoot;
                await socket.ConnectAsync(new Uri(socketRoot + "/socket.io/?EIO=4&transport=websocket&guid=" + Uri.EscapeDataString(_password)));
                var timeout = Task.Delay(TimeSpan.FromSeconds(8));
                if (await Task.WhenAny(completed.Task, timeout) != completed.Task) throw new TimeoutException("LiveBubbles did not confirm that typing stopped.");
                await completed.Task;
                socket.Close(1000, "Typing stopped");
            }
        }

        private static async Task SendSocketTextAsync(MessageWebSocket socket, string text)
        {
            using (var writer = new DataWriter(socket.OutputStream))
            {
                writer.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                writer.WriteString(text);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
        }

        public Task<ChatItem> CreateChatAsync(string address, string message)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("A phone number or email address is required.");
            return CreateChatAsync(new[] { address }, message, null);
        }

        public async Task<ChatItem> CreateChatAsync(IReadOnlyList<string> recipients, string message, string service)
        {
            if (recipients == null || recipients.Count == 0) throw new ArgumentException("At least one recipient is required.");
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("A message is required.");

            var addresses = new JsonArray();
            foreach (var recipient in recipients)
            {
                if (!string.IsNullOrWhiteSpace(recipient)) addresses.Add(JsonValue.CreateStringValue(recipient.Trim()));
            }
            if (addresses.Count == 0) throw new ArgumentException("At least one recipient is required.");
            var body = new JsonObject
            {
                ["addresses"] = addresses,
                ["message"] = JsonValue.CreateStringValue(message.Trim()),
                ["tempGuid"] = JsonValue.CreateStringValue("temp-" + Guid.NewGuid().ToString())
            };
            if (addresses.Count > 1) body["method"] = JsonValue.CreateStringValue("private-api");
            if (!string.IsNullOrWhiteSpace(service)) body["service"] = JsonValue.CreateStringValue(service);
            var root = await PostRootAsync("chat/new", body);
            JsonObject data;
            if (!JsonValueReader.TryObject(root, "data", out data)) return null;
            JsonObject chat;
            if (JsonValueReader.TryObject(data, "chat", out chat)) return ChatItem.FromJson(chat);
            JsonArray chats;
            if (JsonValueReader.TryArray(data, "chats", out chats) && chats.Count > 0 && chats[0].ValueType == JsonValueType.Object) return ChatItem.FromJson(chats[0].GetObject());
            return ChatItem.FromJson(data);
        }

        public async Task<ChatItem> FindDirectChatAsync(string address, string preferredService)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            var services = new[] { preferredService, "iMessage", "SMS", "RCS" }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var service in services)
            {
                try
                {
                    var guid = service + ";-;" + address.Trim();
                    var root = await GetRootAsync("chat/" + Uri.EscapeDataString(guid) + "?with=participants,lastmessage");
                    JsonObject data;
                    var chat = JsonValueReader.TryObject(root, "data", out data) ? data : root;
                    if (!string.IsNullOrWhiteSpace(JsonValueReader.String(chat, "guid"))) return ChatItem.FromJson(chat);
                }
                catch
                {
                    // A missing service-specific GUID is expected while probing the available transports.
                }
            }
            return null;
        }

        public string GetGroupIconUri(string chatGuid)
        {
            return string.IsNullOrWhiteSpace(chatGuid) ? string.Empty : BuildUrl("chat/" + Uri.EscapeDataString(chatGuid) + "/icon");
        }

        public async Task<byte[]> DownloadGroupIconAsync(string chatGuid)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) return null;
            var response = await _http.GetAsync(BuildUrl("chat/" + Uri.EscapeDataString(chatGuid) + "/icon"));
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task RenameGroupChatAsync(string chatGuid, string displayName)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) throw new ArgumentException("Chat is required.");
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A group name is required.");
            await PutRootAsync("chat/" + Uri.EscapeDataString(chatGuid), new JsonObject { ["displayName"] = JsonValue.CreateStringValue(displayName.Trim()) });
        }

        public async Task LeaveGroupChatAsync(string chatGuid)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) throw new ArgumentException("Chat is required.");
            await PostRootAsync("chat/" + Uri.EscapeDataString(chatGuid) + "/leave", new JsonObject());
        }

        public async Task DeleteChatAsync(string chatGuid)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) throw new ArgumentException("Chat is required.");
            var response = await _http.DeleteAsync(BuildUrl("chat/" + Uri.EscapeDataString(chatGuid)));
            await ReadResponseAsync(response);
        }

        public async Task DeleteMessageAsync(string chatGuid, string messageGuid)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) throw new ArgumentException("Chat is required.");
            if (string.IsNullOrWhiteSpace(messageGuid)) throw new ArgumentException("Message is required.");
            var response = await _http.DeleteAsync(BuildUrl("chat/" + Uri.EscapeDataString(chatGuid) + "/" + Uri.EscapeDataString(messageGuid)));
            await ReadResponseAsync(response);
        }

        public async Task SendAttachmentAsync(string chatGuid, StorageFile file)
        {
            var tempGuid = "temp-" + Guid.NewGuid().ToString();
            using (var stream = await file.OpenReadAsync())
            using (var form = new MultipartFormDataContent())
            using (var image = new StreamContent(stream.AsStreamForRead()))
            {
                var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? stream.ContentType : file.ContentType;
                if (string.IsNullOrWhiteSpace(contentType)) contentType = "application/octet-stream";
                image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                form.Add(image, "attachment", file.Name);
                // Newer BlueBubbles servers can truncate semicolon-delimited chat GUIDs in multipart fields.
                // Keep the GUID in the query string where it survives intact.
                var route = "message/attachment?chatGuid=" + Uri.EscapeDataString(chatGuid);
                form.Add(new StringContent(chatGuid), "chatGuid");
                form.Add(new StringContent(tempGuid), "tempGuid");
                form.Add(new StringContent(file.Name), "name");
                var response = await _http.PostAsync(BuildUrl(route), form);
                await ReadResponseAsync(response);
            }
        }

        private string BuildUrl(string route)
        {
            var separator = route.Contains("?") ? "&guid=" : "?guid=";
            return _apiRoot + "/" + route + separator + Uri.EscapeDataString(_password);
        }

        private async Task<JsonObject> GetServerInfoSourceAsync()
        {
            if (_serverInfoSource != null) return _serverInfoSource;
            var root = await GetRootAsync("server/info");
            JsonObject data;
            _serverInfoSource = JsonValueReader.TryObject(root, "data", out data) ? data : root;
            return _serverInfoSource;
        }

        private async Task<JsonObject> GetRootAsync(string route)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var response = await _http.GetAsync(BuildUrl(route)))
                    {
                        if (attempt < 2 && IsTransientStatus(response.StatusCode))
                        {
                            await Task.Delay(300 * (attempt + 1));
                            continue;
                        }
                        return await ReadResponseAsync(response);
                    }
                }
                catch (Exception ex) when (attempt < 2 && IsTransientTransportError(ex))
                {
                    await Task.Delay(300 * (attempt + 1));
                }
            }
        }

        private async Task<JsonObject> PostRootAsync(string route, JsonObject body, bool retryTransient = false)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using (var content = new StringContent(body.Stringify(), Encoding.UTF8, "application/json"))
                    using (var response = await _http.PostAsync(BuildUrl(route), content))
                    {
                        if (retryTransient && attempt < 2 && IsTransientStatus(response.StatusCode))
                        {
                            await Task.Delay(300 * (attempt + 1));
                            continue;
                        }
                        return await ReadResponseAsync(response);
                    }
                }
                catch (Exception ex) when (retryTransient && attempt < 2 && IsTransientTransportError(ex))
                {
                    await Task.Delay(300 * (attempt + 1));
                }
            }
        }

        private static bool IsTransientTransportError(Exception exception)
        {
            return exception is HttpRequestException || exception is TaskCanceledException;
        }

        private static bool IsTransientStatus(System.Net.HttpStatusCode status)
        {
            var code = (int)status;
            return code == 408 || code == 429 || code >= 500;
        }

        private async Task<JsonObject> PutRootAsync(string route, JsonObject body)
        {
            var content = new StringContent(body.Stringify(), Encoding.UTF8, "application/json");
            var response = await _http.PutAsync(BuildUrl(route), content);
            return await ReadResponseAsync(response);
        }

        private static async Task<JsonObject> ReadResponseAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync();
            JsonObject root;
            if (!JsonObject.TryParse(text, out root)) throw new InvalidOperationException("The server returned an unreadable response (HTTP " + (int)response.StatusCode + ").");
            if (!response.IsSuccessStatusCode)
            {
                var error = JsonValueReader.String(root, "error");
                if (string.IsNullOrWhiteSpace(error)) error = JsonValueReader.String(root, "message");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Server request failed (HTTP " + (int)response.StatusCode + ")." : error);
            }
            return root;
        }

        private static JsonArray GetDataArray(JsonObject root)
        {
            JsonArray result;
            return JsonValueReader.TryArray(root, "data", out result) ? result : new JsonArray();
        }

        public void Dispose() { _http.Dispose(); }
    }

    public sealed class ServerCapabilities
    {
        public bool PrivateApiEnabled { get; set; }
        public bool HelperConnected { get; set; }
        public bool CanUsePrivateApi { get { return PrivateApiEnabled && HelperConnected; } }
    }
}
