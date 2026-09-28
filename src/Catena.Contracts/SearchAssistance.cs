namespace Catena.Contracts;

public sealed record FileSearchPlan
{
    public string[] Terms { get; init; } = [];
    public string[] Extensions { get; init; } = [];
    public string Kind { get; init; } = "any";
    public DateOnly? ModifiedFrom { get; init; }
    public DateOnly? ModifiedBefore { get; init; }
    public long? MinimumBytes { get; init; }
    public long? MaximumBytes { get; init; }
    public string Explanation { get; init; } = "";

    public void Validate()
    {
        if (Terms is null || Terms.Length > 8 || Terms.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 100 || t.Any(char.IsControl)) ||
            Extensions is null || Extensions.Length > 12 || Extensions.Any(e => string.IsNullOrEmpty(e) || e.Length > 16 || !e.All(char.IsAsciiLetterOrDigit)) ||
            Kind is not ("any" or "file" or "folder") || MinimumBytes < 0 || MaximumBytes < 0 || MinimumBytes > MaximumBytes ||
            ModifiedFrom >= ModifiedBefore || Explanation is null || Explanation.Length > 1000 ||
            (Terms.Length == 0 && Extensions.Length == 0 && Kind == "any" && ModifiedFrom is null && ModifiedBefore is null && MinimumBytes is null && MaximumBytes is null))
            throw new SearchAssistanceException("搜索条件不完整或无效，请补充文件名、类型或日期。不会执行无条件全盘搜索。");
    }
}

public sealed class SearchAssistanceException(string message) : Exception(message);
public sealed class AiConnection
{
    public required string BaseUrl { get; init; }
    public required string Model { get; init; }
    public string ApiKey { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 30;
    public override string ToString() => "AI connection (credentials omitted)";
}
public interface IAiSearchPlanner
{
    Task<FileSearchPlan> PlanAsync(string description, AiConnection connection, CancellationToken token = default);
}

public sealed record IndexedSearchRequest(FileSearchPlan Plan, Location? Scope = null, int Limit = 200, IReadOnlyList<Location>? Scopes = null);
public sealed record IndexedSearchResponse(IReadOnlyList<FileEntry> Entries, bool LimitReached, string Query, TimeSpan Elapsed);
public interface IIndexedSearchProvider
{
    Task<IndexedSearchResponse> SearchAsync(IndexedSearchRequest request, string executablePath, string instanceName, CancellationToken token = default);
}

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string EverythingExecutable { get; init; } = "";
    public string EverythingInstance { get; init; } = "";
    public bool LiveSearch { get; init; } = true;
    public int DefaultSearchScope { get; init; } = 2;
    public int SearchResultLimit { get; init; } = 200;
    public int Theme { get; init; }
    public int Skin { get; init; }
    public double BackgroundOpacity { get; init; } = .3;
    public string AiBaseUrl { get; init; } = "https://api.openai.com/v1";
    public string AiModel { get; init; } = "";
    public string ProtectedApiKey { get; init; } = "";
    public int AiTimeoutSeconds { get; init; } = 30;
}
public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken token = default);
    Task SaveAsync(AppSettings settings, CancellationToken token = default);
}
public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public interface IEverythingRuntime
{
    Task<string> EnsureRunningAsync(CancellationToken token = default);
    Task InstallAsync();
    bool CanInstall { get; }
}
