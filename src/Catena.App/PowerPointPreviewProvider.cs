using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Catena.Contracts;
using SkiaSharp;

namespace Catena.App;

// A bounded Open XML preview: text, ordinary shapes and embedded pictures, without starting Office.
public sealed class PowerPointPreviewProvider(IFilePreviewProvider fallback) : IPagePreviewProvider
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public Task<FilePreview> ReadAsync(FileEntry entry, CancellationToken token = default) => ReadPageAsync(entry, 0, token);
    public Task<FilePreview> ReadPageAsync(FileEntry entry, int page, CancellationToken token = default)
    {
        if (entry.IsDirectory || Path.GetExtension(entry.Name).ToLowerInvariant() is not (".pptx" or ".pptm"))
            return fallback is IPagePreviewProvider pages ? pages.ReadPageAsync(entry, page, token) : fallback.ReadAsync(entry, token);
        return Task.Run(() => Render(entry, page, token), token);
    }
    private static FilePreview Render(FileEntry entry, int index, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(entry.DisplayPath);
            if (((uint)info.Attributes & (0x1000u | 0x40000u | 0x400000u)) != 0)
                return new(PreviewKind.Information, Notice: "云端文件 · 使用默认应用打开");
            if (info.Length > 128 * 1024 * 1024) return new(PreviewKind.Information, Notice: "演示文稿超过 128 MB，请使用默认应用查看。");
            using var file = new FileStream(entry.DisplayPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var presentation = ReadXml(zip, "ppt/presentation.xml");
            var relations = Relationships(zip, "ppt/_rels/presentation.xml.rels", "ppt/");
            var slides = presentation.Descendants(P + "sldId").Select(e => (string?)e.Attribute(R + "id")).Where(id => id is not null && relations.ContainsKey(id)).Select(id => relations[id!]).ToArray();
            if (slides.Length == 0) return new(PreviewKind.Information, Notice: "演示文稿没有幻灯片。");
            index = Math.Clamp(index, 0, slides.Length - 1);
            var size = presentation.Descendants(P + "sldSz").FirstOrDefault();
            var width = Number(size, "cx", 12192000); var height = Number(size, "cy", 6858000);
            if (width <= 0 || height <= 0 || height / width is < .1 or > 4) throw new InvalidDataException();
            const int pixels = 1100; var scale = pixels / width;
            var slide = ReadXml(zip, slides[index]);
            var folder = slides[index][..(slides[index].LastIndexOf('/') + 1)];
            var images = Relationships(zip, folder + "_rels/" + Path.GetFileName(slides[index]) + ".rels", folder);
            using var surface = SKSurface.Create(new SKImageInfo(pixels, (int)(height * scale)));
            var canvas = surface.Canvas;
            canvas.Clear(Color(slide.Descendants(P + "bgPr").FirstOrDefault()?.Element(A + "solidFill"), SKColors.White));
            var fallbackY = 55f;
            foreach (var shape in slide.Descendants(P + "spTree").First().Elements().Take(300))
            {
                token.ThrowIfCancellationRequested();
                if (shape.Name != P + "sp" && shape.Name != P + "pic") continue;
                var properties = shape.Element(P + "spPr"); var transform = properties?.Element(A + "xfrm");
                var offset = transform?.Element(A + "off"); var extent = transform?.Element(A + "ext");
                var x = (float)(Number(offset, "x", 50 / scale) * scale); var y = (float)(Number(offset, "y", fallbackY / scale) * scale);
                var w = (float)(Number(extent, "cx", 1000 / scale) * scale); var h = (float)(Number(extent, "cy", 100 / scale) * scale);
                if (!float.IsFinite(x + y + w + h) || w <= 0 || h <= 0) continue;
                var rect = SKRect.Create(x, y, w, h);
                using var paint = new SKPaint { IsAntialias = true };
                if (shape.Name == P + "pic")
                {
                    var id = (string?)shape.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
                    if (id is not null && images.TryGetValue(id, out var imageName) && zip.GetEntry(imageName) is { Length: < 16 * 1024 * 1024 } imageEntry)
                    {
                        using var input = imageEntry.Open(); using var memory = new MemoryStream(); input.CopyTo(memory);
                        using var data = SKData.CreateCopy(memory.ToArray()); using var codec = SKCodec.Create(data);
                        if (codec is not null && (long)codec.Info.Width * codec.Info.Height <= 20_000_000)
                        { using var bitmap = SKBitmap.Decode(codec); if (bitmap is not null) canvas.DrawBitmap(bitmap, rect); }
                    }
                    continue;
                }
                if (properties?.Element(A + "solidFill") is { } fill)
                {
                    paint.Color = Color(fill, SKColors.Transparent);
                    var geometry = (string?)properties.Element(A + "prstGeom")?.Attribute("prst");
                    if (geometry == "roundRect") canvas.DrawRoundRect(rect, Math.Min(w, h) * .08f, Math.Min(w, h) * .08f, paint);
                    else if (geometry == "ellipse") canvas.DrawOval(rect, paint);
                    else canvas.DrawRect(rect, paint);
                }
                var text = shape.Element(P + "txBody"); if (text is null) continue;
                var cursor = y + 5;
                canvas.Save(); canvas.ClipRect(rect);
                foreach (var paragraph in text.Elements(A + "p"))
                {
                    var value = string.Concat(paragraph.Descendants(A + "t").Select(t => t.Value));
                    if (value.Length > 8192) value = value[..8192];
                    var formatting = paragraph.Descendants(A + "rPr").FirstOrDefault() ?? paragraph.Descendants(A + "defRPr").FirstOrDefault();
                    var fontPixels = (float)Math.Clamp(Number(formatting, "sz", 2200) / 100 * 12700 * scale, 8, 100);
                    using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", (string?)formatting?.Attribute("b") == "1" ? SKFontStyle.Bold : SKFontStyle.Normal);
                    using var font = new SKFont(typeface, fontPixels);
                    paint.Color = Color(formatting?.Element(A + "solidFill"), new SKColor(32, 43, 62));
                    var align = (string?)paragraph.Element(A + "pPr")?.Attribute("algn");
                    foreach (var line in Wrap(value, font, w - 12))
                    {
                        cursor += fontPixels * 1.22f;
                        if (cursor > y + h) break;
                        var textWidth = font.MeasureText(line);
                        var left = align == "ctr" ? x + (w - textWidth) / 2 : align == "r" ? x + w - textWidth - 6 : x + 6;
                        canvas.DrawText(line, left, cursor, SKTextAlign.Left, font, paint);
                    }
                }
                canvas.Restore(); fallbackY = Math.Max(fallbackY, y + h + 12);
            }
            using var snapshot = surface.Snapshot(); using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return new(PreviewKind.Image, ImageBytes: png.ToArray(), Notice: "兼容预览 · 图表、组合对象和母版可能缺失，可打开原文件查看", PageCount: slides.Length, PageIndex: index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException or ArgumentException)
        { return new(PreviewKind.Information, Notice: "演示文稿受密码保护、已损坏或暂不可读取。"); }
    }
    private static IEnumerable<string> Wrap(string text, SKFont font, float width)
    {
        var line = "";
        foreach (var rune in text.EnumerateRunes())
        {
            if (line.Length > 0 && font.MeasureText(line + rune) > width) { yield return line; line = ""; }
            line += rune.ToString();
        }
        if (line.Length > 0) yield return line;
    }
    private static double Number(XElement? element, string name, double fallback) => double.TryParse((string?)element?.Attribute(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static SKColor Color(XElement? fill, SKColor fallback)
    { var value = (string?)fill?.Element(A + "srgbClr")?.Attribute("val"); return value is not null && SKColor.TryParse("#" + value, out var color) ? color : fallback; }
    private static XDocument ReadXml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException();
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException();
        using var stream = entry.Open(); using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
    private static Dictionary<string, string> Relationships(ZipArchive zip, string name, string folder)
    {
        if (zip.GetEntry(name) is null) return [];
        return ReadXml(zip, name).Root!.Elements().Where(e => (string?)e.Attribute("TargetMode") != "External")
            .Where(e => e.Attribute("Id") is not null && e.Attribute("Target") is not null)
            .ToDictionary(e => (string)e.Attribute("Id")!, e => new Uri(new Uri("https://fixture/" + folder), (string)e.Attribute("Target")!).AbsolutePath.TrimStart('/'));
    }
}
