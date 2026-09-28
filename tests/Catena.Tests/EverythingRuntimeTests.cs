using Catena.Contracts;
using Catena.Platform.Windows;
using Catena.Search.Everything;
using Catena.Storage.Local;

namespace Catena.Tests;

public sealed class EverythingRuntimeTests
{
    [Theory]
    [InlineData("")]
    [InlineData("1.5a")]
    public async Task ExistingClientIsReusedWithoutLaunchingOrInstalling(string instance)
    {
        var host = new Host { Running = instance };
        var runtime = new EverythingRuntime(host);
        Assert.Equal(instance, await runtime.EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, host.Starts); Assert.Equal(0, host.Installs);
    }

    [Fact] public async Task InstalledClientStartsOnlyOnceForConcurrentRequests()
    {
        var host = new Host { Installed = "C:/Program Files/Everything/Everything.exe" };
        var runtime = new EverythingRuntime(host);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => runtime.EnsureRunningAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, host.Starts); Assert.Equal(0, host.Installs);
    }

    [Fact] public async Task MissingDependencyOffersInstallationWithoutUnexpectedElevation()
    {
        var host = new Host(); var runtime = new EverythingRuntime(host);
        var error = await Assert.ThrowsAsync<SearchAssistanceException>(() => runtime.EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Contains("安装搜索组件", error.Message); Assert.Equal(0, host.Installs);
        await runtime.InstallAsync();
        Assert.Equal("", await runtime.EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, host.Installs); Assert.Equal(1, host.Starts);
    }

    [Fact] public async Task CancelledSearchDoesNotRepeatedlyLaunchStartingClient()
    {
        var host = new Host { Installed = "C:/Everything.exe", BecomeReady = false };
        var runtime = new EverythingRuntime(host);
        for (var i = 0; i < 2; i++)
        {
            using var cancellation = new CancellationTokenSource(50);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EnsureRunningAsync(cancellation.Token));
        }
        Assert.Equal(1, host.Starts);
    }

    [Fact] public async Task AdvancedOverridesBypassAutomaticManagement()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "custom-es.exe"); File.WriteAllText(path, "fixture");
        var host = new Host();
        var provider = new EverythingSearchProvider(new LocalFileSystemProvider(), new Runner(), new EverythingRuntime(host));
        await provider.SearchAsync(new(new() { Terms = ["budget"] }), path, "my-instance", TestContext.Current.CancellationToken);
        Assert.Equal(0, host.Starts); Assert.Equal(0, host.Installs);
    }

    private sealed class Host : IEverythingHost
    {
        public string? Running { get; set; }
        public string? Installed { get; set; }
        public int Starts { get; private set; }
        public int Installs { get; private set; }
        public bool BecomeReady { get; init; } = true;
        public bool CanInstall => true;
        public string? FindRunningInstance() => Running;
        public string? FindInstalledExecutable() => Installed;
        public void StartClient(string executable) { Starts++; if (BecomeReady) Running = ""; }
        public Task InstallAsync() { Installs++; Installed = "C:/Everything.exe"; return Task.CompletedTask; }
    }
    private sealed class Runner : IEsProcessRunner
    {
        public Task<EsOutput> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token) => Task.FromResult(new EsOutput(0, ""));
    }
}
