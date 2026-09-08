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
                SocketActivityTriggerDetails details = null;
                try
                {
                    string generation = NotificationBridge.Generation;
                    if (!NotificationBridge.IsCurrent(generation)) return;
                    details = taskInstance.TriggerDetails as SocketActivityTriggerDetails;
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
                        await NotificationRuntime.EnsureConnectedAsync(socketTask.TaskId, generation, deadline.Token, true);
                        await NotificationRuntime.ReconcileAsync(generation, deadline.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (NotificationFailureException ex) { NotificationBridge.RecordError(ex.Stage, ex.InnerException ?? ex); if (NotificationBridge.Enabled) NotificationBridge.SetStatus("Notification connection interrupted. Retrying automatically."); }
                catch (Exception ex)
                {
                    string stage = "background-recovery";
                    try { if (details != null) stage = "socket-" + details.Reason.ToString(); } catch { }
                    NotificationBridge.RecordError(stage, ex);
                    if (NotificationBridge.Enabled) NotificationBridge.SetStatus("Notification connection interrupted. Retrying automatically.");
                }
                finally
                {
                    try { taskInstance.Canceled -= canceled; } catch (Exception ex) { NotificationBridge.RecordError("background-cancel-handler", ex); }
                    try { deferral.Complete(); } catch (Exception ex) { NotificationBridge.RecordError("deferral-complete", ex); }
                }
            }
        }
    }
}
