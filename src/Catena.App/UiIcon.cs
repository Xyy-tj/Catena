using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Catena.App;

// A single cached outline set keeps toolbar stroke weight and proportions consistent.
public sealed class UiIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<UiIcon, string>(nameof(Kind), "search");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<UiIcon>();
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = new Dictionary<string, string>
    {
        ["search"] = "M10,3 A7,7 0 1 0 10,17 A7,7 0 1 0 10,3 M15,15 L21,21",
        ["grid"] = "M3,3 H9 V9 H3 Z M15,3 H21 V9 H15 Z M3,15 H9 V21 H3 Z M15,15 H21 V21 H15 Z",
        ["rows"] = "M4,3 H20 Q21,3 21,4 V20 Q21,21 20,21 H4 Q3,21 3,20 V4 Q3,3 4,3 Z M3,12 H21",
        ["columns"] = "M4,3 H20 Q21,3 21,4 V20 Q21,21 20,21 H4 Q3,21 3,20 V4 Q3,3 4,3 Z M12,3 V21 M7,9 L5,12 L7,15 M17,9 L19,12 L17,15",
        ["single"] = "M4,3 H20 Q21,3 21,4 V20 Q21,21 20,21 H4 Q3,21 3,20 V4 Q3,3 4,3 Z",
        ["folder"] = "M3,5 H9 L12,8 H21 V20 H3 Z",
        ["filter"] = "M3,4 H21 V6 L14,13 V20 L10,22 V13 L3,6 Z",
        ["tree"] = "M3,5 H5 M3,12 H5 M3,19 H5 M9,5 H21 M9,12 H21 M9,19 H21",
        ["pin"] = "M14,3 L21,10 L19,12 L17,10 L12,15 V18 L10,20 L4,14 L6,12 H9 L14,7 L12,5 Z M7,17 L3,21",
        ["settings"] = "M10,3 H14 L15,6 L18,7 L21,7 L22,11 L19,13 L19,16 L20,18 L17,21 L14,19 H11 L8,21 L5,18 L6,15 L5,12 L2,10 L4,6 L7,6 Z M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8",
        ["back"] = "M10,5 L3,12 L10,19 M3,12 H21",
        ["forward"] = "M14,5 L21,12 L14,19 M21,12 H3",
        ["up"] = "M5,10 L12,3 L19,10 M12,3 V21",
        ["refresh"] = "M20,4 V10 H14 M20,10 A8,8 0 1 0 20,16",
        ["more"] = "M4,12 H4.1 M12,12 H12.1 M20,12 H20.1",
        ["close"] = "M5,5 L19,19 M19,5 L5,19",
        ["minimize"] = "M5,12 H19",
        ["maximize"] = "M5,5 H19 V19 H5 Z",
        ["restore"] = "M8,5 H20 V17 M4,9 H16 V21 H4 Z",
        ["copy"] = "M8,7 V3 H21 V17 H17 M3,7 H17 V21 H3 Z",
        ["open"] = "M13,3 H21 V11 M21,3 L10,14 M9,5 H3 V21 H19 V15",
        ["chevron"] = "M6,9 L12,15 L18,9",
        ["terminal"] = "M4,6 L10,12 L4,18 M13,18 H21"
    }.ToDictionary(x => x.Key, x => Geometry.Parse(x.Value));
    private Pen pen = new(Brushes.Gray, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    static UiIcon() => AffectsRender<UiIcon>(KindProperty, ForegroundProperty);
    public UiIcon() { Width = Height = 18; }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForegroundProperty) pen = new(Foreground, Kind == "more" ? 3 : 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    }
    public override void Render(DrawingContext context)
    {
        if (!Shapes.TryGetValue(Kind, out var shape)) return;
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24))) context.DrawGeometry(null, pen, shape);
    }
}
