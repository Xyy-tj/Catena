using Catena.Contracts;
using Catena.Search.Everything;
using Catena.Storage.Local;

namespace Catena.Tests;
public sealed class EverythingTests
{
    [Fact] public void QueryIsCompiledFromAllowlistedFields()
    {
        var query = EverythingSearchProvider.BuildQuery(new()
        {
            Terms = ["预算", "-exit | *.pdf"], Extensions = ["xlsx", "xls"], Kind = "file",
            ModifiedFrom = new(2026, 8, 1), ModifiedBefore = new(2026, 9, 1), MinimumBytes = 100
        });
        Assert.Contains("regex:\\x{9884}\\x{7B97}", query); Assert.Contains("\\x{7C}", query); Assert.Contains("\\x{2A}", query);
        Assert.Contains("ext:xlsx;xls file: dm:>=2026-08-01 dm:<2026-09-01 size:>=100", query);
        Assert.Throws<SearchAssistanceException>(() => EverythingSearchProvider.BuildQuery(new() { Extensions = ["pdf -exit"] }));
    }
    [Fact] public void EfuParsingPreservesChineseCommasAndDirectoryMetadata()
    {
        if (!OperatingSystem.IsWindows()) return;
        var entries = EverythingSearchProvider.ParseEfu("\"C:\\数据\\预算,2026.xlsx\",2048,134351136000000000,,32\r\n\"C:\\数据\\项目\",,,,16\r\n", new LocalFileSystemProvider());
        Assert.Equal(2, entries.Count); Assert.Equal("预算,2026.xlsx", entries[0].Name); Assert.Equal(2048, entries[0].Size);
        Assert.False(entries[0].IsDirectory); Assert.True(entries[1].IsDirectory); Assert.Null(entries[1].Size);
    }
    [Fact] public async Task LimitScopeAndSearchArePassedAsSeparateArguments()
    {
        using var temp = new TestDirectory(); var path = Path.Combine(temp.Path, "fake-es.exe"); File.WriteAllText(path, "test stub");
        var runner = new Runner(); var locations = new LocalFileSystemProvider();
        var search = new EverythingSearchProvider(locations, runner);
        await search.SearchAsync(new(new() { Terms = ["-exit"] }, locations.Normalize(temp.Path), 7), path, "isolated", TestContext.Current.CancellationToken);
        Assert.Contains("-path", runner.Arguments); Assert.Contains(temp.Path, runner.Arguments);
        Assert.Equal("8", runner.Arguments[Array.IndexOf(runner.Arguments, "-n") + 1]);
        Assert.Equal("-search", runner.Arguments[^2]); Assert.DoesNotContain("-exit", runner.Arguments);
    }
    [Fact] public async Task MissingEverythingIsReportedWithoutLocalFallback()
    {
        using var temp = new TestDirectory(); var path = Path.Combine(temp.Path, "fake-es.exe"); File.WriteAllText(path, "test stub");
        var provider = new EverythingSearchProvider(new LocalFileSystemProvider(), new Runner { ExitCode = 8 });
        var error = await Assert.ThrowsAsync<SearchAssistanceException>(() => provider.SearchAsync(new(new() { Terms = ["test"] }), path, "", TestContext.Current.CancellationToken));
        Assert.Contains("尚未就绪", error.Message);
    }
    [Fact] public async Task RealEverythingIpcSearchUsesIsolatedFixtureIndex()
    {
        var instance = Environment.GetEnvironmentVariable("CATENA_EVERYTHING_TEST_INSTANCE");
        if (string.IsNullOrEmpty(instance)) Assert.Skip("设置隔离 Everything 实例后运行真实 IPC 测试。");
        var executable = Environment.GetEnvironmentVariable("CATENA_EVERYTHING_TEST_ES")!;
        var root = Environment.GetEnvironmentVariable("CATENA_EVERYTHING_TEST_ROOT")!;
        using var ipc = new EverythingIpcClient();
        var locations = new LocalFileSystemProvider(); var provider = new EverythingSearchProvider(locations, new EsProcessRunner(), ipc: ipc);
        var plan = new FileSearchPlan { Terms = ["预算"], Extensions = ["xlsx"], Kind = "file" };
        var result = await provider.SearchAsync(new(plan, locations.Normalize(root)), executable, instance, TestContext.Current.CancellationToken);
        Assert.Equal("catena 预算,2026.xlsx", Assert.Single(result.Entries).Name);
        Assert.Equal(2048, result.Entries[0].Size);
        var literal = await provider.SearchAsync(new(new() { Terms = ["-exit"] }), executable, instance, TestContext.Current.CancellationToken);
        Assert.Empty(literal.Entries);
        var special = await provider.SearchAsync(new(new() { Terms = ["[预算] # ; ! (v1) 😀"] }, locations.Normalize(root)), executable, instance, TestContext.Current.CancellationToken);
        Assert.Equal("literal [预算] # ; ! (v1) 😀.txt", Assert.Single(special.Entries).Name);
        var folders = await provider.SearchAsync(new(new() { Terms = ["测试目录"], Kind = "folder" }, locations.Normalize(root)), executable, instance, TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(folders.Entries).IsDirectory);
        var limited = await provider.SearchAsync(new(new() { Terms = ["document-"] }, locations.Normalize(root), 7), executable, instance, TestContext.Current.CancellationToken);
        Assert.Equal(7, limited.Entries.Count); Assert.True(limited.LimitReached);
        var scoped = await provider.SearchAsync(new(new() { Terms = ["recursive-marker"] }, Scopes:
            [locations.Normalize(Path.Combine(root, "scope-a")), locations.Normalize(Path.Combine(root, "scope-b")), locations.Normalize(Path.Combine(root, "scope-a", "deep"))]),
            executable, instance, TestContext.Current.CancellationToken);
        Assert.Equal(2, scoped.Entries.Count); Assert.Equal(300, scoped.Entries.Sum(e => e.Size));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => provider.SearchAsync(
            new(new() { Terms = [i % 2 == 0 ? "预算" : "document-9999"] }), executable, instance, TestContext.Current.CancellationToken)));
        foreach (var response in concurrent) Assert.NotEmpty(response.Entries);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(new(plan), executable, instance, cancelled.Token));
        }
        var samples = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var measured = await provider.SearchAsync(new(plan, locations.Normalize(root)), executable, instance, TestContext.Current.CancellationToken);
            Assert.Single(measured.Entries); samples.Add(measured.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var legacy = new EverythingSearchProvider(locations, new EsProcessRunner());
        var oldSamples = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var measured = await legacy.SearchAsync(new(plan, locations.Normalize(root)), executable, instance, TestContext.Current.CancellationToken);
            Assert.Equal(result.Entries[0].Name, Assert.Single(measured.Entries).Name); oldSamples.Add(measured.Elapsed.TotalMilliseconds);
        }
        oldSamples.Sort();
        var report = $"Same isolated {Environment.GetEnvironmentVariable("CATENA_EVERYTHING_TEST_COUNT")}-entry fixture (plus synthesized parent folders), 30 warm queries per transport.\nDirect IPC + binary parse: P50={samples[14]:F2} ms; P95={samples[28]:F2} ms\nES + UTF-8 EFU + parse: P50={oldSamples[14]:F2} ms; P95={oldSamples[28]:F2} ms";
        TestContext.Current.TestOutputHelper!.WriteLine(report);
        var artifacts = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
        if (artifacts is not null) { Directory.CreateDirectory(artifacts); await File.WriteAllTextAsync(Path.Combine(artifacts, "everything-benchmark.txt"), report, TestContext.Current.CancellationToken); }
    }
    private sealed class Runner : IEsProcessRunner
    {
        public string[] Arguments { get; private set; } = [];
        public int ExitCode { get; init; }
        public Task<EsOutput> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
        { Arguments = arguments.ToArray(); return Task.FromResult(new EsOutput(ExitCode, "")); }
    }
}
