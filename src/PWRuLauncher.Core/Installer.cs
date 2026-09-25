using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace PWRuLauncher.Core
{
    public enum Component { Voice, Text }

    public readonly struct InstallProgress
    {
        public InstallProgress(string status, double fraction) { Status = status; Fraction = fraction; }
        public string Status { get; }
        public double Fraction { get; }
    }

    public sealed class InstallState
    {
        /// <summary>Файлы компонента есть в игре (нашей или прошлой версии).</summary>
        public bool VoicePresent { get; set; }
        public bool TextPresent { get; set; }
        public bool IntroPresent { get; set; }
        /// <summary>Стоит ровно та версия, что в бандле этого exe.</summary>
        public bool VoiceInstalled { get; set; }
        public bool TextInstalled { get; set; }
        /// <summary>Интро VGS — базовый слой: стоит, пока включён хоть один компонент.</summary>
        public bool IntroInstalled { get; set; }
        /// <summary>Есть, но от другой версии бандла — после обновления лаунчера надо переставить.</summary>
        public bool VoiceOutdated => VoicePresent && !VoiceInstalled;
        public bool TextOutdated => TextPresent && !TextInstalled;
        public bool IntroOutdated => IntroPresent && !IntroInstalled;
        public bool Any => VoiceInstalled || TextInstalled;
        public bool AnyOutdated => VoiceOutdated || TextOutdated;
    }

    /// <summary>
    /// Раскладка модов по папке игры: паки в ~mods, UE4SS в Binaries\Win64, интро в Content\Movies
    /// с бэкапом оригинала. Маркеры совместимы с прежними установками локализации.
    /// </summary>
    public sealed class Installer
    {
        public const string VoicePak = "PW_RU_Voice.pak";
        public const string SubsPak = "PW_RU_Subs.pak";
        public const string OldComboPak = "VGS_PW_Russian_Voice.pak";
        public const string BundleMarker = "PWRU_bundle.info";
        public const string MoviesMarker = "PWRU_movies.info";
        public const string MovieBackupSuffix = ".pwru-orig";
        private static readonly string[] Ue4ssWin64Files = { "dwmapi.dll", "UE4SS.dll", "UE4SS-settings.ini" };
        /// <summary>Лог прошлого запуска игры: не удаляем, а переименовываем — иначе каждое обновление стирает
        /// улики упавшего запуска (bugs/2026-09-21-fatal-error-pri-obnovlenii-modov).</summary>
        public const string Ue4ssLog = "UE4SS.log";
        public const string Ue4ssPrevLog = "UE4SS.prev.log";
        public const string GameProcessName = "ProjectWingman-Win64-Shipping";
        private static readonly string[] Ue4ssWin64Dirs = { "Mods" };
        private static readonly string[] Ue4ssLogicModsFiles = { "ModOK.pak" };

        private readonly Payload _payload;
        private readonly GameInstall _game;

        public Installer(Payload payload, GameInstall game) { _payload = payload; _game = game; }

        // ------------------------------------------------------------------ состояние
        public InstallState Inspect()
        {
            var m = _payload.Manifest;
            var st = new InstallState
            {
                VoicePresent = File.Exists(Path.Combine(_game.ModsDir, VoicePak)),
                TextPresent = File.Exists(Path.Combine(_game.ModsDir, SubsPak))
                              || File.Exists(Path.Combine(_game.Win64Dir, "Mods", BundleMarker))
                              || File.Exists(Path.Combine(_game.Win64Dir, "dwmapi.dll")),
                IntroPresent = File.Exists(Path.Combine(_game.MoviesDir, MoviesMarker)),
                VoiceInstalled = SizeMatches(Path.Combine(_game.ModsDir, VoicePak), m.VoiceSize),
                TextInstalled = SizeMatches(Path.Combine(_game.ModsDir, SubsPak), m.SubsSize)
                                && ReadFirstLong(Path.Combine(_game.Win64Dir, "Mods", BundleMarker)) == m.BundleStamp
                                && File.Exists(Path.Combine(_game.Win64Dir, "dwmapi.dll")),
                IntroInstalled = ReadFirstLong(Path.Combine(_game.MoviesDir, MoviesMarker)) == m.BundleStamp,
            };
            return st;
        }

        private static bool SizeMatches(string path, long size)
        {
            try { return File.Exists(path) && new FileInfo(path).Length == size; } catch { return false; }
        }

        private static long? ReadFirstLong(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var r = new StreamReader(path);
                var line = r.ReadLine();
                return line != null && long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (long?)null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ озвучка
        public Task InstallVoiceAsync(IProgress<InstallProgress> progress, CancellationToken ct) => Task.Run(() =>
        {
            EnsureGameClosed();
            InstallLog.Write($"озвучка: установка (бандл {_payload.Manifest.BundleStamp})");
            Directory.CreateDirectory(_game.ModsDir);
            var entry = _payload.Archive.GetEntry(Payload.VoiceEntry) ?? throw new InvalidDataException("В бандле нет озвучки");
            ExtractFile(entry, Path.Combine(_game.ModsDir, VoicePak), "Распаковываю озвучку…", entry.Length, 0, progress, ct);
            RemoveIfExists(Path.Combine(_game.ModsDir, OldComboPak));
        }, ct);

        public Task RemoveVoiceAsync() => Task.Run(() =>
        {
            EnsureGameClosed();
            InstallLog.Write("озвучка: снятие");
            RemoveIfExists(Path.Combine(_game.ModsDir, VoicePak));
            RemoveIfExists(Path.Combine(_game.ModsDir, VoicePak + ".part"));
        });

        // ------------------------------------------------------------------ текст: сабы + UE4SS + интро
        public Task InstallTextAsync(IProgress<InstallProgress> progress, CancellationToken ct) => Task.Run(() =>
        {
            EnsureGameClosed();
            var m = _payload.Manifest;
            InstallLog.Write($"текст: установка (бандл {m.BundleStamp})");
            Directory.CreateDirectory(_game.ModsDir);
            var subs = _payload.Archive.GetEntry(Payload.SubsEntry) ?? throw new InvalidDataException("В бандле нет субтитров");
            long total = subs.Length + _payload.TotalBytes(Payload.Ue4ssPrefix) - _payload.TotalBytes(Payload.MoviesPrefix);
            long done = 0;

            ExtractFile(subs, Path.Combine(_game.ModsDir, SubsPak), "Распаковываю субтитры…", total, done, progress, ct);
            done += subs.Length;

            // чистая переустановка слоя UE4SS: старые файлы прочь (и оригиналы роликов на место), потом свежая раскладка
            RemoveUe4ssLayer();

            foreach (var e in _payload.Archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!e.FullName.StartsWith(Payload.Ue4ssPrefix, StringComparison.Ordinal) || e.FullName.EndsWith("/")) continue;
                var rel = e.FullName.Substring(Payload.Ue4ssPrefix.Length);            // Win64/... | LogicMods/... (Movies — отдельный слой)
                var parts = rel.Split('/');
                if (parts.Length < 2 || Array.IndexOf(parts, "..") >= 0) continue;
                string dest;
                if (parts[0] == "Win64") dest = Combine(_game.Win64Dir, parts, 1);
                else if (parts[0] == "LogicMods") dest = Combine(_game.LogicModsDir, parts, 1);
                else continue;
                ExtractFile(e, dest, "Распаковываю интерфейс…", total, done, progress, ct);
                done += e.Length;
            }

            Directory.CreateDirectory(Path.Combine(_game.Win64Dir, "Mods"));
            File.WriteAllText(Path.Combine(_game.Win64Dir, "Mods", BundleMarker), m.BundleStamp.ToString(CultureInfo.InvariantCulture));
            RemoveIfExists(Path.Combine(_game.ModsDir, OldComboPak));
            InstallLog.Write($"текст: готово, маркер {m.BundleStamp}");
            progress.Report(new InstallProgress("Готово", 1));
        }, ct);

        // ------------------------------------------------------------------ интро: базовый слой
        /// <summary>Интро VGS ставится, пока включён хоть один компонент; снимается только когда выключены оба.</summary>
        public Task InstallIntroAsync(IProgress<InstallProgress> progress, CancellationToken ct) => Task.Run(() =>
        {
            EnsureGameClosed();
            var m = _payload.Manifest;
            InstallLog.Write($"интро: установка (бандл {m.BundleStamp})");
            RestoreMovies();                          // чистая переустановка: оригиналы на место, потом наши
            long total = _payload.TotalBytes(Payload.MoviesPrefix);
            long done = 0;
            var movies = new List<string>();
            foreach (var e in _payload.Archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!e.FullName.StartsWith(Payload.MoviesPrefix, StringComparison.Ordinal) || e.FullName.EndsWith("/")) continue;
                var name = e.FullName.Substring(Payload.MoviesPrefix.Length);
                if (name.Length == 0 || name.Contains("/") || name.Contains("..")) continue;   // только плоско
                var dest = Path.Combine(_game.MoviesDir, name);
                BackupOriginalOnce(dest);
                ExtractFile(e, dest, "Копирую интро…", total, done, progress, ct);
                done += e.Length;
                movies.Add(name);
            }
            Directory.CreateDirectory(_game.MoviesDir);
            File.WriteAllText(Path.Combine(_game.MoviesDir, MoviesMarker),
                m.BundleStamp.ToString(CultureInfo.InvariantCulture) + "\n" + string.Join("\n", movies) + "\n");
        }, ct);

        public Task RemoveIntroAsync() => Task.Run(() =>
        {
            EnsureGameClosed();
            InstallLog.Write("интро: снятие");
            RestoreMovies();
        });

        public Task RemoveTextAsync() => Task.Run(() =>
        {
            EnsureGameClosed();
            InstallLog.Write("текст: снятие");
            RemoveIfExists(Path.Combine(_game.ModsDir, SubsPak));
            RemoveIfExists(Path.Combine(_game.ModsDir, SubsPak + ".part"));
            RemoveUe4ssLayer();
        });

        /// <summary>Удаляет ТОЛЬКО наши файлы. Залоченный dll = игра запущена, честная ошибка наружу.
        /// UE4SS.log при этом не удаляется, а становится UE4SS.prev.log: это лог последнего запуска игры,
        /// и именно он нужен, когда игра упала во время или сразу после обновления.</summary>
        private void RemoveUe4ssLayer()
        {
            var log = Path.Combine(_game.Win64Dir, Ue4ssLog);
            if (File.Exists(log))
            {
                var prev = Path.Combine(_game.Win64Dir, Ue4ssPrevLog);
                try { RemoveIfExists(prev); File.Move(log, prev); InstallLog.Write($"{Ue4ssLog} -> {Ue4ssPrevLog}"); }
                catch (Exception ex) { InstallLog.Write($"{Ue4ssLog}: не переименован ({ex.Message})"); }
            }
            foreach (var f in Ue4ssWin64Files)
            {
                var p = Path.Combine(_game.Win64Dir, f);
                if (!File.Exists(p)) continue;
                try { File.Delete(p); InstallLog.Write($"удалён {f}"); }
                catch (Exception ex) { InstallLog.Write($"НЕ удалён {f}: {ex.Message}"); throw new IOException($"Не удалось удалить {f}: закройте игру. ({ex.Message})"); }
            }
            foreach (var d in Ue4ssWin64Dirs)
            {
                var p = Path.Combine(_game.Win64Dir, d);
                if (!Directory.Exists(p)) continue;
                try { Directory.Delete(p, true); InstallLog.Write($"удалена папка {d}"); }
                catch (Exception ex) { InstallLog.Write($"НЕ удалена папка {d}: {ex.Message}"); throw new IOException($"Не удалось удалить папку {d}: закройте игру. ({ex.Message})"); }
            }
            foreach (var f in Ue4ssLogicModsFiles) RemoveIfExists(Path.Combine(_game.LogicModsDir, f));
        }

        /// <summary>Игра запущена — ничего не трогаем. До 24.09 лаунчер узнавал об этом только по залоченному
        /// файлу, уже снеся часть слоя модов: запущенная игра видела неполный набор и падала с Fatal Error.</summary>
        public static bool IsGameRunning()
        {
            try { return Process.GetProcessesByName(GameProcessName).Length > 0; } catch { return false; }
        }

        private static void EnsureGameClosed()
        {
            if (!IsGameRunning()) return;
            InstallLog.Write("отказ: игра запущена");
            throw new IOException("Игра запущена — закройте её и повторите.");
        }

        private void RestoreMovies()
        {
            var marker = Path.Combine(_game.MoviesDir, MoviesMarker);
            if (!File.Exists(marker)) return;
            string[] lines;
            try { lines = File.ReadAllLines(marker); } catch { lines = Array.Empty<string>(); }
            for (int i = 1; i < lines.Length; i++)
            {
                var name = lines[i].Trim();
                if (name.Length == 0 || name.Contains("..") || name.Contains("/") || name.Contains("\\")) continue;
                var ours = Path.Combine(_game.MoviesDir, name);
                var orig = ours + MovieBackupSuffix;
                try
                {
                    if (File.Exists(orig)) { RemoveIfExists(ours); File.Move(orig, ours); }
                    else RemoveIfExists(ours);   // бэкапа нет (Steam уже вернул родной файл) — просто чистим
                }
                catch (Exception ex) { throw new IOException($"Не удалось вернуть ролик {name}: закройте игру. ({ex.Message})"); }
            }
            RemoveIfExists(marker);
        }

        /// <summary>Оригинал уходит в .pwru-orig только если бэкапа ещё нет — повторная установка после проверки файлов Steam его не затирает.</summary>
        private static void BackupOriginalOnce(string dest)
        {
            var orig = dest + MovieBackupSuffix;
            if (File.Exists(dest) && !File.Exists(orig)) File.Move(dest, orig);
        }

        // ------------------------------------------------------------------ утилиты
        private static string Combine(string root, string[] parts, int from)
        {
            var p = root;
            for (int i = from; i < parts.Length; i++) p = Path.Combine(p, parts[i]);
            return p;
        }

        private static void ExtractFile(ZipArchiveEntry entry, string dest, string status, long total, long doneBefore,
                                        IProgress<InstallProgress> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".part";
            var buffer = new byte[1 << 20];
            long copied = 0;
            var sw = Stopwatch.StartNew();
            using (var src = entry.Open())
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length))
            {
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, n);
                    copied += n;
                    if (sw.ElapsedMilliseconds > 40)
                    {
                        progress.Report(new InstallProgress(status, total > 0 ? (double)(doneBefore + copied) / total : 0));
                        sw.Restart();
                    }
                }
            }
            RemoveIfExists(dest);
            File.Move(tmp, dest);
            InstallLog.Write($"записан {dest} ({entry.Length} б)");
            progress.Report(new InstallProgress(status, total > 0 ? (double)(doneBefore + entry.Length) / total : 0));
        }

        private static void RemoveIfExists(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>Лог установки: что снесли, что распаковали, во сколько. %LOCALAPPDATA%\PWRuLauncher\install.log,
    /// дописывается, при 2 МБ уходит в install.prev.log. Молчит при любой ошибке — лог не должен ломать установку.</summary>
    public static class InstallLog
    {
        public static string Path =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PWRuLauncher", "install.log");

        private static readonly object Gate = new object();

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    var p = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
                    if (File.Exists(p) && new FileInfo(p).Length > 2 * 1024 * 1024)
                    {
                        var prev = System.IO.Path.ChangeExtension(p, ".prev.log");
                        try { if (File.Exists(prev)) File.Delete(prev); File.Move(p, prev); } catch { }
                    }
                    File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
                }
            }
            catch { /* лог не должен ломать установку */ }
        }
    }

    public static class GameLauncher
    {
        public static void Launch(GameInstall game)
        {
            if (game.IsSteamCopy)
            {
                Process.Start(new ProcessStartInfo("steam://rungameid/" + GameInstall.SteamAppId) { UseShellExecute = true });
                return;
            }
            Process.Start(new ProcessStartInfo(game.ShippingExe)
            {
                WorkingDirectory = Path.GetDirectoryName(game.ShippingExe),
                UseShellExecute = true,
            });
        }
    }
}
