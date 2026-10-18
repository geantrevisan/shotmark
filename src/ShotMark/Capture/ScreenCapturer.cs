using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotMark.Native;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;
using WinForms = System.Windows.Forms;

namespace ShotMark.Capture
{
    /// <summary>Grabs the screen and lists the rectangles the user can snap to.</summary>
    public static class ScreenCapturer
    {
        /// <summary>The bounding box of all monitors, in physical pixels.</summary>
        public static Drawing.Rectangle VirtualScreen => new Drawing.Rectangle(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

        public static BitmapSource CaptureVirtualScreen(out Drawing.Rectangle bounds, bool includeCursor = false)
        {
            bounds = VirtualScreen;
            return CaptureRectangle(bounds, includeCursor);
        }

        public static BitmapSource CaptureRectangle(Drawing.Rectangle bounds, bool includeCursor = false)
        {
            using var bmp = new Drawing.Bitmap(bounds.Width, bounds.Height, DrawingImaging.PixelFormat.Format32bppRgb);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
                if (includeCursor) DrawCursor(g, bounds);
            }
            return ToBitmapSource(bmp);
        }

        internal static void DrawCursor(Drawing.Graphics g, Drawing.Rectangle bounds)
        {
            var ci = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
            if (!NativeMethods.GetCursorInfo(ref ci) || (ci.flags & NativeMethods.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;

            int hotX = 0, hotY = 0;
            if (NativeMethods.GetIconInfo(ci.hCursor, out var info))
            {
                hotX = info.xHotspot;
                hotY = info.yHotspot;
                if (info.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmMask);
                if (info.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(info.hbmColor);
            }

            IntPtr hdc = g.GetHdc();
            try
            {
                NativeMethods.DrawIconEx(hdc, ci.ptScreenPos.X - hotX - bounds.X, ci.ptScreenPos.Y - hotY - bounds.Y,
                    ci.hCursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        private static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
        {
            var rect = new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppRgb);
            try
            {
                var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null,
                    data.Scan0, data.Stride * bmp.Height, data.Stride);
                src.Freeze();
                return src;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>
        /// Visible top-level windows (top of the z-order first) followed by the monitors,
        /// as rectangles relative to the virtual screen's top-left corner.
        /// </summary>
        public static List<SnapRegion> GetSnapRegions(Drawing.Rectangle virtualScreen)
        {
            var list = new List<SnapRegion>();
            var log = DiagnosticsEnabled ? new StringBuilder() : null;
            IntPtr shell = NativeMethods.GetShellWindow();
            var monitors = new List<Drawing.Rectangle>();
            foreach (var screen in WinForms.Screen.AllScreens) monitors.Add(screen.Bounds);

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (hWnd == shell || !NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;

                // Skip windows that are hidden by DWM (e.g. suspended UWP apps on other desktops).
                if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                // Extended frame bounds exclude the invisible resize borders of Windows 10/11.
                if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                        out NativeMethods.RECT r, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
                {
                    NativeMethods.GetWindowRect(hWnd, out r);
                }
                var rect = Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

                long exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                string cls = ClassName(hWnd);
                string? skip = SkipReason(hWnd, exStyle, cls, rect, monitors);
                log?.AppendLine($"{(skip == null ? "USE " : "skip")} 0x{hWnd.ToInt64():X8} ex=0x{exStyle:X8} {rect} class='{cls}' title='{Title(hWnd)}' {skip}");
                if (skip != null) return true;

                rect.Intersect(virtualScreen);
                if (rect.Width < 8 || rect.Height < 8) return true;

                list.Add(new SnapRegion(new Int32Rect(rect.X - virtualScreen.X, rect.Y - virtualScreen.Y, rect.Width, rect.Height), hWnd, "Janela"));
                return true;
            }, IntPtr.Zero);

            foreach (var b in monitors)
            {
                list.Add(new SnapRegion(new Int32Rect(b.X - virtualScreen.X, b.Y - virtualScreen.Y, b.Width, b.Height), IntPtr.Zero, "Tela"));
            }

            if (log != null)
            {
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "regions-debug.txt"), log.ToString()); }
                catch (Exception) { /* diagnostics only */ }
            }
            return list;
        }

        /// <summary>Create a file named "debug.flag" next to ShotMark.exe to log window detection.</summary>
        private static bool DiagnosticsEnabled => File.Exists(Path.Combine(AppContext.BaseDirectory, "debug.flag"));

        /// <summary>
        /// Filters out windows the user cannot actually see or click: click-through overlays,
        /// desktop layers, and invisible full-screen helper windows that would otherwise
        /// "steal" the hover from the real windows beneath them.
        /// </summary>
        private static string? SkipReason(IntPtr hWnd, long exStyle, string cls, Drawing.Rectangle rect, List<Drawing.Rectangle> monitors)
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == (uint)Environment.ProcessId && cls.StartsWith("HwndWrapper", StringComparison.Ordinal) && NativeMethods.GetWindowTextLength(hWnd) == 0)
                return "own helper window";

            if ((exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0) return "click-through";
            if (cls == "Progman" || cls == "WorkerW") return "desktop";
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && NativeMethods.GetWindowTextLength(hWnd) == 0) return "untitled tool window";

            bool coversMonitor = monitors.Exists(m => rect.Contains(m));
            bool helperStyle = (exStyle & (NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW)) != 0;
            if (coversMonitor && (helperStyle || NativeMethods.GetWindowTextLength(hWnd) == 0)) return "full-screen overlay";
            return null;
        }

        private static string ClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string Title(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Visible bounds of a window in physical screen pixels (without the invisible resize border).</summary>
        public static Drawing.Rectangle GetWindowBounds(IntPtr hWnd)
        {
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out NativeMethods.RECT r, Marshal.SizeOf<NativeMethods.RECT>()) != 0)
            {
                NativeMethods.GetWindowRect(hWnd, out r);
            }
            return Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        /// <summary>The monitor containing a virtual-screen-relative pixel.</summary>
        public static Int32Rect MonitorAt(Drawing.Rectangle virtualScreen, int x, int y)
        {
            var screen = WinForms.Screen.FromPoint(new Drawing.Point(x + virtualScreen.X, y + virtualScreen.Y));
            var b = screen.Bounds;
            return new Int32Rect(b.X - virtualScreen.X, b.Y - virtualScreen.Y, b.Width, b.Height);
        }
    }
}
