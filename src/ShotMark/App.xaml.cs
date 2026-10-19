using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShotMark.Capture;
using ShotMark.Core;
using ShotMark.Editor;
using ShotMark.Native;
using ShotMark.Services;
using ShotMark.UI;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ShotMark
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private WinForms.NotifyIcon? _tray;
        private Drawing.Icon? _icon;
        private HotkeyManager? _hotkeys;
        private EditorWindow? _editor;
        private bool _capturing;
        private PrintScreenHook? _printHook;
        private WinForms.ToolStripMenuItem? _printMenuItem;
        private CaptureDocument? _lastQuietCapture;
        private string? _lastRecording;
        private ScreenRecorder? _recorder;
        private FloatingBar? _recordBar;
        private RecordingFrame? _recordFrame;
        private DispatcherTimer? _recordTimer;
        private WinForms.ToolStripMenuItem? _stopRecordingItem;
        private Action? _balloonAction;
        private UpdateWindow? _updateWindow;
        private DispatcherTimer? _updateTimer;

        private static AppSettings Settings => AppSettings.Current;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstance = new Mutex(true, "ShotMark.SingleInstance", out bool isFirst);
            if (!isFirst)
            {
                // Already running: ask the first instance to show its window and quit quietly.
                try
                {
                    using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                    activate.Set();
                }
                catch (Exception)
                {
                    MessageBox.Show("O ShotMark já está em execução (veja o ícone na bandeja do sistema).", "ShotMark");
                }
                Shutdown();
                return;
            }
            ListenForActivation();

            DispatcherUnhandledException += OnUnhandledException;
            IndexLibraryBacklog();
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log.Error("Erro fatal", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.Error("Tarefa em segundo plano", args.Exception);
                args.SetObserved();
            };
            Log.Info($"ShotMark iniciado (versão {typeof(App).Assembly.GetName().Version}).");

            _icon = CreateAppIcon();
            CreateTray();
            _hotkeys = new HotkeyManager();
            // The installer may have created the startup entry ("Iniciar com o Windows"): adopt it
            // instead of deleting it because the setting still has its default value.
            if (!Settings.StartWithWindows && StartupManager.IsRegistered())
            {
                Settings.StartWithWindows = true;
                Settings.Save();
            }
            ApplySettings(showWarnings: true);

            bool trayOnly = Array.Exists(e.Args, a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase)) || Settings.StartInTray;
            if (Array.Exists(e.Args, a => string.Equals(a, "--capture", StringComparison.OrdinalIgnoreCase)))
                StartRegionCapture();
            else if (trayOnly)
                Notify("O ShotMark está na bandeja. Aperte Print Screen para capturar.");
            else
                ShowEditor();

            StartUpdateChecks();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            FinishRecordingBeforeExit(); // also covers Windows shutting down / signing out
            _printHook?.Dispose();
            _hotkeys?.Dispose();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _singleInstance?.Dispose();
            base.OnExit(e);
        }

        // ---- settings ----------------------------------------------------------------

        /// <summary>Re-applies everything that depends on the settings (hotkeys, Print Screen, startup, editor defaults).</summary>
        private void ApplySettings(bool showWarnings)
        {
            SetupPrintScreenHook();
            var failed = RegisterHotkeys();
            StartupManager.Apply(Settings.StartWithWindows);
            if (_printMenuItem != null) _printMenuItem.Checked = Settings.UsePrintScreenKey;
            _editor?.ApplySettings();

            if (showWarnings && failed.Count > 0)
            {
                Notify($"Atalhos em uso por outro programa: {string.Join(", ", failed)}. " +
                       "Troque-os em Configurações › Atalhos ou feche o outro programa de captura.", warning: true);
            }
        }

        private void ShowSettings()
        {
            var owner = _editor != null && _editor.IsVisible ? _editor : null;
            var win = new SettingsWindow { Icon = ToImageSource(_icon) };
            if (owner != null) win.Owner = owner;
            else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (win.ShowDialog() == true && win.Result != null)
            {
                win.Result.SaveAsCurrent();
                ApplySettings(showWarnings: true);
                _editor?.RefreshLibraryPublic();
            }
        }

        // ---- tray & hotkeys --------------------------------------------------------

        private void CreateTray()
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Capturar região / elemento\tPrint Screen", null, (_, _) => StartRegionCapture());
            menu.Items.Add("Capturar janela ativa\tAlt+Print Screen", null, (_, _) => CaptureActiveWindow());
            menu.Items.Add("Capturar tela inteira\tShift+Print Screen", null, (_, _) => StartFullScreenCapture());
            menu.Items.Add("Repetir última região", null, (_, _) => RepeatLastRegion());

            var delayed = new WinForms.ToolStripMenuItem("Captura com atraso");
            delayed.DropDownItems.Add("3 segundos", null, (_, _) => StartRegionCapture(3));
            delayed.DropDownItems.Add("5 segundos", null, (_, _) => StartRegionCapture(5));
            delayed.DropDownItems.Add("10 segundos", null, (_, _) => StartRegionCapture(10));
            menu.Items.Add(delayed);
            menu.Items.Add("Captura com rolagem", null, (_, _) => StartScrollFlow());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Gravar vídeo (MP4)", null, (_, _) => StartRecordingFlow(gif: false));
            menu.Items.Add("Gravar GIF animado", null, (_, _) => StartRecordingFlow(gif: true));
            _stopRecordingItem = new WinForms.ToolStripMenuItem("Parar gravação", null, (_, _) => StopRecording()) { Enabled = false };
            menu.Items.Add(_stopRecordingItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            _printMenuItem = new WinForms.ToolStripMenuItem("Usar a tecla Print Screen") { CheckOnClick = true, Checked = Settings.UsePrintScreenKey };
            _printMenuItem.CheckedChanged += (_, _) =>
            {
                if (Settings.UsePrintScreenKey == _printMenuItem.Checked) return;
                Settings.UsePrintScreenKey = _printMenuItem.Checked;
                Settings.Save();
                SetupPrintScreenHook();
            };
            menu.Items.Add(_printMenuItem);
            var actionBarItem = new WinForms.ToolStripMenuItem("Mostrar menu após selecionar") { CheckOnClick = true, Checked = Settings.ShowActionBar };
            actionBarItem.CheckedChanged += (_, _) =>
            {
                Settings.ShowActionBar = actionBarItem.Checked;
                if (!Settings.ShowActionBar) Settings.OpenEditorAfterCapture = true;
                Settings.Save();
            };
            menu.Opening += (_, _) => actionBarItem.Checked = Settings.ShowActionBar;
            menu.Items.Add(actionBarItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Abrir editor", null, (_, _) => ShowEditor());
            menu.Items.Add("Abrir pasta de capturas", null, (_, _) => OpenLibraryFolder());
            menu.Items.Add("Configurações…", null, (_, _) => ShowSettings());
            menu.Items.Add("Verificar atualizações…", null, (_, _) => CheckForUpdatesInteractive());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Sair", null, (_, _) => ExitApp());

            _tray = new WinForms.NotifyIcon
            {
                Icon = _icon,
                Text = "ShotMark — Print Screen para capturar",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == WinForms.MouseButtons.Left) StartRegionCapture();
            };
            _tray.BalloonTipClicked += (_, _) =>
            {
                if (_balloonAction != null)
                {
                    var act = _balloonAction;
                    _balloonAction = null;
                    act();
                    return;
                }
                if (_lastRecording != null && File.Exists(_lastRecording))
                {
                    var file = _lastRecording;
                    _lastRecording = null;
                    try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Error("Abrir gravação", ex); }
                    return;
                }
                if (_lastQuietCapture != null)
                {
                    var doc = _lastQuietCapture;
                    _lastQuietCapture = null;
                    ShowEditor().OpenDocument(doc);
                }
            };
        }

        private List<string> RegisterHotkeys()
        {
            var failed = new List<string>();
            if (_hotkeys == null) return failed;
            _hotkeys.UnregisterAll();

            void Add(HotkeySetting h, Action action)
            {
                if (h == null || h.IsEmpty) return;
                if (!_hotkeys.Register(h.Modifiers, h.Key, action)) failed.Add(h.ToString());
            }

            Add(Settings.RegionHotkey, () => StartRegionCapture());
            Add(Settings.WindowHotkey, () => CaptureActiveWindow());
            Add(Settings.FullScreenHotkey, StartFullScreenCapture);
            Add(Settings.RepeatHotkey, RepeatLastRegion);
            Add(Settings.DelayedHotkey, () => StartRegionCapture(Settings.DefaultDelaySeconds > 0 ? Settings.DefaultDelaySeconds : 3));
            return failed;
        }

        /// <summary>Takes over Print Screen in every app (even if Windows or another tool registered it).</summary>
        private void SetupPrintScreenHook()
        {
            _printHook?.Dispose();
            _printHook = null;
            if (!Settings.UsePrintScreenKey) return;
            try
            {
                _printHook = new PrintScreenHook(Dispatcher, action =>
                {
                    if (_recorder != null)
                    {
                        StopRecording(); // Print Screen during a recording = stop
                        return;
                    }
                    switch (action)
                    {
                        case PrintScreenAction.ActiveWindow: CaptureActiveWindow(); break;
                        case PrintScreenAction.FullScreen: StartFullScreenCapture(); break;
                        default: StartRegionCapture(); break;
                    }
                });
            }
            catch (Exception ex)
            {
                Notify("Não foi possível usar a tecla Print Screen: " + ex.Message, warning: true);
            }
        }

        private void Notify(string text, bool warning = false, Action? onClick = null)
        {
            _balloonAction = onClick;
            if (!Settings.ShowNotifications && !warning && onClick == null) return;
            _tray?.ShowBalloonTip(warning ? 8000 : 4000, "ShotMark", text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
        }

        // ---- capture flow ----------------------------------------------------------

        private async Task<bool> BeginCapture(int delaySeconds)
        {
            if (_capturing) return false;
            _capturing = true;
            if (delaySeconds > 0) await Countdown.RunAsync(delaySeconds);
            return true;
        }

        /// <summary>Captures the foreground window right away (Alt+Print Screen).</summary>
        private async void CaptureActiveWindow(int delaySeconds = 0)
        {
            if (!await BeginCapture(delaySeconds)) return;
            try
            {
                // From the tray icon/menu the foreground window is the taskbar: use the app window behind it.
                IntPtr hwnd = ScreenCapturer.ResolveTargetWindow(NativeMethods.GetForegroundWindow());
                await Task.Delay(120);
                var shot = ScreenCapturer.CaptureVirtualScreen(out var bounds, Settings.IncludeCursor);
                var win = ScreenCapturer.GetWindowBounds(hwnd);
                win.Intersect(bounds);
                if (hwnd == IntPtr.Zero || win.Width < 2 || win.Height < 2)
                {
                    OnCaptured(ImageUtil.Normalize(shot));
                    return;
                }
                var r = ClampToImage(new Int32Rect(win.X - bounds.X, win.Y - bounds.Y, win.Width, win.Height), shot);
                OnCaptured(ImageUtil.Crop(shot, r));
            }
            catch (Exception ex)
            {
                ShowError("Falha na captura", ex);
            }
            finally
            {
                _capturing = false;
            }
        }

        private async void StartRegionCapture(int delaySeconds = 0)
        {
            if (!await BeginCapture(delaySeconds)) return;
            try
            {
                // Give menus/tooltips a moment to disappear before grabbing the screen.
                if (delaySeconds == 0) await Task.Delay(180);
                var shot = ScreenCapturer.CaptureVirtualScreen(out var bounds, Settings.IncludeCursor);
                var regions = ScreenCapturer.GetSnapRegions(bounds);

                var overlay = new CaptureOverlay(shot, bounds, regions, actionBar: Settings.ShowActionBar);
                overlay.ShowDialog();

                if (overlay.Result is Int32Rect r && r.Width > 1 && r.Height > 1)
                {
                    r = ClampToImage(r, shot);
                    Settings.LastRegion = new[] { r.X + bounds.X, r.Y + bounds.Y, r.Width, r.Height };
                    Settings.Save();
                    var area = new Drawing.Rectangle(r.X + bounds.X, r.Y + bounds.Y, r.Width, r.Height);
                    switch (overlay.Action)
                    {
                        case CaptureAction.Copy:
                            OnCaptured(ImageUtil.Crop(shot, r), openEditor: false, forceCopy: true);
                            break;
                        case CaptureAction.Save:
                            SaveCaptureAs(ImageUtil.Crop(shot, r));
                            break;
                        case CaptureAction.Pin:
                        {
                            var img = ImageUtil.Crop(shot, r);
                            new PinWindow(img, i => OnCaptured(i, openEditor: true)).Show();
                            break;
                        }
                        case CaptureAction.RecordMp4:
                        case CaptureAction.RecordGif:
                        {
                            bool gif = overlay.Action == CaptureAction.RecordGif;
                            _ = Dispatcher.BeginInvoke(new Action(() => StartRecording(area, gif, minimizeEditor: false)));
                            break;
                        }
                        case CaptureAction.Scroll:
                            _ = Dispatcher.BeginInvoke(new Action(() => RunScrollCapture(area)));
                            break;
                        default:
                            OnCaptured(ImageUtil.Crop(shot, r));
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError("Falha na captura", ex);
            }
            finally
            {
                _capturing = false;
            }
        }

        private async void RepeatLastRegion()
        {
            var last = Settings.LastRegion;
            if (last == null || last.Length != 4)
            {
                Notify("Ainda não há uma região anterior. Selecione uma agora.");
                StartRegionCapture();
                return;
            }
            if (!await BeginCapture(0)) return;
            try
            {
                await Task.Delay(150);
                var shot = ScreenCapturer.CaptureVirtualScreen(out var bounds, Settings.IncludeCursor);
                var r = ClampToImage(new Int32Rect(last[0] - bounds.X, last[1] - bounds.Y, last[2], last[3]), shot);
                OnCaptured(ImageUtil.Crop(shot, r));
            }
            catch (Exception ex)
            {
                ShowError("Falha na captura", ex);
            }
            finally
            {
                _capturing = false;
            }
        }

        private async void StartFullScreenCapture()
        {
            if (!await BeginCapture(0)) return;
            bool editorWasVisible = _editor?.IsVisible == true && _editor.WindowState != WindowState.Minimized;
            try
            {
                if (editorWasVisible) _editor!.Hide();
                await Task.Delay(250);
                var shot = ScreenCapturer.CaptureVirtualScreen(out _, Settings.IncludeCursor);
                OnCaptured(ImageUtil.Normalize(shot));
            }
            catch (Exception ex)
            {
                ShowError("Falha na captura", ex);
            }
            finally
            {
                _capturing = false;
                // OnCaptured only reopens the editor when the settings say so; never leave it hidden.
                if (editorWasVisible && _editor != null && !_editor.IsVisible) _editor.Show();
            }
        }

        private static Int32Rect ClampToImage(Int32Rect r, BitmapSource img)
        {
            int x = Math.Max(0, Math.Min(img.PixelWidth - 1, r.X)), y = Math.Max(0, Math.Min(img.PixelHeight - 1, r.Y));
            int w = Math.Min(img.PixelWidth - x, r.Width - (x - r.X));
            int h = Math.Min(img.PixelHeight - y, r.Height - (y - r.Y));
            return new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h));
        }

        private const string ActivateEventName = "ShotMark.Activate";
        private EventWaitHandle? _activateEvent;

        /// <summary>Opening ShotMark again (Start menu, desktop icon) brings the running editor to the front.</summary>
        private void ListenForActivation()
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            var thread = new Thread(() =>
            {
                while (_activateEvent.WaitOne())
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var editor = ShowEditor();
                        editor.Activate();
                        NativeMethods.ForceForeground(new WindowInteropHelper(editor).Handle);
                    }));
                }
            })
            { IsBackground = true, Name = "ShotMark activation" };
            thread.Start();
        }

        private void OnCaptured(BitmapSource image, bool? openEditor = null, bool forceCopy = false)
        {
            var doc = new CaptureDocument(image);

            if (Settings.CopyAfterCapture || forceCopy)
            {
                if (!ClipboardUtil.SetImage(image))
                    Notify("Não foi possível copiar para a área de transferência (outro programa está usando).", warning: true);
            }

            // Always keep an editable copy in the library.
            try { DocumentStore.Save(doc, Library.NewPath()); }
            catch (Exception ex) { Log.Error("Salvar na biblioteca", ex); Notify("Não foi possível salvar na biblioteca: " + ex.Message, warning: true); }
            IndexTextInBackground(doc);

            string? exported = null;
            if (Settings.AutoExport)
            {
                try { exported = AutoExport(image); }
                catch (Exception ex) { Notify("Não foi possível salvar a imagem: " + ex.Message, warning: true); }
            }

            if (openEditor ?? Settings.OpenEditorAfterCapture)
            {
                ShowEditor().OpenDocument(doc);
            }
            else
            {
                _lastQuietCapture = doc;
                _lastRecording = null;
                _editor?.RefreshLibraryPublic();
                string what = Settings.CopyAfterCapture || forceCopy ? "Captura copiada" : "Captura salva";
                Notify($"{what} ({image.PixelWidth} × {image.PixelHeight}). Clique aqui para editar." +
                       (exported != null ? $"\nArquivo: {Path.GetFileName(exported)}" : ""));
            }
        }

        private void SaveCaptureAs(BitmapSource img)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp|PDF (*.pdf)|*.pdf",
                FileName = Settings.BuildFileName(DateTime.Now),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                if (string.Equals(Path.GetExtension(dlg.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                    PdfWriter.WriteImage(img, dlg.FileName);
                else
                    ImageUtil.SaveImage(img, dlg.FileName);
                Notify("Imagem salva em " + dlg.FileName);
            }
            catch (Exception ex)
            {
                ShowError("Falha ao salvar", ex);
            }
        }

        // ---- area picking for recording / scrolling ------------------------------------

        private async void PickAreaThen(string prompt, Action<Drawing.Rectangle> then)
        {
            if (!await BeginCapture(0)) return;
            Drawing.Rectangle? picked = null;
            try
            {
                await Task.Delay(180);
                var shot = ScreenCapturer.CaptureVirtualScreen(out var bounds);
                var regions = ScreenCapturer.GetSnapRegions(bounds);
                var overlay = new CaptureOverlay(shot, bounds, regions, actionBar: false, prompt: prompt);
                overlay.ShowDialog();
                if (overlay.Result is Int32Rect r && r.Width > 16 && r.Height > 16)
                {
                    r = ClampToImage(r, shot);
                    picked = new Drawing.Rectangle(r.X + bounds.X, r.Y + bounds.Y, r.Width, r.Height);
                }
            }
            catch (Exception ex)
            {
                ShowError("Falha ao selecionar a área", ex);
            }
            finally
            {
                _capturing = false;
            }
            if (picked is Drawing.Rectangle area) then(area);
        }

        // ---- scrolling capture --------------------------------------------------------

        private void StartScrollFlow() =>
            PickAreaThen("Selecione a área que rola (por exemplo, o conteúdo da página) — Esc cancela", RunScrollCapture);

        private async void RunScrollCapture(Drawing.Rectangle area)
        {
            if (!await BeginCapture(0)) return;
            using var cts = new System.Threading.CancellationTokenSource();
            var bar = new FloatingBar(area, "Capturando rolagem…", showDot: false);
            bar.AddButton("Parar", () => cts.Cancel(), danger: true);
            try
            {
                bar.Show();
                await Task.Delay(250);
                var img = await ScrollCapture.RunAsync(area, cts.Token, new Progress<string>(t => bar.Text = t));
                bar.Close();
                if (img != null) OnCaptured(img);
                else Notify("Não foi possível fazer a captura com rolagem nesta área.", warning: true);
            }
            catch (Exception ex)
            {
                ShowError("Falha na captura com rolagem", ex);
            }
            finally
            {
                if (bar.IsVisible) bar.Close();
                _capturing = false;
            }
        }

        // ---- screen recording ---------------------------------------------------------

        private void StartRecordingFlow(bool gif)
        {
            if (_recorder != null)
            {
                StopRecording();
                return;
            }
            PickAreaThen(gif ? "Selecione a área para gravar o GIF — Esc cancela" : "Selecione a área para gravar o vídeo — Esc cancela",
                area => StartRecording(area, gif));
        }

        private async void StartRecording(Drawing.Rectangle area, bool gif, bool minimizeEditor = true)
        {
            if (_recorder != null) return;
            try
            {
                string folder = Path.Combine(Library.Folder, "Gravações");
                Directory.CreateDirectory(folder);
                string ext = gif ? ".gif" : ".mp4";
                string baseName = Settings.BuildFileName(DateTime.Now).Replace("Captura", "Gravação");
                string path = Path.Combine(folder, baseName + ext);
                int i = 2;
                while (File.Exists(path)) path = Path.Combine(folder, $"{baseName} ({i++}){ext}");

                if (minimizeEditor && _editor?.IsVisible == true) _editor.WindowState = WindowState.Minimized;
                await Countdown.RunAsync(3);

                _recorder = ScreenRecorder.Start(area, Settings.RecordFps, Settings.RecordCursor, gif, path);
                Log.Info($"Gravação iniciada: {path} ({area.Width}x{area.Height})");

                _recordFrame = new RecordingFrame(area);
                _recordFrame.Show();

                _recordBar = new FloatingBar(area, "00:00", showDot: true);
                System.Windows.Controls.Button? pause = null;
                pause = _recordBar.AddButton("Pausar", () =>
                {
                    if (_recorder == null) return;
                    _recorder.TogglePause();
                    pause!.Content = _recorder.IsPaused ? "Continuar" : "Pausar";
                });
                _recordBar.AddButton("Parar", StopRecording, danger: true);
                _recordBar.AddButton("Cancelar", CancelRecording);
                _recordBar.Show();

                bool blink = false;
                _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _recordTimer.Tick += (_, _) =>
                {
                    if (_recorder == null || _recordBar == null) return;
                    var t = _recorder.Elapsed;
                    _recordBar.Text = (gif ? "GIF  " : "") + $"{(int)t.TotalMinutes:00}:{t.Seconds:00}" + (_recorder.IsPaused ? "  (pausado)" : "");
                    blink = !blink;
                    _recordBar.SetDotVisible(_recorder.IsPaused || blink);
                };
                _recordTimer.Start();

                if (_stopRecordingItem != null) _stopRecordingItem.Enabled = true;
            }
            catch (Exception ex)
            {
                CloseRecordingUi();
                _recorder = null;
                ShowError("Não foi possível iniciar a gravação", ex);
            }
        }

        private void CloseRecordingUi()
        {
            _recordTimer?.Stop();
            _recordTimer = null;
            _recordBar?.Close();
            _recordBar = null;
            _recordFrame?.Close();
            _recordFrame = null;
            if (_stopRecordingItem != null) _stopRecordingItem.Enabled = false;
        }

        private async void StopRecording()
        {
            var rec = _recorder;
            if (rec == null) return;
            _recorder = null;
            CloseRecordingUi();
            try
            {
                await rec.StopAsync();
                Log.Info($"Gravação concluída: {rec.OutputPath} (quadros descartados: {rec.DroppedFrames})");
                _lastRecording = rec.OutputPath;
                _lastQuietCapture = null;
                Notify($"Gravação salva: {Path.GetFileName(rec.OutputPath)}. Clique aqui para assistir.");
                _editor?.RefreshLibraryPublic();
            }
            catch (Exception ex)
            {
                ShowError("Falha ao finalizar a gravação", ex);
            }
        }

        private async void CancelRecording()
        {
            var rec = _recorder;
            if (rec == null) return;
            _recorder = null;
            CloseRecordingUi();
            await rec.CancelAsync();
            Notify("Gravação cancelada.");
        }

        /// <summary>
        /// Indexes (OCR) older captures that have no searchable text yet, one at a time,
        /// a few seconds after startup so it never competes with the user.
        /// </summary>
        private static async void IndexLibraryBacklog()
        {
            try
            {
                await Task.Delay(5000);
                if (!Settings.IndexText) return;
                var pending = new List<string>();
                foreach (var f in Directory.Exists(Library.Folder)
                             ? Directory.EnumerateFiles(Library.Folder, "*" + DocumentStore.Extension)
                             : Array.Empty<string>())
                {
                    if (!LibraryIndex.Has(f)) pending.Add(f);
                }
                if (pending.Count == 0) return;
                Log.Info($"Indexando texto de {pending.Count} capturas antigas");
                foreach (var path in pending)
                {
                    if (!Settings.IndexText) return;
                    try
                    {
                        var image = await Task.Run(() => DocumentStore.Load(path).Image);
                        string text = await OcrService.RecognizeAsync(image);
                        LibraryIndex.Set(path, text);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Indexar captura antiga: " + Path.GetFileName(path), ex);
                        LibraryIndex.Set(path, ""); // don't retry a broken file forever
                    }
                    await Task.Delay(300);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Indexar biblioteca", ex);
            }
        }

        /// <summary>Runs OCR after the capture so the library search finds text inside images.</summary>
        private static async void IndexTextInBackground(CaptureDocument doc)
        {
            if (!Settings.IndexText || doc.FilePath == null) return;
            try
            {
                await Task.Delay(400); // let the editor open first
                string text = await OcrService.RecognizeAsync(doc.Image);
                LibraryIndex.Set(doc.FilePath, text);
            }
            catch (Exception ex)
            {
                Log.Error("Indexar texto (OCR)", ex);
            }
        }

        private static string AutoExport(BitmapSource image)
        {
            string folder = string.IsNullOrWhiteSpace(Settings.AutoExportFolder)
                ? Path.Combine(Library.Folder, "Exportadas")
                : Settings.AutoExportFolder;
            Directory.CreateDirectory(folder);
            string ext = "." + (Settings.ExportFormat is "jpg" or "bmp" ? Settings.ExportFormat : "png");
            string baseName = Settings.BuildFileName(DateTime.Now);
            string path = Path.Combine(folder, baseName + ext);
            int i = 2;
            while (File.Exists(path)) path = Path.Combine(folder, $"{baseName} ({i++}){ext}");
            ImageUtil.SaveImage(image, path);
            return path;
        }

        // ---- windows -------------------------------------------------------------

        private EditorWindow ShowEditor()
        {
            if (_editor == null)
            {
                _editor = new EditorWindow { Icon = ToImageSource(_icon) };
                _editor.CaptureRequested += (_, _) => StartRegionCapture();
                _editor.FullScreenCaptureRequested += (_, _) => StartFullScreenCapture();
                _editor.DelayedCaptureRequested += (_, seconds) => StartRegionCapture(seconds);
                _editor.RepeatCaptureRequested += (_, _) => RepeatLastRegion();
                _editor.SettingsRequested += (_, _) => ShowSettings();
                _editor.ScrollCaptureRequested += (_, _) => StartScrollFlow();
                _editor.RecordRequested += (_, gif) => StartRecordingFlow(gif);
                _editor.Closed += (_, _) => _editor = null;
            }

            if (!_editor.IsVisible) _editor.Show();
            if (_editor.WindowState == WindowState.Minimized) _editor.WindowState = WindowState.Normal;
            _editor.Activate();
            // Bring to front even when the capture was started from another app.
            NativeMethods.ForceForeground(new WindowInteropHelper(_editor).Handle);
            return _editor;
        }

        private static void OpenLibraryFolder()
        {
            Directory.CreateDirectory(Library.Folder);
            Process.Start(new ProcessStartInfo(Library.Folder) { UseShellExecute = true });
        }

        private void ExitApp()
        {
            FinishRecordingBeforeExit();
            _editor?.Close();
            Shutdown();
        }

        /// <summary>
        /// Stops an ongoing recording and waits for the file to be finished. The capture and encoder
        /// threads are background threads, so without this they would die with the process and leave
        /// an unplayable MP4 (no index) or no GIF at all.
        /// </summary>
        private void FinishRecordingBeforeExit()
        {
            var rec = _recorder;
            if (rec == null) return;
            _recorder = null;
            CloseRecordingUi();
            try
            {
                // Run on the thread pool: StopAsync must not resume on this (blocked) UI thread.
                if (Task.Run(() => rec.StopAsync()).Wait(TimeSpan.FromSeconds(20)))
                    Log.Info($"Gravação finalizada ao sair: {rec.OutputPath}");
                else
                    Log.Info($"Gravação não terminou a tempo ao sair: {rec.OutputPath}");
            }
            catch (Exception ex)
            {
                Log.Error("Finalizar gravação ao sair", ex);
            }
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Erro inesperado", e.Exception);
            ShowError("Erro inesperado", e.Exception);
            e.Handled = true;
        }

        private static void ShowError(string title, Exception ex)
        {
            Log.Error(title, ex);
            MessageBox.Show($"{title}:\n\n{ex.Message}", "ShotMark", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // ---- updates ---------------------------------------------------------------

        /// <summary>Checks GitHub shortly after startup and then every 6 hours (at most once a day each).</summary>
        private void StartUpdateChecks()
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _updateTimer.Tick += (_, _) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(6);
                CheckForUpdatesInBackground();
            };
            _updateTimer.Start();
        }

        private async void CheckForUpdatesInBackground()
        {
            if (!Settings.AutoCheckUpdates || _updateWindow != null) return;
            if (Settings.LastUpdateCheckUtc is DateTime last && DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;
            try
            {
                var info = await UpdateService.CheckAsync();
                Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                Settings.Save();
                if (info == null || info.VersionText == Settings.SkippedUpdateVersion) return;
                Log.Info($"Nova versão disponível: {info.VersionText}");
                Notify($"ShotMark {info.VersionText} está disponível. Clique aqui para ver as novidades e atualizar.", onClick: () => ShowUpdate(info));
            }
            catch (Exception ex)
            {
                // Offline, proxy, GitHub rate limit…: try again on the next round, silently.
                Log.Info("Verificação de atualização falhou: " + ex.Message);
            }
        }

        /// <summary>Tray menu / Settings › Sobre: always answers, even when there is nothing new.</summary>
        internal async void CheckForUpdatesInteractive()
        {
            if (_updateWindow != null)
            {
                _updateWindow.Activate();
                return;
            }
            try
            {
                var info = await UpdateService.CheckAsync();
                Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                Settings.Save();
                if (info == null)
                {
                    MessageBox.Show($"Você já está usando a versão mais recente ({UpdateService.Format(UpdateService.CurrentVersion)}).",
                        "ShotMark", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                ShowUpdate(info); // a manual check also offers a version that was skipped before
            }
            catch (Exception ex)
            {
                Log.Error("Verificar atualizações", ex);
                MessageBox.Show("Não foi possível verificar atualizações agora.\n\n" + ex.Message, "ShotMark",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowUpdate(UpdateInfo info)
        {
            if (_updateWindow != null)
            {
                _updateWindow.Activate();
                return;
            }
            _updateWindow = new UpdateWindow(info, CanInstallUpdate, ExitApp) { Icon = ToImageSource(_icon) };
            _updateWindow.Closed += (_, _) =>
            {
                if (_updateWindow?.Skipped == true)
                {
                    Settings.SkippedUpdateVersion = info.VersionText;
                    Settings.Save();
                }
                _updateWindow = null;
            };
            _updateWindow.Show();
            _updateWindow.Activate();
        }

        private bool CanInstallUpdate()
        {
            if (_recorder != null || _capturing)
            {
                MessageBox.Show("Termine a gravação ou a captura em andamento antes de atualizar.", "ShotMark",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        // ---- icon ----------------------------------------------------------------

        /// <summary>Draws the app icon at runtime (no binary assets needed).</summary>
        private static Drawing.Icon CreateAppIcon()
        {
            using var bmp = new Drawing.Bitmap(32, 32);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);
                using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 59, 130, 246));
                g.FillEllipse(fill, 1, 1, 30, 30);
                using var pen = new Drawing.Pen(Drawing.Color.White, 2.5f);
                // Viewfinder corners
                g.DrawLines(pen, new[] { new Drawing.PointF(9, 14), new Drawing.PointF(9, 9), new Drawing.PointF(14, 9) });
                g.DrawLines(pen, new[] { new Drawing.PointF(18, 9), new Drawing.PointF(23, 9), new Drawing.PointF(23, 14) });
                g.DrawLines(pen, new[] { new Drawing.PointF(23, 18), new Drawing.PointF(23, 23), new Drawing.PointF(18, 23) });
                g.DrawLines(pen, new[] { new Drawing.PointF(14, 23), new Drawing.PointF(9, 23), new Drawing.PointF(9, 18) });
                using var dot = new Drawing.SolidBrush(Drawing.Color.White);
                g.FillEllipse(dot, 13.5f, 13.5f, 5, 5);
            }
            return Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        private static BitmapSource? ToImageSource(Drawing.Icon? icon)
        {
            if (icon == null) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
    }
}
