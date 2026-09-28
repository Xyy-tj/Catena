using Catena.Contracts;
using global::Windows.Data.Pdf;
using global::Windows.Storage;
using global::Windows.Storage.Streams;

namespace Catena.Platform.Windows;

public static class WindowsPdfPreview
{
    public static Task<FilePreview> ReadAsync(string path, int pageIndex, CancellationToken token) => Task.Run(async () =>
    {
        try
        {
            var info = new FileInfo(path);
            if (((uint)info.Attributes & (0x1000u | 0x40000u | 0x400000u)) != 0)
                return new FilePreview(PreviewKind.Information, Notice: "云端文件 · 使用默认应用打开");
            if (info.Length > 128 * 1024 * 1024) return new FilePreview(PreviewKind.Information, Notice: "PDF 超过 128 MB，请使用默认应用查看。");
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            var document = await PdfDocument.LoadFromFileAsync(file).AsTask(token);
            if (document.PageCount == 0) return new FilePreview(PreviewKind.Information, Notice: "PDF 没有可预览的页面。");
            var index = (uint)Math.Clamp(pageIndex, 0, document.PageCount - 1);
            using var page = document.GetPage(index);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1100 }).AsTask(token);
            if (stream.Size > 16 * 1024 * 1024) return new FilePreview(PreviewKind.Information, Notice: "PDF 页面超过预览大小限制。");
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size).AsTask(token);
            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            return new FilePreview(PreviewKind.Image, ImageBytes: bytes, PageCount: (int)document.PageCount, PageIndex: (int)index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        { return new FilePreview(PreviewKind.Information, Notice: "PDF 受密码保护、已损坏或暂不可读取。"); }
    }, token);
}
