using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShotMark.Core
{
    public static class ImageUtil
    {
        /// <summary>
        /// Returns an independent, frozen Bgra32 copy at 96 DPI so that 1 WPF unit == 1 image pixel.
        /// </summary>
        public static BitmapSource Normalize(BitmapSource source, bool dropAlpha = false)
        {
            BitmapSource converted = source;
            var target = dropAlpha ? PixelFormats.Bgr32 : PixelFormats.Bgra32;
            if (source.Format != target)
            {
                converted = new FormatConvertedBitmap(source, target, null, 0);
            }

            int w = converted.PixelWidth, h = converted.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);

            var result = BitmapSource.Create(w, h, 96, 96, target, null, pixels, stride);
            result.Freeze();
            return result;
        }

        public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
        {
            var cropped = new CroppedBitmap(source, rect);
            return Normalize(cropped);
        }

        /// <summary>Draws the image and its annotations into a single bitmap.</summary>
        public static BitmapSource Flatten(BitmapSource image, IEnumerable<Annotation> annotations)
        {
            int w = image.PixelWidth, h = image.PixelHeight;
            var ctx = new RenderContext { BaseImage = image };
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(image, new Rect(0, 0, w, h));
                foreach (var a in annotations)
                {
                    if (a.Visible) a.Draw(dc, ctx);
                }
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        // ---- image operations (rotate / flip / resize / frame) -----------------------

        /// <summary>Rotates by +90 (clockwise) or -90 degrees.</summary>
        public static BitmapSource Rotate(BitmapSource image, int degrees)
        {
            var t = new TransformedBitmap(image, new RotateTransform(degrees));
            return Normalize(t);
        }

        public static BitmapSource Flip(BitmapSource image, bool horizontal)
        {
            var t = new TransformedBitmap(image, new ScaleTransform(horizontal ? -1 : 1, horizontal ? 1 : -1));
            return Normalize(t);
        }

        public static BitmapSource Resize(BitmapSource image, int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var t = new TransformedBitmap(image, new ScaleTransform((double)width / image.PixelWidth, (double)height / image.PixelHeight));
            return Normalize(t);
        }

        /// <summary>Surrounds the image with a solid border.</summary>
        public static BitmapSource AddBorder(BitmapSource image, int thickness, Color color)
        {
            int w = image.PixelWidth + thickness * 2, h = image.PixelHeight + thickness * 2;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
                dc.DrawImage(image, new Rect(thickness, thickness, image.PixelWidth, image.PixelHeight));
            }
            return Render(visual, w, h);
        }

        /// <summary>Adds a transparent margin with a soft drop shadow (keep PNG to preserve it).</summary>
        public static BitmapSource AddShadow(BitmapSource image, int margin)
        {
            int w = image.PixelWidth + margin * 2, h = image.PixelHeight + margin * 2;
            var visual = new DrawingVisual
            {
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = margin * 0.9,
                    ShadowDepth = margin * 0.25,
                    Direction = 270,
                    Opacity = 0.45,
                    Color = Colors.Black,
                },
            };
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(image, new Rect(margin, margin, image.PixelWidth, image.PixelHeight));
            }
            return Render(visual, w, h);
        }

        private static BitmapSource Render(Visual visual, int w, int h)
        {
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            return Normalize(rtb);
        }

        public static BitmapSource Thumbnail(BitmapSource image, int maxSize)
        {
            double scale = Math.Min(1.0, (double)maxSize / Math.Max(image.PixelWidth, image.PixelHeight));
            if (scale >= 1.0) return image;
            var t = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            t.Freeze();
            return t;
        }

        public static byte[] EncodePng(BitmapSource image)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        public static BitmapSource Decode(Stream stream)
        {
            // Copy to memory so the source stream can be closed right away.
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            ms.Position = 0;
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return Normalize(decoder.Frames[0]);
        }

        public static BitmapSource LoadFile(string path)
        {
            using var fs = File.OpenRead(path);
            return Decode(fs);
        }

        /// <summary>Saves an image; the encoder is chosen from the file extension.</summary>
        public static void SaveImage(BitmapSource image, string path)
        {
            BitmapEncoder enc = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Max(10, Math.Min(100, AppSettings.Current.JpegQuality)) },
                ".bmp" => new BmpBitmapEncoder(),
                ".gif" => new GifBitmapEncoder(),
                ".tif" or ".tiff" => new TiffBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };

            BitmapSource toWrite = image;
            if (enc is JpegBitmapEncoder || enc is BmpBitmapEncoder)
            {
                // These formats have no alpha channel.
                toWrite = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            }

            enc.Frames.Add(BitmapFrame.Create(toWrite));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }
}
