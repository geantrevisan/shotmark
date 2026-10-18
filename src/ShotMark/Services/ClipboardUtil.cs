using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ShotMark.Services
{
    /// <summary>
    /// Puts images on the clipboard reliably: retries while another app holds the clipboard and
    /// also offers a PNG copy (Discord, WhatsApp, browsers and Office paste it with full quality).
    /// </summary>
    public static class ClipboardUtil
    {
        public static bool SetImage(BitmapSource image)
        {
            var data = new DataObject();
            data.SetImage(image);
            try
            {
                var png = new MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(image));
                enc.Save(png);
                png.Position = 0;
                data.SetData("PNG", png, false);
            }
            catch (Exception)
            {
                // PNG is a bonus; the bitmap above is enough for most apps.
            }

            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch (Exception ex) when (attempt < 7)
                {
                    _ = ex;
                    Thread.Sleep(60);
                }
                catch (Exception ex)
                {
                    Log.Error("Copiar para a área de transferência", ex);
                }
            }
            return false;
        }
    }
}
