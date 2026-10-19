using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ShotMark.Core;
using ShotMark.Services;
using ShotMark.UI;

namespace ShotMark.Editor
{
    public partial class EditorWindow : Window
    {
        private static readonly string[] PaletteColors =
        {
            "#FFE53935", "#FFFB8C00", "#FFFFEB3B", "#FF43A047",
            "#FF1E88E5", "#FF8E24AA", "#FF000000", "#FFFFFFFF",
        };

        private static readonly Dictionary<EditorTool, string> ToolHints = new Dictionary<EditorTool, string>
        {
            [EditorTool.Select] = "Clique ou arraste um retângulo para selecionar (Shift soma). Duplo clique edita textos. Ctrl+C / Ctrl+V copiam camadas.",
            [EditorTool.Arrow] = "Arraste para desenhar uma seta. Shift prende em 45°.",
            [EditorTool.Line] = "Arraste para desenhar uma linha. Shift prende em 45°.",
            [EditorTool.Rectangle] = "Arraste para desenhar um retângulo. Shift: quadrado.",
            [EditorTool.Ellipse] = "Arraste para desenhar uma elipse. Shift: círculo.",
            [EditorTool.Pen] = "Desenhe à mão livre.",
            [EditorTool.Highlighter] = "Passe sobre o texto para destacar.",
            [EditorTool.Text] = "Clique onde o texto deve começar.",
            [EditorTool.Callout] = "Clique para inserir um balão de texto.",
            [EditorTool.Step] = "Clique para inserir o próximo número.",
            [EditorTool.Pixelate] = "Arraste sobre dados sensíveis para ocultá-los. A espessura controla o tamanho dos blocos.",
            [EditorTool.Crop] = "Arraste a área que deve permanecer.",
            [EditorTool.Blur] = "Arraste sobre a área para desfocar. A espessura controla a intensidade.",
            [EditorTool.Spotlight] = "Arraste a área de destaque: o resto da imagem fica escurecido.",
            [EditorTool.Stamp] = "Escolha o carimbo e clique na imagem. Arraste a alça para mudar o tamanho.",
            [EditorTool.Magnify] = "Arraste sobre um detalhe para ampliá-lo dentro de uma lupa.",
        };

        private readonly EditHistory _history = new EditHistory();
        private readonly DispatcherTimer _autosave;
        private readonly List<Border> _swatches = new List<Border>();
        private CaptureDocument? _doc;
        private bool _dirty;
        private double _zoom = 1.0;
        private int _lastWidth;
        private int _lastHeight;
        private bool _suppressPropertyEdit;
        private Annotation? _lastPropertyEditTarget;
        private DateTime _lastPropertyEditTime;
        private bool _syncingLayers;
        private TextBox? _inlineBox;
        private TextEditRequest? _inlineRequest;
        private bool _closingInline;
        private Point _dragStart;
        private List<LibraryItem> _libraryAll = new List<LibraryItem>();

        private const string LayersClipboardFormat = "ShotMark.Layers";

        private static readonly string[] FontChoices =
        {
            "Segoe UI", "Arial", "Calibri", "Verdana", "Tahoma", "Georgia", "Times New Roman",
            "Consolas", "Courier New", "Comic Sans MS", "Impact", "Segoe Print",
        };

        public EditorWindow()
        {
            InitializeComponent();

            BuildPalette();
            foreach (var rb in ToolPanel.Children.OfType<RadioButton>())
            {
                rb.Checked += Tool_Checked;
            }
            BuildToolLabels();
            SetRailExpanded(AppSettings.Current.ToolRailExpanded, animate: false);

            Surface.TextEditRequested += Surface_TextEditRequested;
            BuildFontMenu();
            Surface.BeforeChange += (_, _) => _history.Push(TakeSnapshot());
            Surface.Changed += (_, _) => OnDocumentChanged();
            Surface.SelectionChanged += (_, _) => OnSelectionChanged();
            Surface.Tool = EditorTool.Arrow;

            _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _autosave.Tick += (_, _) =>
            {
                _autosave.Stop();
                SaveCurrent(showStatus: false);
                // Keep the clipboard in sync with the edits, so Ctrl+V elsewhere pastes the annotated image.
                if (AppSettings.Current.CopyEditsToClipboard && !ClipboardHasLayers() && _doc != null && _doc.Annotations.Count > 0 && Flattened() is BitmapSource edited)
                    ClipboardUtil.SetImage(edited);
            };

            SourceInitialized += (_, _) => ShotMark.Native.NativeMethods.UseDarkTitleBar(this);
            // Size to the user's screen (or reuse the last position) instead of a fixed 1360×860.
            WindowFit.SizeToMonitor(this, 0.85, 0.88, 2000, 1300,
                AppSettings.Current.EditorBounds, AppSettings.Current.EditorMaximized, maximizeOnSmallScreens: true);
            LocationChanged += (_, _) => RememberBounds();
            SizeChanged += (_, _) => RememberBounds();
            PreviewKeyDown += Window_PreviewKeyDown;
            Drop += Window_Drop;
            Loaded += (_, _) => RefreshLibrary();
            Closing += Window_Closing;

            UpdateUi();
            ToolHint.Text = ToolHints[EditorTool.Arrow];
            ApplySettings();
        }

        /// <summary>Raised when the user asks for a new region capture.</summary>
        public event EventHandler? CaptureRequested;

        /// <summary>Raised when the user asks for a full-screen capture.</summary>
        public event EventHandler? FullScreenCaptureRequested;

        public event EventHandler<int>? DelayedCaptureRequested;
        public event EventHandler? RepeatCaptureRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? ScrollCaptureRequested;
        public event EventHandler<bool>? RecordRequested; // true = GIF, false = MP4
        public event EventHandler? WhatsNewRequested;

        /// <summary>Red dot on the "Novidades" button while the notes of a new version were not opened yet.</summary>
        public void SetNewsBadge(bool on) => NewsBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Applies the default colour / thickness / font from the settings.</summary>
        public void ApplySettings()
        {
            var s = AppSettings.Current;
            if (Surface.Selected != null) return; // don't restyle the selected layer
            _suppressPropertyEdit = true;
            PickColor(s.DefaultColor);
            ThicknessSlider.Value = Math.Max(ThicknessSlider.Minimum, Math.Min(ThicknessSlider.Maximum, s.DefaultThickness));
            FontSlider.Value = Math.Max(FontSlider.Minimum, Math.Min(FontSlider.Maximum, s.DefaultFontSize));
            _suppressPropertyEdit = false;
        }

        public void RefreshLibraryPublic() => RefreshLibrary();

        // ---- documents -----------------------------------------------------------

        public void OpenDocument(CaptureDocument doc)
        {
            CloseInlineEditor(commit: true);
            SaveCurrent(showStatus: false);
            _doc = doc;
            _dirty = doc.FilePath == null;   // new captures/imports get saved into the library
            _history.Clear();
            _lastWidth = doc.Image.PixelWidth;
            _lastHeight = doc.Image.PixelHeight;
            Surface.SetDocument(doc);
            EmptyHint.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ZoomToFit));
            if (_dirty) SaveCurrent(showStatus: false);
            RebuildLayers();
            RefreshLibrary();
            UpdateUi();
            SetStatus($"Aberto: {doc.DisplayName}");
        }

        private void OpenPath(string path)
        {
            try
            {
                CaptureDocument doc;
                if (string.Equals(Path.GetExtension(path), DocumentStore.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    if (_doc?.FilePath != null && string.Equals(Path.GetFullPath(_doc.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                        return;
                    doc = DocumentStore.Load(path);
                }
                else
                {
                    doc = new CaptureDocument(ImageUtil.LoadFile(path));
                }
                OpenDocument(doc);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Não foi possível abrir o arquivo.\n\n{ex.Message}", "ShotMark", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveCurrent(bool showStatus)
        {
            _autosave.Stop();
            if (_doc == null || !_dirty) return;
            try
            {
                DocumentStore.Save(_doc, _doc.FilePath ?? Library.NewPath());
                _dirty = false;
                if (showStatus) SetStatus($"Salvo em {_doc.FilePath}");
                RefreshLibrary();
                UpdateUi();
            }
            catch (Exception ex)
            {
                SetStatus("Erro ao salvar: " + ex.Message);
            }
        }

        private void OnDocumentChanged()
        {
            if (_doc != null && (_doc.Image.PixelWidth != _lastWidth || _doc.Image.PixelHeight != _lastHeight))
            {
                // Image size changed (crop): fit the new image to the window.
                _lastWidth = _doc.Image.PixelWidth;
                _lastHeight = _doc.Image.PixelHeight;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ZoomToFit));
            }
            _dirty = true;
            _autosave.Stop();
            _autosave.Start();
            RebuildLayers();
            UpdateUi();
        }

        private EditorSnapshot TakeSnapshot()
        {
            if (_doc == null) throw new InvalidOperationException("Nenhum documento aberto.");
            return new EditorSnapshot(_doc.Image, AnnotationSerializer.Serialize(_doc.Annotations));
        }

        private void Restore(EditorSnapshot s)
        {
            if (_doc == null) return;
            bool sizeChanged = s.Image.PixelWidth != _doc.Image.PixelWidth || s.Image.PixelHeight != _doc.Image.PixelHeight;
            _doc.Image = s.Image;
            _doc.Annotations = AnnotationSerializer.Deserialize(s.AnnotationsJson);
            Surface.SetDocument(_doc);
            if (sizeChanged) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ZoomToFit));
            OnDocumentChanged();
        }

        private void Undo()
        {
            if (_doc == null) return;
            var s = _history.Undo(TakeSnapshot());
            if (s != null) Restore(s);
        }

        private void Redo()
        {
            if (_doc == null) return;
            var s = _history.Redo(TakeSnapshot());
            if (s != null) Restore(s);
        }

        private static bool ClipboardHasLayers()
        {
            try { return Clipboard.ContainsData(LayersClipboardFormat); }
            catch (Exception) { return false; }
        }

        private BitmapSource? Flattened() => _doc == null ? null : ImageUtil.Flatten(_doc.Image, _doc.Annotations);

        /// <summary>Ctrl+C: selected layers if any, otherwise the whole annotated image.</summary>
        private void CopySmart()
        {
            var json = Surface.CopySelection();
            if (json == null)
            {
                CopyToClipboard();
                return;
            }
            try
            {
                var data = new DataObject();
                data.SetData(LayersClipboardFormat, json);
                Clipboard.SetDataObject(data, copy: true);
                SetStatus($"{Surface.SelectedItems.Count} camada(s) copiada(s). Ctrl+V cola aqui ou em outra captura · Ctrl+Shift+C copia a imagem.");
            }
            catch (Exception ex)
            {
                SetStatus("Não foi possível copiar: " + ex.Message);
            }
        }

        /// <summary>Ctrl+V: layers copied from ShotMark, otherwise an image/file as a new capture.</summary>
        private void PasteSmart()
        {
            try
            {
                if (_doc != null && Clipboard.ContainsData(LayersClipboardFormat) && Clipboard.GetData(LayersClipboardFormat) is string json)
                {
                    if (Surface.Tool != EditorTool.Select) SelectTool(EditorTool.Select);
                    if (Surface.PasteLayers(json)) SetStatus("Camadas coladas.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Colar camadas", ex);
            }
            PasteFromClipboard();
        }

        private void CopyToClipboard()
        {
            var img = Flattened();
            if (img == null) return;
            SetStatus(ClipboardUtil.SetImage(img)
                ? "Imagem copiada para a área de transferência."
                : "Não foi possível copiar: outro programa está usando a área de transferência.");
        }

        private void PasteFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null) OpenDocument(new CaptureDocument(ImageUtil.Normalize(img, dropAlpha: true)));
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault();
                    if (file != null) OpenPath(file);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Não foi possível colar: " + ex.Message);
            }
        }

        // ---- UI state ------------------------------------------------------------

        private void UpdateUi()
        {
            bool has = _doc != null;
            UndoButton.IsEnabled = has && _history.CanUndo;
            RedoButton.IsEnabled = has && _history.CanRedo;
            bool sel = Surface.Selected != null;
            LayerUpButton.IsEnabled = sel;
            LayerDownButton.IsEnabled = sel;
            LayerDuplicateButton.IsEnabled = sel;
            LayerRenameButton.IsEnabled = Surface.SelectedItems.Count == 1;
            LayerDeleteButton.IsEnabled = sel;
            SizeText.Text = has ? $"{_doc!.Image.PixelWidth} × {_doc.Image.PixelHeight} px" : "";
            Title = has ? $"{_doc!.DisplayName}{(_dirty ? " •" : "")} — ShotMark" : "ShotMark";
            EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            CanvasShadow.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            UpdateFontPanel();
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void OnSelectionChanged()
        {
            var sel = Surface.Selected;
            if (sel != null)
            {
                // Reflect the selected annotation's properties in the toolbar.
                _suppressPropertyEdit = true;
                ThicknessSlider.Value = Math.Max(ThicknessSlider.Minimum, Math.Min(ThicknessSlider.Maximum, sel.Thickness));
                if (sel is TextAnnotation ta)
                {
                    FontSlider.Value = Math.Max(FontSlider.Minimum, Math.Min(FontSlider.Maximum, ta.FontSize));
                    FontFamilyText.Text = ta.FontFamilyName;
                    BoldToggle.IsChecked = ta.Bold;
                    ItalicToggle.IsChecked = ta.Italic;
                }
                DashedToggle.IsChecked = sel.Dashed;
                if (sel is ArrowAnnotation arrow) DoubleHeadToggle.IsChecked = arrow.DoubleHead;
                OpacitySlider.Value = Math.Round(Math.Max(0.1, Math.Min(1, sel.Opacity)) * 100);
                if (sel is RectangleAnnotation ra) FillToggle.IsChecked = ra.Filled;
                else if (sel is EllipseAnnotation ea) FillToggle.IsChecked = ea.Filled;
                HighlightSwatch(sel.ColorHex);
                _suppressPropertyEdit = false;
            }
            SyncLayerSelection();
            UpdateUi();
        }

        private void UpdateFontPanel()
        {
            bool text = Surface.Tool == EditorTool.Text || Surface.Tool == EditorTool.Callout || Surface.Selected is TextAnnotation;
            FontPanel.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            bool fill = Surface.Tool == EditorTool.Rectangle || Surface.Tool == EditorTool.Ellipse
                        || Surface.Selected is RectangleAnnotation || Surface.Selected is EllipseAnnotation;
            FillPanel.Visibility = fill ? Visibility.Visible : Visibility.Collapsed;

            var t = Surface.Tool;
            var selected = Surface.Selected;
            bool lineLike = t is EditorTool.Arrow or EditorTool.Line or EditorTool.Rectangle or EditorTool.Ellipse
                            || selected is ArrowAnnotation || selected is LineAnnotation || selected is RectangleAnnotation || selected is EllipseAnnotation;
            StylePanel.Visibility = lineLike ? Visibility.Visible : Visibility.Collapsed;
            DoubleHeadToggle.Visibility = t == EditorTool.Arrow || selected is ArrowAnnotation ? Visibility.Visible : Visibility.Collapsed;
            StampPanel.Visibility = t == EditorTool.Stamp || selected is StampAnnotation ? Visibility.Visible : Visibility.Collapsed;
            RestartStepsButton.Visibility = t == EditorTool.Step ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- text style, line style, stamps, steps ------------------------------------

        private void BuildFontMenu()
        {
            foreach (var name in FontChoices)
            {
                var item = new MenuItem { Header = name, FontFamily = new FontFamily(name) };
                item.Click += (_, _) =>
                {
                    FontFamilyText.Text = name;
                    Surface.CurrentFontFamily = name;
                    EditSelected(a => { if (a is TextAnnotation ta) ta.FontFamilyName = name; });
                    if (_inlineBox != null) _inlineBox.FontFamily = new FontFamily(name);
                };
                FontMenu.Items.Add(item);
            }
        }

        private void FontFamily_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

        private void Bold_Click(object sender, RoutedEventArgs e)
        {
            bool on = BoldToggle.IsChecked == true;
            Surface.CurrentBold = on;
            EditSelected(a => { if (a is TextAnnotation ta) ta.Bold = on; });
            if (_inlineBox != null) _inlineBox.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void Italic_Click(object sender, RoutedEventArgs e)
        {
            bool on = ItalicToggle.IsChecked == true;
            Surface.CurrentItalic = on;
            EditSelected(a => { if (a is TextAnnotation ta) ta.Italic = on; });
            if (_inlineBox != null) _inlineBox.FontStyle = on ? FontStyles.Italic : FontStyles.Normal;
        }

        private void Dashed_Changed(object sender, RoutedEventArgs e)
        {
            if (Surface == null) return;
            bool on = DashedToggle.IsChecked == true;
            Surface.CurrentDashed = on;
            EditSelected(a =>
            {
                if (a is LineAnnotation || a is ArrowAnnotation || a is RectangleAnnotation || a is EllipseAnnotation) a.Dashed = on;
            });
        }

        private void DoubleHead_Changed(object sender, RoutedEventArgs e)
        {
            if (Surface == null) return;
            bool on = DoubleHeadToggle.IsChecked == true;
            Surface.CurrentDoubleHead = on;
            EditSelected(a => { if (a is ArrowAnnotation ar) ar.DoubleHead = on; });
        }

        private void Stamp_Checked(object sender, RoutedEventArgs e)
        {
            if (Surface == null || sender is not FrameworkElement fe || fe.Tag is not string kind) return;
            Surface.CurrentStamp = kind;
            EditSelected(a => { if (a is StampAnnotation st) st.Kind = kind; });
        }

        private void RestartSteps_Click(object sender, RoutedEventArgs e)
        {
            Surface.RestartNumbering();
            SetStatus("O próximo passo será o número 1.");
        }

        // ---- in-place text editor ------------------------------------------------------

        private void Surface_TextEditRequested(object? sender, TextEditRequest request)
        {
            CloseInlineEditor(commit: true);
            var ta = request.Annotation;
            var color = ta.StrokeColor;
            var box = new TextBox
            {
                Text = ta.Text,
                AcceptsReturn = true,
                AcceptsTab = false,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = ta.Typeface.FontFamily,
                FontWeight = ta.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = ta.Italic ? FontStyles.Italic : FontStyles.Normal,
                FontSize = ta.FontSize,
                MinWidth = Math.Max(40, ta.FontSize * 2),
                BorderThickness = new Thickness(1.5 / Math.Max(0.05, _zoom)),
                BorderBrush = (Brush)FindResource("Accent"),
                Padding = ta.Callout ? new Thickness(ta.Padding - 1) : new Thickness(0),
                Background = ta.Callout ? new SolidColorBrush(color) : new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                Foreground = ta.Callout
                    ? (ColorUtil.IsLight(color) ? Brushes.Black : Brushes.White)
                    : new SolidColorBrush(color),
                CaretBrush = ta.Callout && ColorUtil.IsLight(color) ? Brushes.Black : Brushes.White,
            };
            Canvas.SetLeft(box, ta.X - (ta.Callout ? 0 : 2));
            Canvas.SetTop(box, ta.Y - (ta.Callout ? 0 : 1));
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    CloseInlineEditor(commit: false);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    CloseInlineEditor(commit: true);
                    e.Handled = true;
                }
            };
            box.LostKeyboardFocus += (_, _) => CloseInlineEditor(commit: true);

            _inlineBox = box;
            _inlineRequest = request;
            EditLayer.Children.Add(box);
            SetStatus("Digite o texto. Enter quebra a linha · Ctrl+Enter ou clique fora para concluir · Esc cancela.");
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                box.Focus();
                Keyboard.Focus(box);
                box.CaretIndex = box.Text.Length;
                if (!request.IsNew) box.SelectAll();
            }));
        }

        private void CloseInlineEditor(bool commit)
        {
            if (_inlineBox == null || _inlineRequest == null || _closingInline) return;
            _closingInline = true;
            try
            {
                var box = _inlineBox;
                var req = _inlineRequest;
                _inlineBox = null;
                _inlineRequest = null;
                string? text = commit ? box.Text.TrimEnd() : null;
                EditLayer.Children.Remove(box);
                Surface.EndEditText(req.Annotation, text, req.IsNew);
                Surface.Focus();
                SetStatus("");
            }
            finally
            {
                _closingInline = false;
            }
        }

        private void Fill_Changed(object sender, RoutedEventArgs e)
        {
            if (Surface == null) return;
            bool on = FillToggle.IsChecked == true;
            Surface.CurrentFilled = on;
            EditSelected(a =>
            {
                if (a is RectangleAnnotation r) r.Filled = on;
                else if (a is EllipseAnnotation el) el.Filled = on;
            });
        }

        private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityText == null || Surface == null) return;
            OpacityText.Text = $"{(int)e.NewValue}%";
            Surface.CurrentOpacity = e.NewValue / 100.0;
            EditSelected(a => a.Opacity = e.NewValue / 100.0);
        }

        // ---- capture menu, image menu, pin, print, settings --------------------------

        private static void OpenButtonMenu(object sender)
        {
            if (sender is Button b && b.ContextMenu != null)
            {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                b.ContextMenu.IsOpen = true;
            }
        }

        private void CaptureMore_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);
        private void ImageMenu_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);
        private void Delay3_Click(object sender, RoutedEventArgs e) => DelayedCaptureRequested?.Invoke(this, 3);
        private void Delay5_Click(object sender, RoutedEventArgs e) => DelayedCaptureRequested?.Invoke(this, 5);
        private void Delay10_Click(object sender, RoutedEventArgs e) => DelayedCaptureRequested?.Invoke(this, 10);
        private void RepeatRegion_Click(object sender, RoutedEventArgs e) => RepeatCaptureRequested?.Invoke(this, EventArgs.Empty);
        private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        private void WhatsNew_Click(object sender, RoutedEventArgs e) => WhatsNewRequested?.Invoke(this, EventArgs.Empty);
        private void ScrollCapture_Click(object sender, RoutedEventArgs e) => ScrollCaptureRequested?.Invoke(this, EventArgs.Empty);
        private void RecordMp4_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke(this, false);
        private void RecordGif_Click(object sender, RoutedEventArgs e) => RecordRequested?.Invoke(this, true);
        private void MoreMenu_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);
        private void CopyImage_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

        /// <summary>Writes the annotated image to a temp PNG and returns its path.</summary>
        private string? ExportTempPng()
        {
            var img = Flattened();
            if (img == null || _doc == null) return null;
            string dir = Path.Combine(Path.GetTempPath(), "ShotMark");
            Directory.CreateDirectory(dir);
            string name = string.Concat(_doc.DisplayName.Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(dir, (string.IsNullOrWhiteSpace(name) ? "Captura" : name) + ".png");
            ImageUtil.SaveImage(img, path);
            return path;
        }

        private void CopyAsFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = ExportTempPng();
                if (path == null) return;
                var files = new System.Collections.Specialized.StringCollection { path };
                Clipboard.SetFileDropList(files);
                SetStatus("Arquivo copiado: cole (Ctrl+V) no e-mail, chat ou numa pasta.");
            }
            catch (Exception ex)
            {
                Log.Error("Copiar como arquivo", ex);
                SetStatus("Não foi possível copiar o arquivo: " + ex.Message);
            }
        }

        private void ExportPdf_Click(object sender, RoutedEventArgs e)
        {
            var img = Flattened();
            if (img == null || _doc == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = _doc.DisplayName,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                PdfWriter.WriteImage(img, dlg.FileName);
                SetStatus($"PDF salvo em {dlg.FileName}");
            }
            catch (Exception ex)
            {
                Log.Error("Exportar PDF", ex);
                MessageBox.Show(this, $"Falha ao gerar o PDF.\n\n{ex.Message}", "ShotMark", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RevealCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (_doc?.FilePath != null && File.Exists(_doc.FilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_doc.FilePath}\"") { UseShellExecute = true });
        }

        private void DragOut_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

        private void DragOut_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _doc == null) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4) return;
            try
            {
                var path = ExportTempPng();
                var img = Flattened();
                if (path == null || img == null) return;
                var data = new DataObject();
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
                data.SetImage(img);
                DragDrop.DoDragDrop(DragOutButton, data, DragDropEffects.Copy);
                SetStatus("Imagem arrastada.");
            }
            catch (Exception ex)
            {
                Log.Error("Arrastar imagem", ex);
            }
            e.Handled = true;
        }

        private void RotateRight_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            int h = _doc.Image.PixelHeight;
            Surface.TransformImage(img => ImageUtil.Rotate(img, 90), p => new Point(h - p.Y, p.X));
        }

        private void RotateLeft_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            int w = _doc.Image.PixelWidth;
            Surface.TransformImage(img => ImageUtil.Rotate(img, -90), p => new Point(p.Y, w - p.X));
        }

        private void FlipH_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            int w = _doc.Image.PixelWidth;
            Surface.TransformImage(img => ImageUtil.Flip(img, horizontal: true), p => new Point(w - p.X, p.Y));
        }

        private void FlipV_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            int h = _doc.Image.PixelHeight;
            Surface.TransformImage(img => ImageUtil.Flip(img, horizontal: false), p => new Point(p.X, h - p.Y));
        }

        private void Resize_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            int w = _doc.Image.PixelWidth, h = _doc.Image.PixelHeight;
            var answer = TextInputDialog.Show(this, $"Redimensionar ({w} × {h} px) — novo tamanho em %", "50");
            if (answer == null) return;
            if (!double.TryParse(answer.Trim().TrimEnd('%').Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double pct) || pct < 1 || pct > 800)
            {
                SetStatus("Informe uma porcentagem entre 1 e 800.");
                return;
            }
            double s = pct / 100.0;
            int nw = Math.Max(1, (int)Math.Round(w * s)), nh = Math.Max(1, (int)Math.Round(h * s));
            double sx = (double)nw / w, sy = (double)nh / h;
            Surface.TransformImage(img => ImageUtil.Resize(img, nw, nh), p => new Point(p.X * sx, p.Y * sy), s);
            SetStatus($"Imagem redimensionada para {nw} × {nh} px.");
        }

        private void Border_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            var answer = TextInputDialog.Show(this, "Espessura da borda em pixels", "12");
            if (answer == null || !int.TryParse(answer.Trim(), out int t) || t < 1 || t > 500) return;
            var color = Surface.CurrentColor;
            Surface.TransformImage(img => ImageUtil.AddBorder(img, t, color), p => new Point(p.X + t, p.Y + t));
        }

        // ---- tool rail with labels (retractable) ------------------------------------

        private static readonly Dictionary<string, string> ToolLabels = new()
        {
            ["Select"] = "Selecionar",
            ["Arrow"] = "Seta",
            ["Line"] = "Linha",
            ["Rectangle"] = "Retângulo",
            ["Ellipse"] = "Elipse",
            ["Pen"] = "Caneta",
            ["Highlighter"] = "Marca-texto",
            ["Text"] = "Texto",
            ["Callout"] = "Balão",
            ["Step"] = "Passos",
            ["Stamp"] = "Carimbos",
            ["Magnify"] = "Lupa",
            ["Pixelate"] = "Pixelar",
            ["Blur"] = "Desfocar",
            ["Spotlight"] = "Holofote",
            ["Crop"] = "Recortar",
        };

        private const double RailCollapsedWidth = 38;
        private const double RailExpandedWidth = 148;
        private readonly List<TextBlock> _toolLabels = new();
        private bool _railExpanded;

        private void BuildToolLabels()
        {
            foreach (var rb in ToolPanel.Children.OfType<RadioButton>())
            {
                if (rb.Content is not UIElement icon || rb.Tag is not string tag) continue;
                string shortcut = "";
                if (rb.ToolTip is string tip)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(tip, @"\((\w)\)");
                    if (m.Success) shortcut = m.Groups[1].Value;
                }
                rb.Content = null;
                var label = new TextBlock
                {
                    Text = ToolLabels.TryGetValue(tag, out var t) ? t : tag,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                };
                var key = new TextBlock
                {
                    Text = shortcut,
                    Opacity = 0.45,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                var labelRow = new DockPanel { LastChildFill = true, Visibility = Visibility.Collapsed };
                DockPanel.SetDock(key, Dock.Right);
                labelRow.Children.Add(key);
                labelRow.Children.Add(label);
                labelRow.Tag = "label";

                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(icon, Dock.Left);
                row.Children.Add(icon);
                row.Children.Add(labelRow);
                rb.Content = row;
                _toolLabels.Add(label);
            }
        }

        private void SetRailExpanded(bool expanded, bool animate = true)
        {
            _railExpanded = expanded;
            double target = expanded ? RailExpandedWidth : RailCollapsedWidth;
            foreach (var rb in ToolPanel.Children.OfType<RadioButton>())
            {
                rb.HorizontalContentAlignment = expanded ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
                rb.Padding = expanded ? new Thickness(9, 0, 10, 0) : new Thickness(0);
                if (rb.Content is DockPanel row)
                {
                    foreach (var child in row.Children.OfType<DockPanel>())
                        child.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                }
                if (animate)
                {
                    var anim = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(140))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase(),
                    };
                    rb.BeginAnimation(WidthProperty, anim);
                }
                else
                {
                    rb.BeginAnimation(WidthProperty, null);
                    rb.Width = target;
                }
            }
            RailToggleIcon.Data = expanded ? Icons.CollapseLeft : Icons.Menu;
            RailToggle.ToolTip = expanded ? "Recolher o menu (só ícones)" : "Expandir o menu (mostrar nomes)";
        }

        private void RailToggle_Click(object sender, RoutedEventArgs e)
        {
            SetRailExpanded(!_railExpanded);
            AppSettings.Current.ToolRailExpanded = _railExpanded;
            try { AppSettings.Current.Save(); } catch (Exception) { }
        }

        private void BlurAll_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            Surface.AddLayer(new BlurAnnotation
            {
                X1 = 0, Y1 = 0, X2 = _doc.Image.PixelWidth, Y2 = _doc.Image.PixelHeight,
                Strength = Math.Max(6, Surface.CurrentThickness * 2.5),
                Name = "Desfoque (imagem inteira)",
            }, atBottom: true);
            SetStatus("Imagem desfocada. A intensidade segue a Espessura; a camada pode ser ocultada ou apagada.");
        }

        private void PixelateAll_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            Surface.AddLayer(new PixelateAnnotation
            {
                X1 = 0, Y1 = 0, X2 = _doc.Image.PixelWidth, Y2 = _doc.Image.PixelHeight,
                BlockSize = Math.Max(8, Surface.CurrentThickness * 3),
                Name = "Pixelado (imagem inteira)",
            }, atBottom: true);
            SetStatus("Imagem pixelada. A camada pode ser ocultada ou apagada no painel Camadas.");
        }

        private void Shadow_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            const int m = 28;
            Surface.TransformImage(img => ImageUtil.AddShadow(img, m), p => new Point(p.X + m, p.Y + m));
            SetStatus("Sombra adicionada. Exporte em PNG para manter a transparência.");
        }

        private void Pin_Click(object sender, RoutedEventArgs e) => PinToScreen();

        private void PinToScreen()
        {
            var img = Flattened();
            if (img == null) return;
            var pin = new PinWindow(img);
            pin.Show();
            SetStatus("Imagem fixada na tela. Arraste para mover, roda do mouse para zoom, duplo clique ou Esc para fechar.");
        }

        private void Print_Click(object sender, RoutedEventArgs e) => PrintImage();

        private void PrintImage()
        {
            var img = Flattened();
            if (img == null || _doc == null) return;
            var dlg = new PrintDialog();
            if (dlg.ShowDialog() != true) return;
            var size = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
            var view = new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(24) };
            view.Measure(size);
            view.Arrange(new Rect(size));
            dlg.PrintVisual(view, _doc.DisplayName);
            SetStatus("Enviado para a impressora.");
        }

        // ---- layers panel ----------------------------------------------------------

        /// <summary>Rebuilds the layer rows (topmost layer first, like Photoshop).</summary>
        private void RebuildLayers()
        {
            _syncingLayers = true;
            try
            {
                if (_doc == null)
                {
                    LayersList.ItemsSource = null;
                    LayerCountText.Text = "0";
                    BackgroundThumb.Source = null;
                    LayersEmptyText.Visibility = Visibility.Visible;
                    return;
                }

                var items = Enumerable.Reverse(_doc.Annotations).Select(a => new LayerItem(a)).ToList();
                LayersList.ItemsSource = items;
                LayerCountText.Text = items.Count.ToString();
                LayersEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (!ReferenceEquals(BackgroundThumb.Tag, _doc.Image))
                {
                    BackgroundThumb.Source = ImageUtil.Thumbnail(_doc.Image, 96);
                    BackgroundThumb.Tag = _doc.Image;
                }
                bool anyHidden = _doc.Annotations.Any(a => !a.Visible);
                ToggleAllIcon.Data = anyHidden ? Icons.EyeOff : Icons.Eye;
                SelectLayerRows(items);
            }
            finally
            {
                _syncingLayers = false;
            }
        }

        private void SelectLayerRows(IEnumerable<LayerItem> items)
        {
            LayersList.SelectedItems.Clear();
            foreach (var i in items)
            {
                if (Surface.SelectedItems.Contains(i.Model)) LayersList.SelectedItems.Add(i);
            }
        }

        private void SyncLayerSelection()
        {
            if (LayersList.ItemsSource is not IEnumerable<LayerItem> items) return;
            _syncingLayers = true;
            try
            {
                SelectLayerRows(items);
                var match = items.FirstOrDefault(i => ReferenceEquals(i.Model, Surface.Selected));
                if (match != null) LayersList.ScrollIntoView(match);
            }
            finally
            {
                _syncingLayers = false;
            }
        }

        private void Layers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLayers) return;
            var picked = LayersList.SelectedItems.OfType<LayerItem>().Select(i => i.Model).ToList();
            if (picked.Count == 0) return;
            // Picking a layer switches to the move tool so it can be dragged right away.
            if (Surface.Tool != EditorTool.Select) SelectTool(EditorTool.Select);
            Surface.SetSelection(picked);
        }

        private static LayerItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as LayerItem;

        private void LayerVisible_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is LayerItem item)
            {
                Surface.ChangeLayer(item.Model, a => a.Visible = !a.Visible);
                SetStatus($"Camada \"{item.Model.DisplayName}\" {(item.Model.Visible ? "visível" : "oculta")}.");
            }

        }

        private void LayerLock_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is LayerItem item)
            {
                Surface.ChangeLayer(item.Model, a => a.Locked = !a.Locked);
                SetStatus($"Camada \"{item.Model.DisplayName}\" {(item.Model.Locked ? "travada" : "destravada")}.");
            }
        }

        private void ToggleAllLayers_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null || _doc.Annotations.Count == 0) return;
            bool show = _doc.Annotations.Any(a => !a.Visible);
            _history.Push(TakeSnapshot());
            foreach (var a in _doc.Annotations) a.Visible = show;
            Surface.Refresh();
            OnDocumentChanged();
        }

        private Annotation? CurrentLayer => Surface.Selected;

        private void LayerUp_Click(object sender, RoutedEventArgs e)
        {
            Surface.MoveSelection(+1);
        }

        private void LayerDown_Click(object sender, RoutedEventArgs e)
        {
            Surface.MoveSelection(-1);
        }

        private void LayerDuplicate_Click(object sender, RoutedEventArgs e)
        {
            Surface.DuplicateSelection();
        }

        private void LayerDelete_Click(object sender, RoutedEventArgs e)
        {
            Surface.DeleteSelected();
        }

        private void LayerRename_Click(object sender, RoutedEventArgs e) => RenameCurrentLayer();

        private void Layers_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Ignore double-clicks on the eye/lock buttons.
            if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(d) != null) return;
            RenameCurrentLayer();
        }

        private void RenameCurrentLayer()
        {
            if (CurrentLayer is not Annotation a) return;
            var name = TextInputDialog.Show(this, "Renomear camada", a.DisplayName);
            if (name == null) return;
            name = name.Trim();
            Surface.ChangeLayer(a, x => x.Name = string.IsNullOrEmpty(name) || name == x.DefaultName ? null : name);
        }

        private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        /// <summary>Applies a toolbar change to the selected annotation (consecutive slider moves = one undo step).</summary>
        private void EditSelected(Action<Annotation> change)
        {
            var sel = Surface.Selected;
            if (sel == null || _suppressPropertyEdit) return;
            bool sameGesture = ReferenceEquals(sel, _lastPropertyEditTarget) && (DateTime.Now - _lastPropertyEditTime).TotalMilliseconds < 800;
            if (!sameGesture) _history.Push(TakeSnapshot());
            _lastPropertyEditTarget = sel;
            _lastPropertyEditTime = DateTime.Now;
            foreach (var a in Surface.SelectedItems.ToList()) change(a);
            Surface.Refresh();
            OnDocumentChanged();
        }

        private void BuildPalette()
        {
            foreach (var hex in PaletteColors)
            {
                var color = ColorUtil.Parse(hex);
                var dot = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(color),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                };
                var swatch = new Border
                {
                    Width = 26,
                    Height = 26,
                    Margin = new Thickness(1, 0, 1, 0),
                    CornerRadius = new CornerRadius(13),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    Cursor = Cursors.Hand,
                    Tag = hex,
                    ToolTip = hex,
                    Child = dot,
                };
                swatch.MouseLeftButtonUp += (_, _) => PickColor(hex);
                _swatches.Add(swatch);
                PalettePanel.Children.Add(swatch);
            }

            var custom = new Button
            {
                Content = "+",
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 15,
                ToolTip = "Outra cor…",
            };
            custom.Click += (_, _) =>
            {
                using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = ToDrawing(Surface.CurrentColor) };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var c = dlg.Color;
                    PickColor(ColorUtil.ToHex(Color.FromArgb(255, c.R, c.G, c.B)));
                }
            };
            PalettePanel.Children.Add(custom);
            HighlightSwatch(PaletteColors[0]);
        }

        private static System.Drawing.Color ToDrawing(Color c) => System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);

        private void PickColor(string hex)
        {
            Surface.CurrentColor = ColorUtil.Parse(hex);
            HighlightSwatch(hex);
            EditSelected(a => a.ColorHex = hex);
        }

        private void HighlightSwatch(string hex)
        {
            foreach (var s in _swatches)
            {
                bool on = string.Equals((string)s.Tag, hex, StringComparison.OrdinalIgnoreCase);
                s.BorderBrush = on ? (Brush)FindResource("Accent") : Brushes.Transparent;
            }
        }

        // ---- zoom ----------------------------------------------------------------

        private void SetZoom(double zoom)
        {
            _zoom = Math.Max(0.05, Math.Min(8.0, zoom));
            ZoomTransform.ScaleX = _zoom;
            ZoomTransform.ScaleY = _zoom;
            ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
            Surface.Zoom = _zoom;
            Surface.Refresh();
            if (_inlineBox != null) _inlineBox.BorderThickness = new Thickness(1.5 / _zoom);
            RenderOptions.SetBitmapScalingMode(Surface, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        }

        private void ZoomToFit()
        {
            if (_doc == null) return;
            double availW = Math.Max(50, Scroller.ActualWidth - 2 * CanvasHost.Margin.Left - 20);
            double availH = Math.Max(50, Scroller.ActualHeight - 2 * CanvasHost.Margin.Top - 20);
            double fit = Math.Min(availW / _doc.Image.PixelWidth, availH / _doc.Image.PixelHeight);
            SetZoom(Math.Min(1.0, fit));
        }

        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
                e.Handled = true;
            }
        }

        // ---- toolbar handlers ----------------------------------------------------

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string name && Enum.TryParse(name, out EditorTool tool))
            {
                Surface.Tool = tool;
                ToolHint.Text = ToolHints.TryGetValue(tool, out var hint) ? hint : "";
                UpdateUi();
            }
        }

        private void SelectTool(EditorTool tool)
        {
            var rb = ToolPanel.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tool.ToString());
            if (rb != null) rb.IsChecked = true;
        }

        private void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ThicknessText == null || Surface == null) return; // fired during InitializeComponent
            ThicknessText.Text = ((int)e.NewValue).ToString();
            Surface.CurrentThickness = e.NewValue;
            EditSelected(a =>
            {
                a.Thickness = e.NewValue;
                if (a is PixelateAnnotation px) px.BlockSize = Math.Max(6, e.NewValue * 3);
                if (a is StepAnnotation st) st.Radius = 12 + e.NewValue * 1.5;
            });
        }

        private void Font_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (FontText == null || Surface == null) return;
            FontText.Text = ((int)e.NewValue).ToString();
            Surface.CurrentFontSize = e.NewValue;
            if (Surface.Selected is TextAnnotation)
                EditSelected(a => { if (a is TextAnnotation ta) ta.FontSize = e.NewValue; });
        }

        private void Capture_Click(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke(this, EventArgs.Empty);

        private void CaptureFull_Click(object sender, RoutedEventArgs e) => FullScreenCaptureRequested?.Invoke(this, EventArgs.Empty);

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Projetos e imagens|*.smk;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Projeto ShotMark (*.smk)|*.smk|Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff",
                InitialDirectory = Directory.Exists(Library.Folder) ? Library.Folder : "",
            };
            if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            _dirty = true;
            SaveCurrent(showStatus: true);
        }

        private void Export_Click(object sender, RoutedEventArgs e) => Export();

        private void Export()
        {
            var img = Flattened();
            if (img == null || _doc == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp|GIF (*.gif)|*.gif|TIFF (*.tif)|*.tif|PDF (*.pdf)|*.pdf",
                FilterIndex = AppSettings.Current.ExportFormat == "jpg" ? 2 : AppSettings.Current.ExportFormat == "bmp" ? 3 : 1,
                FileName = _doc.DisplayName,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                if (string.Equals(Path.GetExtension(dlg.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                    PdfWriter.WriteImage(img, dlg.FileName);
                else
                    ImageUtil.SaveImage(img, dlg.FileName);
                SetStatus($"Exportado para {dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Falha ao exportar.\n\n{ex.Message}", "ShotMark", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

        private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

        private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

        private void Delete_Click(object sender, RoutedEventArgs e) => Surface.DeleteSelected();

        private async void Ocr_Click(object sender, RoutedEventArgs e)
        {
            if (_doc == null) return;
            SetStatus("Reconhecendo texto…");
            Cursor = Cursors.Wait;
            try
            {
                string text = await OcrService.RecognizeAsync(_doc.Image);
                Cursor = null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    SetStatus("Nenhum texto encontrado.");
                    return;
                }
                Clipboard.SetText(text);
                if (_doc.FilePath != null) LibraryIndex.Set(_doc.FilePath, text);
                SetStatus("Texto reconhecido copiado para a área de transferência.");
                TextInputDialog.Show(this, "Texto reconhecido", text, readOnly: true);
            }
            catch (Exception ex)
            {
                SetStatus("OCR falhou: " + ex.Message);
                MessageBox.Show(this, ex.Message, "OCR", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                Cursor = null;
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
        private void ZoomFit_Click(object sender, RoutedEventArgs e) => ZoomToFit();
        private void Zoom100_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

        // ---- library -------------------------------------------------------------

        private void RefreshLibrary()
        {
            try
            {
                _libraryAll = Library.List(300);
                ApplyLibraryFilter();
            }
            catch (Exception ex)
            {
                SetStatus("Não foi possível ler a biblioteca: " + ex.Message);
            }
        }

        private LibraryItem? SelectedLibraryItem => LibraryList.SelectedItem as LibraryItem;

        private void ApplyLibraryFilter()
        {
            string q = (LibrarySearch?.Text ?? "").Trim();
            LibrarySearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var items = q.Length == 0 ? _libraryAll : _libraryAll.Where(i => i.Matches(q)).ToList();
            LibraryList.ItemsSource = items;
            if (_doc?.FilePath != null)
            {
                LibraryList.SelectedItem = items.FirstOrDefault(i =>
                    string.Equals(i.Path, _doc.FilePath, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (LibraryList == null) return;
            ApplyLibraryFilter();
        }

        private void Library_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedLibraryItem is LibraryItem item) OpenPath(item.Path);
        }

        private void Library_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && SelectedLibraryItem is LibraryItem item)
            {
                OpenPath(item.Path);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                LibraryDelete_Click(sender, e);
                e.Handled = true;
            }
        }

        private void LibraryOpen_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedLibraryItem is LibraryItem item) OpenPath(item.Path);
        }

        private void LibraryReveal_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedLibraryItem is LibraryItem item)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }

        private void LibraryDelete_Click(object sender, RoutedEventArgs e)
        {
            var items = LibraryList.SelectedItems.OfType<LibraryItem>().ToList();
            if (items.Count == 0) return;
            string what = items.Count == 1 ? $"\"{Path.GetFileName(items[0].Path)}\"" : $"{items.Count} capturas";
            if (MessageBox.Show(this, $"Excluir {what} permanentemente?", "ShotMark",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int failed = 0;
            foreach (var item in items)
            {
                try
                {
                    bool isOpen = _doc?.FilePath != null && string.Equals(_doc.FilePath, item.Path, StringComparison.OrdinalIgnoreCase);
                    if (isOpen)
                    {
                        CloseInlineEditor(commit: false);
                        _autosave.Stop();
                        _dirty = false;
                        _doc = null;
                        _history.Clear();
                        Surface.SetDocument(null);
                    }
                    File.Delete(item.Path);
                    LibraryIndex.Remove(item.Path);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Error("Excluir captura", ex);
                }
            }
            RefreshLibrary();
            RebuildLayers();
            UpdateUi();
            SetStatus(failed == 0 ? $"{items.Count} captura(s) excluída(s)." : $"{failed} arquivo(s) não puderam ser excluídos.");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(Library.Folder);
            Process.Start(new ProcessStartInfo(Library.Folder) { UseShellExecute = true });
        }

        // ---- keyboard / drag & drop / closing ------------------------------------

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            var mods = Keyboard.Modifiers;
            bool ctrl = mods.HasFlag(ModifierKeys.Control);
            bool shift = mods.HasFlag(ModifierKeys.Shift);

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.Z when shift: Redo(); break;
                    case Key.Z: Undo(); break;
                    case Key.Y: Redo(); break;
                    case Key.C when shift: CopyToClipboard(); break;
                    case Key.C: CopySmart(); break;
                    case Key.V: PasteSmart(); break;
                    case Key.A:
                        if (Surface.Tool != EditorTool.Select) SelectTool(EditorTool.Select);
                        Surface.SelectAll();
                        break;
                    case Key.S: Save_Click(this, e); break;
                    case Key.O: Open_Click(this, e); break;
                    case Key.E: Export(); break;
                    case Key.P: PrintImage(); break;
                    case Key.T: PinToScreen(); break;
                    case Key.OemComma: SettingsRequested?.Invoke(this, EventArgs.Empty); break;
                    case Key.D: Surface.DuplicateSelection(); break;
                    case Key.OemCloseBrackets: Surface.MoveSelection(+1); break;
                    case Key.OemOpenBrackets: Surface.MoveSelection(-1); break;
                    case Key.D0: case Key.NumPad0: ZoomToFit(); break;
                    case Key.D1: case Key.NumPad1: SetZoom(1.0); break;
                    case Key.OemPlus: case Key.Add: SetZoom(_zoom * 1.25); break;
                    case Key.OemMinus: case Key.Subtract: SetZoom(_zoom / 1.25); break;
                    default: return;
                }
                e.Handled = true;
                return;
            }

            if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return;

            if (e.Key == Key.F2 && Surface.SelectedItems.Count == 1)
            {
                RenameCurrentLayer();
                e.Handled = true;
                return;
            }

            if (Surface.Selected != null)
            {
                double step = shift ? 10 : 1;
                switch (e.Key)
                {
                    case Key.Delete:
                    case Key.Back: Surface.DeleteSelected(); e.Handled = true; return;
                    case Key.Left: Surface.NudgeSelected(-step, 0); e.Handled = true; return;
                    case Key.Right: Surface.NudgeSelected(step, 0); e.Handled = true; return;
                    case Key.Up: Surface.NudgeSelected(0, -step); e.Handled = true; return;
                    case Key.Down: Surface.NudgeSelected(0, step); e.Handled = true; return;
                    case Key.Escape: Surface.ClearSelection(); e.Handled = true; return;
                }
            }

            if (shift) return;
            EditorTool? tool = e.Key switch
            {
                Key.V => EditorTool.Select,
                Key.A => EditorTool.Arrow,
                Key.L => EditorTool.Line,
                Key.R => EditorTool.Rectangle,
                Key.E => EditorTool.Ellipse,
                Key.P => EditorTool.Pen,
                Key.H => EditorTool.Highlighter,
                Key.T => EditorTool.Text,
                Key.C => EditorTool.Callout,
                Key.N => EditorTool.Step,
                Key.B => EditorTool.Pixelate,
                Key.U => EditorTool.Blur,
                Key.S => EditorTool.Stamp,
                Key.M => EditorTool.Magnify,
                Key.G => EditorTool.Spotlight,
                Key.X => EditorTool.Crop,
                _ => null,
            };
            if (tool.HasValue)
            {
                SelectTool(tool.Value);
                e.Handled = true;
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                OpenPath(files[0]);
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            CloseInlineEditor(commit: true);
            SaveCurrent(showStatus: false);

            var s = AppSettings.Current;
            if (_normalBounds != null) s.EditorBounds = _normalBounds;
            s.EditorMaximized = WindowState == WindowState.Maximized;
            s.Save();
        }

        private int[]? _normalBounds;

        /// <summary>Remembers the window rectangle (physical pixels) while it is neither maximized nor minimized.</summary>
        private void RememberBounds()
        {
            if (!IsLoaded || WindowState != WindowState.Normal) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && ShotMark.Native.NativeMethods.GetWindowRect(hwnd, out var r))
                _normalBounds = new[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
        }
    }
}
