namespace Catena.Contracts;

public enum PreviewKind { Information, Text, Image, Native }
public sealed record FilePreview(PreviewKind Kind, string Text = "", byte[]? ImageBytes = null, string Notice = "", string NativePath = "", int PageCount = 1, int PageIndex = 0);
public interface IFilePreviewProvider
{
    Task<FilePreview> ReadAsync(FileEntry entry, CancellationToken token = default);
}
public interface IPagePreviewProvider : IFilePreviewProvider
{
    Task<FilePreview> ReadPageAsync(FileEntry entry, int page, CancellationToken token = default);
}

public interface IFallbackPreviewProvider : IPagePreviewProvider
{
    Task<FilePreview> ReadFallbackPageAsync(FileEntry entry, int page, CancellationToken token = default);
}
