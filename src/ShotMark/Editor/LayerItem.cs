using System.Windows;
using System.Windows.Media;
using ShotMark.Core;
using ShotMark.UI;

namespace ShotMark.Editor
{
    /// <summary>Row shown in the layers panel for one annotation.</summary>
    public sealed class LayerItem
    {
        private static readonly Brush NeutralBrush = ColorUtil.Brush(Color.FromRgb(0xC8, 0xCB, 0xD0));

        public LayerItem(Annotation model)
        {
            Model = model;
        }

        public Annotation Model { get; }

        public string Name => Model.DisplayName;

        public Geometry Icon => Model switch
        {
            ArrowAnnotation => Icons.Arrow,
            LineAnnotation => Icons.Line,
            RectangleAnnotation => Icons.Rectangle,
            EllipseAnnotation => Icons.Ellipse,
            PixelateAnnotation => Icons.Pixelate,
            BlurAnnotation => Icons.Blur,
            SpotlightAnnotation => Icons.Spotlight,
            FreehandAnnotation f => f.Highlighter ? Icons.Highlighter : Icons.Pen,
            TextAnnotation t => t.Callout ? Icons.Callout : Icons.Text,
            StepAnnotation => Icons.Step,
            _ => Icons.Layers,
        };

        /// <summary>Icon tinted with the annotation colour (neutral for colourless layers).</summary>
        public Brush ColorBrush
        {
            get
            {
                if (Model is PixelateAnnotation || Model is BlurAnnotation || Model is SpotlightAnnotation) return NeutralBrush;
                var c = Model.StrokeColor;
                // Very dark colours would disappear on the dark panel.
                if (c.R + c.G + c.B < 120) return NeutralBrush;
                return ColorUtil.Brush(Color.FromRgb(c.R, c.G, c.B));
            }
        }

        public Geometry VisibilityIcon => Model.Visible ? Icons.Eye : Icons.EyeOff;
        public Geometry LockIcon => Model.Locked ? Icons.Lock : Icons.Unlock;
        public double RowOpacity => Model.Visible ? 1.0 : 0.45;
        public double LockOpacity => Model.Locked ? 1.0 : 0.3;
        public FontStyle NameStyle => Model.Visible ? FontStyles.Normal : FontStyles.Italic;
    }
}
