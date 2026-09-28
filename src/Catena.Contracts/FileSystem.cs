namespace Catena.Contracts;

public sealed record Location(string ProviderId, string Uri);

[Flags]
public enum EntryCapabilities { None = 0, Browse = 1, Open = 2, Reveal = 4 }
public enum EntryKind { File, Directory }
public sealed record FileEntry(Location Location, string Name, EntryKind Kind, long? Size,
    DateTimeOffset? ModifiedAt, EntryCapabilities Capabilities)
{
    public bool IsDirectory => Kind == EntryKind.Directory;
    public string Symbol => IsDirectory ? "▸" : "·";
    public string TypeText => IsDirectory ? "文件夹" : Path.GetExtension(Name) is { Length: > 0 } extension ? extension[1..].ToUpperInvariant() + " 文件" : "文件";
    public string DisplayPath => System.Uri.TryCreate(Location.Uri, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : Location.Uri;
    public string SizeText => Size is null ? "—" : Size < 1024 ? $"{Size} B" : Size < 1048576 ? $"{Size / 1024d:N1} KB" : $"{Size / 1048576d:N1} MB";
    public string ModifiedText => ModifiedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
}

public enum FileSystemError { NotFound, AccessDenied, Unavailable, Unsupported, InvalidPath }
public sealed class FileSystemException(FileSystemError code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public FileSystemError Code { get; } = code;
}

public interface ILocationProvider
{
    string Id { get; }
    Location Normalize(string path);
    string GetPath(Location location);
    Location? GetParent(Location location);
    bool AreEqual(Location left, Location right);
    IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(Location directory, CancellationToken cancellationToken = default);
    Task<FileEntry> GetEntryAsync(Location location, CancellationToken cancellationToken = default);
}

public sealed record SearchQuery(string Text, Location Scope, int Limit = 200);
public sealed record SearchBatch(IReadOnlyList<FileEntry> Entries, int SkippedDirectories, bool LimitReached);
public interface ISearchProvider
{
    string Name { get; }
    IAsyncEnumerable<SearchBatch> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
}

public interface IPlatformActions
{
    void Open(Location location);
    void Reveal(Location location);
    void OpenTerminal(Location directory);
}
