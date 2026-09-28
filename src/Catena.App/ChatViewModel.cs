using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using Catena.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Catena.App;

public sealed partial class ChatMessageViewModel(string role, string text, IReadOnlyList<ChatAttachmentViewModel>? attachments = null) : ObservableObject
{
    public string Role { get; } = role;
    public bool IsUser => Role == "user";
    public string Author => IsUser ? "你" : "Catena";
    [ObservableProperty] private string text = text;
    public IReadOnlyList<ChatAttachmentViewModel> Attachments { get; } = attachments ?? [];
    public AiChatMessage Snapshot() => new(Role, Text, Attachments.Select(a => a.Content).ToArray());
}

public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly IAiChatClient? client;
    private readonly SettingsViewModel? settings;
    private readonly string context;
    private readonly DispatcherTimer typing = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly StringBuilder buffer = new();
    private CancellationTokenSource? pending;
    private ChatMessageViewModel? output;
    private bool disposed;
    public string Folder { get; }
    public string ModelName => settings?.Current.AiModel is { Length: > 0 } name ? name : "未配置模型";
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<ChatAttachmentViewModel> Attachments { get; } = [];
    [ObservableProperty] private string draft = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isSending;
    [ObservableProperty] private bool isLoadingAttachments;
    [ObservableProperty] private bool includeContext = true;
    public bool IsEmpty => Messages.Count == 0;
    public bool CanCompose => !IsSending && !IsLoadingAttachments;
    public event Action? ContentChanged;
    public event Action? Disposed;
    partial void OnIsSendingChanged(bool value) => OnPropertyChanged(nameof(CanCompose));
    partial void OnIsLoadingAttachmentsChanged(bool value) => OnPropertyChanged(nameof(CanCompose));
    public ChatViewModel(IAiChatClient? client, SettingsViewModel? settings, string folder, string context)
    {
        this.client = client; this.settings = settings; Folder = folder; this.context = context;
        typing.Tick += (_, _) => Drain(false);
        if (settings is not null) settings.Saved += SettingsChanged;
    }
    private void SettingsChanged() => OnPropertyChanged(nameof(ModelName));
    public void AddAttachment(AiAttachment attachment)
    {
        if (disposed || IsSending) return;
        if (Attachments.Count >= 4) { Status = "每次最多添加 4 个附件。"; return; }
        if (Attachments.Sum(a => a.Content.DataUrl.Length) + attachment.DataUrl.Length > 12 * 1024 * 1024)
        { Status = "附件总量过大，请分次发送。"; return; }
        Attachments.Add(new(attachment));
    }
    [RelayCommand] private void RemoveAttachment(ChatAttachmentViewModel attachment)
    { if (!IsSending && Attachments.Remove(attachment)) attachment.Dispose(); }
    [RelayCommand] private async Task SendAsync()
    {
        if (!CanCompose || disposed || (string.IsNullOrWhiteSpace(Draft) && Attachments.Count == 0)) return;
        if (client is null || settings is null || string.IsNullOrWhiteSpace(settings.Current.AiModel)) { Status = "请在主界面设置中配置模型。"; return; }
        if (Draft.Length > 16000) { Status = "单条消息最多 16000 字符。"; return; }
        if (Messages.Count >= 24) { Status = "此对话已满，请新建对话。"; return; }
        // Validate the connection before consuming the draft.
        AiConnection connection;
        try { connection = settings.Connection(); }
        catch (SearchAssistanceException ex) { Status = ex.Message; return; }
        Messages.Add(new("user", Draft.Trim(), Attachments.ToArray())); Attachments.Clear(); Draft = "";
        var history = Messages.Where(m => m.Text.Length > 0 || m.Attachments.Count > 0).Select(m => m.Snapshot()).ToArray();
        output = new("assistant", ""); Messages.Add(output); OnPropertyChanged(nameof(IsEmpty));
        IsSending = true; Status = "连接中…"; buffer.Clear(); typing.Start(); ContentChanged?.Invoke();
        using var cancellation = new CancellationTokenSource(); pending = cancellation;
        try
        {
            await foreach (var chunk in client.StreamAsync(history, IncludeContext ? context : "", connection, cancellation.Token))
            {
                if (cancellation.IsCancellationRequested || disposed) break;
                if (buffer.Length + output.Text.Length + chunk.Text.Length > 100000) throw new SearchAssistanceException("回复过长，已停止生成。");
                buffer.Append(chunk.Text);
                if (Status == "连接中…" && chunk.Text.Length > 0) Status = "";
                if (chunk.Notice.Length > 0) Status = chunk.Notice;
            }
            while (buffer.Length > 0 && !cancellation.IsCancellationRequested && !disposed) await Task.Delay(30, cancellation.Token);
            if (output.Text.Length == 0 && Status.Length == 0) Status = "模型没有返回正文。";
            if (Status == "连接中…") Status = "模型没有返回正文。";
        }
        catch (OperationCanceledException) { if (!disposed) Status = "已停止"; }
        catch (SearchAssistanceException ex) { if (!disposed) Status = ex.Message; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { if (!disposed) Status = "无法完成回复，请检查模型配置后重试。"; }
        finally
        {
            Drain(true); typing.Stop(); pending = null; IsSending = false;
            ContentChanged?.Invoke();
        }
    }
    private void Drain(bool all)
    {
        if (buffer.Length == 0 || output is null) return;
        var count = all ? buffer.Length : Math.Min(buffer.Length, Math.Clamp(buffer.Length / 8, 2, 512));
        if (count < buffer.Length && char.IsHighSurrogate(buffer[count - 1])) count++;
        if (!disposed) output.Text += buffer.ToString(0, count);
        buffer.Remove(0, count); ContentChanged?.Invoke();
    }
    [RelayCommand] private void Stop() { pending?.Cancel(); Drain(true); typing.Stop(); }
    [RelayCommand] private void NewConversation()
    {
        if (!CanCompose) return;
        foreach (var item in Messages.SelectMany(m => m.Attachments)) item.Dispose();
        Messages.Clear(); Status = ""; OnPropertyChanged(nameof(IsEmpty));
    }
    public void Dispose()
    {
        if (disposed) return; Stop(); disposed = true; typing.Stop();
        if (settings is not null) settings.Saved -= SettingsChanged;
        foreach (var item in Messages.SelectMany(m => m.Attachments).Concat(Attachments)) item.Dispose();
        Disposed?.Invoke();
    }
}
