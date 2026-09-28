using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Catena.App;

public sealed partial class ChatWindow : Window
{
    private ChatViewModel? Model => DataContext as ChatViewModel;
    private readonly CancellationTokenSource lifetime = new();
    public ChatWindow()
    {
        InitializeComponent();
        Opened += (_, _) => { if (Model is { } model) { model.Disposed += Close; model.ContentChanged += ScrollResponse; } Composer.Focus(); };
        Closed += (_, _) => { lifetime.Cancel(); if (Model is { } model) { model.Disposed -= Close; model.ContentChanged -= ScrollResponse; model.StopCommand.Execute(null); } };
        AddHandler(KeyDownEvent, PasteKeyDown, RoutingStrategies.Tunnel);
    }
    private void ScrollResponse()
    {
        if (ConversationScroll.Extent.Height - ConversationScroll.Viewport.Height - ConversationScroll.Offset.Y > 100) return;
        Dispatcher.UIThread.Post(() => { if (!lifetime.IsCancellationRequested) ConversationScroll.ScrollToEnd(); }, DispatcherPriority.Background);
    }
    private void UseSuggestion(object? sender, RoutedEventArgs e)
    { if (Model is { } model && sender is Button { Content: string text }) { model.Draft = text; Composer.Focus(); } }
    private async void ComposerKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Model is { } model) { e.Handled = true; await model.SendCommand.ExecuteAsync(null); } }
    private void RemoveAttachment(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: ChatAttachmentViewModel attachment }) Model?.RemoveAttachmentCommand.Execute(attachment); }
    private async void CopyMessage(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChatMessageViewModel message } || Clipboard is null) return;
        try { await Clipboard.SetTextAsync(message.Text); } catch (Exception) { if (Model is { } model) model.Status = "剪贴板暂不可用。"; }
    }
    private async void ChooseAttachments(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanCompose: true } model) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "添加附件", AllowMultiple = true });
            await AddFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception) { model.Status = "无法打开文件选择器，可使用 Ctrl+V 粘贴。"; }
    }
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (Model is not { CanCompose: true } model) return;
        model.IsLoadingAttachments = true; model.Status = "读取附件…";
        try
        {
            foreach (var path in paths.Take(5))
            {
                if (model.Attachments.Count >= 4) { model.Status = "每次最多添加 4 个附件。"; break; }
                var content = await Task.Run(() => ChatAttachmentLoader.FromFileAsync(path, lifetime.Token), lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                model.AddAttachment(content);
            }
            if (model.Status == "读取附件…") model.Status = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Catena.Contracts.FileSystemException or ArgumentException or NotSupportedException)
        { model.Status = "附件读取失败，请检查文件是否可用且不超过 8 MB。"; }
        finally { model.IsLoadingAttachments = false; }
    }
    private async void PasteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || e.KeyModifiers != KeyModifiers.Control || Model is not { CanCompose: true } model || Clipboard is null) return;
        e.Handled = true;
        try
        {
            var files = await Clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 }) { await AddFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>()); return; }
            using var bitmap = await Clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > 50_000_000) { model.Status = "图片尺寸过大。"; return; }
                model.IsLoadingAttachments = true;
                try
                {
                    using var bytes = new MemoryStream(); bitmap.Save(bytes, PngBitmapEncoderOptions.Default);
                    var attachment = await Task.Run(() => ChatAttachmentLoader.Image("剪贴板图片.png", bytes.ToArray()), lifetime.Token);
                    if (!lifetime.IsCancellationRequested) model.AddAttachment(attachment);
                }
                finally { model.IsLoadingAttachments = false; }
                return;
            }
            var text = await Clipboard.TryGetTextAsync();
            if (text is not null && !lifetime.IsCancellationRequested)
            {
                var start = Math.Min(Composer.SelectionStart, Composer.SelectionEnd); var end = Math.Max(Composer.SelectionStart, Composer.SelectionEnd);
                model.Draft = model.Draft[..start] + text + model.Draft[end..]; Composer.CaretIndex = start + text.Length; Composer.Focus();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { model.Status = "无法粘贴，请用附件按钮选择文件。"; }
    }
}
