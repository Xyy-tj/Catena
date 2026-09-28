using Catena.Contracts;

namespace Catena.Core;

// Owned by one UI context. Providers do I/O off that context; stale completions never publish.
public sealed class PaneNavigator : IDisposable
{
    private readonly ILocationProvider provider;
    private CancellationTokenSource? pending;
    private long requestId;
    private readonly List<Location> history;
    private int historyIndex;
    public Guid Id { get; }
    public Location Location { get; private set; }
    public Location? PinnedRoot { get; set; }
    public List<FileEntry> Entries { get; } = [];
    public bool IsLoading { get; private set; }
    public string? Error { get; private set; }
    public bool CanBack => historyIndex > 0;
    public bool CanForward => historyIndex < history.Count - 1;
    public IReadOnlyList<Location> History => history;
    public event Action? Changed;
    public event Action<Location>? Visited;

    public PaneNavigator(ILocationProvider provider, PaneState state)
    {
        this.provider = provider;
        Id = state.Id; Location = state.Location; PinnedRoot = state.PinnedRoot;
        history = state.History.ToList();
        historyIndex = Math.Clamp(state.HistoryIndex, -1, history.Count - 1);
        if (historyIndex < 0) { history.Clear(); history.Add(Location); historyIndex = 0; }
    }

    public Task RefreshAsync() => NavigateAsync(Location, false, false);
    public Task BackAsync() => CanBack ? NavigateHistoryAsync(historyIndex - 1) : Task.CompletedTask;
    public Task ForwardAsync() => CanForward ? NavigateHistoryAsync(historyIndex + 1) : Task.CompletedTask;
    public Task UpAsync() => provider.GetParent(Location) is { } parent ? NavigateAsync(parent) : Task.CompletedTask;
    public Task HomeAsync() => PinnedRoot is { } root ? NavigateAsync(root) : Task.CompletedTask;
    private Task NavigateHistoryAsync(int index) { historyIndex = index; return NavigateAsync(history[index], false); }

    public async Task NavigateAsync(Location target, bool recordHistory = true, bool recordVisit = true)
    {
        pending?.Cancel();
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        var request = ++requestId;
        if (recordHistory && !provider.AreEqual(Location, target))
        {
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(target);
            if (history.Count > 100) history.RemoveAt(0);
            historyIndex = history.Count - 1;
        }
        Location = target; Entries.Clear(); Error = null; IsLoading = true; Changed?.Invoke();
        try
        {
            await foreach (var batch in provider.EnumerateAsync(target, cancellation.Token))
            {
                if (request != requestId) return;
                Entries.AddRange(batch);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (FileSystemException ex) { if (request == requestId) Error = ex.Message; }
        finally
        {
            if (request == requestId)
            {
                IsLoading = false; pending = null; Changed?.Invoke();
                if (recordVisit && Error is null && !cancellation.IsCancellationRequested) Visited?.Invoke(Location);
            }
        }
    }

    public PaneState Snapshot() => new()
    {
        Id = Id, Location = Location, PinnedRoot = PinnedRoot,
        History = history.ToList(), HistoryIndex = historyIndex
    };

    public void Dispose() { ++requestId; pending?.Cancel(); pending = null; }
}
