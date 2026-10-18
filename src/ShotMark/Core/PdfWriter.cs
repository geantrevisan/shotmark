using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShotMark.Core
{
    /// <summary>Writes a one-page PDF containing the image (JPEG-compressed), page sized to the image.</summary>
    public static class PdfWriter
    {
        public static void WriteImage(BitmapSource image, string path, int jpegQuality = 92)
        {
            // JPEG has no alpha: flatten onto white first.
            var rgb = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            var enc = new JpegBitmapEncoder { QualityLevel = jpegQuality };
            enc.Frames.Add(BitmapFrame.Create(rgb));
            byte[] jpeg;
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                jpeg = ms.ToArray();
            }

            int w = image.PixelWidth, h = image.PixelHeight;
            // 96 px per inch → 72 pt per inch.
            string pw = (w * 0.75).ToString("0.##", CultureInfo.InvariantCulture);
            string ph = (h * 0.75).ToString("0.##", CultureInfo.InvariantCulture);
            string content = $"q {pw} 0 0 {ph} 0 0 cm /Im0 Do Q";

            using var fs = File.Create(path);
            var offsets = new List<long>();
            void Ascii(string s)
            {
                var b = Encoding.ASCII.GetBytes(s);
                fs.Write(b, 0, b.Length);
            }
            void BeginObj(int n)
            {
                offsets.Add(fs.Position);
                Ascii($"{n} 0 obj\n");
            }

            Ascii("%PDF-1.4\n%âãÏÓ\n");
            BeginObj(1); Ascii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            BeginObj(2); Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            BeginObj(3); Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw} {ph}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
            BeginObj(4);
            Ascii($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            fs.Write(jpeg, 0, jpeg.Length);
            Ascii("\nendstream\nendobj\n");
            BeginObj(5);
            Ascii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

            long xref = fs.Position;
            Ascii($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) Ascii($"{o:D10} 00000 n \n");
            Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
    }
}
