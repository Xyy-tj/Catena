using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Catena.App;
using Catena.Contracts;
using Catena.Core;
using Catena.Platform.Windows;
using Catena.Storage.Local;
using Location = Catena.Contracts.Location;

namespace Catena.Tests;

public sealed class FileMenuTests
{
    [AvaloniaFact] public async Task RightClickUsesImmediateLocalMenuAndPreservesMultiSelection()
    {
        using var temp = new TestDirectory(); var provider = new LocalFileSystemProvider(); var platform = new Platform();
        File.WriteAllText(Path.Combine(temp.Path, "first.txt"), "one"); File.WriteAllText(Path.Combine(temp.Path, "second.txt"), "two");
        using var pane = new PaneViewModel(provider, platform, Workspace.Create(provider.Normalize(temp.Path)).Panes[0], 1);
        var view = new PaneView { DataContext = pane }; var window = new Window { Width = 650, Height = 500, Content = view };
        window.Show(); await pane.NavigateAsync(provider.Normalize(temp.Path)); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var list = view.FindControl<ListBox>("FileList")!;
        void RightClick(int index)
        {
            var row = (Control)list.ContainerFromIndex(index)!;
            var point = row.TranslatePoint(new Point(150, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            window.UpdateLayout();
            Assert.True(list.ContextMenu?.IsOpen); // No native HWND or COM is available in the headless backend.
        }
        MenuItem Item(string title) => list.ContextMenu!.Items.OfType<MenuItem>().Single(x => Equals(x.Header, title));
        RightClick(0); Assert.Equal(0, list.SelectedIndex); Assert.True(Item("重命名").IsEnabled); Assert.Equal(0, platform.Calls);
        Item("复制路径").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(Path.Combine(temp.Path, "first.txt"), await window.Clipboard!.TryGetTextAsync());
        RightClick(0); Item("复制").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await window.Clipboard!.TryGetFilesAsync())?.Count() != 1) await Task.Delay(10, timeout.Token);
        list.SelectedItems!.Add(pane.Entries[1]);
        RightClick(0); Assert.Equal(2, list.SelectedItems.Count); Assert.False(Item("打开").IsEnabled); Assert.False(Item("重命名").IsEnabled);
        Assert.True(Item("复制").IsEnabled); Assert.True(Item("删除…").IsEnabled);
        Item("复制路径").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Contains("second.txt", await window.Clipboard!.TryGetTextAsync());
        list.SelectedItems.Clear(); list.SelectedItem = pane.Entries[1];
        RightClick(0); Assert.Single(list.SelectedItems); Assert.Equal(pane.Entries[0], list.SelectedItem);
        var artifacts = Environment.GetEnvironmentVariable("CATENA_TEST_ARTIFACTS");
        if (artifacts is not null)
        {
            var menu = list.ContextMenu!;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(menu.Bounds.Width), (int)Math.Ceiling(menu.Bounds.Height)));
            bitmap.Render(menu); bitmap.Save(Path.Combine(artifacts, "catena-file-menu.png"), PngBitmapEncoderOptions.Default);
        }
        Item("属性").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Single(window.OwnedWindows); window.OwnedWindows.Single().Close(); await Task.Yield();
        RightClick(0); Item("重命名").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal("重命名", window.OwnedWindows.Single().Title); window.OwnedWindows.Single().Close(); await Task.Yield();
        RightClick(0); Item("打开").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.Equal(1, platform.Calls);
        list.ContextMenu?.Close(); window.Close();
    }

    [Fact] public async Task CopyRejectsOriginalAndDescendantTargetsBeforeStartingShellOperation()
    {
        using var temp = new TestDirectory(); var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "folder")).FullName;
        await Assert.ThrowsAsync<IOException>(() => WindowsFileOperations.CopyAsync([folder], temp.Path));
        await Assert.ThrowsAsync<IOException>(() => WindowsFileOperations.CopyAsync([folder], folder));
        Assert.True(Directory.Exists(folder)); Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    private sealed class Platform : IPlatformActions
    {
        public int Calls { get; private set; }
        public void Open(Location location) => Calls++;
        public void Reveal(Location location) => Calls++;
        public void OpenTerminal(Location location) => Calls++;
    }
}

