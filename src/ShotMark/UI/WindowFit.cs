using System;
using System.Windows;
using System.Windows.Interop;
using ShotMark.Native;
using ShotMark.Services;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ShotMark.UI
{
    /// <summary>
    /// Sizes a window to the monitor it opens on (the one under the mouse), so it looks right on a
    /// 1366×768 laptop, with 125–175 % display scaling or on a 4K monitor — instead of a fixed size
    /// that can be larger than the screen or tiny on big monitors. A saved position is reused when it
    /// still fits one of the current monitors.
    /// </summary>
    public static class WindowFit
    {
        public static void SizeToMonitor(Window window, double widthFraction, double heightFraction, double maxWidth, double maxHeight,
            int[]? savedBounds = null, bool savedMaximized = false, bool maximizeOnSmallScreens = false)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SourceInitialized += (_, _) =>
            {
                try
                {
                    Place(window, widthFraction, heightFraction, maxWidth, maxHeight, savedBounds, savedMaximized, maximizeOnSmallScreens);
                }
                catch (Exception ex)
                {
                    Log.Error("Posicionar janela", ex);
                }
            };
        }

        private static void Place(Window w, double widthFraction, double heightFraction, double maxWidth, double maxHeight,
            int[]? savedBounds, bool savedMaximized, bool maximizeOnSmallScreens)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            Drawing.Rectangle target, work;
            bool maximize;

            if (Usable(savedBounds) is Drawing.Rectangle saved)
            {
                target = saved;
                work = WinForms.Screen.FromRectangle(saved).WorkingArea;
                maximize = savedMaximized;
            }
            else
            {
                NativeMethods.GetCursorPos(out var p);
                work = WinForms.Screen.FromPoint(new Drawing.Point(p.X, p.Y)).WorkingArea;
                double s = ScaleOf(work);
                double workW = work.Width / s, workH = work.Height / s;
                double width = Math.Min(workW, Math.Max(Math.Min(w.MinWidth, workW), Math.Min(maxWidth, workW * widthFraction)));
                double height = Math.Min(workH, Math.Max(Math.Min(w.MinHeight, workH), Math.Min(maxHeight, workH * heightFraction)));
                int pw = (int)Math.Round(width * s), ph = (int)Math.Round(height * s);
                target = new Drawing.Rectangle(work.X + (work.Width - pw) / 2, work.Y + (work.Height - ph) / 2, pw, ph);
                maximize = maximizeOnSmallScreens && (workW < 1100 || workH < 700);
            }

            double scale = ScaleOf(work);
            // Never demand more room than the screen has.
            w.MinWidth = Math.Min(w.MinWidth, work.Width / scale);
            w.MinHeight = Math.Min(w.MinHeight, work.Height / scale);

            // Moving onto a monitor with another scaling makes Windows rescale the window, so: move first,
            // then set the size in that monitor's units and apply the exact pixel rectangle again.
            const uint flags = NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, flags);
            w.Left = target.X / scale;
            w.Top = target.Y / scale;
            w.Width = target.Width / scale;
            w.Height = target.Height / scale;
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, flags);
            if (maximize) w.WindowState = WindowState.Maximized;
        }

        /// <summary>The saved rectangle, if it is still (mostly) on a monitor and not larger than it.</summary>
        private static Drawing.Rectangle? Usable(int[]? b)
        {
            if (b == null || b.Length != 4 || b[2] < 300 || b[3] < 200) return null;
            var r = new Drawing.Rectangle(b[0], b[1], b[2], b[3]);
            foreach (var screen in WinForms.Screen.AllScreens)
            {
                var wa = screen.WorkingArea;
                var inter = Drawing.Rectangle.Intersect(wa, r);
                bool mostlyVisible = (long)inter.Width * inter.Height >= (long)r.Width * r.Height * 6 / 10;
                bool fits = r.Width <= wa.Width + 16 && r.Height <= wa.Height + 16;
                if (mostlyVisible && fits) return r;
            }
            return null;
        }

        /// <summary>Display scaling of the monitor holding a rectangle (1.0 = 100 %).</summary>
        private static double ScaleOf(Drawing.Rectangle r)
        {
            try
            {
                var center = new NativeMethods.POINT { X = r.X + r.Width / 2, Y = r.Y + r.Height / 2 };
                var mon = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero && NativeMethods.GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0)
                    return dpi / 96.0;
            }
            catch (Exception)
            {
                // Older systems: assume 100 %.
            }
            return 1.0;
        }
    }
}
