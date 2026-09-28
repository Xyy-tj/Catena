using Catena.Contracts;
using Catena.Storage.Local;

namespace Catena.Tests;

public sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CatenaTests", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}

public sealed class FileSystemTests
{
    private readonly LocalFileSystemProvider provider = new();
    [Fact] public void UnicodeAndSpacesRoundTrip()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "中文 目录 #100%");
        var location = provider.Normalize(path);
        Assert.Equal(path, provider.GetPath(location));
        Assert.True(provider.AreEqual(location, provider.Normalize(path + Path.DirectorySeparatorChar)));
    }
    [Fact] public void WindowsUncAndCaseSemantics()
    {
        if (!OperatingSystem.IsWindows()) return;
        var location = provider.Normalize(@"\\server\share\中文 文件");
        Assert.Equal(@"\\server\share\中文 文件", provider.GetPath(location));
        Assert.True(provider.AreEqual(provider.Normalize(@"C:\Data"), provider.Normalize(@"c:\DATA")));
        Assert.Null(provider.GetParent(provider.Normalize(@"C:\")));
    }
    [Fact] public async Task EnumerationIsBatchedAndCancellationWorks()
    {
        using var temp = new TestDirectory();
        for (var i = 0; i < 600; i++) File.WriteAllText(Path.Combine(temp.Path, $"file{i}.txt"), "test");
        var batches = new List<IReadOnlyList<FileEntry>>();
        await foreach (var batch in provider.EnumerateAsync(provider.Normalize(temp.Path), TestContext.Current.CancellationToken)) batches.Add(batch);
        Assert.Equal(600, batches.Sum(x => x.Count)); Assert.All(batches, b => Assert.InRange(b.Count, 1, 256));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in provider.EnumerateAsync(provider.Normalize(temp.Path), cancellation.Token)) { } });
    }
    [Fact] public async Task MissingDirectoryHasStableError()
    {
        using var temp = new TestDirectory();
        var error = await Assert.ThrowsAsync<FileSystemException>(async () =>
        { await foreach (var _ in provider.EnumerateAsync(provider.Normalize(Path.Combine(temp.Path, "missing")), TestContext.Current.CancellationToken)) { } });
        Assert.Equal(FileSystemError.NotFound, error.Code);
    }
    [Fact] public async Task SearchIsScopedLiteralAndLimited()
    {
        using var temp = new TestDirectory();
        var scoped = Directory.CreateDirectory(Path.Combine(temp.Path, "scope")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(scoped, "sub")).FullName;
        File.WriteAllText(Path.Combine(temp.Path, "match-outside.txt"), "");
        for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(sub, $"match-{i}.txt"), "");
        var search = new LocalSearchProvider(provider); var results = new List<SearchBatch>();
        await foreach (var batch in search.SearchAsync(new("match", provider.Normalize(scoped), 5), TestContext.Current.CancellationToken)) results.Add(batch);
        Assert.Equal(5, results.Sum(r => r.Entries.Count)); Assert.True(results[^1].LimitReached);
        Assert.All(results.SelectMany(r => r.Entries), entry => Assert.DoesNotContain("outside", entry.Name));
        var literals = new List<FileEntry>();
        await foreach (var batch in search.SearchAsync(new("*.txt", provider.Normalize(scoped)), TestContext.Current.CancellationToken)) literals.AddRange(batch.Entries);
        Assert.Empty(literals);
    }
    [Fact] public async Task SearchDoesNotFollowJunctionLoop()
    {
        using var temp = new TestDirectory();
        var sub = Directory.CreateDirectory(Path.Combine(temp.Path, "nested")).FullName;
        File.WriteAllText(Path.Combine(sub, "needle.txt"), "");
        var link = Path.Combine(sub, "loop");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(temp.Path);
            using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(TestContext.Current.CancellationToken); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, temp.Path);
        try
        {
            var count = 0;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var batch in new LocalSearchProvider(provider).SearchAsync(new("needle", provider.Normalize(temp.Path)), cancellation.Token)) count += batch.Entries.Count;
            Assert.Equal(1, count);
        }
        finally { Directory.Delete(link); }
    }
}
