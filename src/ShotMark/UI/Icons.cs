using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShotMark.UI
{
    /// <summary>
    /// Stroke icons drawn on a 24×24 grid (original artwork, line style).
    /// Use with the <see cref="Icon"/> element: &lt;ui:Icon Data="{x:Static ui:Icons.Arrow}"/&gt;.
    /// </summary>
    public static class Icons
    {
        private static Geometry G(string data)
        {
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        // Tools
        public static Geometry Select { get; } = G("M6,3 L6,18 L10,14.5 L13,21 L15.5,20 L12.5,13.5 L18,13.5 Z");
        public static Geometry Arrow { get; } = G("M5,19 L18,6 M10,5.5 L18.5,5.5 L18.5,14");
        public static Geometry Line { get; } = G("M5,19 L19,5");
        public static Geometry Rectangle { get; } = G("M4,6 L20,6 L20,18 L4,18 Z");
        public static Geometry Ellipse { get; } = G("M3.5,12 A8.5,7 0 1 0 20.5,12 A8.5,7 0 1 0 3.5,12 Z");
        public static Geometry Pen { get; } = G("M4,20 L5,15.5 L16,4.5 L19.5,8 L8.5,19 Z M13.5,7 L17,10.5");
        public static Geometry Highlighter { get; } = G("M14,4 L20,10 L11,19 L6,19 L5,14 Z M9,9 L15,15 M4,21 L13,21");
        public static Geometry Text { get; } = G("M5,7 L5,4.5 L19,4.5 L19,7 M12,4.5 L12,19.5 M9,19.5 L15,19.5");
        public static Geometry Callout { get; } = G("M4,5 L20,5 L20,15.5 L12,15.5 L7.5,20 L7.5,15.5 L4,15.5 Z M8,9 L16,9 M8,12 L13,12");
        public static Geometry Step { get; } = G("M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M10.5,9 L12.5,7.5 L12.5,16.5");
        public static Geometry Pixelate { get; } = G("M4,4 L9,4 L9,9 L4,9 Z M15,4 L20,4 L20,9 L15,9 Z M9.5,9.5 L14.5,9.5 L14.5,14.5 L9.5,14.5 Z M4,15 L9,15 L9,20 L4,20 Z M15,15 L20,15 L20,20 L15,20 Z");
        public static Geometry Crop { get; } = G("M6,2.5 L6,18 L21.5,18 M2.5,6 L18,6 L18,21.5");

        public static Geometry Menu { get; } = G("M4,7 L20,7 M4,12 L20,12 M4,17 L20,17");
        public static Geometry CollapseLeft { get; } = G("M14,6 L8,12 L14,18 M19,4 L19,20");

        // Actions
        public static Geometry Capture { get; } = G("M4,9 L4,4 L9,4 M15,4 L20,4 L20,9 M20,15 L20,20 L15,20 M9,20 L4,20 L4,15 M12,9 L12,15 M9,12 L15,12");
        public static Geometry FullScreen { get; } = G("M3,5 L21,5 L21,17 L3,17 Z M8,21 L16,21 M12,17 L12,21");
        public static Geometry Open { get; } = G("M3,6 L3,19 L20,19 L21.5,10 L6,10 L3,19 M3,6 L9,6 L11,8 L18,8 L18,10");
        public static Geometry Save { get; } = G("M5,4 L16,4 L20,8 L20,20 L4,20 L4,4 Z M8,4 L8,9 L15,9 L15,4 M8,20 L8,14 L16,14 L16,20");
        public static Geometry Export { get; } = G("M12,3 L12,15 M7.5,7.5 L12,3 L16.5,7.5 M5,12 L4,12 L4,21 L20,21 L20,12 L19,12");
        public static Geometry Copy { get; } = G("M8,8 L20,8 L20,20 L8,20 Z M16,8 L16,4 L4,4 L4,16 L8,16");
        public static Geometry Undo { get; } = G("M8.5,13.5 L4,9 L8.5,4.5 M4,9 L14.5,9 A5.5,5.5 0 0 1 14.5,20 L10,20");
        public static Geometry Redo { get; } = G("M15.5,13.5 L20,9 L15.5,4.5 M20,9 L9.5,9 A5.5,5.5 0 0 0 9.5,20 L14,20");
        public static Geometry Ocr { get; } = G("M4,8 L4,4 L8,4 M16,4 L20,4 L20,8 M20,16 L20,20 L16,20 M8,20 L4,20 L4,16 M8,8.5 L16,8.5 M12,8.5 L12,16");
        public static Geometry ZoomIn { get; } = G("M4,10.5 A6.5,6.5 0 1 0 17,10.5 A6.5,6.5 0 1 0 4,10.5 Z M15.5,15.5 L20.5,20.5 M7.5,10.5 L13.5,10.5 M10.5,7.5 L10.5,13.5");
        public static Geometry ZoomOut { get; } = G("M4,10.5 A6.5,6.5 0 1 0 17,10.5 A6.5,6.5 0 1 0 4,10.5 Z M15.5,15.5 L20.5,20.5 M7.5,10.5 L13.5,10.5");
        public static Geometry Fit { get; } = G("M4,9 L4,4 L9,4 M15,4 L20,4 L20,9 M20,15 L20,20 L15,20 M9,20 L4,20 L4,15");
        public static Geometry Folder { get; } = G("M3,6 L9,6 L11,8 L21,8 L21,19 L3,19 Z");

        // Layers
        public static Geometry Layers { get; } = G("M12,3 L21,8 L12,13 L3,8 Z M3,12 L12,17 L21,12 M3,16 L12,21 L21,16");
        public static Geometry Eye { get; } = G("M2,12 C5,6 19,6 22,12 C19,18 5,18 2,12 Z M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z");
        public static Geometry EyeOff { get; } = G("M2,12 C3.5,9 6,7.2 9,6.6 M14.5,6.7 C17.8,7.3 20.5,9.2 22,12 C20.8,14.4 18.8,16.1 16.5,17 M12,18 C7.6,18 3.8,15.7 2,12 M3,3 L21,21");
        public static Geometry Lock { get; } = G("M5.5,11 L18.5,11 L18.5,20.5 L5.5,20.5 Z M8,11 L8,8 A4,4 0 0 1 16,8 L16,11");
        public static Geometry Unlock { get; } = G("M5.5,11 L18.5,11 L18.5,20.5 L5.5,20.5 Z M8,11 L8,8 A4,4 0 0 1 15.6,6.3");
        public static Geometry Trash { get; } = G("M4,7 L20,7 M9,7 L9,4 L15,4 L15,7 M6,7 L7,20.5 L17,20.5 L18,7 M10,11 L10,16.5 M14,11 L14,16.5");
        public static Geometry Up { get; } = G("M6,15 L12,9 L18,15");
        public static Geometry Down { get; } = G("M6,9 L12,15 L18,9");
        public static Geometry Duplicate { get; } = G("M8,8 L20,8 L20,20 L8,20 Z M16,8 L16,4 L4,4 L4,16 L8,16 M14,11 L14,17 M11,14 L17,14");
        public static Geometry Image { get; } = G("M3,5 L21,5 L21,19 L3,19 Z M3,16 L8.5,11 L13,15 L16,12.5 L21,16.5 M14.5,9 A1.5,1.5 0 1 0 17.5,9 A1.5,1.5 0 1 0 14.5,9 Z");
        public static Geometry Blur { get; } = G("M12,3 C12,3 5.5,10.5 5.5,14.5 A6.5,6.5 0 0 0 18.5,14.5 C18.5,10.5 12,3 12,3 Z M9,15 A3,3 0 0 0 12,18");
        public static Geometry Spotlight { get; } = G("M3,4 L21,4 L21,20 L3,20 Z M8,9 L16,9 L16,15 L8,15 Z M3,4 L8,9 M21,4 L16,9 M3,20 L8,15 M21,20 L16,15");
        public static Geometry Sparkle { get; } = G("M10,3 L11.8,8.7 L17.5,10.5 L11.8,12.3 L10,18 L8.2,12.3 L2.5,10.5 L8.2,8.7 Z M18,13 L18.9,15.6 L21.5,16.5 L18.9,17.4 L18,20 L17.1,17.4 L14.5,16.5 L17.1,15.6 Z");
        public static Geometry Gear { get; } = G("M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z M12,2.5 L12,5.5 M12,18.5 L12,21.5 M2.5,12 L5.5,12 M18.5,12 L21.5,12 M5.3,5.3 L7.4,7.4 M16.6,16.6 L18.7,18.7 M5.3,18.7 L7.4,16.6 M16.6,7.4 L18.7,5.3");
        public static Geometry Pin { get; } = G("M9,3 L15,3 L14,9.5 L17.5,13 L6.5,13 L10,9.5 Z M12,13 L12,21");
        public static Geometry Print { get; } = G("M7,9 L7,3 L17,3 L17,9 M6,17 L3,17 L3,9 L21,9 L21,17 L18,17 M7,13.5 L17,13.5 L17,21 L7,21 Z");
        public static Geometry Timer { get; } = G("M12,9 L12,13.5 L15,15.5 M5,13.5 A7,7 0 1 0 19,13.5 A7,7 0 1 0 5,13.5 Z M9.5,2.5 L14.5,2.5");
        public static Geometry Repeat { get; } = G("M4,12 A8,8 0 0 1 18,6.5 M19.5,2.5 L19.5,7.5 L14.5,7.5 M20,12 A8,8 0 0 1 6,17.5 M4.5,21.5 L4.5,16.5 L9.5,16.5");
        public static Geometry Rotate { get; } = G("M20,12 A8,8 0 1 1 15.5,4.8 M15,2 L16,5.2 L12.8,6.2");
        public static Geometry More { get; } = G("M5,12 L5.01,12 M12,12 L12.01,12 M19,12 L19.01,12");
        public static Geometry Stamp { get; } = G("M3.5,12 A8.5,8.5 0 1 0 20.5,12 A8.5,8.5 0 1 0 3.5,12 Z M8,12.5 L11,15.5 L16.5,9");
        public static Geometry Magnify { get; } = G("M3.5,10 A6.5,6.5 0 1 0 16.5,10 A6.5,6.5 0 1 0 3.5,10 Z M15,15 L21,21 M10,7 L10,13 M7,10 L13,10");
        public static Geometry Drag { get; } = G("M12,3 L12,21 M3,12 L21,12 M12,3 L9.5,5.5 M12,3 L14.5,5.5 M12,21 L9.5,18.5 M12,21 L14.5,18.5 M3,12 L5.5,9.5 M3,12 L5.5,14.5 M21,12 L18.5,9.5 M21,12 L18.5,14.5");
        public static Geometry Video { get; } = G("M3,6.5 L15,6.5 L15,17.5 L3,17.5 Z M15,10.5 L21,7 L21,17 L15,13.5");
        public static Geometry Scroll { get; } = G("M6,3 L18,3 L18,21 L6,21 Z M12,7 L12,17 M9.5,9.5 L12,7 L14.5,9.5 M9.5,14.5 L12,17 L14.5,14.5");
        public static Geometry Search { get; } = G("M4,10.5 A6.5,6.5 0 1 0 17,10.5 A6.5,6.5 0 1 0 4,10.5 Z M15.5,15.5 L20.5,20.5");
        public static Geometry Rename { get; } = G("M4,20 L5,15.5 L14,6.5 L17.5,10 L8.5,19 Z M15,5.5 L17,3.5 L20.5,7 L18.5,9");
    }

    /// <summary>Draws one of the <see cref="Icons"/> scaled to <see cref="Size"/>, using the inherited Foreground.</summary>
    public sealed class Icon : FrameworkElement
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data), typeof(Geometry), typeof(Icon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
            nameof(Size), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
            nameof(StrokeWidth), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(Icon),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public Geometry? Data
        {
            get => (Geometry?)GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        /// <summary>Stroke width in screen pixels.</summary>
        public double StrokeWidth
        {
            get => (double)GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public Icon()
        {
            SnapsToDevicePixels = true;
        }

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new System.Windows.Size(Size, Size);

        protected override void OnRender(DrawingContext dc)
        {
            var data = Data;
            if (data == null) return;
            double scale = Size / 24.0;
            var pen = new Pen(Foreground, StrokeWidth / scale)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(null, pen, data);
            dc.Pop();
        }
    }
}
