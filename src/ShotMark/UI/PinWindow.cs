using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ShotMark.UI
{
    /// <summary>
    /// Floats an image above every window ("pin to screen"). Drag to move, wheel to zoom,
    /// double-click or Esc to close, right-click for options.
    /// </summary>
    public sealed class PinWindow : Window
    {
        private readonly BitmapSource _image;
        private readonly Image _view;
        private double _zoom = 1.0;
        private double _dpiScale = 1.0;

        public PinWindow(BitmapSource image, Action<BitmapSource>? openInEditor = null)
        {
            _image = image;
            Title = "ShotMark — fixado na tela";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = Cursors.SizeAll;

            _view = new Image { Source = image, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_view, BitmapScalingMode.HighQuality);
            Content = new Border
            {
                Margin = new Thickness(14),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                Background = Brushes.Black,
                Child = _view,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black },
            };

            var menu = new ContextMenu();
            AddItem(menu, "Copiar imagem", () =>
            {
                ShotMark.Services.ClipboardUtil.SetImage(_image);
            });
            if (openInEditor != null) AddItem(menu, "Abrir no editor", () => openInEditor(_image));
            menu.Items.Add(new Separator());
            AddItem(menu, "Tamanho real (100%)", () => SetZoom(1.0));
            AddItem(menu, "Opacidade 100%", () => Opacity = 1.0);
            AddItem(menu, "Opacidade 75%", () => Opacity = 0.75);
            AddItem(menu, "Opacidade 50%", () => Opacity = 0.5);
            menu.Items.Add(new Separator());
            AddItem(menu, "Fechar", Close, "Esc");
            ContextMenu = menu;

            SourceInitialized += (_, _) =>
            {
                _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                SetZoom(1.0);
                // Open centred on the mouse.
                var p = System.Windows.Forms.Control.MousePosition;
                Left = p.X / _dpiScale - ActualWidthOrEstimate().Width / 2;
                Top = p.Y / _dpiScale - ActualWidthOrEstimate().Height / 2;
            };
        }

        private Size ActualWidthOrEstimate() =>
            new Size(_image.PixelWidth / _dpiScale * _zoom + 30, _image.PixelHeight / _dpiScale * _zoom + 30);

        private static void AddItem(ContextMenu menu, string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        private void SetZoom(double zoom)
        {
            _zoom = Math.Max(0.1, Math.Min(5.0, zoom));
            // 1 image pixel = 1 physical screen pixel at 100%.
            _view.Width = _image.PixelWidth / _dpiScale * _zoom;
            _view.Height = _image.PixelHeight / _dpiScale * _zoom;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ClickCount == 2)
            {
                Close();
                return;
            }
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShotMark.Services.ClipboardUtil.SetImage(_image);
            }
        }
    }

    /// <summary>Big "3… 2… 1…" before a delayed capture.</summary>
    public static class Countdown
    {
        public static async System.Threading.Tasks.Task RunAsync(int seconds)
        {
            if (seconds <= 0) return;
            var number = new TextBlock
            {
                FontSize = 72,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var caption = new TextBlock
            {
                Text = "Capturando em",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCB, 0xD0)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var panel = new StackPanel();
            panel.Children.Add(caption);
            panel.Children.Add(number);

            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                IsHitTestVisible = false,
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x21, 0x25)),
                    CornerRadius = new CornerRadius(22),
                    Padding = new Thickness(44, 16, 44, 18),
                    Child = panel,
                },
            };
            win.Show();
            for (int i = seconds; i > 0; i--)
            {
                number.Text = i.ToString();
                await System.Threading.Tasks.Task.Delay(1000);
            }
            win.Close();
            await System.Threading.Tasks.Task.Delay(220); // let the window disappear before grabbing the screen
        }
    }
}
