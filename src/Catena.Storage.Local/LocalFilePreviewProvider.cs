using System.IO.Compression;
using System.Text;
using System.Xml;
using Catena.Contracts;

namespace Catena.Storage.Local;

public sealed class LocalFilePreviewProvider : IFilePreviewProvider
{
    public const int TextLimit = 65536;
    private const int ImageLimit = 16 * 1024 * 1024;
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".ico" };
    private static readonly HashSet<string> TextFiles = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".log", ".json", ".xml", ".yml", ".yaml", ".csv", ".tsv", ".ini", ".toml", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".rs", ".go", ".java", ".c", ".cpp", ".h", ".css", ".html", ".sql", ".ps1", ".sh", ".gitignore", ".slnx", ".csproj", ".props", ".axaml" };

    public Task<FilePreview> ReadAsync(FileEntry entry, CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (entry.IsDirectory) return new FilePreview(PreviewKind.Information);
        if (entry.Location.ProviderId != "local" || !Uri.TryCreate(entry.Location.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
            return new FilePreview(PreviewKind.Information, Notice: "此位置暂不支持预览。");
        var extension = Path.GetExtension(entry.Name);
        if (!Images.Contains(extension) && !TextFiles.Contains(extension) && !extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return new FilePreview(PreviewKind.Information, Notice: "暂无预览");
        try
        {
            // Online-only placeholders must not hydrate just because selection changed.
            var attributes = (uint)File.GetAttributes(uri.LocalPath);
            if ((attributes & (0x1000u | 0x40000u | 0x400000u)) != 0)
                return new FilePreview(PreviewKind.Information, Notice: "云端文件 · 使用默认应用打开");
            await using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
            if (Images.Contains(extension))
            {
                if (stream.Length > ImageLimit) return new FilePreview(PreviewKind.Information, Notice: "图片超过 16 MB，请使用默认应用查看。");
                using var output = new MemoryStream(); var chunk = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(chunk, token)) > 0)
                {
                    if (output.Length + read > ImageLimit) return new FilePreview(PreviewKind.Information, Notice: "图片超过预览大小限制。");
                    output.Write(chunk, 0, read);
                }
                return new FilePreview(PreviewKind.Image, ImageBytes: output.ToArray());
            }
            if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            {
                if (stream.Length > 32 * 1024 * 1024) return new FilePreview(PreviewKind.Information, Notice: "文档超过 32 MB，请使用默认应用查看。");
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var document = archive.GetEntry("word/document.xml");
                if (document is null || document.Length > 4 * 1024 * 1024) return new FilePreview(PreviewKind.Information, Notice: "无法预览此 Word 文档。");
                using var input = document.Open();
                using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var result = new StringBuilder();
                while (reader.Read() && result.Length < TextLimit)
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Text) result.Append(reader.Value);
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p") result.AppendLine();
                    else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "tab" or "br") result.Append(' ');
                }
                return new FilePreview(PreviewKind.Text, result.ToString(0, Math.Min(result.Length, TextLimit)), Notice: "正文节选");
            }
            using var textReader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            var chars = new char[TextLimit + 1]; var count = await textReader.ReadBlockAsync(chars, token);
            if (chars.AsSpan(0, count).Contains('\0')) return new FilePreview(PreviewKind.Information, Notice: "检测到二进制内容，请使用默认应用打开。");
            return new FilePreview(PreviewKind.Text, new string(chars, 0, Math.Min(count, TextLimit)), Notice: count > TextLimit ? "仅显示前 64K 字符" : "");
        }
        catch (DecoderFallbackException) { return new FilePreview(PreviewKind.Information, Notice: "文本编码暂不支持，请使用默认应用查看。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
        { return new FilePreview(PreviewKind.Information, Notice: "文件无法读取、已被移走或格式无效。"); }
    }, token);
}
