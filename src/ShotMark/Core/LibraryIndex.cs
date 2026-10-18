using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ShotMark.Core
{
    /// <summary>
    /// Searchable text of each capture (filled by OCR), stored as index.json in the library folder.
    /// Keys are file names, so the index survives moving the whole folder.
    /// </summary>
    public static class LibraryIndex
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string>? _cache;
        private static string? _cacheFolder;

        private static string IndexPath => Path.Combine(Library.Folder, "index.json");

        private static Dictionary<string, string> Load()
        {
            if (_cache != null && _cacheFolder == Library.Folder) return _cache;
            _cacheFolder = Library.Folder;
            try
            {
                _cache = File.Exists(IndexPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexPath)) ?? new Dictionary<string, string>()
                    : new Dictionary<string, string>();
            }
            catch (Exception)
            {
                _cache = new Dictionary<string, string>();
            }
            _cache = new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase);
            return _cache;
        }

        public static string? Get(string path)
        {
            lock (Gate)
            {
                return Load().TryGetValue(Path.GetFileName(path), out var t) ? t : null;
            }
        }

        public static bool Has(string path) => Get(path) != null;

        public static void Set(string path, string text)
        {
            lock (Gate)
            {
                Load()[Path.GetFileName(path)] = text ?? "";
                Save();
            }
        }

        public static void Remove(string path)
        {
            lock (Gate)
            {
                if (Load().Remove(Path.GetFileName(path))) Save();
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Library.Folder);
                File.WriteAllText(IndexPath, JsonSerializer.Serialize(_cache));
            }
            catch (Exception)
            {
                // The index is only a search aid.
            }
        }
    }
}
