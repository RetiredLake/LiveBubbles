using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.ApplicationModel.Contacts;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Windows.UI.Input;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.ViewManagement;
using Windows.UI.StartScreen;
using Windows.System.Profile;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Documents;
using WpBlueBubbles.Models;
using WpBlueBubbles.Services;

namespace WpBlueBubbles
{
    public sealed partial class MainPage : Page
    {
        public bool IsClientReady { get { return _client != null; } }
        private readonly ObservableCollection<ChatItem> _chats = new ObservableCollection<ChatItem>();
        private readonly List<ChatItem> _allChats = new List<ChatItem>();
        private readonly ObservableCollection<ChatItem> _recipientMatches = new ObservableCollection<ChatItem>();
        private readonly ObservableCollection<MessageItem> _messages = new ObservableCollection<MessageItem>();
        private readonly ObservableCollection<ContactChoice> _contacts = new ObservableCollection<ContactChoice>();
        private readonly List<ContactChoice> _allContacts = new List<ContactChoice>();
        private readonly DispatcherTimer _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _qrScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _typingStopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        private readonly DispatcherTimer _availabilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        private BlueBubblesClient _client;
        private QrCameraScanner _qrScanner;
        private ChatItem _selectedChat;
        private bool _isRefreshing;
        private bool _isQrScanInProgress;
        private int _messagesPerChat = 15;
        private int _syncTimeframeDays = 7;
        private IReadOnlyDictionary<string, string> _contactNames = new Dictionary<string, string>();
        private IReadOnlyDictionary<string, ImageSource> _contactImages = new Dictionary<string, ImageSource>();
        private IReadOnlyDictionary<string, string> _contactTileImages = new Dictionary<string, string>();
        private ShareOperation _shareOperation;
        private readonly List<StorageFile> _sharedFiles = new List<StorageFile>();
        private readonly List<StorageFile> _shareTemporaryFiles = new List<StorageFile>();
        private string _sharedText;
        private bool _showArchived;
        private ChatItem _composeSelectedChat;
        private bool _updatingRecipient;
        private bool _contactsForCompose;
        private bool _statusIsError;
        private readonly InputPane _inputPane;
        private bool _settingsLoaded;
        private bool _inputPaneVisible;
        private string _chatStateSignature;
        private int _messageLoadGeneration;
        private int _chatLoadGeneration;
        private DateTimeOffset _pinMessagesToBottomUntil;
        private ServerCapabilities _serverCapabilities = new ServerCapabilities();
        private bool _serverCapabilitiesKnown;
        private DataTemplate _compactChatTemplate;
        private DataTemplate _compactMessageTemplate;
        private string _typingChatGuid;
        private int _availabilityGeneration;
        private string _composeService;
        private bool _contextMenuOpen;
        private bool _isForwarding;
        private readonly UISettings _uiSettings = new UISettings();
        private AppThemeMode _themeMode = AppThemeMode.System;
        private static readonly bool UseLegacyInAppSyncStatus = false;
        private static readonly bool UsePhoneSyncStatus = false;

        public MainPage()
        {
            InitializeComponent();
            _compactChatTemplate = ChatsList.ItemTemplate;
            _compactMessageTemplate = MessagesList.ItemTemplate;
            _inputPane = InputPane.GetForCurrentView();
            ChatsList.ItemsSource = _chats;
            MessagesList.ItemsSource = _messages;
            RecipientMatchesList.ItemsSource = _recipientMatches;
            ContactsList.ItemsSource = _contacts;
            _pollTimer.Tick += PollTimer_Tick;
            _qrScanTimer.Tick += QrScanTimer_Tick;
            _statusTimer.Tick += StatusTimer_Tick;
            _typingStopTimer.Tick += TypingStopTimer_Tick;
            _availabilityTimer.Tick += AvailabilityTimer_Tick;
            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
            SizeChanged += MainPage_SizeChanged;
            SystemNavigationManager.GetForCurrentView().BackRequested += MainPage_BackRequested;
            _inputPane.Showing += InputPane_Showing;
            _inputPane.Hiding += InputPane_Hiding;
            _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
            MessageBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(MessageBox_KeyDown), true);
            ComposeMessageBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(ComposeMessageBox_KeyDown), true);
            InitializeNotifications();
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseVisible);
            UpdateResponsiveLayout();
            ServerSettings settings;
            try
            {
                SettingsStore.EnsureVersion019Defaults();
                settings = SettingsStore.Load();
            }
            catch
            {
                // Corrupt legacy local state must never require uninstalling the app to recover.
                SettingsStore.Clear();
                SettingsStore.EnsureVersion019Defaults();
                settings = SettingsStore.Load();
            }
            _themeMode = settings.ThemeMode;
            SelectThemeMode(_themeMode);
            AccentColorToggle.IsOn = settings.UseAccentColor;
            LargerUiToggle.IsOn = settings.LargerUi;
            SendReadReceiptsToggle.IsOn = settings.SendReadReceipts;
            SendTypingIndicatorsToggle.IsOn = settings.SendTypingIndicators;
            DeveloperModeToggle.IsOn = settings.DeveloperMode;
            _pollTimer.Interval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);
            ApplyTheme(settings.ThemeMode, settings.UseAccentColor, settings.LargerUi);
            SetPackageVersion();
            UpdateServerDetails(settings.Address);
            ServerAddressBox.Text = settings.Address ?? string.Empty;
            ServerPasswordBox.Password = string.Empty;
            _messagesPerChat = settings.MessagesPerChat;
            _syncTimeframeDays = settings.SyncTimeframeDays;
            MessagesPerChatSlider.Value = _messagesPerChat;
            SelectTimeframe(_syncTimeframeDays);
            SelectPollInterval(settings.PollIntervalSeconds);
            _settingsLoaded = true;
            UpdateDeveloperModePresentation();
            UpdateSyncDescription();
            RefreshNotificationControls();
            if (!settings.IsComplete)
            {
                SetSettingsMode(false);
                SettingsOverlay.Visibility = Visibility.Visible;
                return;
            }
            await StartClientAsync(settings.Address, settings.Password, false);
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            UnloadNotifications();
            _pollTimer.Stop();
            _qrScanTimer.Stop();
            _statusTimer.Stop();
            _typingStopTimer.Stop();
            _availabilityTimer.Stop();
            StopTypingWithoutWaiting();
            _qrScanner?.Dispose();
            if (_client != null) _client.Dispose();
            SystemNavigationManager.GetForCurrentView().BackRequested -= MainPage_BackRequested;
            _inputPane.Showing -= InputPane_Showing;
            _inputPane.Hiding -= InputPane_Hiding;
            _uiSettings.ColorValuesChanged -= UiSettings_ColorValuesChanged;
        }

        private void MainPage_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateResponsiveLayout(); }

        private bool UseSinglePaneLayout
        {
            get
            {
                return string.Equals(AnalyticsInfo.DeviceForm, "Phone", StringComparison.OrdinalIgnoreCase) || ActualWidth < 1000;
            }
        }

        private void UpdateResponsiveLayout()
        {
            if (!UseSinglePaneLayout)
            {
                ChatColumn.Width = new GridLength(360);
                DividerColumn.Width = new GridLength(1);
                ConversationColumn.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(ChatsPane, 0);
                Grid.SetColumnSpan(ChatsPane, 1);
                Grid.SetColumn(ConversationPane, 2);
                Grid.SetColumnSpan(ConversationPane, 1);
                ChatsPane.Visibility = Visibility.Visible;
                ConversationPane.Visibility = Visibility.Visible;
                MenuButton.Visibility = Visibility.Visible;
                BackButton.Visibility = Visibility.Collapsed;
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
            }
            else if (_selectedChat == null || ChatsPane.Visibility == Visibility.Visible)
            {
                ChatColumn.Width = new GridLength(1, GridUnitType.Star);
                DividerColumn.Width = new GridLength(0);
                ConversationColumn.Width = new GridLength(0);
                Grid.SetColumn(ChatsPane, 0);
                Grid.SetColumnSpan(ChatsPane, 3);
                Grid.SetColumn(ConversationPane, 0);
                Grid.SetColumnSpan(ConversationPane, 3);
                ChatsPane.Visibility = Visibility.Visible;
                ConversationPane.Visibility = Visibility.Collapsed;
                MenuButton.Visibility = Visibility.Visible;
                BackButton.Visibility = Visibility.Collapsed;
            }
            UpdateHeaderActions(_selectedChat != null && ConversationPane.Visibility == Visibility.Visible);
        }

        private async Task<bool> StartClientAsync(string address, string password, bool closeSettings)
        {
            _messageLoadGeneration++;
            ResetMessageItems();
            ConnectButton.IsEnabled = false;
            ConnectProgress.IsActive = true;
            ConnectError.Text = string.Empty;
            try
            {
                if (_client != null) _client.Dispose();
                _client = new BlueBubblesClient(address, password);
                _chatStateSignature = null;
                await _client.TestConnectionAsync();
                try { _serverCapabilities = await _client.GetServerCapabilitiesAsync(); _serverCapabilitiesKnown = true; }
                catch { _serverCapabilities = new ServerCapabilities(); _serverCapabilitiesKnown = false; }
                UpdateServerDetails(address);
                try
                {
                    var identity = await _client.GetRegisteredPhoneNumberAsync();
                    NavigationIdentityText.Text = identity == "BlueBubbles" ? "LiveBubbles" : identity;
                }
                catch { NavigationIdentityText.Text = "LiveBubbles"; }
                SettingsStore.Save(address, password);
                SettingsStore.SaveSyncOptions(_messagesPerChat, _syncTimeframeDays);
                ShowInitialLoadingDots();
                SetSyncing(true, "Requesting chat list from BlueBubbles...");
                await LoadContactsAsync();
                Exception initialSyncError = null;
                try { await RefreshChatsAsync(); }
                catch (Exception ex) { initialSyncError = ex; }
                _pollTimer.Start();
                SetSettingsMode(true);
                if (closeSettings) SettingsOverlay.Visibility = Visibility.Collapsed;
                OpenPendingActivation();
                StartNotificationsWithoutWaiting();
                if (initialSyncError == null) ShowStatus(string.Empty, false);
                else ShowStatus(FriendlyError(initialSyncError, "load chats") + " BlueBubbles will retry automatically.", true);
                return true;
            }
            catch (Exception ex)
            {
                ConnectError.Text = FriendlyError(ex, "connect to the server");
                SetSettingsMode(false);
                SettingsOverlay.Visibility = Visibility.Visible;
                ShowStatus("Could not connect", true);
                return false;
            }
            finally
            {
                HideInitialLoadingDots();
                SetSyncing(false, null);
                ConnectButton.IsEnabled = true;
                ConnectProgress.IsActive = false;
            }
        }

        private async Task<bool> RefreshChatsAsync()
        {
            var client = _client;
            if (client == null) return false;
            var timeframeDays = _syncTimeframeDays;
            var loadGeneration = ++_chatLoadGeneration;
            var chats = (await client.GetChatsAsync(timeframeDays)).OrderByDescending(chat => chat.LastMessageTimestamp).ThenBy(chat => chat.Title).ToList();
            if (loadGeneration != _chatLoadGeneration || client != _client || timeframeDays != _syncTimeframeDays) return false;
            foreach (var chat in chats)
            {
                chat.ApplyContactData(_contactNames, _contactImages, _contactTileImages);
                if (chat.IsGroupChat)
                {
                    var iconUri = _client.GetGroupIconUri(chat.Guid);
                    if (!string.IsNullOrWhiteSpace(iconUri)) chat.AvatarSource = new BitmapImage(new Uri(iconUri));
                }
            }
            UpdateNotificationContext();
            NotificationStateStore.ReconcileReadState(chats);
            var signature = BuildChatStateSignature(chats);
            if (signature == _chatStateSignature) return false;
            var selectedGuid = _selectedChat == null ? null : _selectedChat.Guid;
            _allChats.Clear();
            _allChats.AddRange(chats);
            _chatStateSignature = signature;
            var activeCount = _allChats.Count(chat => !chat.IsArchived);
            var archivedCount = _allChats.Count - activeCount;
            for (var index = 0; index < _allChats.Count; index++)
            {
                var current = _allChats[index];
                SetSyncing(true, "Indexing " + (index + 1) + " of " + _allChats.Count + " chats (" + activeCount + " active, " + archivedCount + " archived): " + current.Title);
            }
            ApplyChatSearch();
            if (selectedGuid != null) _selectedChat = _allChats.FirstOrDefault(c => c.Guid == selectedGuid) ?? _selectedChat;
            return true;
        }

        private static string BuildChatStateSignature(IEnumerable<ChatItem> chats)
        {
            return string.Join("|", chats.OrderBy(chat => chat.Guid).Select(chat => chat.Guid + ":" + chat.LastMessageGuid + ":" + chat.LastMessageTimestamp + ":" + chat.IsArchived + ":" + chat.IsUnread + ":" + chat.Service + ":" + chat.Title));
        }

        private void ResetMessageItems()
        {
            // Tear down message visuals so no recycled text or media survives a chat change.
            MessagesList.ItemsSource = null;
            _messages.Clear();
            MessagesList.ItemsSource = _messages;
        }

        private async Task RefreshMessagesAsync(bool forceScroll)
        {
            var client = _client;
            var selectedChat = _selectedChat;
            if (client == null || selectedChat == null) return;
            var selectedGuid = selectedChat.Guid;
            // A newer refresh supersedes every older request, even for the same chat.
            var loadGeneration = ++_messageLoadGeneration;
            var received = await client.GetMessagesAsync(selectedGuid, _messagesPerChat, _syncTimeframeDays);
            if (loadGeneration != _messageLoadGeneration || _selectedChat == null || !string.Equals(_selectedChat.Guid, selectedGuid, StringComparison.OrdinalIgnoreCase)) return;
            var priorLastGuid = _messages.Count == 0 ? null : _messages[_messages.Count - 1].Guid;
            var newLastGuid = received.Count == 0 ? null : received[received.Count - 1].Guid;
            if (!forceScroll && priorLastGuid == newLastGuid && _messages.Count == received.Count) return;
            var total = received.Count;
            _pinMessagesToBottomUntil = DateTimeOffset.Now.AddSeconds(forceScroll ? 15 : 4);
            SetSyncing(true, "Syncing messages: 0 of " + total);
            ResetMessageItems();
            for (var i = 0; i < total; i++)
            {
                var message = received[i];
                message.UsesSmsColor = selectedChat.UsesSmsColor;
                message.ResolveSender(selectedChat.IsGroupChat, _contactNames);
                if (message.IsImageAttachment) message.SetAttachmentUri(client.GetAttachmentDownloadUri(message.AttachmentGuid));
                else if (message.IsVideoAttachment) await PrepareVideoAsync(client, message);
                _messages.Add(message);
                SetSyncing(true, "Syncing messages: " + (i + 1) + " of " + total);
            }
            await ScrollToNewestMessageAsync();
        }

        private async Task ScrollToNewestMessageAsync()
        {
            if (_messages.Count == 0) return;
            var newestMessage = _messages[_messages.Count - 1];
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                MessagesList.UpdateLayout();
                MessagesList.ScrollIntoView(newestMessage, ScrollIntoViewAlignment.Default);
            });
        }

        private async void PollTimer_Tick(object sender, object e)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            SetSyncing(true, "Refreshing chat list from BlueBubbles...");
            try
            {
                var chatsChanged = await RefreshChatsAsync();
                if (chatsChanged)
                {
                    await RefreshMessagesAsync(false);
                    if (_selectedChat != null && ConversationPane.Visibility == Visibility.Visible) await MarkSelectedChatReadAsync();
                }
            }
            catch (Exception ex) { ShowStatus(FriendlyError(ex, "refresh chats"), true); }
            finally { _isRefreshing = false; SetSyncing(false, null); }
        }

        private async void ChatsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            StopTypingWithoutWaiting();
            _messageLoadGeneration++;
            ResetMessageItems();
            _selectedChat = e.ClickedItem as ChatItem;
            if (_selectedChat == null) return;
            UpdateHeaderActions(true);
            PageTitle.Text = _selectedChat.Title;
            EmptyConversation.Visibility = Visibility.Collapsed;
            MessagesList.Visibility = Visibility.Visible;
            Composer.Visibility = Visibility.Visible;
            if (UseSinglePaneLayout)
            {
                ReturnToConversation();
            }
            ShowStatus("Loading messages...", false);
            ShowInitialLoadingDots();
            try { SetSyncing(true, "Syncing messages..."); await RefreshMessagesAsync(true); await MarkSelectedChatReadAsync(); ShowStatus(string.Empty, false); }
            catch (Exception ex) { ShowStatus(FriendlyError(ex, "open the conversation"), true); }
            finally { HideInitialLoadingDots(); SetSyncing(false, null); }
        }

        private async Task SendCurrentMessageAsync()
        {
            var text = MessageBox.Text.Trim();
            if (_client == null || _selectedChat == null || (text.Length == 0 && _sharedFiles.Count == 0)) return;
            SendButton.IsEnabled = false;
            MessageBox.IsReadOnly = true;
            MessageBox.Opacity = 0.55;
            try
            {
                StopTypingWithoutWaiting();
                foreach (var file in _sharedFiles.ToList()) await _client.SendAttachmentAsync(_selectedChat.Guid, file);
                if (text.Length > 0) await _client.SendTextAsync(_selectedChat.Guid, text);
                MessageBox.Text = string.Empty;
                CompleteSharedContent();
                await RefreshMessagesAsync(true);
            }
            catch (Exception ex) { ShowStatus(FriendlyError(ex, "send the message"), true); }
            finally
            {
                SendButton.IsEnabled = true;
                MessageBox.IsReadOnly = false;
                MessageBox.Opacity = 1;
                if (ShouldRefocusAfterSend) MessageBox.Focus(FocusState.Programmatic);
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e) { await SendCurrentMessageAsync(); }
        private async void MessageBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (ShouldSendOnEnter(e))
            {
                e.Handled = true;
                await SendCurrentMessageAsync();
            }
        }

        private bool ShouldSendOnEnter(KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return false;
            if (string.Equals(AnalyticsInfo.DeviceForm, "Phone", StringComparison.OrdinalIgnoreCase)) return false;
            return !Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        }

        private bool ShouldRefocusAfterSend
        {
            get { return !string.Equals(AnalyticsInfo.DeviceForm, "Phone", StringComparison.OrdinalIgnoreCase); }
        }

        private async void MessageBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            await UpdateTypingAsync(_selectedChat, MessageBox.Text);
        }

        private async void ComposeMessageBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            await UpdateTypingAsync(_composeSelectedChat, ComposeMessageBox.Text);
        }

        private async Task UpdateTypingAsync(ChatItem chat, string text)
        {
            if (!SendTypingIndicatorsToggle.IsOn || !_serverCapabilities.CanUsePrivateApi || _client == null || chat == null || string.IsNullOrWhiteSpace(chat.Guid)) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                await StopTypingAsync();
                return;
            }

            _typingStopTimer.Stop();
            if (!string.Equals(_typingChatGuid, chat.Guid, StringComparison.OrdinalIgnoreCase))
            {
                await StopTypingAsync();
                _typingChatGuid = chat.Guid;
                try
                {
                    await _client.StartTypingAsync(chat.Guid);
                }
                catch (Exception ex)
                {
                    if (string.Equals(_typingChatGuid, chat.Guid, StringComparison.OrdinalIgnoreCase)) _typingChatGuid = null;
                    ShowStatus(FriendlyError(ex, "update typing status"), true);
                }
            }
            _typingStopTimer.Start();
        }

        private async void TypingStopTimer_Tick(object sender, object e)
        {
            await StopTypingAsync();
        }

        private async Task StopTypingAsync()
        {
            _typingStopTimer.Stop();
            var guid = _typingChatGuid;
            _typingChatGuid = null;
            if (!_serverCapabilities.CanUsePrivateApi || _client == null || string.IsNullOrWhiteSpace(guid)) return;
            try { await _client.StopTypingAsync(guid); }
            catch (Exception ex) { ShowStatus(FriendlyError(ex, "update typing status"), true); }
        }

        private async void StopTypingWithoutWaiting()
        {
            await StopTypingAsync();
        }

        private async Task MarkSelectedChatReadAsync()
        {
            var chat = _selectedChat;
            if (chat == null) return;
            var sendReceipt = _serverCapabilities.CanUsePrivateApi && SendReadReceiptsToggle.IsOn && chat.IsUnread;
            chat.IsUnread = false;
            NotificationStateStore.MarkRead(chat.Guid);
            UpdateNotificationContext();
            await LiveBubbles.Notifications.NotificationBridge.MarkReadAsync(chat.Guid, chat.LastMessageTimestamp);
            if (!sendReceipt || _client == null || string.IsNullOrWhiteSpace(chat.Guid)) return;
            try
            {
                await _client.MarkChatReadAsync(chat.Guid);
            }
            catch (Exception ex)
            {
                ShowStatus(FriendlyError(ex, "send the read receipt"), true);
            }
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            await StartClientAsync(ServerAddressBox.Text, ServerPasswordBox.Password, true);
        }

        private void ManualSetup_Click(object sender, RoutedEventArgs e)
        {
            ManualSetupPanel.Visibility = Visibility.Visible;
            QrSetupPanel.Visibility = Visibility.Collapsed;
            ConnectError.Text = string.Empty;
        }

        private void QrSetup_Click(object sender, RoutedEventArgs e)
        {
            ManualSetupPanel.Visibility = Visibility.Collapsed;
            QrSetupPanel.Visibility = Visibility.Visible;
            ConnectError.Text = string.Empty;
        }

        private async Task<bool> ConnectQrPayloadAsync(string payloadText)
        {
            QrSetupPayload payload;
            string error;
            if (!QrSetupPayload.TryParse(payloadText, out payload, out error))
            {
                ConnectError.Text = error;
                return false;
            }

            ServerAddressBox.Text = payload.Address;
            ServerPasswordBox.Password = payload.Password;
            return await StartClientAsync(payload.Address, payload.Password, true);
        }

        private async void ScanQr_Click(object sender, RoutedEventArgs e)
        {
            ConnectError.Text = string.Empty;
            try
            {
                _qrScanner?.Dispose();
                _qrScanner = new QrCameraScanner();
                QrCameraPreview.Visibility = Visibility.Visible;
                QrConnectProgress.IsActive = false;
                QrScannerStatus.Text = "Point the camera at the BlueBubbles setup QR code";
                QrScannerOverlay.Visibility = Visibility.Visible;
                await _qrScanner.StartAsync(QrCameraPreview);
                _qrScanTimer.Start();
            }
            catch (Exception ex)
            {
                await StopQrScannerAsync();
                ConnectError.Text = "Camera access is needed to scan a setup QR code: " + ex.Message;
            }
        }

        private async void QrScanTimer_Tick(object sender, object e)
        {
            if (_isQrScanInProgress || _qrScanner == null) return;
            _isQrScanInProgress = true;
            try
            {
                var payloadText = await _qrScanner.TryReadAsync();
                if (string.IsNullOrWhiteSpace(payloadText)) return;
                _qrScanTimer.Stop();
                await _qrScanner.StopAsync(QrCameraPreview);
                QrCameraPreview.Visibility = Visibility.Collapsed;
                QrScannerStatus.Text = "QR code found. Connecting to BlueBubbles...";
                QrConnectProgress.IsActive = true;
                await ConnectQrPayloadAsync(payloadText);
                QrConnectProgress.IsActive = false;
                QrScannerOverlay.Visibility = Visibility.Collapsed;
            }
            catch { }
            finally { _isQrScanInProgress = false; }
        }

        private async void CancelQrScan_Click(object sender, RoutedEventArgs e) { await StopQrScannerAsync(); }

        private async Task StopQrScannerAsync()
        {
            _qrScanTimer.Stop();
            if (_qrScanner != null) await _qrScanner.StopAsync(QrCameraPreview);
            QrCameraPreview.Visibility = Visibility.Visible;
            QrConnectProgress.IsActive = false;
            QrScannerOverlay.Visibility = Visibility.Collapsed;
        }

        private async void Attach_Click(object sender, RoutedEventArgs e)
        {
            if (_client == null || _selectedChat == null) return;
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".heic");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".m4v");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".wmv");
            picker.FileTypeFilter.Add(".pdf");
            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;
            _sharedFiles.Clear();
            _sharedFiles.AddRange(files);
            StageSharedContentInComposer();
        }

        private void Message_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started) return;
            e.Handled = true;
            var element = sender as FrameworkElement;
            var message = element?.DataContext as MessageItem;
            ShowMessageContextMenu(element, message);
        }

        private void Message_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var element = sender as FrameworkElement;
            ShowMessageContextMenu(element, element?.DataContext as MessageItem);
        }

        private void ShowMessageContextMenu(FrameworkElement element, MessageItem message)
        {
            ShowMessageActions(element, message);
        }

        private void ShowMessageActions(FrameworkElement element, MessageItem message)
        {
            if (_contextMenuOpen || element == null || message == null) return;
            var actions = new List<Tuple<string, Func<Task>, string>>();
            var hasText = !string.IsNullOrWhiteSpace(message.Text);
            var hasMedia = !string.IsNullOrWhiteSpace(message.AttachmentGuid);
            if (_serverCapabilities.CanUsePrivateApi && _client != null && _selectedChat != null && !string.IsNullOrWhiteSpace(_selectedChat.Guid) && !string.IsNullOrWhiteSpace(message.Guid))
                actions.Add(Tuple.Create<string, Func<Task>, string>("Delete", () => DeleteMessageAsync(message), "delete the message"));
            if (hasText || hasMedia)
                actions.Add(Tuple.Create<string, Func<Task>, string>("Forward", () => ForwardMessageAsync(message), "forward the message"));
            if (hasText)
            {
                actions.Add(Tuple.Create<string, Func<Task>, string>("Copy", () =>
                {
                    var data = new DataPackage();
                    data.SetText(message.Text);
                    Clipboard.SetContent(data);
                    Clipboard.Flush();
                    return Task.CompletedTask;
                }, "copy the message"));
            }
            if (hasMedia && message.IsImageAttachment)
            {
                actions.Add(Tuple.Create<string, Func<Task>, string>("Save", async () =>
                {
                    var bytes = await _client.DownloadAttachmentAsync(message.AttachmentGuid);
                    var file = await KnownFolders.PicturesLibrary.CreateFileAsync(GetMediaFileName(message), CreationCollisionOption.GenerateUniqueName);
                    await FileIO.WriteBytesAsync(file, bytes);
                    await new MessageDialog("Saved to Pictures.", "BlueBubbles").ShowAsync();
                }, "save the media"));
            }
            if (actions.Count > 0) ShowDarkActionFlyout(element, actions);
        }

        private void Media_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started) return;
            e.Handled = true;
            var element = sender as FrameworkElement;
            ShowMediaContextMenu(element, element?.DataContext as MessageItem);
        }

        private void Media_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            var element = sender as FrameworkElement;
            ShowMediaContextMenu(element, element?.DataContext as MessageItem);
        }

        private void ShowMediaContextMenu(FrameworkElement element, MessageItem message)
        {
            ShowMessageActions(element, message);
        }

        private void ShowDarkActionFlyout(FrameworkElement element, IReadOnlyList<Tuple<string, Func<Task>, string>> actions)
        {
            _contextMenuOpen = true;
            var content = new StackPanel { Width = LargerUiToggle.IsOn ? 192 : 168, Background = new SolidColorBrush(Windows.UI.Colors.Black) };
            var presenterStyle = new Style(typeof(FlyoutPresenter));
            presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Windows.UI.Colors.Black)));
            presenterStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Windows.UI.Colors.White)));
            presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            var flyout = new Flyout { Content = content, Placement = FlyoutPlacementMode.Top, FlyoutPresenterStyle = presenterStyle };
            flyout.Closed += (sender, args) => _contextMenuOpen = false;
            foreach (var choice in actions)
            {
                var button = new Button
                {
                    Content = choice.Item1,
                    Width = content.Width,
                    Background = new SolidColorBrush(Windows.UI.Colors.Black),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(16, 10, 16, 10),
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                button.Click += async (sender, args) =>
                {
                    flyout.Hide();
                    await Task.Delay(40);
                    try { await choice.Item2(); }
                    catch (Exception ex) { ShowStatus(FriendlyError(ex, choice.Item3), true); }
                };
                content.Children.Add(button);
            }
            try { flyout.ShowAt(element); }
            catch (Exception ex)
            {
                _contextMenuOpen = false;
                ShowStatus(FriendlyError(ex, "open the message menu"), true);
            }
        }

        private static string GetMediaFileName(MessageItem message)
        {
            var fileName = message.AttachmentLabel;
            if (string.IsNullOrWhiteSpace(fileName) || string.Equals(fileName, "Attachment available", StringComparison.OrdinalIgnoreCase))
            {
                var extension = message.IsVideoAttachment ? ".mp4" : ".jpg";
                if (string.Equals(message.AttachmentMimeType, "image/png", StringComparison.OrdinalIgnoreCase)) extension = ".png";
                else if (string.Equals(message.AttachmentMimeType, "image/gif", StringComparison.OrdinalIgnoreCase)) extension = ".gif";
                else if (string.Equals(message.AttachmentMimeType, "image/heic", StringComparison.OrdinalIgnoreCase)) extension = ".heic";
                else if (message.AttachmentMimeType.IndexOf("quicktime", StringComparison.OrdinalIgnoreCase) >= 0) extension = ".mov";
                fileName = "BlueBubbles-" + (string.IsNullOrWhiteSpace(message.Guid) ? Guid.NewGuid().ToString("N") : message.Guid) + extension;
            }
            foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
            return fileName;
        }

        private void Compose_Click(object sender, RoutedEventArgs e)
        {
            OpenCompose();
        }

        private void OpenCompose()
        {
            StopTypingWithoutWaiting();
            RecipientBox.Text = string.Empty;
            ComposeMessageBox.Text = string.Empty;
            _composeSelectedChat = null;
            _composeService = null;
            _recipientMatches.Clear();
            foreach (var chat in _allChats.Take(12)) _recipientMatches.Add(chat);
            ComposeOverlay.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(_sharedText)) ComposeMessageBox.Text = _sharedText;
            SharedComposePreview.Text = BuildSharedPreview();
            SharedComposePreview.Visibility = string.IsNullOrWhiteSpace(SharedComposePreview.Text) ? Visibility.Collapsed : Visibility.Visible;
            RecipientBox.Focus(FocusState.Programmatic);
        }

        public void OpenComposeForRecipient(string recipient)
        {
            if (string.IsNullOrWhiteSpace(recipient)) return;
            OpenCompose();
            RecipientBox.Text = recipient;
        }

        public async void OpenChatFromNotification(string chatGuid)
        {
            if (string.IsNullOrWhiteSpace(chatGuid)) return;
            var activationGeneration = ++_messageLoadGeneration;
            var chat = await FindNotificationChatAsync(chatGuid);
            if (activationGeneration != _messageLoadGeneration || _client == null) return;
            if (chat == null) { ShowStatus("This conversation is unavailable. Reconnect and try again.", true); return; }
            SettingsOverlay.Visibility = Visibility.Collapsed;
            _messageLoadGeneration++;
            ResetMessageItems();
            _selectedChat = chat;
            UpdateHeaderActions(true);
            PageTitle.Text = chat.Title;
            EmptyConversation.Visibility = Visibility.Collapsed;
            MessagesList.Visibility = Visibility.Visible;
            Composer.Visibility = Visibility.Visible;
            if (UseSinglePaneLayout) ReturnToConversation();
            try { await RefreshMessagesAsync(true); await MarkSelectedChatReadAsync(); }
            catch (Exception ex) { ShowStatus(FriendlyError(ex, "open the conversation"), true); }
        }

        private void CloseCompose_Click(object sender, RoutedEventArgs e)
        {
            StopTypingWithoutWaiting();
            ComposeOverlay.Visibility = Visibility.Collapsed;
            if (_shareOperation != null) CompleteSharedContent();
        }

        private void RecipientMatchesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatItem;
            if (chat == null) return;
            _updatingRecipient = true;
            RecipientBox.Text = chat.Title;
            _updatingRecipient = false;
            _composeSelectedChat = chat;
            SetComposeService(chat.Service);
            ComposeHint.Text = "Selected conversation: " + chat.Title;
            ComposeMessageBox.Focus(FocusState.Programmatic);
        }

        private void RecipientBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = RecipientBox.Text.Trim();
            if (!_updatingRecipient) _composeSelectedChat = null;
            _recipientMatches.Clear();
            foreach (var chat in _allChats.Where(chat => string.IsNullOrWhiteSpace(query) || chat.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || chat.ParticipantSummary.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (chat.ParticipantAddresses != null && chat.ParticipantAddresses.Any(address => address.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))).Take(20)) _recipientMatches.Add(chat);
            ComposeHint.Text = string.IsNullOrWhiteSpace(query) ? "Enter one or more phone numbers or email addresses, then write a message." : "Select an existing conversation or send to the addresses entered.";
            _availabilityTimer.Stop();
            _availabilityGeneration++;
            if (_composeSelectedChat != null) SetComposeService(_composeSelectedChat.Service);
            else if (ParseRecipients(query).Count > 0)
            {
                if (_serverCapabilities.CanUsePrivateApi) _availabilityTimer.Start();
            }
            else SetComposeService(null);
        }

        private async void AvailabilityTimer_Tick(object sender, object e)
        {
            _availabilityTimer.Stop();
            if (_client == null || !_serverCapabilities.CanUsePrivateApi || _composeSelectedChat != null) return;
            var recipients = ParseRecipients(RecipientBox.Text);
            if (recipients.Count == 0) return;
            var generation = ++_availabilityGeneration;
            try
            {
                var allIMessage = true;
                foreach (var recipient in recipients)
                {
                    if (!await _client.GetIMessageAvailabilityAsync(recipient)) allIMessage = false;
                    if (generation != _availabilityGeneration) return;
                }
                SetComposeService(allIMessage ? "iMessage" : "SMS");
            }
            catch
            {
                if (generation == _availabilityGeneration)
                {
                    _composeService = null;
                }
            }
        }

        private void SetComposeService(string service)
        {
            _composeService = service;
        }

        private static List<string> ParseRecipients(string text)
        {
            return (text ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async void ComposeSend_Click(object sender, RoutedEventArgs e) { await SendComposedMessageAsync(); }

        private async void ComposeMessageBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!ShouldSendOnEnter(e)) return;
            e.Handled = true;
            await SendComposedMessageAsync();
        }

        private async Task SendComposedMessageAsync()
        {
            var fromShareTarget = _shareOperation != null;
            if (_client == null && !await WaitForClientAsync())
            {
                FinishFailedCompose(fromShareTarget, "BlueBubbles is not connected to the server.");
                return;
            }
            var message = ComposeMessageBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(message) && _sharedFiles.Count == 0) { ShowStatus("Write a message or choose an attachment before sending.", true); return; }
            var recipient = RecipientBox.Text.Trim();
            var recipients = ParseRecipients(recipient);
            if (_composeSelectedChat == null) _composeSelectedChat = FindExistingChat(recipients);
            if (_composeSelectedChat == null && recipients.Count == 1) _composeSelectedChat = await _client.FindDirectChatAsync(recipients[0], _composeService);
            if (_composeSelectedChat == null && recipients.Count == 0) { ShowStatus("Enter at least one phone number or email address.", true); return; }
            if (_composeSelectedChat == null && recipients.Count > 1 && !_serverCapabilities.CanUsePrivateApi) { ShowStatus("Creating group chats requires the BlueBubbles Private API helper.", true); return; }
            if (_composeSelectedChat == null && string.IsNullOrWhiteSpace(message))
            {
                var error = "A text message is required to start a brand-new conversation before sending attachments.";
                if (fromShareTarget) FinishFailedCompose(true, error);
                else ShowStatus(error, true);
                return;
            }

            var sendAccepted = false;
            ComposeSendButton.IsEnabled = false;
            ComposeMessageBox.IsReadOnly = true;
            RecipientBox.IsReadOnly = true;
            ComposeMessageBox.Opacity = RecipientBox.Opacity = 0.55;
            try
            {
                SetSyncing(true, "Sending message...");
                ChatItem chat = _composeSelectedChat;
                if (chat == null)
                {
                    chat = await _client.CreateChatAsync(recipients, message, _composeService);
                    sendAccepted = true;
                    if (chat == null || string.IsNullOrWhiteSpace(chat.Guid))
                    {
                        await RefreshChatsAsync();
                        chat = FindExistingChat(recipients);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(message))
                {
                    await _client.SendTextAsync(chat.Guid, message);
                    sendAccepted = true;
                }
                if (chat == null || string.IsNullOrWhiteSpace(chat.Guid)) throw new InvalidOperationException("BlueBubbles accepted the message but did not return the conversation.");
                foreach (var file in _sharedFiles.ToList())
                {
                    await _client.SendAttachmentAsync(chat.Guid, file);
                    sendAccepted = true;
                }
                if (string.IsNullOrWhiteSpace(chat.Title)) chat.Title = string.IsNullOrWhiteSpace(recipient) ? "Conversation" : recipient;
                ComposeMessageBox.Text = string.Empty;
                ComposeOverlay.Visibility = Visibility.Collapsed;
                CompleteSharedContent();
                OpenConversation(chat);
                try
                {
                    await RefreshChatsAsync();
                    var refreshed = _allChats.FirstOrDefault(item => item.Guid == chat.Guid);
                    if (refreshed != null) OpenConversation(refreshed);
                    await RefreshMessagesAsync(true);
                }
                catch (Exception ex)
                {
                    ShowStatus("The message was sent, but the conversation could not be refreshed: " + FriendlyError(ex, "refresh the conversation"), true);
                }
            }
            catch (Exception ex)
            {
                FinishFailedCompose(fromShareTarget, sendAccepted
                    ? "The message was accepted, but BlueBubbles could not open its conversation."
                    : FriendlyError(ex, "send the message"));
            }
            finally
            {
                ComposeSendButton.IsEnabled = true;
                ComposeMessageBox.IsReadOnly = false;
                RecipientBox.IsReadOnly = false;
                ComposeMessageBox.Opacity = RecipientBox.Opacity = 1;
                if (ComposeOverlay.Visibility == Visibility.Visible && ShouldRefocusAfterSend) ComposeMessageBox.Focus(FocusState.Programmatic);
                SetSyncing(false, null);
            }
        }

        private async Task<bool> WaitForClientAsync()
        {
            for (var attempt = 0; attempt < 100 && _client == null; attempt++) await Task.Delay(100);
            return _client != null;
        }

        private ChatItem FindExistingChat(IReadOnlyList<string> recipients)
        {
            if (recipients == null || recipients.Count == 0) return null;
            var expected = new HashSet<string>(recipients.Select(NormalizeRecipient).Where(value => value.Length > 0), StringComparer.OrdinalIgnoreCase);
            if (expected.Count == 0) return null;
            return _allChats.FirstOrDefault(chat =>
            {
                var actual = new HashSet<string>((chat.ParticipantAddresses ?? new List<string>()).Select(NormalizeRecipient).Where(value => value.Length > 0), StringComparer.OrdinalIgnoreCase);
                return actual.SetEquals(expected);
            });
        }

        private static string NormalizeRecipient(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            if (value.IndexOf('@') >= 0) return value.Trim().ToLowerInvariant();
            var digits = new string(value.Where(char.IsDigit).ToArray());
            return digits.Length == 11 && digits.StartsWith("1") ? digits.Substring(1) : digits.Length > 10 ? digits.Substring(digits.Length - 10) : digits;
        }

        private void OpenConversation(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.Guid)) { ReturnToChats(); return; }
            _messageLoadGeneration++;
            ResetMessageItems();
            _selectedChat = chat;
            PageTitle.Text = chat.Title;
            EmptyConversation.Visibility = Visibility.Collapsed;
            MessagesList.Visibility = Visibility.Visible;
            Composer.Visibility = Visibility.Visible;
            UpdateHeaderActions(true);
            if (UseSinglePaneLayout) ReturnToConversation();
        }

        private void FinishFailedCompose(bool fromShareTarget, string error)
        {
            ComposeOverlay.Visibility = Visibility.Collapsed;
            ReturnToChats();
            if (fromShareTarget) FailSharedContent(error);
            ShowStatus(error, true);
        }

        private async void ChooseContact_Click(object sender, RoutedEventArgs e)
        {
            _contactsForCompose = true;
            ComposeOverlay.Visibility = Visibility.Collapsed;
            await OpenContactsAsync();
        }

        private async void Contacts_Click(object sender, RoutedEventArgs e)
        {
            _contactsForCompose = false;
            NavigationSplitView.IsPaneOpen = false;
            await OpenContactsAsync();
        }

        private async Task OpenContactsAsync()
        {
            if (_allContacts.Count == 0) await LoadContactsAsync();
            ContactsSearchBox.Text = string.Empty;
            ApplyContactsSearch();
            ContactsOverlay.Visibility = Visibility.Visible;
            if (_allContacts.Count == 0) ShowStatus("No phone contacts are available. Check the contacts permission in Settings.", true);
        }

        private void CloseContacts_Click(object sender, RoutedEventArgs e)
        {
            ContactsOverlay.Visibility = Visibility.Collapsed;
            if (_contactsForCompose) ComposeOverlay.Visibility = Visibility.Visible;
        }

        private void ContactsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyContactsSearch();
        }

        private void ApplyContactsSearch()
        {
            var query = ContactsSearchBox == null ? string.Empty : ContactsSearchBox.Text.Trim();
            _contacts.Clear();
            foreach (var contact in _allContacts.Where(contact => string.IsNullOrWhiteSpace(query) || contact.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || contact.Address.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).Take(100)) _contacts.Add(contact);
        }

        private void ContactsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var contact = e.ClickedItem as ContactChoice;
            if (contact == null) return;
            ContactsOverlay.Visibility = Visibility.Collapsed;
            if (_contactsForCompose)
            {
                var recipients = ParseRecipients(RecipientBox.Text);
                if (!recipients.Contains(contact.Address, StringComparer.OrdinalIgnoreCase)) recipients.Add(contact.Address);
                RecipientBox.Text = string.Join(", ", recipients);
                ComposeOverlay.Visibility = Visibility.Visible;
                RecipientBox.Focus(FocusState.Programmatic);
            }
            else OpenComposeForRecipient(contact.Address);
        }

        private async void MessagesPerChatSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (!_settingsLoaded) return;
            _messagesPerChat = Math.Max(1, (int)Math.Round(e.NewValue));
            SettingsStore.SaveSyncOptions(_messagesPerChat, _syncTimeframeDays);
            UpdateSyncDescription();
            if (_selectedChat != null) await RefreshMessagesAsync(true);
        }

        private async void SyncTimeframeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_settingsLoaded) return;
            var item = SyncTimeframeBox.SelectedItem as ComboBoxItem;
            if (item?.Tag != null) _syncTimeframeDays = int.Parse(item.Tag.ToString());
            SettingsStore.SaveSyncOptions(_messagesPerChat, _syncTimeframeDays);
            UpdateSyncDescription();
            await RefreshChatsAsync();
            if (_selectedChat != null) await RefreshMessagesAsync(true);
        }

        private void SelectTimeframe(int days)
        {
            for (var i = 0; i < SyncTimeframeBox.Items.Count; i++)
            {
                var item = SyncTimeframeBox.Items[i] as ComboBoxItem;
                if (item?.Tag?.ToString() == days.ToString()) { SyncTimeframeBox.SelectedIndex = i; return; }
            }
            SyncTimeframeBox.SelectedIndex = 0;
        }

        private void PollIntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_settingsLoaded) return;
            var item = PollIntervalBox.SelectedItem as ComboBoxItem;
            int seconds;
            if (item?.Tag == null || !int.TryParse(item.Tag.ToString(), out seconds)) return;
            _pollTimer.Interval = TimeSpan.FromSeconds(seconds);
            SettingsStore.SavePollInterval(seconds);
            if (_client != null)
            {
                _pollTimer.Stop();
                _pollTimer.Start();
            }
        }

        private void SelectPollInterval(int seconds)
        {
            for (var i = 0; i < PollIntervalBox.Items.Count; i++)
            {
                var item = PollIntervalBox.Items[i] as ComboBoxItem;
                if (item?.Tag?.ToString() == seconds.ToString()) { PollIntervalBox.SelectedIndex = i; return; }
            }
            PollIntervalBox.SelectedIndex = 1;
        }

        private string BuildSyncDescription()
        {
            var range = _syncTimeframeDays == 0 ? "all time" : _syncTimeframeDays == 7 ? "the last 7 days" : _syncTimeframeDays == 30 ? "the last 30 days" : "the last year";
            return "Loading the latest " + _messagesPerChat + " messages per chat from " + range + ".";
        }

        private void UpdateSyncDescription()
        {
            if (SyncDescription != null) SyncDescription.Text = BuildSyncDescription();
        }

        private void SetSyncing(bool syncing, string detail)
        {
            if (!UsePhoneSyncStatus) return;
            if (UseLegacyInAppSyncStatus && syncing)
            {
                StatusText.Text = detail ?? "Syncing...";
                StatusText.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White);
                StatusBar.Visibility = Visibility.Visible;
            }
            else if (UseLegacyInAppSyncStatus && !_statusIsError)
            {
                StatusBar.Visibility = Visibility.Collapsed;
            }
            UpdatePhoneSyncStatus(syncing, detail);
            if (UseLegacyInAppSyncStatus) UpdateLegacyInAppSyncStatus(syncing, detail);
        }

        private void ShowInitialLoadingDots()
        {
            if (InitialLoadingProgress == null) return;
            InitialLoadingProgress.IsIndeterminate = true;
            InitialLoadingProgress.Visibility = Visibility.Visible;
        }

        private void HideInitialLoadingDots()
        {
            if (InitialLoadingProgress == null) return;
            InitialLoadingProgress.IsIndeterminate = false;
            InitialLoadingProgress.Visibility = Visibility.Collapsed;
        }

        private static async Task EnsurePhoneStatusBarAsync()
        {
            try
            {
                var statusBarType = Type.GetType("Windows.UI.ViewManagement.StatusBar, Windows, ContentType=WindowsRuntime");
                var phoneStatus = statusBarType?.GetMethod("GetForCurrentView").Invoke(null, null);
                var action = phoneStatus?.GetType().GetMethod("ShowAsync").Invoke(phoneStatus, null) as Windows.Foundation.IAsyncAction;
                if (action != null) await action;
            }
            catch { }
        }

        private static void UpdatePhoneSyncStatus(bool syncing, string detail)
        {
            try
            {
                // The W10M status bar metadata is absent from the desktop SDK reference set.
                // This still invokes the native StatusBar.ProgressIndicator API on phone at runtime.
                var statusBarType = Type.GetType("Windows.UI.ViewManagement.StatusBar, Windows, ContentType=WindowsRuntime");
                if (statusBarType == null) return;
                var phoneStatus = statusBarType.GetMethod("GetForCurrentView").Invoke(null, null);
                var indicator = statusBarType.GetProperty("ProgressIndicator").GetValue(phoneStatus);
                var indicatorType = indicator.GetType();
                indicatorType.GetProperty("Text").SetValue(indicator, detail ?? string.Empty);
                indicatorType.GetProperty("ProgressValue").SetValue(indicator, null);
                indicatorType.GetMethod(syncing ? "ShowAsync" : "HideAsync").Invoke(indicator, null);
            }
            catch { }
        }

        // The old bottom in-app status implementation remains available for diagnostics.
        private static void UpdateLegacyInAppSyncStatus(bool syncing, string detail)
        {
            try
            {
                var statusBarType = Type.GetType("Windows.UI.ViewManagement.StatusBar, Windows, ContentType=WindowsRuntime");
                if (statusBarType == null) return;
                var phoneStatus = statusBarType.GetMethod("GetForCurrentView").Invoke(null, null);
                var indicator = statusBarType.GetProperty("ProgressIndicator").GetValue(phoneStatus);
                var indicatorType = indicator.GetType();
                indicatorType.GetProperty("Text").SetValue(indicator, detail ?? string.Empty);
                indicatorType.GetProperty("ProgressValue").SetValue(indicator, syncing ? (double?)0.5 : null);
                indicatorType.GetMethod(syncing ? "ShowAsync" : "HideAsync").Invoke(indicator, null);
            }
            catch { }
        }

        private void Menu_Click(object sender, RoutedEventArgs e) { NavigationSplitView.IsPaneOpen = !NavigationSplitView.IsPaneOpen; }
        private void Settings_Click(object sender, RoutedEventArgs e) { NavigationSplitView.IsPaneOpen = false; SetSettingsMode(true); SettingsOverlay.Visibility = Visibility.Visible; }
        private void CloseSettings_Click(object sender, RoutedEventArgs e) { if (_client != null) SettingsOverlay.Visibility = Visibility.Collapsed; }
        private void Chats_Click(object sender, RoutedEventArgs e) { ShowChatPage(false); }
        private void Archived_Click(object sender, RoutedEventArgs e) { ShowChatPage(true); }
        private void Back_Click(object sender, RoutedEventArgs e) { ReturnToChats(); }

        private void SetSettingsMode(bool connected)
        {
            SetupOnlyPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            ConnectedSettingsPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            SettingsBackButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            SettingsHeader.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_settingsLoaded) return;
            SettingsStore.SaveAppearance(_themeMode, AccentColorToggle.IsOn, LargerUiToggle.IsOn);
            ApplyTheme(_themeMode, AccentColorToggle.IsOn, LargerUiToggle.IsOn);
        }

        private void ThemeModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = ThemeModeBox.SelectedItem as ComboBoxItem;
            AppThemeMode mode;
            if (item?.Tag == null || !Enum.TryParse(item.Tag.ToString(), true, out mode)) return;
            _themeMode = mode;
            if (!_settingsLoaded) return;
            SettingsStore.SaveAppearance(_themeMode, AccentColorToggle.IsOn, LargerUiToggle.IsOn);
            ApplyTheme(_themeMode, AccentColorToggle.IsOn, LargerUiToggle.IsOn);
        }

        private void SelectThemeMode(AppThemeMode mode)
        {
            for (var i = 0; i < ThemeModeBox.Items.Count; i++)
            {
                var item = ThemeModeBox.Items[i] as ComboBoxItem;
                if (string.Equals(item?.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    ThemeModeBox.SelectedIndex = i;
                    return;
                }
            }
            ThemeModeBox.SelectedIndex = 0;
        }

        private void DeveloperModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settingsLoaded) SettingsStore.SaveDeveloperMode(DeveloperModeToggle.IsOn);
            UpdateDeveloperModePresentation();
        }

        private void SendReadReceiptsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settingsLoaded) SettingsStore.SaveSendReadReceipts(SendReadReceiptsToggle.IsOn);
        }

        private async void SendTypingIndicatorsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settingsLoaded) SettingsStore.SaveSendTypingIndicators(SendTypingIndicatorsToggle.IsOn);
            if (!SendTypingIndicatorsToggle.IsOn) await StopTypingAsync();
        }

        private void UpdateDeveloperModePresentation()
        {
            RefreshNotificationControls();
        }

        private void ApplyTheme(AppThemeMode selectedMode, bool useAccentColor, bool largerUi)
        {
            var mode = ResolveThemeMode(selectedMode);
            var resources = Application.Current.Resources;
            var blue = useAccentColor ? GetWindowsAccentColor() : Windows.UI.Color.FromArgb(255, 14, 99, 156);
            SetBrushColor(resources, "MessengerBlueBrush", blue);
            if (mode == AppThemeMode.Light)
            {
                RequestedTheme = ElementTheme.Light;
                SetBrushColor(resources, "IncomingMessageBrush", Windows.UI.Color.FromArgb(255, 229, 229, 234));
                SetBrushColor(resources, "AppBackgroundBrush", Windows.UI.Colors.White);
                SetBrushColor(resources, "PanelBackgroundBrush", Windows.UI.Color.FromArgb(255, 245, 245, 245));
                SetBrushColor(resources, "HeaderBackgroundBrush", Windows.UI.Colors.White);
                SetBrushColor(resources, "AppBorderBrush", Windows.UI.Color.FromArgb(255, 210, 210, 210));
                SetBrushColor(resources, "MutedTextBrush", Windows.UI.Color.FromArgb(255, 94, 94, 94));
            }
            else
            {
                RequestedTheme = ElementTheme.Dark;
                SetBrushColor(resources, "IncomingMessageBrush", Windows.UI.Color.FromArgb(255, 38, 52, 61));
                var oled = mode == AppThemeMode.Dark;
                SetBrushColor(resources, "AppBackgroundBrush", oled ? Windows.UI.Colors.Black : Windows.UI.Color.FromArgb(255, 7, 17, 23));
                SetBrushColor(resources, "PanelBackgroundBrush", oled ? Windows.UI.Colors.Black : Windows.UI.Color.FromArgb(255, 11, 23, 30));
                SetBrushColor(resources, "HeaderBackgroundBrush", oled ? Windows.UI.Colors.Black : Windows.UI.Color.FromArgb(255, 9, 20, 27));
                SetBrushColor(resources, "AppBorderBrush", Windows.UI.Color.FromArgb(255, 51, 67, 76));
                SetBrushColor(resources, "MutedTextBrush", Windows.UI.Color.FromArgb(255, 174, 184, 190));
            }
            foreach (var message in _messages) message.RefreshBubbleBrush();
            ApplyUiDensity(largerUi);
        }

        private AppThemeMode ResolveThemeMode(AppThemeMode mode)
        {
            if (mode != AppThemeMode.System) return mode;
            try
            {
                var color = _uiSettings.GetColorValue(UIColorType.Background);
                var luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
                return luminance < 128 ? AppThemeMode.Dark : AppThemeMode.Light;
            }
            catch { return AppThemeMode.Dark; }
        }

        private async void UiSettings_ColorValuesChanged(UISettings sender, object args)
        {
            if (_themeMode != AppThemeMode.System) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ApplyTheme(_themeMode, AccentColorToggle.IsOn, LargerUiToggle.IsOn));
        }

        private static Windows.UI.Color GetWindowsAccentColor()
        {
            try
            {
                object systemAccent;
                if (Application.Current.Resources.TryGetValue("SystemAccentColor", out systemAccent) && systemAccent is Windows.UI.Color)
                    return (Windows.UI.Color)systemAccent;
            }
            catch { }
            try { return new UISettings().GetColorValue(UIColorType.Accent); }
            catch { return Windows.UI.Color.FromArgb(255, 14, 99, 156); }
        }

        private void ApplyUiDensity(bool larger)
        {
            NavigationSplitView.OpenPaneLength = larger ? 310 : 250;
            NavigationPaneGrid.Padding = larger ? new Thickness(20, 28, 16, 18) : new Thickness(14, 20, 12, 14);
            NavigationAvatar.Width = NavigationAvatar.Height = larger ? 52 : 42;
            NavigationAvatar.CornerRadius = new CornerRadius(larger ? 26 : 21);
            NavigationIdentityText.FontSize = larger ? 26 : 21;
            foreach (var button in new[] { NavChatsButton, NavArchivedButton, NavContactsButton, NavSettingsButton })
            {
                button.FontSize = larger ? 22 : 18;
                button.MinHeight = larger ? 62 : 50;
                button.Padding = larger ? new Thickness(12, 0, 0, 0) : new Thickness(8, 0, 0, 0);
            }
            PrimaryHeaderRow.Height = new GridLength(larger ? 64 : 54);
            HeaderLeadingColumn.Width = new GridLength(larger ? 64 : 54);
            PageTitle.FontSize = larger ? 25 : 22;
            foreach (var row in new[] { SettingsHeaderRow, ComposeHeaderRow, ContactsHeaderRow }) row.Height = new GridLength(larger ? 64 : 54);
            foreach (var column in new[] { SettingsHeaderLeadingColumn, ComposeHeaderLeadingColumn, ContactsHeaderLeadingColumn }) column.Width = new GridLength(larger ? 64 : 54);
            foreach (var title in new[] { SettingsHeaderTitle, ComposeHeaderTitle, ContactsHeaderTitle }) title.FontSize = larger ? 25 : 22;
            var iconSize = larger ? 52 : 44;
            foreach (var button in new[] { MenuButton, BackButton, ChatActionsButton, SearchButton, ComposeButton, SettingsBackButton, ComposeBackButton, ContactsBackButton })
            {
                button.Width = iconSize;
                button.Height = iconSize;
                button.FontSize = larger ? 20 : 18;
            }
            var composerSize = larger ? 48 : 42;
            ComposerAttachColumn.Width = new GridLength(composerSize);
            ComposerSendColumn.Width = new GridLength(composerSize);
            AttachButton.Width = AttachButton.Height = composerSize;
            SendButton.Width = SendButton.Height = composerSize;
            MessageBox.FontSize = larger ? 18 : 16;
            MessageBox.MinHeight = larger ? 38 : 34;
            MessageBox.MaxHeight = larger ? 124 : 104;
            var itemStyle = new Style(typeof(ListViewItem));
            itemStyle.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(ListViewItem.MinHeightProperty, larger ? 82d : 68d));
            ChatsList.ItemContainerStyle = itemStyle;
            ChatsList.ItemTemplate = larger ? Resources["LargeChatTemplate"] as DataTemplate : _compactChatTemplate;
            MessagesList.ItemTemplate = larger ? Resources["LargeMessageTemplate"] as DataTemplate : _compactMessageTemplate;
            MessagesList.Padding = larger ? new Thickness(14, 10, 14, 10) : new Thickness(10, 7, 10, 7);
        }

        private static void SetBrushColor(ResourceDictionary resources, string key, Windows.UI.Color color)
        {
            var brush = resources[key] as SolidColorBrush;
            if (brush != null) brush.Color = color;
        }

        private void SetPackageVersion()
        {
            var version = Package.Current.Id.Version;
            PackageVersionText.Text = "v" + version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision;
        }

        private async void CheckForUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdateButton.IsEnabled = false;
            UpdateProgressRing.Visibility = Visibility.Visible;
            UpdateProgressRing.IsActive = true;
            UpdateStatusText.Text = "Checking GitHub...";
            try
            {
                var service = new GitHubUpdateService();
                var release = await service.GetLatestReleaseAsync();
                var currentId = Package.Current.Id.Version;
                var current = new Version(currentId.Major, currentId.Minor, currentId.Build, currentId.Revision);
                if (release.Version <= current)
                {
                    UpdateStatusText.Text = "LiveBubbles is up to date.";
                    await new MessageDialog("You already have the latest release.", "No update available").ShowAsync();
                    return;
                }
                UpdateStatusText.Text = "Downloading v" + release.Version.ToString(4) + "...";
                var progress = new Progress<double>(value => UpdateStatusText.Text = "Downloading v" + release.Version.ToString(4) + " - " + (int)(value * 100) + "%");
                var file = await service.DownloadAsync(release, progress);
                UpdateStatusText.Text = "Opening the Windows installer...";
                if (!await Launcher.LaunchFileAsync(file))
                {
                    UpdateStatusText.Text = "Windows could not open the installer. Opening the release page instead.";
                    Uri uri;
                    if (Uri.TryCreate(release.ReleaseUrl, UriKind.Absolute, out uri)) await Launcher.LaunchUriAsync(uri);
                }
            }
            catch (Exception ex) { UpdateStatusText.Text = "Update check failed. " + FriendlyError(ex, "reach GitHub"); }
            finally
            {
                UpdateProgressRing.IsActive = false;
                UpdateProgressRing.Visibility = Visibility.Collapsed;
                CheckForUpdateButton.IsEnabled = true;
            }
        }

        private async Task DeleteMessageAsync(MessageItem message)
        {
            if (_client == null || _selectedChat == null || !_serverCapabilities.CanUsePrivateApi) return;
            var dialog = new MessageDialog("Delete this message permanently from the BlueBubbles server? This cannot be undone. It will not unsend the message from other participants.", "Delete message?");
            var confirm = new UICommand("Delete permanently");
            dialog.Commands.Add(confirm);
            dialog.Commands.Add(new UICommand("Cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            if (await dialog.ShowAsync() != confirm) return;
            await _client.DeleteMessageAsync(_selectedChat.Guid, message.Guid);
            _messages.Remove(message);
            _chatStateSignature = null;
            await RefreshChatsAsync();
        }

        private async Task ForwardMessageAsync(MessageItem message)
        {
            if (_client == null) return;
            ClearSharedContent();
            _isForwarding = true;
            _sharedText = message.HasText ? message.Text : null;
            try
            {
                if (!string.IsNullOrWhiteSpace(message.AttachmentGuid))
                {
                    var folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("ForwardedMessages", CreationCollisionOption.OpenIfExists);
                    var file = await folder.CreateFileAsync(GetMediaFileName(message), CreationCollisionOption.GenerateUniqueName);
                    await FileIO.WriteBytesAsync(file, await _client.DownloadAttachmentAsync(message.AttachmentGuid));
                    _sharedFiles.Add(file);
                    _shareTemporaryFiles.Add(file);
                }
                OpenCompose();
            }
            catch
            {
                ClearSharedContent();
                throw;
            }
        }

        private void UpdateServerDetails(string address)
        {
            ServerDetailsText.Text = SanitizeServerAddress(address);
            PrivateApiStatusText.Text = !_serverCapabilitiesKnown ? "Status unavailable"
                : _serverCapabilities.CanUsePrivateApi ? "Connected"
                : _serverCapabilities.PrivateApiEnabled ? "Enabled, helper disconnected" : "Disabled";
            PrivateApiRefreshButton.Visibility = _client != null && (!_serverCapabilitiesKnown || !_serverCapabilities.CanUsePrivateApi)
                ? Visibility.Visible : Visibility.Collapsed;
            SendReadReceiptsToggle.IsEnabled = _serverCapabilities.CanUsePrivateApi;
            SendReadReceiptsToggle.Visibility = _serverCapabilities.CanUsePrivateApi ? Visibility.Visible : Visibility.Collapsed;
            SendTypingIndicatorsToggle.IsEnabled = _serverCapabilities.CanUsePrivateApi;
            SendTypingIndicatorsToggle.Visibility = _serverCapabilities.CanUsePrivateApi ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void PrivateApiRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_client == null) return;
            PrivateApiRefreshButton.IsEnabled = false;
            PrivateApiStatusText.Text = "Checking...";
            try
            {
                _serverCapabilities = await _client.RefreshServerCapabilitiesAsync();
                _serverCapabilitiesKnown = true;
                UpdateServerDetails(ServerAddressBox.Text);
            }
            catch (Exception ex)
            {
                _serverCapabilities = new ServerCapabilities();
                _serverCapabilitiesKnown = false;
                UpdateServerDetails(ServerAddressBox.Text);
                ShowStatus(FriendlyError(ex, "refresh Private API status"), true);
            }
            finally { PrivateApiRefreshButton.IsEnabled = true; }
        }

        private static string SanitizeServerAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "Unavailable";
            var value = address.Trim();
            if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = "http://" + value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return "Unavailable";
            return uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog("Reset LiveBubbles on this device? Messages will not be deleted from the server.", "Sign out and reset?");
            var confirm = new UICommand("Sign out and reset");
            dialog.Commands.Add(confirm);
            dialog.Commands.Add(new UICommand("Cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            if (await dialog.ShowAsync() != confirm) return;
            await SignOutLocallyAsync();
        }

        private async Task SignOutLocallyAsync()
        {
            _settingsLoaded = false;
            try { await LiveBubbles.Notifications.NotificationBridge.StopAsync(); } catch { }
            _messageLoadGeneration++;
            _pollTimer.Stop();
            _client?.Dispose();
            _client = null;
            _selectedChat = null;
            _allChats.Clear();
            _chats.Clear();
            ResetMessageItems();
            _chatStateSignature = null;
            SettingsStore.Clear();
            await ClearStorageFolderAsync(ApplicationData.Current.LocalFolder);
            await ClearStorageFolderAsync(ApplicationData.Current.TemporaryFolder);
            await ClearStorageFolderAsync(ApplicationData.Current.LocalCacheFolder);
            _allContacts.Clear();
            _contacts.Clear();
            _contactNames = new Dictionary<string, string>();
            _contactImages = new Dictionary<string, ImageSource>();
            _contactTileImages = new Dictionary<string, string>();
            ClearSharedContent();
            _themeMode = AppThemeMode.System;
            SelectThemeMode(_themeMode);
            AccentColorToggle.IsOn = false;
            SendReadReceiptsToggle.IsOn = false;
            SendTypingIndicatorsToggle.IsOn = false;
            DeveloperModeToggle.IsOn = false;
            LargerUiToggle.IsOn = false;
            _pollTimer.Interval = TimeSpan.FromSeconds(5);
            SelectPollInterval(5);
            ApplyTheme(AppThemeMode.System, false, false);
            _serverCapabilitiesKnown = false;
            _serverCapabilities = new ServerCapabilities();
            UpdateServerDetails(string.Empty);
            ServerAddressBox.Text = string.Empty;
            ServerPasswordBox.Password = string.Empty;
            SetSettingsMode(false);
            SettingsOverlay.Visibility = Visibility.Visible;
            ReturnToChats();
            RefreshNotificationControls();
            _settingsLoaded = true;
        }

        private static async Task ClearStorageFolderAsync(StorageFolder folder)
        {
            try
            {
                foreach (var item in await folder.GetItemsAsync())
                {
                    try { await item.DeleteAsync(StorageDeleteOption.PermanentDelete); }
                    catch { try { await item.DeleteAsync(); } catch { } }
                }
            }
            catch { }
        }

        private void ShowChatPage(bool archived)
        {
            NavigationSplitView.IsPaneOpen = false;
            _messageLoadGeneration++;
            ResetMessageItems();
            _showArchived = archived;
            SearchBox.Visibility = Visibility.Collapsed;
            SearchBox.Text = string.Empty;
            PageTitle.Text = archived ? "Archived" : "Chats";
            ApplyChatSearch();
            _selectedChat = null;
            EmptyConversation.Visibility = Visibility.Visible;
            MessagesList.Visibility = Visibility.Collapsed;
            Composer.Visibility = Visibility.Collapsed;
            UpdateHeaderActions(false);
            ReturnToChats();
        }

        private void UpdateHeaderActions(bool conversationOpen)
        {
            var keepDesktopActions = !UseSinglePaneLayout;
            if (ComposeButton != null) ComposeButton.Visibility = !conversationOpen || keepDesktopActions ? Visibility.Visible : Visibility.Collapsed;
            if (SearchButton != null) SearchButton.Visibility = !conversationOpen || keepDesktopActions ? Visibility.Visible : Visibility.Collapsed;
            if (ChatActionsButton != null) ChatActionsButton.Visibility = conversationOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ChatActions_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedChat == null) return;
            var notificationChat = _selectedChat;
            var mute = new UICommand(LiveBubbles.Notifications.NotificationBridge.IsMuted(notificationChat.Guid) ? "Unmute notifications" : "Mute notifications");
            var pin = new UICommand("Pin to Start");
            var rename = new UICommand("Rename group");
            var leave = new UICommand("Leave group");
            var delete = new UICommand("Delete chat");
            var menu = new PopupMenu();
            menu.Commands.Add(pin);
            menu.Commands.Add(mute);
            if (_selectedChat.IsGroupChat)
            {
                menu.Commands.Add(rename);
                menu.Commands.Add(leave);
            }
            menu.Commands.Add(delete);
            var point = ChatActionsButton.TransformToVisual(null).TransformPoint(new Point());
            var selected = await menu.ShowForSelectionAsync(new Rect(point, ChatActionsButton.RenderSize));
            if (selected == mute)
            {
                LiveBubbles.Notifications.NotificationBridge.SetMuted(notificationChat.Guid, !LiveBubbles.Notifications.NotificationBridge.IsMuted(notificationChat.Guid));
            }
            else if (selected == pin)
            {
                await PinSelectedChatAsync();
            }
            else if (selected == rename)
            {
                await RenameSelectedGroupAsync();
            }
            else if (selected == leave)
            {
                await LeaveSelectedGroupAsync();
            }
            else if (selected == delete)
            {
                await DeleteSelectedChatAsync();
            }
        }

        private async Task RenameSelectedGroupAsync()
        {
            if (_client == null || _selectedChat == null || !_selectedChat.IsGroupChat) return;
            if (!_serverCapabilities.CanUsePrivateApi) { ShowStatus("Renaming groups requires the BlueBubbles Private API helper.", true); return; }
            var nameBox = new TextBox { Text = _selectedChat.Title, PlaceholderText = "Group name" };
            var dialog = new ContentDialog { Title = "Rename group", Content = nameBox, PrimaryButtonText = "Rename", CloseButtonText = "Cancel" };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text)) return;
            try
            {
                SetSyncing(true, "Renaming group...");
                await _client.RenameGroupChatAsync(_selectedChat.Guid, nameBox.Text);
                _chatStateSignature = null;
                await RefreshChatsAsync();
                _selectedChat = _allChats.FirstOrDefault(chat => chat.Guid == _selectedChat.Guid) ?? _selectedChat;
                PageTitle.Text = _selectedChat.Title;
            }
            catch (Exception ex) { ShowStatus(FriendlyError(ex, "rename the conversation"), true); }
            finally { SetSyncing(false, null); }
        }

        private async Task LeaveSelectedGroupAsync()
        {
            if (_client == null || _selectedChat == null || !_selectedChat.IsGroupChat) return;
            if (!_serverCapabilities.CanUsePrivateApi) { ShowStatus("Leaving groups requires the BlueBubbles Private API helper.", true); return; }
            var dialog = new MessageDialog("Leave \"" + _selectedChat.Title + "\"? You may need another participant to add you again.", "Leave group?");
            var confirm = new UICommand("Leave group");
            dialog.Commands.Add(confirm);
            dialog.Commands.Add(new UICommand("Cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            if (await dialog.ShowAsync() != confirm) return;
            try
            {
                SetSyncing(true, "Leaving group...");
                await _client.LeaveGroupChatAsync(_selectedChat.Guid);
                ShowChatPage(_showArchived);
                _chatStateSignature = null;
                await RefreshChatsAsync();
            }
            catch (Exception ex) { ShowStatus(FriendlyError(ex, "leave the conversation"), true); }
            finally { SetSyncing(false, null); }
        }

        private async Task PinSelectedChatAsync()
        {
            if (_selectedChat == null) return;
            var tileId = "chat-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(_selectedChat.Guid)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (SecondaryTile.Exists(tileId)) { ShowStatus("This conversation is already pinned to Start.", false); return; }
            var logoUri = await GetTileImageUriAsync(_selectedChat, tileId);
            var tile = new SecondaryTile(tileId, _selectedChat.Title, "chat=" + Uri.EscapeDataString(_selectedChat.Guid), new Uri(logoUri), TileSize.Square150x150);
            tile.VisualElements.ShowNameOnSquare150x150Logo = true;
            await tile.RequestCreateAsync();
        }

        private async Task<string> GetTileImageUriAsync(ChatItem chat, string tileId)
        {
            if (!chat.IsGroupChat && !string.IsNullOrWhiteSpace(chat.TileImageUri)) return chat.TileImageUri;
            if (chat.IsGroupChat && _client != null)
            {
                try
                {
                    var bytes = await _client.DownloadGroupIconAsync(chat.Guid);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("TileIcons", CreationCollisionOption.OpenIfExists);
                        var file = await folder.CreateFileAsync(tileId + ".jpg", CreationCollisionOption.ReplaceExisting);
                        await FileIO.WriteBytesAsync(file, bytes);
                        return "ms-appdata:///local/TileIcons/" + file.Name;
                    }
                }
                catch { }
            }
            return "ms-appx:///Assets/Square150x150Logo.png";
        }

        private async Task DeleteSelectedChatAsync()
        {
            if (_client == null || _selectedChat == null) return;
            if (!_serverCapabilities.CanUsePrivateApi) { ShowStatus("Deleting chats requires the BlueBubbles Private API helper.", true); return; }
            var title = _selectedChat.Title;
            var dialog = new MessageDialog("Delete \"" + title + "\" permanently from the BlueBubbles server? This cannot be undone.", "Delete conversation?");
            var confirm = new UICommand("Delete permanently");
            dialog.Commands.Add(confirm);
            dialog.Commands.Add(new UICommand("Cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;
            var selected = await dialog.ShowAsync();
            if (selected != confirm) return;

            try
            {
                SetSyncing(true, "Deleting conversation...");
                await _client.DeleteChatAsync(_selectedChat.Guid);
                ShowChatPage(_showArchived);
                await RefreshChatsAsync();
            }
            catch (Exception ex)
            {
                ShowStatus(FriendlyError(ex, "delete the conversation"), true);
            }
            finally
            {
                SetSyncing(false, null);
            }
        }

        private async void InputPane_Showing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            _inputPaneVisible = true;
            // Fit the entire app between its current top edge and the keyboard's top edge.
            // This is absolute, so it cannot double-apply an inset if Windows already resized us.
            args.EnsuredFocusedElementInView = true;
            RootGrid.Margin = new Thickness(0);
            var rootTop = RootGrid.TransformToVisual(null).TransformPoint(new Point(0, 0)).Y;
            var availableHeight = args.OccludedRect.Top - rootTop;
            if (availableHeight > 0)
            {
                RootGrid.VerticalAlignment = VerticalAlignment.Top;
                RootGrid.Height = availableHeight;
            }
            PrimaryHeader.Visibility = Visibility.Visible;
            if (_messages.Count > 0)
            {
                _pinMessagesToBottomUntil = DateTimeOffset.Now.AddSeconds(2);
                await ScrollToNewestMessageAsync();
                await Task.Delay(180);
                if (_inputPaneVisible) await ScrollToNewestMessageAsync();
            }
        }

        private void InputPane_Hiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            _inputPaneVisible = false;
            RootGrid.Margin = new Thickness(0);
            RootGrid.Height = double.NaN;
            RootGrid.VerticalAlignment = VerticalAlignment.Stretch;
        }
        private void MainPage_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (ImageViewerOverlay.Visibility == Visibility.Visible) { CloseImageViewer(); e.Handled = true; }
            else if (QrScannerOverlay.Visibility == Visibility.Visible) { CancelQrScan_Click(this, null); e.Handled = true; }
            else if (ComposeOverlay.Visibility == Visibility.Visible) { CloseCompose_Click(this, null); e.Handled = true; }
            else if (ContactsOverlay.Visibility == Visibility.Visible) { CloseContacts_Click(this, null); e.Handled = true; }
            else if (SettingsOverlay.Visibility == Visibility.Visible && _client != null) { SettingsOverlay.Visibility = Visibility.Collapsed; e.Handled = true; }
            else if (UseSinglePaneLayout && ChatsPane.Visibility == Visibility.Collapsed) { ReturnToChats(); e.Handled = true; }
        }

        private void ReturnToChats()
        {
            StopTypingWithoutWaiting();
            if (UseSinglePaneLayout)
            {
                ChatColumn.Width = new GridLength(1, GridUnitType.Star);
                DividerColumn.Width = new GridLength(0);
                ConversationColumn.Width = new GridLength(0);
                Grid.SetColumn(ChatsPane, 0);
                Grid.SetColumnSpan(ChatsPane, 3);
                Grid.SetColumn(ConversationPane, 0);
                Grid.SetColumnSpan(ConversationPane, 3);
                ChatsPane.Visibility = Visibility.Visible;
                ConversationPane.Visibility = Visibility.Collapsed;
                MenuButton.Visibility = Visibility.Visible;
                BackButton.Visibility = Visibility.Collapsed;
                PageTitle.Text = _showArchived ? "Archived" : "Chats";
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
            }
            UpdateHeaderActions(false);
        }

        private void ReturnToConversation()
        {
            ChatColumn.Width = new GridLength(1, GridUnitType.Star);
            DividerColumn.Width = new GridLength(0);
            ConversationColumn.Width = new GridLength(0);
            Grid.SetColumn(ChatsPane, 0);
            Grid.SetColumnSpan(ChatsPane, 3);
            Grid.SetColumn(ConversationPane, 0);
            Grid.SetColumnSpan(ConversationPane, 3);
            ChatsPane.Visibility = Visibility.Collapsed;
            ConversationPane.Visibility = Visibility.Visible;
            MenuButton.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Visible;
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility = AppViewBackButtonVisibility.Visible;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyChatSearch();
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            var opening = SearchBox.Visibility != Visibility.Visible;
            SearchBox.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
            if (opening) SearchBox.Focus(FocusState.Programmatic);
            else
            {
                SearchBox.Text = string.Empty;
                ApplyChatSearch();
            }
        }

        private void ApplyChatSearch()
        {
            var query = SearchBox == null ? string.Empty : SearchBox.Text.Trim();
            _chats.Clear();
            foreach (var chat in _allChats.Where(chat => chat.IsArchived == _showArchived && (string.IsNullOrWhiteSpace(query) || chat.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || chat.Preview.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || chat.ParticipantSummary.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).OrderByDescending(chat => chat.LastMessageTimestamp).ThenBy(chat => chat.Title)) _chats.Add(chat);
        }

        private async Task LoadContactsAsync()
        {
            try
            {
                var service = new ContactsService();
                var contacts = await service.LoadContactsAsync();
                _allContacts.Clear();
                _allContacts.AddRange(contacts);
                ApplyContactsSearch();
                _contactNames = ContactsService.BuildNames(contacts);
                _contactImages = ContactsService.BuildImages(contacts);
                _contactTileImages = ContactsService.BuildTileImages(contacts);
            }
            catch
            {
                _allContacts.Clear();
                _contacts.Clear();
                _contactNames = new Dictionary<string, string>();
                _contactImages = new Dictionary<string, ImageSource>();
                _contactTileImages = new Dictionary<string, string>();
            }
        }

        private void OpenPendingActivation()
        {
            var app = Application.Current as App;
            var chatGuid = app?.TakePendingChatGuid();
            if (!string.IsNullOrWhiteSpace(chatGuid))
            {
                OpenChatFromNotification(chatGuid);
            }
            var recipient = app?.TakePendingRecipient();
            if (!string.IsNullOrWhiteSpace(recipient)) OpenComposeForRecipient(recipient);
        }

        public async void PrepareSharedContent(ShareOperation operation)
        {
            try
            {
                var view = operation.Data;
                _shareOperation = operation;
                operation.ReportStarted();
                if (view.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await view.GetStorageItemsAsync();
                    _sharedFiles.Clear();
                    _shareTemporaryFiles.Clear();
                    var folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("SharedIncoming", CreationCollisionOption.OpenIfExists);
                    foreach (var source in items.OfType<StorageFile>())
                    {
                        var copy = await source.CopyAsync(folder, source.Name, NameCollisionOption.GenerateUniqueName);
                        _shareTemporaryFiles.Add(copy);
                        _sharedFiles.Add(copy);
                    }
                    if (_sharedFiles.Count == 0) { ShowStatus("No supported file was provided.", true); CompleteSharedContent(); return; }
                }
                if (view.Contains(StandardDataFormats.Text))
                {
                    _sharedText = await view.GetTextAsync();
                }
                if (_sharedFiles.Count == 0 && string.IsNullOrWhiteSpace(_sharedText)) { ShowStatus("No shareable item was provided.", true); CompleteSharedContent(); return; }
                operation.ReportDataRetrieved();
                OpenCompose();
            }
            catch (Exception ex)
            {
                FailSharedContent("BlueBubbles could not read the shared item: " + ex.Message);
            }
        }

        private string BuildSharedPreview()
        {
            var verb = _isForwarding ? "Forwarding " : "Sharing ";
            if (_sharedFiles.Count > 0 && !string.IsNullOrWhiteSpace(_sharedText)) return verb + DescribeSharedFiles() + " and text.";
            if (_sharedFiles.Count > 0) return verb + DescribeSharedFiles() + ".";
            return string.IsNullOrWhiteSpace(_sharedText) ? string.Empty : verb + "text.";
        }

        private void StageSharedContentInComposer()
        {
            if (_sharedFiles.Count == 0 && string.IsNullOrWhiteSpace(_sharedText)) return;
            if (!string.IsNullOrWhiteSpace(_sharedText)) MessageBox.Text = _sharedText;
            SharedAttachmentBanner.Text = BuildSharedPreview();
            SharedAttachmentBanner.Visibility = Visibility.Visible;
            AttachmentBannerHost.Visibility = Visibility.Visible;
        }

        private void ClearAttachment_Click(object sender, RoutedEventArgs e)
        {
            if (_shareOperation != null) CompleteSharedContent();
            else ClearSharedContent();
        }

        private void CompleteSharedContent()
        {
            try { _shareOperation?.ReportCompleted(); } catch { }
            ClearSharedContent();
        }

        private void FailSharedContent(string message)
        {
            try { _shareOperation?.ReportError(message); } catch { }
            ClearSharedContent();
        }

        private void ClearSharedContent()
        {
            var temporaryFiles = _shareTemporaryFiles.ToList();
            _shareTemporaryFiles.Clear();
            _shareOperation = null;
            _sharedFiles.Clear();
            _sharedText = null;
            _isForwarding = false;
            SharedAttachmentBanner.Visibility = Visibility.Collapsed;
            SharedAttachmentBanner.Text = string.Empty;
            AttachmentBannerHost.Visibility = Visibility.Collapsed;
            SharedComposePreview.Visibility = Visibility.Collapsed;
            _ = DeleteTemporarySharedFilesAsync(temporaryFiles);
        }

        private static async Task DeleteTemporarySharedFilesAsync(IEnumerable<StorageFile> files)
        {
            foreach (var file in files)
            {
                try { await file.DeleteAsync(StorageDeleteOption.PermanentDelete); }
                catch { try { await file.DeleteAsync(); } catch { } }
            }
        }

        private async Task PrepareVideoAsync(BlueBubblesClient client, MessageItem message)
        {
            try
            {
                var folder = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("MessageMedia", CreationCollisionOption.OpenIfExists);
                var extension = Path.GetExtension(message.AttachmentLabel);
                if (string.IsNullOrWhiteSpace(extension)) extension = message.AttachmentMimeType.IndexOf("quicktime", StringComparison.OrdinalIgnoreCase) >= 0 ? ".mov" : ".mp4";
                var safeGuid = Regex.Replace(message.AttachmentGuid ?? message.Guid ?? Guid.NewGuid().ToString("N"), "[^A-Za-z0-9_-]", "_");
                var file = await folder.CreateFileAsync(safeGuid + extension, CreationCollisionOption.OpenIfExists);
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size == 0) await FileIO.WriteBytesAsync(file, await client.DownloadAttachmentAsync(message.AttachmentGuid));
                message.SetAttachmentUri("ms-appdata:///temp/MessageMedia/" + file.Name);
            }
            catch
            {
                message.MarkAttachmentFailed();
            }
        }

        private string DescribeSharedFiles()
        {
            if (_sharedFiles.Count == 1) return _sharedFiles[0].Name;
            return _sharedFiles.Count + " files";
        }

        private void AttachmentMedia_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            var media = sender as FrameworkElement;
            var message = media?.DataContext as MessageItem;
            if (message == null || !message.IsVideoAttachment) return;
            message.MarkAttachmentFailed();
            if (DeveloperModeToggle.IsOn) ShowStatus("Developer details: MediaElement could not decode or open this video.", true);
        }

        private void ChatAvatar_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            var image = sender as Image;
            var chat = image == null ? null : image.DataContext as ChatItem;
            if (chat != null && chat.IsGroupChat) chat.AvatarSource = null;
        }

        private async void Media_Opened(object sender, RoutedEventArgs e)
        {
            if (DateTimeOffset.Now <= _pinMessagesToBottomUntil) await ScrollToNewestMessageAsync();
        }

        private void Image_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var image = sender as Image;
            var message = image == null ? null : image.DataContext as MessageItem;
            if (message == null || string.IsNullOrWhiteSpace(message.AttachmentUri)) return;
            ImageViewerImage.Source = new BitmapImage(new Uri(message.AttachmentUri));
            ImageViewerOverlay.Visibility = Visibility.Visible;
            e.Handled = true;
        }

        private void CloseImageViewer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseImageViewer();
            e.Handled = true;
        }

        private void CloseImageViewer()
        {
            ImageViewerOverlay.Visibility = Visibility.Collapsed;
            ImageViewerImage.Source = null;
        }

        private void ShowStatus(string message, bool isError)
        {
            _statusTimer.Stop();
            _statusIsError = isError && !string.IsNullOrWhiteSpace(message);
            StatusText.Text = message;
            StatusText.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(isError ? Windows.UI.Color.FromArgb(255, 255, 140, 130) : Windows.UI.Colors.White);
            StatusBar.Visibility = isError && !string.IsNullOrWhiteSpace(message) ? Visibility.Visible : Visibility.Collapsed;
            if (_statusIsError) _statusTimer.Start();
        }

        private string FriendlyError(Exception exception, string action)
        {
            var root = exception == null ? null : exception.GetBaseException();
            var detail = root == null ? string.Empty : root.Message ?? string.Empty;
            string friendly;
            if (!HasNetworkConnection()) friendly = "This device is offline. Connect to Wi-Fi or cellular data, then try again.";
            else if (root is HttpRequestException || root is TaskCanceledException || detail.IndexOf("net_http", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0)
                friendly = "The BlueBubbles server is offline or unreachable. Check that the Mac and server are running on the same network.";
            else if (detail.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("404", StringComparison.OrdinalIgnoreCase) >= 0)
                friendly = "The conversation was not found on the BlueBubbles server. Refresh Chats and try again.";
            else friendly = "BlueBubbles could not " + action + ".";
            if (!DeveloperModeToggle.IsOn || detail.Length == 0) return friendly;
            if (detail.Length > 900) detail = detail.Substring(0, 900).TrimEnd() + "...";
            return friendly + "\r\n\r\nDeveloper details: " + root.GetType().Name + ": " + detail;
        }

        private static bool HasNetworkConnection()
        {
            try
            {
                return NetworkInformation.GetConnectionProfiles().Any(profile => profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None);
            }
            catch { return true; }
        }

        private void StatusTimer_Tick(object sender, object e)
        {
            _statusTimer.Stop();
            ShowStatus(string.Empty, false);
        }

        private void MessageText_Loaded(object sender, RoutedEventArgs e)
        {
            var block = sender as RichTextBlock;
            RenderMessageText(block, block?.DataContext as MessageItem);
        }

        private void MessageText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            RenderMessageText(sender as RichTextBlock, args.NewValue as MessageItem);
        }

        private static void RenderMessageText(RichTextBlock block, MessageItem message)
        {
            if (block == null) return;
            block.Blocks.Clear();
            if (message == null) return;
            var paragraph = new Paragraph();
            var index = 0;
            foreach (Match match in Regex.Matches(message.Text ?? string.Empty, @"https?://[^\s]+", RegexOptions.IgnoreCase))
            {
                if (match.Index > index) paragraph.Inlines.Add(new Run { Text = message.Text.Substring(index, match.Index - index) });
                Uri uri;
                if (Uri.TryCreate(match.Value, UriKind.Absolute, out uri))
                {
                    var link = new Hyperlink { NavigateUri = uri };
                    link.Inlines.Add(new Run { Text = match.Value });
                    paragraph.Inlines.Add(link);
                }
                else paragraph.Inlines.Add(new Run { Text = match.Value });
                index = match.Index + match.Length;
            }
            if (index < (message.Text ?? string.Empty).Length) paragraph.Inlines.Add(new Run { Text = message.Text.Substring(index) });
            block.Blocks.Add(paragraph);
        }
    }
}
