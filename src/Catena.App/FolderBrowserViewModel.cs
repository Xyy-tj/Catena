using System.Collections.ObjectModel;
using System.ComponentModel;
using Catena.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class FolderNodeViewModel : ObservableObject, IDisposable
{
    private readonly LocationLibraryViewModel library;
    private readonly CancellationToken token;
    private bool loaded;
    private bool loading;
    private bool disposed;
    public string Name { get; }
    public string? Path { get; }
    public bool IsFile { get; }
    public CatalogRootViewModel? Root { get; }
    public bool IsLocation => Path is not null;
    public FileEntry? Icon => Path is null ? null : new(new("local", new Uri(Path).AbsoluteUri), Name, IsFile ? EntryKind.File : EntryKind.Directory, null, null, EntryCapabilities.Browse);
    public string Detail => (Root?.Details ?? Path ?? Name) + (Status.Length > 0 ? "\n" + Status : "");
    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private string status = "";
    public bool HasStatus => Status.Length > 0;
    partial void OnStatusChanged(string value) { OnPropertyChanged(nameof(HasStatus)); OnPropertyChanged(nameof(Detail)); }
    partial void OnIsExpandedChanged(bool value) { if (value && !loaded && Path is not null) _ = LoadAsync(); }
    public FolderNodeViewModel(string name, string? path, LocationLibraryViewModel library, CancellationToken token, bool canExpand = true, bool isFile = false, CatalogRootViewModel? root = null)
    {
        Name = name; Path = path; this.library = library; this.token = token; IsFile = isFile; Root = root;
        if (path is not null && canExpand && !isFile) Children.Add(new("…", null, library, token));
        else loaded = true;
        if (Root is not null) Root.PropertyChanged += RootChanged;
    }
    private void RootChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Detail));
    public async Task LoadAsync(bool force = false)
    {
        if (Path is null || IsFile || loading || disposed) return;
        loading = true; Status = "读取中…";
        try
        {
            var cached = await library.ReadCacheAsync(Path, token);
            if (cached is not null) Apply(cached, true);
            if (force || cached is null || cached.Truncated || DateTimeOffset.UtcNow - cached.Updated > TimeSpan.FromMinutes(5))
            {
                try { Apply(await library.ReadFoldersAsync(Path, token), false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { Status = cached is null ? "无法读取 · 可刷新重试" : $"缓存 {cached.Updated.LocalDateTime:MM-dd HH:mm} · 离线"; }
            }
            loaded = true;
        }
        catch (OperationCanceledException) { }
        finally { loading = false; }
    }
    private void Apply(FolderListing listing, bool cached)
    {
        if (disposed || token.IsCancellationRequested) return;
        foreach (var child in Children) child.Dispose(); Children.Clear();
        foreach (var item in listing.Items) Children.Add(new(item.Name, item.Path, library, token, item.CanExpand));
        if (listing.Truncated) Children.Add(new("部分目录 · 刷新可更新", null, library, token));
        Status = listing.Truncated ? "部分目录 · 可刷新" : cached ? $"缓存 {listing.Updated.LocalDateTime:MM-dd HH:mm}" : "";
    }
    public void Dispose() { disposed = true; if (Root is not null) Root.PropertyChanged -= RootChanged; foreach (var child in Children) child.Dispose(); }
}

public sealed partial class FolderBrowserViewModel : ObservableObject, IDisposable
{
    private readonly LocationLibraryViewModel library;
    private readonly CancellationTokenSource lifetime = new();
    private readonly FolderNodeViewModel favorites;
    private readonly FolderNodeViewModel recent;
    private readonly FolderNodeViewModel current;
    private readonly Func<string, bool, Task> navigate;
    public ObservableCollection<FolderNodeViewModel> Nodes { get; } = [];
    [ObservableProperty] private FolderNodeViewModel? selectedNode;
    partial void OnSelectedNodeChanged(FolderNodeViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedRoot));
        if (value?.Path is { } path) _ = navigate(path, value.IsFile);
    }
    public CatalogRootViewModel? SelectedRoot => SelectedNode?.Root;
    public FolderBrowserViewModel(LocationLibraryViewModel library, Func<string, bool, Task> navigate, IEnumerable<string>? drives = null)
    {
        this.library = library; this.navigate = navigate;
        favorites = Node("常用"); favorites.IsExpanded = true; Nodes.Add(favorites);
        recent = Node("最近"); Nodes.Add(recent);
        current = Node("当前"); Nodes.Add(current);
        var computer = Node("此电脑"); Nodes.Add(computer);
        foreach (var path in drives ?? Directory.GetLogicalDrives()) computer.Children.Add(Node(path, path));
        ReloadRoots(); ReloadRecent(); library.RootsChanged += ReloadRoots; library.RecentChanged += ReloadRecent;
    }
    private FolderNodeViewModel Node(string name, string? path = null) => new(name, path, library, lifetime.Token);
    public void SetCurrent(string path)
    {
        if (current.Children.FirstOrDefault()?.Path == path) return;
        foreach (var node in current.Children) node.Dispose(); current.Children.Clear();
        var name = System.IO.Path.GetFileName(path); current.Children.Add(Node(name.Length > 0 ? name : path, path));
    }
    private void ReloadRoots()
    {
        foreach (var node in favorites.Children) node.Dispose(); favorites.Children.Clear();
        foreach (var root in library.Roots) favorites.Children.Add(new(root.Name, root.Path, library, lifetime.Token, root: root));
    }
    private void ReloadRecent()
    {
        foreach (var node in recent.Children) node.Dispose(); recent.Children.Clear();
        foreach (var visit in library.Recent) recent.Children.Add(new(visit.Name, visit.Path, library, lifetime.Token, isFile: visit.IsFile));
    }
    [RelayCommand] private async Task RefreshAsync()
    { if (SelectedNode is { } node) await node.LoadAsync(true); }
    public void Dispose()
    {
        lifetime.Cancel(); library.RootsChanged -= ReloadRoots; library.RecentChanged -= ReloadRecent;
        foreach (var node in Nodes) node.Dispose();
    }
}
