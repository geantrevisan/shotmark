using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace ShotMark.Capture
{
    /// <summary>
    /// Builds an animated, looping GIF. Each frame is palette-quantised by WPF's GIF encoder;
    /// this class re-assembles the frames with per-frame delays and a NETSCAPE loop block.
    /// </summary>
    public sealed class GifWriter
    {
        private readonly List<(byte[] Palette, int PaletteBits, byte[] ImageData, int Width, int Height, int DelayCs)> _frames = new();
        private int _width;
        private int _height;

        public int FrameCount => _frames.Count;

        /// <param name="delayMs">How long this frame stays on screen.</param>
        public void AddFrame(BitmapSource frame, int delayMs)
        {
            var enc = new GifBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(frame));
            byte[] gif;
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                gif = ms.ToArray();
            }
            var parsed = Parse(gif);
            _width = Math.Max(_width, parsed.Width);
            _height = Math.Max(_height, parsed.Height);
            _frames.Add((parsed.Palette, parsed.PaletteBits, parsed.ImageData, parsed.Width, parsed.Height, Math.Max(2, delayMs / 10)));
        }

        /// <summary>Updates the delay of the last frame (used when the next frame arrives).</summary>
        public void SetLastDelay(int delayMs)
        {
            if (_frames.Count == 0) return;
            var f = _frames[^1];
            _frames[^1] = (f.Palette, f.PaletteBits, f.ImageData, f.Width, f.Height, Math.Max(2, delayMs / 10));
        }

        public void Save(string path)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            w.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            w.Write((ushort)_width);
            w.Write((ushort)_height);
            w.Write((byte)0x70); // no global colour table, 8-bit colour resolution
            w.Write((byte)0);    // background colour index
            w.Write((byte)0);    // pixel aspect ratio

            // NETSCAPE2.0: loop forever.
            w.Write(new byte[] { 0x21, 0xFF, 0x0B });
            w.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            w.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });

            foreach (var f in _frames)
            {
                // Graphic Control Extension: delay, no transparency, dispose = none.
                w.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 });
                w.Write((ushort)f.DelayCs);
                w.Write(new byte[] { 0x00, 0x00 });

                // Image descriptor with a local colour table.
                w.Write((byte)0x2C);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)f.Width);
                w.Write((ushort)f.Height);
                w.Write((byte)(0x80 | (f.PaletteBits - 1)));
                w.Write(f.Palette);
                w.Write(f.ImageData);
            }
            w.Write((byte)0x3B);
        }

        private static (byte[] Palette, int PaletteBits, byte[] ImageData, int Width, int Height) Parse(byte[] gif)
        {
            int pos = 6;
            int width = gif[pos] | (gif[pos + 1] << 8);
            int height = gif[pos + 2] | (gif[pos + 3] << 8);
            byte packed = gif[pos + 4];
            pos += 7;

            byte[] palette = Array.Empty<byte>();
            int bits = 1;
            if ((packed & 0x80) != 0)
            {
                bits = (packed & 0x07) + 1;
                int size = 3 * (1 << bits);
                palette = new byte[size];
                Buffer.BlockCopy(gif, pos, palette, 0, size);
                pos += size;
            }

            while (pos < gif.Length)
            {
                byte b = gif[pos];
                if (b == 0x21)
                {
                    // Extension: skip label + sub-blocks.
                    pos += 2;
                    while (gif[pos] != 0) pos += gif[pos] + 1;
                    pos++;
                }
                else if (b == 0x2C)
                {
                    byte ipacked = gif[pos + 9];
                    pos += 10;
                    if ((ipacked & 0x80) != 0)
                    {
                        bits = (ipacked & 0x07) + 1;
                        int size = 3 * (1 << bits);
                        palette = new byte[size];
                        Buffer.BlockCopy(gif, pos, palette, 0, size);
                        pos += size;
                    }
                    int start = pos;
                    pos++; // LZW minimum code size
                    while (gif[pos] != 0) pos += gif[pos] + 1;
                    pos++;
                    var data = new byte[pos - start];
                    Buffer.BlockCopy(gif, start, data, 0, data.Length);
                    if (palette.Length == 0)
                    {
                        // Should not happen with WPF's encoder; fall back to a grey ramp.
                        bits = 8;
                        palette = new byte[768];
                        for (int i = 0; i < 256; i++) palette[i * 3] = palette[i * 3 + 1] = palette[i * 3 + 2] = (byte)i;
                    }
                    return (palette, bits, data, width, height);
                }
                else
                {
                    break;
                }
            }
            throw new InvalidDataException("Quadro GIF inválido.");
        }
    }
}
