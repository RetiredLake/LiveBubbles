using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace LiveBubbles.Notifications
{
    internal static class NotificationPresenter
    {
        internal const string Group = "LiveBubbles";
        internal static string Tag(string chat)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(chat))).Replace("-", "").Substring(0, 16);
        }

        internal static async Task ObserveAsync(JsonObject message, string chat, string title, string generation, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(chat)) return;
            bool alert = false; int unread = 0;
            await NotificationStorage.ChangeAsync(generation, state =>
            {
                var cursor = NotificationStorage.ReadCursor(state, chat);
                long created = NotificationStorage.Number(message, "dateCreated");
                string messageId = NotificationStorage.String(message, "guid");
                bool alreadySeen = cursor.Seen.Contains(messageId);
                bool outgoing = NotificationStorage.Bool(message, "isFromMe");
                bool serverRead = NotificationStorage.Number(message, "dateRead") > 0 || NotificationStorage.Bool(message, "isRead");
                bool active = NotificationBridge.ActiveChat == chat;
                long floor = Math.Max(NotificationBridge.EnabledSince, NotificationStorage.Number(state, "replayFloor"));
                alert = NotificationPolicy.Observe(cursor, messageId, created, floor, outgoing, serverRead, NotificationBridge.IsMuted(chat), active);
                if (!string.IsNullOrWhiteSpace(messageId) && !alreadySeen && !outgoing && !serverRead && !active && created > floor && created > cursor.ReadThrough && cursor.Unread)
                {
                    string body = NotificationStorage.String(message, "text");
                    if (string.IsNullOrWhiteSpace(body)) body = NotificationStorage.Array(message, "attachments").Count > 0 ? "New attachment" : "New message";
                    NotificationStorage.SavePreview(state, chat, string.IsNullOrWhiteSpace(title) ? "New message" : title, body, created);
                }
                NotificationStorage.SaveCursor(state, chat, cursor);
                unread = CountUnread(state);
            }, token, () =>
            {
                if (!NotificationBridge.IsCurrent(generation)) return;
                try { UpdateBadge(unread); } catch { }
                if (!alert || NotificationBridge.IsMuted(chat) || NotificationBridge.ActiveChat == chat) return;
                try
                {
                    string body = "New message";
                    if (NotificationBridge.ShowPreviews)
                    {
                        body = NotificationStorage.String(message, "text");
                        if (string.IsNullOrWhiteSpace(body)) body = NotificationStorage.Array(message, "attachments").Count > 0 ? "New attachment" : "New message";
                    }
                    Show(NotificationBridge.ShowPreviews && !string.IsNullOrWhiteSpace(title) ? title : "LiveBubbles", body, chat);
                }
                catch (Exception ex) { NotificationBridge.RecordError("toast", ex); NotificationBridge.SetStatus("Windows has blocked notification display for LiveBubbles."); }
            });
        }

        internal static void Show(string title, string body, string chat)
        {
            XmlDocument xml;
            if (!NotificationBridge.ShowPreviews && !string.IsNullOrEmpty(chat))
            {
                xml = new XmlDocument();
                xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\" /></visual><actions><input id=\"reply\" type=\"text\" placeHolderContent=\"Reply\"/><action content=\"Send\" arguments=\"reply\" activationType=\"foreground\"/></actions></toast>");
                var binding = xml.GetElementsByTagName("binding")[0];
                AppendText(xml, binding, "LiveBubbles");
                AppendText(xml, binding, "New message");
                var action = xml.GetElementsByTagName("action")[0] as XmlElement;
                if (action != null) action.SetAttribute("arguments", "chat=" + Uri.EscapeDataString(chat) + "&reply=1");
            }
            else
            {
                xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var text = xml.GetElementsByTagName("text");
                text[0].AppendChild(xml.CreateTextNode(Trim(title))); text[1].AppendChild(xml.CreateTextNode(Trim(body)));
            }
            if (!string.IsNullOrEmpty(chat)) xml.DocumentElement.SetAttribute("launch", "chat=" + Uri.EscapeDataString(chat));
            var toast = new ToastNotification(xml) { Group = Group, Tag = string.IsNullOrEmpty(chat) ? "test" : Tag(chat), ExpirationTime = DateTimeOffset.Now.AddHours(12) };
            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }

        internal static void UpdateBadge(int count)
        {
            UpdateBadge(count, NotificationStorage.ReadSnapshot());
        }

        internal static void Refresh()
        {
            if (!NotificationBridge.Enabled) { UpdateBadge(0, null); return; }
            var state = NotificationStorage.ReadSnapshot();
            if (state == null || NotificationStorage.String(state, "generation") != NotificationBridge.Generation) { UpdateBadge(0, null); return; }
            var chats = NotificationStorage.Object(state, "chats");
            var count = CountUnread(state);
            UpdateBadge(count, state);
        }

        private static int CountUnread(JsonObject state)
        {
            return NotificationStorage.Object(state, "chats").Values.Count(value => value != null && value.ValueType == JsonValueType.Object && NotificationStorage.Bool(value.GetObject(), "unread"));
        }

        private static void UpdateBadge(int count, JsonObject state)
        {
            var tile = TileUpdateManager.CreateTileUpdaterForApplication(); var badge = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
            if (!NotificationBridge.Enabled || count <= 0) { tile.Clear(); badge.Clear(); return; }
            count = Math.Min(99, count);
            var latest = FindLatestUnread(state);
            string title = NotificationBridge.ShowPreviews && latest != null && !string.IsNullOrWhiteSpace(latest.Title) ? latest.Title : "Unread messages";
            string body = NotificationBridge.ShowPreviews && latest != null && !string.IsNullOrWhiteSpace(latest.Body) ? latest.Body : count + " unread " + (count == 1 ? "chat" : "chats");
            var xml = BuildTileXml(count, title, body);
            // Keep the tile current until every unread chat is read. A timed
            // expiration would make an unopened message disappear overnight.
            tile.Update(new TileNotification(xml));
            var badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
            badgeXml.SelectSingleNode("/badge").Attributes.GetNamedItem("value").NodeValue = count.ToString();
            badge.Update(new BadgeNotification(badgeXml));
        }

        private sealed class UnreadPreview
        {
            internal string Title;
            internal string Body;
            internal long Timestamp;
        }

        private static UnreadPreview FindLatestUnread(JsonObject state)
        {
            if (state == null) return null;
            UnreadPreview latest = null;
            foreach (var item in NotificationStorage.Object(state, "chats"))
            {
                if (item.Value == null || item.Value.ValueType != JsonValueType.Object) continue;
                var row = item.Value.GetObject();
                if (!NotificationStorage.Bool(row, "unread")) continue;
                var preview = new UnreadPreview
                {
                    Title = NotificationStorage.String(row, "previewTitle"),
                    Body = NotificationStorage.String(row, "previewBody"),
                    Timestamp = NotificationStorage.Number(row, "previewTimestamp")
                };
                if (preview.Timestamp <= 0) preview.Timestamp = NotificationStorage.Number(row, "latest");
                if (latest == null || preview.Timestamp > latest.Timestamp) latest = preview;
            }
            return latest;
        }

        private static XmlDocument BuildTileXml(int count, string title, string body)
        {
            var xml = new XmlDocument();
            var tile = xml.CreateElement("tile");
            var visual = xml.CreateElement("visual");
            var square = xml.CreateElement("binding");
            square.SetAttribute("template", "TileSquare150x150Text04");
            AppendText(xml, square, count.ToString());
            AppendText(xml, square, title);
            AppendText(xml, square, body);
            var wide = xml.CreateElement("binding");
            wide.SetAttribute("template", "TileWide310x150Text03");
            AppendText(xml, wide, title);
            AppendText(xml, wide, body);
            AppendText(xml, wide, count + " unread " + (count == 1 ? "chat" : "chats"));
            visual.AppendChild(square); visual.AppendChild(wide); tile.AppendChild(visual); xml.AppendChild(tile);
            return xml;
        }

        private static void AppendText(XmlDocument xml, Windows.Data.Xml.Dom.IXmlNode parent, string value)
        {
            var text = xml.CreateElement("text");
            text.SetAttribute("id", (parent.ChildNodes.Count + 1).ToString());
            text.AppendChild(xml.CreateTextNode(Trim(value)));
            parent.AppendChild(text);
        }

        internal static void ClearChat(string chat) { ToastNotificationManager.History.Remove(Tag(chat), Group); }
        internal static void ClearAll() { ToastNotificationManager.History.RemoveGroup(Group); UpdateBadge(0); }
        private static string Trim(string value) { value = (value ?? "").Replace('\r', ' ').Replace('\n', ' '); return value.Length > 120 ? value.Substring(0, 117) + "..." : value; }
    }
}
