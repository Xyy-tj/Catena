using System.Collections.ObjectModel;
using Avalonia.Threading;
using Catena.Contracts;
using Catena.Core;
using Catena.Storage.Local;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILocationProvider provider;
    private readonly ISearchProvider search;
    private readonly IPlatformActions platform;
    private readonly IWorkspaceRepository repository;
    private readonly DispatcherTimer autosave;
    private Workspace workspace;
    private bool persistenceAvailable;
    private bool disposed;
    private readonly IIndexedSearchProvider? indexedSearch;
    private readonly IAiSearchPlanner? aiPlanner;
    public SettingsViewModel? Settings { get; }
    public PreviewViewModel Preview { get; }
    public LocationLibraryViewModel? Library { get; }
    private readonly Dictionary<Guid, PaneSearchViewModel> searches = [];
    private readonly Dictionary<Guid, ChatViewModel> chats = [];
    private readonly IAiChatClient? chatClient;
    public ObservableCollection<PaneViewModel> Panes { get; } = [];
    public ObservableCollection<WorkspaceSummary> Workspaces { get; } = [];
    public PaneViewModel? ActivePane => Panes.FirstOrDefault(p => p.IsActive);
    public int PaneCount => workspace.PaneCount;
    public bool IsFourPanes => PaneCount == 4;
    public bool IsTwoPanes => PaneCount == 2;
    public bool TwoPaneRows => workspace.TwoPaneRows;
    public bool IsRowsLayout => PaneCount == 2 && TwoPaneRows;
    public bool IsColumnsLayout => PaneCount == 2 && !TwoPaneRows;
    public bool IsSinglePane => PaneCount == 1;
    private void NotifyLayout() { OnPropertyChanged(nameof(PaneCount)); OnPropertyChanged(nameof(IsFourPanes)); OnPropertyChanged(nameof(IsTwoPanes)); OnPropertyChanged(nameof(IsSinglePane)); OnPropertyChanged(nameof(TwoPaneRows)); OnPropertyChanged(nameof(IsRowsLayout)); OnPropertyChanged(nameof(IsColumnsLayout)); }
    public double HorizontalRatio { get => workspace.HorizontalRatio; set { workspace.HorizontalRatio = Math.Clamp(value, .001, .999); ScheduleSave(); } }
    public double VerticalRatio { get => workspace.VerticalRatio; set { workspace.VerticalRatio = Math.Clamp(value, .001, .999); ScheduleSave(); } }
    public double PreviewWidth { get => workspace.PreviewWidth; set { workspace.PreviewWidth = Math.Clamp(value, 200, 10000); ScheduleSave(); } }
    public event Action? LayoutChanged;
    [ObservableProperty] private string workspaceName = "默认工作区";
    [ObservableProperty] private WorkspaceSummary? selectedWorkspace;
    [ObservableProperty] private string message = "正在恢复工作区…";
    public bool HasMessage => Message.Length > 0;
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    [ObservableProperty] private bool workspaceToolsVisible;
    [ObservableProperty] private bool isBusy = true;
    public bool CanManage => !IsBusy && persistenceAvailable;
    [ObservableProperty] private bool previewVisible = true;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanManage));
    partial void OnPreviewVisibleChanged(bool value) => RefreshPreview();
    private void RefreshPreview() => _ = Preview.ShowAsync(PreviewVisible ? ActivePane?.SelectedEntry : null);
    private void PaneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (sender == ActivePane && e.PropertyName == nameof(PaneViewModel.SelectedEntry)) RefreshPreview(); }
    public MainViewModel(ILocationProvider provider, ISearchProvider search, IPlatformActions platform, IWorkspaceRepository repository,
        IIndexedSearchProvider? indexedSearch = null, IAiSearchPlanner? aiPlanner = null, SettingsViewModel? settings = null, IFilePreviewProvider? previews = null, LocationLibraryViewModel? library = null, IAiChatClient? chatClient = null)
    {
        this.provider = provider; this.search = search; this.platform = platform; this.repository = repository;
        this.indexedSearch = indexedSearch; this.aiPlanner = aiPlanner; Settings = settings;
        Library = library; this.chatClient = chatClient;
        Preview = new(previews ?? new LocalFilePreviewProvider(), platform, entry => { if (Library is not null) _ = Library.RecordAsync(entry.DisplayPath, !entry.IsDirectory); });
        workspace = Workspace.Create(provider.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        autosave.Tick += async (_, _) => { autosave.Stop(); if (!IsBusy) await SaveSafelyAsync(); };
    }

    public async Task InitializeAsync()
    {
        if (Library is not null) await Library.InitializeAsync();
        if (Settings is not null) await Settings.LoadAsync();
        try
        {
            await repository.InitializeAsync();
            workspace = await repository.LoadAsync() ?? workspace;
            persistenceAvailable = true;
            await repository.SaveAsync(workspace);
            await ReloadWorkspacesAsync();
            Message = "";
        }
        catch (Exception ex) when (IsPersistenceError(ex))
        { Message = "无法恢复工作区，原数据库已保留。当前为临时会话，请检查数据目录或从备份恢复后重启。"; }
        ApplyWorkspace(); IsBusy = false;
        await Task.WhenAll(Panes.Select(p => p.RefreshCommand.ExecuteAsync(null)));
    }
    private static bool IsPersistenceError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Data.Common.DbException or InvalidOperationException or ArgumentException;
    private void ApplyWorkspace()
    {
        ClearDialogs();
        foreach (var pane in Panes) { pane.StateChanged -= ScheduleSave; pane.PropertyChanged -= PaneChanged; pane.Dispose(); }
        Panes.Clear(); WorkspaceName = workspace.Name;
        for (var i = 0; i < workspace.Panes.Count; i++)
        {
            var pane = new PaneViewModel(provider, platform, workspace.Panes[i], i + 1, Library);
            pane.StateChanged += ScheduleSave; pane.PropertyChanged += PaneChanged; Panes.Add(pane);
        }
        Activate(Panes.Take(PaneCount).FirstOrDefault(p => p.Id == workspace.ActivePaneId) ?? Panes[0]);
        NotifyLayout();
        LayoutChanged?.Invoke();
    }
    public void Activate(PaneViewModel pane)
    {
        foreach (var item in Panes) item.IsActive = item == pane;
        workspace.ActivePaneId = pane.Id;
        OnPropertyChanged(nameof(ActivePane)); RefreshPreview(); ScheduleSave();
    }
    [RelayCommand] private void SetLayout(string count)
    {
        if (count is "rows" or "columns") { workspace.TwoPaneRows = count == "rows"; count = "2"; }
        if (!int.TryParse(count, out var value) || value is not (1 or 2 or 4)) return;
        workspace.PaneCount = value;
        if (ActivePane is null || Panes.IndexOf(ActivePane) >= value) Activate(Panes[0]);
        NotifyLayout();
        LayoutChanged?.Invoke(); ScheduleSave();
    }
    private Workspace Snapshot() => workspace with { Panes = Panes.Select(p => p.Snapshot()).ToList() };
    private void ScheduleSave()
    {
        if (disposed || IsBusy || !persistenceAvailable) return;
        autosave.Stop(); autosave.Start();
    }
    public async Task<bool> SaveSafelyAsync()
    {
        autosave.Stop();
        if (Library is not null) await Library.FlushAsync();
        if (!persistenceAvailable || Panes.Count == 0) return true;
        try { await repository.SaveAsync(Snapshot()); return true; }
        catch (Exception ex) when (IsPersistenceError(ex)) { Message = "工作区保存失败，请检查磁盘空间和数据目录权限。当前窗口仍可使用。"; return false; }
    }
    private async Task ReloadWorkspacesAsync()
    {
        Workspaces.Clear(); foreach (var item in await repository.ListAsync()) Workspaces.Add(item);
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == workspace.Id);
    }
    [RelayCommand] private async Task SwitchWorkspaceAsync()
    {
        if (!CanManage || SelectedWorkspace is not { } target || target.Id == workspace.Id) return;
        IsBusy = true;
        try
        {
            if (!await SaveSafelyAsync()) return;
            var loaded = await repository.LoadAsync(target.Id);
            if (loaded is null) return;
            workspace = loaded; ApplyWorkspace(); await SaveSafelyAsync();
            await Task.WhenAll(Panes.Select(p => p.RefreshCommand.ExecuteAsync(null)));
        }
        catch (Exception ex) when (IsPersistenceError(ex)) { Message = "切换失败，目标工作区数据已保留。"; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task SaveAsAsync()
    {
        if (!CanManage || string.IsNullOrWhiteSpace(WorkspaceName)) return;
        IsBusy = true;
        try
        {
            if (!await SaveSafelyAsync()) return;
            var copy = Snapshot() with { Id = Guid.NewGuid(), Name = WorkspaceName.Trim() };
            await repository.SaveAsync(copy); workspace = copy; await ReloadWorkspacesAsync(); Message = "已另存工作区。";
        }
        catch (Exception ex) when (IsPersistenceError(ex)) { Message = "另存工作区失败，请检查数据目录。"; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private async Task RenameAsync()
    {
        if (!CanManage || string.IsNullOrWhiteSpace(WorkspaceName)) return;
        IsBusy = true;
        try
        {
            var renamed = Snapshot() with { Name = WorkspaceName.Trim() };
            await repository.SaveAsync(renamed); workspace = renamed; await ReloadWorkspacesAsync(); Message = "工作区已重命名。";
        }
        catch (Exception ex) when (IsPersistenceError(ex)) { Message = "重命名失败。"; }
        finally { IsBusy = false; }
    }
    public async Task DeleteCurrentAsync()
    {
        if (!CanManage || Workspaces.Count < 2) { Message = "至少保留一个工作区。"; return; }
        IsBusy = true; autosave.Stop();
        try
        {
            var target = Workspaces.First(w => w.Id != workspace.Id);
            var loaded = await repository.LoadAsync(target.Id) ?? throw new InvalidDataException();
            await repository.SaveAsync(loaded);
            await repository.DeleteAsync(workspace.Id);
            workspace = loaded; ApplyWorkspace(); await ReloadWorkspacesAsync();
            await Task.WhenAll(Panes.Select(p => p.RefreshCommand.ExecuteAsync(null)));
            Message = "工作区已删除，文件未受影响。";
        }
        catch (Exception ex) when (IsPersistenceError(ex)) { Message = "删除工作区失败。"; }
        finally { IsBusy = false; }
    }

    public PaneSearchViewModel GetSearch(PaneViewModel pane)
    {
        if (!searches.TryGetValue(pane.Id, out var searchModel))
        {
            searchModel = new(pane, provider, search, indexedSearch, aiPlanner, Settings, async entry =>
            {
                var index = Panes.IndexOf(pane);
                if (index < 0) return;
                if (index >= PaneCount) SetLayout(index < 2 ? "2" : "4");
                Activate(pane);
                await pane.NavigateAsync(entry.IsDirectory ? entry.Location : provider.GetParent(entry.Location)!, entry.IsDirectory ? null : entry.Location);
            });
            searchModel.WorkspaceScopes = () => Panes.Take(PaneCount).Select(p => p.Location).Distinct().ToArray();
            searches.Add(pane.Id, searchModel);
        }
        return searchModel;
    }
    public ChatViewModel GetChat(PaneViewModel pane)
    {
        var path = provider.GetPath(pane.Location);
        if (chats.TryGetValue(pane.Id, out var previous) && previous.Folder == path) return previous;
        previous?.Dispose();
        var profile = Library?.Roots.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))?.Profile;
        var context = System.Text.Json.JsonSerializer.Serialize(new
        {
            path, visibleEntryCount = pane.Entries.Count, listing = "当前可见列表，最多 120 项，不含文件正文或子目录完整清单",
            entries = pane.Entries.Take(120).Select(e => new { e.Name, e.IsDirectory, e.SizeText, e.ModifiedText }),
            cachedProfile = profile is null ? null : new { profile.Files, profile.Folders, profile.Types, profile.Updated, profile.Partial }
        });
        var model = new ChatViewModel(chatClient, Settings, path, context);
        chats[pane.Id] = model; return model;
    }
    private void ClearDialogs()
    {
        foreach (var item in searches.Values) item.Dispose(); searches.Clear();
        foreach (var item in chats.Values) item.Dispose(); chats.Clear();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; autosave.Stop(); ClearDialogs(); Preview.Dispose();
        foreach (var pane in Panes) { pane.PropertyChanged -= PaneChanged; pane.Dispose(); }
        Library?.Dispose();
    }
}

