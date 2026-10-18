using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotMark.Native;
using ShotMark.Services;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace ShotMark.Capture
{
    /// <summary>
    /// Scrolling capture: scrolls the window under the area with the mouse wheel, waits for the page
    /// to settle, finds how far the content moved and stitches everything into one tall image.
    ///
    /// Robust against the usual web-page problems:
    ///  - smooth scrolling / lazy images: each step waits until two grabs in a row are identical;
    ///  - fixed or sticky headers/footers: rows that stay in place between frames are excluded from
    ///    matching and stitching (the footer is added once at the end);
    ///  - the browser scrollbar: its thumb moves every step, so a changing strip on the right edge
    ///    is cropped from the final image.
    /// </summary>
    public static class ScrollCapture
    {
        private const int MaxSteps = 120;
        private const int MaxHeight = 32000;
        private const int EdgeScan = 28;       // right-edge columns checked for a scrollbar
        private const int MinMatchRows = 24;

        public static async Task<BitmapSource?> RunAsync(Drawing.Rectangle area, CancellationToken token, IProgress<string>? progress = null)
        {
            int w = area.Width, h = area.Height;
            if (w < 48 || h < 80) return null;

            NativeMethods.GetCursorPos(out var oldCursor);
            NativeMethods.SetCursorPos(area.Right - Math.Min(10, w / 8), area.Y + h / 2);
            await Task.Delay(150);

            var rows = new List<byte[]>();
            byte[] prev = await GrabSettledAsync(area, null);
            ulong[] hp = RowHashes(prev, w, h);
            float[] pp = Profile(prev, w, h);
            AppendRows(rows, prev, w, 0, h);
            int prevEnd = h;          // rows already stitched, in prev's coordinates
            int lastBottomBand = 0;
            byte[] last = prev;
            int expected = h / 3;
            int unchanged = 0;
            var scrollbarVotes = new int[EdgeScan];
            int scrollbarSamples = 0;

            try
            {
                for (int step = 0; step < MaxSteps && !token.IsCancellationRequested; step++)
                {
                    // Scroll less than a full screen so every step overlaps the previous one.
                    int notches = Math.Max(1, Math.Min(5, (int)(h * 0.45 / 125)));
                    NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_WHEEL, 0, 0, -120 * notches, IntPtr.Zero);

                    byte[] next = await GrabSettledAsync(area, prev);
                    ulong[] hn = RowHashes(next, w, h);
                    float[] pn = Profile(next, w, h);

                    // Fixed bands: rows identical at the same position (sticky header / footer).
                    int top = 0;
                    while (top < h && hp[top] == hn[top]) top++;
                    if (top >= h - 2)
                    {
                        if (++unchanged >= 2) break; // end of the page
                        continue;
                    }
                    unchanged = 0;
                    int bottom = 0;
                    while (bottom < h - top && hp[h - 1 - bottom] == hn[h - 1 - bottom]) bottom++;
                    // Also accept nearly identical rows (re-rendered with tiny differences).
                    while (top < h && RowDiff(pp, top, pn, top) <= 0.5f) top++;
                    while (bottom < h - top && RowDiff(pp, h - 1 - bottom, pn, h - 1 - bottom) <= 0.5f) bottom++;
                    // Only treat thin bands as fixed (a big identical area just means little moved).
                    if (top > h / 3) top = 0;
                    if (bottom > h / 3) bottom = 0;

                    int dy = FindShift(pp, pn, h, top, bottom, expected, out double err);
                    if (dy <= 0)
                    {
                        // Something on the page was still changing (carousel, lazy image): look again once.
                        await Task.Delay(500);
                        next = await GrabSettledAsync(area, prev);
                        hn = RowHashes(next, w, h);
                        pn = Profile(next, w, h);
                        dy = FindShift(pp, pn, h, top, bottom, expected, out err);
                    }
                    if (dy <= 0)
                    {
                        Log.Info($"Rolagem: sem correspondência no passo {step} (topo {top}, base {bottom}, semelhança {err:P0})");
                        SaveDebugFrames(prev, next, w, h);
                        break;
                    }

                    VoteScrollbar(prev, next, w, h, dy, top, bottom, scrollbarVotes);
                    scrollbarSamples++;

                    int contentEnd = h - bottom;
                    // Drop anything stitched below the scrolling region (e.g. a fixed footer from the first frame).
                    if (prevEnd > contentEnd)
                    {
                        rows.RemoveRange(rows.Count - (prevEnd - contentEnd), prevEnd - contentEnd);
                        prevEnd = contentEnd;
                    }
                    int from = Math.Max(top, prevEnd - dy);
                    AppendRows(rows, next, w, from, contentEnd);
                    prevEnd = contentEnd;
                    lastBottomBand = bottom;
                    last = next;

                    expected = dy;
                    prev = next;
                    hp = hn;
                    pp = pn;
                    progress?.Report($"Capturando… {rows.Count} px");
                    if (rows.Count >= MaxHeight) break;
                }
            }
            finally
            {
                NativeMethods.SetCursorPos(oldCursor.X, oldCursor.Y);
            }

            // Fixed footer goes once, at the very end.
            if (lastBottomBand > 0) AppendRows(rows, last, w, h - lastBottomBand, h);

            int crop = 0;
            if (scrollbarSamples > 0)
            {
                // Scan from the right edge: columns that mismatch in most steps are the scrollbar.
                for (int i = 0; i < EdgeScan; i++)
                {
                    if (scrollbarVotes[i] * 2 >= scrollbarSamples) crop = i + 1; else break;
                }
            }
            int outW = w - crop;
            int height = Math.Min(rows.Count, MaxHeight);
            int stride = outW * 4;
            var pixels = new byte[stride * height];
            for (int y = 0; y < height; y++) Buffer.BlockCopy(rows[y], 0, pixels, y * stride, stride);
            var result = BitmapSource.Create(outW, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            result.Freeze();
            return result;
        }

        /// <summary>Grabs until two consecutive frames are identical (scroll animation and lazy images done).</summary>
        private static async Task<byte[]> GrabSettledAsync(Drawing.Rectangle area, byte[]? before)
        {
            await Task.Delay(before == null ? 0 : 120);
            byte[] a = Grab(area);
            for (int i = 0; i < 14; i++)
            {
                await Task.Delay(90);
                byte[] b = Grab(area);
                if (SameBytes(a, b)) return b;
                a = b;
            }
            return a;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            return a.AsSpan().SequenceEqual(b);
        }

        private static byte[] Grab(Drawing.Rectangle area)
        {
            using var bmp = new Drawing.Bitmap(area.Width, area.Height, DrawingImaging.PixelFormat.Format32bppRgb);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, Drawing.CopyPixelOperation.SourceCopy);
            }
            int stride = area.Width * 4;
            var bytes = new byte[stride * area.Height];
            var data = bmp.LockBits(new Drawing.Rectangle(0, 0, area.Width, area.Height), DrawingImaging.ImageLockMode.ReadOnly, DrawingImaging.PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < area.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * stride, stride);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            return bytes;
        }

        private static void AppendRows(List<byte[]> rows, byte[] frame, int w, int from, int to)
        {
            int stride = w * 4;
            for (int y = Math.Max(0, from); y < to; y++)
            {
                var row = new byte[stride];
                Buffer.BlockCopy(frame, y * stride, row, 0, stride);
                rows.Add(row);
            }
        }

        /// <summary>Row hashes ignoring the right edge (scrollbar) — B, G, R only.</summary>
        private static ulong[] RowHashes(byte[] frame, int w, int h)
        {
            int stride = w * 4;
            int usable = Math.Max(4, (w - EdgeScan) * 4);
            var hashes = new ulong[h];
            for (int y = 0; y < h; y++)
            {
                ulong hash = 14695981039346656037UL;
                int start = y * stride;
                for (int i = 0; i < usable; i += 4)
                {
                    hash = (hash ^ frame[start + i]) * 1099511628211UL;
                    hash = (hash ^ frame[start + i + 1]) * 1099511628211UL;
                    hash = (hash ^ frame[start + i + 2]) * 1099511628211UL;
                }
                hashes[y] = hash;
            }
            return hashes;
        }

        private const int Bins = 64;

        /// <summary>
        /// Compact per-row signature: the row's brightness averaged into 64 horizontal bins
        /// (right edge excluded). Averaging absorbs the tiny re-rendering differences browsers
        /// make when content lands on a different pixel offset (fractional display scaling).
        /// </summary>
        private static float[] Profile(byte[] frame, int w, int h)
        {
            int usable = Math.Max(Bins, w - EdgeScan);
            var prof = new float[h * Bins];
            int stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                int rowStart = y * stride;
                for (int b = 0; b < Bins; b++)
                {
                    int x0 = b * usable / Bins, x1 = Math.Max(x0 + 1, (b + 1) * usable / Bins);
                    int sum = 0;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = rowStart + x * 4;
                        sum += frame[i] + 2 * frame[i + 1] + frame[i + 2];
                    }
                    prof[y * Bins + b] = sum / (4f * (x1 - x0));
                }
            }
            return prof;
        }

        private const float RowTolerance = 2.5f;   // mean brightness difference per bin for "same row"

        private static float RowDiff(float[] a, int ya, float[] b, int yb)
        {
            int ia = ya * Bins, ib = yb * Bins;
            float sum = 0;
            for (int j = 0; j < Bins; j++)
            {
                float d = a[ia + j] - b[ib + j];
                sum += d < 0 ? -d : d;
            }
            return sum / Bins;
        }

        /// <summary>
        /// Rows the content moved up. Columns (bins) that did not move at all between the two frames
        /// — sticky side panels, fixed menus, blank margins — are left out, and a row counts as the
        /// same when most of its remaining bins match within a small tolerance.
        /// Returns -1 when no shift matches convincingly.
        /// </summary>
        private static int FindShift(float[] pp, float[] pn, int h, int top, int bottom, int expected, out double bestScore)
        {
            bestScore = 0;
            int end = h - bottom;
            int region = end - top;
            if (region < MinMatchRows * 2) return -1;

            // Static columns: bins identical at the same position in (nearly) every row.
            var use = new List<int>(Bins);
            for (int j = 0; j < Bins; j++)
            {
                int still = 0;
                for (int y = top; y < end; y++)
                {
                    float d = pp[y * Bins + j] - pn[y * Bins + j];
                    if (d <= 1f && d >= -1f) still++;
                }
                if (still < region * 0.85) use.Add(j);
            }
            if (use.Count < 4) return -1;
            int[] cols = use.ToArray();
            int needed = Math.Max(2, (int)Math.Ceiling(cols.Length * 0.7));

            int best = -1;
            for (int dy = 1; dy <= region - MinMatchRows; dy++)
            {
                int overlap = region - dy;
                int same = 0;
                for (int k = top; k < top + overlap; k++)
                {
                    int ia = (k + dy) * Bins, ib = k * Bins, ok = 0;
                    for (int c = 0; c < cols.Length; c++)
                    {
                        float d = pp[ia + cols[c]] - pn[ib + cols[c]];
                        if (d <= 3f && d >= -3f) ok++;
                    }
                    if (ok >= needed) same++;
                }
                double score = (double)same / overlap;
                bool better = score > bestScore + 0.01
                              || (score >= bestScore - 0.01 && best > 0 && Math.Abs(dy - expected) < Math.Abs(best - expected) && score > 0.5);
                if (better)
                {
                    if (score > bestScore) bestScore = score;
                    best = dy;
                }
            }
            return bestScore >= 0.6 ? best : -1;
        }

        /// <summary>Saves the two frames that could not be matched, for diagnosis.</summary>
        private static void SaveDebugFrames(byte[] a, byte[] b, int w, int h)
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShotMark", "logs");
                System.IO.Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("HHmmss");
                foreach (var (data, name) in new[] { (a, "anterior"), (b, "seguinte") })
                {
                    var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, data, w * 4);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"rolagem-{stamp}-{name}.png"));
                    enc.Save(fs);
                }
            }
            catch (Exception)
            {
                // diagnostics only
            }
        }

        /// <summary>Counts, per right-edge column, whether it fails to match at the found shift.</summary>
        private static void VoteScrollbar(byte[] prev, byte[] next, int w, int h, int dy, int top, int bottom, int[] votes)
        {
            int stride = w * 4;
            int end = h - bottom;
            for (int i = 0; i < EdgeScan && i < w; i++)
            {
                int x = w - 1 - i;
                int diff = 0, total = 0;
                for (int k = top; k + dy < end; k += 2)
                {
                    int a = (k + dy) * stride + x * 4, b = k * stride + x * 4;
                    total++;
                    if (prev[a] != next[b] || prev[a + 1] != next[b + 1] || prev[a + 2] != next[b + 2]) diff++;
                }
                if (total > 0 && diff * 20 > total) votes[i]++; // > 5% of the column moved differently
            }
        }
    }
}
