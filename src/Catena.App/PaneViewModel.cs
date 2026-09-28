using Catena.Contracts;
using Catena.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class PaneViewModel : ObservableObject, IDisposable
{
    private readonly ILocationProvider provider;
    private readonly IPlatformActions platform;
    private readonly PaneNavigator navigator;
    public LocationLibraryViewModel? Library { get; }
    public FolderBrowserViewModel? Browser { get; }
    public Guid Id => navigator.Id;
    public Location Location => navigator.Location;
    public Location? PinnedRoot => navigator.PinnedRoot;
    public IEnumerable<Location> History => navigator.History;
    public string Caption { get; }
    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(provider.GetPath(Location))) is { Length: > 0 } name ? name : provider.GetPath(Location);
    public FileEntry FolderEntry => new(Location, FolderName, EntryKind.Directory, null, null, EntryCapabilities.None);
    public string PinnedLabel => PinnedRoot is null ? "" : provider.GetPath(PinnedRoot);
    public string PinButtonText => HasPinnedRoot ? "已固定" : "固定";
    public bool IsLoading => navigator.IsLoading;
    public bool CanBack => navigator.CanBack;
    public bool CanForward => navigator.CanForward;
    public bool HasPinnedRoot => PinnedRoot is not null;
    public event Action? StateChanged;
    public event Action? EntriesUpdating;
    [ObservableProperty] private string address = "";
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private bool filterVisible;
    [ObservableProperty] private bool treeVisible;
    [ObservableProperty] private double treeWidth = 176;
    [ObservableProperty] private double listZoom = 1;
    partial void OnListZoomChanged(double value) => StateChanged?.Invoke();
    public void ZoomList(double delta) => ListZoom = Math.Clamp(Math.Round(ListZoom + delta * .1, 2), .75, 2);
    partial void OnTreeVisibleChanged(bool value) => StateChanged?.Invoke();
    partial void OnTreeWidthChanged(double value) => StateChanged?.Invoke();
    public string FilterButtonText => Filter.Length > 0 ? "筛选中" : "筛选";
    [ObservableProperty] private int sortIndex;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private IReadOnlyList<FileEntry> entries = [];
    [ObservableProperty] private FileEntry? selectedEntry;
    public List<Location> SelectedLocations { get; set; } = [];

    public PaneViewModel(ILocationProvider provider, IPlatformActions platform, PaneState state, int number, LocationLibraryViewModel? library = null)
    {
        this.provider = provider; this.platform = platform; navigator = new(provider, state);
        Caption = $"窗格 {number}"; address = provider.GetPath(state.Location);
        filter = state.Filter; sortIndex = state.Sort; SelectedLocations = state.SelectedLocations.ToList();
        filterVisible = filter.Length > 0;
        Library = library; treeVisible = state.TreeVisible; treeWidth = state.TreeWidth; listZoom = state.ListZoom;
        if (library is not null) { Browser = new(library, NavigatePathAsync); Browser.SetCurrent(address); }
        navigator.Visited += RecordVisit;
        navigator.Changed += OnNavigationChanged;
    }
    private void RecordVisit(Location location) { if (Library is not null) _ = Library.RecordAsync(provider.GetPath(location), false); }
    public async Task NavigatePathAsync(string path, bool isFile = false)
    {
        try
        {
            var location = provider.Normalize(path); var entry = await provider.GetEntryAsync(location);
            await NavigateAsync(entry.IsDirectory ? location : provider.GetParent(location)!, entry.IsDirectory ? null : location);
        }
        catch (FileSystemException ex) { Status = ex.Message; }
    }
    [RelayCommand] private async Task AddFavoriteAsync() { if (Library is not null) await Library.AddAsync(provider.GetPath(Location)); }

    private void OnNavigationChanged()
    {
        Address = provider.GetPath(Location);
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(FolderEntry));
        Browser?.SetCurrent(Address);
        OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(CanBack)); OnPropertyChanged(nameof(CanForward));
        UpdateEntries(); StateChanged?.Invoke();
    }
    partial void OnFilterChanged(string value) { OnPropertyChanged(nameof(FilterButtonText)); UpdateEntries(); StateChanged?.Invoke(); }
    partial void OnSortIndexChanged(int value) { UpdateEntries(); StateChanged?.Invoke(); }
    private void UpdateEntries()
    {
        var filtered = navigator.Entries.Where(e => e.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase));
        // Keep progressive batches cheap; complete sorting once enumeration finishes.
        EntriesUpdating?.Invoke();
        Entries = (navigator.IsLoading ? filtered : SortIndex switch
        {
            1 => filtered.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.ModifiedAt),
            2 => filtered.OrderByDescending(e => e.IsDirectory).ThenByDescending(e => e.Size),
            _ => filtered.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
        }).ToArray();
        Status = navigator.Error ?? (navigator.IsLoading ? $"读取中 · {Entries.Count} 项" :
            Entries.Count == 0 ? (Filter.Length > 0 ? "没有匹配项" : "空目录") : $"{Entries.Count:N0} 项");
    }

    public async Task NavigateAsync(Location target, Location? select = null)
    {
        Filter = ""; SelectedLocations = select is null ? [] : [select];
        await navigator.NavigateAsync(target);
        SelectedEntry = select is null ? null : Entries.FirstOrDefault(e => provider.AreEqual(e.Location, select));
        SelectedLocations = SelectedEntry is null ? [] : [SelectedEntry.Location];
        NotifySelectionChanged();
    }
    [RelayCommand] private async Task NavigateAddressAsync()
    {
        try
        {
            var location = provider.Normalize(Address);
            var entry = await provider.GetEntryAsync(location);
            await NavigateAsync(entry.IsDirectory ? location : provider.GetParent(location)!, entry.IsDirectory ? null : location);
        }
        catch (FileSystemException ex) { Status = ex.Message; }
    }
    [RelayCommand] private Task RefreshAsync() => navigator.RefreshAsync();
    [RelayCommand] private Task BackAsync() => navigator.BackAsync();
    [RelayCommand] private Task ForwardAsync() => navigator.ForwardAsync();
    [RelayCommand] private Task UpAsync() => navigator.UpAsync();
    [RelayCommand] private Task HomeAsync() => navigator.HomeAsync();
    [RelayCommand] private void Pin() { navigator.PinnedRoot = Location; PinChanged(); }
    [RelayCommand] private void Unpin() { navigator.PinnedRoot = null; PinChanged(); }
    [RelayCommand] private void TogglePin() { if (HasPinnedRoot) Unpin(); else Pin(); }
    private void PinChanged() { OnPropertyChanged(nameof(PinnedLabel)); OnPropertyChanged(nameof(HasPinnedRoot)); OnPropertyChanged(nameof(PinButtonText)); StateChanged?.Invoke(); }
    [RelayCommand] private async Task OpenAsync()
    {
        if (SelectedEntry is not { } entry) return;
        if (entry.IsDirectory) await NavigateAsync(entry.Location);
        else RunPlatform(() => { platform.Open(entry.Location); if (Library is not null) _ = Library.RecordAsync(provider.GetPath(entry.Location), true); });
    }
    [RelayCommand] private void Reveal() => RunPlatform(() => platform.Reveal(SelectedEntry?.Location ?? Location));
    [RelayCommand] private void Terminal() => RunPlatform(() => platform.OpenTerminal(Location));
    private void RunPlatform(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or PlatformNotSupportedException)
        { Status = "系统操作失败，请检查关联程序或目录是否可用。"; }
    }
    public void NotifySelectionChanged()
    {
        if (!IsLoading)
        {
            var locations = SelectedLocations.ToHashSet();
            var selected = Entries.Where(e => locations.Contains(e.Location)).ToArray();
            var size = selected.Sum(e => e.Size ?? 0);
            Status = $"{Entries.Count:N0} 项" + (selected.Length > 0 ? $"   |   已选择 {selected.Length} 项" + (size > 0 ? $"（{size / 1048576d:N1} MB）" : "") : "");
        }
        StateChanged?.Invoke();
    }
    public PaneState Snapshot() => navigator.Snapshot() with { Filter = Filter, Sort = SortIndex, SelectedLocations = SelectedLocations.ToList(), TreeVisible = TreeVisible, TreeWidth = TreeWidth, ListZoom = ListZoom };
    public void Dispose() { navigator.Changed -= OnNavigationChanged; navigator.Visited -= RecordVisit; navigator.Dispose(); Browser?.Dispose(); }
}
