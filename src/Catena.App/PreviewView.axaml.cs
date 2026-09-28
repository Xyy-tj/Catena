using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
namespace Catena.App;
public sealed partial class PreviewView : UserControl
{
    public PreviewView()
    {
        InitializeComponent();
        DocumentHost.Failed += async message => { if (DataContext is PreviewViewModel model) await model.ShowFallbackAsync(message); };
        DocumentHost.Ready += path => { if (DataContext is PreviewViewModel model && model.NativePath == path) model.Notice = ""; };
    }
    private async void CopyPath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PreviewViewModel { Entry: { } entry } model || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(entry.DisplayPath); } catch (Exception) { model.Notice = "剪贴板暂不可用。"; }
    }
}
