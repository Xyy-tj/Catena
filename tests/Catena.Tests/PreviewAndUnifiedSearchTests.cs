using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Catena.App;
using Catena.Contracts;
using Catena.Core;
using Catena.Persistence;
using Catena.Platform.Windows;
using Catena.Storage.Local;
using Location = Catena.Contracts.Location;

namespace Catena.Tests;

public sealed class PreviewAndUnifiedSearchTests
{
    private static readonly LocalFileSystemProvider Files = new();
    private static Task<FileEntry> Entry(string path) => Files.GetEntryAsync(Files.Normalize(path));

    [Fact] public async Task TextPreviewIsBoundedAndReadOnlyAndUnderstandsUnicodeBom()
    {
        using var temp = new TestDirectory(); var preview = new LocalFilePreviewProvider();
        var path = Path.Combine(temp.Path, "中文.txt"); var text = "中文预览\n" + new string('x', LocalFilePreviewProvider.TextLimit);
        File.WriteAllText(path, text, Encoding.Unicode);
        var result = await preview.ReadAsync(await Entry(path), TestContext.Current.CancellationToken);
        Assert.Equal(PreviewKind.Text, result.Kind); Assert.StartsWith("中文预览", result.Text);
        Assert.Equal(LocalFilePreviewProvider.TextLimit, result.Text.Length); Assert.Contains("64K", result.Notice);
        Assert.Equal(text, File.ReadAllText(path, Encoding.Unicode));
    }
    [Fact] public async Task WordPreviewExtractsParagraphsAndRejectsExternalEntities()
    {
        using var temp = new TestDirectory(); var preview = new LocalFilePreviewProvider();
        var path = Path.Combine(temp.Path, "说明.DOCX");
        WriteDocx(path, "<w:document xmlns:w='urn:test'><w:p><w:r><w:t>项目说明</w:t></w:r></w:p><w:p><w:r><w:t>第二段</w:t></w:r></w:p></w:document>");
        var result = await preview.ReadAsync(await Entry(path), TestContext.Current.CancellationToken);
        Assert.Equal(PreviewKind.Text, result.Kind); Assert.Contains("项目说明" + Environment.NewLine + "第二段", result.Text);
        File.Delete(path); WriteDocx(path, "<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///C:/private'>]><x>&secret;</x>");
        result = await preview.ReadAsync(await Entry(path), TestContext.Current.CancellationToken);
        Assert.Equal(PreviewKind.Information, result.Kind); Assert.Empty(result.Text);
    }
    [Fact] public async Task UnsupportedBinaryAndMissingFilesHaveUsefulPreviewStates()
    {
        using var temp = new TestDirectory(); var preview = new LocalFilePreviewProvider();
        var path = Path.Combine(temp.Path, "file.pdf"); File.WriteAllText(path, "%PDF");
        Assert.Equal(PreviewKind.Information, (await preview.ReadAsync(await Entry(path), TestContext.Current.CancellationToken)).Kind);
        var text = Path.Combine(temp.Path, "file.txt"); File.WriteAllText(text, "a\0b");
        Assert.Contains("二进制", (await preview.ReadAsync(await Entry(text), TestContext.Current.CancellationToken)).Notice);
        var missing = await Entry(text); File.Delete(text);
        Assert.Contains("无法读取", (await preview.ReadAsync(missing, TestContext.Current.CancellationToken)).Notice);
    }
    [Fact] public async Task WindowsShellReturnsVisibleFolderAndDocumentIcons()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = await Task.Run(() => WindowsFileIcons.ReadTypeIcon("", true), TestContext.Current.CancellationToken);
        var file = await Task.Run(() => WindowsFileIcons.ReadTypeIcon(".txt", false), TestContext.Current.CancellationToken);
        Assert.NotNull(folder); Assert.NotNull(file); Assert.Equal(4096, folder.Length);
        Assert.Contains(Enumerable.Range(0, 1024), i => folder[i * 4 + 3] != 0);
        Assert.False(folder.SequenceEqual(file));
    }
    [AvaloniaFact] public async Task OlderPreviewCannotReplaceNewSelection()
    {
        var provider = new DelayedPreview(); using var model = new PreviewViewModel(provider, new Platform());
        var oldEntry = new FileEntry(new("local", "file:///C:/old.txt"), "old.txt", EntryKind.File, 0, null, EntryCapabilities.Open);
        var nextEntry = oldEntry with { Name = "new.txt", Location = new("local", "file:///C:/new.txt") };
        var old = model.ShowAsync(oldEntry); await provider.Started.Task;
        await model.ShowAsync(nextEntry); provider.Release.SetResult(); await old;
        Assert.Equal("new.txt", model.Text); Assert.Equal(nextEntry, model.Entry);
        await model.ShowAsync(null); Assert.Null(model.Entry); Assert.Empty(model.Text);
    }
    [AvaloniaFact] public async Task UnifiedSearchRoutesPathsAndKeepsGlobalFailureExplicit()
    {
        using var temp = new TestDirectory(); var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "资料")).FullName;
        var file = Path.Combine(folder, "预算 2026.txt"); File.WriteAllText(file, "预览正文");
        var repo = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "state.db"));
        await repo.InitializeAsync(); await repo.SaveAsync(Workspace.Create(Files.Normalize(temp.Path)));
        using var model = new MainViewModel(Files, new LocalSearchProvider(Files), new Platform(), repo);
        await model.InitializeAsync(); var search = model.GetSearch(model.Panes[1]); search.Query = file; await search.SearchCommand.ExecuteAsync(null);
        Assert.Equal(model.Panes[1], model.ActivePane); Assert.Equal("预算 2026.txt", model.Panes[1].SelectedEntry?.Name);
        search.ScopeIndex = 0; search.Query = "预算 2026"; await search.SearchCommand.ExecuteAsync(null);
        Assert.Empty(search.Results); Assert.True(search.CanSearchCurrentFolder);
        await search.SearchCurrentFolderCommand.ExecuteAsync(null);
        Assert.Equal(1, search.ScopeIndex); Assert.Single(search.Results); Assert.Contains("目录查找", search.Status);
    }
    [AvaloniaFact] public async Task ExplorerLayoutShowsIconsAndTextAndImagePreviews()
    {
        using var temp = new TestDirectory(); Directory.CreateDirectory(Path.Combine(temp.Path, "设计资料"));
        var note = Path.Combine(temp.Path, "项目说明.md"); File.WriteAllText(note, "# Catena 工作区\n\n在熟悉的文件列表中浏览资料。\n\n• 统一查找入口\n• 图片与文字预览\n• 四个独立窗格");
        var png = Path.Combine(temp.Path, "图像.png");
        using (var bitmap = new WriteableBitmap(new PixelSize(48, 24), new Vector(96, 96)))
        {
            using (var pixels = bitmap.Lock())
            {
                var bytes = Enumerable.Range(0, pixels.RowBytes * pixels.Size.Height).Select(i => (byte)(i % 4 == 3 ? 255 : i % 4 == 0 ? 230 : 100)).ToArray();
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, pixels.Address, bytes.Length);
            }
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
        }
        var repo = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "state.db")); await repo.InitializeAsync();
        var workspace = Workspace.Create(Files.Normalize(temp.Path)); workspace.PaneCount = 4; await repo.SaveAsync(workspace);
        using var model = new MainViewModel(Files, new LocalSearchProvider(Files), new Platform(), repo);
        var window = new MainWindow { DataContext = model, RequestedThemeVariant = ThemeVariant.Light }; window.Show();
        await WaitUntil(() => !model.IsBusy && model.Panes.All(p => !p.IsLoading));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        model.ActivePane!.SelectedEntry = model.ActivePane.Entries.Single(e => e.Name == "项目说明.md");
        await WaitUntil(() => !model.Preview.IsLoading);
        Assert.True(model.Preview.HasText, $"Preview entry={model.Preview.Entry?.Name}; notice={model.Preview.Notice}");
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.NotEmpty(window.GetVisualDescendants().OfType<FileIconView>());
        Assert.Equal(2, window.FindControl<ComboBox>("GlobalSearchScope")!.SelectedIndex);
        var searchDialog = window.OpenSearch(model.ActivePane!); Assert.NotNull(searchDialog.FindControl<ListBox>("SearchResultList")); searchDialog.Close();
        var preview = Assert.Single(window.GetVisualDescendants().OfType<PreviewView>()); Assert.True(preview.IsVisible);
        SaveScreenshot(window, "catena-explorer-light.png");
        window.Width = 1000; window.Height = 700;
        await WaitUntil(() => window.Bounds.Width == 1000 && window.Bounds.Height == 700);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); window.UpdateLayout();
        Assert.True(window.FindControl<Border>("GlobalSearchEntry")!.Bounds.Width > 100);
        SaveScreenshot(window, "catena-explorer-compact.png");
        model.ActivePane.SelectedEntry = model.ActivePane.Entries.Single(e => e.Name == "图像.png");
        await WaitUntil(() => model.Preview.HasImage);
        Assert.InRange(model.Preview.Image!.PixelSize.Width, 1, 1024);
        window.Width = 1380; window.Height = 900; window.RequestedThemeVariant = ThemeVariant.Dark;
        await WaitUntil(() => window.Bounds.Width == 1380 && window.Bounds.Height == 900);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); window.UpdateLayout();
        SaveScreenshot(window, "catena-explorer-dark.png");
        model.PreviewVisible = false; window.UpdateLayout(); Assert.False(preview.IsVisible); Assert.Null(model.Preview.Image);
        window.Close(); await WaitUntil(() => !window.IsVisible);
    }
    private static void WriteDocx(string path, string xml)
    { using var archive = ZipFile.Open(path, ZipArchiveMode.Create); using var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()); writer.Write(xml); }
    [AvaloniaFact] public async Task NativeFailureFallsBackAndKeepsPageNavigationUntilSelectionChanges()
    {
        var provider = new FallbackPreview(); using var model = new PreviewViewModel(provider, new Platform());
        var entry = new FileEntry(new("local", "file:///C:/slides.pptx"), "slides.pptx", EntryKind.File, 1, null, EntryCapabilities.Open);
        await model.ShowAsync(entry); Assert.True(model.HasNative);
        await model.ShowFallbackAsync("failed"); Assert.False(model.HasNative); Assert.Equal("page 0", model.Text);
        await model.NextPageCommand.ExecuteAsync(null); Assert.Equal("page 1", model.Text); Assert.Equal(1, provider.NativeCalls);
        await model.ShowAsync(entry with { Location = new("local", "file:///C:/next.pptx") });
        Assert.True(model.HasNative); Assert.Equal(2, provider.NativeCalls);
    }
    [Fact] public async Task WindowsProviderPreservesFallbackPageIndex()
    {
        var fallback = new FallbackPreview();
        var provider = new WindowsDocumentPreviewProvider(fallback);
        var entry = new FileEntry(new("local", "file:///C:/slides.pptx"), "slides.pptx", EntryKind.File, 1, null, EntryCapabilities.Open);
        var page = await provider.ReadFallbackPageAsync(entry, 1, TestContext.Current.CancellationToken);
        Assert.Equal(1, page.PageIndex);
    }
    [AvaloniaFact] public async Task RepeatedSelectionDoesNotReloadButModifiedMetadataDoes()
    {
        var provider = new FallbackPreview(); using var model = new PreviewViewModel(provider, new Platform());
        var entry = new FileEntry(new("local", "file:///C:/slides.pptx"), "slides.pptx", EntryKind.File, 1, DateTimeOffset.UtcNow, EntryCapabilities.Open);
        var first = model.ShowAsync(entry);
        await model.ShowAsync(entry with { }); await first;
        for (var i = 0; i < 10; i++) await model.ShowAsync(entry with { });
        Assert.Equal(1, provider.NativeCalls);
        await model.ShowAsync(entry with { Size = 2 }); Assert.Equal(2, provider.NativeCalls);
        await model.ShowAsync(null); await model.ShowAsync(entry); Assert.Equal(3, provider.NativeCalls);
    }
    private sealed class FallbackPreview : IFallbackPreviewProvider
    {
        public int NativeCalls { get; private set; }
        public Task<FilePreview> ReadAsync(FileEntry entry, CancellationToken token = default) => ReadPageAsync(entry, 0, token);
        public Task<FilePreview> ReadPageAsync(FileEntry entry, int page, CancellationToken token = default)
        { NativeCalls++; return Task.FromResult(new FilePreview(PreviewKind.Native, NativePath: entry.DisplayPath, PageIndex: page)); }
        public Task<FilePreview> ReadFallbackPageAsync(FileEntry entry, int page, CancellationToken token = default)
            => Task.FromResult(new FilePreview(PreviewKind.Text, "page " + page, PageCount: 2, PageIndex: page));
    }
    private static async Task WaitUntil(Func<bool> ready)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!ready()) await Task.Delay(10, timeout.Token); }
    private static void SaveScreenshot(Window window, string name)
    {
        window.UpdateLayout(); var folder = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS"); if (folder is null) return;
        Directory.CreateDirectory(folder); using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window); bitmap.Save(Path.Combine(folder, name), PngBitmapEncoderOptions.Default);
    }
    private sealed class DelayedPreview : IFilePreviewProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FilePreview> ReadAsync(FileEntry entry, CancellationToken token = default)
        { if (entry.Name == "old.txt") { Started.SetResult(); await Release.Task; } return new(PreviewKind.Text, entry.Name); }
    }
    private sealed class Platform : IPlatformActions
    { public void Open(Location location) { } public void Reveal(Location location) { } public void OpenTerminal(Location location) { } }
}

