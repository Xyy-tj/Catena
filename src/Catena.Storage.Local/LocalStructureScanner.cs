using System.Diagnostics;
using System.Runtime.InteropServices;
using Catena.Contracts;

namespace Catena.Storage.Local;

// Metadata only: never opens file content or follows directory aliases.
public sealed class LocalStructureScanner
{
    public Task<FolderListing> ReadFoldersAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        var items = new List<CatalogItem>(); var truncated = false;
        foreach (var directory in new DirectoryInfo(path).EnumerateDirectories())
        {
            token.ThrowIfCancellationRequested();
            if (items.Count >= 2000) { truncated = true; break; }
            try { items.Add(Describe(directory)); }
            catch (Exception ex) when (LocalFileSystemProvider.IsIoError(ex)) { }
        }
        return new FolderListing(items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), DateTimeOffset.UtcNow, truncated);
    }, token);

    public Task<CatalogSnapshot> ScanAsync(string root, CatalogScanLimits? limits = null, IProgress<int>? progress = null, CancellationToken token = default) => Task.Run(() =>
    {
        limits ??= new();
        if (limits.MaxEntries <= 0 || limits.MaxDepth < 0 || limits.MaxSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists) throw new DirectoryNotFoundException();
        if (!CanTraverse(rootInfo)) throw new IOException("不扫描目录链接。");
        var timer = Stopwatch.StartNew(); var result = new List<CatalogItem>();
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0));
        var folders = 0; var files = 0; long bytes = 0; var depth = 0; var skipped = 0; var partial = false;
        DateTimeOffset? latest = null;
        var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var branches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (queue.TryDequeue(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= limits.MaxEntries || timer.Elapsed.TotalSeconds > limits.MaxSeconds) { partial = true; break; }
            try
            {
                foreach (var info in new DirectoryInfo(next.Path).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Count >= limits.MaxEntries || timer.Elapsed.TotalSeconds > limits.MaxSeconds) { partial = true; break; }
                    CatalogItem item;
                    try { item = Describe(info); }
                    catch (Exception ex) when (LocalFileSystemProvider.IsIoError(ex)) { skipped++; continue; }
                    result.Add(item); depth = Math.Max(depth, next.Depth + 1);
                    if (item.IsDirectory)
                    {
                        folders++;
                        if (!item.CanExpand) skipped++;
                        else if (next.Depth < limits.MaxDepth) queue.Enqueue((item.Path, next.Depth + 1));
                        else partial = true;
                    }
                    else
                    {
                        files++; bytes += item.Size;
                        var extension = Path.GetExtension(item.Name).ToLowerInvariant(); if (extension.Length == 0) extension = "无扩展名";
                        types[extension] = types.GetValueOrDefault(extension) + 1;
                        var relative = Path.GetRelativePath(root, item.Path); var separator = relative.IndexOf(Path.DirectorySeparatorChar);
                        var branch = separator < 0 ? "根目录" : relative[..separator];
                        branches[branch] = branches.GetValueOrDefault(branch) + 1;
                    }
                    if (latest is null || item.Modified > latest) latest = item.Modified;
                    if (result.Count % 1000 == 0) progress?.Report(result.Count);
                }
            }
            catch (Exception ex) when (LocalFileSystemProvider.IsIoError(ex))
            {
                if (next.Depth == 0) throw; // Never replace a useful cache with an unreadable root.
                skipped++;
            }
        }
        token.ThrowIfCancellationRequested();
        var profile = new StructureProfile(folders, files, bytes, depth, skipped, partial || skipped > 0, DateTimeOffset.UtcNow, latest,
            types.OrderByDescending(x => x.Value).Take(12).ToDictionary(), branches.OrderByDescending(x => x.Value).Take(12).ToDictionary());
        return new CatalogSnapshot(root, result, profile);
    }, token);

    private static CatalogItem Describe(FileSystemInfo info)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        return new(info.FullName, Path.GetDirectoryName(info.FullName)!, info.Name, isDirectory,
            info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc, isDirectory && CanTraverse(info));
    }
    public static bool IsCloudTag(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;
    public static bool CanTraverse(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return true;
        if (!OperatingSystem.IsWindows()) return false;
        var handle = FindFirstFile(info.FullName, out var data);
        if (handle == new IntPtr(-1)) return false;
        try { return IsCloudTag(data.Reserved0); }
        finally { FindClose(handle); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct FindData
    {
        public uint Attributes; public long CreationTime; public long AccessTime; public long WriteTime;
        public uint SizeHigh; public uint SizeLow; public uint Reserved0; public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
    }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFile(string path, out FindData data);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
}
