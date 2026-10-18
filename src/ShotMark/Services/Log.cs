using System;
using System.IO;
using System.Text;

namespace ShotMark.Services
{
    /// <summary>Tiny file logger: %LocalAppData%\ShotMark\logs\shotmark.log (rotates at 1 MB).</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShotMark", "logs");

        public static string FilePath => Path.Combine(Folder, "shotmark.log");

        public static void Info(string message) => Write("INFO ", message);

        public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > 1_000_000)
                    {
                        string old = Path.Combine(Folder, "shotmark.old.log");
                        File.Delete(old);
                        file.MoveTo(old);
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never break the app.
            }
        }
    }
}
