using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Catena.Contracts;

namespace Catena.Storage.Local;

public sealed class LocalFileSystemProvider : ILocationProvider
{
    public string Id => "local";
    public Location Normalize(string path)
    {
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
            if (path == "~") path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException();
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return new Location(Id, new Uri(path).AbsoluteUri);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException)
        { throw new FileSystemException(FileSystemError.InvalidPath, "请输入完整的本地路径或 file URI。", ex); }
    }

    public string GetPath(Location location)
    {
        if (location.ProviderId != Id || !Uri.TryCreate(location.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
            throw new FileSystemException(FileSystemError.Unsupported, "当前版本仅支持本地和 UNC 目录。");
        return uri.LocalPath;
    }

    public Location? GetParent(Location location) => Directory.GetParent(GetPath(location)) is { } parent ? Normalize(parent.FullName) : null;
    public bool AreEqual(Location left, Location right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(GetPath(left)), Path.TrimEndingDirectorySeparator(GetPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public Task<FileEntry> GetEntryAsync(Location location, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = GetPath(location);
            var attributes = File.GetAttributes(path);
            return Describe((attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path));
        }
        catch (Exception ex) when (IsIoError(ex)) { throw Translate(ex); }
    }, cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(Location directory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var channel = Channel.CreateBounded<IReadOnlyList<FileEntry>>(2);
        var producer = Task.Run(async () =>
        {
            try
            {
                var buffer = new List<FileEntry>(256);
                foreach (var info in new DirectoryInfo(GetPath(directory)).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try { buffer.Add(Describe(info)); }
                    catch (Exception ex) when (IsIoError(ex)) { continue; }
                    if (buffer.Count == 256) { await channel.Writer.WriteAsync(buffer, token); buffer = new(256); }
                }
                if (buffer.Count > 0) await channel.Writer.WriteAsync(buffer, token);
                channel.Writer.TryComplete();
            }
            catch (Exception ex) { channel.Writer.TryComplete(IsIoError(ex) ? Translate(ex) : ex); }
        }, CancellationToken.None);
        try { await foreach (var batch in channel.Reader.ReadAllAsync(token)) yield return batch; }
        finally { linked.Cancel(); await producer; }
    }

    internal FileEntry Describe(FileSystemInfo info)
    {
        var directory = (info.Attributes & FileAttributes.Directory) != 0;
        return new FileEntry(Normalize(info.FullName), info.Name, directory ? EntryKind.Directory : EntryKind.File,
            info is FileInfo file ? file.Length : null, info.LastWriteTimeUtc,
            EntryCapabilities.Open | EntryCapabilities.Reveal | (directory ? EntryCapabilities.Browse : EntryCapabilities.None));
    }

    internal static bool IsIoError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    internal static FileSystemException Translate(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => new(FileSystemError.NotFound, "目录或文件已不存在，请检查路径后刷新。", ex),
        UnauthorizedAccessException or System.Security.SecurityException => new(FileSystemError.AccessDenied, "没有访问权限，请选择其他目录或调整权限后重试。", ex),
        _ => new(FileSystemError.Unavailable, "无法读取此位置，请检查磁盘或网络连接后刷新。", ex)
    };
}
