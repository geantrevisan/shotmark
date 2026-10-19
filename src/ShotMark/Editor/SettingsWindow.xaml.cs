using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ShotMark.Core;

namespace ShotMark.Editor
{
    public partial class SettingsWindow : Window
    {
        private static readonly string[] SwatchColors =
        {
            "#FFE53935", "#FFFB8C00", "#FFFFEB3B", "#FF43A047",
            "#FF1E88E5", "#FF8E24AA", "#FF000000", "#FFFFFFFF",
        };

        private readonly List<Border> _colorSwatches = new List<Border>();
        private AppSettings _working;
        private string _defaultColor;

        public SettingsWindow()
        {
            InitializeComponent();
            _working = AppSettings.Current.Clone();
            _defaultColor = _working.DefaultColor;
            SourceInitialized += (_, _) => ShotMark.Native.NativeMethods.UseDarkTitleBar(this);
            // Designed for 860×620; shrink (never grow) on small screens or high display scaling.
            ShotMark.UI.WindowFit.SizeToMonitor(this, 0.92, 0.9, 860, 620);
            BuildColorPicker();
            Load(_working);
            VersionText.Text = "Versão " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        }

        /// <summary>The saved settings when the user pressed Salvar, otherwise null.</summary>
        public AppSettings? Result { get; private set; }

        // ---- load / store ----------------------------------------------------------

        private void Load(AppSettings s)
        {
            StartWithWindows.IsChecked = s.StartWithWindows;
            StartInTray.IsChecked = s.StartInTray;
            AfterMenu.IsChecked = s.ShowActionBar;
            AfterEditor.IsChecked = !s.ShowActionBar && s.OpenEditorAfterCapture;
            AfterCopy.IsChecked = !s.ShowActionBar && !s.OpenEditorAfterCapture;
            CopyAfterCapture.IsChecked = s.CopyAfterCapture;
            CopyEditsToClipboard.IsChecked = s.CopyEditsToClipboard;
            ShowNotifications.IsChecked = s.ShowNotifications;

            UsePrintScreen.IsChecked = s.UsePrintScreenKey;
            Delay0.IsChecked = s.DefaultDelaySeconds <= 0;
            Delay3.IsChecked = s.DefaultDelaySeconds is > 0 and <= 3;
            Delay5.IsChecked = s.DefaultDelaySeconds is > 3 and <= 5;
            Delay10.IsChecked = s.DefaultDelaySeconds > 5;
            IncludeCursor.IsChecked = s.IncludeCursor;
            ShowMagnifier.IsChecked = s.ShowMagnifier;
            SmartDetection.IsChecked = s.SmartDetection;
            IndexText.IsChecked = s.IndexText;
            RecordCursor.IsChecked = s.RecordCursor;
            AutoCheckUpdates.IsChecked = s.AutoCheckUpdates;
            (s.RecordFps <= 10 ? Fps10 : s.RecordFps <= 15 ? Fps15 : s.RecordFps <= 20 ? Fps20 : Fps30).IsChecked = true;

            HkRegion.Hotkey = s.RegionHotkey;
            HkWindow.Hotkey = s.WindowHotkey;
            HkFull.Hotkey = s.FullScreenHotkey;
            HkRepeat.Hotkey = s.RepeatHotkey;
            HkDelayed.Hotkey = s.DelayedHotkey;

            LibraryFolder.Text = s.LibraryFolder;
            FileNamePattern.Text = s.FileNamePattern;
            AutoExport.IsChecked = s.AutoExport;
            AutoExportFolder.Text = s.AutoExportFolder;
            FmtPng.IsChecked = s.ExportFormat == "png";
            FmtJpg.IsChecked = s.ExportFormat == "jpg";
            FmtBmp.IsChecked = s.ExportFormat == "bmp";
            JpegQuality.Value = s.JpegQuality;

            _defaultColor = s.DefaultColor;
            HighlightColor();
            DefaultThickness.Value = s.DefaultThickness;
            DefaultFontSize.Value = s.DefaultFontSize;

            UpdateDependentControls();
        }

        private AppSettings Store()
        {
            var s = _working;
            s.StartWithWindows = StartWithWindows.IsChecked == true;
            s.StartInTray = StartInTray.IsChecked == true;
            s.ShowActionBar = AfterMenu.IsChecked == true;
            s.OpenEditorAfterCapture = AfterCopy.IsChecked != true;
            s.CopyAfterCapture = CopyAfterCapture.IsChecked == true;
            s.CopyEditsToClipboard = CopyEditsToClipboard.IsChecked == true;
            s.ShowNotifications = ShowNotifications.IsChecked == true;

            s.UsePrintScreenKey = UsePrintScreen.IsChecked == true;
            s.DefaultDelaySeconds = Delay3.IsChecked == true ? 3 : Delay5.IsChecked == true ? 5 : Delay10.IsChecked == true ? 10 : 0;
            s.IncludeCursor = IncludeCursor.IsChecked == true;
            s.ShowMagnifier = ShowMagnifier.IsChecked == true;
            s.SmartDetection = SmartDetection.IsChecked == true;
            s.IndexText = IndexText.IsChecked == true;
            s.RecordCursor = RecordCursor.IsChecked == true;
            s.AutoCheckUpdates = AutoCheckUpdates.IsChecked == true;
            s.RecordFps = Fps10.IsChecked == true ? 10 : Fps15.IsChecked == true ? 15 : Fps30.IsChecked == true ? 30 : 20;

            s.RegionHotkey = HkRegion.Hotkey;
            s.WindowHotkey = HkWindow.Hotkey;
            s.FullScreenHotkey = HkFull.Hotkey;
            s.RepeatHotkey = HkRepeat.Hotkey;
            s.DelayedHotkey = HkDelayed.Hotkey;

            s.LibraryFolder = string.IsNullOrWhiteSpace(LibraryFolder.Text) ? AppSettings.DefaultLibraryFolder : LibraryFolder.Text.Trim();
            s.FileNamePattern = string.IsNullOrWhiteSpace(FileNamePattern.Text) ? "Captura {data} {hora}" : FileNamePattern.Text.Trim();
            s.AutoExport = AutoExport.IsChecked == true;
            s.AutoExportFolder = AutoExportFolder.Text.Trim();
            s.ExportFormat = FmtJpg.IsChecked == true ? "jpg" : FmtBmp.IsChecked == true ? "bmp" : "png";
            s.JpegQuality = (int)JpegQuality.Value;

            s.DefaultColor = _defaultColor;
            s.DefaultThickness = DefaultThickness.Value;
            s.DefaultFontSize = DefaultFontSize.Value;
            return s;
        }

        private string? Validate(AppSettings s)
        {
            var hotkeys = new[] { s.RegionHotkey, s.WindowHotkey, s.FullScreenHotkey, s.RepeatHotkey, s.DelayedHotkey }
                .Where(h => !h.IsEmpty).ToList();
            var dup = hotkeys.GroupBy(h => (h.Modifiers, h.Key)).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) return $"O atalho {dup.First()} está repetido.";

            foreach (var folder in new[] { s.LibraryFolder, s.AutoExport ? s.AutoExportFolder : null })
            {
                if (folder == null) continue;
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    return $"Não foi possível usar a pasta \"{folder}\": {ex.Message}";
                }
            }
            return null;
        }

        // ---- navigation --------------------------------------------------------------

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageGeneral == null) return; // during InitializeComponent
            string tag = (sender as FrameworkElement)?.Tag as string ?? "General";
            PageGeneral.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            PageCapture.Visibility = tag == "Capture" ? Visibility.Visible : Visibility.Collapsed;
            PageHotkeys.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
            PageOutput.Visibility = tag == "Output" ? Visibility.Visible : Visibility.Collapsed;
            PageEditor.Visibility = tag == "Editor" ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- controls ----------------------------------------------------------------

        private void UpdateDependentControls()
        {
            if (AutoExportPanel == null) return;
            AutoExportPanel.IsEnabled = AutoExport.IsChecked == true;
            AutoExportPanel.Opacity = AutoExport.IsChecked == true ? 1 : 0.45;
            JpegCard.Visibility = FmtJpg.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AutoExport_Changed(object sender, RoutedEventArgs e) => UpdateDependentControls();
        private void Format_Changed(object sender, RoutedEventArgs e) => UpdateDependentControls();

        private void JpegQuality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (JpegQualityText != null) JpegQualityText.Text = $"{(int)e.NewValue}%";
        }

        private void DefaultThickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DefaultThicknessText != null) DefaultThicknessText.Text = ((int)e.NewValue).ToString();
        }

        private void DefaultFontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DefaultFontSizeText != null) DefaultFontSizeText.Text = ((int)e.NewValue).ToString();
        }

        private void FileNamePattern_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (FileNamePreview == null) return;
            var tmp = new AppSettings { FileNamePattern = FileNamePattern.Text };
            FileNamePreview.Text = "Exemplo: " + tmp.BuildFileName(DateTime.Now) + ".png";
        }

        private void BuildColorPicker()
        {
            foreach (var hex in SwatchColors)
            {
                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(2, 0, 2, 0),
                    CornerRadius = new CornerRadius(13),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = hex,
                    Child = new Border
                    {
                        Width = 18,
                        Height = 18,
                        CornerRadius = new CornerRadius(9),
                        Background = new SolidColorBrush(ColorUtil.Parse(hex)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1),
                    },
                };
                swatch.MouseLeftButtonUp += (_, _) =>
                {
                    _defaultColor = hex;
                    HighlightColor();
                };
                _colorSwatches.Add(swatch);
                DefaultColorPanel.Children.Add(swatch);
            }
        }

        private void HighlightColor()
        {
            foreach (var s in _colorSwatches)
            {
                bool on = string.Equals((string)s.Tag, _defaultColor, StringComparison.OrdinalIgnoreCase);
                s.BorderBrush = on ? (Brush)FindResource("Accent") : Brushes.Transparent;
            }
        }

        private string? PickFolder(string current)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Escolha a pasta",
                InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }

        private void BrowseLibrary_Click(object sender, RoutedEventArgs e)
        {
            var f = PickFolder(LibraryFolder.Text);
            if (f != null) LibraryFolder.Text = f;
        }

        private void BrowseExport_Click(object sender, RoutedEventArgs e)
        {
            var f = PickFolder(AutoExportFolder.Text);
            if (f != null) AutoExportFolder.Text = f;
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { }
        }

        private void LinkedIn_Click(object sender, RoutedEventArgs e) => OpenUrl("https://br.linkedin.com/in/geanfioramonte");

        private void GitHub_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/geantrevisan");

        private void WhatsNew_Click(object sender, RoutedEventArgs e) =>
            (Application.Current as App)?.ShowWhatsNew();

        private void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
            (Application.Current as App)?.CheckForUpdatesInteractive();

        private void OpenLibrary_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(Library.Folder);
            Process.Start(new ProcessStartInfo(Library.Folder) { UseShellExecute = true });
        }

        private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotMark");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }

        // ---- footer -------------------------------------------------------------------

        private void Defaults_Click(object sender, RoutedEventArgs e)
        {
            var keep = _working.LastRegion;
            _working = new AppSettings { LastRegion = keep };
            Load(_working);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var s = Store();
            var error = Validate(s);
            if (error != null)
            {
                MessageBox.Show(this, error, "Configurações", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Result = s;
            DialogResult = true;
        }
    }
}
