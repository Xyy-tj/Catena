using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Catena.App;
using Catena.Contracts;
using Catena.Core;
using Catena.Persistence;
using Catena.Storage.Local;
using Location = Catena.Contracts.Location;

namespace Catena.Tests;
// Temporary documentation renderer; all entries and conversation text are synthetic.
public sealed class ReadmeExport
{
    [AvaloniaFact] public async Task Export()
    {
        var output = Environment.GetEnvironmentVariable("CATENA_README_IMAGES") ?? throw new InvalidOperationException("Set CATENA_README_IMAGES to the documentation image directory.");
        Directory.CreateDirectory(output);
        using var temp = new TestDirectory();
        var provider = new DemoProvider();
        var repo = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "demo.db"));
        await repo.InitializeAsync();
        var state = Workspace.Create(provider.Normalize(@"C:\Demo"));
        state.Name = "科研工作区"; state.PaneCount = 4; state.PreviewWidth = 380;
        for (var i = 0; i < 4; i++) state.Panes[i] = state.Panes[i] with { Location = provider.Normalize(provider.Paths[i]), PinnedRoot = provider.Normalize(provider.Paths[i]) };
        await repo.SaveAsync(state);
        var store = new JsonAppSettingsStore(Path.Combine(temp.Path, "settings.json"));
        await store.SaveAsync(new() { AiModel = "demo-model", Theme = 1, LiveSearch = false });
        using var settings = new SettingsViewModel(store, new Protector(), new Planner(), new Index());
        using var model = new MainViewModel(provider, new LocalSearchProvider(new LocalFileSystemProvider()), new Platform(), repo, settings: settings, previews: new Preview());
        var window = new MainWindow { DataContext = model, Width = 1628, Height = 1000 };
        window.Show();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (model.IsBusy || model.Panes.Any(p => p.IsLoading)) await Task.Delay(20, timeout.Token);
        await Task.Delay(200, timeout.Token); window.UpdateLayout();
        model.Activate(model.Panes[1]);
        var paneView = window.GetVisualDescendants().OfType<PaneView>().Single(p => p.DataContext == model.Panes[1]);
        paneView.FindControl<ListBox>("FileList")!.SelectedItem = model.Panes[1].Entries.Single(e => e.Name == "README.md");
        while (!model.Preview.HasText) await Task.Delay(20, timeout.Token);
        await Task.Delay(700, timeout.Token);
        Save(window, "workspace-light.png");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        await Task.Delay(200, timeout.Token);
        Save(window, "workspace-dark.png");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var search = model.GetSearch(model.Panes[0]);
        search.Query = "研究";
        search.Results = new[] {
            provider.Entry(@"C:\Demo\研究资料\研究进展汇报.pptx", 18400000),
            provider.Entry(@"C:\Demo\研究资料\研究计划.pdf", 2300000),
            provider.Entry(@"C:\Demo\论文写作\研究方法.md", 18400),
            provider.Entry(@"C:\Demo\实验数据\研究数据汇总.xlsx", 420000),
            provider.Entry(@"C:\Demo\归档\研究笔记_2025.docx", 84000),
            provider.Entry(@"C:\Demo\参考文献\研究综述.pdf", 5700000)
        };
        search.Status = "6 项 · 演示数据"; search.SelectedResult = search.Results[0];
        var searchWindow = new SearchWindow { DataContext = search, Width = 820, Height = 480 };
        searchWindow.Show(window); await Task.Delay(500, timeout.Token); Save(searchWindow, "search.png"); searchWindow.Close();
        using var chat = new ChatViewModel(null, settings, @"C:\Demo\研究资料", "");
        chat.Messages.Add(new("user", "这个文件夹里有论文、实验数据和汇报材料，怎样整理更容易找到？"));
        chat.Messages.Add(new("assistant", "## 按工作阶段分组\n\n保留原始数据，先为新增文件建立固定位置。\n\n```text\n研究资料/\n  01_参考文献/\n  02_实验数据/\n  03_论文写作/\n  04_汇报材料/\n  归档/\n```\n\n文件名可以用 **日期 + 主题 + 版本**，例如 `2026-09-28_研究进展_v2.pptx`。\n\n每个项目保留一份 README，记录资料来源和目录用途。"));
        chat.Draft = "再给我一个适合多人协作的命名建议";
        var chatWindow = new ChatWindow { DataContext = chat, Width = 820, Height = 740 };
        chatWindow.Show(window); await Task.Delay(250, timeout.Token); Save(chatWindow, "ai-chat.png"); chatWindow.Close();
        window.Close(); while (window.IsVisible) await Task.Delay(20, timeout.Token);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        void Save(Window target, string name) { target.UpdateLayout(); using var bitmap = new RenderTargetBitmap(new PixelSize((int)target.Bounds.Width, (int)target.Bounds.Height)); bitmap.Render(target); bitmap.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default); }
    }
    private sealed class DemoProvider : ILocationProvider
    {
        private readonly LocalFileSystemProvider paths = new();
        public string[] Paths = [@"C:\Demo\研究资料", @"D:\Projects\Catena", @"C:\Demo\下载", @"C:\Demo\论文写作"];
        private readonly string[][] names = [
            ["参考文献/", "实验数据/", "研究进展汇报.pptx", "研究计划.pdf", "会议记录.docx", "预算清单.xlsx"],
            ["docs/", "src/", "tests/", "Catena.slnx", "LICENSE", "README.md"],
            ["已归档/", "实验数据_2026.zip", "会议日程.pdf", "论文修改意见.pdf", "figures.zip", "数据说明.xlsx"],
            ["experiments/", "figures/", "manuscript/", "references.bib", "results.csv", "投稿清单.md"] ];
        public string Id => paths.Id;
        public Location Normalize(string path) => paths.Normalize(path);
        public string GetPath(Location location) => paths.GetPath(location);
        public Location? GetParent(Location location) => paths.GetParent(location);
        public bool AreEqual(Location left, Location right) => paths.AreEqual(left, right);
        public FileEntry Entry(string path, long size) => new(Normalize(path.TrimEnd('/')), Path.GetFileName(path.TrimEnd('/')), path.EndsWith('/') ? EntryKind.Directory : EntryKind.File, path.EndsWith('/') ? null : size, new DateTimeOffset(2026, 9, 28, 14, 20, 0, TimeSpan.FromHours(8)), EntryCapabilities.Open | EntryCapabilities.Reveal | EntryCapabilities.Browse);
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(Location directory, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); var i = Array.FindIndex(Paths, p => AreEqual(Normalize(p), directory)); if (i >= 0) yield return names[i].Select((n, j) => Entry(Path.Combine(Paths[i], n), n == "README.md" ? 2600 : (j + 1) * 83421)).ToArray(); }
        public Task<FileEntry> GetEntryAsync(Location location, CancellationToken cancellationToken = default) => Task.FromResult(Entry(GetPath(location) + "/", 0));
    }
    private sealed class Preview : IFilePreviewProvider
    { public Task<FilePreview> ReadAsync(FileEntry entry, CancellationToken token = default) => Task.FromResult(new FilePreview(PreviewKind.Text, "Catena\n\n多窗格文件工作台\n\n把常用目录留在手边，\n快速找到正在处理的文件。\n\n项目结构\n\nsrc/      应用与核心模块\ntests/    自动化测试\ndocs/     文档与使用说明\n\n开发环境\n\nC# · .NET 10 · Avalonia\nSQLite · Everything IPC\n\nMIT License")); }
    private sealed class Platform : IPlatformActions { public void Open(Location l) {} public void Reveal(Location l) {} public void OpenTerminal(Location l) {} }
    private sealed class Protector : ISecretProtector { public string Protect(string v) => v; public string Unprotect(string v) => v; }
    private sealed class Planner : IAiSearchPlanner { public Task<FileSearchPlan> PlanAsync(string d, AiConnection c, CancellationToken t = default) => throw new NotSupportedException(); }
    private sealed class Index : IIndexedSearchProvider { public Task<IndexedSearchResponse> SearchAsync(IndexedSearchRequest r, string e, string i, CancellationToken t = default) => throw new NotSupportedException(); }
}
