using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Catena.App;

// Deliberately local rendering: no browser, scripts, remote images or Markdown packages.
public sealed class ChatMarkdownView : StackPanel
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<ChatMarkdownView, string>(nameof(Text), "");
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(120) };
    public ChatMarkdownView()
    {
        Spacing = 7; refresh.Tick += (_, _) => { refresh.Stop(); RenderText(); };
        DetachedFromVisualTree += (_, _) => refresh.Stop();
        AttachedToVisualTree += (_, _) => RenderText();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == TextProperty && !refresh.IsEnabled) refresh.Start(); }
    private void RenderText()
    {
        Children.Clear(); var code = false; var block = new System.Text.StringBuilder();
        void Flush()
        {
            if (block.Length == 0) return;
            var view = new SelectableTextBlock { Text = block.ToString().TrimEnd(), TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23 };
            if (code) { view.FontFamily = new FontFamily("Cascadia Code,Consolas,Microsoft YaHei UI"); view.FontSize = 12; }
            Children.Add(code ? new Border { Background = new SolidColorBrush(Color.FromArgb(18, 100, 130, 160)), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = view } : view);
            block.Clear();
        }
        foreach (var line in Text.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) { Flush(); code = !code; continue; }
            if (!code && line.StartsWith('#'))
            { Flush(); Children.Add(new SelectableTextBlock { Text = line.TrimStart('#', ' '), FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }); continue; }
            if (!code && string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
            block.AppendLine(!code ? line.Replace("**", "") : line);
        }
        Flush();
    }
}
