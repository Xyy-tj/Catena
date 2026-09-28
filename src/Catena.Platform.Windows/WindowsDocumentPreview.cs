using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Catena.Contracts;
using Microsoft.Win32;

namespace Catena.Platform.Windows;

public sealed class WindowsDocumentPreviewProvider(IFilePreviewProvider fallback) : IFallbackPreviewProvider
{
    private static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".docm", ".ppt", ".pptx", ".pptm", ".xls", ".xlsx", ".rtf" };
    public async Task<FilePreview> ReadAsync(FileEntry entry, CancellationToken token = default)
        => await ReadPageAsync(entry, 0, token);
    public async Task<FilePreview> ReadPageAsync(FileEntry entry, int page, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows() || entry.IsDirectory || entry.Location.ProviderId != "local" || !Formats.Contains(Path.GetExtension(entry.Name)))
            return await ReadFallbackPageAsync(entry, page, token);
        var result = await Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) return null;
            token.ThrowIfCancellationRequested();
            try
            {
                var path = new Uri(entry.Location.Uri).LocalPath;
                if (((uint)File.GetAttributes(path) & (0x1000u | 0x40000u | 0x400000u)) != 0)
                    return new FilePreview(PreviewKind.Information, Notice: "云端文件 · 使用默认应用打开");
                if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
                return WindowsDocumentPreview.FindHandler(Path.GetExtension(path)) is not null
                    ? new FilePreview(PreviewKind.Native, Notice: "正在加载文档预览…", NativePath: path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { return new FilePreview(PreviewKind.Information, Notice: "无法读取文档预览。"); }
        }, token);
        if (result is not null) return result;
        if (Path.GetExtension(entry.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await WindowsPdfPreview.ReadAsync(entry.DisplayPath, page, token);
        var content = await ReadFallbackPageAsync(entry, page, token);
        return content.Kind == PreviewKind.Information && string.IsNullOrEmpty(content.Notice)
            ? content with { Notice = "未安装此格式的 Windows 预览处理程序。" } : content;
    }
    public Task<FilePreview> ReadFallbackPageAsync(FileEntry entry, int page, CancellationToken token = default)
        => fallback is IPagePreviewProvider paged ? paged.ReadPageAsync(entry, page, token) : fallback.ReadAsync(entry, token);
}

// Use out-of-process shell handlers (Prevhost or an application's own COM server).
[SupportedOSPlatform("windows")]
public sealed class WindowsDocumentPreview : IDisposable
{
    public Action<string, TimeSpan>? Timing { get; init; }
    private const string PreviewCategory = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private IPreviewHandler? handler;
    private IStream? stream;
    private Guid? handlerId;
    private bool initialized;
    private (string Path, long Length, DateTime Modified, nint Parent)? loaded;
    private NativeRect lastBounds;
    private nint lastParent;
    public static Guid? FindHandler(string extension)
    {
        using var ext = Registry.ClassesRoot.OpenSubKey(extension);
        foreach (var key in new[] { extension, ext?.GetValue(null) as string, "SystemFileAssociations\\" + extension })
        {
            if (string.IsNullOrEmpty(key)) continue;
            using var registration = Registry.ClassesRoot.OpenSubKey(key + "\\shellex\\" + PreviewCategory);
            if (Guid.TryParse(registration?.GetValue(null) as string, out var id)) return id;
        }
        return null;
    }
    public void Load(string path, nint parent)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var info = new FileInfo(path);
        if (((uint)info.Attributes & (0x1000u | 0x40000u | 0x400000u)) != 0)
            throw new IOException("云端文件未在本地，不能自动预览。");
        var identity = (info.FullName, info.Length, info.LastWriteTimeUtc, parent);
        if (initialized && loaded == identity)
        {
            Resize(parent); Timing?.Invoke("cache-hit", watch.Elapsed); return;
        }
        var id = FindHandler(Path.GetExtension(path)) ?? throw new NotSupportedException("未安装预览处理程序。");
        var reuse = handler is not null && handlerId == id;
        if (!reuse) Dispose(); else Clear();
        if (handler is null)
        {
            var iid = typeof(IPreviewHandler).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref id, 0, 4, ref iid, out handler));
            handlerId = id;
        }
        Timing?.Invoke(reuse ? "reuse" : "activate", watch.Elapsed); watch.Restart();
        try
        {
            initialized = true;
            if (handler is IInitializeWithStream fromStream)
            {
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x40, 0, false, null, out stream));
                fromStream.Initialize(stream, 0);
            }
            else if (handler is IInitializeWithFile fromFile) fromFile.Initialize(path, 0);
            else throw new NotSupportedException("预览处理程序不支持此文件初始化方式。");
            Timing?.Invoke("initialize", watch.Elapsed); watch.Restart();
            GetClientRect(parent, out var bounds);
            handler.SetWindow(parent, ref bounds);
            lastBounds = bounds; lastParent = parent;
            Timing?.Invoke("set-window", watch.Elapsed); watch.Restart();
            handler.DoPreview();
            loaded = identity;
            Timing?.Invoke("render", watch.Elapsed);
        }
        catch (COMException) when (reuse)
        {
            // Third-party handlers may reject re-initialization despite Unload. Retry fresh once.
            Dispose(); Load(path, parent);
        }
        catch { Dispose(); throw; }
    }
    public void Resize(nint parent)
    {
        if (handler is null || !initialized || !GetClientRect(parent, out var bounds)) return;
        if (parent == lastParent && bounds.Left == lastBounds.Left && bounds.Top == lastBounds.Top &&
            bounds.Right == lastBounds.Right && bounds.Bottom == lastBounds.Bottom) return;
        handler.SetRect(ref bounds); lastBounds = bounds; lastParent = parent;
    }
    public void Clear()
    {
        loaded = null;
        lastParent = 0;
        if (handler is not null && initialized)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            initialized = false;
            try { handler.Unload(); } catch (COMException) { }
            Timing?.Invoke("unload", watch.Elapsed);
        }
        if (stream is not null) { Marshal.ReleaseComObject(stream); stream = null; }
    }
    public void Dispose()
    {
        Clear();
        if (handler is null) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Marshal.ReleaseComObject(handler); handler = null; handlerId = null;
        Timing?.Invoke("release", watch.Elapsed);
    }
    [StructLayout(LayoutKind.Sequential)] public struct NativeRect { public int Left, Top, Right, Bottom; }
    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(nint window, ref NativeRect bounds); void SetRect(ref NativeRect bounds); void DoPreview(); void Unload();
        void SetFocus(); void QueryFocus(out nint window); [PreserveSig] int TranslateAccelerator(nint message);
    }
    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }
    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode); }
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid id, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPreviewHandler handler);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IStream? reserved, out IStream stream);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect bounds);
}
