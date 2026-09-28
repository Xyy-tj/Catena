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
        ["settings"] = "M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915 M15,12 A3,3 0 1 0 9,12 A3,3 0 1 0 15,12",
        ["github"] = "M6.766 11.328c-2.063-.25-3.516-1.734-3.516-3.656 0-.781.281-1.625.75-2.188-.203-.515-.172-1.609.063-2.062.625-.078 1.468.25 1.968.703.594-.187 1.219-.281 1.985-.281.765 0 1.39.094 1.953.265.484-.437 1.344-.765 1.969-.687.218.422.25 1.515.046 2.047.5.593.766 1.39.766 2.203 0 1.922-1.453 3.375-3.547 3.64.531.344.89 1.094.89 1.954v1.625c0 .468.391.734.86.547C13.781 14.359 16 11.53 16 8.03 16 3.61 12.406 0 7.984 0 3.563 0 0 3.61 0 8.031a7.88 7.88 0 0 0 5.172 7.422c.422.156.828-.125.828-.547v-1.25c-.219.094-.5.156-.75.156-1.031 0-1.64-.562-2.078-1.609-.172-.422-.36-.672-.719-.719-.187-.015-.25-.093-.25-.187 0-.188.313-.328.625-.328.453 0 .844.281 1.25.86.313.452.64.655 1.031.655s.641-.14 1-.5c.266-.265.47-.5.657-.656",
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
        ["clipboard"] = "M9,4 H5 V21 H19 V4 H15 M9,2 H15 V6 H9 Z",
        ["rename"] = "M4,20 L5,15 L17,3 L21,7 L9,19 Z M14,6 L18,10",
        ["trash"] = "M3,6 H21 M9,6 V3 H15 V6 M5,6 L6,21 H18 L19,6 M10,10 V17 M14,10 V17",
        ["info"] = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M12,10 V17 M12,6 V7",
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
        var size = Kind == "github" ? 16 : 24;
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / size, Bounds.Height / size)))
            context.DrawGeometry(Kind == "github" ? Foreground ?? Brushes.Gray : null, Kind == "github" ? null : pen, shape);
    }
}
