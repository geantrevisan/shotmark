using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShotMark.Services;

namespace ShotMark.UI
{
    /// <summary>"Novidades": the release notes embedded in the app (CHANGELOG.md), newest version first.</summary>
    public sealed class WhatsNewWindow : Window
    {
        public WhatsNewWindow(bool justUpdated)
        {
            string version = UpdateService.Format(UpdateService.CurrentVersion);
            Title = "Novidades do ShotMark";
            Width = 640;
            Height = 640;
            MinWidth = 420;
            MinHeight = 360;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = Res("WindowBg");
            Foreground = Res("Fg");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            WindowFit.SizeToMonitor(this, 0.5, 0.78, 640, 680);
            SourceInitialized += (_, _) => Native.NativeMethods.UseDarkTitleBar(this);

            var title = new TextBlock
            {
                Text = justUpdated ? $"O ShotMark foi atualizado para a versão {version}" : "Novidades",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            var subtitle = new TextBlock
            {
                Text = justUpdated ? "Veja o que mudou." : $"Você está usando a versão {version}.",
                Foreground = Res("FgMuted"),
                Margin = new Thickness(0, 4, 0, 14),
            };

            string body = ReleaseNotes.Body;
            var notes = new Border
            {
                Background = Res("PanelBg"),
                BorderBrush = Res("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 10, 6),
                Child = MarkdownView.Create(body.Length > 0 ? body : "As notas desta versão não estão disponíveis."),
            };

            var all = new Button { Content = "Todas as versões no GitHub", Padding = new Thickness(12, 7, 12, 7) };
            all.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("Abrir releases", ex); }
            };
            var close = new Button { Content = "Fechar", Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(8, 0, 0, 0), IsDefault = true, IsCancel = true };
            if (Application.Current.TryFindResource("AccentButton") is Style accent) close.Style = accent;
            close.Click += (_, _) => Close();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(all);
            buttons.Children.Add(close);

            var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };
            DockPanel.SetDock(title, Dock.Top);
            DockPanel.SetDock(subtitle, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(title);
            root.Children.Add(subtitle);
            root.Children.Add(buttons);
            root.Children.Add(notes);
            Content = root;
        }

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    }
}
