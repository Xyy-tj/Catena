using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Catena.App;
public sealed partial class LocationsWindow : Window
{
    public LocationsWindow() => InitializeComponent();
    private async void AddFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LocationLibraryViewModel library) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "添加常用位置", AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await library.AddAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        { library.Status = "文件夹选择器不可用，可在窗格目录树中点击 ＋。"; }
    }
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
