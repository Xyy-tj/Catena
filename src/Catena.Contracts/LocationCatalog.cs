namespace Catena.Contracts;

public sealed record CatalogItem(string Path, string Parent, string Name, bool IsDirectory, long Size, DateTimeOffset Modified, bool CanExpand = true);
public sealed record FolderListing(IReadOnlyList<CatalogItem> Items, DateTimeOffset Updated, bool Truncated = false);
public sealed record StructureProfile(int Folders, int Files, long Bytes, int MaxDepth, int Skipped,
    bool Partial, DateTimeOffset Updated, DateTimeOffset? LatestModified,
    IReadOnlyDictionary<string, int> Types, IReadOnlyDictionary<string, int> Branches);
public sealed record CatalogSnapshot(string Root, IReadOnlyList<CatalogItem> Items, StructureProfile Profile);
public sealed record CatalogRoot(string Path, string Name, StructureProfile? Profile);
public sealed record RecentLocation(string Path, string Name, bool IsFile, DateTimeOffset Visited, int Visits);
public sealed record CatalogScanLimits(int MaxEntries = 200000, int MaxDepth = 64, int MaxSeconds = 120);

public interface ILocationCatalogStore
{
    Task InitializeAsync(IReadOnlyList<string> defaults, CancellationToken token = default);
    Task<IReadOnlyList<CatalogRoot>> GetRootsAsync(CancellationToken token = default);
    Task AddRootAsync(string path, string name, CancellationToken token = default);
    Task RemoveRootAsync(string path, CancellationToken token = default);
    Task SaveSnapshotAsync(CatalogSnapshot snapshot, CancellationToken token = default);
    Task<FolderListing?> GetFoldersAsync(string path, CancellationToken token = default);
    Task SaveFoldersAsync(string path, FolderListing listing, CancellationToken token = default);
    Task RecordVisitAsync(string path, bool isFile, CancellationToken token = default);
    Task<IReadOnlyList<RecentLocation>> GetRecentAsync(CancellationToken token = default);
    Task ClearRecentAsync(CancellationToken token = default);
}
