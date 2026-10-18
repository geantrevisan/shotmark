using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ShotMark.Native;
using Drawing = System.Drawing;

namespace ShotMark.UI
{
    /// <summary>Click-through red frame around the area being recorded (never appears in the recording).</summary>
    public sealed class RecordingFrame : Window
    {
        private const int Pad = 4;
        private readonly Drawing.Rectangle _area;

        public RecordingFrame(Drawing.Rectangle area)
        {
            _area = area;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            IsHitTestVisible = false;
            Content = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xF0, 0x52, 0x52)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Margin = new Thickness(1),
            };
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.MakeClickThrough(this);
                NativeMethods.ExcludeFromCapture(this);
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                    _area.X - Pad, _area.Y - Pad, _area.Width + Pad * 2, _area.Height + Pad * 2,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            };
        }
    }

    /// <summary>
    /// Small floating control bar (recording timer, pause/stop, scrolling progress).
    /// Hidden from captures and draggable.
    /// </summary>
    public sealed class FloatingBar : Window
    {
        private readonly StackPanel _buttons;
        private readonly TextBlock _text;
        private readonly Ellipse _dot;
        private readonly Drawing.Rectangle _anchor;

        public FloatingBar(Drawing.Rectangle anchor, string text, bool showDot)
        {
            _anchor = anchor;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;

            _dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(0xF0, 0x52, 0x52)),
                Margin = new Thickness(4, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = showDot ? Visibility.Visible : Visibility.Collapsed,
            };
            _text = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 70,
                FontWeight = FontWeights.SemiBold,
            };
            _buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_dot);
            row.Children.Add(_text);
            row.Children.Add(_buttons);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1F, 0x21, 0x25)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 7, 8, 7),
                Child = row,
                Cursor = Cursors.SizeAll,
            };

            SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(this);
            Loaded += (_, _) => PlaceNearAnchor();
            MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is not Button) try { DragMove(); } catch (InvalidOperationException) { }
            };
        }

        public string Text
        {
            get => _text.Text;
            set => _text.Text = value;
        }

        public void SetDotVisible(bool on) => _dot.Opacity = on ? 1 : 0.25;

        public Button AddButton(string label, Action onClick, bool danger = false)
        {
            var b = new Button
            {
                Content = label,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = 12.5,
            };
            if (Application.Current?.TryFindResource(danger ? (object)"AccentButton" : typeof(Button)) is Style st) b.Style = st;
            if (danger) b.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            b.Click += (_, _) => onClick();
            _buttons.Children.Add(b);
            return b;
        }

        /// <summary>Below the area if there is room, otherwise above, otherwise inside the top edge.</summary>
        private void PlaceNearAnchor()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
            var screen = System.Windows.Forms.Screen.FromRectangle(_anchor).WorkingArea;
            int x = _anchor.X + (_anchor.Width - w) / 2;
            int y = _anchor.Bottom + 12;
            if (y + h > screen.Bottom) y = _anchor.Top - h - 12;
            if (y < screen.Top) y = _anchor.Top + 12;
            x = Math.Max(screen.Left + 4, Math.Min(screen.Right - w - 4, x));
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_SHOWWINDOW);
        }
    }
}
