using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PWRuLauncher
{
    /// <summary>
    /// Нативные библиотеки (Skia, HarfBuzz, ANGLE) вшиты в exe ресурсами native.*.dll.
    /// Перед стартом Avalonia они распаковываются в %LOCALAPPDATA%\PWRuLauncher\native\&lt;хэш&gt;\
    /// (один раз на версию) и подгружаются явно — SkiaSharp дальше находит их по имени модуля.
    /// </summary>
    internal static class NativeLoader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        public static void Ensure()
        {
            var asm = typeof(NativeLoader).Assembly;
            string[] names;
            try { names = asm.GetManifestResourceNames(); } catch { return; }

            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PWRuLauncher", "native");
            var dir = Path.Combine(root, Stamp(asm));
            Directory.CreateDirectory(dir);

            foreach (var res in names)
            {
                if (!res.StartsWith("native.", StringComparison.Ordinal) || !res.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var file = Path.Combine(dir, res.Substring("native.".Length));
                try
                {
                    using var s = asm.GetManifestResourceStream(res);
                    if (s == null) continue;
                    if (!File.Exists(file) || new FileInfo(file).Length != s.Length)
                    {
                        var tmp = file + ".part";
                        using (var f = File.Create(tmp)) s.CopyTo(f);
                        try { if (File.Exists(file)) File.Delete(file); } catch { }
                        File.Move(tmp, file);
                    }
                }
                catch { /* уже распакована и занята другим экземпляром — пусть загрузится как есть */ }
            }

            SetDllDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "*.dll")) LoadLibrary(f);
        }

        /// <summary>Папка на версию сборки: у нового exe свой набор, старые не мешают.</summary>
        private static string Stamp(Assembly asm)
        {
            try
            {
                using var md5 = MD5.Create();
                var loc = asm.Location;
                var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(asm.FullName + "|" + (File.Exists(loc) ? new FileInfo(loc).Length.ToString() : "")));
                return BitConverter.ToString(bytes, 0, 6).Replace("-", "").ToLowerInvariant();
            }
            catch { return "default"; }
        }
    }
}
