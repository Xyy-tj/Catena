using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Catena.Contracts;
using Catena.Platform.Windows;
using Avalonia.VisualTree;

namespace Catena.App;
public sealed partial class PaneView : UserControl
{
    private PaneViewModel? previous;
    private bool restoring;
    private bool detached;
    public PaneView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, _) => Activate(), RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, ZoomList, RoutingStrategies.Tunnel);
        GotFocus += (_, _) => Activate();
        FileList.AddHandler(PointerReleasedEvent, ShowFileMenu, RoutingStrategies.Bubble, handledEventsToo: true);
        AttachedToVisualTree += (_, _) => { ModelChanged(this, new PropertyChangedEventArgs(nameof(PaneViewModel.Entries))); RebuildBreadcrumbs(); };
        DataContextChanged += (_, _) =>
        {
            if (previous is not null) { previous.PropertyChanged -= ModelChanged; previous.EntriesUpdating -= BeginEntriesUpdate; }
            previous = DataContext as PaneViewModel;
            if (previous is not null) { previous.PropertyChanged += ModelChanged; previous.EntriesUpdating += BeginEntriesUpdate; }
        };
        DetachedFromVisualTree += (_, _) => { detached = true; if (previous is not null) { previous.PropertyChanged -= ModelChanged; previous.EntriesUpdating -= BeginEntriesUpdate; } };
    }
    private void BeginEntriesUpdate() => restoring = true;
    private async void ShowFileMenu(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || Model is not { } pane || e.Source is not Avalonia.Visual source) return;
        var row = source as ListBoxItem ?? source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not FileEntry entry) return;
        if (FileList.SelectedItems?.Contains(entry) != true) FileList.SelectedItem = entry;
        var selected = FileList.SelectedItems?.OfType<FileEntry>().ToArray() ?? [entry];
        if (!OperatingSystem.IsWindows() || TopLevel.GetTopLevel(this) is not { } top || top.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle) return;
        e.Handled = true;
        try
        {
            var point = top.PointToScreen(e.GetPosition(top));
            var action = WindowsShellMenu.Show(selected.Select(x => x.DisplayPath).ToArray(), handle.Handle, point.X, point.Y, selected.Length == 1 && entry.IsDirectory);
            if (action == WindowsShellMenu.Action.Open) await pane.NavigateAsync(entry.Location);
            else if (action == WindowsShellMenu.Action.Rename && top is Window owner) await RenameFile(owner, entry, pane);
            else if (action == WindowsShellMenu.Action.Refresh) await pane.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or IOException or UnauthorizedAccessException)
        { pane.Status = "无法打开 Windows 文件菜单。"; }
    }
    private static async Task RenameFile(Window owner, FileEntry entry, PaneViewModel pane)
    {
        var dialog = new Window { Title = "重命名", Width = 420, Height = 190, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Text = entry.Name }; var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Content = "保存" }; var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            var value = name.Text?.Trim() ?? "";
            if (value.Length == 0 || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.EndsWith('.'))
            { error.Text = "请输入有效的文件名。"; return; }
            try
            {
                var destination = Path.Combine(Path.GetDirectoryName(entry.DisplayPath)!, value);
                if (entry.IsDirectory) Directory.Move(entry.DisplayPath, destination); else File.Move(entry.DisplayPath, destination);
                dialog.Close(); await pane.RefreshCommand.ExecuteAsync(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { error.Text = "重命名失败：名称已存在或文件正在使用。"; }
        };
        dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { name, error,
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } } } };
        dialog.Opened += (_, _) => { name.Focus(); name.SelectionStart = 0; name.SelectionEnd = entry.IsDirectory ? entry.Name.Length : Path.GetFileNameWithoutExtension(entry.Name).Length; };
        await dialog.ShowDialog(owner);
    }
    private PaneViewModel? Model => DataContext as PaneViewModel;
    private void Activate()
    {
        if (Model is { IsActive: false } pane && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) main.Activate(pane);
    }
    public void FocusAddress() { AddressBox.IsVisible = true; BreadcrumbBorder.IsVisible = false; AddressBox.Focus(); AddressBox.SelectAll(); }
    private void EditAddress(object? sender, TappedEventArgs e) { FocusAddress(); e.Handled = true; }
    private void FinishEditingAddress(object? sender, RoutedEventArgs e) { AddressBox.IsVisible = false; BreadcrumbBorder.IsVisible = true; }
    private void AddressSizeChanged(object? sender, SizeChangedEventArgs e) => RebuildBreadcrumbs();
    private string breadcrumbKey = "";
    private void RebuildBreadcrumbs()
    {
        if (Model is not { } pane) return;
        var path = new Uri(pane.Location.Uri).LocalPath;
        var count = BreadcrumbBorder.Bounds.Width >= 300 ? 3 : BreadcrumbBorder.Bounds.Width >= 160 ? 2 : 1;
        var key = path + "|" + count; if (key == breadcrumbKey) return; breadcrumbKey = key;
        BreadcrumbGrid.Children.Clear(); BreadcrumbGrid.ColumnDefinitions.Clear();
        var parts = new List<(string Name, string Path)>(); var current = Path.TrimEndingDirectorySeparator(path);
        for (var i = 0; i < count; i++)
        {
            parts.Insert(0, (Path.GetFileName(current) is { Length: > 0 } name ? name : current, current));
            var parent = Path.GetDirectoryName(current); if (string.IsNullOrEmpty(parent)) break; current = parent;
        }
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i]; BreadcrumbGrid.ColumnDefinitions.Add(new(GridLength.Star));
            var button = new Button { Background = Avalonia.Media.Brushes.Transparent, Padding = new Avalonia.Thickness(4, 2), BorderThickness = new Avalonia.Thickness(0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Content = new TextBlock { Text = (i > 0 ? "›  " : "") + part.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis } };
            ToolTip.SetTip(button, part.Path); button.Click += async (_, _) => { if (part.Path == path || parts.Count == 1) FocusAddress(); else await pane.NavigatePathAsync(part.Path); };
            Grid.SetColumn(button, i); BreadcrumbGrid.Children.Add(button);
        }
    }
    private void OpenSearch(object? sender, RoutedEventArgs e)
    { if (Model is { } pane && TopLevel.GetTopLevel(this) is MainWindow window) window.FocusSearch(pane); }
    private void ZoomList(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && Model is { } pane)
        { Activate(); pane.ZoomList(Math.Sign(e.Delta.Y)); e.Handled = true; }
    }
    private void OpenChat(object? sender, RoutedEventArgs e)
    {
        FooterActions.Flyout?.Hide();
        if (Model is { } pane && TopLevel.GetTopLevel(this) is MainWindow window) window.OpenChat(pane);
    }
    private void DetailRowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid grid) return;
        grid.ColumnDefinitions[2].Width = new GridLength(e.NewSize.Width >= 480 ? 104 : 72); grid.ColumnDefinitions[3].Width = new GridLength(e.NewSize.Width >= 390 ? 130 : 0);
        foreach (var child in grid.Children.Where(c => Grid.GetColumn(c) == 3)) child.IsVisible = e.NewSize.Width >= 390;
    }
    private void TitleSizeChanged(object? sender, SizeChangedEventArgs e) => FolderTitle.MaxWidth = Math.Max(30, e.NewSize.Width - 138);
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PaneViewModel.FolderName)) RebuildBreadcrumbs();
        if (args.PropertyName is nameof(PaneViewModel.TreeVisible) or nameof(PaneViewModel.TreeWidth) or nameof(PaneViewModel.Entries)) UpdateTreeWidth();
        if (args.PropertyName == nameof(PaneViewModel.Entries))
        {
            restoring = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (detached || Model is not { } pane) { restoring = false; return; }
                restoring = true;
                FileList.SelectedItems?.Clear();
                foreach (var entry in pane.Entries.Where(e => pane.SelectedLocations.Contains(e.Location))) FileList.SelectedItems?.Add(entry);
                pane.PropertyChanged -= ModelChanged;
                pane.SelectedEntry = FileList.SelectedItem as FileEntry;
                pane.PropertyChanged += ModelChanged;
                if (pane.SelectedEntry is { } selected) FileList.ScrollIntoView(selected);
                restoring = false;
            });
        }
        if (args.PropertyName == nameof(PaneViewModel.SelectedEntry) && Model?.SelectedEntry is { } entry)
        {
            restoring = true; FileList.SelectedItem = entry; FileList.ScrollIntoView(entry); restoring = false;
        }
    }
    private void BrowserSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateTreeWidth();
    private void UpdateTreeWidth()
    {
        if (Model is not { } pane) return;
        var max = Math.Max(100, BrowserGrid.Bounds.Width - 126);
        BrowserGrid.ColumnDefinitions[0].MinWidth = pane.TreeVisible ? 100 : 0;
        BrowserGrid.ColumnDefinitions[0].MaxWidth = max;
        BrowserGrid.ColumnDefinitions[0].Width = new GridLength(pane.TreeVisible ? Math.Min(pane.TreeWidth, max) : 0);
        BrowserGrid.ColumnDefinitions[1].Width = new GridLength(pane.TreeVisible ? 6 : 0);
        BrowserGrid.ColumnDefinitions[2].MinWidth = 100;
    }
    private void TreeDragCompleted(object? sender, VectorEventArgs e)
    { if (Model is { } pane) pane.TreeWidth = Math.Clamp(BrowserGrid.ColumnDefinitions[0].ActualWidth, 100, 1000); }
    private async void ChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } pane || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var start = await storage.TryGetFolderFromPathAsync(new Uri(pane.Location.Uri));
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择文件夹", AllowMultiple = false, SuggestedStartLocation = start });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await pane.NavigatePathAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        { pane.Status = "无法打开文件夹选择器，请输入路径。"; }
    }
    private async void AddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Model is { } pane) { e.Handled = true; await pane.NavigateAddressCommand.ExecuteAsync(null); FileList.Focus(); }
        else if (e.Key == Key.Escape) { e.Handled = true; FileList.Focus(); }
    }
    private async void OpenEntry(object? sender, TappedEventArgs e)
    { if (Model is { } pane) await pane.OpenCommand.ExecuteAsync(null); }
    private async void ListKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && Model is { } pane) { e.Handled = true; await pane.OpenCommand.ExecuteAsync(null); } }
    private void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!restoring && Model is { } pane && !pane.IsLoading)
        {
            pane.SelectedLocations = FileList.SelectedItems?.OfType<FileEntry>().Select(x => x.Location).ToList() ?? [];
            // Avoid changing the UI selection again while reflecting a user multi-selection.
            pane.PropertyChanged -= ModelChanged;
            pane.SelectedEntry = FileList.SelectedItem as FileEntry;
            pane.PropertyChanged += ModelChanged;
            pane.NotifySelectionChanged();
        }
    }
    public async void CopyPath(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } pane || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var paths = FileList.SelectedItems?.OfType<FileEntry>().Select(x => new Uri(x.Location.Uri).LocalPath).ToArray() ?? [];
        try { await clipboard.SetValueAsync(DataFormat.Text, paths.Length > 0 ? string.Join(Environment.NewLine, paths) : new Uri(pane.Location.Uri).LocalPath); }
        catch (Exception) { pane.Status = "复制失败，剪贴板暂不可用。"; }
    }
}

