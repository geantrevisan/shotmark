using System;
using System.Windows.Media;

namespace ShotMark.Core
{
    public static class ColorUtil
    {
        public static Color Parse(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Colors.Red;
            try
            {
                var obj = ColorConverter.ConvertFromString(hex);
                return obj is Color c ? c : Colors.Red;
            }
            catch (FormatException)
            {
                return Colors.Red;
            }
        }

        public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        public static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        /// <summary>True when black text reads better than white text on this color.</summary>
        public static bool IsLight(Color c)
        {
            double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return lum > 0.62;
        }
    }
}
