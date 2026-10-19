using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ShotMark.Services
{
    /// <summary>A newer release found on GitHub.</summary>
    public sealed class UpdateInfo
    {
        public UpdateInfo(Version version, string tag, string notes, string pageUrl)
        {
            Version = version;
            Tag = tag;
            Notes = notes;
            PageUrl = pageUrl;
        }

        public Version Version { get; }
        public string Tag { get; }
        public string Notes { get; }
        public string PageUrl { get; }

        /// <summary>The file to download for this installation (setup .exe or portable .zip); null if the release has none.</summary>
        public string? AssetName { get; set; }
        public string? AssetUrl { get; set; }
        public long AssetSize { get; set; }
        public bool IsInstaller { get; set; }

        /// <summary>SHA256SUMS.txt published with the release, if any.</summary>
        public string? ChecksumsUrl { get; set; }

        public string VersionText => UpdateService.Format(Version);
    }

    /// <summary>
    /// Checks the GitHub releases of ShotMark and updates the app in place:
    ///  - installed with the setup program → downloads ShotMark-Setup-x.y.z.exe and runs it silently
    ///    (the installer reopens ShotMark when it finishes);
    ///  - portable copy → downloads the ZIP, and a small script swaps the files once ShotMark has closed.
    /// Downloads are verified against SHA256SUMS.txt when the release includes it.
    /// </summary>
    public static class UpdateService
    {
        public const string Repository = "geantrevisan/shotmark";
        public static string ReleasesPage => $"https://github.com/{Repository}/releases";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ShotMark/{Format(CurrentVersion)} (+https://github.com/{Repository})");
            return http;
        }

        public static Version CurrentVersion
        {
            get
            {
                var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

        /// <summary>True when ShotMark was installed with the setup program (Inno Setup leaves its uninstaller next to the exe).</summary>
        public static bool IsInstalledCopy => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

        /// <summary>Returns the latest release when it is newer than this version, otherwise null.</summary>
        public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null; // no release published yet
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (ParseVersion(tag) is not Version latest || latest <= CurrentVersion) return null;

            var info = new UpdateInfo(
                latest, tag,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? ReleasesPage : ReleasesPage);

            bool installed = IsInstalledCopy;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    string url = a.GetProperty("browser_download_url").GetString() ?? "";
                    long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (!IsGitHubUrl(url)) continue;

                    if (name.StartsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ChecksumsUrl = url;
                    }
                    else if (installed && Regex.IsMatch(name, @"^ShotMark-Setup-.*\.exe$", RegexOptions.IgnoreCase))
                    {
                        info.AssetName = name; info.AssetUrl = url; info.AssetSize = size; info.IsInstaller = true;
                    }
                    else if (!installed && Regex.IsMatch(name, @"^ShotMark-.*-win-x64-portable\.zip$", RegexOptions.IgnoreCase))
                    {
                        info.AssetName = name; info.AssetUrl = url; info.AssetSize = size; info.IsInstaller = false;
                    }
                }
            }
            return info;
        }

        /// <summary>"v1.2.3", "1.2", "v1.2.3-beta" → 1.2.3.</summary>
        public static Version? ParseVersion(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }

        private static bool IsGitHubUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
            (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

        /// <summary>Downloads the update file and verifies size and SHA-256. Returns the local path.</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
        {
            if (info.AssetUrl == null || info.AssetName == null)
                throw new InvalidOperationException("Esta versão não tem arquivo de atualização para esta instalação.");

            string assetName = info.AssetName;
            string dir = Path.Combine(Path.GetTempPath(), "ShotMark", "update", info.VersionText);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Path.GetFileName(assetName));
            string part = path + ".part";

            using (var response = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? info.AssetSize;
                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(part);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            if (info.AssetSize > 0 && new FileInfo(part).Length != info.AssetSize)
            {
                File.Delete(part);
                throw new InvalidDataException("O download veio incompleto. Tente novamente.");
            }

            if (info.ChecksumsUrl != null)
            {
                string sums = await Http.GetStringAsync(info.ChecksumsUrl, ct).ConfigureAwait(false);
                string? expected = sums.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.EndsWith(assetName, StringComparison.OrdinalIgnoreCase))
                    .Select(l => Regex.Match(l, "[0-9a-fA-F]{64}").Value)
                    .FirstOrDefault(h => h.Length == 64);
                if (expected == null)
                {
                    Log.Info($"Atualização: {assetName} não consta em SHA256SUMS; verificação de hash ignorada.");
                }
                else
                {
                    string actual;
                    await using (var fs = File.OpenRead(part))
                        actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(part);
                        throw new InvalidDataException("O arquivo baixado não confere com o checksum publicado. A atualização foi cancelada.");
                    }
                }
            }
            else
            {
                Log.Info("Atualização: a release não publica SHA256SUMS; apenas o tamanho foi verificado.");
            }

            File.Move(part, path, overwrite: true);
            progress?.Report(1.0);
            return path;
        }

        /// <summary>
        /// Starts the installation. The caller must exit ShotMark right after this returns
        /// (the setup closes it anyway; the portable updater waits for it).
        /// </summary>
        public static void StartInstall(UpdateInfo info, string downloadedFile)
        {
            if (info.IsInstaller)
            {
                // Inno Setup reuses the previous folder and install mode (per-user or all users);
                // /SILENT shows only a progress window, and the installer reopens ShotMark at the end.
                Process.Start(new ProcessStartInfo(downloadedFile, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
                {
                    UseShellExecute = true,
                });
                return;
            }

            // Portable: unpack next to the download, then let a script replace the files after we exit.
            string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (!CanWrite(appDir))
                throw new UnauthorizedAccessException($"Sem permissão para gravar em \"{appDir}\". Baixe a nova versão pela página de releases.");

            string staging = Path.Combine(Path.GetDirectoryName(downloadedFile)!, "files");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ZipFile.ExtractToDirectory(downloadedFile, staging);
            if (!File.Exists(Path.Combine(staging, "ShotMark.exe")))
                throw new InvalidDataException("O pacote baixado não contém o ShotMark.exe.");

            int pid = Environment.ProcessId;
            string script = Path.Combine(Path.GetDirectoryName(downloadedFile)!, "aplicar-atualizacao.cmd");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine("set /a tries=0");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul || goto copy");
            sb.AppendLine("set /a tries+=1");
            sb.AppendLine("if %tries% geq 60 goto copy");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("goto wait");
            sb.AppendLine(":copy");
            sb.AppendLine($"xcopy \"{staging}\\*\" \"{appDir}\\\" /e /y /q /r >nul");
            sb.AppendLine($"start \"\" \"{Path.Combine(appDir, "ShotMark.exe")}\"");
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, $".shotmark-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
