using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ShotMark.Core
{
    public sealed class LibraryItem
    {
        public LibraryItem(string path, DateTime modified, BitmapSource? thumbnail)
        {
            Path = path;
            Modified = modified;
            Thumbnail = thumbnail;
        }

        public string Path { get; }
        public DateTime Modified { get; }
        public BitmapSource? Thumbnail { get; }
        public string Title => Modified.ToString("dd/MM HH:mm:ss");

        public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);

        /// <summary>Case- and accent-insensitive match on name, date and recognised text.</summary>
        public bool Matches(string query)
        {
            string q = Fold(query);
            if (q.Length == 0) return true;
            string hay = Fold(FileName + " " + Modified.ToString("dd/MM/yyyy HH:mm") + " " + (LibraryIndex.Get(Path) ?? ""));
            foreach (var term in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!hay.Contains(term)) return false;
            }
            return true;
        }

        private static string Fold(string s)
        {
            var normalized = (s ?? "").ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>The folder where every capture is auto-saved (Pictures\ShotMark).</summary>
    public static class Library
    {
        public static string Folder
        {
            get
            {
                var f = AppSettings.Current.LibraryFolder;
                return string.IsNullOrWhiteSpace(f) ? AppSettings.DefaultLibraryFolder : f;
            }
        }

        public static string NewPath()
        {
            Directory.CreateDirectory(Folder);
            string baseName = AppSettings.Current.BuildFileName(DateTime.Now);
            string path = System.IO.Path.Combine(Folder, baseName + DocumentStore.Extension);
            int i = 2;
            while (File.Exists(path))
            {
                path = System.IO.Path.Combine(Folder, $"{baseName} ({i++}){DocumentStore.Extension}");
            }
            return path;
        }

        public static List<LibraryItem> List(int max = 150)
        {
            if (!Directory.Exists(Folder)) return new List<LibraryItem>();
            return new DirectoryInfo(Folder)
                .EnumerateFiles("*" + DocumentStore.Extension)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(max)
                .Select(f => new LibraryItem(f.FullName, f.LastWriteTime, DocumentStore.LoadPreview(f.FullName, 160)))
                .ToList();
        }
    }
}
