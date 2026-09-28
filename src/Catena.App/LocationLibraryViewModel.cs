using System.Collections.ObjectModel;
using Catena.Contracts;
using Catena.Storage.Local;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class CatalogRootViewModel(CatalogRoot root) : ObservableObject
{
    public string Path { get; } = root.Path;
    public string Name { get; } = root.Name;
    [ObservableProperty] private StructureProfile? profile = root.Profile;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isScanning;
    public string Summary => Status.Length > 0 ? Status : Profile is { } p ? $"{p.Folders:N0} 文件夹 · {p.Files:N0} 文件{(p.Partial ? " · 部分" : "")}" : "待建立缓存";
    public string Details => Path + "\n" + Summary + (Profile is { } p ?
        $"\n缓存 {p.Updated.LocalDateTime:yyyy-MM-dd HH:mm} · {p.Bytes / 1048576d:N1} MB · {p.MaxDepth} 层\n最近修改 {p.LatestModified?.LocalDateTime:yyyy-MM-dd HH:mm}\n" +
        string.Join(" · ", p.Types.Select(x => $"{x.Key} {x.Value:N0}")) + "\n" +
        string.Join("\n", p.Branches.Select(x => $"{x.Key}：{x.Value:N0} 文件")) +
        (p.Skipped > 0 ? $"\n跳过 {p.Skipped:N0} 处" : "") : "");
    partial void OnProfileChanged(StructureProfile? value) => NotifySummary();
    partial void OnStatusChanged(string value) => NotifySummary();
    private void NotifySummary() { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Details)); }
}

public sealed partial class LocationLibraryViewModel(ILocationCatalogStore store, LocalStructureScanner scanner) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly HashSet<Task> writes = [];
    private readonly Dictionary<string, CancellationTokenSource> scanning = new(StringComparer.OrdinalIgnoreCase);
    private bool available;
    private bool initialized;
    public ObservableCollection<CatalogRootViewModel> Roots { get; } = [];
    public ObservableCollection<RecentLocation> Recent { get; } = [];
    public event Action? RootsChanged;
    public event Action? RecentChanged;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private CatalogRootViewModel? selectedRoot;
    public async Task InitializeAsync(IReadOnlyList<string>? defaults = null, bool startScans = true)
    {
        if (initialized) return; initialized = true;
        try
        {
            defaults ??= await Task.Run(() => new[] { Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDriveCommercial"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive") }
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p!)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            await store.InitializeAsync(defaults, lifetime.Token); available = true;
            foreach (var root in await store.GetRootsAsync(lifetime.Token)) Roots.Add(new(root));
            await ReloadRecentAsync(); RootsChanged?.Invoke();
            if (startScans) foreach (var root in Roots.Where(r => r.Profile is null || DateTimeOffset.UtcNow - r.Profile.Updated > TimeSpan.FromHours(24)).ToArray()) _ = RefreshAsync(root);
        }
        catch (Exception ex) when (Recoverable(ex)) { Status = "位置记录不可用，原数据已保留。"; }
    }
    public async Task AddAsync(string path)
    {
        if (!available) { Status = "位置记录不可用。"; return; }
        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            if (!await Task.Run(() => Directory.Exists(path))) { Status = "文件夹不可用。"; return; }
            var name = Path.GetFileName(path); if (name.Length == 0) name = path;
            await store.AddRootAsync(path, name, lifetime.Token); var root = new CatalogRootViewModel(new(path, name, null));
            Roots.Add(root); RootsChanged?.Invoke(); Status = ""; _ = RefreshAsync(root);
        }
        catch (Exception ex) when (Recoverable(ex)) { Status = "无法加入常用位置。"; }
    }
    [RelayCommand] private async Task RemoveAsync(CatalogRootViewModel? root)
    {
        if (root is null || !available) return;
        try
        {
            if (scanning.TryGetValue(root.Path, out var cancellation)) cancellation.Cancel();
            await store.RemoveRootAsync(root.Path, lifetime.Token); Roots.Remove(root); SelectedRoot = Roots.FirstOrDefault(); RootsChanged?.Invoke();
        }
        catch (Exception ex) when (Recoverable(ex)) { Status = "无法移除此位置。"; }
    }
    [RelayCommand(AllowConcurrentExecutions = true)] private async Task RefreshAsync(CatalogRootViewModel? root)
    {
        if (root is null || !available || scanning.ContainsKey(root.Path)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); scanning[root.Path] = cancellation;
        root.IsScanning = true; root.Status = "等待更新…";
        try
        {
            await scanGate.WaitAsync(cancellation.Token);
            try
            {
                root.Status = "建立缓存…";
                var progress = new Progress<int>(count => { if (root.IsScanning && !cancellation.IsCancellationRequested) root.Status = $"已扫描 {count:N0} 项"; });
                var snapshot = await scanner.ScanAsync(root.Path, progress: progress, token: cancellation.Token);
                await store.SaveSnapshotAsync(snapshot, cancellation.Token);
                root.Profile = snapshot.Profile; root.Status = "";
            }
            finally { scanGate.Release(); }
        }
        catch (OperationCanceledException) { root.Status = "更新已取消"; }
        catch (Exception ex) when (Recoverable(ex)) { root.Status = root.Profile is null ? "暂时无法读取" : "无法更新 · 保留缓存"; }
        finally { root.IsScanning = false; scanning.Remove(root.Path); }
    }
    [RelayCommand] private void Cancel(CatalogRootViewModel? root)
    { if (root is not null && scanning.TryGetValue(root.Path, out var cancellation)) cancellation.Cancel(); }
    public Task RecordAsync(string path, bool isFile)
    {
        var task = RecordCoreAsync(path, isFile);
        lock (writes) writes.Add(task);
        _ = task.ContinueWith(done => { lock (writes) writes.Remove(done); }, TaskScheduler.Default);
        return task;
    }
    public Task FlushAsync() { lock (writes) return Task.WhenAll(writes.ToArray()); }
    private async Task RecordCoreAsync(string path, bool isFile)
    {
        if (!available || lifetime.IsCancellationRequested) return;
        try { await store.RecordVisitAsync(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), isFile, lifetime.Token); await ReloadRecentAsync(); }
        catch (Exception ex) when (Recoverable(ex)) { Status = "近期记录保存失败。"; }
    }
    private async Task ReloadRecentAsync()
    { var rows = await store.GetRecentAsync(lifetime.Token); Recent.Clear(); foreach (var row in rows) Recent.Add(row); RecentChanged?.Invoke(); }
    [RelayCommand] private async Task ClearRecentAsync()
    {
        if (!available) return;
        try { await store.ClearRecentAsync(lifetime.Token); await ReloadRecentAsync(); }
        catch (Exception ex) when (Recoverable(ex)) { Status = "无法清除近期记录。"; }
    }
    public async Task<FolderListing?> ReadCacheAsync(string path, CancellationToken token)
    {
        if (!available) return null;
        try { return await store.GetFoldersAsync(path, token); }
        catch (Exception ex) when (Recoverable(ex) && ex is not OperationCanceledException) { return null; }
    }
    public async Task<FolderListing> ReadFoldersAsync(string path, CancellationToken token)
    {
        var listing = await scanner.ReadFoldersAsync(path, token);
        if (available)
        {
            try { await store.SaveFoldersAsync(path, listing, token); }
            catch (Exception ex) when (Recoverable(ex) && ex is not OperationCanceledException) { Status = "目录缓存保存失败。"; }
        }
        return listing;
    }
    private static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
        System.Data.Common.DbException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or OperationCanceledException;
    public void Dispose() { lifetime.Cancel(); }
}
