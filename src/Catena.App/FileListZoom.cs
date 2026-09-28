using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Catena.App;

// Scale the full viewport; a long list's desired height must never shrink its column width.
public sealed class FileListZoom : Decorator
{
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<FileListZoom, double>(nameof(Zoom), 1);
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    private readonly ScaleTransform scale = new();
    static FileListZoom() { AffectsMeasure<FileListZoom>(ZoomProperty); }
    public FileListZoom() { ClipToBounds = true; }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty && Child is { } child)
        { child.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute); child.RenderTransform = scale; }
        if (change.Property == ZoomProperty) { scale.ScaleX = scale.ScaleY = Math.Clamp(Zoom, .75, 2); }
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var zoom = Math.Clamp(Zoom, .75, 2);
        Child?.Measure(new Size(availableSize.Width / zoom, availableSize.Height / zoom));
        return Child is { } child ? new Size(child.DesiredSize.Width * zoom, child.DesiredSize.Height * zoom) : default;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var zoom = Math.Clamp(Zoom, .75, 2);
        Child?.Arrange(new Rect(0, 0, finalSize.Width / zoom, finalSize.Height / zoom));
        return finalSize;
    }
}
