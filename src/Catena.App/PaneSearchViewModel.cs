using System.Collections.ObjectModel;
using Avalonia.Threading;
using Catena.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class PaneSearchViewModel : ObservableObject, IDisposable
{
    private readonly ILocationProvider provider;
    private readonly ISearchProvider local;
    private readonly IIndexedSearchProvider? index;
    private readonly IAiSearchPlanner? planner;
    private readonly SettingsViewModel? settings;
    private readonly Func<FileEntry, Task> locate;
    private readonly DispatcherTimer debounce;
    private CancellationTokenSource? pending;
    private long revision;
    private bool disposed;
    private string lastFolder;
    public PaneViewModel Pane { get; }
    public string Caption => Pane.Caption + " · 查找";
    public string Folder => provider.GetPath(Pane.Location);
    [ObservableProperty] private IReadOnlyList<FileEntry> results = [];
    public Func<IReadOnlyList<Location>>? WorkspaceScopes { get; set; }
    public bool LiveInput { get; set; } = true;
    public bool LastUsedAi { get; private set; }
    [ObservableProperty] private string query = "";
    [ObservableProperty] private int scopeIndex = 1;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string planSummary = "";
    [ObservableProperty] private bool canSearchCurrentFolder;
    [ObservableProperty] private bool isSearching;
    [ObservableProperty] private FileEntry? selectedResult;
    public bool HasPlan => PlanSummary.Length > 0;
    public event Action? Located;
    public event Action? Disposed;
    partial void OnPlanSummaryChanged(string value) => OnPropertyChanged(nameof(HasPlan));
    partial void OnQueryChanged(string value) { Invalidate(); if (LiveInput && settings?.Current.LiveSearch == true && value.Length > 0) debounce.Start(); }
    partial void OnScopeIndexChanged(int value) { Invalidate(); if (LiveInput && settings?.Current.LiveSearch == true && Query.Length > 0) debounce.Start(); }
    public PaneSearchViewModel(PaneViewModel pane, ILocationProvider provider, ISearchProvider local, IIndexedSearchProvider? index,
        IAiSearchPlanner? planner, SettingsViewModel? settings, Func<FileEntry, Task> locate)
    {
        Pane = pane; this.provider = provider; this.local = local; this.index = index; this.planner = planner; this.settings = settings; this.locate = locate;
        lastFolder = Folder;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        debounce.Tick += async (_, _) => { debounce.Stop(); if (!disposed) await SearchCore(false, false); };
        if (settings is not null) settings.Saved += Invalidate;
        Pane.PropertyChanged += PaneChanged;
    }
    private void PaneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(PaneViewModel.FolderName) && lastFolder != Folder) { lastFolder = Folder; Invalidate(); OnPropertyChanged(nameof(Folder)); } }
    private void Invalidate() { Suspend(); Results = []; SelectedResult = null; PlanSummary = ""; CanSearchCurrentFolder = false; }
    public void Suspend() { debounce.Stop(); var previous = pending; pending = null; previous?.Cancel(); revision++; IsSearching = false; }
    [RelayCommand] private void Cancel() { Suspend(); Status = "已取消"; }
    [RelayCommand(AllowConcurrentExecutions = true)] private Task SearchAsync() => SearchCore(false, true);
    [RelayCommand(AllowConcurrentExecutions = true)] private Task AiSearchAsync() => SearchCore(true, true);
    [RelayCommand] private async Task SearchCurrentFolderAsync() { ScopeIndex = 1; await SearchCore(false, true, localOnly: true); }
    private async Task SearchCore(bool useAi, bool submitted, bool localOnly = false)
    {
        Invalidate(); if (string.IsNullOrWhiteSpace(Query) || disposed) { Status = ""; return; }
        LastUsedAi = useAi;
        using var cancellation = new CancellationTokenSource(); pending = cancellation; var request = revision;
        var text = Query.Trim().Trim('"'); var scope = ScopeIndex == 1 ? Pane.Location : null; IsSearching = true;
        try
        {
            if (!useAi && Path.IsPathFullyQualified(text))
            {
                if (!submitted) { Status = "Enter 定位"; return; }
                var entry = await provider.GetEntryAsync(provider.Normalize(text), cancellation.Token);
                if (request != revision) return;
                await locate(entry); Located?.Invoke(); return;
            }
            FileSearchPlan plan;
            if (useAi)
            {
                if (planner is null || settings is null || string.IsNullOrWhiteSpace(settings.Current.AiModel)) throw new SearchAssistanceException("请先在设置中配置模型。");
                Status = "理解描述…"; plan = await planner.PlanAsync(text, settings.Connection(), cancellation.Token);
                if (request != revision) return; plan.Validate();
                PlanSummary = string.Join(" · ", new[] { string.Join(" ", plan.Terms), string.Join(" / ", plan.Extensions), plan.Explanation }.Where(x => x.Length > 0));
            }
            else plan = new() { Terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
            plan.Validate();
            if (!localOnly)
            {
                if (index is null || settings is null) throw new SearchAssistanceException("快速索引暂不可用。");
                Status = "查找中…";
                var scopes = ScopeIndex == 2 ? WorkspaceScopes?.Invoke() : null;
                if (ScopeIndex == 2 && (scopes is null || scopes.Count == 0)) throw new SearchAssistanceException("当前工作区没有打开的文件夹。");
                var limit = settings.Current.SearchResultLimit;
                var result = await index.SearchAsync(new(plan, scope, limit, scopes), settings.Current.EverythingExecutable, settings.Current.EverythingInstance, cancellation.Token);
                if (request != revision) return;
                Results = result.Entries;
                Status = $"{Results.Count} 项 · {result.Elapsed.TotalMilliseconds:N0} ms" + (result.LimitReached ? $" · 前 {limit} 项" : "");
            }
            else
            {
                if (request != revision) return;
                Status = "目录查找…";
                await foreach (var batch in local.SearchAsync(new(text, scope ?? Pane.Location), cancellation.Token))
                {
                    if (request != revision) return;
                    Results = Results.Concat(batch.Entries).ToArray();
                    Status = $"{Results.Count} 项 · 目录查找" + (batch.SkippedDirectories > 0 ? $" · 跳过 {batch.SkippedDirectories} 项" : "") + (batch.LimitReached ? " · 前 200 项" : "");
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (FileSystemException ex) { if (request == revision) Status = ex.Message; }
        catch (SearchAssistanceException ex)
        { if (request == revision) { Status = ex.Message; CanSearchCurrentFolder = !useAi; } }
        finally { if (request == revision) { pending = null; IsSearching = false; } }
    }
    [RelayCommand] private async Task LocateAsync()
    { if (SelectedResult is { } entry) { await locate(entry); Located?.Invoke(); } }
    public void Dispose() { if (disposed) return; disposed = true; Suspend(); Pane.PropertyChanged -= PaneChanged; if (settings is not null) settings.Saved -= Invalidate; Disposed?.Invoke(); }
}
