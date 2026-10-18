using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShotMark.Core;
using ShotMark.Native;
using ShotMark.UI;
using Drawing = System.Drawing;

namespace ShotMark.Capture
{
    /// <summary>What to do with the selected area (chosen in the action bar).</summary>
    public enum CaptureAction
    {
        Edit,
        Copy,
        Save,
        Pin,
        RecordMp4,
        RecordGif,
        Scroll,
    }

    /// <summary>
    /// Full-screen window showing a frozen screenshot. The user drags a region, or clicks to take
    /// the element / window / monitor under the cursor (mouse wheel walks between them).
    /// Result is in screenshot pixel coordinates.
    /// </summary>
    public sealed class CaptureOverlay : Window
    {
        private readonly Drawing.Rectangle _virtual;

        public CaptureOverlay(BitmapSource screenshot, Drawing.Rectangle virtualScreen, List<SnapRegion> snapRegions, bool actionBar = false, string? prompt = null)
        {
            _virtual = virtualScreen;
            Title = "ShotMark";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0; Width = 10; Height = 10;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;

            var surface = new OverlaySurface(screenshot, virtualScreen, snapRegions, Finish, Cancel, actionBar, prompt);
            Content = surface;

            SourceInitialized += (_, _) =>
            {
                // Cover every monitor, in physical pixels.
                var hwnd = new WindowInteropHelper(this).Handle;
                // Mixed-DPI setups: Windows would resize the window when it spans monitors with
                // different scaling. Swallow WM_DPICHANGED so it keeps covering everything 1:1.
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == 0x02E0) handled = true; // WM_DPICHANGED
                    return IntPtr.Zero;
                });
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                    _virtual.X, _virtual.Y, _virtual.Width, _virtual.Height, NativeMethods.SWP_SHOWWINDOW);
            };
            Loaded += (_, _) =>
            {
                // Started from a key press in another app: take the foreground so Esc/Enter reach us.
                NativeMethods.ForceForeground(new WindowInteropHelper(this).Handle);
                Activate();
                surface.Focus();
                Keyboard.Focus(surface);
            };
        }

        public Int32Rect? Result { get; private set; }

        public CaptureAction Action { get; private set; } = CaptureAction.Edit;

        private void Finish(Int32Rect rect, CaptureAction action)
        {
            Result = rect;
            Action = action;
            Close();
        }

        private void Cancel()
        {
            Result = null;
            Close();
        }
    }

    internal sealed class OverlaySurface : FrameworkElement
    {
        private const double MagnifierSize = 132;
        private const int MagnifierPixels = 15;      // odd, so the cursor pixel is centred
        private const double DragThreshold = 4;

        private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));
        private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xEE, 0x1F, 0x21, 0x25)));
        private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
        private static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6)));
        private static readonly Brush MutedText = Frozen(new SolidColorBrush(Color.FromRgb(0xA8, 0xAC, 0xB4)));
        private static readonly Pen SelectionPen = FrozenPen(new Pen(AccentBrush, 2));
        private static readonly Pen CenterPen = FrozenPen(new Pen(AccentBrush, 1.5));
        private static readonly Pen CrosshairPen = FrozenPen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF))), 1)
        {
            DashStyle = new DashStyle(new double[] { 4, 4 }, 0),
        });
        private static readonly Pen WhitePen = FrozenPen(new Pen(Brushes.White, 2));

        private readonly BitmapSource _shot;
        private readonly Drawing.Rectangle _virtual;
        private readonly List<SnapRegion> _regions;
        private readonly Action<Int32Rect, CaptureAction> _finish;
        private readonly bool _actionBar;
        private readonly string? _prompt;

        // Adjust mode (after a selection, when the action bar is enabled).
        private bool _adjusting;
        private Int32Rect _sel;
        private Int32Rect _adjOrig;
        private int _adjHandle = -1;
        private bool _adjMoving;
        private int _adjStartX, _adjStartY;
        private int _hoverButton = -1;
        private readonly List<(Rect Rect, CaptureAction? Action, string Label, Geometry Icon)> _buttons = new();
        private static readonly Brush ButtonHover = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x35, 0x38, 0x40)));
        private static readonly Brush HandleFill = Brushes.White;
        private readonly Action _cancel;
        private readonly DispatcherTimer _elementTimer;

        // Snap candidates under the cursor, smallest first (elements…, window, monitor).
        private List<SnapRegion> _levels = new List<SnapRegion>();
        private int _level;
        private IntPtr _levelsHwnd;
        private SnapRegion? _hoverWindow;
        private int _requestId;

        private Int32Rect _drag;
        private bool _isDown;
        private bool _dragging;
        private int _downX, _downY, _curX, _curY;
        private string? _flash;
        private DateTime _flashUntil;

        public OverlaySurface(BitmapSource shot, Drawing.Rectangle virtualScreen, List<SnapRegion> regions,
            Action<Int32Rect, CaptureAction> finish, Action cancel, bool actionBar, string? prompt)
        {
            _shot = shot;
            _virtual = virtualScreen;
            _regions = regions;
            _finish = finish;
            _actionBar = actionBar;
            _prompt = prompt;
            _cancel = cancel;
            Focusable = true;
            FocusVisualStyle = null;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

            _elementTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };
            _elementTimer.Tick += (_, _) => RequestElements();

            UpdateCursor();
            UpdateHover();
        }

        private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
        private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

        // ---- coordinate helpers -------------------------------------------------

        /// <summary>DIPs per screenshot pixel (the window covers the whole virtual screen).</summary>
        private double ScaleX => ActualWidth > 0 ? ActualWidth / _shot.PixelWidth : 1;
        private double ScaleY => ActualHeight > 0 ? ActualHeight / _shot.PixelHeight : 1;

        private Rect ToDip(Int32Rect r) => new Rect(r.X * ScaleX, r.Y * ScaleY, r.Width * ScaleX, r.Height * ScaleY);

        private static bool Contains(Int32Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

        private void UpdateCursor()
        {
            // Physical cursor position: exact even on mixed-DPI setups.
            NativeMethods.GetCursorPos(out var p);
            _curX = Math.Max(0, Math.Min(_shot.PixelWidth - 1, p.X - _virtual.X));
            _curY = Math.Max(0, Math.Min(_shot.PixelHeight - 1, p.Y - _virtual.Y));
        }

        private SnapRegion? FindRegion(int x, int y)
        {
            foreach (var r in _regions)
            {
                if (Contains(r.Rect, x, y)) return r;
            }
            return null;
        }

        private List<SnapRegion> BaseLevels(SnapRegion? window)
        {
            var list = new List<SnapRegion>();
            if (window is SnapRegion w && w.Hwnd != IntPtr.Zero) list.Add(w);
            var monitor = ScreenCapturer.MonitorAt(_virtual, _curX, _curY);
            if (list.Count == 0 || !list[^1].Rect.Equals(monitor)) list.Add(new SnapRegion(monitor, IntPtr.Zero, "Tela"));
            return list;
        }

        private SnapRegion? Current => _levels.Count > 0 ? _levels[Math.Min(_level, _levels.Count - 1)] : (SnapRegion?)null;

        /// <summary>Recomputes what is under the cursor; asks UI Automation for finer elements after a short pause.</summary>
        private void UpdateHover()
        {
            var window = FindRegion(_curX, _curY);
            IntPtr hwnd = window?.Hwnd ?? IntPtr.Zero;

            // Keep showing the current element while the cursor stays inside it (avoids flicker).
            bool keep = Current is SnapRegion cur && hwnd == _levelsHwnd && Contains(cur.Rect, _curX, _curY);
            if (!keep)
            {
                _levels = BaseLevels(window);
                _level = 0;
                _levelsHwnd = hwnd;
            }

            _hoverWindow = window;
            _requestId++;
            _elementTimer.Stop();
            if (hwnd != IntPtr.Zero && AppSettings.Current.SmartDetection) _elementTimer.Start();
        }

        private void RequestElements()
        {
            _elementTimer.Stop();
            if (_isDown || _hoverWindow is not SnapRegion window || window.Hwnd == IntPtr.Zero) return;

            int id = ++_requestId;
            int sx = _curX + _virtual.X, sy = _curY + _virtual.Y;
            var virt = _virtual;
            Task.Run(() => ElementPicker.Chain(window.Hwnd, sx, sy, virt, window.Rect))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted) return;
                    Dispatcher.BeginInvoke(new Action(() => ApplyElements(id, window, t.Result)));
                }, TaskScheduler.Default);
        }

        private void ApplyElements(int id, SnapRegion window, List<SnapRegion> chain)
        {
            if (id != _requestId || _isDown) return;
            var elements = chain.Where(e => Contains(e.Rect, _curX, _curY)).ToList();
            var levels = new List<SnapRegion>(elements);
            levels.AddRange(BaseLevels(window));
            _levels = levels;
            _level = 0;
            _levelsHwnd = window.Hwnd;
            InvalidateVisual();
        }

        private static Int32Rect RectFrom(int x1, int y1, int x2, int y2)
        {
            int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
            return new Int32Rect(x, y, Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);
        }

        // ---- input ---------------------------------------------------------------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            UpdateCursor();
            if (_adjusting && AdjustMouseDown(e)) return;
            _isDown = true;
            _dragging = false;
            _downX = _curX;
            _downY = _curY;
            _elementTimer.Stop();
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            UpdateCursor();
            if (_adjusting)
            {
                AdjustMouseMove(e);
                return;
            }
            if (_isDown)
            {
                if (!_dragging && (Math.Abs(_curX - _downX) > DragThreshold || Math.Abs(_curY - _downY) > DragThreshold))
                    _dragging = true;
                if (_dragging) _drag = RectFrom(_downX, _downY, _curX, _curY);
            }
            else
            {
                UpdateHover();
            }
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (_adjusting || _isDown || _levels.Count < 2) return;
            _requestId++;          // a pending lookup must not undo the user's choice
            _elementTimer.Stop();
            _level = e.Delta > 0
                ? Math.Min(_level + 1, _levels.Count - 1)   // wheel up: bigger
                : Math.Max(_level - 1, 0);                   // wheel down: smaller
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_adjusting)
            {
                if (_adjHandle >= 0 || _adjMoving) ReleaseMouseCapture();
                _adjHandle = -1;
                _adjMoving = false;
                e.Handled = true;
                return;
            }
            if (!_isDown) return;
            _isDown = false;
            ReleaseMouseCapture();
            UpdateCursor();

            if (_dragging)
            {
                Complete(_drag);
            }
            else
            {
                var r = Current?.Rect ?? ScreenCapturer.MonitorAt(_virtual, _curX, _curY);
                Complete(r);
            }
            e.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            if (_isDown)
            {
                // Right click while dragging only cancels the drag.
                _isDown = false;
                _dragging = false;
                ReleaseMouseCapture();
                InvalidateVisual();
            }
            else
            {
                _cancel();
            }
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _cancel();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    UpdateCursor();
                    if (_adjusting) _finish(_sel, CaptureAction.Edit);
                    else Complete(ScreenCapturer.MonitorAt(_virtual, _curX, _curY));
                    e.Handled = true;
                    break;
                case Key.C when _adjusting && Keyboard.Modifiers == ModifierKeys.Control:
                    _finish(_sel, CaptureAction.Copy);
                    e.Handled = true;
                    break;
                case Key.S when _adjusting && Keyboard.Modifiers == ModifierKeys.Control:
                    _finish(_sel, CaptureAction.Save);
                    e.Handled = true;
                    break;
                case Key.C when Keyboard.Modifiers == ModifierKeys.None:
                    UpdateCursor();
                    string hex = PixelHex(_curX, _curY);
                    try { Clipboard.SetText(hex); } catch (Exception) { /* clipboard busy */ }
                    _flash = $"Cor {hex} copiada";
                    _flashUntil = DateTime.Now.AddSeconds(1.6);
                    InvalidateVisual();
                    e.Handled = true;
                    break;
                case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                    Complete(new Int32Rect(0, 0, _shot.PixelWidth, _shot.PixelHeight));
                    e.Handled = true;
                    break;
                case Key.Up:
                case Key.PageUp:
                    if (_levels.Count > 1) { _requestId++; _level = Math.Min(_level + 1, _levels.Count - 1); InvalidateVisual(); }
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.PageDown:
                    if (_levels.Count > 1) { _requestId++; _level = Math.Max(_level - 1, 0); InvalidateVisual(); }
                    e.Handled = true;
                    break;
            }
        }

        // ---- adjust mode + action bar ------------------------------------------------

        /// <summary>Selection done: either finish right away or let the user adjust and pick an action.</summary>
        private void Complete(Int32Rect r)
        {
            if (!_actionBar)
            {
                _finish(r, CaptureAction.Edit);
                return;
            }
            _adjusting = true;
            _sel = r;
            _elementTimer.Stop();
            _requestId++;
            Cursor = Cursors.Arrow;
            InvalidateVisual();
        }

        private Point[] HandlePoints()
        {
            int l = _sel.X, t = _sel.Y, r = _sel.X + _sel.Width, b = _sel.Y + _sel.Height;
            int cx = (l + r) / 2, cy = (t + b) / 2;
            return new[]
            {
                new Point(l, t), new Point(cx, t), new Point(r, t), new Point(r, cy),
                new Point(r, b), new Point(cx, b), new Point(l, b), new Point(l, cy),
            };
        }

        private int AdjustHandleAt(int x, int y)
        {
            double tol = 8 / Math.Max(0.1, ScaleX);
            var pts = HandlePoints();
            for (int i = 0; i < pts.Length; i++)
            {
                if (Math.Abs(pts[i].X - x) <= tol && Math.Abs(pts[i].Y - y) <= tol) return i;
            }
            return -1;
        }

        private int ButtonAt(Point dip)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                if (_buttons[i].Rect.Contains(dip)) return i;
            }
            return -1;
        }

        /// <returns>true when the click was consumed by adjust mode.</returns>
        private bool AdjustMouseDown(MouseButtonEventArgs e)
        {
            int b = ButtonAt(e.GetPosition(this));
            if (b >= 0)
            {
                var action = _buttons[b].Action;
                if (action.HasValue) _finish(_sel, action.Value); else _cancel();
                e.Handled = true;
                return true;
            }

            bool inside = Contains(_sel, _curX, _curY);
            if (e.ClickCount == 2 && inside)
            {
                _finish(_sel, CaptureAction.Edit);
                e.Handled = true;
                return true;
            }

            int h = AdjustHandleAt(_curX, _curY);
            if (h >= 0 || inside)
            {
                _adjHandle = h;
                _adjMoving = h < 0;
                _adjOrig = _sel;
                _adjStartX = _curX;
                _adjStartY = _curY;
                CaptureMouse();
                e.Handled = true;
                return true;
            }

            // Click outside: start a brand-new selection.
            _adjusting = false;
            Cursor = Cursors.Cross;
            return false;
        }

        private void AdjustMouseMove(MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && (_adjHandle >= 0 || _adjMoving))
            {
                int dx = _curX - _adjStartX, dy = _curY - _adjStartY;
                int l = _adjOrig.X, t = _adjOrig.Y, r = _adjOrig.X + _adjOrig.Width, bt = _adjOrig.Y + _adjOrig.Height;
                if (_adjMoving)
                {
                    dx = Math.Max(-l, Math.Min(_shot.PixelWidth - r, dx));
                    dy = Math.Max(-t, Math.Min(_shot.PixelHeight - bt, dy));
                    l += dx; r += dx; t += dy; bt += dy;
                }
                else
                {
                    switch (_adjHandle)
                    {
                        case 0: l = _curX; t = _curY; break;
                        case 1: t = _curY; break;
                        case 2: r = _curX; t = _curY; break;
                        case 3: r = _curX; break;
                        case 4: r = _curX; bt = _curY; break;
                        case 5: bt = _curY; break;
                        case 6: l = _curX; bt = _curY; break;
                        case 7: l = _curX; break;
                    }
                }
                int x1 = Math.Min(l, r), x2 = Math.Max(l, r), y1 = Math.Min(t, bt), y2 = Math.Max(t, bt);
                _sel = new Int32Rect(x1, y1, Math.Max(2, x2 - x1), Math.Max(2, y2 - y1));
                InvalidateVisual();
                return;
            }

            int hover = ButtonAt(e.GetPosition(this));
            if (hover != _hoverButton)
            {
                _hoverButton = hover;
                InvalidateVisual();
            }
            int h = AdjustHandleAt(_curX, _curY);
            Cursor = hover >= 0 ? Cursors.Hand
                : h is 0 or 4 ? Cursors.SizeNWSE
                : h is 2 or 6 ? Cursors.SizeNESW
                : h is 1 or 5 ? Cursors.SizeNS
                : h is 3 or 7 ? Cursors.SizeWE
                : Contains(_sel, _curX, _curY) ? Cursors.SizeAll : Cursors.Cross;
        }

        private void RenderAdjust(DrawingContext dc, Rect full)
        {
            var r = ToDip(_sel);
            var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(r));
            dc.DrawGeometry(DimBrush, null, dim);
            dc.DrawRectangle(null, SelectionPen, r);

            foreach (var hp in HandlePoints())
            {
                var c = new Point(hp.X * ScaleX, hp.Y * ScaleY);
                dc.DrawRectangle(HandleFill, SelectionPen, new Rect(c.X - 4.5, c.Y - 4.5, 9, 9));
            }
            DrawLabel(dc, $"{_sel.Width} × {_sel.Height}", new Point(r.X, r.Y - 32 < 0 ? r.Y + 6 : r.Y - 32));

            // ---- action bar ----
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var specs = new List<(CaptureAction? Action, string Label, Geometry Icon, bool GapBefore)>
            {
                (CaptureAction.Edit, "Editar", Icons.Pen, false),
                (CaptureAction.Copy, "Copiar", Icons.Copy, false),
                (CaptureAction.Save, "Salvar", Icons.Save, false),
                (CaptureAction.Pin, "Fixar", Icons.Pin, false),
                (CaptureAction.RecordMp4, "Vídeo", Icons.Video, true),
                (CaptureAction.RecordGif, "GIF", Icons.Video, false),
                (CaptureAction.Scroll, "Rolagem", Icons.Scroll, false),
                (null, "", Icons.Unlock, true),
            };
            const double h = 34, pad = 6, gap = 2, sep = 12;
            var texts = specs.Select(sp => sp.Label.Length == 0 ? null : MakeText(sp.Label, 12.5, Brushes.White, FontWeights.Normal, ppd)).ToList();
            double width = pad * 2;
            for (int i = 0; i < specs.Count; i++)
            {
                width += 12 + 16 + (texts[i] != null ? 7 + texts[i]!.Width : 0) + 12 + gap + (specs[i].GapBefore ? sep : 0);
            }

            double x = Math.Max(full.X + 8, Math.Min(r.X, full.Right - width - 8));
            double y = r.Bottom + 10;
            if (y + h + pad * 2 > full.Bottom - 6) y = r.Y - h - pad * 2 - 10;
            if (y < 6) y = r.Bottom - h - pad * 2 - 10;
            var bar = new Rect(x, y, width, h + pad * 2);
            dc.DrawRoundedRectangle(LabelBrush, null, bar, 10, 10);

            _buttons.Clear();
            double bx = bar.X + pad;
            var iconPen = new Pen(Brushes.White, 1.7 * 24 / 16) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            iconPen.Freeze();
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].GapBefore)
                {
                    dc.DrawRectangle(Brushes.Gray, null, new Rect(bx + sep / 2 - 0.5, bar.Y + pad + 7, 1, h - 14));
                    bx += sep;
                }
                double bw = 12 + 16 + (texts[i] != null ? 7 + texts[i]!.Width : 0) + 12;
                var br = new Rect(bx, bar.Y + pad, bw, h);
                bool primary = specs[i].Action == CaptureAction.Edit;
                if (primary) dc.DrawRoundedRectangle(AccentBrush, null, br, 7, 7);
                else if (i == _hoverButton) dc.DrawRoundedRectangle(ButtonHover, null, br, 7, 7);

                var icon = specs[i].Action == null ? CloseIcon : specs[i].Icon;
                dc.PushTransform(new TranslateTransform(br.X + 12, br.Y + (h - 16) / 2));
                dc.PushTransform(new ScaleTransform(16 / 24.0, 16 / 24.0));
                dc.DrawGeometry(null, iconPen, icon);
                dc.Pop();
                dc.Pop();
                if (texts[i] != null) dc.DrawText(texts[i]!, new Point(br.X + 12 + 16 + 7, br.Y + (h - texts[i]!.Height) / 2));

                _buttons.Add((br, specs[i].Action, specs[i].Label, icon));
                bx += bw + gap;
            }

            const string tip = "Arraste as alças para ajustar  ·  Enter: editar  ·  Ctrl+C: copiar  ·  Esc: cancelar  ·  Para ir direto ao editor: Configurações › Geral";
            var tipText = MakeText(tip, 11.5, MutedText, FontWeights.Normal, ppd);
            double ty = bar.Y > r.Bottom ? bar.Bottom + 6 : bar.Y - tipText.Height - 6;
            if (ty > 0 && ty + tipText.Height < full.Bottom)
                dc.DrawText(tipText, new Point(bar.X + 8, ty));
        }

        private static readonly Geometry CloseIcon = Freeze(Geometry.Parse("M6,6 L18,18 M18,6 L6,18"));

        private static Geometry Freeze(Geometry g)
        {
            g.Freeze();
            return g;
        }

        // ---- rendering -----------------------------------------------------------

        protected override void OnRender(DrawingContext dc)
        {
            var full = new Rect(0, 0, ActualWidth, ActualHeight);
            if (full.Width <= 0 || full.Height <= 0) return;

            dc.DrawImage(_shot, full);

            if (_adjusting)
            {
                RenderAdjust(dc, full);
                return;
            }

            Int32Rect? sel = _dragging ? _drag : Current?.Rect;
            if (sel is Int32Rect s)
            {
                var r = ToDip(s);
                var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(r));
                dc.DrawGeometry(DimBrush, null, dim);
                if (!_dragging) dc.DrawRectangle(HoverFill, null, r);
                dc.DrawRectangle(null, SelectionPen, r);

                string label = _dragging || Current == null
                    ? $"{s.Width} × {s.Height}"
                    : $"{Current.Value.Kind}  ·  {s.Width} × {s.Height}" + (_levels.Count > 1 ? $"   ({_level + 1}/{_levels.Count})" : "");
                DrawLabel(dc, label, new Point(r.X, r.Y - 32 < 0 ? r.Y + 6 : r.Y - 32));
            }
            else
            {
                dc.DrawRectangle(DimBrush, null, full);
            }

            var cursor = new Point((_curX + 0.5) * ScaleX, (_curY + 0.5) * ScaleY);
            if (!_dragging)
            {
                dc.DrawLine(CrosshairPen, new Point(0, cursor.Y), new Point(full.Width, cursor.Y));
                dc.DrawLine(CrosshairPen, new Point(cursor.X, 0), new Point(cursor.X, full.Height));
            }

            if (AppSettings.Current.ShowMagnifier)
            {
                DrawMagnifier(dc, cursor, full);
            }
            else
            {
                DrawLabel(dc, InfoText(), new Point(cursor.X + 18, cursor.Y + 18));
            }
            DrawHelp(dc);
        }

        private void DrawMagnifier(DrawingContext dc, Point cursor, Rect full)
        {
            double x = cursor.X + 24, y = cursor.Y + 24;
            if (x + MagnifierSize > full.Right) x = cursor.X - 24 - MagnifierSize;
            if (y + MagnifierSize + 30 > full.Bottom) y = cursor.Y - 24 - MagnifierSize - 30;
            var box = new Rect(x, y, MagnifierSize, MagnifierSize);

            int half = MagnifierPixels / 2;
            int sx = _curX - half, sy = _curY - half;
            int cx = Math.Max(0, sx), cy = Math.Max(0, sy);
            int cw = Math.Min(_shot.PixelWidth, sx + MagnifierPixels) - cx;
            int ch = Math.Min(_shot.PixelHeight, sy + MagnifierPixels) - cy;
            double cell = MagnifierSize / MagnifierPixels;

            var clip = new RectangleGeometry(box, 10, 10);
            dc.PushClip(clip);
            dc.DrawRectangle(Brushes.Black, null, box);
            if (cw > 0 && ch > 0)
            {
                var crop = new CroppedBitmap(_shot, new Int32Rect(cx, cy, cw, ch));
                var dest = new Rect(box.X + (cx - sx) * cell, box.Y + (cy - sy) * cell, cw * cell, ch * cell);
                var group = new DrawingGroup();
                RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
                using (var gc = group.Open()) gc.DrawImage(crop, dest);
                dc.DrawDrawing(group);
            }
            dc.DrawRectangle(null, CenterPen, new Rect(box.X + half * cell, box.Y + half * cell, cell, cell));
            dc.Pop();
            dc.DrawRoundedRectangle(null, WhitePen, box, 10, 10);

            string info = InfoText();
            var labelAt = new Point(box.X, box.Bottom + 6);
            DrawLabel(dc, info, labelAt);

            // Colour swatch of the pixel under the cursor.
            if (!_dragging && _flash == null)
            {
                var swatch = new Rect(box.Right - 18, box.Bottom + 10, 14, 14);
                dc.DrawRoundedRectangle(PixelBrush(_curX, _curY), WhitePen, swatch, 3, 3);
            }
        }

        private string InfoText()
        {
            if (_flash != null && DateTime.Now < _flashUntil) return _flash;
            _flash = null;
            return _dragging
                ? $"{_drag.Width} × {_drag.Height}"
                : $"X {_curX + _virtual.X}  Y {_curY + _virtual.Y}  ·  {PixelHex(_curX, _curY)}";
        }

        private byte[] Pixel(int x, int y)
        {
            var px = new byte[4];
            try
            {
                new CroppedBitmap(_shot, new Int32Rect(x, y, 1, 1)).CopyPixels(px, 4, 0);
            }
            catch (Exception)
            {
            }
            return px; // B, G, R, A
        }

        private string PixelHex(int x, int y)
        {
            var p = Pixel(x, y);
            return $"#{p[2]:X2}{p[1]:X2}{p[0]:X2}";
        }

        private Brush PixelBrush(int x, int y)
        {
            var p = Pixel(x, y);
            var b = new SolidColorBrush(Color.FromRgb(p[2], p[1], p[0]));
            b.Freeze();
            return b;
        }

        private void DrawHelp(DrawingContext dc)
        {
            if (_prompt != null)
            {
                DrawPrompt(dc, _prompt);
                return;
            }
            var parts = new[]
            {
                ("Arraste", "seleciona região"),
                ("Clique", "captura o destaque"),
                ("Roda do mouse", "elemento maior/menor"),
                ("Enter", "monitor"),
                ("C", "copia a cor"),
                ("Esc", "cancelar"),
            };

            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var texts = parts.Select(p => (Key: MakeText(p.Item1, 12.5, Brushes.White, FontWeights.SemiBold, ppd),
                                           Desc: MakeText(p.Item2, 12.5, MutedText, FontWeights.Normal, ppd))).ToList();
            const double gap = 22, inner = 6, padX = 16, padY = 9;
            double width = texts.Sum(t => t.Key.Width + inner + t.Desc.Width) + gap * (texts.Count - 1) + padX * 2;
            double height = texts.Max(t => t.Key.Height) + padY * 2;

            var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Drawing.Rectangle(_virtual.X, _virtual.Y, _virtual.Width, _virtual.Height);
            double px = (primary.X - _virtual.X) * ScaleX, pw = primary.Width * ScaleX;
            double py = (primary.Y - _virtual.Y) * ScaleY;
            var rect = new Rect(px + (pw - width) / 2, py + 18, width, height);
            dc.DrawRoundedRectangle(LabelBrush, null, rect, height / 2, height / 2);

            double x = rect.X + padX;
            foreach (var t in texts)
            {
                dc.DrawText(t.Key, new Point(x, rect.Y + padY));
                x += t.Key.Width + inner;
                dc.DrawText(t.Desc, new Point(x, rect.Y + padY));
                x += t.Desc.Width + gap;
            }
        }

        private void DrawPrompt(DrawingContext dc, string text)
        {
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ft = MakeText(text, 13.5, Brushes.White, FontWeights.SemiBold, ppd);
            var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Drawing.Rectangle(_virtual.X, _virtual.Y, _virtual.Width, _virtual.Height);
            double px = (primary.X - _virtual.X) * ScaleX, pw = primary.Width * ScaleX, py = (primary.Y - _virtual.Y) * ScaleY;
            var rect = new Rect(px + (pw - ft.Width - 36) / 2, py + 18, ft.Width + 36, ft.Height + 18);
            dc.DrawRoundedRectangle(AccentBrush, null, rect, rect.Height / 2, rect.Height / 2);
            dc.DrawText(ft, new Point(rect.X + 18, rect.Y + 9));
        }

        private void DrawLabel(DrawingContext dc, string text, Point at)
        {
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ft = MakeText(text, 12, Brushes.White, FontWeights.SemiBold, ppd);
            var rect = new Rect(at.X, at.Y, ft.Width + 18, ft.Height + 10);
            dc.DrawRoundedRectangle(LabelBrush, null, rect, 6, 6);
            dc.DrawText(ft, new Point(rect.X + 9, rect.Y + 5));
        }

        private static FormattedText MakeText(string text, double size, Brush brush, FontWeight weight, double ppd)
        {
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, ppd);
        }
    }
}
