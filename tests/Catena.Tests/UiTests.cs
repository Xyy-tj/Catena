using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Catena.App;
using Catena.Contracts;
using Catena.Core;
using Catena.Persistence;
using Catena.Storage.Local;
using Location = Catena.Contracts.Location;

[assembly: AvaloniaTestApplication(typeof(Catena.Tests.TestAppBuilder))]

namespace Catena.Tests;
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Catena.App.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiTests
{
    [AvaloniaFact] public async Task SplittersResizeLiveAndRestoreAfterRestart()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var repository = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "workspace.db"));
        await repository.InitializeAsync(); var state = Workspace.Create(provider.Normalize(temp.Path)); state.PaneCount = 4;
        await repository.SaveAsync(state);
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new FakePlatform(), repository);
        var window = new MainWindow { DataContext = model }; window.Show();
        await WaitUntilAsync(() => !model.IsBusy && model.Panes.All(p => !p.IsLoading));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); window.UpdateLayout();
        var host = window.FindControl<Grid>("PaneHost")!;
        var content = window.FindControl<Grid>("ContentGrid")!;
        GridSplitter Splitter(string name) => window.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.Name == name);
        var width = host.ColumnDefinitions[0].ActualWidth;
        await Drag(window, Splitter("PaneColumnSplitter"), new Vector(100, 0));
        Assert.InRange(host.ColumnDefinitions[0].ActualWidth - width, 98, 102);
        Assert.Equal(8, host.ColumnDefinitions[1].ActualWidth);
        var height = host.RowDefinitions[0].ActualHeight;
        await Drag(window, Splitter("PaneRowSplitter"), new Vector(0, 65), .25);
        Assert.InRange(host.RowDefinitions[0].ActualHeight - height, 63, 67);
        Assert.Equal(8, host.RowDefinitions[1].ActualHeight);
        await Drag(window, Splitter("PreviewSplitter"), new Vector(-80, 0));
        Assert.InRange(content.ColumnDefinitions[2].ActualWidth, 458, 462);
        var previewWidth = model.PreviewWidth;
        model.PreviewVisible = false; window.UpdateLayout();
        Assert.Equal(0, content.ColumnDefinitions[2].ActualWidth);
        model.PreviewVisible = true; window.UpdateLayout();
        Assert.Equal(previewWidth, content.ColumnDefinitions[2].ActualWidth, 1);
        // Minimum sizes stop extreme drags from hiding a pane completely.
        await Drag(window, Splitter("PaneColumnSplitter"), new Vector(-5000, 0));
        Assert.True(host.ColumnDefinitions[0].ActualWidth >= 239);
        Assert.True(host.ColumnDefinitions[2].ActualWidth >= 239);
        model.SetLayoutCommand.Execute("2"); window.UpdateLayout();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<GridSplitter>(), s => s.Name == "PaneRowSplitter");
        await Drag(window, Splitter("PaneColumnSplitter"), new Vector(70, 0));
        model.SetLayoutCommand.Execute("4"); window.UpdateLayout();
        var savedRatio = model.HorizontalRatio; var savedVertical = model.VerticalRatio;
        Assert.True(await model.SaveSafelyAsync());
        window.Close(); await WaitUntilAsync(() => !window.IsVisible);
        using var restored = new MainViewModel(provider, new LocalSearchProvider(provider), new FakePlatform(), repository);
        var next = new MainWindow { DataContext = restored }; next.Show();
        await WaitUntilAsync(() => !restored.IsBusy && restored.Panes.All(p => !p.IsLoading)); next.UpdateLayout();
        Assert.Equal(savedRatio, restored.HorizontalRatio, 5); Assert.Equal(savedVertical, restored.VerticalRatio, 5);
        Assert.Equal(previewWidth, restored.PreviewWidth, 1);
        Assert.Equal(previewWidth, next.FindControl<Grid>("ContentGrid")!.ColumnDefinitions[2].ActualWidth, 1);
        var row = next.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.Name == "PaneRowSplitter");
        var resetPoint = row.TranslatePoint(new Point(row.Bounds.Width / 4, row.Bounds.Height / 2), next)!.Value;
        next.MouseMove(resetPoint);
        next.MouseDown(resetPoint, MouseButton.Left); next.MouseUp(resetPoint, MouseButton.Left);
        next.MouseDown(resetPoint, MouseButton.Left); next.MouseUp(resetPoint, MouseButton.Left); next.UpdateLayout();
        var nextHost = next.FindControl<Grid>("PaneHost")!;
        Assert.Equal(nextHost.RowDefinitions[0].ActualHeight, nextHost.RowDefinitions[2].ActualHeight, 1);
        next.Close(); await WaitUntilAsync(() => !next.IsVisible);
    }
    private static async Task Drag(Window window, GridSplitter splitter, Vector delta, double horizontalPoint = .5)
    {
        var start = splitter.TranslatePoint(new Point(splitter.Bounds.Width * horizontalPoint, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseMove(start); window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + delta, RawInputModifiers.LeftMouseButton);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); window.UpdateLayout();
        // The boundary must already have moved before releasing the mouse.
        var moved = splitter.TranslatePoint(new Point(splitter.Bounds.Width * horizontalPoint, splitter.Bounds.Height / 2), window)!.Value;
        Assert.True(Math.Abs(moved.X - start.X) + Math.Abs(moved.Y - start.Y) > 1);
        window.MouseUp(start + delta, MouseButton.Left); window.UpdateLayout();
    }

    [AvaloniaFact] public async Task FourPaneWorkspaceSurvivesLayoutChangesAndRestart()
    {
        using var temp = new TestDirectory();
        var provider = new LocalFileSystemProvider();
        var repository = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "workspaces.db"));
        await repository.InitializeAsync();
        var root = provider.Normalize(temp.Path);
        var workspace = Workspace.Create(root); workspace.PaneCount = 4; workspace.Name = "开发工作区";
        var names = new[] { "项目", "原始数据", "下载", "输出" };
        for (var i = 0; i < 4; i++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(temp.Path, names[i])).FullName;
            Directory.CreateDirectory(Path.Combine(directory, "documents"));
            File.WriteAllText(Path.Combine(directory, "需求说明.md"), "fixture");
            File.WriteAllText(Path.Combine(directory, "report.csv"), "name,value");
            workspace.Panes[i] = workspace.Panes[i] with { Location = provider.Normalize(directory), PinnedRoot = provider.Normalize(directory) };
        }
        await repository.SaveAsync(workspace);
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new FakePlatform(), repository);
        var window = new MainWindow { DataContext = model }; window.Show();
        await WaitUntilAsync(() => !model.IsBusy && model.Panes.All(p => !p.IsLoading));
        Assert.Equal(4, window.GetVisualDescendants().OfType<PaneView>().Count());
        Assert.All(model.Panes, p => Assert.Equal(3, p.Entries.Count));
        var fourth = model.Panes[3]; model.Activate(fourth);
        model.SetLayoutCommand.Execute("1"); Assert.Equal(model.Panes[0], model.ActivePane);
        model.SetLayoutCommand.Execute("4"); Assert.Same(fourth, model.Panes[3]); Assert.Equal(workspace.Panes[3].Location, fourth.Location);
        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k"); window.KeyRelease(Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
        Assert.Empty(window.OwnedWindows.OfType<SearchWindow>()); Assert.True(window.FindControl<TextBox>("GlobalSearchBox")!.IsFocused);
        var search = model.GetSearch(model.Panes[3]); search.Query = "需求";
        await search.SearchCurrentFolderCommand.ExecuteAsync(null);
        Assert.Single(search.Results); search.SelectedResult = search.Results[0];
        await search.LocateCommand.ExecuteAsync(null);
        Assert.Equal(fourth, model.ActivePane); Assert.Equal("需求说明.md", fourth.SelectedEntry?.Name);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Contains(fourth.SelectedLocations, l => l.Uri.EndsWith(".md"));
        Assert.True(await model.SaveSafelyAsync());
        var recovered = (await repository.LoadAsync())!;
        Assert.Equal(4, recovered.PaneCount); Assert.Equal(fourth.Id, recovered.ActivePaneId);
        Assert.Single(recovered.Panes[3].SelectedLocations);
        window.UpdateLayout();
        var artifacts = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
        if (artifacts is not null)
        {
            Directory.CreateDirectory(artifacts);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(artifacts, "catena-four-panes.png"), PngBitmapEncoderOptions.Default);
        }
        window.Close(); await WaitUntilAsync(() => !window.IsVisible);
        using var restarted = new MainViewModel(provider, new LocalSearchProvider(provider), new FakePlatform(), repository);
        var restoredWindow = new MainWindow { DataContext = restarted }; restoredWindow.Show();
        await WaitUntilAsync(() => !restarted.IsBusy && restarted.Panes.All(p => !p.IsLoading));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(4, restarted.PaneCount); Assert.Equal(fourth.Id, restarted.ActivePane!.Id);
        Assert.Equal("需求说明.md", restarted.Panes[3].SelectedEntry?.Name);
        restoredWindow.Close(); await WaitUntilAsync(() => !restoredWindow.IsVisible);
    }

    [AvaloniaFact] public async Task NamedWorkspaceSwitchSavesBothSides()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var repository = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "workspace.db"));
        await repository.InitializeAsync(); await repository.SaveAsync(Workspace.Create(provider.Normalize(temp.Path)));
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new FakePlatform(), repository);
        await model.InitializeAsync(); var original = model.SelectedWorkspace!;
        model.WorkspaceName = "研究"; await model.SaveAsCommand.ExecuteAsync(null); var research = model.SelectedWorkspace!;
        Assert.NotEqual(original.Id, research.Id);
        model.Panes[0].PinCommand.Execute(null); model.SetLayoutCommand.Execute("4");
        model.SelectedWorkspace = original; await model.SwitchWorkspaceCommand.ExecuteAsync(null);
        Assert.Equal(2, model.PaneCount); Assert.Null(model.Panes[0].PinnedRoot);
        model.SelectedWorkspace = research; await model.SwitchWorkspaceCommand.ExecuteAsync(null);
        Assert.Equal(4, model.PaneCount); Assert.NotNull(model.Panes[0].PinnedRoot);
        model.WorkspaceName = "科研"; await model.RenameCommand.ExecuteAsync(null);
        Assert.Equal("科研", model.SelectedWorkspace!.Name);
        await model.DeleteCurrentAsync(); Assert.Single(model.Workspaces); Assert.Equal(original.Id, model.SelectedWorkspace!.Id);
    }

    [AvaloniaFact] public async Task CorruptStateOpensTemporarySessionWithoutReplacingDatabase()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var db = Path.Combine(temp.Path, "state.db"); File.WriteAllText(db, "broken");
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new FakePlatform(), new SqliteWorkspaceRepository(db));
        await model.InitializeAsync(); Assert.False(model.CanManage); Assert.Contains("临时会话", model.Message);
        await model.SaveSafelyAsync(); Assert.Equal("broken", File.ReadAllText(db));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private sealed class FakePlatform : IPlatformActions
    {
        public void Open(Location location) { }
        public void Reveal(Location location) { }
        public void OpenTerminal(Location directory) { }
    }
}


