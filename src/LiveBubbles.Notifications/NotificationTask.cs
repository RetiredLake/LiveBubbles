using System;
using System.Linq;
using System.Threading;
using Windows.ApplicationModel.Background;
using Windows.Networking.Sockets;

namespace LiveBubbles.Notifications
{
    public sealed class NotificationTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                BackgroundTaskCanceledEventHandler canceled = (sender, reason) => deadline.Cancel();
                taskInstance.Canceled += canceled;
                try
                {
                    string generation = NotificationBridge.Generation;
                    if (!NotificationBridge.IsCurrent(generation)) return;
                    var socketTask = BackgroundTaskRegistration.AllTasks.Values.FirstOrDefault(t => t.Name == NotificationBridge.TaskName);
                    if (socketTask == null) return;
                    var details = taskInstance.TriggerDetails as SocketActivityTriggerDetails;
                    if (details != null) await NotificationRuntime.HandleSocketAsync(details, socketTask.TaskId, generation, deadline.Token);
                    else
                    {
                        await NotificationRuntime.EnsureConnectedAsync(socketTask.TaskId, generation, deadline.Token);
                        await NotificationRuntime.ReconcileAsync(generation, deadline.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch { if (NotificationBridge.Enabled) NotificationBridge.SetStatus("Notification connection interrupted. Retrying automatically."); }
                finally { taskInstance.Canceled -= canceled; deferral.Complete(); }
            }
        }
    }
}
