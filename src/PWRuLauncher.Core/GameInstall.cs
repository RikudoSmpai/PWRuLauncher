using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PWRuLauncher.Core
{
    /// <summary>Папка игры и производные пути. Строится только из подтверждённого корня.</summary>
    public sealed class GameInstall
    {
        public const string ShippingExeRelative = @"ProjectWingman\Binaries\Win64\ProjectWingman-Win64-Shipping.exe";
        public const string SteamAppId = "895870";

        public string Root { get; }
        public string PaksDir => Path.Combine(Root, "ProjectWingman", "Content", "Paks");
        public string ModsDir => Path.Combine(PaksDir, "~mods");
        public string LogicModsDir => Path.Combine(PaksDir, "LogicMods");
        public string Win64Dir => Path.Combine(Root, "ProjectWingman", "Binaries", "Win64");
        public string MoviesDir => Path.Combine(Root, "ProjectWingman", "Content", "Movies");
        public string ShippingExe => Path.Combine(Root, ShippingExeRelative);

        /// <summary>Steam-копия живёт внутри steamapps — запускаем через Steam, чтобы оверлей и статус были штатными.</summary>
        public bool IsSteamCopy
        {
            get
            {
                foreach (var part in Root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    if (string.Equals(part, "steamapps", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        private GameInstall(string root) => Root = root;

        public static bool IsGameRoot(string dir)
            => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, ShippingExeRelative));

        /// <summary>Принимает корень игры, папку Paks, Binaries, любой файл внутри — поднимается вверх до корня.</summary>
        public static GameInstall? FromAnyPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string? dir;
            try
            {
                dir = path!.Trim().Trim('"');
                if (File.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (dir == null || !Directory.Exists(dir)) return null;
                dir = Path.GetFullPath(dir);
            }
            catch { return null; }

            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (IsGameRoot(dir)) return new GameInstall(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }

    /// <summary>Автопоиск игры: Steam-библиотеки, GOG, типовые папки, соседство с самим лаунчером.</summary>
    public static class GameLocator
    {
        public static GameInstall? AutoDetect(string? launcherDir = null)
        {
            foreach (var c in Candidates(launcherDir))
            {
                var g = GameInstall.FromAnyPath(c);
                if (g != null) return g;
            }
            return null;
        }

        public static IEnumerable<string> Candidates(string? launcherDir)
        {
            // 1. лаунчер положили внутрь папки игры
            if (!string.IsNullOrEmpty(launcherDir)) yield return launcherDir!;

            // 2. Steam: все библиотеки из libraryfolders.vdf
            foreach (var lib in SteamLibraries())
                yield return Path.Combine(lib, "steamapps", "common", "Project Wingman");

            // 3. GOG Galaxy: реестр игр
            foreach (var p in GogPaths()) yield return p;

            // 4. типовые папки на всех дисках
            string[] tails =
            {
                @"Games\Project Wingman", @"GOG Games\Project Wingman", @"Project Wingman",
                @"Program Files (x86)\GOG Galaxy\Games\Project Wingman", @"Program Files\Epic Games\ProjectWingman",
                @"SteamLibrary\steamapps\common\Project Wingman", @"Steam\steamapps\common\Project Wingman",
                @"Program Files (x86)\Steam\steamapps\common\Project Wingman",
            };
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); } catch { yield break; }
            foreach (var d in drives)
            {
                if (d.DriveType != DriveType.Fixed) continue;
                foreach (var t in tails) yield return Path.Combine(d.RootDirectory.FullName, t);
            }
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var steam = ReadRegistry(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                        ?? ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (string.IsNullOrEmpty(steam)) yield break;
            steam = steam!.Replace('/', '\\');
            yield return steam;
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;
            string[] lines;
            try { lines = File.ReadAllLines(vdf); } catch { yield break; }
            foreach (var raw in lines)
            {
                // строка вида:  "path"  "D:\\SteamLibrary"
                var line = raw.Trim();
                if (!line.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                var q = line.IndexOf('"', 6);
                if (q < 0) continue;
                var end = line.IndexOf('"', q + 1);
                if (end < 0) continue;
                yield return line.Substring(q + 1, end - q - 1).Replace(@"\\", @"\");
            }
        }

        private static IEnumerable<string> GogPaths()
        {
            var result = new List<string>();
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var sub in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                        using var games = baseKey.OpenSubKey(sub);
                        if (games == null) continue;
                        foreach (var id in games.GetSubKeyNames())
                        {
                            using var k = games.OpenSubKey(id);
                            var name = k?.GetValue("gameName") as string ?? "";
                            var path = k?.GetValue("path") as string;
                            if (!string.IsNullOrEmpty(path) &&
                                (name.IndexOf("Wingman", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 path!.IndexOf("Wingman", StringComparison.OrdinalIgnoreCase) >= 0))
                                result.Add(path!);
                        }
                    }
                    catch { /* реестр недоступен — идём дальше */ }
                }
            }
            return result;
        }

        private static string? ReadRegistry(RegistryHive hive, string sub, string value)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(sub);
                return k?.GetValue(value) as string;
            }
            catch { return null; }
        }
    }
}
