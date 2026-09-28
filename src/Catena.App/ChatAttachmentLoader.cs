using Avalonia.Media.Imaging;
using Catena.Contracts;
using Catena.Storage.Local;

namespace Catena.App;

public sealed class ChatAttachmentViewModel(AiAttachment content) : IDisposable
{
    public AiAttachment Content { get; } = content;
    public string Name => Content.Name;
    public string Label => Content.Kind switch { "image" => "图片", "pdf" => "PDF · 需模型支持", "text" => "正文节选", _ => "仅属性" };
    public bool HasThumbnail => Thumbnail is not null;
    public Bitmap? Thumbnail { get; } = content.Kind == "image" ? new Bitmap(new MemoryStream(Convert.FromBase64String(content.DataUrl.Split(',')[1]))) : null;
    public void Dispose() => Thumbnail?.Dispose();
}

public static class ChatAttachmentLoader
{
    public const int FileLimit = 8 * 1024 * 1024;
    public static async Task<AiAttachment> FromFileAsync(string path, CancellationToken token = default)
    {
        var provider = new LocalFileSystemProvider();
        var entry = await provider.GetEntryAsync(provider.Normalize(path), token);
        var attributes = File.GetAttributes(path);
        var metadata = new AiAttachment(entry.Name, "metadata", $"名称：{entry.Name}\n类型：{entry.TypeText}\n大小：{entry.SizeText}\n修改：{entry.ModifiedText}");
        if (entry.IsDirectory || ((uint)attributes & (0x1000 | 0x40000 | 0x400000)) != 0) return metadata;
        if (new FileInfo(path).Length > FileLimit) throw new IOException("附件超过 8 MB。");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
            return await Task.Run(async () => Image(entry.Name, await ReadBoundedAsync(path, token)), token);
        if (extension == ".pdf") return new(entry.Name, "pdf", DataUrl: "data:application/pdf;base64," + Convert.ToBase64String(await ReadBoundedAsync(path, token)));
        var preview = await new LocalFilePreviewProvider().ReadAsync(entry, token);
        return preview.Kind == PreviewKind.Text ? new(entry.Name, "text", preview.Text[..Math.Min(16000, preview.Text.Length)]) : metadata;
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path); using var output = new MemoryStream(); var buffer = new byte[65536];
        int read; while ((read = await stream.ReadAsync(buffer, token)) > 0)
        { if (output.Length + read > FileLimit) throw new IOException("附件超过 8 MB。"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    public static AiAttachment Image(string name, byte[] bytes)
    {
        using var encoded = new SkiaSharp.SKMemoryStream(bytes); using var codec = SkiaSharp.SKCodec.Create(encoded);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 50_000_000)
            throw new IOException("图片尺寸过大或格式无效。");
        var scale = Math.Min(1, 1536d / Math.Max(codec.Info.Width, codec.Info.Height));
        using var input = new MemoryStream(bytes);
        using var bitmap = Bitmap.DecodeToWidth(input, Math.Max(1, (int)(codec.Info.Width * scale)));
        using var output = new MemoryStream(); bitmap.Save(output, PngBitmapEncoderOptions.Default);
        if (output.Length > FileLimit) throw new IOException("图片过大，请缩小后重试。");
        return new(name, "image", DataUrl: "data:image/png;base64," + Convert.ToBase64String(output.ToArray()));
    }
}

