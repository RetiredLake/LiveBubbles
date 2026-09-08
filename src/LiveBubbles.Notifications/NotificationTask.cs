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
                    var details = taskInstance.TriggerDetails as SocketActivityTriggerDetails;
                    if (details != null)
                    {
                        // SocketActivityTriggerDetails already belongs to this
                        // registration. Looking it up again can return no task in
                        // the background process, silently dropping every event.
                        await NotificationRuntime.HandleSocketAsync(details, taskInstance.Task.TaskId, generation, deadline.Token);
                    }
                    else
                    {
                        var socketTask = BackgroundTaskRegistration.AllTasks.Values.FirstOrDefault(t => t.Name == NotificationBridge.TaskName);
                        if (socketTask == null) return;
                        await NotificationRuntime.EnsureConnectedAsync(socketTask.TaskId, generation, deadline.Token);
                        await NotificationRuntime.ReconcileAsync(generation, deadline.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { NotificationBridge.RecordError("background", ex); if (NotificationBridge.Enabled) NotificationBridge.SetStatus("Notification connection interrupted. Retrying automatically."); }
                finally { taskInstance.Canceled -= canceled; deferral.Complete(); }
            }
        }
    }
}
