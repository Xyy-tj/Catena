using System.Net;
using System.Text.Json;

namespace Catena.App;

public sealed record AppRelease(Version Version, Uri Page);
public interface IUpdateChecker
{
    Task<AppRelease?> CheckAsync(CancellationToken token = default);
}

public sealed class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    public static readonly Uri ReleasesUri = new("https://github.com/Xyy-tj/Catena/releases");
    private readonly HttpClient client;
    public GitHubUpdateChecker() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    public GitHubUpdateChecker(HttpMessageHandler handler)
    {
        client = new(handler) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Catena-UpdateCheck/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public async Task<AppRelease?> CheckAsync(CancellationToken token = default)
    {
        using var response = await client.GetAsync("https://api.github.com/repos/Xyy-tj/Catena/releases/latest", token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) throw new InvalidDataException("无法识别发布版本号。");
        // Construct the link from our fixed repository, never launch a URL supplied by the response.
        return new(version!, new Uri(ReleasesUri + "/tag/" + Uri.EscapeDataString(tag)));
    }
    public static bool TryParseVersion(string tag, out Version? version)
    {
        var text = tag.Trim().TrimStart('v', 'V').Split('+')[0];
        if (Version.TryParse(text, out var parsed) && parsed.Build >= 0)
        { version = new(parsed.Major, parsed.Minor, parsed.Build, Math.Max(0, parsed.Revision)); return true; }
        version = null; return false;
    }
    public void Dispose() => client.Dispose();
}
