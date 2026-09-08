using System;
using System.Collections.Generic;

namespace LiveBubbles.Notifications
{
    // Deliberately independent of UWP and of the application's message/media models.
    internal sealed class NotificationCursor
    {
        internal long ReadThrough;
        internal long LatestTimestamp;
        internal long ReplayFloor;
        internal readonly Dictionary<string, long> SeenTimes = new Dictionary<string, long>(StringComparer.Ordinal);
        internal bool Unread;
        internal readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Queue<string> Order = new Queue<string>();
    }

    internal static class NotificationPolicy
    {
        internal static bool Observe(NotificationCursor cursor, string messageId, long timestamp,
            long enabledSince, bool outgoing, bool serverRead, bool muted, bool active)
        {
            if (string.IsNullOrWhiteSpace(messageId) || timestamp <= 0 || timestamp <= cursor.ReplayFloor || cursor.Seen.Contains(messageId)) return false;
            cursor.Seen.Add(messageId);
            cursor.Order.Enqueue(messageId);
            cursor.SeenTimes[messageId] = timestamp;
            cursor.LatestTimestamp = Math.Max(cursor.LatestTimestamp, timestamp);
            while (cursor.Order.Count > 256)
            {
                var expired = cursor.Order.Dequeue();
                cursor.ReplayFloor = Math.Max(cursor.ReplayFloor, cursor.SeenTimes[expired]);
                cursor.SeenTimes.Remove(expired); cursor.Seen.Remove(expired);
            }
            if (outgoing || timestamp <= enabledSince || timestamp <= cursor.ReadThrough || serverRead) return false;
            if (active)
            {
                cursor.ReadThrough = Math.Max(cursor.ReadThrough, timestamp);
                cursor.Unread = false;
                return false;
            }
            cursor.Unread = true;
            return !muted;
        }
    }
}
