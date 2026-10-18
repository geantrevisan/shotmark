using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotMark.Core;

namespace ShotMark.Editor
{
    public enum EditorTool
    {
        Select,
        Arrow,
        Line,
        Rectangle,
        Ellipse,
        Pen,
        Highlighter,
        Text,
        Callout,
        Step,
        Stamp,
        Magnify,
        Pixelate,
        Blur,
        Spotlight,
        Crop,
    }

    /// <summary>Asks the host to show an in-place text editor for a text/callout layer.</summary>
    public sealed class TextEditRequest : EventArgs
    {
        public TextEditRequest(TextAnnotation annotation, bool isNew)
        {
            Annotation = annotation;
            IsNew = isNew;
        }

        public TextAnnotation Annotation { get; }
        public bool IsNew { get; }
    }

    /// <summary>
    /// Draws the capture and its annotations and turns mouse gestures into annotations.
    /// One WPF unit == one image pixel (images are normalised to 96 DPI); zoom is applied outside.
    /// </summary>
    public sealed class EditorCanvas : FrameworkElement
    {
        private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
        private static readonly Brush MarqueeFill = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6)));
        private static readonly Pen CropPen = CreateDashedPen(Colors.White, 1.5);
        private static readonly Brush CropDim = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));

        private readonly RenderContext _ctx = new RenderContext();
        private readonly List<Annotation> _selection = new List<Annotation>();
        private EditorTool _tool = EditorTool.Arrow;
        private Annotation? _drawing;
        private Point _start;
        private Point _last;
        private bool _moving;
        private bool _moveSnapshotTaken;
        private Rect? _cropRect;
        private Rect? _marquee;
        private bool _marqueeAdditive;
        private int _handle = -1;
        private bool _handleSnapshotTaken;
        private int? _stepCounter;

        public EditorCanvas()
        {
            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;
            Cursor = Cursors.Cross;
        }

        public CaptureDocument? Document { get; private set; }

        // ---- current style for new layers ----
        public Color CurrentColor { get; set; } = Color.FromRgb(0xE5, 0x39, 0x35);
        public double CurrentThickness { get; set; } = 4;
        public double CurrentFontSize { get; set; } = 24;
        public bool CurrentFilled { get; set; }
        public double CurrentOpacity { get; set; } = 1.0;
        public bool CurrentDashed { get; set; }
        public bool CurrentDoubleHead { get; set; }
        public string CurrentFontFamily { get; set; } = "Segoe UI";
        public bool CurrentBold { get; set; } = true;
        public bool CurrentItalic { get; set; }
        public string CurrentStamp { get; set; } = "check";

        /// <summary>Current zoom of the host (keeps handles a constant size on screen).</summary>
        public double Zoom { get; set; } = 1.0;

        /// <summary>Text layer currently being edited in place (hidden while editing).</summary>
        public TextAnnotation? EditingText { get; set; }

        /// <summary>Raised right before the document changes so the host can record an undo step.</summary>
        public event EventHandler? BeforeChange;

        /// <summary>Raised after the document changed.</summary>
        public event EventHandler? Changed;

        public event EventHandler? SelectionChanged;

        public event EventHandler<TextEditRequest>? TextEditRequested;

        public EditorTool Tool
        {
            get => _tool;
            set
            {
                _tool = value;
                _drawing = null;
                _cropRect = null;
                _marquee = null;
                if (value != EditorTool.Select) ClearSelection();
                Cursor = value == EditorTool.Select ? Cursors.Arrow : Cursors.Cross;
                InvalidateVisual();
            }
        }

        /// <summary>Primary (last picked) selected layer.</summary>
        public Annotation? Selected => _selection.Count > 0 ? _selection[^1] : null;

        public IReadOnlyList<Annotation> SelectedItems => _selection;

        public void SetDocument(CaptureDocument? doc)
        {
            Document = doc;
            _drawing = null;
            _cropRect = null;
            _marquee = null;
            _moving = false;
            _handle = -1;
            _stepCounter = null;
            EditingText = null;
            ClearSelection();
            UpdateSize();
            InvalidateVisual();
        }

        public void UpdateSize()
        {
            Width = Document?.Image.PixelWidth ?? 0;
            Height = Document?.Image.PixelHeight ?? 0;
        }

        public void Refresh() => InvalidateVisual();

        // ---- selection ----------------------------------------------------------------

        public void Select(Annotation? a)
        {
            if (_selection.Count == 1 && ReferenceEquals(_selection[0], a)) return;
            if (a == null && _selection.Count == 0) return;
            _selection.Clear();
            if (a != null) _selection.Add(a);
            OnSelectionChanged();
        }

        public void SetSelection(IEnumerable<Annotation> items)
        {
            var list = items.Distinct().ToList();
            if (list.SequenceEqual(_selection)) return;
            _selection.Clear();
            _selection.AddRange(list);
            OnSelectionChanged();
        }

        public void ClearSelection() => Select(null);

        public void SelectAll()
        {
            if (Document == null) return;
            SetSelection(Document.Annotations.Where(a => a.Visible && !a.Locked));
        }

        private void ToggleSelection(Annotation a)
        {
            if (_selection.Contains(a)) _selection.Remove(a); else _selection.Add(a);
            OnSelectionChanged();
        }

        private void OnSelectionChanged()
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        // ---- editing operations used by the window ------------------------------

        public void DeleteSelected()
        {
            if (Document == null || _selection.Count == 0) return;
            RaiseBefore();
            foreach (var a in _selection) Document.Annotations.Remove(a);
            _selection.Clear();
            OnSelectionChanged();
            RaiseChanged();
        }

        public void NudgeSelected(double dx, double dy)
        {
            var movable = _selection.Where(a => !a.Locked).ToList();
            if (movable.Count == 0) return;
            RaiseBefore();
            foreach (var a in movable) a.Offset(dx, dy);
            RaiseChanged();
        }

        /// <summary>Moves the selected layers up (+1, towards the front) or down (-1).</summary>
        public void MoveSelection(int delta)
        {
            if (Document == null || _selection.Count == 0) return;
            var list = Document.Annotations;
            var ordered = _selection.OrderBy(a => list.IndexOf(a)).ToList();
            if (delta > 0) ordered.Reverse();
            bool any = ordered.Any(a =>
            {
                int j = list.IndexOf(a) + delta;
                return j >= 0 && j < list.Count && !_selection.Contains(list[j]);
            });
            if (!any) return;
            RaiseBefore();
            foreach (var a in ordered)
            {
                int i = list.IndexOf(a), j = i + delta;
                if (j < 0 || j >= list.Count || _selection.Contains(list[j])) continue;
                list.RemoveAt(i);
                list.Insert(j, a);
            }
            RaiseChanged();
        }

        public void DuplicateSelection()
        {
            if (Document == null || _selection.Count == 0) return;
            var copies = AnnotationSerializer.Clone(_selection.ToList());
            RaiseBefore();
            foreach (var c in copies)
            {
                c.Offset(14, 14);
                c.Locked = false;
                c.Visible = true;
                if (!string.IsNullOrWhiteSpace(c.Name)) c.Name += " (cópia)";
                Document.Annotations.Add(c);
            }
            _selection.Clear();
            _selection.AddRange(copies);
            OnSelectionChanged();
            RaiseChanged();
        }

        /// <summary>Serialised copy of the selected layers (for the clipboard).</summary>
        public string? CopySelection() => _selection.Count == 0 ? null : AnnotationSerializer.Serialize(_selection.ToList());

        /// <summary>Pastes layers copied with <see cref="CopySelection"/>.</summary>
        public bool PasteLayers(string json)
        {
            if (Document == null) return false;
            List<Annotation> items;
            try { items = AnnotationSerializer.Deserialize(json); }
            catch (Exception) { return false; }
            if (items.Count == 0) return false;
            RaiseBefore();
            foreach (var a in items)
            {
                a.Offset(16, 16);
                a.Locked = false;
                Document.Annotations.Add(a);
            }
            _selection.Clear();
            _selection.AddRange(items);
            OnSelectionChanged();
            RaiseChanged();
            return true;
        }

        /// <summary>Adds a layer (optionally at the bottom, under the other annotations) as one undo step.</summary>
        public void AddLayer(Annotation a, bool atBottom = false)
        {
            if (Document == null) return;
            RaiseBefore();
            if (atBottom) Document.Annotations.Insert(0, a); else Document.Annotations.Add(a);
            _selection.Clear();
            _selection.Add(a);
            OnSelectionChanged();
            RaiseChanged();
        }

        /// <summary>Applies a property change to the given layers as one undo step.</summary>
        public void ChangeLayers(IEnumerable<Annotation> layers, Action<Annotation> change)
        {
            var list = layers.ToList();
            if (Document == null || list.Count == 0) return;
            RaiseBefore();
            foreach (var a in list) change(a);
            RaiseChanged();
        }

        public void ChangeLayer(Annotation a, Action<Annotation> change) => ChangeLayers(new[] { a }, change);

        public void Delete(Annotation a)
        {
            if (Document == null) return;
            RaiseBefore();
            Document.Annotations.Remove(a);
            if (_selection.Remove(a)) OnSelectionChanged();
            RaiseChanged();
        }

        /// <summary>Next step badge restarts from 1.</summary>
        public void RestartNumbering() => _stepCounter = 1;

        public void ApplyCrop(Rect r)
        {
            if (Document == null) return;
            var img = Document.Image;
            int x = (int)Math.Round(Math.Max(0, r.X));
            int y = (int)Math.Round(Math.Max(0, r.Y));
            int w = (int)Math.Round(Math.Min(img.PixelWidth, r.Right)) - x;
            int h = (int)Math.Round(Math.Min(img.PixelHeight, r.Bottom)) - y;
            if (w < 2 || h < 2) return;

            RaiseBefore();
            Document.Image = ImageUtil.Crop(img, new Int32Rect(x, y, w, h));
            foreach (var a in Document.Annotations) a.Offset(-x, -y);
            ClearSelection();
            UpdateSize();
            RaiseChanged();
        }

        /// <summary>
        /// Replaces the base image (rotate, flip, resize, border…) and maps every layer accordingly, as one undo step.
        /// </summary>
        public void TransformImage(Func<BitmapSource, BitmapSource> imageOp, Func<Point, Point>? pointMap, double sizeScale = 1.0)
        {
            if (Document == null) return;
            var newImage = imageOp(Document.Image);
            RaiseBefore();
            foreach (var a in Document.Annotations)
            {
                if (pointMap != null) a.TransformPoints(pointMap);
                if (Math.Abs(sizeScale - 1.0) > 0.001) a.ScaleSizes(sizeScale);
                a.EndHandleDrag();
            }
            Document.Image = newImage;
            ClearSelection();
            UpdateSize();
            RaiseChanged();
        }

        // ---- in-place text editing ----------------------------------------------------

        public void BeginEditText(TextAnnotation ta, bool isNew)
        {
            EditingText = ta;
            InvalidateVisual();
            TextEditRequested?.Invoke(this, new TextEditRequest(ta, isNew));
        }

        /// <summary>Called by the host when the in-place editor closes.</summary>
        public void EndEditText(TextAnnotation ta, string? text, bool isNew)
        {
            EditingText = null;
            if (Document == null || text == null)
            {
                InvalidateVisual();
                return;
            }

            if (isNew)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    RaiseBefore();
                    ta.Text = text;
                    Document.Annotations.Add(ta);
                    RaiseChanged();
                }
            }
            else if (text != ta.Text)
            {
                RaiseBefore();
                if (string.IsNullOrWhiteSpace(text))
                {
                    Document.Annotations.Remove(ta);
                    if (_selection.Remove(ta)) OnSelectionChanged();
                }
                else
                {
                    ta.Text = text;
                }
                RaiseChanged();
            }
            InvalidateVisual();
        }

        private void RaiseBefore() => BeforeChange?.Invoke(this, EventArgs.Empty);

        private void RaiseChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        // ---- mouse ---------------------------------------------------------------

        private Point Clamp(Point p)
        {
            if (Document == null) return p;
            return new Point(
                Math.Max(0, Math.Min(Document.Image.PixelWidth, p.X)),
                Math.Max(0, Math.Min(Document.Image.PixelHeight, p.Y)));
        }

        private Annotation? HitTestAnnotations(Point p)
        {
            if (Document == null) return null;
            // Topmost (last drawn) first.
            for (int i = Document.Annotations.Count - 1; i >= 0; i--)
            {
                var a = Document.Annotations[i];
                if (a.Visible && !a.Locked && a.HitTest(p)) return a;
            }
            return null;
        }

        private double HandleSize => 10 / Math.Max(0.05, Zoom);

        private int HandleAt(Point p)
        {
            if (_tool != EditorTool.Select || _selection.Count != 1) return -1;
            var a = _selection[0];
            if (a.Locked || !a.Visible) return -1;
            var pts = a.GetHandles();
            double tol = HandleSize * 0.8;
            for (int i = pts.Length - 1; i >= 0; i--)
            {
                if (Math.Abs(pts[i].X - p.X) <= tol && Math.Abs(pts[i].Y - p.Y) <= tol) return i;
            }
            return -1;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (Document == null) return;
            Focus();
            var p = Clamp(e.GetPosition(this));
            _start = p;
            _last = p;
            bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            switch (_tool)
            {
                case EditorTool.Select:
                {
                    int h = HandleAt(p);
                    if (h >= 0)
                    {
                        _handle = h;
                        _handleSnapshotTaken = false;
                        CaptureMouse();
                        break;
                    }

                    var hit = HitTestAnnotations(p);
                    if (e.ClickCount == 2 && hit is TextAnnotation ta)
                    {
                        Select(ta);
                        BeginEditText(ta, isNew: false);
                        break;
                    }

                    if (hit == null)
                    {
                        // Empty area: rubber-band selection.
                        if (!additive) ClearSelection();
                        _marquee = new Rect(p, p);
                        _marqueeAdditive = additive;
                        CaptureMouse();
                        break;
                    }

                    if (additive)
                    {
                        ToggleSelection(hit);
                        break;
                    }

                    if (!_selection.Contains(hit)) Select(hit);
                    _moving = true;
                    _moveSnapshotTaken = false;
                    CaptureMouse();
                    break;
                }

                case EditorTool.Text:
                case EditorTool.Callout:
                {
                    // Clicking an existing text edits it instead of creating a new one.
                    if (HitTestAnnotations(p) is TextAnnotation existing)
                    {
                        BeginEditText(existing, isNew: false);
                        break;
                    }
                    bool callout = _tool == EditorTool.Callout;
                    var ta = new TextAnnotation
                    {
                        X = p.X,
                        Y = p.Y,
                        FontSize = CurrentFontSize,
                        Callout = callout,
                        FontFamilyName = CurrentFontFamily,
                        Bold = CurrentBold,
                        Italic = CurrentItalic,
                        ColorHex = ColorUtil.ToHex(CurrentColor),
                        Thickness = CurrentThickness,
                        Opacity = CurrentOpacity,
                    };
                    if (callout)
                    {
                        // Default pointer: below-left of the box.
                        ta.TailX = p.X - CurrentFontSize * 0.6;
                        ta.TailY = p.Y + CurrentFontSize * 3.4;
                    }
                    BeginEditText(ta, isNew: true);
                    break;
                }

                case EditorTool.Step:
                {
                    int next = _stepCounter ?? (Document.Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1);
                    if (_stepCounter.HasValue) _stepCounter++;
                    RaiseBefore();
                    Document.Annotations.Add(new StepAnnotation
                    {
                        X = p.X,
                        Y = p.Y,
                        Number = next,
                        Radius = 12 + CurrentThickness * 1.5,
                        ColorHex = ColorUtil.ToHex(CurrentColor),
                        Thickness = CurrentThickness,
                        Opacity = CurrentOpacity,
                    });
                    RaiseChanged();
                    break;
                }

                case EditorTool.Stamp:
                {
                    RaiseBefore();
                    Document.Annotations.Add(new StampAnnotation
                    {
                        X = p.X,
                        Y = p.Y,
                        Size = 28 + CurrentThickness * 4,
                        Kind = CurrentStamp,
                        Opacity = CurrentOpacity,
                    });
                    RaiseChanged();
                    break;
                }

                case EditorTool.Crop:
                    _cropRect = new Rect(p, p);
                    CaptureMouse();
                    break;

                default:
                    _drawing = CreateAnnotation(_tool, p);
                    CaptureMouse();
                    break;
            }

            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Document == null) return;
            var p = Clamp(e.GetPosition(this));
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            bool pressed = e.LeftButton == MouseButtonState.Pressed;

            if (_handle >= 0 && pressed && Selected is Annotation resizing)
            {
                if (!_handleSnapshotTaken)
                {
                    RaiseBefore();
                    _handleSnapshotTaken = true;
                }
                resizing.DragHandle(_handle, p);
                InvalidateVisual();
            }
            else if (_moving && pressed && _selection.Count > 0)
            {
                if (!_moveSnapshotTaken)
                {
                    RaiseBefore();
                    _moveSnapshotTaken = true;
                }
                foreach (var a in _selection)
                {
                    if (!a.Locked) a.Offset(p.X - _last.X, p.Y - _last.Y);
                }
                _last = p;
                InvalidateVisual();
            }
            else if (_marquee != null && pressed)
            {
                _marquee = new Rect(_start, p);
                InvalidateVisual();
            }
            else if (_drawing is TwoPointAnnotation tp)
            {
                tp.SetEnd(shift || tp is MagnifyAnnotation ? Constrain(tp, p) : p);
                InvalidateVisual();
            }
            else if (_drawing is FreehandAnnotation fh)
            {
                if ((p - _last).Length >= 1.5)
                {
                    fh.AddPoint(p);
                    _last = p;
                    InvalidateVisual();
                }
            }
            else if (_cropRect != null)
            {
                _cropRect = new Rect(_start, p);
                InvalidateVisual();
            }
            else if (_tool == EditorTool.Select)
            {
                int h = HandleAt(p);
                if (h >= 0) Cursor = Cursors.Cross;
                else Cursor = HitTestAnnotations(p) != null ? Cursors.SizeAll : Cursors.Arrow;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (Document == null) return;
            ReleaseMouseCapture();
            FinishGestures();

            if (_drawing != null)
            {
                var d = _drawing;
                _drawing = null;
                if (IsMeaningful(d))
                {
                    RaiseBefore();
                    Document.Annotations.Add(d);
                    RaiseChanged();
                }
            }

            if (_marquee is Rect m)
            {
                _marquee = null;
                if (m.Width > 2 || m.Height > 2)
                {
                    var hits = Document.Annotations.Where(a => a.Visible && !a.Locked && m.IntersectsWith(a.GetBounds())).ToList();
                    SetSelection(_marqueeAdditive ? _selection.Concat(hits).ToList() : hits);
                }
            }

            if (_cropRect is Rect cr)
            {
                _cropRect = null;
                if (cr.Width >= 4 && cr.Height >= 4) ApplyCrop(cr);
            }

            InvalidateVisual();
            e.Handled = true;
        }

        private void FinishGestures()
        {
            if (_handle >= 0)
            {
                _handle = -1;
                if (_handleSnapshotTaken)
                {
                    Selected?.EndHandleDrag();
                    _handleSnapshotTaken = false;
                    RaiseChanged();
                }
            }
            if (_moving)
            {
                _moving = false;
                if (_moveSnapshotTaken)
                {
                    _moveSnapshotTaken = false;
                    RaiseChanged();
                }
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            // If capture is lost mid-gesture (e.g. Alt+Tab), commit what we have.
            FinishGestures();
        }

        private Annotation CreateAnnotation(EditorTool tool, Point p)
        {
            string color = ColorUtil.ToHex(CurrentColor);
            Annotation a = tool switch
            {
                EditorTool.Arrow => new ArrowAnnotation { DoubleHead = CurrentDoubleHead },
                EditorTool.Line => new LineAnnotation(),
                EditorTool.Rectangle => new RectangleAnnotation { Filled = CurrentFilled },
                EditorTool.Ellipse => new EllipseAnnotation { Filled = CurrentFilled },
                EditorTool.Pixelate => new PixelateAnnotation { BlockSize = Math.Max(6, CurrentThickness * 3) },
                EditorTool.Blur => new BlurAnnotation { Strength = Math.Max(4, CurrentThickness * 2.5) },
                EditorTool.Spotlight => new SpotlightAnnotation(),
                EditorTool.Magnify => new MagnifyAnnotation(),
                EditorTool.Highlighter => new FreehandAnnotation { Highlighter = true },
                _ => new FreehandAnnotation(),
            };
            a.ColorHex = tool == EditorTool.Highlighter && CurrentColor == Color.FromRgb(0xE5, 0x39, 0x35)
                ? "#FFFFEB3B" // highlighter defaults to yellow when the default red is selected
                : color;
            a.Thickness = CurrentThickness;
            if (a is LineAnnotation || a is ArrowAnnotation || a is RectangleAnnotation || a is EllipseAnnotation) a.Dashed = CurrentDashed;
            if (!(a is SpotlightAnnotation)) a.Opacity = CurrentOpacity;

            if (a is TwoPointAnnotation tp) tp.SetStart(p);
            else if (a is FreehandAnnotation fh) fh.AddPoint(p);
            return a;
        }

        private static bool IsMeaningful(Annotation a) => a switch
        {
            TwoPointAnnotation tp when tp is LineAnnotation || tp is ArrowAnnotation => tp.Length >= 4,
            TwoPointAnnotation tp => tp.Area.Width >= 3 && tp.Area.Height >= 3,
            FreehandAnnotation fh => fh.Points.Count >= 1,
            _ => true,
        };

        /// <summary>Shift: lines snap to 45°, boxes become squares/circles.</summary>
        private static Point Constrain(TwoPointAnnotation tp, Point p)
        {
            Vector v = p - tp.P1;
            if (tp is LineAnnotation || tp is ArrowAnnotation)
            {
                double angle = Math.Atan2(v.Y, v.X);
                double snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
                double len = v.Length;
                return tp.P1 + new Vector(Math.Cos(snapped) * len, Math.Sin(snapped) * len);
            }
            double size = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            return tp.P1 + new Vector(Math.Sign(v.X == 0 ? 1 : v.X) * size, Math.Sign(v.Y == 0 ? 1 : v.Y) * size);
        }

        // ---- rendering -----------------------------------------------------------

        protected override void OnRender(DrawingContext dc)
        {
            if (Document == null) return;
            var img = Document.Image;
            var full = new Rect(0, 0, img.PixelWidth, img.PixelHeight);
            _ctx.BaseImage = img;

            dc.DrawImage(img, full);
            foreach (var a in Document.Annotations)
            {
                if (a.Visible && !ReferenceEquals(a, EditingText)) a.Draw(dc, _ctx);
            }
            _drawing?.Draw(dc, _ctx);

            double z = Math.Max(0.05, Zoom);
            var selPen = CreateDashedPen(Color.FromRgb(0x3B, 0x82, 0xF6), 1.5 / z);
            foreach (var a in _selection)
            {
                if (ReferenceEquals(a, EditingText)) continue;
                var b = a.GetBounds();
                if (b.IsEmpty) continue;
                b.Inflate(4 / z, 4 / z);
                dc.DrawRectangle(null, selPen, b);
            }

            if (_tool == EditorTool.Select && _selection.Count == 1 && !_selection[0].Locked && EditingText == null)
            {
                double hs = HandleSize;
                var border = new Pen(AccentBrush, 1.5 / z);
                var pts = _selection[0].GetHandles();
                for (int i = 0; i < pts.Length; i++)
                {
                    var hp = pts[i];
                    // Arrow bend handle and callout pointer are round, corners are square.
                    bool round = (_selection[0] is ArrowAnnotation && i == 2) || _selection[0] is TextAnnotation;
                    if (round) dc.DrawEllipse(AccentBrush, new Pen(Brushes.White, 1.5 / z), hp, hs / 2, hs / 2);
                    else dc.DrawRectangle(Brushes.White, border, new Rect(hp.X - hs / 2, hp.Y - hs / 2, hs, hs));
                }
            }

            if (_marquee is Rect m)
            {
                dc.DrawRectangle(MarqueeFill, new Pen(AccentBrush, 1 / z), m);
            }

            if (_cropRect is Rect cr)
            {
                var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(cr));
                dc.DrawGeometry(CropDim, null, dim);
                dc.DrawRectangle(null, CropPen, cr);
            }
        }

        private static Pen CreateDashedPen(Color c, double thickness)
        {
            var pen = new Pen(Freeze(new SolidColorBrush(c)), thickness) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            pen.Freeze();
            return pen;
        }

        private static T Freeze<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }
    }
}
