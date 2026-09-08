using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Background;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Storage;

namespace LiveBubbles.Notifications
{
    public sealed class NotificationBridge
    {
        internal const string TaskName = "LiveBubbles.SocketNotifications";
        private const string RecoveryName = "LiveBubbles.NotificationRecovery";
        private const string NetworkName = "LiveBubbles.NotificationNetwork";
        private const string Prefix = "LiveBubbles.Notifications.";
        private static Windows.Foundation.Collections.IPropertySet Values { get { return ApplicationData.Current.LocalSettings.Values; } }
        private static bool Flag(string key) { object value; return Values.TryGetValue(Prefix + key, out value) && value is bool && (bool)value; }
        private static string Text(string key) { object value; return Values.TryGetValue(Prefix + key, out value) ? value as string ?? "" : ""; }
        private static long Number(string key) { long value; return long.TryParse(Text(key), out value) ? value : 0; }
        public static bool Enabled { get { return Flag("Enabled"); } }
        public static bool ShowPreviews { get { return !Values.ContainsKey(Prefix + "Previews") || Flag("Previews"); } set { Values[Prefix + "Previews"] = value; } }
        public static string Status { get { return Text("Status"); } }
        public static string Diagnostics { get { return Text("Diagnostics"); } }
        internal static string Generation { get { return Text("Generation"); } }
        internal static long EnabledSince { get { return Number("EnabledSince"); } }
        internal static bool IsCurrent(string generation) { return Enabled && generation.Length > 0 && Generation == generation; }
        internal static string ActiveChat { get { return Number("ActiveUntil") > NotificationStorage.Now ? Text("ActiveChat") : ""; } }
        internal static void SetStatus(string value) { Values[Prefix + "Status"] = value; }
        internal static void RecordError(string stage, Exception error)
        {
            string type = error == null ? "Unknown" : error.GetType().Name;
            string hresult = error == null ? "" : "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture);
            Values[Prefix + "Diagnostics"] = (stage ?? "notification") + ": " + type + (hresult.Length == 0 ? "" : " (" + hresult + ")");
        }

        public static void SetActiveChat(string chatGuid)
        {
            Values[Prefix + "ActiveChat"] = chatGuid ?? "";
            Values[Prefix + "ActiveUntil"] = (string.IsNullOrEmpty(chatGuid) ? 0 : NotificationStorage.Now + 45000).ToString();
        }
        public static bool IsMuted(string chatGuid) { return Flag("Muted." + NotificationPresenter.Tag(chatGuid ?? "")); }
        public static void SetMuted(string chatGuid, bool muted)
        {
            Values[Prefix + "Muted." + NotificationPresenter.Tag(chatGuid)] = muted;
            if (muted) { try { NotificationPresenter.ClearChat(chatGuid); } catch { } }
        }
        public static void ShowTestNotification() { NotificationPresenter.Show("LiveBubbles", "Notifications are working on this device.", null); }
        public static IAsyncAction TestAsync() { return TestCoreAsync().AsAsyncAction(); }
        public static IAsyncAction StartAsync() { return StartCoreAsync(false).AsAsyncAction(); }
        public static IAsyncAction EnableAsync() { return StartCoreAsync(true).AsAsyncAction(); }
        public static IAsyncAction StopAsync() { return StopCoreAsync().AsAsyncAction(); }
        public static IAsyncAction MarkReadAsync(string chatGuid, long timestamp) { return MarkReadCoreAsync(chatGuid, timestamp).AsAsyncAction(); }

        private static async Task TestCoreAsync()
        {
            if (!Enabled) throw new InvalidOperationException("Enable notifications before testing the connection.");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                try
                {
                    string generation;
                    var socketTask = BackgroundTaskRegistration.AllTasks.Values.FirstOrDefault(t => t.Name == TaskName);
                    if (socketTask == null)
                    {
                        await StartCoreAsync(false);
                        socketTask = BackgroundTaskRegistration.AllTasks.Values.FirstOrDefault(t => t.Name == TaskName);
                    }
                    if (socketTask == null) throw new InvalidOperationException("The LiveBubbles notification background task is not registered.");
                    generation = Generation;
                    if (!IsCurrent(generation)) throw new InvalidOperationException("Notifications are no longer enabled.");
                    // Force a fresh authenticated handshake and broker handoff so
                    // this cannot pass merely because a local toast is allowed.
                    await NotificationRuntime.EnsureConnectedAsync(socketTask.TaskId, generation, deadline.Token, true);
                    if (!IsCurrent(generation)) throw new InvalidOperationException("Notifications are no longer enabled.");
                    try { ShowTestNotification(); }
                    catch (Exception error) { throw new NotificationFailureException("toast-test", error); }
                    SetStatus("Connection and local notification test passed.");
                }
                catch (NotificationFailureException ex)
                {
                    RecordError(ex.Stage, ex.InnerException ?? ex);
                    SetStatus("Notification connection test failed. See developer details.");
                    throw;
                }
                catch (Exception ex)
                {
                    RecordError("test", ex);
                    SetStatus("Notification connection test failed. See developer details.");
                    throw;
                }
            }
        }

        private static async Task StartCoreAsync(bool explicitlyEnable)
        {
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                try
                {
                    using (await NotificationStorage.LockAsync("notification-registration", deadline.Token))
                    {
                        var serverUri = NotificationRuntime.ServerUri(false);
                        string serverKey = NotificationPresenter.Tag(serverUri.Scheme + "://" + serverUri.Authority + serverUri.AbsolutePath);
                        if (Text("ServerKey") != serverKey)
                        {
                            Values[Prefix + "Generation"] = Guid.NewGuid().ToString("N");
                            Values[Prefix + "EnabledSince"] = NotificationStorage.Now.ToString();
                            await NotificationRuntime.CloseAsync(deadline.Token);
                            try { NotificationPresenter.ClearAll(); } catch { }
                            foreach (var key in Values.Keys.Where(k => k.StartsWith(Prefix + "Muted.", StringComparison.Ordinal)).ToList()) Values.Remove(key);
                            Values[Prefix + "ServerKey"] = serverKey;
                        }
                        if (!Values.ContainsKey(Prefix + "Enabled") || explicitlyEnable && !Enabled || Enabled && string.IsNullOrEmpty(Generation))
                        {
                            Values[Prefix + "EnabledSince"] = NotificationStorage.Now.ToString();
                            Values[Prefix + "Generation"] = Guid.NewGuid().ToString("N");
                            Values[Prefix + "Enabled"] = true;
                        }
                        if (!Enabled) { SetStatus("Off"); return; }
                        string generation = Generation;
                        var version = Package.Current.Id.Version;
                        string marker = version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision;
                        if (Text("RegistrationVersion") != marker)
                        {
                            await NotificationRuntime.CloseAsync(deadline.Token);
                            Unregister();
                            BackgroundExecutionManager.RemoveAccess();
                        }
                        var access = await BackgroundExecutionManager.RequestAccessAsync();
                        if (!IsCurrent(generation)) return;
                        if (!access.ToString().StartsWith("Allowed", StringComparison.Ordinal))
                        {
                            SetStatus("Windows has blocked background notifications. Allow background activity for LiveBubbles in Windows settings.");
                            return;
                        }
                        var socketTask = Register(TaskName, new SocketActivityTrigger());
                        Register(RecoveryName, new TimeTrigger(15, false));
                        Register(NetworkName, new SystemTrigger(SystemTriggerType.NetworkStateChange, false));
                        Values[Prefix + "RegistrationVersion"] = marker;
                        await NotificationRuntime.EnsureConnectedAsync(socketTask.TaskId, generation, deadline.Token);
                        await NotificationRuntime.ReconcileAsync(generation, deadline.Token);
                    }
                }
                catch (Exception ex) { RecordError("registration", ex); if (Enabled) SetStatus("Notification connection unavailable. Retrying automatically; open the app to retry now."); }
            }
        }

        private static IBackgroundTaskRegistration Register(string name, IBackgroundTrigger trigger)
        {
            var task = BackgroundTaskRegistration.AllTasks.Values.FirstOrDefault(t => t.Name == name);
            if (task != null) return task;
            var builder = new BackgroundTaskBuilder { Name = name, TaskEntryPoint = "LiveBubbles.Notifications.NotificationTask", IsNetworkRequested = true };
            builder.SetTrigger(trigger);
            return builder.Register();
        }

        private static async Task StopCoreAsync()
        {
            Values[Prefix + "Enabled"] = false;
            Values[Prefix + "Generation"] = Guid.NewGuid().ToString("N");
            SetActiveChat(null); SetStatus("Off");
            Unregister();
            try
            {
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20))) await NotificationRuntime.CloseAsync(deadline.Token);
            }
            finally
            {
                using (await NotificationStorage.LockAsync("notification-state", CancellationToken.None)) NotificationPresenter.ClearAll();
            }
        }

        private static void Unregister()
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(t => t.Name == TaskName || t.Name == RecoveryName || t.Name == NetworkName || t.Name == "WpBlueBubbles.MessageSync").ToList()) task.Unregister(true);
        }

        private static async Task MarkReadCoreAsync(string chatGuid, long timestamp)
        {
            if (string.IsNullOrEmpty(chatGuid)) return;
            try
            {
                string generation = Generation; int unread = 0;
                await NotificationStorage.ChangeAsync(generation, state =>
                {
                    var cursor = NotificationStorage.ReadCursor(state, chatGuid);
                    cursor.ReadThrough = Math.Max(cursor.ReadThrough, timestamp);
                    if (cursor.LatestTimestamp <= cursor.ReadThrough) cursor.Unread = false;
                    NotificationStorage.SaveCursor(state, chatGuid, cursor);
                    unread = NotificationStorage.Object(state, "chats").Values.Count(v => NotificationStorage.Bool(v.GetObject(), "unread"));
                }, CancellationToken.None, () =>
                {
                    NotificationPresenter.ClearChat(chatGuid);
                    if (IsCurrent(generation)) NotificationPresenter.UpdateBadge(unread);
                });
            }
            catch { /* Notification decoration never blocks reading a conversation. */ }
        }
    }
}
