using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Catena.App;
using Catena.Contracts;
using Catena.Persistence;

namespace Catena.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("v0.6.3", "0.6.3.0")]
    [InlineData("0.10.0", "0.10.0.0")]
    [InlineData("0.6.3+build42", "0.6.3.0")]
    public void ReleaseVersionsAreNumeric(string tag, string expected)
    { Assert.True(GitHubUpdateChecker.TryParseVersion(tag, out var version)); Assert.Equal(new Version(expected), version); }

    [Theory]
    [InlineData("v0.6.3-beta")]
    [InlineData("main")]
    [InlineData("0.6")]
    public void UnstableOrInvalidVersionsAreRejected(string tag) => Assert.False(GitHubUpdateChecker.TryParseVersion(tag, out _));

    [Fact] public async Task OfficialReleaseUsesFixedEndpointAndTrustedBrowserLink()
    {
        var handler = new Handler(HttpStatusCode.OK, """{"tag_name":"v0.10.0","draft":false,"prerelease":false,"html_url":"https://untrusted.example"}""");
        using var checker = new GitHubUpdateChecker(handler);
        var release = (await checker.CheckAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("https://api.github.com/repos/Xyy-tj/Catena/releases/latest", handler.Url);
        Assert.False(handler.HasCredentials); Assert.True(handler.HasUserAgent);
        Assert.Equal(new Version(0, 10, 0, 0), release.Version);
        Assert.Equal("https://github.com/Xyy-tj/Catena/releases/tag/v0.10.0", release.Page.AbsoluteUri);
    }
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"1.0.0\",\"draft\":false,\"prerelease\":true}")]
    public async Task NoStableReleaseIsDistinctFromNetworkFailure(HttpStatusCode status, string body)
    { using var checker = new GitHubUpdateChecker(new Handler(status, body)); Assert.Null(await checker.CheckAsync(TestContext.Current.CancellationToken)); }

    [AvaloniaFact] public async Task ChecksShowNewCurrentEmptyAndFailureStates()
    {
        using var checker = new GitHubUpdateChecker(new Handler(HttpStatusCode.OK, """{"tag_name":"9.0.0","draft":false,"prerelease":false}"""));
        using var updates = new UpdateViewModel(checker);
        await updates.CheckCommand.ExecuteAsync(null);
        Assert.True(updates.IsUpdateAvailable); Assert.Contains("9.0.0", updates.Status); Assert.NotEmpty(updates.LastCheckedText);
        using var currentChecker = new GitHubUpdateChecker(new Handler(HttpStatusCode.OK, """{"tag_name":"0.1.0","draft":false,"prerelease":false}"""));
        using var current = new UpdateViewModel(currentChecker); await current.CheckCommand.ExecuteAsync(null);
        Assert.False(current.IsUpdateAvailable); Assert.Equal("当前已是最新版本", current.Status);
        using var missingChecker = new GitHubUpdateChecker(new Handler(HttpStatusCode.NotFound, ""));
        using var missing = new UpdateViewModel(missingChecker); await missing.CheckCommand.ExecuteAsync(null);
        Assert.Equal("暂无正式发布版本", missing.Status);
        using var failedChecker = new GitHubUpdateChecker(new Handler(HttpStatusCode.Forbidden, ""));
        using var failed = new UpdateViewModel(failedChecker); await failed.CheckCommand.ExecuteAsync(null);
        Assert.Contains("稍后重试", failed.Status); Assert.Empty(failed.LastCheckedText); Assert.False(failed.IsChecking);
        using var malformedChecker = new GitHubUpdateChecker(new Handler(HttpStatusCode.OK, "{}"));
        using var malformed = new UpdateViewModel(malformedChecker); await malformed.CheckCommand.ExecuteAsync(null);
        Assert.Contains("版本信息暂不可用", malformed.Status);
    }
    [AvaloniaFact] public async Task UpdatePreferencePersistsAndSettingsPageRenders()
    {
        using var temp = new TestDirectory(); var store = new JsonAppSettingsStore(Path.Combine(temp.Path, "settings.json"));
        using var checker = new GitHubUpdateChecker(new Handler(HttpStatusCode.OK, """{"tag_name":"9.0.0","draft":false,"prerelease":false}"""));
        using var updates = new UpdateViewModel(checker);
        using var settings = new SettingsViewModel(store, new Protector(), new Planner(), new Index(), updates: updates);
        await settings.LoadAsync(); Assert.True(settings.AutoCheckUpdates);
        settings.AutoCheckUpdates = false; await settings.SaveCommand.ExecuteAsync(null);
        Assert.False((await store.LoadAsync()).AutoCheckUpdates);
        await updates.CheckCommand.ExecuteAsync(null);
        var window = new SettingsWindow { DataContext = settings }; window.Show();
        window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 4; window.UpdateLayout();
        var output = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
        if (output is not null)
        { using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(820, 640)); bitmap.Render(window); bitmap.Save(Path.Combine(output, "catena-updates.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        window.Close();
    }
    [AvaloniaFact] public async Task DisposalCancelsPendingCheckWithoutPublishing()
    {
        var checker = new WaitingChecker(); var model = new UpdateViewModel(checker);
        var check = model.CheckCommand.ExecuteAsync(null); Assert.True(model.IsChecking);
        model.Dispose(); await check; Assert.True(checker.Cancelled); Assert.False(model.IsUpdateAvailable);
    }
    [AvaloniaFact] public async Task AutomaticChecksWaitForStartupAndRespectDisabledPreference()
    {
        var checker = new CountingChecker(); using var updates = new UpdateViewModel(checker);
        updates.SetAutomatic(true); updates.SetAutomatic(false);
        await Task.Delay(TimeSpan.FromSeconds(10.3), TestContext.Current.CancellationToken);
        Assert.Equal(0, checker.Calls);
        updates.SetAutomatic(true); Assert.Equal(0, checker.Calls);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (checker.Calls == 0) await Task.Delay(30, timeout.Token);
        Assert.Equal(1, checker.Calls); Assert.Equal("暂无正式发布版本", updates.Status);
        updates.SetAutomatic(true); // Saving other settings must not cause an extra request.
        await Task.Delay(TimeSpan.FromSeconds(10.3), TestContext.Current.CancellationToken);
        Assert.Equal(1, checker.Calls);
    }
    private sealed class CountingChecker : IUpdateChecker
    { public int Calls; public Task<AppRelease?> CheckAsync(CancellationToken token = default) { Calls++; return Task.FromResult<AppRelease?>(null); } }
    private sealed class WaitingChecker : IUpdateChecker
    {
        public bool Cancelled;
        public async Task<AppRelease?> CheckAsync(CancellationToken token = default)
        { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { Cancelled = true; throw; } return null; }
    }
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Url; public bool HasCredentials; public bool HasUserAgent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Url = request.RequestUri!.AbsoluteUri; HasCredentials = request.Headers.Authorization is not null; HasUserAgent = request.Headers.UserAgent.Count > 0; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }); }
    }
    private sealed class Protector : ISecretProtector { public string Protect(string v) => v; public string Unprotect(string v) => v; }
    private sealed class Planner : IAiSearchPlanner { public Task<FileSearchPlan> PlanAsync(string d, AiConnection c, CancellationToken t = default) => throw new NotSupportedException(); }
    private sealed class Index : IIndexedSearchProvider { public Task<IndexedSearchResponse> SearchAsync(IndexedSearchRequest r, string e, string i, CancellationToken t = default) => throw new NotSupportedException(); }
}
