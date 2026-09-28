using System.Diagnostics;
using Catena.Contracts;

namespace Catena.Platform.Windows;

public sealed class WindowsPlatformActions(ILocationProvider provider) : IPlatformActions
{
    public void Open(Location location) => Process.Start(new ProcessStartInfo(provider.GetPath(location)) { UseShellExecute = true });
    public void Reveal(Location location)
    {
        EnsureWindows();
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.Arguments = $"/select,\"{provider.GetPath(location)}\"";
        Process.Start(start);
    }
    public void OpenTerminal(Location directory)
    {
        EnsureWindows();
        // No path interpolation into a shell command. WorkingDirectory is passed separately.
        Process.Start(new ProcessStartInfo("powershell.exe")
        { WorkingDirectory = provider.GetPath(directory), UseShellExecute = true });
    }
    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此操作目前仅支持 Windows。");
    }
}
