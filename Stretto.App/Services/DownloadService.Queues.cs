using Stretto.Core;

namespace Stretto.App.Services;

/// <summary>Releasing and pausing queues, and the timetable that turns them on and off.</summary>
public sealed partial class DownloadService
{
    // ═══ Queues ═══

    /// <summary>Releases a queue's work. Items sitting as Pending become eligible.</summary>
    public void StartQueue(DownloadQueue queue)
    {
        queue.IsRunning = true;

        foreach (var item in ItemsIn(queue).Where(i => i.Status == DownloadStatus.Pending
                                                    || i.Status == DownloadStatus.Paused))
        {
            item.Error = null;
            item.Status = DownloadStatus.Queued;
        }

        Pump();
    }

    /// <summary>Stops a queue and pauses whatever it had in flight, keeping resume points.</summary>
    public void PauseQueue(DownloadQueue queue)
    {
        queue.IsRunning = false;

        foreach (var item in ItemsIn(queue).Where(i => i.CanPause))
            Pause(item);
    }

    public IEnumerable<DownloadItem> ItemsIn(DownloadQueue queue) =>
        queue.ItemIds.Select(id => Items.FirstOrDefault(i => i.Id == id)).OfType<DownloadItem>();

    /// <summary>Called on a timer: turns queues on and off according to their timetables.</summary>
    public void TickSchedules()
    {
        if (!Queues.ApplySchedules(DateTime.Now))
            return;

        foreach (var queue in Queues.Queues.Where(q => q.SchedulerEnabled))
        {
            if (queue.IsRunning)
                StartQueue(queue);
            else
                PauseQueue(queue);
        }
    }
}
