using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShotMark.Services;

namespace ShotMark.UI
{
    /// <summary>"Nova versão disponível": release notes, download progress and install.</summary>
    public sealed class UpdateWindow : Window
    {
        private readonly UpdateInfo _info;
        private readonly Func<bool> _canInstall;
        private readonly Action _exitForInstall;
        private readonly TextBlock _status;
        private readonly ProgressBar _progress;
        private readonly Button _install;
        private readonly Button _skip;
        private readonly Button _later;
        private CancellationTokenSource? _cts;

        /// <summary>The user chose "Pular esta versão".</summary>
        public bool Skipped { get; private set; }

        /// <param name="canInstall">Returns false (after telling the user why) when ShotMark cannot close right now, e.g. while recording.</param>
        /// <param name="exitForInstall">Closes ShotMark once the installer has started.</param>
        public UpdateWindow(UpdateInfo info, Func<bool> canInstall, Action exitForInstall)
        {
            _info = info;
            _canInstall = canInstall;
            _exitForInstall = exitForInstall;

            Title = "Atualização do ShotMark";
            Width = 560;
            Height = 460;
            MinWidth = 420;
            MinHeight = 320;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = true;
            Topmost = true;
            Background = (Brush)Application.Current.FindResource("WindowBg");
            Foreground = (Brush)Application.Current.FindResource("Fg");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            WindowFit.SizeToMonitor(this, 0.6, 0.7, 560, 460);
            SourceInitialized += (_, _) => Native.NativeMethods.UseDarkTitleBar(this);

            var title = new TextBlock
            {
                Text = $"ShotMark {info.VersionText} está disponível",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
            };
            var subtitle = new TextBlock
            {
                Text = $"Você está usando a versão {UpdateService.Format(UpdateService.CurrentVersion)}." +
                       (info.AssetUrl == null ? " Esta versão precisa ser baixada pela página de releases." : ""),
                Foreground = (Brush)Application.Current.FindResource("FgMuted"),
                Margin = new Thickness(0, 4, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            };

            var notes = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(info.Notes) ? "(sem notas para esta versão)" : info.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = (Brush)Application.Current.FindResource("PanelBg"),
                Foreground = (Brush)Application.Current.FindResource("Fg"),
                BorderBrush = (Brush)Application.Current.FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
            };

            _progress = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
            _status = new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("FgMuted"),
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };

            _install = new Button { Content = info.AssetUrl != null ? "Atualizar agora" : "Abrir página da versão", Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
            if (Application.Current.TryFindResource("AccentButton") is Style accent) _install.Style = accent;
            _skip = new Button { Content = "Pular esta versão", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
            _later = new Button { Content = "Depois", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };

            _install.Click += (_, _) => Install();
            _skip.Click += (_, _) => { Skipped = true; Close(); };
            _later.Click += (_, _) => Close();
            Closing += (_, e) =>
            {
                if (_cts != null && !_cts.IsCancellationRequested) _cts.Cancel(); // closing cancels a running download
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(_skip);
            buttons.Children.Add(_later);
            buttons.Children.Add(_install);

            var root = new DockPanel { Margin = new Thickness(20) };
            DockPanel.SetDock(title, Dock.Top);
            DockPanel.SetDock(subtitle, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(_status, Dock.Bottom);
            DockPanel.SetDock(_progress, Dock.Bottom);
            root.Children.Add(title);
            root.Children.Add(subtitle);
            root.Children.Add(buttons);
            root.Children.Add(_status);
            root.Children.Add(_progress);
            root.Children.Add(notes);
            Content = root;
        }

        private async void Install()
        {
            if (_info.AssetUrl == null)
            {
                OpenPage();
                Close();
                return;
            }
            if (!_canInstall()) return;

            _install.IsEnabled = false;
            _skip.IsEnabled = false;
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = _info.AssetSize <= 0;
            _status.Text = "Baixando…";
            _cts = new CancellationTokenSource();
            try
            {
                var progress = new Progress<double>(p =>
                {
                    _progress.IsIndeterminate = false;
                    _progress.Value = p;
                    _status.Text = _info.AssetSize > 0
                        ? $"Baixando… {p * _info.AssetSize / 1048576.0:0.0} de {_info.AssetSize / 1048576.0:0.0} MB"
                        : $"Baixando… {p:P0}";
                });
                string file = await UpdateService.DownloadAsync(_info, progress, _cts.Token);

                if (!_canInstall()) { ResetButtons("Download concluído. Tente de novo quando terminar."); return; }
                _status.Text = _info.IsInstaller ? "Abrindo o instalador… o ShotMark será reaberto ao final." : "Aplicando… o ShotMark será reaberto em instantes.";
                UpdateService.StartInstall(_info, file);
                Log.Info($"Atualização para {_info.VersionText} iniciada ({_info.AssetName}).");
                _cts = null;
                Close();
                _exitForInstall();
            }
            catch (OperationCanceledException)
            {
                // Window closed during the download.
            }
            catch (Exception ex)
            {
                Log.Error("Atualização", ex);
                ResetButtons("Não foi possível atualizar: " + ex.Message);
            }
        }

        private void ResetButtons(string message)
        {
            _status.Text = message;
            _progress.Visibility = Visibility.Collapsed;
            _install.IsEnabled = true;
            _skip.IsEnabled = true;
        }

        private void OpenPage()
        {
            try { Process.Start(new ProcessStartInfo(_info.PageUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Abrir página da versão", ex); }
        }
    }
}
