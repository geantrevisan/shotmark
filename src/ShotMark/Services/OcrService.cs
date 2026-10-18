using System;
using System.Linq;
using System.Threading.Tasks;
using ShotMark.Core;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinImaging = Windows.Graphics.Imaging;
using WpfImaging = System.Windows.Media.Imaging;

namespace ShotMark.Services
{
    /// <summary>Text recognition using the OCR engine built into Windows 10/11 (no extra download).</summary>
    public static class OcrService
    {
        public static async Task<string> RecognizeAsync(WpfImaging.BitmapSource image)
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (engine == null)
                throw new InvalidOperationException(
                    "Nenhum idioma de OCR está instalado no Windows. Instale um pacote de idioma em Configurações > Hora e idioma.");

            // Windows OCR works best on text at least ~20px tall: upscale small captures,
            // and stay under the engine's size limit.
            double scale = 1.0;
            int maxSide = Math.Max(image.PixelWidth, image.PixelHeight);
            if (maxSide < 1200) scale = 2.0;
            uint limit = OcrEngine.MaxImageDimension;
            if (maxSide * scale > limit) scale = limit / (double)maxSide;

            WpfImaging.BitmapSource input = image;
            if (Math.Abs(scale - 1.0) > 0.01)
            {
                var t = new WpfImaging.TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
                t.Freeze();
                input = t;
            }

            byte[] png = ImageUtil.EncodePng(input);

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var decoder = await WinImaging.BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);

            return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
        }
    }
}
