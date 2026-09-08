using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using WpBlueBubbles.Services;
using System.Reflection;

namespace WpBlueBubbles
{
    sealed partial class App : Application
    {
        internal string PendingChatGuid { get; private set; }
        internal string PendingRecipient { get; private set; }
        public App()
        {
            UnhandledException += App_UnhandledException;
            try
            {
                InitializeComponent();
            }
            catch (System.Exception ex)
            {
                WriteStartupError(ex);
                throw;
            }
            Suspending += OnSuspending;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            ReadLaunchArguments(e.Arguments);
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null)
            {
                try
                {
                    frame.Navigate(typeof(MainPage));
                }
                catch (System.Exception ex)
                {
                    WriteStartupError(ex);
                    frame.Content = new TextBlock { Text = "LiveBubbles could not start:\r\n\r\n" + ex, TextWrapping = TextWrapping.Wrap };
                }
            }
            var page = frame.Content as MainPage;
            if (page != null && page.IsClientReady && !string.IsNullOrWhiteSpace(PendingChatGuid)) page.OpenChatFromNotification(TakePendingChatGuid());
            Window.Current.Activate();
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            ReadLaunchArguments(ReadToastArguments(args));
            PendingRecipient = ReadContactRecipient(args);
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null) frame.Navigate(typeof(MainPage));
            var page = frame.Content as MainPage;
            if (page != null && page.IsClientReady && !string.IsNullOrWhiteSpace(PendingChatGuid))
            {
                var chatGuid = TakePendingChatGuid();
                page.OpenChatFromNotification(chatGuid);
            }
            if (page != null && page.IsClientReady && !string.IsNullOrWhiteSpace(PendingRecipient))
            {
                var recipient = TakePendingRecipient();
                page.OpenComposeForRecipient(recipient);
            }
            Window.Current.Activate();
        }

        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            args.TaskInstance.GetDeferral().Complete();
        }

        protected override void OnShareTargetActivated(ShareTargetActivatedEventArgs args)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null) frame.Navigate(typeof(MainPage));
            var page = frame.Content as MainPage;
            page?.PrepareSharedContent(args.ShareOperation);
            Window.Current.Activate();
        }

        private void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteStartupError(e.Exception);
        }

        private static void WriteStartupError(System.Exception exception)
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup-error.txt"),
                    exception.ToString());
            }
            catch { }
        }

        private void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e) { LiveBubbles.Notifications.NotificationBridge.SetActiveChat(null); }

        internal string TakePendingChatGuid()
        {
            var value = PendingChatGuid;
            PendingChatGuid = null;
            return value;
        }

        internal string TakePendingRecipient()
        {
            var value = PendingRecipient;
            PendingRecipient = null;
            return value;
        }

        private void ReadLaunchArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return;
            const string prefix = "chat=";
            if (arguments.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                PendingChatGuid = System.Uri.UnescapeDataString(arguments.Substring(prefix.Length));
        }

        private static string ReadContactRecipient(IActivatedEventArgs args)
        {
            // This uses reflection because the contact activation contract is not present in every installed W10M SDK.
            try
            {
                var type = args.GetType();
                if (type.FullName != "Windows.ApplicationModel.Activation.ContactMessageActivatedEventArgs") return string.Empty;
                var serviceId = type.GetProperty("ServiceId")?.GetValue(args) as string;
                var serviceUserId = type.GetProperty("ServiceUserId")?.GetValue(args) as string;
                if (string.Equals(serviceId, "telephone", System.StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(serviceUserId)) return serviceUserId;
                var contact = type.GetProperty("Contact")?.GetValue(args);
                var phones = contact?.GetType().GetProperty("Phones")?.GetValue(contact) as System.Collections.IEnumerable;
                if (phones != null)
                {
                    foreach (var phone in phones)
                    {
                        var number = phone?.GetType().GetProperty("Number")?.GetValue(phone) as string;
                        if (!string.IsNullOrWhiteSpace(number)) return number;
                    }
                }
                var emails = contact?.GetType().GetProperty("Emails")?.GetValue(contact) as System.Collections.IEnumerable;
                if (emails != null)
                    foreach (var email in emails)
                    {
                        var address = email?.GetType().GetProperty("Address")?.GetValue(email) as string;
                        if (!string.IsNullOrWhiteSpace(address)) return address;
                    }
                if (!string.IsNullOrWhiteSpace(serviceUserId)) return serviceUserId;
            }
            catch { }
            return string.Empty;
        }

        private static string ReadToastArguments(IActivatedEventArgs args)
        {
            try
            {
                var type = args.GetType();
                if (type.FullName != "Windows.ApplicationModel.Activation.ToastNotificationActivatedEventArgs") return string.Empty;
                return type.GetProperty("Argument")?.GetValue(args) as string ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
