using Avalonia.Media.Imaging;
using Catena.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class PreviewViewModel(IFilePreviewProvider provider, IPlatformActions platform, Action<FileEntry>? opened = null) : ObservableObject, IDisposable
{
    private CancellationTokenSource? pending;
    private long revision;
    [ObservableProperty] private FileEntry? entry;
    [ObservableProperty] private string text = "";
    [ObservableProperty] private string notice = "选择一个文件查看预览";
    [ObservableProperty] private Bitmap? image;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string nativePath = "";
    [ObservableProperty] private int pageCount;
    [ObservableProperty] private int pageIndex;
    public bool HasPages => PageCount > 1;
    public string PageLabel => $"{PageIndex + 1} / {PageCount}";
    public bool CanPreviousPage => !IsLoading && PageIndex > 0;
    public bool CanNextPage => !IsLoading && PageIndex + 1 < PageCount;
    partial void OnPageCountChanged(int value) => NotifyPages();
    partial void OnPageIndexChanged(int value) => NotifyPages();
    partial void OnIsLoadingChanged(bool value) => NotifyPages();
    private void NotifyPages() { OnPropertyChanged(nameof(HasPages)); OnPropertyChanged(nameof(PageLabel)); OnPropertyChanged(nameof(CanPreviousPage)); OnPropertyChanged(nameof(CanNextPage)); }
    [RelayCommand] private Task PreviousPageAsync() => CanPreviousPage ? ShowAsync(Entry, PageIndex - 1) : Task.CompletedTask;
    [RelayCommand] private Task NextPageAsync() => CanNextPage ? ShowAsync(Entry, PageIndex + 1) : Task.CompletedTask;
    public bool HasNative => NativePath.Length > 0;
    partial void OnNativePathChanged(string value) { OnPropertyChanged(nameof(HasNative)); OnPropertyChanged(nameof(IsInformation)); }
    public bool HasText => Text.Length > 0;
    public bool HasImage => Image is not null;
    public bool HasEntry => Entry is not null;
    public bool IsInformation => !HasText && !HasImage && !HasNative;
    public string FullSize => Entry?.Size is { } size ? $"{Entry.SizeText}（{size:N0} 字节）" : "—";
    partial void OnTextChanged(string value) { OnPropertyChanged(nameof(HasText)); OnPropertyChanged(nameof(IsInformation)); }
    partial void OnImageChanged(Bitmap? value) { OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(IsInformation)); }
    partial void OnEntryChanged(FileEntry? value) { OnPropertyChanged(nameof(HasEntry)); OnPropertyChanged(nameof(FullSize)); }

    public async Task ShowAsync(FileEntry? selected, int page = 0)
    {
        var previous = pending; pending = null; previous?.Cancel();
        var current = ++revision;
        var oldImage = Image; Image = null; oldImage?.Dispose();
        NativePath = ""; Entry = selected; Text = ""; PageCount = 0; PageIndex = page; Notice = selected is null ? "选择一个文件查看预览" : "正在读取预览…"; IsLoading = selected is not null;
        if (selected is null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8)); pending = cancellation;
        Bitmap? decoded = null;
        try
        {
            // Debounce rapid keyboard selection; decoding never blocks the UI thread.
            await Task.Delay(100, cancellation.Token);
            var content = await (provider is IPagePreviewProvider paged ? paged.ReadPageAsync(selected, page, cancellation.Token) : provider.ReadAsync(selected, cancellation.Token)).WaitAsync(cancellation.Token);
            if (content.ImageBytes is { } bytes)
            {
                var decoding = Task.Run(() => DecodeImage(bytes));
                try { decoded = await decoding.WaitAsync(cancellation.Token); }
                catch (OperationCanceledException)
                {
                    _ = decoding.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
                    throw;
                }
            }
            if (revision != current) return;
            Text = content.Text; Notice = content.Notice; Image = decoded; decoded = null; NativePath = content.NativePath;
            PageIndex = content.PageIndex; PageCount = content.PageCount;
        }
        catch (OperationCanceledException) { if (revision == current) Notice = "预览读取超时，请使用默认应用打开。"; }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        { if (revision == current) Notice = "无法显示此文件的预览，请使用默认应用打开。"; }
        finally { decoded?.Dispose(); if (revision == current) { pending = null; IsLoading = false; } }
    }
    private static Bitmap DecodeImage(byte[] bytes)
    {
        using var encoded = new SkiaSharp.SKMemoryStream(bytes);
        using var codec = SkiaSharp.SKCodec.Create(encoded);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            (long)codec.Info.Width * codec.Info.Height > 50_000_000 || Math.Max(codec.Info.Width, codec.Info.Height) > 32768)
            throw new NotSupportedException("Image exceeds preview dimensions.");
        var scale = Math.Min(1, 1024d / Math.Max(codec.Info.Width, codec.Info.Height));
        using var stream = new MemoryStream(bytes);
        return Bitmap.DecodeToWidth(stream, Math.Max(1, (int)(codec.Info.Width * scale)));
    }
    [RelayCommand] private void Open()
    {
        if (Entry is not { } selected) return;
        try { platform.Open(selected.Location); opened?.Invoke(selected); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        { Notice = "无法打开，请检查默认应用关联。"; }
    }
    public void Dispose() { revision++; var previous = pending; pending = null; previous?.Cancel(); NativePath = ""; var bitmap = Image; Image = null; bitmap?.Dispose(); }
    [RelayCommand] private void Reveal() => RunAction(() => { if (Entry is { } entry) platform.Reveal(entry.Location); });
    [RelayCommand] private void Terminal() => RunAction(() =>
    {
        if (Entry is not { } entry) return;
        var path = entry.IsDirectory ? entry.DisplayPath : Path.GetDirectoryName(entry.DisplayPath)!;
        platform.OpenTerminal(new("local", new Uri(path).AbsoluteUri));
    });
    private void RunAction(Action action)
    { try { action(); } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { Notice = "无法打开此位置。"; } }
}
