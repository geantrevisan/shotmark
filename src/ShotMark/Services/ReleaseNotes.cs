using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ShotMark.Services
{
    /// <summary>The CHANGELOG.md embedded in the app: "what's new" for the installed version, offline.</summary>
    public static class ReleaseNotes
    {
        public static string Changelog { get; } = Load();

        /// <summary>The changelog without its title and comments, starting at the newest version.</summary>
        public static string Body
        {
            get
            {
                string text = Regex.Replace(Changelog.Replace("\r", ""), "<!--.*?-->", "", RegexOptions.Singleline);
                return string.Join("\n", text.Split('\n').SkipWhile(l => !l.StartsWith("## "))).Trim();
            }
        }

        private static string Load()
        {
            using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("CHANGELOG.md");
            if (stream == null) return "";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
