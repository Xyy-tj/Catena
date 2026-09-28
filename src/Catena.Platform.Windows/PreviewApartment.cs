using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Catena.Platform.Windows;

// A single STA with a message pump keeps slow third-party preview handlers off the UI thread.
[SupportedOSPlatform("windows")]
public static class PreviewApartment
{
    private static readonly ConcurrentQueue<Action> Jobs = new();
    private static readonly TaskCompletionSource<uint> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    static PreviewApartment()
    {
        var thread = new Thread(() =>
        {
            CoInitializeEx(0, 2);
            PeekMessage(out _, 0, 0, 0, 0);
            Ready.TrySetResult(GetCurrentThreadId());
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == 0x8002) { while (Jobs.TryDequeue(out var job)) job(); }
                else { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
        }) { IsBackground = true, Name = "Catena document preview" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public static async Task RunAsync(Action action)
    {
        var thread = await Ready.Task.ConfigureAwait(false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Jobs.Enqueue(() => { try { action(); completion.TrySetResult(); } catch (Exception ex) { completion.TrySetException(ex); } });
        PostThreadMessage(thread, 0x8002, 0, 0);
        await completion.Task.ConfigureAwait(false);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
}
