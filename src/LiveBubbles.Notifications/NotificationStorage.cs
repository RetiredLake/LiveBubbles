using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace LiveBubbles.Notifications
{
    internal static class NotificationStorage
    {
        internal static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
        internal static string Folder { get { return ApplicationData.Current.LocalFolder.Path; } }
        // Optional BlueBubbles fields are not type-stable across server
        // versions. Null values (for example associatedMessageGuid and
        // dateRead) must be treated as missing instead of throwing a WinRT
        // InvalidOperationException during notification reconciliation.
        internal static string String(JsonObject value, string key)
        {
            if (value == null || !value.ContainsKey(key) || value[key] == null || value[key].ValueType != JsonValueType.String) return "";
            try { return value[key].GetString(); } catch { return ""; }
        }
        internal static long Number(JsonObject value, string key)
        {
            if (value == null || !value.ContainsKey(key) || value[key] == null || value[key].ValueType != JsonValueType.Number) return 0;
            try { return (long)value[key].GetNumber(); } catch { return 0; }
        }
        internal static JsonObject Object(JsonObject value, string key)
        {
            if (value == null || !value.ContainsKey(key) || value[key] == null || value[key].ValueType != JsonValueType.Object) return new JsonObject();
            try { return value[key].GetObject(); } catch { return new JsonObject(); }
        }
        internal static JsonArray Array(JsonObject value, string key)
        {
            if (value == null || !value.ContainsKey(key) || value[key] == null || value[key].ValueType != JsonValueType.Array) return new JsonArray();
            try { return value[key].GetArray(); } catch { return new JsonArray(); }
        }
        internal static bool Bool(JsonObject value, string key)
        {
            if (value == null || !value.ContainsKey(key) || value[key] == null || value[key].ValueType != JsonValueType.Boolean) return false;
            try { return value[key].GetBoolean(); } catch { return false; }
        }
        internal static void Put(JsonObject value, string key, string text) { value[key] = JsonValue.CreateStringValue(text ?? ""); }
        internal static void Put(JsonObject value, string key, long number) { value[key] = JsonValue.CreateNumberValue(number); }
        internal static void Put(JsonObject value, string key, bool flag) { value[key] = JsonValue.CreateBooleanValue(flag); }

        internal static JsonObject ReadSnapshot()
        {
            string path = Path.Combine(Folder, "livebubbles-notifications.json");
            if (!File.Exists(path)) return new JsonObject();
            try
            {
                JsonObject state;
                return JsonObject.TryParse(File.ReadAllText(path), out state) && state != null ? state : new JsonObject();
            }
            catch { return new JsonObject(); }
        }

        internal static async Task<FileStream> LockAsync(string name, CancellationToken token)
        {
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(Path.Combine(Folder, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (attempt >= 40) throw; }
                await Task.Delay(50, token);
            }
        }

        internal static async Task ChangeAsync(string generation, Action<JsonObject> change, CancellationToken token, Action committed = null)
        {
            using (await LockAsync("notification-state", token))
            {
                if (!NotificationBridge.IsCurrent(generation)) return;
                string path = Path.Combine(Folder, "livebubbles-notifications.json");
                JsonObject state = null;
                if (File.Exists(path))
                {
                    try { JsonObject.TryParse(File.ReadAllText(path), out state); }
                    catch (IOException) { throw; }
                }
                bool corrupt = state == null && File.Exists(path);
                if (state == null || String(state, "generation") != generation)
                {
                    state = new JsonObject(); Put(state, "generation", generation);
                    if (corrupt) Put(state, "replayFloor", Now);
                }
                change(state);
                if (!NotificationBridge.IsCurrent(generation)) return;
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, state.Stringify(), new UTF8Encoding(false));
                if (File.Exists(path)) { var source = await StorageFile.GetFileFromPathAsync(temporary); var destination = await StorageFile.GetFileFromPathAsync(path); await source.MoveAndReplaceAsync(destination); }
                else File.Move(temporary, path);
                if (NotificationBridge.IsCurrent(generation) && committed != null) committed();
            }
        }

        internal static NotificationCursor ReadCursor(JsonObject state, string chat)
        {
            var row = Object(Object(state, "chats"), chat);
            var cursor = new NotificationCursor { ReadThrough = Number(row, "readThrough"), Unread = Bool(row, "unread"), LatestTimestamp = Number(row, "latest"), ReplayFloor = Number(row, "floor") };
            foreach (var value in Array(row, "seen"))
            {
                if (value.ValueType != JsonValueType.String) continue;
                var id = value.GetString(); if (cursor.Seen.Add(id)) { cursor.Order.Enqueue(id); cursor.SeenTimes[id] = Number(Object(row, "times"), id); }
            }
            return cursor;
        }

        internal static void SaveCursor(JsonObject state, string chat, NotificationCursor cursor)
        {
            var chats = Object(state, "chats"); var row = Object(chats, chat);
            Put(row, "readThrough", cursor.ReadThrough); Put(row, "unread", cursor.Unread); Put(row, "touched", Now);
            var seen = new JsonArray(); foreach (var id in cursor.Order) seen.Add(JsonValue.CreateStringValue(id));
            var times = new JsonObject(); foreach (var item in cursor.SeenTimes) Put(times, item.Key, item.Value);
            row["times"] = times; Put(row, "latest", cursor.LatestTimestamp); Put(row, "floor", cursor.ReplayFloor);
            row["seen"] = seen; chats[chat] = row;
            // Bounded disk use; forgotten old messages remain suppressed by the enable/read watermarks.
            if (chats.Count > 2000)
                foreach (var key in chats.OrderBy(pair => Number(pair.Value.GetObject(), "touched")).Take(chats.Count - 2000).Select(pair => pair.Key).ToList())
                { Put(state, "replayFloor", Math.Max(Number(state, "replayFloor"), Number(chats[key].GetObject(), "latest"))); chats.Remove(key); }
            state["chats"] = chats;
        }

        internal static void SavePreview(JsonObject state, string chat, string title, string body, long timestamp)
        {
            if (state == null || string.IsNullOrWhiteSpace(chat)) return;
            var chats = Object(state, "chats");
            var row = Object(chats, chat);
            Put(row, "previewTitle", Compact(title));
            Put(row, "previewBody", Compact(body));
            Put(row, "previewTimestamp", timestamp);
            chats[chat] = row;
            state["chats"] = chats;
        }

        internal static void ClearPreview(JsonObject state, string chat)
        {
            if (state == null || string.IsNullOrWhiteSpace(chat)) return;
            var chats = Object(state, "chats");
            JsonObject row;
            if (!chats.ContainsKey(chat) || chats[chat] == null || chats[chat].ValueType != JsonValueType.Object || !TryGetObject(chats, chat, out row)) return;
            row.Remove("previewTitle"); row.Remove("previewBody"); row.Remove("previewTimestamp");
            chats[chat] = row;
            state["chats"] = chats;
        }

        private static bool TryGetObject(JsonObject value, string key, out JsonObject result)
        {
            try
            {
                result = value[key].GetObject();
                return result != null;
            }
            catch { result = null; return false; }
        }

        private static string Compact(string value)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length > 512 ? value.Substring(0, 509) + "..." : value;
        }
    }
}
