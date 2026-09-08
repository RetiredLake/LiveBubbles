using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using LiveBubbles.Notifications;
using WpBlueBubbles.Models;

namespace WpBlueBubbles
{
    public sealed partial class MainPage
    {
        private readonly DispatcherTimer _notificationUiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        private bool _updatingNotifications;
        private bool _notificationWindowActive = true;

        private void InitializeNotifications()
        {
            _notificationUiTimer.Tick += NotificationUiTimer_Tick;
            Window.Current.Activated += NotificationWindow_Activated;
            Window.Current.VisibilityChanged += NotificationWindow_VisibilityChanged;
            _notificationUiTimer.Start();
            RefreshNotificationControls();
        }

        private void UnloadNotifications()
        {
            _notificationUiTimer.Stop();
            _notificationUiTimer.Tick -= NotificationUiTimer_Tick;
            Window.Current.Activated -= NotificationWindow_Activated;
            Window.Current.VisibilityChanged -= NotificationWindow_VisibilityChanged;
            NotificationBridge.SetActiveChat(null);
        }

        private void NotificationWindow_Activated(object sender, WindowActivatedEventArgs e)
        {
            _notificationWindowActive = e.WindowActivationState != CoreWindowActivationState.Deactivated;
            UpdateNotificationContext();
        }
        private void NotificationWindow_VisibilityChanged(object sender, VisibilityChangedEventArgs e) { UpdateNotificationContext(); }
        private void NotificationUiTimer_Tick(object sender, object e) { UpdateNotificationContext(); RefreshNotificationControls(); }
        private void UpdateNotificationContext()
        {
            bool visible = _notificationWindowActive && Window.Current.Visible && ConversationPane.Visibility == Visibility.Visible
                && SettingsOverlay.Visibility != Visibility.Visible && ContactsOverlay.Visibility != Visibility.Visible && ComposeOverlay.Visibility != Visibility.Visible;
            NotificationBridge.SetActiveChat(visible && _selectedChat != null ? _selectedChat.Guid : null);
        }

        private async void StartNotificationsWithoutWaiting()
        {
            try { await StartNotificationsAsync(); } catch { RefreshNotificationControls(); }
        }

        private async Task StartNotificationsAsync()
        {
            UpdateNotificationContext();
            await NotificationBridge.StartAsync();
            RefreshNotificationControls();
        }

        private void RefreshNotificationControls()
        {
            if (NotificationsToggle == null || _updatingNotifications) return;
            _updatingNotifications = true;
            try
            {
                NotificationsToggle.IsOn = NotificationBridge.Enabled;
                NotificationPreviewsToggle.IsOn = NotificationBridge.ShowPreviews;
                NotificationPreviewsToggle.IsEnabled = NotificationBridge.Enabled;
                NotificationsStatusText.Text = string.IsNullOrWhiteSpace(NotificationBridge.Status) ? "Notifications connect automatically after sign-in." : NotificationBridge.Status;
            }
            finally { _updatingNotifications = false; }
        }

        private async void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_settingsLoaded || _updatingNotifications || _client == null) return;
            _updatingNotifications = true;
            NotificationsToggle.IsEnabled = false;
            try
            {
                if (NotificationsToggle.IsOn) await NotificationBridge.EnableAsync();
                else await NotificationBridge.StopAsync();
            }
            catch { NotificationsStatusText.Text = "Windows could not change notifications. Try again."; }
            finally { _updatingNotifications = false; NotificationsToggle.IsEnabled = true; RefreshNotificationControls(); }
        }
        private void NotificationPreviewsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settingsLoaded && !_updatingNotifications) NotificationBridge.ShowPreviews = NotificationPreviewsToggle.IsOn;
        }
        private void TestNotification_Click(object sender, RoutedEventArgs e)
        {
            try { NotificationBridge.ShowTestNotification(); }
            catch { NotificationsStatusText.Text = "Windows has blocked notifications for LiveBubbles."; }
        }
        private async void RetryNotifications_Click(object sender, RoutedEventArgs e) { await StartNotificationsAsync(); }

        private async Task<ChatItem> FindNotificationChatAsync(string chatGuid)
        {
            var chat = _allChats.FirstOrDefault(item => item.Guid == chatGuid);
            if (chat != null || _client == null) return chat;
            var client = _client;
            try
            {
                // Toasts can refer to a chat outside the current time filter. Metadata only.
                var chats = await client.GetChatsAsync(0);
                if (client != _client) return null;
                chat = chats.FirstOrDefault(item => item.Guid == chatGuid);
                if (chat != null) chat.ApplyContactData(_contactNames, _contactImages, _contactTileImages);
                return chat;
            }
            catch { return null; }
        }
    }
}
