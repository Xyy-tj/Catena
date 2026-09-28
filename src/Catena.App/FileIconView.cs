using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Catena.Contracts;
using Catena.Platform.Windows;

namespace Catena.App;

public sealed class FileIconView : Image
{
    public static readonly StyledProperty<FileEntry?> EntryProperty = AvaloniaProperty.Register<FileIconView, FileEntry?>(nameof(Entry));
    public FileEntry? Entry { get => GetValue(EntryProperty); set => SetValue(EntryProperty, value); }
    private static readonly ConcurrentDictionary<string, Lazy<Task<IImage?>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim IconGate = new(1, 1);
    private static readonly IImage Folder = new DrawingImage(new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#EFB840")),
        Geometry = Geometry.Parse("M1,7 L12,7 16,11 31,11 31,27 1,27 Z M1,7 L1,4 11,4 14,7 Z") });
    private static readonly IImage File = new DrawingImage(new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#CCD5DE")), Pen = new Pen(new SolidColorBrush(Color.Parse("#75879A")), 1),
        Geometry = Geometry.Parse("M6,2 L21,2 28,9 28,30 6,30 Z M21,2 L21,9 28,9") });
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EntryProperty) _ = LoadAsync();
    }
    private async Task LoadAsync()
    {
        var entry = Entry; Source = entry?.IsDirectory == true ? Folder : File;
        if (entry is null) return;
        var key = entry.IsDirectory ? "<folder>" : Path.GetExtension(entry.Name);
        if (key.Length > 32 || Cache.Count >= 256 && !Cache.ContainsKey(key)) key = "";
        var cached = Cache.GetOrAdd(key, k => new Lazy<Task<IImage?>>(() => Task.Run(async () =>
        {
            await IconGate.WaitAsync();
            try
            {
                if (WindowsFileIcons.ReadTypeIcon(k == "<folder>" ? "" : k, k == "<folder>") is not { } data) return null;
                var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using var pixels = bitmap.Lock(); Marshal.Copy(data, 0, pixels.Address, data.Length); return (IImage)bitmap;
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException or DllNotFoundException) { return null; }
            finally { IconGate.Release(); }
        })));
        var icon = await cached.Value;
        if (ReferenceEquals(Entry, entry) && icon is not null) Source = icon;
    }
}
