using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using Drawing = System.Drawing;

namespace ShotMark.Capture
{
    /// <summary>A rectangle the overlay can snap to, relative to the virtual screen.</summary>
    public readonly record struct SnapRegion(Int32Rect Rect, IntPtr Hwnd, string Kind);

    /// <summary>
    /// Finds the UI element under the cursor (button, panel, text box, list…) with UI Automation,
    /// returning the chain from the deepest element up to (but excluding) the window itself.
    /// </summary>
    public static class ElementPicker
    {
        private const int MaxDepth = 24;

        public static List<SnapRegion> Chain(IntPtr hwnd, int screenX, int screenY, Drawing.Rectangle virtualScreen, Int32Rect windowRect)
        {
            var result = new List<SnapRegion>();
            if (hwnd == IntPtr.Zero) return result;

            var pt = new Point(screenX, screenY);
            var winAbs = new Rect(windowRect.X + virtualScreen.X, windowRect.Y + virtualScreen.Y, windowRect.Width, windowRect.Height);

            try
            {
                var cache = new CacheRequest { TreeFilter = Automation.ControlViewCondition };
                cache.Add(AutomationElement.BoundingRectangleProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.LocalizedControlTypeProperty);

                using (cache.Activate())
                {
                    AutomationElement current = AutomationElement.FromHandle(hwnd);
                    for (int depth = 0; depth < MaxDepth; depth++)
                    {
                        var children = current.FindAll(TreeScope.Children, Automation.ControlViewCondition);

                        AutomationElement? best = null;
                        Rect bestRect = Rect.Empty;
                        double bestArea = double.MaxValue;
                        foreach (AutomationElement child in children)
                        {
                            Rect r;
                            try
                            {
                                if ((bool)child.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty)) continue;
                                r = (Rect)child.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                            if (r.IsEmpty || r.Width < 4 || r.Height < 4 || !r.Contains(pt)) continue;
                            double area = r.Width * r.Height;
                            if (area < bestArea)
                            {
                                best = child;
                                bestRect = r;
                                bestArea = area;
                            }
                        }
                        if (best == null) break;

                        bestRect.Intersect(winAbs);
                        if (!bestRect.IsEmpty && bestRect.Width >= 4 && bestRect.Height >= 4)
                        {
                            var rel = new Int32Rect(
                                (int)Math.Round(bestRect.X) - virtualScreen.X,
                                (int)Math.Round(bestRect.Y) - virtualScreen.Y,
                                (int)Math.Round(bestRect.Width),
                                (int)Math.Round(bestRect.Height));
                            bool sameAsWindow = Math.Abs(rel.X - windowRect.X) <= 2 && Math.Abs(rel.Y - windowRect.Y) <= 2
                                                && Math.Abs(rel.Width - windowRect.Width) <= 4 && Math.Abs(rel.Height - windowRect.Height) <= 4;
                            bool sameAsPrevious = result.Count > 0 && result[^1].Rect.Equals(rel);
                            if (!sameAsWindow && !sameAsPrevious)
                                result.Add(new SnapRegion(rel, hwnd, KindOf(best)));
                        }
                        current = best;
                    }
                }
            }
            catch (Exception)
            {
                // UI Automation can fail for elevated, hung or exotic windows: fall back to the window.
            }

            result.Reverse(); // deepest (smallest) first
            return result;
        }

        private static string KindOf(AutomationElement e)
        {
            try
            {
                var s = e.GetCachedPropertyValue(AutomationElement.LocalizedControlTypeProperty) as string;
                if (!string.IsNullOrWhiteSpace(s))
                    return char.ToUpper(s![0], CultureInfo.CurrentCulture) + s.Substring(1);
            }
            catch (Exception)
            {
            }
            return "Elemento";
        }
    }
}
