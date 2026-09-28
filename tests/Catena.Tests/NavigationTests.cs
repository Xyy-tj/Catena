using System.Runtime.CompilerServices;
using Catena.Contracts;
using Catena.Core;
using Catena.Storage.Local;

namespace Catena.Tests;
public sealed class NavigationTests
{
    [Fact] public async Task StaleProviderCompletionCannotOverwriteNewLocation()
    {
        var provider = new DelayedProvider();
        var first = provider.Normalize("first"); var last = provider.Normalize("last");
        using var pane = new PaneNavigator(provider, new PaneState { Location = first });
        var slow = pane.NavigateAsync(first);
        await pane.NavigateAsync(last);
        provider.Release.SetResult(); await slow;
        Assert.Equal(last, pane.Location); Assert.Equal("last", Assert.Single(pane.Entries).Name); Assert.False(pane.IsLoading);
    }
    [Fact] public async Task HistoryAndPinnedRootsRemainIndependent()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var a = provider.Normalize(temp.Path); var b = provider.Normalize(Directory.CreateDirectory(Path.Combine(temp.Path, "b")).FullName);
        using var first = new PaneNavigator(provider, new PaneState { Location = a, PinnedRoot = a });
        using var second = new PaneNavigator(provider, new PaneState { Location = a });
        await first.NavigateAsync(b); Assert.Equal(a, second.Location); Assert.Equal(a, first.PinnedRoot);
        await first.BackAsync(); Assert.Equal(a, first.Location); Assert.True(first.CanForward);
        await first.ForwardAsync(); Assert.Equal(b, first.Location);
        await first.HomeAsync(); Assert.Equal(a, first.Location);
    }
    [Fact] public async Task MissingLocationIsKeptAndCanRecover()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider();
        var path = Path.Combine(temp.Path, "offline"); var location = provider.Normalize(path);
        using var pane = new PaneNavigator(provider, new PaneState { Location = location });
        await pane.RefreshAsync(); Assert.NotNull(pane.Error); Assert.Equal(location, pane.Location);
        Directory.CreateDirectory(path); await pane.RefreshAsync(); Assert.Null(pane.Error); Assert.Empty(pane.Entries);
    }

    private sealed class DelayedProvider : ILocationProvider
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "fake";
        public Location Normalize(string path) => new(Id, path);
        public string GetPath(Location location) => location.Uri;
        public Location? GetParent(Location location) => null;
        public bool AreEqual(Location left, Location right) => left == right;
        public Task<FileEntry> GetEntryAsync(Location location, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(Location directory, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Simulate an uncooperative provider that ignores cancellation.
            if (directory.Uri == "first") await Release.Task;
            yield return new[] { new FileEntry(directory, directory.Uri, EntryKind.Directory, null, null, EntryCapabilities.Browse) };
        }
    }
}
