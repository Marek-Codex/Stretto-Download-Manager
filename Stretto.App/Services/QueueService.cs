using Stretto.App.Models;
using Stretto.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stretto.App.Services;

/// <summary>
/// A named, ordered set of downloads that runs as a unit, optionally on a timetable.
///
/// This is the part of XDM the concurrency cap alone does not cover: "these forty files,
/// in this order, but only overnight".
/// </summary>
public sealed class DownloadQueue : INotifyPropertyChanged
{
    private string _name = "New queue";
    private bool _isRunning;

    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Notify(); }
    }

    /// <summary>Download ids, in the order they should run.</summary>
    public List<string> ItemIds { get; set; } = [];

    public bool SchedulerEnabled { get; set; }

    public TimeSpan StartAt { get; set; } = new(4, 0, 0);
    public TimeSpan StopAt { get; set; } = new(23, 59, 0);

    public List<DayOfWeek> Days { get; set; } =
        [.. Enum.GetValues<DayOfWeek>()];

    /// <summary>Runtime only: whether the queue is currently releasing work.</summary>
    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set { if (_isRunning == value) return; _isRunning = value; Notify(); Notify(nameof(StateText)); }
    }

    [JsonIgnore]
    public string StateText => IsRunning ? "Running" : SchedulerEnabled ? "Scheduled" : "Stopped";

    /// <summary>
    /// Whether the timetable says this queue should be running right now.
    /// Handles windows that cross midnight, which is the common case for an overnight queue.
    /// </summary>
    public bool ScheduleSaysRun(DateTime now)
    {
        if (!SchedulerEnabled || !Days.Contains(now.DayOfWeek))
            return false;

        var time = now.TimeOfDay;

        return StopAt > StartAt
            ? time >= StartAt && time < StopAt
            : time >= StartAt || time < StopAt;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>Owns the queues and decides what the downloader is allowed to start.</summary>
public sealed class QueueService
{
    private static string Path => System.IO.Path.Combine(DownloadStore.Directory, "queues.json");

    public ObservableCollection<DownloadQueue> Queues { get; } = [];

    public const string DefaultQueueName = "Default queue";

    public QueueService()
    {
        Load();
        EnsureDefault();
    }

    /// <summary>
    /// The queue "Download Later" drops things into. Always present, because "put this in
    /// the queue" should not first require inventing a queue -- and a first run with an
    /// empty Queue and scheduler window has nothing to explain itself with.
    /// </summary>
    public DownloadQueue Default => Queues[0];

    private void EnsureDefault()
    {
        if (Queues.Count > 0)
            return;

        Queues.Add(new DownloadQueue { Name = DefaultQueueName });
        Save();
    }

    /// <summary>Puts a download at the back of a queue.</summary>
    public void Enqueue(DownloadQueue queue, DownloadItem item)
    {
        if (queue.ItemIds.Contains(item.Id))
            return;

        queue.ItemIds.Add(item.Id);
        Save();
    }

    /// <summary>The queue an item belongs to, or null when it is unqueued.</summary>
    public DownloadQueue? QueueFor(DownloadItem item) =>
        Queues.FirstOrDefault(q => q.ItemIds.Contains(item.Id));

    /// <summary>
    /// An unqueued download runs whenever there is a slot. A queued one only runs while its
    /// queue is running, which is what makes a queue a queue rather than a label.
    ///
    /// <paramref name="running"/> also holds a queue to one transfer at a time. Letting a
    /// queue fill every slot would defeat the reason to queue in the first place: the point
    /// of "put it in the queue" is that the big files go one after another.
    /// </summary>
    public bool CanRun(DownloadItem item, IEnumerable<DownloadItem>? running = null)
    {
        if (QueueFor(item) is not { } queue)
            return true;

        if (!queue.IsRunning)
            return false;

        return running is null || !running.Any(r => queue.ItemIds.Contains(r.Id));
    }

    /// <summary>
    /// Run order: queued work first, in the order the user arranged it, then everything
    /// else oldest-first. Without this a queue would run in whatever order the list happens
    /// to be in, which defeats the point of being able to reorder it.
    /// </summary>
    public IEnumerable<DownloadItem> Prioritise(IEnumerable<DownloadItem> candidates)
    {
        var items = candidates.ToList();
        var ordered = new List<DownloadItem>();
        var orderedIds = new HashSet<string>();

        foreach (var queue in Queues.Where(q => q.IsRunning))
        {
            foreach (var id in queue.ItemIds)
            {
                var match = items.FirstOrDefault(i => i.Id == id);
                if (match is not null && orderedIds.Add(match.Id))
                {
                    ordered.Add(match);
                    items.Remove(match);
                }
            }
        }

        ordered.AddRange(items.OrderBy(i => i.Added));
        return ordered;
    }

    /// <summary>Applies each queue's timetable. Returns true when anything changed.</summary>
    public bool ApplySchedules(DateTime now)
    {
        var changed = false;

        foreach (var queue in Queues.Where(q => q.SchedulerEnabled))
        {
            var shouldRun = queue.ScheduleSaysRun(now);
            if (queue.IsRunning == shouldRun)
                continue;

            queue.IsRunning = shouldRun;
            changed = true;
        }

        return changed;
    }

    public DownloadQueue Create(string name, IEnumerable<DownloadItem> items)
    {
        var queue = new DownloadQueue
        {
            Name = string.IsNullOrWhiteSpace(name) ? "New queue" : name.Trim(),
            ItemIds = [.. items.Select(i => i.Id)],
        };

        Queues.Add(queue);
        Save();
        return queue;
    }

    /// <summary>
    /// Moves downloads into a queue, or out of every queue when target is null. A download
    /// belongs to at most one queue, so this removes before it adds.
    /// </summary>
    public void MoveTo(DownloadQueue? target, IEnumerable<DownloadItem> items)
    {
        var moving = items.ToList();

        foreach (var queue in Queues)
            queue.ItemIds.RemoveAll(id => moving.Any(i => i.Id == id));

        if (target is not null)
            foreach (var item in moving.Where(i => !target.ItemIds.Contains(i.Id)))
                target.ItemIds.Add(item.Id);

        Save();
    }

    public void Remove(DownloadQueue queue)
    {
        Queues.Remove(queue);

        // Deleting the last queue would leave Default with nothing to return, and leave
        // "Download Later" with nowhere to put anything.
        EnsureDefault();
        Save();
    }

    /// <summary>Keeps queues from referencing downloads that no longer exist.</summary>
    public void Prune(IEnumerable<DownloadItem> items)
    {
        var live = items.Select(i => i.Id).ToHashSet();

        foreach (var queue in Queues)
            queue.ItemIds.RemoveAll(id => !live.Contains(id));
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(Path))
                return;

            var loaded = JsonSerializer.Deserialize<List<DownloadQueue>>(File.ReadAllText(Path));
            if (loaded is null)
                return;

            Queues.Clear();
            foreach (var queue in loaded)
            {
                // Running is a runtime state; a queue must not resume itself just because
                // the app was closed while it happened to be running.
                queue.IsRunning = false;
                Queues.Add(queue);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            AppLog.Write($"Could not read queues: {exception.Message}");
        }

        EnsureDefault();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DownloadStore.Directory);
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(Queues,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            AppLog.Write($"Could not save queues: {exception.Message}");
        }
    }
}
