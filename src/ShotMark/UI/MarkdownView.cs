using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShotMark.UI
{
    /// <summary>
    /// Renders the small Markdown subset used in release notes (headings, bullet/numbered lists,
    /// **bold**, `code`, [links](https://…)) as a read-only, theme-matching document.
    /// </summary>
    public static class MarkdownView
    {
        private static readonly Regex InlinePattern = new Regex(@"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\((https?://[^)\s]+)\)", RegexOptions.Compiled);

        public static FlowDocumentScrollViewer Create(string markdown) => new FlowDocumentScrollViewer
        {
            Document = Render(markdown),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Focusable = false,
        };

        public static FlowDocument Render(string markdown)
        {
            var doc = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 13.5,
                LineHeight = 20,
                PagePadding = new Thickness(2, 0, 8, 8),
                Foreground = Res("Fg", Brushes.White),
                Background = Brushes.Transparent,
                TextAlignment = TextAlignment.Left,
            };

            List? list = null;
            TextMarkerStyle listStyle = TextMarkerStyle.Disc;
            foreach (var raw in Regex.Replace(markdown ?? "", "<!--.*?-->", "", RegexOptions.Singleline).Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length == 0)
                {
                    list = null;
                    continue;
                }

                var heading = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (heading.Success)
                {
                    list = null;
                    int level = heading.Groups[1].Value.Length;
                    var p = new Paragraph
                    {
                        FontWeight = FontWeights.SemiBold,
                        FontSize = level <= 2 ? 18 : level == 3 ? 14 : 13.5,
                        Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : level <= 2 ? 20 : 12, 0, 4),
                        Foreground = level == 3 ? Res("Accent", Brushes.CornflowerBlue) : Res("Fg", Brushes.White),
                    };
                    AddInlines(p.Inlines, heading.Groups[2].Value);
                    doc.Blocks.Add(p);
                    continue;
                }

                var bullet = Regex.Match(line, @"^\s*(?:([-*+])|(\d+)[.)])\s+(.*)$");
                if (bullet.Success)
                {
                    var style = bullet.Groups[2].Success ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                    if (list == null || style != listStyle)
                    {
                        listStyle = style;
                        list = new List { MarkerStyle = style, Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(20, 0, 0, 0) };
                        doc.Blocks.Add(list);
                    }
                    var item = new Paragraph { Margin = new Thickness(0, 2, 0, 3) };
                    AddInlines(item.Inlines, bullet.Groups[3].Value);
                    list.ListItems.Add(new ListItem(item));
                    continue;
                }

                list = null;
                var para = new Paragraph { Margin = new Thickness(0, 4, 0, 4) };
                AddInlines(para.Inlines, line.Trim());
                doc.Blocks.Add(para);
            }
            return doc;
        }

        private static void AddInlines(InlineCollection target, string text)
        {
            int pos = 0;
            foreach (Match m in InlinePattern.Matches(text))
            {
                if (m.Index > pos) target.Add(new Run(text.Substring(pos, m.Index - pos)));
                if (m.Groups[1].Success)
                {
                    target.Add(new Bold(new Run(m.Groups[1].Value)));
                }
                else if (m.Groups[2].Success)
                {
                    target.Add(new Run(m.Groups[2].Value)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12.5,
                        Background = Res("ControlBg", Brushes.DimGray),
                    });
                }
                else
                {
                    var link = new Hyperlink(new Run(m.Groups[3].Value))
                    {
                        NavigateUri = new Uri(m.Groups[4].Value),
                        Foreground = Res("Accent", Brushes.CornflowerBlue),
                    };
                    link.RequestNavigate += (_, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception) { }
                        e.Handled = true;
                    };
                    target.Add(link);
                }
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) target.Add(new Run(text.Substring(pos)));
        }

        private static Brush Res(string key, Brush fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? fallback;
    }
}
