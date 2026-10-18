using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShotMark.Core
{
    /// <summary>Information an annotation may need while rendering (e.g. the base image for pixelation).</summary>
    public sealed class RenderContext
    {
        public BitmapSource? BaseImage { get; set; }
    }

    /// <summary>A serializable 2D point (System.Windows.Point does not round-trip cleanly through JSON).</summary>
    public sealed class Pt
    {
        public Pt() { }
        public Pt(double x, double y) { X = x; Y = y; }
        public double X { get; set; }
        public double Y { get; set; }
        public Point ToPoint() => new Point(X, Y);
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
    [JsonDerivedType(typeof(LineAnnotation), "line")]
    [JsonDerivedType(typeof(RectangleAnnotation), "rect")]
    [JsonDerivedType(typeof(EllipseAnnotation), "ellipse")]
    [JsonDerivedType(typeof(PixelateAnnotation), "pixelate")]
    [JsonDerivedType(typeof(FreehandAnnotation), "freehand")]
    [JsonDerivedType(typeof(TextAnnotation), "text")]
    [JsonDerivedType(typeof(StepAnnotation), "step")]
    [JsonDerivedType(typeof(BlurAnnotation), "blur")]
    [JsonDerivedType(typeof(SpotlightAnnotation), "spotlight")]
    [JsonDerivedType(typeof(MagnifyAnnotation), "magnify")]
    [JsonDerivedType(typeof(StampAnnotation), "stamp")]
    public abstract class Annotation
    {
        public string ColorHex { get; set; } = "#FFE53935";
        public double Thickness { get; set; } = 4;

        /// <summary>Layer visibility (hidden layers are not drawn nor exported).</summary>
        public bool Visible { get; set; } = true;

        /// <summary>Locked layers cannot be picked or moved on the canvas.</summary>
        public bool Locked { get; set; }

        /// <summary>Optional user-given layer name.</summary>
        public string? Name { get; set; }

        /// <summary>Layer opacity, 0.05 – 1.</summary>
        public double Opacity { get; set; } = 1.0;

        /// <summary>Dashed outline (lines, arrows, rectangles, ellipses).</summary>
        public bool Dashed { get; set; }

        /// <summary>Points the user can drag to reshape the layer (empty = move only).</summary>
        public virtual Point[] GetHandles() => Array.Empty<Point>();

        /// <summary>Moves handle <paramref name="index"/> (from <see cref="GetHandles"/>) to <paramref name="p"/>.</summary>
        public virtual void DragHandle(int index, Point p) { }

        /// <summary>Called when a handle drag ends (e.g. to normalise rectangles).</summary>
        public virtual void EndHandleDrag() { }

        /// <summary>Stroke pen honouring <see cref="Dashed"/>.</summary>
        protected Pen StrokePen(double thickness, PenLineCap cap = PenLineCap.Round, PenLineJoin join = PenLineJoin.Round)
        {
            var pen = new Pen(ColorUtil.Brush(StrokeColor), thickness)
            {
                StartLineCap = Dashed ? PenLineCap.Flat : cap,
                EndLineCap = Dashed ? PenLineCap.Flat : cap,
                DashCap = PenLineCap.Round,
                LineJoin = join,
            };
            if (Dashed) pen.DashStyle = new DashStyle(new double[] { 2.2, 1.6 }, 0);
            pen.Freeze();
            return pen;
        }

        /// <summary>Renders the layer applying its opacity. Use this instead of calling Render directly.</summary>
        public void Draw(DrawingContext dc, RenderContext ctx)
        {
            bool fade = Opacity < 0.999;
            if (fade) dc.PushOpacity(Math.Max(0.05, Opacity));
            Render(dc, ctx);
            if (fade) dc.Pop();
        }

        /// <summary>Maps every geometric point (used by rotate / flip / resize / border).</summary>
        public abstract void TransformPoints(Func<Point, Point> map);

        /// <summary>Scales stroke widths, font sizes etc. when the image is resized.</summary>
        public virtual void ScaleSizes(double factor)
        {
            Thickness = Math.Max(1, Thickness * factor);
        }

        [JsonIgnore]
        public System.Windows.Media.Color StrokeColor => ColorUtil.Parse(ColorHex);

        /// <summary>Human readable type, used for default layer names.</summary>
        [JsonIgnore]
        public abstract string TypeName { get; }

        [JsonIgnore]
        public virtual string DefaultName => TypeName;

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DefaultName : Name!;

        protected static string Shorten(string text, int max = 22)
        {
            var oneLine = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max - 1) + "…";
        }

        public abstract Rect GetBounds();
        public abstract void Offset(double dx, double dy);
        public abstract void Render(DrawingContext dc, RenderContext ctx);

        public virtual bool HitTest(Point p)
        {
            var b = GetBounds();
            double pad = Thickness / 2 + 5;
            b.Inflate(pad, pad);
            return b.Contains(p);
        }

        protected Pen MakePen(System.Windows.Media.Color color, double thickness, PenLineCap cap = PenLineCap.Round, PenLineJoin join = PenLineJoin.Round)
        {
            var pen = new Pen(ColorUtil.Brush(color), thickness)
            {
                StartLineCap = cap,
                EndLineCap = cap,
                LineJoin = join,
            };
            pen.Freeze();
            return pen;
        }

        internal static double DistanceToSegment(Point p, Point a, Point b)
        {
            Vector ab = b - a;
            double len2 = ab.LengthSquared;
            if (len2 < 1e-9) return (p - a).Length;
            double t = Math.Max(0, Math.Min(1, Vector.Multiply(p - a, ab) / len2));
            Point proj = a + ab * t;
            return (p - proj).Length;
        }
    }

    /// <summary>Annotations defined by a start and end point (drag gesture).</summary>
    public abstract class TwoPointAnnotation : Annotation
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }

        [JsonIgnore] public Point P1 => new Point(X1, Y1);
        [JsonIgnore] public Point P2 => new Point(X2, Y2);
        [JsonIgnore] public Rect Area => new Rect(P1, P2);

        public void SetStart(Point p) { X1 = p.X; Y1 = p.Y; X2 = p.X; Y2 = p.Y; }
        public void SetEnd(Point p) { X2 = p.X; Y2 = p.Y; }

        public override Rect GetBounds() => Area;

        public override void Offset(double dx, double dy)
        {
            X1 += dx; Y1 += dy; X2 += dx; Y2 += dy;
        }

        public override void TransformPoints(Func<Point, Point> map)
        {
            var a = map(P1);
            var b = map(P2);
            X1 = a.X; Y1 = a.Y; X2 = b.X; Y2 = b.Y;
        }

        /// <summary>Makes (X1,Y1) the top-left and (X2,Y2) the bottom-right corner.</summary>
        public void Normalize()
        {
            var r = Area;
            X1 = r.Left; Y1 = r.Top; X2 = r.Right; Y2 = r.Bottom;
        }

        public void SetStartOnly(Point p) { X1 = p.X; Y1 = p.Y; }

        /// <summary>Lines and arrows expose their end points; boxes expose their corners (TL, TR, BL, BR).</summary>
        [JsonIgnore] protected virtual bool IsLineLike => false;

        public override Point[] GetHandles()
        {
            if (IsLineLike) return new[] { P1, P2 };
            var r = Area;
            return new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight };
        }

        public override void DragHandle(int index, Point p)
        {
            if (IsLineLike)
            {
                if (index == 0) SetStartOnly(p); else if (index == 1) SetEnd(p);
                return;
            }
            switch (index)
            {
                case 0: X1 = p.X; Y1 = p.Y; break;
                case 1: X2 = p.X; Y1 = p.Y; break;
                case 2: X1 = p.X; Y2 = p.Y; break;
                case 3: X2 = p.X; Y2 = p.Y; break;
            }
        }

        public override void EndHandleDrag()
        {
            if (!IsLineLike) Normalize();
        }

        [JsonIgnore]
        public double Length => (P2 - P1).Length;
    }

    public sealed class LineAnnotation : TwoPointAnnotation
    {
        public override string TypeName => "Linha";

        protected override bool IsLineLike => true;

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            dc.DrawLine(StrokePen(Thickness), P1, P2);
        }

        public override bool HitTest(Point p) => DistanceToSegment(p, P1, P2) <= Thickness / 2 + 6;
    }

    public sealed class ArrowAnnotation : TwoPointAnnotation
    {
        public override string TypeName => DoubleHead ? "Seta dupla" : "Seta";

        protected override bool IsLineLike => true;

        /// <summary>Arrow heads on both ends.</summary>
        public bool DoubleHead { get; set; }

        /// <summary>Bend: distance of the curve's middle from the straight line (0 = straight).</summary>
        public double Curve { get; set; }

        private Vector Normal
        {
            get
            {
                var v = P2 - P1;
                if (v.Length < 1e-6) return new Vector(0, -1);
                v.Normalize();
                return new Vector(-v.Y, v.X);
            }
        }

        private Point Mid => new Point((X1 + X2) / 2, (Y1 + Y2) / 2);

        /// <summary>Quadratic Bézier control point (the curve passes through Mid + Normal*Curve).</summary>
        private Point Control => Mid + Normal * (Curve * 2);

        private bool IsCurved => Math.Abs(Curve) > 0.5;

        public override Point[] GetHandles() => new[] { P1, P2, Mid + Normal * Curve };

        public override void DragHandle(int index, Point p)
        {
            if (index == 2)
            {
                Curve = Vector.Multiply(p - Mid, Normal);
                return;
            }
            // Keep the bend's absolute position while an end moves.
            var bend = Mid + Normal * Curve;
            base.DragHandle(index, p);
            Curve = Vector.Multiply(bend - Mid, Normal);
        }

        public override void TransformPoints(Func<Point, Point> map)
        {
            var bend = map(Mid + Normal * Curve);
            base.TransformPoints(map);
            Curve = Vector.Multiply(bend - Mid, Normal);
        }

        public override Rect GetBounds()
        {
            var r = Area;
            if (IsCurved) r.Union(Mid + Normal * Curve);
            return r;
        }

        private IEnumerable<Point> Samples(int n = 24)
        {
            var c = Control;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n, u = 1 - t;
                yield return new Point(u * u * X1 + 2 * u * t * c.X + t * t * X2, u * u * Y1 + 2 * u * t * c.Y + t * t * Y2);
            }
        }

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            if (Length < 1) return;
            double head = Math.Max(12, Thickness * 3.6);
            head = Math.Min(head, Length * (DoubleHead ? 0.45 : 0.9));
            var brush = ColorUtil.Brush(StrokeColor);

            // Direction of the line arriving at each end.
            Vector endDir = IsCurved ? P2 - Control : P2 - P1;
            Vector startDir = IsCurved ? P1 - Control : P1 - P2;
            endDir.Normalize();
            startDir.Normalize();

            // Stop the shaft inside the heads so its square end never pokes out.
            Point shaftEnd = P2 - endDir * (head * 0.6);
            Point shaftStart = DoubleHead ? P1 - startDir * (head * 0.6) : P1;

            var shaft = new StreamGeometry();
            using (var gc = shaft.Open())
            {
                gc.BeginFigure(shaftStart, false, false);
                if (IsCurved) gc.QuadraticBezierTo(Control, shaftEnd, true, true);
                else gc.LineTo(shaftEnd, true, true);
            }
            shaft.Freeze();
            dc.DrawGeometry(null, StrokePen(Thickness), shaft);

            DrawHead(dc, brush, P2, endDir, head);
            if (DoubleHead) DrawHead(dc, brush, P1, startDir, head);
        }

        private static void DrawHead(DrawingContext dc, Brush brush, Point tip, Vector dir, double head)
        {
            var n = new Vector(-dir.Y, dir.X);
            double half = head * 0.55;
            Point basePt = tip - dir * head;
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(tip, true, true);
                gc.LineTo(basePt + n * half, true, true);
                gc.LineTo(basePt - n * half, true, true);
            }
            g.Freeze();
            dc.DrawGeometry(brush, null, g);
        }

        public override bool HitTest(Point p)
        {
            double tol = Math.Max(Thickness * 2, 8);
            if (!IsCurved) return DistanceToSegment(p, P1, P2) <= tol;
            Point? prev = null;
            foreach (var q in Samples())
            {
                if (prev is Point a && DistanceToSegment(p, a, q) <= tol) return true;
                prev = q;
            }
            return false;
        }
    }

    public sealed class RectangleAnnotation : TwoPointAnnotation
    {
        public override string TypeName => "Retângulo";

        public bool Filled { get; set; }

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            dc.DrawRectangle(Filled ? ColorUtil.Brush(StrokeColor) : null,
                StrokePen(Thickness, PenLineCap.Square, PenLineJoin.Miter), Area);
        }

        public override bool HitTest(Point p)
        {
            if (Filled) return base.HitTest(p);
            // Hit only near the outline so objects underneath stay selectable.
            var outer = Area; double pad = Thickness / 2 + 6;
            outer.Inflate(pad, pad);
            if (!outer.Contains(p)) return false;
            var inner = Area; inner.Inflate(-pad, -pad);
            return inner.IsEmpty || !inner.Contains(p);
        }
    }

    public sealed class EllipseAnnotation : TwoPointAnnotation
    {
        public override string TypeName => "Elipse";

        public bool Filled { get; set; }

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            var r = Area;
            var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            dc.DrawEllipse(Filled ? ColorUtil.Brush(StrokeColor) : null, StrokePen(Thickness), center, r.Width / 2, r.Height / 2);
        }
    }

    /// <summary>Hides the area underneath by pixelating the base image.</summary>
    public sealed class PixelateAnnotation : TwoPointAnnotation
    {
        public override string TypeName => "Pixelização";

        public double BlockSize { get; set; } = 12;

        public override void ScaleSizes(double factor)
        {
            base.ScaleSizes(factor);
            BlockSize = Math.Max(2, BlockSize * factor);
        }

        private ImageSource? _cache;
        private string? _cacheKey;
        private BitmapSource? _cacheImage;

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            var img = ctx.BaseImage;
            if (img == null) return;

            var r = Area;
            r.Intersect(new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;

            var ir = new Int32Rect((int)Math.Floor(r.X), (int)Math.Floor(r.Y),
                Math.Max(1, (int)Math.Ceiling(r.Width)), Math.Max(1, (int)Math.Ceiling(r.Height)));
            if (ir.X + ir.Width > img.PixelWidth) ir.Width = img.PixelWidth - ir.X;
            if (ir.Y + ir.Height > img.PixelHeight) ir.Height = img.PixelHeight - ir.Y;
            if (ir.Width < 1 || ir.Height < 1) return;

            string key = $"{ir.X},{ir.Y},{ir.Width},{ir.Height},{BlockSize}";
            if (_cache == null || _cacheKey != key || !ReferenceEquals(_cacheImage, img))
            {
                double block = Math.Max(2, BlockSize);
                int sw = Math.Max(1, (int)Math.Ceiling(ir.Width / block));
                int sh = Math.Max(1, (int)Math.Ceiling(ir.Height / block));
                var crop = new CroppedBitmap(img, ir);
                var small = new TransformedBitmap(crop, new ScaleTransform((double)sw / ir.Width, (double)sh / ir.Height));
                small.Freeze();
                _cache = small;
                _cacheKey = key;
                _cacheImage = img;
            }

            var group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
            using (var gc = group.Open())
            {
                gc.DrawImage(_cache, new Rect(ir.X, ir.Y, ir.Width, ir.Height));
            }
            group.Freeze();
            dc.DrawDrawing(group);
        }
    }

    /// <summary>Freehand pen stroke, or a translucent highlighter stroke.</summary>
    public sealed class FreehandAnnotation : Annotation
    {
        public override string TypeName => Highlighter ? "Marca-texto" : "Caneta";

        public List<Pt> Points { get; set; } = new List<Pt>();
        public bool Highlighter { get; set; }

        private double EffectiveThickness => Highlighter ? Math.Max(10, Thickness * 4) : Thickness;

        public void AddPoint(Point p) => Points.Add(new Pt(p.X, p.Y));

        public override Rect GetBounds()
        {
            if (Points.Count == 0) return Rect.Empty;
            double minX = Points.Min(p => p.X), minY = Points.Min(p => p.Y);
            double maxX = Points.Max(p => p.X), maxY = Points.Max(p => p.Y);
            var r = new Rect(new Point(minX, minY), new Point(maxX, maxY));
            double t = EffectiveThickness / 2;
            r.Inflate(t, t);
            return r;
        }

        public override void Offset(double dx, double dy)
        {
            foreach (var p in Points) { p.X += dx; p.Y += dy; }
        }

        public override void TransformPoints(Func<Point, Point> map)
        {
            foreach (var p in Points)
            {
                var q = map(p.ToPoint());
                p.X = q.X; p.Y = q.Y;
            }
        }

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            if (Points.Count == 0) return;
            var color = Highlighter ? ColorUtil.WithAlpha(StrokeColor, 0x70) : StrokeColor;
            var pen = Highlighter
                ? MakePen(color, EffectiveThickness, PenLineCap.Square, PenLineJoin.Round)
                : MakePen(color, EffectiveThickness);

            if (Points.Count == 1)
            {
                var p = Points[0].ToPoint();
                dc.DrawLine(pen, p, new Point(p.X + 0.01, p.Y));
                return;
            }

            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(Points[0].ToPoint(), false, false);
                gc.PolyLineTo(Points.Skip(1).Select(p => p.ToPoint()).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }

        public override bool HitTest(Point p)
        {
            double tol = EffectiveThickness / 2 + 5;
            if (Points.Count == 1) return (Points[0].ToPoint() - p).Length <= tol;
            for (int i = 1; i < Points.Count; i++)
            {
                if (DistanceToSegment(p, Points[i - 1].ToPoint(), Points[i].ToPoint()) <= tol) return true;
            }
            return false;
        }
    }

    /// <summary>Plain text, or a "callout" (text on a filled rounded box, optionally with a pointer).</summary>
    public sealed class TextAnnotation : Annotation
    {
        public override string TypeName => Callout ? "Balão" : "Texto";
        public override string DefaultName => $"{TypeName}: {Shorten(Text)}";

        public double X { get; set; }
        public double Y { get; set; }
        public string Text { get; set; } = "";
        public double FontSize { get; set; } = 24;
        public bool Callout { get; set; }
        public string FontFamilyName { get; set; } = "Segoe UI";
        public bool Bold { get; set; } = true;
        public bool Italic { get; set; }

        /// <summary>Callout pointer tip (null = no pointer).</summary>
        public double? TailX { get; set; }
        public double? TailY { get; set; }

        [JsonIgnore] public double Padding => Math.Round(FontSize * 0.45);

        [JsonIgnore]
        public Typeface Typeface => new Typeface(new FontFamily(string.IsNullOrWhiteSpace(FontFamilyName) ? "Segoe UI" : FontFamilyName),
            Italic ? FontStyles.Italic : FontStyles.Normal, Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);

        private FormattedText Build(Brush brush)
        {
            return new FormattedText(string.IsNullOrEmpty(Text) ? " " : Text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, Typeface, FontSize, brush, 1.0);
        }

        /// <summary>Box around the text (without the pointer).</summary>
        public Rect GetBox()
        {
            var ft = Build(Brushes.Black);
            double w = ft.WidthIncludingTrailingWhitespace, h = ft.Height;
            return Callout
                ? new Rect(X, Y, w + Padding * 2, h + Padding * 2)
                : new Rect(X, Y, w, h);
        }

        public override Rect GetBounds()
        {
            var r = GetBox();
            if (Callout && TailX.HasValue && TailY.HasValue) r.Union(new Point(TailX.Value, TailY.Value));
            return r;
        }

        public override Point[] GetHandles() =>
            Callout && TailX.HasValue && TailY.HasValue ? new[] { new Point(TailX.Value, TailY.Value) } : Array.Empty<Point>();

        public override void DragHandle(int index, Point p)
        {
            TailX = p.X;
            TailY = p.Y;
        }

        public override void Offset(double dx, double dy)
        {
            X += dx; Y += dy;
            if (TailX.HasValue) TailX += dx;
            if (TailY.HasValue) TailY += dy;
        }

        public override void TransformPoints(Func<Point, Point> map)
        {
            // Keep the text upright: move it so its centre follows the transform.
            var b = GetBox();
            var c = map(new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
            X = c.X - b.Width / 2;
            Y = c.Y - b.Height / 2;
            if (TailX.HasValue && TailY.HasValue)
            {
                var t = map(new Point(TailX.Value, TailY.Value));
                TailX = t.X; TailY = t.Y;
            }
        }

        public override void ScaleSizes(double factor)
        {
            base.ScaleSizes(factor);
            FontSize = Math.Max(6, FontSize * factor);
        }

        /// <summary>Rounded box merged with a triangular pointer towards the tail.</summary>
        private Geometry CalloutGeometry(Rect box)
        {
            Geometry shape = new RectangleGeometry(box, 8, 8);
            if (!TailX.HasValue || !TailY.HasValue) return shape;
            var tail = new Point(TailX.Value, TailY.Value);
            if (box.Contains(tail)) return shape;

            var c = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
            Vector d = tail - c;
            double baseHalf = Math.Max(6, Math.Min(box.Width, box.Height) * 0.22);
            Point a, b;
            // Pick the side of the box facing the tail.
            if (Math.Abs(d.X) * box.Height > Math.Abs(d.Y) * box.Width)
            {
                double x = d.X > 0 ? box.Right - 1 : box.Left + 1;
                double y = Math.Max(box.Top + baseHalf + 4, Math.Min(box.Bottom - baseHalf - 4, tail.Y));
                a = new Point(x, y - baseHalf); b = new Point(x, y + baseHalf);
            }
            else
            {
                double y = d.Y > 0 ? box.Bottom - 1 : box.Top + 1;
                double x = Math.Max(box.Left + baseHalf + 4, Math.Min(box.Right - baseHalf - 4, tail.X));
                a = new Point(x - baseHalf, y); b = new Point(x + baseHalf, y);
            }
            var tri = new StreamGeometry();
            using (var gc = tri.Open())
            {
                gc.BeginFigure(a, true, true);
                gc.LineTo(tail, true, true);
                gc.LineTo(b, true, true);
            }
            tri.Freeze();
            return new CombinedGeometry(GeometryCombineMode.Union, shape, tri);
        }

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            var color = StrokeColor;
            if (Callout)
            {
                var box = GetBox();
                dc.DrawGeometry(ColorUtil.Brush(color), null, CalloutGeometry(box));
                var fg = ColorUtil.IsLight(color) ? Brushes.Black : Brushes.White;
                dc.DrawText(Build(fg), new Point(X + Padding, Y + Padding));
            }
            else
            {
                // A soft halo keeps text legible over busy screenshots.
                var ft = Build(ColorUtil.Brush(color));
                var outline = ft.BuildGeometry(new Point(X, Y));
                var haloColor = ColorUtil.IsLight(color) ? System.Windows.Media.Color.FromArgb(150, 0, 0, 0) : System.Windows.Media.Color.FromArgb(170, 255, 255, 255);
                dc.DrawGeometry(null, MakePen(haloColor, Math.Max(2, FontSize / 9)), outline);
                dc.DrawText(ft, new Point(X, Y));
            }
        }
    }

    /// <summary>Numbered circle for step-by-step instructions.</summary>
    public sealed class StepAnnotation : Annotation
    {
        public override string TypeName => "Passo";
        public override string DefaultName => $"Passo {Number}";

        public double X { get; set; }
        public double Y { get; set; }
        public int Number { get; set; } = 1;
        public double Radius { get; set; } = 18;

        public override Rect GetBounds() => new Rect(X - Radius, Y - Radius, Radius * 2, Radius * 2);

        public override void Offset(double dx, double dy) { X += dx; Y += dy; }

        public override void TransformPoints(Func<Point, Point> map)
        {
            var c = map(new Point(X, Y));
            X = c.X; Y = c.Y;
        }

        public override void ScaleSizes(double factor)
        {
            base.ScaleSizes(factor);
            Radius = Math.Max(6, Radius * factor);
        }

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            var color = StrokeColor;
            var center = new Point(X, Y);
            dc.DrawEllipse(ColorUtil.Brush(color), MakePen(Colors.White, Math.Max(2, Radius / 8)), center, Radius, Radius);

            var fg = ColorUtil.IsLight(color) ? Brushes.Black : Brushes.White;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, Radius * 1.1, fg, 1.0);
            dc.DrawText(ft, new Point(X - ft.Width / 2, Y - ft.Height / 2));
        }
    }
}

namespace ShotMark.Core
{
    /// <summary>Smooth blur of the area underneath (downscale + bilinear upscale).</summary>
    public sealed class BlurAnnotation : TwoPointAnnotation
    {
        public override string TypeName => "Desfoque";

        public double Strength { get; set; } = 10;

        private ImageSource? _cache;
        private string? _cacheKey;
        private BitmapSource? _cacheImage;

        public override void ScaleSizes(double factor)
        {
            base.ScaleSizes(factor);
            Strength = Math.Max(2, Strength * factor);
        }

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            var img = ctx.BaseImage;
            if (img == null) return;

            var r = Area;
            r.Intersect(new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) return;

            var ir = new Int32Rect((int)Math.Floor(r.X), (int)Math.Floor(r.Y),
                Math.Max(1, (int)Math.Ceiling(r.Width)), Math.Max(1, (int)Math.Ceiling(r.Height)));
            if (ir.X + ir.Width > img.PixelWidth) ir.Width = img.PixelWidth - ir.X;
            if (ir.Y + ir.Height > img.PixelHeight) ir.Height = img.PixelHeight - ir.Y;
            if (ir.Width < 1 || ir.Height < 1) return;

            string key = $"{ir.X},{ir.Y},{ir.Width},{ir.Height},{Strength}";
            if (_cache == null || _cacheKey != key || !ReferenceEquals(_cacheImage, img))
            {
                double k = Math.Max(2, Strength);
                int sw = Math.Max(1, (int)Math.Ceiling(ir.Width / k));
                int sh = Math.Max(1, (int)Math.Ceiling(ir.Height / k));
                var crop = new CroppedBitmap(img, ir);
                var small = new TransformedBitmap(crop, new ScaleTransform((double)sw / ir.Width, (double)sh / ir.Height));
                small.Freeze();
                _cache = small;
                _cacheKey = key;
                _cacheImage = img;
            }

            var group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.Linear);
            using (var gc = group.Open())
            {
                gc.PushClip(new RectangleGeometry(new Rect(ir.X, ir.Y, ir.Width, ir.Height)));
                gc.DrawImage(_cache, new Rect(ir.X, ir.Y, ir.Width, ir.Height));
                gc.Pop();
            }
            group.Freeze();
            dc.DrawDrawing(group);
        }
    }

    /// <summary>Darkens everything outside the area to draw attention to it.</summary>
    public sealed class SpotlightAnnotation : TwoPointAnnotation
    {
        public override string TypeName => "Holofote";

        /// <summary>How dark the outside gets, 0–1.</summary>
        public double Dim { get; set; } = 0.6;

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            var img = ctx.BaseImage;
            if (img == null) return;
            var full = new Rect(0, 0, img.PixelWidth, img.PixelHeight);
            var hole = new RectangleGeometry(Area, 6, 6);
            var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), hole);
            var brush = ColorUtil.Brush(System.Windows.Media.Color.FromArgb((byte)(255 * Math.Max(0, Math.Min(1, Dim))), 0, 0, 0));
            dc.DrawGeometry(brush, null, dim);
            dc.DrawGeometry(null, MakePen(System.Windows.Media.Color.FromArgb(200, 255, 255, 255), 1.5), hole);
        }

        public override bool HitTest(Point p)
        {
            // Only the frame is grabbable, so whatever is inside stays editable.
            var outer = Area; outer.Inflate(8, 8);
            var inner = Area; inner.Inflate(-8, -8);
            return outer.Contains(p) && (inner.IsEmpty || !inner.Contains(p));
        }
    }
}

namespace ShotMark.Core
{
    /// <summary>Round magnifying glass that enlarges the area it covers.</summary>
    public sealed class MagnifyAnnotation : TwoPointAnnotation
    {
        public override string TypeName => "Lupa";

        public double Zoom { get; set; } = 2.0;

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            var img = ctx.BaseImage;
            var r = Area;
            if (img == null || r.Width < 4 || r.Height < 4) return;
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            var circle = new EllipseGeometry(c, r.Width / 2, r.Height / 2);

            dc.PushClip(circle);
            double z = Math.Max(1.1, Zoom);
            dc.DrawImage(img, new Rect(c.X - c.X * z, c.Y - c.Y * z, img.PixelWidth * z, img.PixelHeight * z));
            dc.Pop();
            dc.DrawGeometry(null, MakePen(StrokeColor, Math.Max(2, Thickness)), circle);
        }

        public override bool HitTest(Point p)
        {
            var r = Area;
            if (r.Width < 1 || r.Height < 1) return false;
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            double dx = (p.X - c.X) / (r.Width / 2 + 6), dy = (p.Y - c.Y) / (r.Height / 2 + 6);
            return dx * dx + dy * dy <= 1;
        }
    }

    /// <summary>Ready-made symbols: check, cross, warning, info, question, star.</summary>
    public sealed class StampAnnotation : Annotation
    {
        public static readonly string[] Kinds = { "check", "cross", "warning", "info", "question", "star" };

        public override string TypeName => Kind switch
        {
            "check" => "Carimbo: certo",
            "cross" => "Carimbo: errado",
            "warning" => "Carimbo: atenção",
            "info" => "Carimbo: informação",
            "question" => "Carimbo: dúvida",
            _ => "Carimbo: estrela",
        };

        public double X { get; set; }
        public double Y { get; set; }
        public double Size { get; set; } = 44;
        public string Kind { get; set; } = "check";

        private double R => Size / 2;

        public override Rect GetBounds() => new Rect(X - R, Y - R, Size, Size);
        public override void Offset(double dx, double dy) { X += dx; Y += dy; }

        public override void TransformPoints(Func<Point, Point> map)
        {
            var c = map(new Point(X, Y));
            X = c.X; Y = c.Y;
        }

        public override void ScaleSizes(double factor)
        {
            base.ScaleSizes(factor);
            Size = Math.Max(10, Size * factor);
        }

        public override Point[] GetHandles() => new[] { new Point(X + R, Y + R) };

        public override void DragHandle(int index, Point p)
        {
            Size = Math.Max(14, 2 * Math.Max(Math.Abs(p.X - X), Math.Abs(p.Y - Y)));
        }

        private static System.Windows.Media.Color C(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);

        public override void Render(DrawingContext dc, RenderContext ctx)
        {
            var center = new Point(X, Y);
            double r = R;
            var white = MakePen(Colors.White, Math.Max(2, Size / 11));
            switch (Kind)
            {
                case "warning":
                {
                    var tri = new StreamGeometry();
                    using (var gc = tri.Open())
                    {
                        gc.BeginFigure(new Point(X, Y - r), true, true);
                        gc.LineTo(new Point(X + r, Y + r * 0.85), true, true);
                        gc.LineTo(new Point(X - r, Y + r * 0.85), true, true);
                    }
                    tri.Freeze();
                    dc.DrawGeometry(ColorUtil.Brush(C(0xF5, 0xB0, 0x0B)), MakePen(Colors.White, Math.Max(2, Size / 16), PenLineCap.Round, PenLineJoin.Round), tri);
                    var mark = MakePen(C(0x1F, 0x21, 0x25), Math.Max(2, Size / 10));
                    dc.DrawLine(mark, new Point(X, Y - r * 0.35), new Point(X, Y + r * 0.25));
                    dc.DrawEllipse(ColorUtil.Brush(C(0x1F, 0x21, 0x25)), null, new Point(X, Y + r * 0.55), Size / 16, Size / 16);
                    break;
                }
                case "star":
                {
                    var star = new StreamGeometry();
                    using (var gc = star.Open())
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            double ang = -Math.PI / 2 + i * Math.PI / 5;
                            double rad = i % 2 == 0 ? r : r * 0.45;
                            var pt = new Point(X + Math.Cos(ang) * rad, Y + Math.Sin(ang) * rad);
                            if (i == 0) gc.BeginFigure(pt, true, true); else gc.LineTo(pt, true, true);
                        }
                    }
                    star.Freeze();
                    dc.DrawGeometry(ColorUtil.Brush(C(0xFA, 0xCC, 0x15)), MakePen(Colors.White, Math.Max(2, Size / 18), PenLineCap.Round, PenLineJoin.Round), star);
                    break;
                }
                default:
                {
                    var fill = Kind switch
                    {
                        "check" => C(0x22, 0xA0, 0x55),
                        "cross" => C(0xE5, 0x39, 0x35),
                        "info" => C(0x3B, 0x82, 0xF6),
                        _ => C(0x8E, 0x24, 0xAA),
                    };
                    dc.DrawEllipse(ColorUtil.Brush(fill), MakePen(Colors.White, Math.Max(2, Size / 16)), center, r, r);
                    double k = r * 0.45;
                    if (Kind == "check")
                    {
                        var g = new StreamGeometry();
                        using (var gc = g.Open())
                        {
                            gc.BeginFigure(new Point(X - k, Y + k * 0.05), false, false);
                            gc.LineTo(new Point(X - k * 0.25, Y + k * 0.75), true, true);
                            gc.LineTo(new Point(X + k, Y - k * 0.65), true, true);
                        }
                        g.Freeze();
                        dc.DrawGeometry(null, white, g);
                    }
                    else if (Kind == "cross")
                    {
                        dc.DrawLine(white, new Point(X - k * 0.8, Y - k * 0.8), new Point(X + k * 0.8, Y + k * 0.8));
                        dc.DrawLine(white, new Point(X + k * 0.8, Y - k * 0.8), new Point(X - k * 0.8, Y + k * 0.8));
                    }
                    else
                    {
                        string glyph = Kind == "info" ? "i" : "?";
                        var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, Size * 0.62, Brushes.White, 1.0);
                        dc.DrawText(ft, new Point(X - ft.Width / 2, Y - ft.Height / 2));
                    }
                    break;
                }
            }
        }
    }
}
