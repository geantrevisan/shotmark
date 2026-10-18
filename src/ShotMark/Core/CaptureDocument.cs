using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace ShotMark.Core
{
    /// <summary>A capture being edited: the base image plus editable annotations.</summary>
    public sealed class CaptureDocument
    {
        public CaptureDocument(BitmapSource image)
        {
            Image = image;
        }

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public BitmapSource Image { get; set; }
        public List<Annotation> Annotations { get; set; } = new List<Annotation>();

        /// <summary>Location of the .smk file, or null if never saved.</summary>
        public string? FilePath { get; set; }

        public string DisplayName => FilePath != null ? Path.GetFileNameWithoutExtension(FilePath) : "Nova captura";
    }

    public static class AnnotationSerializer
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public static string Serialize(List<Annotation> annotations) => JsonSerializer.Serialize(annotations, Options);

        public static List<Annotation> Deserialize(string json) =>
            JsonSerializer.Deserialize<List<Annotation>>(json, Options) ?? new List<Annotation>();

        public static List<Annotation> Clone(List<Annotation> annotations) => Deserialize(Serialize(annotations));
    }

    /// <summary>
    /// Reads and writes the ShotMark project format (.smk): a zip containing the original
    /// image (image.png), the annotations (document.json) and a flattened preview (preview.png).
    /// </summary>
    public static class DocumentStore
    {
        public const string Extension = ".smk";
        private const string ImageEntry = "image.png";
        private const string ManifestEntry = "document.json";
        private const string PreviewEntry = "preview.png";

        internal sealed class Manifest
        {
            public int Version { get; set; } = 1;
            public string Id { get; set; } = "";
            public DateTime CreatedUtc { get; set; }
            public List<Annotation> Annotations { get; set; } = new List<Annotation>();
        }

        public static void Save(CaptureDocument doc, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var manifest = new Manifest
            {
                Id = doc.Id,
                CreatedUtc = doc.CreatedUtc,
                Annotations = doc.Annotations,
            };

            byte[] imageBytes = ImageUtil.EncodePng(doc.Image);
            byte[] previewBytes = ImageUtil.EncodePng(ImageUtil.Thumbnail(ImageUtil.Flatten(doc.Image, doc.Annotations), 360));
            byte[] manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, AnnotationSerializer.Options));

            // Write to a temp file first so a crash never leaves a half-written project.
            string temp = path + ".tmp";
            using (var fs = File.Create(temp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, ImageEntry, imageBytes, CompressionLevel.NoCompression);
                WriteEntry(zip, ManifestEntry, manifestBytes, CompressionLevel.Optimal);
                WriteEntry(zip, PreviewEntry, previewBytes, CompressionLevel.NoCompression);
            }
            File.Move(temp, path, overwrite: true);
            doc.FilePath = path;
        }

        public static CaptureDocument Load(string path)
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            var imageEntry = zip.GetEntry(ImageEntry) ?? throw new InvalidDataException("Arquivo .smk sem imagem.");
            BitmapSource image;
            using (var s = imageEntry.Open()) image = ImageUtil.Decode(s);

            var doc = new CaptureDocument(image) { FilePath = path };

            var manifestEntry = zip.GetEntry(ManifestEntry);
            if (manifestEntry != null)
            {
                using var s = manifestEntry.Open();
                using var reader = new StreamReader(s, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd(), AnnotationSerializer.Options);
                if (manifest != null)
                {
                    if (!string.IsNullOrEmpty(manifest.Id)) doc.Id = manifest.Id;
                    doc.CreatedUtc = manifest.CreatedUtc;
                    doc.Annotations = manifest.Annotations ?? new List<Annotation>();
                }
            }
            return doc;
        }

        public static BitmapSource? LoadPreview(string path, int decodeWidth)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                var entry = zip.GetEntry(PreviewEntry) ?? zip.GetEntry(ImageEntry);
                if (entry == null) return null;

                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, byte[] data, CompressionLevel level)
        {
            var entry = zip.CreateEntry(name, level);
            using var s = entry.Open();
            s.Write(data, 0, data.Length);
        }
    }
}
