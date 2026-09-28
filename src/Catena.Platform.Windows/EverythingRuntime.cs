using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Catena.Contracts;
using Microsoft.Win32;

namespace Catena.Platform.Windows;

public interface IEverythingHost
{
    string? FindRunningInstance();
    string? FindInstalledExecutable();
    void StartClient(string executable);
    bool CanInstall { get; }
    Task InstallAsync();
}

public sealed class EverythingRuntime(IEverythingHost host) : IEverythingRuntime
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime lastLaunch = DateTime.MinValue;
    public bool CanInstall => host.CanInstall;

    public async Task<string> EnsureRunningAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (host.FindRunningInstance() is { } running) return running;
        await gate.WaitAsync(token);
        try
        {
            if (host.FindRunningInstance() is { } existing) return existing;
            var executable = host.FindInstalledExecutable();
            if (executable is null)
                throw new SearchAssistanceException("搜索组件尚未安装。打开设置，点击「安装搜索组件」即可完成，无需配置路径。");
            // Cancelling a search must not cause each following keystroke to launch a new client.
            if (DateTime.UtcNow - lastLaunch > TimeSpan.FromSeconds(20))
            {
                host.StartClient(executable); lastLaunch = DateTime.UtcNow;
            }
            for (var i = 0; i < 40; i++)
            {
                if (host.FindRunningInstance() is { } ready) return ready;
                await Task.Delay(250, token);
            }
            throw new SearchAssistanceException("搜索组件正在启动或等待系统权限，请稍后重试。可在设置中检查搜索组件。");
        }
        catch (Win32Exception) { throw new SearchAssistanceException("无法启动搜索组件，请在设置中检查或修复安装。"); }
        finally { gate.Release(); }
    }

    public async Task InstallAsync()
    {
        // Installation has its own lifetime; never kill an installer halfway through a service operation.
        await gate.WaitAsync();
        try { await host.InstallAsync(); lastLaunch = DateTime.MinValue; }
        finally { gate.Release(); }
    }
}

public sealed class WindowsEverythingHost : IEverythingHost
{
    private static string Installer => Path.Combine(AppContext.BaseDirectory, "tools", "everything", "Everything-Setup.exe");
    public bool CanInstall => OperatingSystem.IsWindows() && File.Exists(Installer);

    public string? FindRunningInstance()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var instance in new[] { "", "1.5a" })
            if (FindWindowW("EVERYTHING_TASKBAR_NOTIFICATION" + (instance.Length == 0 ? "" : "_(" + instance + ")"), null) != IntPtr.Zero)
                return instance;
        return null;
    }

    public string? FindInstalledExecutable()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var keyName in new[] { @"SOFTWARE\voidtools\Everything", @"SOFTWARE\voidtools\Everything 1.5a", @"SOFTWARE\voidtools\Everything-1.5a" })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(keyName);
                if (key?.GetValue("InstallLocation") is string directory && InstalledFile(directory) is { } executable) return executable;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        foreach (var folder in new[] { "Everything", "Everything 1.5a" })
            if (InstalledFile(Path.Combine(directory, folder)) is { } executable) return executable;
        return null;
    }

    private static string? InstalledFile(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) return null;
        var executable = Path.Combine(directory, "Everything.exe");
        return File.Exists(executable) ? executable : null;
    }

    public void StartClient(string executable)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-startup");
        using var process = Process.Start(start);
    }

    public async Task InstallAsync()
    {
        if (FindInstalledExecutable() is not null || FindRunningInstance() is not null) return;
        if (!CanInstall) throw new SearchAssistanceException("此开发目录没有安装组件，请使用完整 Catena 安装包。");
        try
        {
            await using (var payload = File.OpenRead(Installer))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(payload));
                if (hash != "C42EFAD041D4C0BB4D4AC97AE7CBE89F153EC1FE078772392E749C7F5D5282D3")
                    throw new SearchAssistanceException("搜索组件安装文件校验失败，请重新获取完整 Catena 安装包。");
            }
            using var process = Process.Start(new ProcessStartInfo(Installer)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "/S -install-options \"-app-data -disable-run-as-admin -install-service -install-start-menu-shortcuts -no-choose-volumes\""
            }) ?? throw new SearchAssistanceException("无法启动搜索组件安装程序。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || FindInstalledExecutable() is null)
                throw new SearchAssistanceException("搜索组件安装未完成，可重新点击安装。目录浏览和目录递归搜索仍可使用。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new SearchAssistanceException("已取消系统权限请求。需要时可再次点击「安装搜索组件」。"); }
        catch (Win32Exception) { throw new SearchAssistanceException("无法运行搜索组件安装程序，请使用完整 Catena 安装包重新安装。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new SearchAssistanceException("无法读取搜索组件安装文件，请使用完整 Catena 安装包修复。"); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);
}
