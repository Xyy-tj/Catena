using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Catena.AI;
using Catena.App;
using Catena.Contracts;
using Catena.Core;
using Catena.Persistence;
using Catena.Storage.Local;

using Location = Catena.Contracts.Location;
namespace Catena.Tests;

public sealed class ChatTests
{
    private static AiConnection Connection => new() { BaseUrl = "https://example.test/v1", Model = "fixture", ApiKey = "secret-not-in-errors" };
    private static string Event(string text, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text }, finish_reason = finish } } }) + "\r\n\r\n";
    [Fact] public async Task StreamHandlesFragmentedUnicodeAndAttachmentsWithoutExposingKeys()
    {
        var handler = new Handler(": keepalive\n\n" + Event("中文 📁") + Event(" 建议", "stop") + "data: [DONE]\n\n");
        using var http = new HttpClient(handler); var client = new OpenAiChatClient(http);
        var chunks = new List<AiChatChunk>();
        await foreach (var chunk in client.StreamAsync([
            new("user", "问题", [new("图.png", "image", DataUrl: "data:image/png;base64,AA=="), new("说明.txt", "text", "附件正文")]),
            new("assistant", "上轮答复"), new("user", "继续")], "仅目录元数据", Connection, TestContext.Current.CancellationToken)) chunks.Add(chunk);
        Assert.Equal("中文 📁 建议", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal("https://example.test/v1/chat/completions", handler.Url);
        using var request = JsonDocument.Parse(handler.Body!); Assert.True(request.RootElement.GetProperty("stream").GetBoolean());
        var messages = request.RootElement.GetProperty("messages");
        Assert.Equal("image_url", messages[2].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, messages[3].GetProperty("content").ValueKind);
        Assert.DoesNotContain(Connection.ApiKey, handler.Body!);
    }
    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n", "中断")]
    [InlineData("data: []\n\n", "无法识别")]
    [InlineData("data: {\"error\":\"secret-not-in-errors\"}\n\n", "模型返回错误")]
    public async Task BrokenStreamsPreserveSafeErrors(string response, string expected)
    {
        using var http = new HttpClient(new Handler(response)); var client = new OpenAiChatClient(http);
        var error = await Assert.ThrowsAsync<SearchAssistanceException>(async () =>
        { await foreach (var _ in client.StreamAsync([new("user", "test")], "", Connection, TestContext.Current.CancellationToken)) { } });
        Assert.Contains(expected, error.Message); Assert.DoesNotContain(Connection.ApiKey, error.Message);
    }
    [Fact] public async Task JsonFallbackAndOutputBoundsAreEnforced()
    {
        foreach (var size in new[] { 20, 100001 })
        {
            var json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = new string('a', size) }, finish_reason = "stop" } } });
            using var http = new HttpClient(new Handler(json, "application/json", 2048)); var client = new OpenAiChatClient(http);
            async Task Read() { await foreach (var _ in client.StreamAsync([new("user", "test")], "", Connection, TestContext.Current.CancellationToken)) { } }
            if (size > 100000) await Assert.ThrowsAsync<SearchAssistanceException>(Read); else await Read();
        }
    }
    [Fact] public async Task HttpFailuresDoNotCopyServerSecrets()
    {
        using var http = new HttpClient(new Handler("secret-not-in-errors") { Status = HttpStatusCode.Unauthorized });
        var error = await Assert.ThrowsAsync<SearchAssistanceException>(async () =>
        { await foreach (var _ in new OpenAiChatClient(http).StreamAsync([new("user", "test")], "", Connection, TestContext.Current.CancellationToken)) { } });
        Assert.Contains("密钥", error.Message); Assert.DoesNotContain(Connection.ApiKey, error.Message);
    }
    [Fact] public async Task AttachmentsExtractBoundedTextAndUseMetadataForDirectories()
    {
        using var temp = new TestDirectory(); var text = Path.Combine(temp.Path, "说明.txt"); File.WriteAllText(text, new string('中', 20000));
        var attachment = await ChatAttachmentLoader.FromFileAsync(text, TestContext.Current.CancellationToken);
        Assert.Equal("text", attachment.Kind); Assert.Equal(16000, attachment.Text.Length);
        Assert.Equal("metadata", (await ChatAttachmentLoader.FromFileAsync(temp.Path, TestContext.Current.CancellationToken)).Kind);
        var large = Path.Combine(temp.Path, "large.pdf"); using (var stream = File.Create(large)) stream.SetLength(ChatAttachmentLoader.FileLimit + 1);
        await Assert.ThrowsAsync<IOException>(() => ChatAttachmentLoader.FromFileAsync(large, TestContext.Current.CancellationToken));
    }
    [AvaloniaFact] public async Task ChatStreamsTypesStopsAndPastesFilesImagesAndText()
    {
        using var temp = new TestDirectory(); using var settings = Settings(); await settings.LoadAsync();
        var client = new StreamingClient(); using var model = new ChatViewModel(client, settings, @"C:\研究\博士学位论文", "fixture metadata");
        var window = new ChatWindow { DataContext = model }; window.Show();
        var file = Path.Combine(temp.Path, "目录说明.txt"); File.WriteAllText(file, "论文、文献、实验数据分开管理。");
        var storageFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(file)); Assert.NotNull(storageFile);
        await window.Clipboard!.SetFilesAsync([storageFile]); Paste(window);
        await Wait(() => model.Attachments.Count == 1 && !model.IsLoadingAttachments);
        Assert.Equal("text", model.Attachments[0].Content.Kind);
        using var bitmap = new WriteableBitmap(new PixelSize(40, 30), new Vector(96, 96));
        await window.Clipboard!.SetBitmapAsync(bitmap); Paste(window);
        await Wait(() => model.Attachments.Count == 2 && !model.IsLoadingAttachments);
        Assert.NotNull(model.Attachments[1].Thumbnail);
        model.RemoveAttachmentCommand.Execute(model.Attachments[1]); Assert.Single(model.Attachments);
        await window.Clipboard!.SetTextAsync("建议如何组织这个文件夹？"); Paste(window);
        await Wait(() => model.Draft.Length > 0); Assert.Equal(0, client.Calls);
        var sending = model.SendCommand.ExecuteAsync(null); await Wait(() => client.Calls == 1);
        Assert.Equal("fixture metadata", client.Context); Assert.Single(client.History![0].Attachments!);
        await client.Chunks.Writer.WriteAsync(new("## 建议的目录结构\n\n先按工作阶段整理，保留原始数据。\n\n```text\n博士学位论文/\n  01_参考文献/\n  02_实验数据/\n  03_论文写作/\n  04_答辩材料/\n```\n\n先从新增文件开始，避免一次性搬动所有资料。"), TestContext.Current.CancellationToken);
        await Wait(() => model.Messages[1].Text.Length > 2);
        Assert.True(model.IsSending); Assert.True(model.Messages[1].Text.Length < 145);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Screenshot(window, "catena-chat-streaming.png");
        model.StopCommand.Execute(null); await sending; Assert.Contains("已停止", model.Status); Assert.False(model.IsSending);
        Assert.Contains("01_参考文献", model.Messages[1].Text);
        client.Chunks = Channel.CreateUnbounded<AiChatChunk>(); model.IncludeContext = false; model.Draft = "继续";
        sending = model.SendCommand.ExecuteAsync(null); await Wait(() => client.Calls == 2); Assert.Equal("", client.Context);
        client.Chunks.Writer.TryWrite(new("好的")); client.Chunks.Writer.Complete(); await sending;
        Assert.Equal("好的", model.Messages[^1].Text);
        window.Close();
    }
    [AvaloniaFact] public async Task EachPaneHasIndependentSearchDialogAndWorkspaceSwitchClosesIt()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var repo = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "workspace.db")); await repo.InitializeAsync();
        var workspace = Workspace.Create(provider.Normalize(temp.Path)); workspace.PaneCount = 4;
        for (var i = 0; i < 4; i++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "研究资料" + i)).FullName;
            File.WriteAllText(Path.Combine(folder, "目录说明.md"), "sample");
            workspace.Panes[i] = workspace.Panes[i] with { Location = provider.Normalize(folder) };
        }
        await repo.SaveAsync(workspace);
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new Platform(), repo);
        var window = new MainWindow { DataContext = model }; window.Show(); await Wait(() => !model.IsBusy && model.Panes.All(p => !p.IsLoading));
        var first = window.OpenSearch(model.Panes[0]); var fourth = window.OpenSearch(model.Panes[3]);
        Assert.Same(fourth, window.OpenSearch(model.Panes[3]));
        var a = (PaneSearchViewModel)first.DataContext!; var b = (PaneSearchViewModel)fourth.DataContext!;
        a.Query = "没有"; b.Query = "说明"; await Task.WhenAll(a.SearchCurrentFolderCommand.ExecuteAsync(null), b.SearchCurrentFolderCommand.ExecuteAsync(null));
        Assert.Empty(a.Results); b.SelectedResult = Assert.Single(b.Results);
        Screenshot(fourth, "catena-pane-search.png");
        await b.LocateCommand.ExecuteAsync(null); Assert.False(fourth.IsVisible); Assert.True(first.IsVisible); Assert.Same(model.Panes[3], model.ActivePane);
        var old = model.SelectedWorkspace; model.WorkspaceName = "另一个工作区"; await model.SaveAsCommand.ExecuteAsync(null);
        model.SelectedWorkspace = old; await model.SwitchWorkspaceCommand.ExecuteAsync(null); await Wait(() => !model.IsBusy);
        Assert.False(first.IsVisible); window.Close(); await Wait(() => !window.IsVisible);
    }
    private static void Paste(Window window) { window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v"); window.KeyRelease(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v"); }
    [AvaloniaFact] public async Task PrototypeLayoutUsesThreeSegmentsAndVirtualizedFileRows()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var repo = new SqliteWorkspaceRepository(Path.Combine(temp.Path, "workspace.db")); await repo.InitializeAsync();
        var workspace = Workspace.Create(provider.Normalize(temp.Path)); workspace.Name = "科研工作区"; workspace.PaneCount = 4; workspace.PreviewWidth = 416; workspace.VerticalRatio = .52;
        var folders = new[] { "博士阶段考核", "项目代码", "下载", "论文与资料" };
        var names = new[] {
            new[] { "博士阶段考核汇报.pptx", "博士论文框架图.pptx", "研究成果汇总.pdf", "参考材料/", "答辩附件/" },
            new[] { "src/", "tests/", "docs/", "assets/", "README.md", "Catena.sln", "LICENSE", ".gitignore" },
            new[] { "实验数据_2026.zip", "论文修改意见.pdf", "会议日程.pdf", "figures.zip", "数据说明.xlsx", "已归档/" },
            new[] { "manuscript/", "figures/", "experiments/", "ProCARE.pdf", "references.bib", "results.csv", "README.md" } };
        for (var i = 0; i < 4; i++)
        {
            var path = Directory.CreateDirectory(Path.Combine(temp.Path, folders[i])).FullName;
            foreach (var name in names[i])
                if (name.EndsWith('/')) Directory.CreateDirectory(Path.Combine(path, name));
                else File.WriteAllText(Path.Combine(path, name), "Catena\n\n项目文档与研究资料\n\n文件按工作阶段分类。\n\n01 参考文献\n02 实验数据\n03 论文写作\n04 答辩材料");
            workspace.Panes[i] = workspace.Panes[i] with { Location = provider.Normalize(path), PinnedRoot = provider.Normalize(path) };
        }
        await repo.SaveAsync(workspace);
        using var model = new MainViewModel(provider, new LocalSearchProvider(provider), new Platform(), repo);
        var window = new MainWindow { DataContext = model, Width = 1628, Height = 984 }; window.Show();
        await Wait(() => !model.IsBusy && model.Panes.All(p => !p.IsLoading));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        model.Activate(model.Panes[1]); model.Panes[1].SelectedEntry = model.Panes[1].Entries.Single(e => e.Name == "README.md");
        await Wait(() => model.Preview.HasText); await Task.Delay(180, TestContext.Current.CancellationToken);
        window.UpdateLayout(); Screenshot(window, "catena-prototype-layout.png");
        var buttons = new[] { "FourPaneButton", "RowsPaneButton", "ColumnsPaneButton" }.Select(n => window.FindControl<Button>(n)!).ToArray();
        Assert.All(buttons, b => Assert.InRange(b.Bounds.Width, 33, 35));
        void Click(Control control) { var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value; window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); window.UpdateLayout(); }
        Click(buttons[1]);
        Assert.True(model.IsRowsLayout); Assert.Single(window.FindControl<Grid>("PaneHost")!.ColumnDefinitions);
        Assert.Equal(3, window.FindControl<Grid>("PaneHost")!.RowDefinitions.Count);
        Assert.True(await model.SaveSafelyAsync()); Assert.True((await repo.LoadAsync())!.TwoPaneRows);
        Click(buttons[2]); Assert.True(model.IsColumnsLayout);
        var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(); Click(toggle); Assert.False(model.PreviewVisible); Click(toggle); Assert.True(model.PreviewVisible);
        var entry = model.Panes[0].Entries.First(); model.Panes[0].Entries = Enumerable.Range(0, 10000).Select(i => entry with { Name = $"文件{i:D5}.txt" }).ToArray();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); window.UpdateLayout();
        var pane = window.GetVisualDescendants().OfType<PaneView>().First(v => v.DataContext == model.Panes[0]); var list = pane.FindControl<ListBox>("FileList")!;
        Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);
        var mouse = list.TranslatePoint(new Point(30, 50), window)!.Value;
        window.MouseWheel(mouse, new Vector(0, 1), RawInputModifiers.Control); window.UpdateLayout();
        Assert.Equal(1.1, model.Panes[0].ListZoom); Assert.Equal(1, model.Panes[1].ListZoom);
        Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 100);
        window.Close(); await Wait(() => !window.IsVisible);
    }
    private static async Task Wait(Func<bool> condition)
    { using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(10, limit.Token); }
    private static void Screenshot(Window window, string name)
    {
        window.UpdateLayout(); var directory = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS"); if (directory is null) return;
        Directory.CreateDirectory(directory); using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window); bitmap.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
    private static SettingsViewModel Settings() => new(new Store(), new Protector(), new Planner(), new Index());
    private sealed class Store : IAppSettingsStore
    { public Task<AppSettings> LoadAsync(CancellationToken token = default) => Task.FromResult(new AppSettings { AiModel = "测试模型", LiveSearch = false }); public Task SaveAsync(AppSettings settings, CancellationToken token = default) => Task.CompletedTask; }
    private sealed class Protector : ISecretProtector { public string Protect(string s) => s; public string Unprotect(string s) => s; }
    private sealed class Planner : IAiSearchPlanner { public Task<FileSearchPlan> PlanAsync(string s, AiConnection c, CancellationToken token = default) => throw new NotImplementedException(); }
    private sealed class Index : IIndexedSearchProvider { public Task<IndexedSearchResponse> SearchAsync(IndexedSearchRequest r, string p, string n, CancellationToken token = default) => throw new NotImplementedException(); }
    private sealed class Platform : IPlatformActions { public void Open(Location l) { } public void Reveal(Location l) { } public void OpenTerminal(Location l) { } }
    private sealed class StreamingClient : IAiChatClient
    {
        public Channel<AiChatChunk> Chunks { get; set; } = Channel.CreateUnbounded<AiChatChunk>(); public int Calls; public string? Context; public IReadOnlyList<AiChatMessage>? History;
        public async IAsyncEnumerable<AiChatChunk> StreamAsync(IReadOnlyList<AiChatMessage> messages, string directoryContext, AiConnection connection, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; Context = directoryContext; History = messages; await foreach (var item in Chunks.Reader.ReadAllAsync(token)) yield return item; }
    }
    private sealed class Handler(string response, string media = "text/event-stream", int size = 3) : HttpMessageHandler
    {
        public string? Body; public string? Url; public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token); Url = request.RequestUri!.AbsoluteUri;
            var content = new StreamContent(new FragmentStream(Encoding.UTF8.GetBytes(response), size)); content.Headers.ContentType = new(media);
            return new(Status) { Content = content };
        }
    }
    private sealed class FragmentStream(byte[] bytes, int size) : MemoryStream(bytes)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(size, buffer.Length)], token); }
}


