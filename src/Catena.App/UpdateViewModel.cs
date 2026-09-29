using System.Net;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class UpdateViewModel(IUpdateChecker checker) : ObservableObject, IDisposable
{
    private readonly DispatcherTimer timer = new();
    private CancellationTokenSource? pending;
    private bool started;
    private bool disposed;
    private bool automatic;
    private DateTimeOffset? lastAttempt;
    public Version CurrentVersion { get; } = typeof(App).Assembly.GetName().Version ?? new(0, 0, 0, 0);
    public string CurrentVersionText => "当前版本 " + CurrentVersion.ToString(3);
    [ObservableProperty] private string status = "尚未检查更新";
    [ObservableProperty] private string lastCheckedText = "";
    [ObservableProperty] private bool isChecking;
    [ObservableProperty] private bool isUpdateAvailable;
    public Uri ReleasePage { get; private set; } = GitHubUpdateChecker.ReleasesUri;
    public void SetAutomatic(bool enabled)
    {
        if (disposed) return;
        automatic = enabled;
        if (!started) { timer.Tick += OnTimer; started = true; }
        timer.Stop();
        if (enabled) { timer.Interval = TimeSpan.FromSeconds(10); timer.Start(); }
    }
    private async void OnTimer(object? sender, EventArgs e)
    {
        timer.Interval = TimeSpan.FromHours(24);
        if (automatic && !disposed && (lastAttempt is null || DateTimeOffset.UtcNow - lastAttempt >= TimeSpan.FromHours(24)))
            await CheckAsync();
    }
    [RelayCommand] private async Task CheckAsync()
    {
        if (disposed || IsChecking) return;
        using var cancellation = new CancellationTokenSource(); pending = cancellation;
        IsChecking = true; Status = "正在检查更新…"; lastAttempt = DateTimeOffset.UtcNow;
        try
        {
            var release = await checker.CheckAsync(cancellation.Token);
            if (disposed) return;
            LastCheckedText = "上次检查 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            IsUpdateAvailable = release is not null && release.Version > CurrentVersion;
            ReleasePage = release?.Page ?? GitHubUpdateChecker.ReleasesUri;
            Status = release is null ? "暂无正式发布版本" : IsUpdateAvailable ? "发现新版本 " + release.Version.ToString(3) : "当前已是最新版本";
        }
        catch (HttpRequestException ex)
        {
            if (!disposed) Status = ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                ? "GitHub 暂时拒绝请求或限制频率，请稍后重试" : "无法连接 GitHub，请检查网络后重试";
        }
        catch (OperationCanceledException) { if (!disposed) Status = "检查超时，请稍后重试"; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { if (!disposed) Status = "版本信息暂不可用，请稍后重试"; }
        finally { pending = null; if (!disposed) IsChecking = false; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); timer.Tick -= OnTimer; pending?.Cancel();
    }
}
