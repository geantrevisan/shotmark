using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace ShotMark.Core
{
    /// <summary>A global hotkey: Win32 modifier flags (MOD_*) plus a virtual-key code. Key 0 = disabled.</summary>
    public sealed class HotkeySetting
    {
        public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

        public HotkeySetting() { }
        public HotkeySetting(uint modifiers, uint key) { Modifiers = modifiers; Key = key; }

        public uint Modifiers { get; set; }
        public uint Key { get; set; }

        [JsonIgnore] public bool IsEmpty => Key == 0;

        public HotkeySetting Clone() => new HotkeySetting(Modifiers, Key);

        public override string ToString()
        {
            if (IsEmpty) return "Nenhum";
            var parts = new List<string>();
            if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & ModShift) != 0) parts.Add("Shift");
            if ((Modifiers & ModWin) != 0) parts.Add("Win");
            var key = KeyInterop.KeyFromVirtualKey((int)Key);
            parts.Add(key switch
            {
                System.Windows.Input.Key.Snapshot => "Print Screen",
                >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)key - (int)System.Windows.Input.Key.D0).ToString(),
                System.Windows.Input.Key.OemPlus => "+",
                System.Windows.Input.Key.OemMinus => "-",
                _ => key.ToString(),
            });
            return string.Join(" + ", parts);
        }
    }

    /// <summary>User preferences, stored in %AppData%\ShotMark\settings.json.</summary>
    public sealed class AppSettings
    {
        // ---- Geral ----
        public bool StartWithWindows { get; set; }
        public bool StartInTray { get; set; }
        public bool OpenEditorAfterCapture { get; set; } = true;
        public bool CopyAfterCapture { get; set; } = true;
        public bool CopyEditsToClipboard { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;

        // ---- Captura ----
        public bool UsePrintScreenKey { get; set; } = true;
        public int DefaultDelaySeconds { get; set; }
        public bool IncludeCursor { get; set; }
        public bool ShowMagnifier { get; set; } = true;
        public bool SmartDetection { get; set; } = true;
        public bool ToolRailExpanded { get; set; } = true;
        public bool ShowActionBar { get; set; } = true;
        public bool IndexText { get; set; } = true;
        public int RecordFps { get; set; } = 20;
        public bool AutoCheckUpdates { get; set; } = true;
        public bool RecordCursor { get; set; } = true;

        // ---- Atalhos ----
        public HotkeySetting RegionHotkey { get; set; } = new HotkeySetting(HotkeySetting.ModControl | HotkeySetting.ModShift, 0x58);      // Ctrl+Shift+X
        public HotkeySetting WindowHotkey { get; set; } = new HotkeySetting(HotkeySetting.ModControl | HotkeySetting.ModShift, 0x57);      // Ctrl+Shift+W
        public HotkeySetting FullScreenHotkey { get; set; } = new HotkeySetting(HotkeySetting.ModControl | HotkeySetting.ModShift, 0x46);  // Ctrl+Shift+F
        public HotkeySetting RepeatHotkey { get; set; } = new HotkeySetting(HotkeySetting.ModControl | HotkeySetting.ModShift, 0x52);      // Ctrl+Shift+R
        public HotkeySetting DelayedHotkey { get; set; } = new HotkeySetting(HotkeySetting.ModControl | HotkeySetting.ModShift, 0x44);     // Ctrl+Shift+D

        // ---- Saída ----
        public string LibraryFolder { get; set; } = DefaultLibraryFolder;
        public bool AutoExport { get; set; }
        public string AutoExportFolder { get; set; } = Path.Combine(DefaultLibraryFolder, "Exportadas");
        public string ExportFormat { get; set; } = "png";
        public int JpegQuality { get; set; } = 92;
        public string FileNamePattern { get; set; } = "Captura {data} {hora}";

        // ---- Editor ----
        public string DefaultColor { get; set; } = "#FFE53935";
        public double DefaultThickness { get; set; } = 4;
        public double DefaultFontSize { get; set; } = 24;

        // ---- Estado ----
        public int[]? LastRegion { get; set; }

        /// <summary>Editor window position/size in physical pixels (x, y, width, height), when not maximized.</summary>
        public int[]? EditorBounds { get; set; }
        public bool EditorMaximized { get; set; }

        public DateTime? LastUpdateCheckUtc { get; set; }

        /// <summary>Version the user chose to skip ("Pular esta versão"); it is not offered again.</summary>
        public string? SkippedUpdateVersion { get; set; }

        /// <summary>Version whose "Novidades" the user has already been told about.</summary>
        public string? LastSeenVersion { get; set; }

        /// <summary>True when there was no settings file yet (fresh install, nothing to announce).</summary>
        [JsonIgnore]
        public static bool IsFirstRun { get; private set; }

        [JsonIgnore]
        public static string DefaultLibraryFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ShotMark");

        [JsonIgnore]
        public static AppSettings Current { get; private set; } = Load();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotMark", "settings.json");

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            }
            catch (Exception)
            {
                // Corrupt settings: start with defaults.
                return new AppSettings();
            }
            IsFirstRun = true;
            return new AppSettings();
        }

        public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json) ?? new AppSettings();

        /// <summary>Makes these settings the current ones and writes them to disk.</summary>
        public void SaveAsCurrent()
        {
            Current = this;
            Save();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch (Exception)
            {
                // Non-critical.
            }
        }

        /// <summary>Builds a file name from the pattern: {data}, {hora}, {ano}, {mes}, {dia}, {n}.</summary>
        public string BuildFileName(DateTime when, int counter = 1)
        {
            string name = (FileNamePattern ?? "Captura {data} {hora}")
                .Replace("{data}", when.ToString("yyyy-MM-dd"))
                .Replace("{hora}", when.ToString("HH.mm.ss"))
                .Replace("{ano}", when.ToString("yyyy"))
                .Replace("{mes}", when.ToString("MM"))
                .Replace("{dia}", when.ToString("dd"))
                .Replace("{n}", counter.ToString());
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Captura" : name.Trim();
        }
    }
}
