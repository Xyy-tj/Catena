using Microsoft.VisualBasic.FileIO;

namespace Catena.Platform.Windows;

// Explicit commands only. Shell file-operation dialogs handle collisions, cancellation and recycle warnings.
// A dedicated STA keeps file transfers independent of both UI and preview-handler threads.
public static class WindowsFileOperations
{
    public static Task CopyAsync(string[] paths, string destination) => RunAsync(() =>
    {
        foreach (var path in paths)
        {
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var target = Path.Combine(Path.GetFullPath(destination), Path.GetFileName(source));
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能复制到原位置或自身子目录。");
            if (Directory.Exists(source)) FileSystem.CopyDirectory(source, target, UIOption.AllDialogs, UICancelOption.ThrowException);
            else FileSystem.CopyFile(source, target, UIOption.AllDialogs, UICancelOption.ThrowException);
        }
    });

    public static Task DeleteAsync(string[] paths) => RunAsync(() =>
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            else FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        }
    });

    private static Task RunAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (OperationCanceledException) { completion.SetCanceled(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Catena file operation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
}
