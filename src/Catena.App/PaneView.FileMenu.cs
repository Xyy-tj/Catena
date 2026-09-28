using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Catena.Contracts;
using Catena.Platform.Windows;

namespace Catena.App;

public sealed partial class PaneView
{
    private bool fileOperationRunning;

    // Menu opening uses only the loaded rows. No Shell extensions, clipboard reads or disk access.
    private void ShowFileMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (Model is not { } pane) return;
        var source = e.Source as Visual;
        var row = source as ListBoxItem ?? source?.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is FileEntry entry && FileList.SelectedItems?.Contains(entry) != true)
            FileList.SelectedItem = entry;
        var selected = row is not null || !e.TryGetPosition(FileList, out _)
            ? FileList.SelectedItems?.OfType<FileEntry>().ToArray() ?? [] : [];
        FileList.ContextMenu?.Close();
        var menu = new ContextMenu { MinWidth = 206, Placement = PlacementMode.Pointer };
        menu.Classes.Add("file-menu");
        void Add(string title, string icon, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, Icon = new UiIcon { Kind = icon, Width = 16, Height = 16 }, IsEnabled = enabled && !fileOperationRunning };
            item.Click += async (_, _) =>
            {
                menu.Close();
                if (fileOperationRunning) return;
                fileOperationRunning = true;
                try { await action(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                { pane.Status = "操作失败：" + ex.Message; }
                finally { fileOperationRunning = false; }
            };
            menu.Items.Add(item);
        }
        if (selected.Length > 0)
        {
            Add("打开", "open", async () =>
            {
                pane.SelectedEntry = selected[0];
                await pane.OpenCommand.ExecuteAsync(null);
            }, selected.Length == 1);
            menu.Items.Add(new Separator());
            Add("复制", "copy", () => CopyFilesAsync(selected));
        }
        Add("粘贴", "clipboard", () => PasteFilesAsync(pane));
        if (selected.Length > 0)
        {
            Add("复制路径", "copy", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetValueAsync(DataFormat.Text, string.Join(Environment.NewLine, selected.Select(x => x.DisplayPath)));
            });
            menu.Items.Add(new Separator());
            Add("重命名", "rename", async () =>
            {
                if (TopLevel.GetTopLevel(this) is Window owner) await RenameFile(owner, selected[0], pane);
            }, selected.Length == 1);
            Add("删除…", "trash", async () =>
            {
                try { await WindowsFileOperations.DeleteAsync(selected.Select(x => x.DisplayPath).ToArray()); }
                finally { await pane.RefreshCommand.ExecuteAsync(null); }
            });
            menu.Items.Add(new Separator());
            Add("在资源管理器中显示", "folder", () =>
            {
                pane.SelectedEntry = selected[0]; pane.RevealCommand.Execute(null); return Task.CompletedTask;
            }, selected.Length == 1);
            Add("属性", "info", () => ShowFileProperties(selected[0]), selected.Length == 1);
        }
        else Add("刷新", "refresh", () => pane.RefreshCommand.ExecuteAsync(null));
        FileList.ContextMenu = menu;
        menu.Open(FileList);
        e.Handled = true;
    }

    private async Task CopyFilesAsync(FileEntry[] entries)
    {
        if (TopLevel.GetTopLevel(this) is not { Clipboard: { } clipboard } top) return;
        var files = new List<IStorageItem>();
        try
        {
            foreach (var entry in entries)
            {
                IStorageItem? item = entry.IsDirectory
                    ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(entry.Location.Uri))
                    : await top.StorageProvider.TryGetFileFromPathAsync(new Uri(entry.Location.Uri));
                if (item is null) throw new IOException("文件已移动或不可访问。");
                files.Add(item);
            }
            await clipboard.SetFilesAsync(files);
        }
        catch { foreach (var file in files) file.Dispose(); throw; }
        // Clipboard owns the storage items until another value replaces them.
    }

    private async Task PasteFilesAsync(PaneViewModel pane)
    {
        var destination = new Uri(pane.Location.Uri).LocalPath;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var files = await clipboard.TryGetFilesAsync();
        var paths = files?.Select(x => x.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
        if (paths.Length == 0) { pane.Status = "剪贴板中没有可粘贴的文件。"; return; }
        try { await WindowsFileOperations.CopyAsync(paths, destination); }
        finally { await pane.RefreshCommand.ExecuteAsync(null); }
    }

    private async Task ShowFileProperties(FileEntry entry)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window { Title = "属性", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children =
        {
            new TextBlock { Text = entry.Name, FontSize = 17, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new SelectableTextBlock { Text = entry.DisplayPath, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = $"类型    {entry.TypeText}\n大小    {entry.SizeText}\n修改时间    {entry.ModifiedText}", LineHeight = 26 }, close
        } };
        await dialog.ShowDialog(owner);
    }
}
