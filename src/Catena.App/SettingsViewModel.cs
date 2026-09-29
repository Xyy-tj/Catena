using System.Security.Cryptography;
using Catena.AI;
using Catena.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class SettingsViewModel(IAppSettingsStore store, ISecretProtector protector,
    IAiSearchPlanner planner, IIndexedSearchProvider everything, IEverythingRuntime? runtime = null, UpdateViewModel? updates = null) : ObservableObject, IDisposable
{
    public UpdateViewModel? Updates { get; } = updates;
    private CancellationTokenSource? pending;
    private bool loadFailed;
    public AppSettings Current { get; private set; } = new();
    public event Action? Saved;
    [ObservableProperty] private string everythingExecutable = "";
    [ObservableProperty] private string everythingInstance = "";
    [ObservableProperty] private bool liveSearch = true;
    [ObservableProperty] private bool autoCheckUpdates = true;
    [ObservableProperty] private int defaultSearchScope = 2;
    [ObservableProperty] private int searchResultLimit = 200;
    [ObservableProperty] private int theme;
    [ObservableProperty] private int skin;
    [ObservableProperty] private double backgroundOpacity = .3;
    [ObservableProperty] private string aiBaseUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string aiModel = "";
    [ObservableProperty] private string apiKey = "";
    [ObservableProperty] private bool clearApiKey;
    [ObservableProperty] private int timeoutSeconds = 30;
    [ObservableProperty] private string status = "";
    public bool HasStatus => Status.Length > 0;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    [ObservableProperty] private bool isTesting;
    [ObservableProperty] private bool isInstalling;
    public bool CanEdit => !IsTesting && !IsInstalling && !loadFailed;
    public bool CanInstallSearch => runtime?.CanInstall == true && !IsTesting && !IsInstalling;
    public string KeyPlaceholder => Current.ProtectedApiKey.Length > 0 ? "已加密保存；留空保留原 Key" : "API Key（本机无鉴权服务可留空）";
    partial void OnIsTestingChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanInstallSearch)); }
    partial void OnIsInstallingChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanInstallSearch)); }
    public async Task LoadAsync()
    {
        try { Current = await store.LoadAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { loadFailed = true; Status = "设置文件损坏或版本不兼容，已保留原文件。请备份并修复 settings.json 后重启。"; }
        ResetDraft(); OnPropertyChanged(nameof(CanEdit));
        Updates?.SetAutomatic(!loadFailed && Current.AutoCheckUpdates);
    }
    public void ResetDraft()
    {
        EverythingExecutable = Current.EverythingExecutable; EverythingInstance = Current.EverythingInstance;
        LiveSearch = Current.LiveSearch; AiBaseUrl = Current.AiBaseUrl; AiModel = Current.AiModel;
        AutoCheckUpdates = Current.AutoCheckUpdates;
        DefaultSearchScope = Current.DefaultSearchScope; SearchResultLimit = Current.SearchResultLimit;
        Theme = Current.Theme; Skin = Current.Skin; BackgroundOpacity = Current.BackgroundOpacity;
        TimeoutSeconds = Current.AiTimeoutSeconds; ApiKey = ""; ClearApiKey = false; OnPropertyChanged(nameof(KeyPlaceholder));
    }
    private AppSettings Draft()
    {
        var endpoint = OpenAiSearchPlanner.CompletionEndpoint(AiBaseUrl);
        if (TimeoutSeconds is < 5 or > 120) throw new SearchAssistanceException("超时应为 5–120 秒。");
        if (SearchResultLimit is < 50 or > 1000) throw new SearchAssistanceException("结果上限应为 50–1000 项。");
        if (EverythingInstance.Any(char.IsControl)) throw new SearchAssistanceException("Everything 实例名不能包含控制字符。");
        if (ApiKey.Length > 0 && ApiKey.Any(char.IsControl)) throw new SearchAssistanceException("API Key 不能包含换行或控制字符。");
        if (!ClearApiKey && ApiKey.Length == 0 && Current.ProtectedApiKey.Length > 0 &&
            endpoint.GetLeftPart(UriPartial.Authority) != OpenAiSearchPlanner.CompletionEndpoint(Current.AiBaseUrl).GetLeftPart(UriPartial.Authority))
            throw new SearchAssistanceException("接口主机已更改，请为新服务重新填写 API Key 或勾选清除，避免沿用旧服务密钥。");
        return Current with
        {
            EverythingExecutable = EverythingExecutable.Trim().Trim('"'), EverythingInstance = EverythingInstance.Trim(), LiveSearch = LiveSearch,
            AutoCheckUpdates = AutoCheckUpdates,
            DefaultSearchScope = DefaultSearchScope, SearchResultLimit = SearchResultLimit,
            Theme = Theme, Skin = Skin, BackgroundOpacity = BackgroundOpacity,
            AiBaseUrl = AiBaseUrl.Trim().TrimEnd('/'), AiModel = AiModel.Trim(), AiTimeoutSeconds = TimeoutSeconds,
            ProtectedApiKey = ClearApiKey ? "" : ApiKey.Length > 0 ? protector.Protect(ApiKey.Trim()) : Current.ProtectedApiKey
        };
    }
    public AiConnection Connection(AppSettings? settings = null)
    {
        settings ??= Current;
        try { return new() { BaseUrl = settings.AiBaseUrl, Model = settings.AiModel, ApiKey = protector.Unprotect(settings.ProtectedApiKey), TimeoutSeconds = settings.AiTimeoutSeconds }; }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
        { throw new SearchAssistanceException("无法解密 API Key，请在当前 Windows 用户下重新填写并保存。"); }
    }
    [RelayCommand] private async Task SaveAsync()
    {
        if (!CanEdit) return;
        try
        {
            var draft = Draft(); await store.SaveAsync(draft); Current = draft; ResetDraft();
            Updates?.SetAutomatic(Current.AutoCheckUpdates);
            Status = "已保存"; Saved?.Invoke();
        }
        catch (SearchAssistanceException ex) { Status = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or PlatformNotSupportedException)
        { Status = "保存失败，请检查目录权限或 Windows 密钥存储；原设置仍有效。"; }
    }
    [RelayCommand] private async Task TestAiAsync()
    {
        if (IsTesting) return;
        using var cancellation = new CancellationTokenSource(); pending = cancellation; IsTesting = true;
        try
        {
            Status = "正在测试模型的搜索条件解析…";
            var plan = await planner.PlanAsync("查找最近七天修改的 PDF 文件", Connection(Draft()), cancellation.Token);
            Status = "模型连接成功 · " + plan.Explanation;
        }
        catch (SearchAssistanceException ex) { Status = ex.Message; }
        catch (OperationCanceledException) { Status = "测试已取消。"; }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException) { Status = "无法使用系统密钥存储，请检查当前用户环境。"; }
        finally { pending = null; IsTesting = false; }
    }
    [RelayCommand] private async Task TestEverythingAsync()
    {
        if (IsTesting) return;
        using var cancellation = new CancellationTokenSource(); pending = cancellation; IsTesting = true;
        try
        {
            Status = "正在连接 Everything 索引…";
            var result = await everything.SearchAsync(new(new() { Terms = ["__catena_connection_probe__"] }, Limit: 1), EverythingExecutable.Trim().Trim('"'), EverythingInstance.Trim(), cancellation.Token);
            Status = $"Everything 已连接 · {result.Elapsed.TotalMilliseconds:N0} ms";
        }
        catch (SearchAssistanceException ex) { Status = ex.Message; }
        catch (OperationCanceledException) { Status = "测试已取消。"; }
        finally { pending = null; IsTesting = false; }
    }
    [RelayCommand] private void CancelTest() => pending?.Cancel();
    [RelayCommand] private async Task InstallSearchAsync()
    {
        if (!CanInstallSearch || runtime is null) return;
        IsInstalling = true;
        try
        {
            Status = "正在安装搜索组件，请在系统提示中允许安装。安装过程无需填写路径或模型设置。";
            await runtime.InstallAsync();
            await runtime.EnsureRunningAsync();
            Status = "搜索组件已启动。首次索引可能需要一些时间，完成后即可查找。";
        }
        catch (SearchAssistanceException ex) { Status = ex.Message; }
        finally { IsInstalling = false; }
    }
    public void Dispose() { pending?.Cancel(); Updates?.Dispose(); }
}
