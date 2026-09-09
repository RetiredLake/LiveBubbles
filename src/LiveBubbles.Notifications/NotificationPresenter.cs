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
                bool active = NotificationBridge.ActiveChat == chat;
                alert = NotificationPolicy.Observe(cursor, NotificationStorage.String(message, "guid"), created,
                    Math.Max(NotificationBridge.EnabledSince, NotificationStorage.Number(state, "replayFloor")), NotificationStorage.Bool(message, "isFromMe"),
                    NotificationStorage.Number(message, "dateRead") > 0 || NotificationStorage.Bool(message, "isRead"), NotificationBridge.IsMuted(chat), active);
                NotificationStorage.SaveCursor(state, chat, cursor);
                unread = NotificationStorage.Object(state, "chats").Values.Count(v => NotificationStorage.Bool(v.GetObject(), "unread"));
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
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            if (!string.IsNullOrEmpty(chat)) xml.DocumentElement.SetAttribute("launch", "chat=" + Uri.EscapeDataString(chat));
            var text = xml.GetElementsByTagName("text");
            text[0].AppendChild(xml.CreateTextNode(Trim(title))); text[1].AppendChild(xml.CreateTextNode(Trim(body)));
            var toast = new ToastNotification(xml) { Group = Group, Tag = string.IsNullOrEmpty(chat) ? "test" : Tag(chat), ExpirationTime = DateTimeOffset.Now.AddHours(12) };
            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }

        internal static void UpdateBadge(int count)
        {
            var tile = TileUpdateManager.CreateTileUpdaterForApplication(); var badge = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
            if (!NotificationBridge.Enabled || count <= 0) { tile.Clear(); badge.Clear(); return; }
            count = Math.Min(99, count);
            var xml = new XmlDocument();
            xml.LoadXml("<tile><visual><binding template=\"TileSquare150x150Text04\"><text id=\"1\">" + count + "</text></binding><binding template=\"TileWide310x150Text09\"><text id=\"1\">" + count + " unread chats</text></binding></visual></tile>");
            tile.Update(new TileNotification(xml) { ExpirationTime = DateTimeOffset.Now.AddDays(1) });
            var badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
            badgeXml.SelectSingleNode("/badge").Attributes.GetNamedItem("value").NodeValue = count.ToString();
            badge.Update(new BadgeNotification(badgeXml));
        }

        internal static void ClearChat(string chat) { ToastNotificationManager.History.Remove(Tag(chat), Group); }
        internal static void ClearAll() { ToastNotificationManager.History.RemoveGroup(Group); UpdateBadge(0); }
        private static string Trim(string value) { value = (value ?? "").Replace('\r', ' ').Replace('\n', ' '); return value.Length > 120 ? value.Substring(0, 117) + "..." : value; }
    }
}
