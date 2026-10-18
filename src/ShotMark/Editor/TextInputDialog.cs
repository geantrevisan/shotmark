using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ShotMark.Editor
{
    /// <summary>Small multi-line text prompt (used for text/callout annotations and OCR results).</summary>
    public static class TextInputDialog
    {
        public static string? Show(Window? owner, string title, string initial, bool readOnly = false)
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x1F, 0x21, 0x25));
            var fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));

            var box = new TextBox
            {
                Text = initial,
                AcceptsReturn = true,
                AcceptsTab = false,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 15,
                MinHeight = 120,
                IsReadOnly = readOnly,
                Padding = new Thickness(8),
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x18, 0x1B)),
                Foreground = fg,
                CaretBrush = fg,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)),
                BorderThickness = new Thickness(1),
                SelectionBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            };

            var ok = new Button { Content = readOnly ? "Fechar" : "OK", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
            if (Application.Current?.TryFindResource("AccentButton") is Style accent) ok.Style = accent;
            var cancel = new Button { Content = "Cancelar", MinWidth = 90, IsCancel = true, Padding = new Thickness(12, 6, 12, 6) };

            var hint = new TextBlock
            {
                Text = readOnly ? "O texto já foi copiado para a área de transferência." : "Ctrl+Enter para confirmar",
                Foreground = fg,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
            DockPanel.SetDock(hint, Dock.Left);
            buttons.Children.Add(hint);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(ok);
            if (!readOnly) right.Children.Add(cancel);
            DockPanel.SetDock(right, Dock.Right);
            buttons.Children.Add(right);

            var root = new DockPanel { Margin = new Thickness(14) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(box);

            var win = new Window
            {
                Title = title,
                Content = root,
                Width = 520,
                Height = 300,
                MinWidth = 320,
                MinHeight = 220,
                Background = bg,
                Foreground = fg,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip,
            };
            if (owner != null && owner.IsVisible) win.Owner = owner;
            win.SourceInitialized += (_, _) => ShotMark.Native.NativeMethods.UseDarkTitleBar(win);

            ok.Click += (_, _) => win.DialogResult = true;
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    win.DialogResult = true;
                    e.Handled = true;
                }
            };
            win.Loaded += (_, _) =>
            {
                box.Focus();
                if (!readOnly) box.SelectAll();
            };

            return win.ShowDialog() == true ? box.Text : null;
        }
    }
}
