using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PWRuLauncher.Core
{
    /// <summary>Ассет релиза GitHub (нам нужны PWRuLauncher.exe и PWRuLauncher.exe.sha256).</summary>
    [DataContract]
    public sealed class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "browser_download_url")] public string Url = "";
        [DataMember(Name = "size")] public long Size;
    }

    /// <summary>Ответ GET /repos/{owner}/{repo}/releases/latest — только нужные поля, остальное игнорируется.</summary>
    [DataContract]
    public sealed class ReleaseInfo
    {
        [DataMember(Name = "tag_name")] public string Tag = "";
        [DataMember(Name = "name")] public string Name = "";
        [DataMember(Name = "html_url")] public string HtmlUrl = "";
        [DataMember(Name = "body")] public string Body = "";
        [DataMember(Name = "prerelease")] public bool Prerelease;
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets = new List<ReleaseAsset>();

        public Version? Version => Updates.ParseVersion(Tag);
        public ReleaseAsset? Exe => Find(Updates.ExeAssetName);
        public ReleaseAsset? ExeSha => Find(Updates.ExeAssetName + ".sha256");

        private ReleaseAsset? Find(string name)
        {
            foreach (var a in Assets)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }
    }

    /// <summary>Клиент релизов GitHub: проверка версии, скачивание, sha256. Сеть только здесь и только по запросу.</summary>
    public static class Updates
    {
        public const string Owner = "RikudoSmpai";
        public const string Repo = "PWRuLauncher";
        public const string ExeAssetName = "PWRuLauncher.exe";
        public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
        public static string ReleasesPageUrl => RepoUrl + "/releases";
        public static string LatestReleaseApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        public static Version CurrentVersion
        {
            get
            {
                var v = typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0);
                return Normalize(v);
            }
        }

        public static Version? ParseVersion(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var t = tag!.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
            var cut = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) t = t.Substring(0, cut);
            return Version.TryParse(t, out var v) ? Normalize(v) : null;
        }

        public static Version Normalize(Version v) => new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

        private static HttpClient MakeClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"PWRuLauncher/{CurrentVersion}");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.Timeout = TimeSpan.FromMinutes(30);
            return c;
        }

        /// <summary>Последний релиз. Бросает при сети/формате — вызывающий решает, как показывать.</summary>
        public static async Task<ReleaseInfo> FetchLatestAsync(string? apiUrl, CancellationToken ct)
        {
            using var client = MakeClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var json = await client.GetStringAsync(apiUrl ?? LatestReleaseApiUrl).ConfigureAwait(false);
            var ser = new DataContractJsonSerializer(typeof(ReleaseInfo));
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var info = (ReleaseInfo?)ser.ReadObject(ms) ?? throw new InvalidDataException("пустой ответ");
            if (info.Assets == null) info.Assets = new List<ReleaseAsset>();
            return info;
        }

        public static async Task<string> FetchTextAsync(string url, CancellationToken ct)
        {
            using var client = MakeClient();
            return await client.GetStringAsync(url).ConfigureAwait(false);
        }

        /// <summary>Скачивание в dest через .part с прогрессом 0..1 (по Content-Length или expectedSize).</summary>
        public static async Task DownloadAsync(string url, string dest, long expectedSize, IProgress<double> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".part";
            using (var client = MakeClient())
            using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? expectedSize;
                using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                var buf = new byte[1 << 18];
                long done = 0; int n;
                var sw = Stopwatch.StartNew();
                while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                    done += n;
                    if (sw.ElapsedMilliseconds > 60) { progress.Report(total > 0 ? (double)done / total : 0); sw.Restart(); }
                }
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
            progress.Report(1);
        }

        public static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var f = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Из текста вида "&lt;hex&gt;  PWRuLauncher.exe" (формат sha256sum) достаёт хэш.</summary>
        public static string? ParseShaText(string text)
        {
            foreach (var tok in text.Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries))
                if (tok.Length == 64 && IsHex(tok)) return tok.ToLowerInvariant();
            return null;
        }

        private static bool IsHex(string s)
        {
            foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }

    /// <summary>Подмена собственного exe: работающий файл нельзя перезаписать, но можно переименовать.</summary>
    public static class SelfUpdate
    {
        public const string OldSuffix = ".old";
        public const string UpdatedFromArg = "--updated-from";

        public static string StagingDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PWRuLauncher", "update");

        /// <summary>current → current.old, staged → current, запуск нового с --updated-from. Вызывающий после этого закрывается.</summary>
        public static void SwapAndStart(string currentExe, string stagedExe, string extraArgs)
        {
            var old = currentExe + OldSuffix;
            if (File.Exists(old)) File.Delete(old);
            File.Move(currentExe, old);
            try
            {
                File.Move(stagedExe, currentExe);
            }
            catch
            {
                File.Move(old, currentExe);   // откат: старый exe обратно на место
                throw;
            }
            var args = $"{UpdatedFromArg} \"{old}\"" + (string.IsNullOrEmpty(extraArgs) ? "" : " " + extraArgs);
            Process.Start(new ProcessStartInfo(currentExe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(currentExe) });
        }

        /// <summary>Новый exe после старта убирает старый файл (тот ещё секунду-другую может быть занят).</summary>
        public static void CleanupOld(string oldPath)
        {
            for (int i = 0; i < 20; i++)
            {
                try { if (!File.Exists(oldPath)) return; File.Delete(oldPath); return; }
                catch { Thread.Sleep(250); }
            }
        }
    }
}
