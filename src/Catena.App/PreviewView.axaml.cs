using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
namespace Catena.App;
public sealed partial class PreviewView : UserControl
{
    public PreviewView()
    {
        InitializeComponent();
        DocumentHost.Failed += message => { if (DataContext is PreviewViewModel model) { model.NativePath = ""; model.Notice = message; } };
    }
    private async void CopyPath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PreviewViewModel { Entry: { } entry } model || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(entry.DisplayPath); } catch (Exception) { model.Notice = "剪贴板暂不可用。"; }
    }
}
