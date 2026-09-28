using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Catena.App;
using Catena.Contracts;
using Catena.Core;
using Catena.Persistence;
using Catena.Storage.Local;
using Location = Catena.Contracts.Location;

namespace Catena.Tests;

public sealed class LocationCatalogTests
{
    [Fact] public async Task OptionalRealLocationMetadataScan()
    {
        var root = Environment.GetEnvironmentVariable("CATENA_TEST_CATALOG_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(LocalStructureScanner.CanTraverse(new DirectoryInfo(root)));
        var snapshot = await new LocalStructureScanner().ScanAsync(root, new(MaxSeconds: 45), token: TestContext.Current.CancellationToken);
        Assert.NotEmpty(snapshot.Items);
        using var temp = new TestDirectory(); var store = new SqliteLocationCatalog(Path.Combine(temp.Path, "navigation.db"));
        await store.InitializeAsync([root], TestContext.Current.CancellationToken);
        await store.SaveSnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        Assert.NotNull(await store.GetFoldersAsync(root, TestContext.Current.CancellationToken));
        if (Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS") is { } output)
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "location-scan-check.json"), System.Text.Json.JsonSerializer.Serialize(new
            { snapshot.Profile.Folders, snapshot.Profile.Files, snapshot.Profile.Bytes, snapshot.Profile.Skipped, snapshot.Profile.Partial, Milliseconds = timer.ElapsedMilliseconds }), TestContext.Current.CancellationToken);
        }
    }

    [AvaloniaFact] public async Task OfflineTreeUsesSavedStructureAndRefreshPreservesIt()
    {
        using var temp = new TestDirectory(); var root = Directory.CreateDirectory(Path.Combine(temp.Path, "cached")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        var store = new SqliteLocationCatalog(Path.Combine(temp.Path, "navigation.db"));
        using var library = new LocationLibraryViewModel(store, new LocalStructureScanner()); await library.InitializeAsync([root], false);
        await library.RefreshCommand.ExecuteAsync(library.Roots[0]); Directory.Delete(child); Directory.Delete(root);
        using var node = new FolderNodeViewModel("cached", root, library, TestContext.Current.CancellationToken);
        await node.LoadAsync(true); Assert.Equal(child, Assert.Single(node.Children).Path); Assert.Contains("离线", node.Status);
        await library.RefreshCommand.ExecuteAsync(library.Roots[0]); Assert.Contains("保留缓存", library.Roots[0].Status);
        Assert.Equal(1, library.Roots[0].Profile!.Folders);
    }

    [Fact] public async Task ScannerProfilesMetadataWithoutOpeningLockedFilesAndHonorsLimits()
    {
        using var temp = new TestDirectory(); var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "资料 #100% 中文")).FullName;
        var file = Path.Combine(folder, "预算.TXT"); File.WriteAllText(file, "private content");
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var scanner = new LocalStructureScanner(); var result = await scanner.ScanAsync(temp.Path, token: TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Profile.Files); Assert.Equal(1, result.Profile.Folders); Assert.Equal(locked.Length, result.Profile.Bytes);
        Assert.Equal(1, result.Profile.Types[".txt"]); Assert.Equal(1, result.Profile.Branches["资料 #100% 中文"]); Assert.False(result.Profile.Partial);
        var bounded = await scanner.ScanAsync(temp.Path, new(MaxEntries: 1), token: TestContext.Current.CancellationToken);
        Assert.True(bounded.Profile.Partial); Assert.Single(bounded.Items);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(temp.Path, token: cancelled.Token));
    }

    [Fact] public async Task ScannerSkipsDirectoryAliasesAndRecognizesCloudTags()
    {
        Assert.True(LocalStructureScanner.IsCloudTag(0x9000001A)); Assert.True(LocalStructureScanner.IsCloudTag(0x9000F01A));
        Assert.False(LocalStructureScanner.IsCloudTag(0xA0000003)); Assert.False(LocalStructureScanner.IsCloudTag(0xA000000C));
        using var temp = new TestDirectory(); var link = Path.Combine(temp.Path, "loop");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, temp.Path }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(TestContext.Current.CancellationToken); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, temp.Path);
        try
        {
            var snapshot = await new LocalStructureScanner().ScanAsync(temp.Path, token: TestContext.Current.CancellationToken);
            Assert.False(Assert.Single(snapshot.Items).CanExpand); Assert.Equal(1, snapshot.Profile.Skipped);
        }
        finally { Directory.Delete(link); }
    }

    [Fact] public async Task SnapshotsSurviveOfflineAndPartialUpdatesWhileCompleteRefreshRemovesDeletedFolders()
    {
        using var temp = new TestDirectory(); var root = Directory.CreateDirectory(Path.Combine(temp.Path, "OneDrive")).FullName;
        var first = Directory.CreateDirectory(Path.Combine(root, "旧资料")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "新资料")).FullName;
        var store = new SqliteLocationCatalog(Path.Combine(temp.Path, "navigation.db")); await store.InitializeAsync([root], TestContext.Current.CancellationToken);
        var scanner = new LocalStructureScanner(); await store.SaveSnapshotAsync(await scanner.ScanAsync(root, token: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(2, (await store.GetFoldersAsync(root, TestContext.Current.CancellationToken))!.Items.Count);
        await store.SaveSnapshotAsync(await scanner.ScanAsync(root, new(MaxEntries: 1), token: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(2, (await store.GetFoldersAsync(root, TestContext.Current.CancellationToken))!.Items.Count); Assert.True((await store.GetFoldersAsync(root, TestContext.Current.CancellationToken))!.Truncated);
        Directory.Delete(first); await store.SaveSnapshotAsync(await scanner.ScanAsync(root, token: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.Equal(second, Assert.Single((await store.GetFoldersAsync(root, TestContext.Current.CancellationToken))!.Items).Path);
        Directory.Delete(second); Directory.Delete(root);
        var reopened = new SqliteLocationCatalog(Path.Combine(temp.Path, "navigation.db")); await reopened.InitializeAsync([], TestContext.Current.CancellationToken);
        Assert.Equal(second, Assert.Single((await reopened.GetFoldersAsync(root, TestContext.Current.CancellationToken))!.Items).Path);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => scanner.ScanAsync(root, token: TestContext.Current.CancellationToken));
        Assert.NotNull((await reopened.GetRootsAsync(TestContext.Current.CancellationToken))[0].Profile);
        var previousProfile = (await reopened.GetRootsAsync(TestContext.Current.CancellationToken))[0].Profile!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopened.SaveSnapshotAsync(new(root, [], previousProfile), cancellation.Token));
    }

    [Fact] public async Task RecentRecordsAreBoundedAndRemovingADefaultRootPersists()
    {
        using var temp = new TestDirectory(); var store = new SqliteLocationCatalog(Path.Combine(temp.Path, "navigation.db"));
        await store.InitializeAsync([temp.Path], TestContext.Current.CancellationToken);
        var path = Path.Combine(temp.Path, "file.txt");
        await store.RecordVisitAsync(path, true, TestContext.Current.CancellationToken); await store.RecordVisitAsync(path.ToUpperInvariant(), true, TestContext.Current.CancellationToken);
        var row = Assert.Single(await store.GetRecentAsync(TestContext.Current.CancellationToken)); Assert.Equal(2, row.Visits); Assert.True(row.IsFile);
        for (var i = 0; i < 35; i++) await store.RecordVisitAsync(Path.Combine(temp.Path, i.ToString()), false, TestContext.Current.CancellationToken);
        Assert.Equal(30, (await store.GetRecentAsync(TestContext.Current.CancellationToken)).Count);
        await store.ClearRecentAsync(TestContext.Current.CancellationToken); Assert.Empty(await store.GetRecentAsync(TestContext.Current.CancellationToken));
        await store.RemoveRootAsync(temp.Path, TestContext.Current.CancellationToken);
        var reopened = new SqliteLocationCatalog(Path.Combine(temp.Path, "navigation.db")); await reopened.InitializeAsync([temp.Path], TestContext.Current.CancellationToken);
        Assert.Empty(await reopened.GetRootsAsync(TestContext.Current.CancellationToken));
        var scan = await new LocalStructureScanner().ScanAsync(temp.Path, token: TestContext.Current.CancellationToken); await reopened.SaveSnapshotAsync(scan, TestContext.Current.CancellationToken);
        Assert.Empty(await reopened.GetRootsAsync(TestContext.Current.CancellationToken)); // A late scan cannot re-add a removed root.
    }

    [Fact] public async Task OnlineOnlyPreviewDoesNotReadFileContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TestDirectory(); var path = Path.Combine(temp.Path, "cloud.txt"); File.WriteAllText(path, "must stay local");
        var attributes = File.GetAttributes(path); File.SetAttributes(path, attributes | FileAttributes.Offline);
        try
        {
            var provider = new LocalFileSystemProvider(); var entry = await provider.GetEntryAsync(provider.Normalize(path), TestContext.Current.CancellationToken);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var result = await new LocalFilePreviewProvider().ReadAsync(entry, TestContext.Current.CancellationToken);
            Assert.Equal(PreviewKind.Information, result.Kind); Assert.Empty(result.Text); Assert.Contains("云端", result.Notice);
        }
        finally { File.SetAttributes(path, attributes); }
    }

    [AvaloniaFact] public async Task PaneTreesNavigateIndependentlyAndRecentRecordsSurviveRestart()
    {
        using var temp = new TestDirectory(); var root = Directory.CreateDirectory(Path.Combine(temp.Path, "OneDrive")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(root, "项目资料")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "照片")); Directory.CreateDirectory(Path.Combine(root, "归档"));
        var file = Path.Combine(project, "计划.md"); File.WriteAllText(file, "# 本周计划\n资料整理与目录浏览");
        var provider = new LocalFileSystemProvider(); var scanner = new LocalStructureScanner();
        var store = new SqliteLocationCatalog(Path.Combine(temp.Path, "navigation.db"));
        using var library = new LocationLibraryViewModel(store, scanner); await library.InitializeAsync([root], false);
        await library.RefreshCommand.ExecuteAsync(library.Roots[0]); Assert.NotNull(library.Roots[0].Profile);
        var repo = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "state.db")); await repo.InitializeAsync();
        var state = Workspace.Create(provider.Normalize(root)); state.Panes[0] = state.Panes[0] with { TreeVisible = true };
        await repo.SaveAsync(state);
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new Platform(), repo, library: library);
        var window = new MainWindow { DataContext = model }; window.Show();
        await Until(() => !model.IsBusy && model.Panes.All(p => !p.IsLoading));
        Assert.Empty(library.Recent); // Startup and refresh are not user visits.
        var pane = model.Panes[0]; var browser = pane.Browser!;
        var oneDrive = browser.Nodes[0].Children[0]; oneDrive.IsExpanded = true; await oneDrive.LoadAsync();
        await Until(() => oneDrive.Children.Any(n => n.Name == "项目资料"));
        var tree = window.GetVisualDescendants().OfType<PaneView>().First(v => v.DataContext == pane).FindControl<TreeView>("FolderTree")!;
        tree.SelectedItem = oneDrive.Children.Single(n => n.Name == "项目资料");
        await Until(() => pane.Address == project && !pane.IsLoading); await library.FlushAsync();
        Assert.Equal(root, model.Panes[1].Address); Assert.Contains(library.Recent, r => r.Path == project);
        var visits = library.Recent.Single(r => r.Path == project).Visits;
        await pane.RefreshCommand.ExecuteAsync(null); await library.FlushAsync(); Assert.Equal(visits, library.Recent.Single(r => r.Path == project).Visits);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        pane.SelectedEntry = pane.Entries.Single(e => e.Name == "计划.md"); await pane.OpenCommand.ExecuteAsync(null); await library.FlushAsync();
        Assert.Contains(library.Recent, r => r.Path == file && r.IsFile);
        await Until(() => model.Preview.HasText); browser.Nodes[1].IsExpanded = true;
        Capture(window, "catena-folder-tree.png");
        library.SelectedRoot = library.Roots[0]; var locations = new LocationsWindow { DataContext = library }; locations.Show();
        locations.UpdateLayout(); Capture(locations, "catena-location-profile.png"); locations.Close();
        Assert.True(await model.SaveSafelyAsync()); window.Close(); await Until(() => !window.IsVisible);
        var freshStore = new SqliteLocationCatalog(Path.Combine(temp.Path, "navigation.db")); await freshStore.InitializeAsync([], TestContext.Current.CancellationToken);
        Assert.Contains(await freshStore.GetRecentAsync(TestContext.Current.CancellationToken), r => r.Path == file && r.IsFile);
        Assert.True((await repo.LoadAsync())!.Panes[0].TreeVisible);
    }
    private static async Task Until(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var path = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS"); if (path is null) return;
        Directory.CreateDirectory(path); using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window); bitmap.Save(Path.Combine(path, name), PngBitmapEncoderOptions.Default);
    }
    private sealed class Platform : IPlatformActions
    { public void Open(Location location) { } public void Reveal(Location location) { } public void OpenTerminal(Location directory) { } }
}
