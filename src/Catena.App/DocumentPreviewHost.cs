using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Catena.Platform.Windows;

namespace Catena.App;

public sealed class DocumentPreviewHost : NativeControlHost
{
    public static readonly StyledProperty<string> FilePathProperty = AvaloniaProperty.Register<DocumentPreviewHost, string>(nameof(FilePath), "");
    public string FilePath { get => GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    private nint window;
    private WindowsDocumentPreview? preview;
    private long revision;
    private int resizeQueued;
    private long resizeRevision;
    private CancellationTokenSource? idleRelease;
    public event Action<string>? Failed;
    public event Action<string>? Ready;
    static DocumentPreviewHost() => FilePathProperty.Changed.AddClassHandler<DocumentPreviewHost>((host, _) => host.Load());
    public DocumentPreviewHost() => SizeChanged += (_, _) => Dispatcher.UIThread.Post(Resize);
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows() || parent.HandleDescriptor != "HWND") return base.CreateNativeControlCore(parent);
        window = CreateWindowEx(0, "STATIC", "", 0x50000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        Dispatcher.UIThread.Post(Load);
        return new PlatformHandle(window, "HWND");
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (window == 0) { base.DestroyNativeControlCore(control); return; }
        Interlocked.Increment(ref revision);
        idleRelease?.Cancel(); idleRelease?.Dispose(); idleRelease = null;
        if (OperatingSystem.IsWindows()) { _ = PreviewApartment.RunAsync(() => { if (OperatingSystem.IsWindows()) preview?.Dispose(); preview = null; }); DestroyWindow(window); }
        window = 0;
    }
    private async void Load()
    {
        if (!OperatingSystem.IsWindows() || window == 0) return;
        var current = Interlocked.Increment(ref revision); var path = FilePath; var target = window;
        idleRelease?.Cancel(); idleRelease?.Dispose(); idleRelease = null;
        if (path.Length == 0)
        {
            idleRelease = new CancellationTokenSource();
            _ = ReleaseWhenIdleAsync(current, idleRelease.Token);
            return;
        }
        try
        {
            // Coalesce XAML binding/selection changes before entering an uncancellable COM call.
            await Task.Delay(40);
            if (current != Interlocked.Read(ref revision) || target != window) return;
            await PreviewApartment.RunAsync(() =>
            {
                if (!OperatingSystem.IsWindows()) return;
                if (Interlocked.Read(ref revision) != current || target != window) return;
                var session = preview ?? new WindowsDocumentPreview();
                preview = session;
                try
                {
                    session.Load(path, target);
                    if (Interlocked.Read(ref revision) != current) session.Clear();
                }
                catch { session.Dispose(); preview = null; throw; }
            }).WaitAsync(TimeSpan.FromSeconds(30));
            // Initial loading can race the first native arrange (a 1 x 1 placeholder).
            // Always fit the handler to the current viewport after it is ready.
            if (current == Interlocked.Read(ref revision)) { Resize(); if (path.Length > 0) Ready?.Invoke(path); }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidCastException or TimeoutException)
        {
            if (current != Interlocked.Read(ref revision)) return;
            Interlocked.Increment(ref revision);
            Failed?.Invoke("Windows 预览暂不可用，请使用默认应用打开。");
        }
    }
    private async Task ReleaseWhenIdleAsync(long current, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            await PreviewApartment.RunAsync(() =>
            {
                if (OperatingSystem.IsWindows() && current == Interlocked.Read(ref revision))
                { preview?.Dispose(); preview = null; }
            });
        }
        catch (OperationCanceledException) { }
        catch (COMException) { }
    }
    private async void Resize()
    {
        if (!OperatingSystem.IsWindows() || window == 0) return;
        Interlocked.Increment(ref resizeRevision);
        if (Interlocked.Exchange(ref resizeQueued, 1) != 0) return;
        var target = window;
        try
        {
            long requested;
            do
            {
                requested = Interlocked.Read(ref resizeRevision);
                await PreviewApartment.RunAsync(() => { if (OperatingSystem.IsWindows() && window == target) preview?.Resize(target); });
            } while (window == target && requested != Interlocked.Read(ref resizeRevision));
        }
        catch (COMException) { }
        finally { Interlocked.Exchange(ref resizeQueued, 0); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
