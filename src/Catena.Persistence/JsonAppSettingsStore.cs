using System.Text.Json;
using Catena.Contracts;

namespace Catena.Persistence;

public sealed class JsonAppSettingsStore(string path) : IAppSettingsStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<AppSettings> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path, token)) ?? throw new InvalidDataException("设置文件为空。");
        Validate(settings); return settings;
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken token = default)
    {
        Validate(settings); await gate.WaitAsync(token);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
    private static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != 1 || settings.AiBaseUrl is null || settings.AiModel is null ||
            settings.EverythingExecutable is null || settings.EverythingInstance is null || settings.ProtectedApiKey is null ||
            settings.AiTimeoutSeconds is < 5 or > 120 || settings.DefaultSearchScope is < 0 or > 2 ||
            settings.SearchResultLimit is < 50 or > 1000 || settings.Theme is < 0 or > 2 || settings.Skin is < 0 or > 1 ||
            !double.IsFinite(settings.BackgroundOpacity) || settings.BackgroundOpacity is < 0 or > 1)
            throw new InvalidDataException("设置文件版本或内容无效，原文件已保留。");
    }
}
