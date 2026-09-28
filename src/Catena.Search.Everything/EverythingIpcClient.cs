using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Catena.Contracts;

namespace Catena.Search.Everything;

public interface IEverythingIpcClient
{
    Task<byte[]> QueryAsync(string query, string instance, int limit, CancellationToken token);
}

// Everything's public QUERY2 protocol. No helper process, temporary file or SDK DLL.
// Each concurrent query needs its own reply HWND: Everything cancels older queries to the same HWND.
public sealed class EverythingIpcClient : IEverythingIpcClient, IDisposable
{
    private const uint CopyData = 0x004a, WorkMessage = 0x8001;
    private const uint Fields = 0x04 | 0x10 | 0x40; // full path, size, modified
    private readonly ConcurrentQueue<Action> work = new();
    private readonly Dictionary<nint, Pending> pending = [];
    private readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WndProc procedure;
    private readonly Thread thread;
    private readonly object gate = new();
    private readonly string className = "Catena.Everything." + Guid.NewGuid().ToString("N");
    private bool started, disposed;
    private uint serial;

    public EverythingIpcClient()
    {
        procedure = WindowProcedure;
        thread = new Thread(Pump) { IsBackground = true, Name = "Catena Everything IPC" };
    }

    public async Task<byte[]> QueryAsync(string query, string instance, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new SearchAssistanceException("Everything 需要 Windows。");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started) { started = true; thread.Start(); }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var deadlineToken = deadline.Token;
        var item = new Pending();
        try
        {
            var control = await ready.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            Enqueue(control, () => Send(item, query, instance, Math.Clamp(limit, 1, 1001), deadlineToken));
            return await item.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new SearchAssistanceException("Everything 响应超时，请确认索引已就绪。"); }
        finally
        {
            // Cleanup also follows a cancelled queued send, so no reply window leaks.
            if (ready.Task.IsCompletedSuccessfully)
                Enqueue(ready.Task.Result, () => Remove(item));
        }
    }

    private void Enqueue(nint window, Action action)
    {
        lock (gate)
        {
            if (disposed) return;
            work.Enqueue(action);
            PostMessage(window, WorkMessage, 0, 0);
        }
    }

    private void Send(Pending item, string query, string instance, int limit, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        try
        {
            var target = FindWindow("EVERYTHING_TASKBAR_NOTIFICATION" +
                (string.IsNullOrWhiteSpace(instance) ? "" : "_(" + instance.Trim() + ")"), null);
            if (target == 0) throw new SearchAssistanceException("搜索组件尚未就绪，请在设置中检查 Everything。");
            item.Window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, 0, 0, GetModuleHandle(null), 0);
            if (item.Window == 0) throw new SearchAssistanceException("无法创建 Everything 查询连接。");
            item.Id = ++serial;
            pending.Add(item.Window, item);
            var bytes = new byte[28 + Encoding.Unicode.GetByteCount(query) + 2];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, unchecked((uint)item.Window));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), item.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)limit);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), Fields);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 1); // name ascending: always indexed
            Encoding.Unicode.GetBytes(query, bytes.AsSpan(28));
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var data = new CopyDataStruct { Kind = 18, Length = bytes.Length, Data = handle.AddrOfPinnedObject() };
                if (SendMessageTimeout(target, CopyData, item.Window, ref data, 2, 1500, out var accepted) == 0 || accepted == 0)
                    throw new SearchAssistanceException("Everything 未接受查询，请检查版本、运行状态和权限是否一致。");
            }
            finally { handle.Free(); }
        }
        catch (Exception ex) { item.Completion.TrySetException(ex); }
    }

    private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WorkMessage)
        {
            while (work.TryDequeue(out var action)) action();
            return 0;
        }
        if (message == CopyData && pending.TryGetValue(window, out var item))
        {
            var data = Marshal.PtrToStructure<CopyDataStruct>(lParam);
            if (data.Kind != item.Id) return 0; // ignore a late reply to a recycled HWND
            if (data.Length is < 20 or > 4 * 1024 * 1024 || data.Data == 0)
                item.Completion.TrySetException(new SearchAssistanceException("Everything 返回数据超过限制或格式无效。"));
            else
            {
                var bytes = new byte[data.Length];
                Marshal.Copy(data.Data, bytes, 0, bytes.Length);
                item.Completion.TrySetResult(bytes);
            }
            return 1;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void Remove(Pending item)
    {
        if (item.Window == 0) return;
        pending.Remove(item.Window);
        DestroyWindow(item.Window);
        item.Window = 0;
    }

    private void Pump()
    {
        nint control = 0;
        try
        {
            var registration = new WindowClass { Procedure = procedure, Instance = GetModuleHandle(null), ClassName = className };
            if (RegisterClass(ref registration) == 0) throw new SearchAssistanceException("无法初始化 Everything IPC。");
            control = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, 0, 0, registration.Instance, 0);
            if (control == 0) throw new SearchAssistanceException("无法初始化 Everything IPC 窗口。");
            ready.TrySetResult(control);
            while (GetMessage(out var message, 0, 0, 0) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception ex) { ready.TrySetException(ex); }
        finally
        {
            foreach (var item in pending.Values.ToArray())
            { item.Completion.TrySetException(new ObjectDisposedException(nameof(EverythingIpcClient))); Remove(item); }
            if (control != 0) DestroyWindow(control);
            UnregisterClass(className, GetModuleHandle(null));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (!started) return;
        }
        // The pump must own destruction of its windows. Avoid blocking the UI at shutdown.
        _ = ready.Task.ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            work.Enqueue(() => PostQuitMessage(0));
            PostMessage(t.Result, WorkMessage, 0, 0);
        }, TaskScheduler.Default);
    }

    public static IReadOnlyList<FileEntry> ParseResults(byte[] bytes, ILocationProvider locations)
    {
        try
        {
            var data = bytes.AsSpan();
            var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
            if (count > 1001 || BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != Fields || data.Length < 20 + count * 8)
                throw new FormatException();
            var result = new List<FileEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var directory = (BinaryPrimitives.ReadUInt32LittleEndian(data[(20 + i * 8)..]) & 3) != 0;
                var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[(24 + i * 8)..]));
                if (offset < 20 + count * 8) throw new FormatException();
                var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) * 2);
                var path = Encoding.Unicode.GetString(data.Slice(offset + 4, length));
                var metadata = offset + 4 + length + 2;
                var size = BinaryPrimitives.ReadInt64LittleEndian(data[metadata..]);
                var date = BinaryPrimitives.ReadInt64LittleEndian(data[(metadata + 8)..]);
                var location = locations.Normalize(path);
                result.Add(new(location, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                    directory ? EntryKind.Directory : EntryKind.File, directory || size < 0 ? null : size,
                    date > 0 ? DateTimeOffset.FromFileTime(date) : null,
                    EntryCapabilities.Open | EntryCapabilities.Reveal | (directory ? EntryCapabilities.Browse : EntryCapabilities.None)));
            }
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or FormatException or FileSystemException)
        { throw new SearchAssistanceException("Everything 结果格式无效，请检查客户端版本。"); }
    }

    private sealed class Pending
    {
        public nint Window;
        public uint Id;
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct CopyDataStruct { public nuint Kind; public int Length; public nint Data; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public WndProc Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassW")] private static extern ushort RegisterClass(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, ref CopyDataStruct data, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out NativeMessage message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
