using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Location = Catena.Contracts.Location;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Catena.App;
using Catena.Contracts;
using Catena.Core;
using Catena.Persistence;
using Catena.Platform.Windows;
using Catena.Storage.Local;

namespace Catena.Tests;
public sealed class SettingsAndSearchUiTests
{
    [AvaloniaFact] public async Task HeaderSearchSubmitsWorkspaceScopesAndAppearancePersists()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var rootA = Directory.CreateDirectory(Path.Combine(temp.Path, "a")).FullName;
        var rootB = Directory.CreateDirectory(Path.Combine(temp.Path, "b")).FullName;
        var workspace = Workspace.Create(provider.Normalize(rootA)); workspace.Panes[1] = workspace.Panes[1] with { Location = provider.Normalize(rootB) };
        var repo = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "workspaces.db")); await repo.InitializeAsync(); await repo.SaveAsync(workspace);
        var store = new JsonAppSettingsStore(Path.Combine(temp.Path, "settings.json"));
        await store.SaveAsync(new() { Theme = 1, Skin = 1, AiModel = "fixture" });
        var index = new Index(); var planner = new Planner();
        using var settings = new SettingsViewModel(store, new TestProtector(), planner, index);
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new Platform(), repo, index, planner, settings);
        var window = new MainWindow { DataContext = model }; window.Show();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (model.IsBusy || model.Panes.Any(p => p.IsLoading)) await Task.Delay(10, timeout.Token);
        var input = window.FindControl<TextBox>("GlobalSearchBox")!;
        input.Text = "预算"; input.Focus(); Assert.Empty(window.OwnedWindows); Assert.Null(index.Request);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, ""); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "");
        while (index.Request is null) await Task.Delay(10, timeout.Token);
        Assert.Equal(2, index.Request.Scopes!.Count); Assert.Contains(provider.Normalize(rootB), index.Request.Scopes);
        Assert.Single(window.OwnedWindows.OfType<SearchWindow>()); Assert.Equal(0, planner.Calls);
        var pane = model.Panes[0]; pane.ZoomList(2); Assert.Equal(1.2, pane.ListZoom); Assert.Equal(1, model.Panes[1].ListZoom);
        await model.SaveSafelyAsync(); Assert.Equal(1.2, (await repo.LoadAsync())!.Panes[0].ListZoom);
        window.UpdateLayout(); Assert.NotNull(window.FindControl<Image>("SkinBackground")!.Source);
        var tabs = new SettingsWindow { DataContext = settings }; tabs.Show(window); tabs.UpdateLayout();
        Assert.Equal(6, tabs.FindControl<TabControl>("SettingsTabs")!.ItemCount);
        tabs.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 1; tabs.UpdateLayout();
        var artifacts = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
        if (artifacts is not null)
        {
            foreach (var pair in new[] { (window as Window, "catena-blue-mist.png"), (tabs as Window, "catena-settings-appearance.png") })
            { using var bitmap = new RenderTargetBitmap(new PixelSize((int)pair.Item1.Bounds.Width, (int)pair.Item1.Bounds.Height)); bitmap.Render(pair.Item1); bitmap.Save(Path.Combine(artifacts, pair.Item2), PngBitmapEncoderOptions.Default); }
        }
        tabs.Close(); foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
        window.Close(); while (window.IsVisible) await Task.Delay(10, timeout.Token);
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }
    [Fact] public async Task WindowsKeyIsEncryptedAtRestAndSettingsRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestDirectory(); var protector = new WindowsSecretProtector();
        var store = new JsonAppSettingsStore(Path.Combine(temp.Path, "settings.json"));
        const string secret = "test-only-not-a-real-key";
        var encrypted = protector.Protect(secret);
        Assert.NotEqual(secret, encrypted);
        await store.SaveAsync(new() { AiBaseUrl = "https://models.example/v1", AiModel = "configured-model", ProtectedApiKey = encrypted }, TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(secret, protector.Unprotect(saved.ProtectedApiKey));
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"), TestContext.Current.CancellationToken));
    }
    [AvaloniaFact] public async Task SettingsPreserveOrClearKeyAndPreventReuseAcrossHosts()
    {
        using var temp = new TestDirectory();
        var store = new JsonAppSettingsStore(Path.Combine(temp.Path, "settings.json"));
        using var settings = new SettingsViewModel(store, new TestProtector(), new Planner(), new Index());
        await settings.LoadAsync(); settings.AiBaseUrl = "https://first.example/v1"; settings.AiModel = "custom"; settings.ApiKey = "fake-key";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal("fake-key", settings.Connection().ApiKey); Assert.Equal("", settings.ApiKey);
        settings.AiModel = "other-model"; await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal("fake-key", settings.Connection().ApiKey);
        settings.AiBaseUrl = "https://second.example/v1"; await settings.SaveCommand.ExecuteAsync(null);
        Assert.Contains("重新填写", settings.Status); Assert.Equal("https://first.example/v1", settings.Current.AiBaseUrl);
        settings.ClearApiKey = true; await settings.SaveCommand.ExecuteAsync(null);
        Assert.Equal("", settings.Connection().ApiKey); Assert.Equal("https://second.example/v1", settings.Current.AiBaseUrl);
        var window = new SettingsWindow { DataContext = settings }; window.Show(); window.UpdateLayout();
        Assert.True(window.Bounds.Height > 500);
        var artifacts = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
        if (artifacts is not null)
        {
            Directory.CreateDirectory(artifacts); using var bitmap = new RenderTargetBitmap(new PixelSize(700, 760));
            bitmap.Render(window); bitmap.Save(Path.Combine(artifacts, "catena-settings.png"), PngBitmapEncoderOptions.Default);
        }
        window.Close();
    }
    [AvaloniaFact] public async Task AiPlanSearchAndTargetPaneNavigationWorkTogether()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var path = Path.Combine(temp.Path, "预算.xlsx"); File.WriteAllText(path, "fixture");
        var index = new Index { Entry = await provider.GetEntryAsync(provider.Normalize(path)) }; var planner = new Planner();
        var repository = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "workspaces.db"));
        await repository.InitializeAsync(); await repository.SaveAsync(Workspace.Create(provider.Normalize(temp.Path)));
        using var settings = new SettingsViewModel(new JsonAppSettingsStore(Path.Combine(temp.Path, "settings.json")), new TestProtector(), planner, index);
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new Platform(), repository, index, planner, settings);
        await model.InitializeAsync(); var search = model.GetSearch(model.Panes[1]); search.Query = "找预算表";
        Assert.Equal(0, planner.Calls); // AI never runs on each keystroke.
        settings.AiModel = "test-model"; await settings.SaveCommand.ExecuteAsync(null);
        await search.AiSearchCommand.ExecuteAsync(null);
        Assert.Equal(1, planner.Calls); Assert.Equal("预算", Assert.Single(index.Request!.Plan.Terms));
        Assert.Equal(provider.Normalize(temp.Path), index.Request.Scope);
        search.SelectedResult = Assert.Single(search.Results); await search.LocateCommand.ExecuteAsync(null);
        Assert.Equal(model.Panes[1], model.ActivePane); Assert.Equal("预算.xlsx", model.Panes[1].SelectedEntry!.Name);
        search.Query = "普通关键词"; await search.SearchCommand.ExecuteAsync(null); Assert.Equal(1, planner.Calls);
    }
    [AvaloniaFact] public async Task CancelledSearchCannotPublishOrLeaveDisposedCancellationSource()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var index = new Index { WaitForRelease = true }; var planner = new Planner();
        var repository = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "workspaces.db"));
        await repository.InitializeAsync(); await repository.SaveAsync(Workspace.Create(provider.Normalize(temp.Path)));
        using var settings = new SettingsViewModel(new JsonAppSettingsStore(Path.Combine(temp.Path, "settings.json")), new TestProtector(), planner, index);
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new Platform(), repository, index, planner, settings);
        await model.InitializeAsync(); var search = model.GetSearch(model.Panes[0]); search.Query = "旧查询";
        var old = search.SearchCommand.ExecuteAsync(null);
        search.CancelCommand.Execute(null); index.Release.SetResult(); await old;
        search.Query = "新查询"; // Regression: this used to cancel an already disposed CTS.
        Assert.Empty(search.Results); Assert.False(search.IsSearching);
    }
    private sealed class TestProtector : ISecretProtector
    { public string Protect(string value) => "test:" + value; public string Unprotect(string value) => value.Length == 0 ? "" : value[5..]; }
    private sealed class Planner : IAiSearchPlanner
    {
        public int Calls { get; private set; }
        public Task<FileSearchPlan> PlanAsync(string description, AiConnection connection, CancellationToken token = default)
        { Calls++; return Task.FromResult(new FileSearchPlan { Terms = ["预算"], Extensions = ["xlsx"], Explanation = "查找预算表" }); }
    }
    private sealed class Index : IIndexedSearchProvider
    {
        public FileEntry? Entry { get; init; }
        public IndexedSearchRequest? Request { get; private set; }
        public bool WaitForRelease { get; init; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IndexedSearchResponse> SearchAsync(IndexedSearchRequest request, string executablePath, string instanceName, CancellationToken token = default)
        { Request = request; if (WaitForRelease) await Release.Task; return new(Entry is null ? [] : [Entry], false, "compiled", TimeSpan.FromMilliseconds(10)); }
    }
    private sealed class Platform : IPlatformActions
    { public void Open(Location location) { } public void Reveal(Location location) { } public void OpenTerminal(Location directory) { } }
}

