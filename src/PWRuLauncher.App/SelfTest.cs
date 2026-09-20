using System;
using System.IO;
using System.Text;
using System.Threading;
using PWRuLauncher.Core;

namespace PWRuLauncher
{
    /// <summary>Сквозной прогон установщика на указанной копии игры: установить → проверить → снять → проверить.</summary>
    internal static class SelfTest
    {
        /// <summary>--selftest-install &lt;игра&gt;: поставить всё (интро+текст+озвучка) и выйти, ничего не снимая — для тестов «обновления».</summary>
        public static int RunInstallOnly(string[] args, string gameRoot)
        {
            var exe = typeof(SelfTest).Assembly.Location;
            var logPath = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "PWRuLauncher.selftest.log");
            try
            {
                string? payloadPath = null;
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--payload") payloadPath = args[i + 1].Trim('"', '\'');
                var game = GameInstall.FromAnyPath(gameRoot) ?? throw new Exception("no game at " + gameRoot);
                using var payload = Payload.TryOpen(exe, payloadPath) ?? throw new Exception("no payload");
                var inst = new Installer(payload, game);
                var progress = new Progress<InstallProgress>(_ => { });
                inst.InstallIntroAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
                inst.InstallTextAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
                inst.InstallVoiceAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
                var st = inst.Inspect();
                File.WriteAllText(logPath, $"install-only v{payload.Manifest.Version}: voice={st.VoiceInstalled} text={st.TextInstalled} intro={st.IntroInstalled}\nRESULT OK\n");
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(logPath, "EXCEPTION " + ex + "\nRESULT FAILED\n"); return 2; }
        }

        /// <summary>Контур обновления без окна: latest.json → сравнение версий → exe + sha256 → (при --swap) подмена и запуск нового.</summary>
        public static int RunUpdate(string[] args, int urlIndex)
        {
            var exe = typeof(SelfTest).Assembly.Location;
            var logPath = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "PWRuLauncher.update.log");
            var sb = new StringBuilder();
            int failed = 0;
            void Check(bool ok, string what) { sb.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failed++; }
            try
            {
                var url = args[urlIndex].Trim('"', '\'');
                bool swap = false; var passthrough = new StringBuilder();
                for (int i = urlIndex + 1; i < args.Length; i++)
                {
                    if (args[i] == "--swap") { swap = true; continue; }
                    if (swap) passthrough.Append(' ').Append(args[i].Contains(" ") ? "\"" + args[i] + "\"" : args[i]);
                }
                sb.AppendLine("current " + Updates.CurrentVersion + " exe " + exe);
                var latest = Updates.FetchLatestAsync(url, CancellationToken.None).GetAwaiter().GetResult();
                sb.AppendLine($"latest tag={latest.Tag} version={latest.Version} assets={latest.Assets.Count}");
                Check(latest.Version != null, "tag parses as version");
                Check(latest.Exe != null, "exe asset present");
                bool newer = latest.Version != null && latest.Version > Updates.CurrentVersion;
                sb.AppendLine("update available: " + newer);
                if (!newer || latest.Exe == null) { sb.AppendLine("RESULT NO-UPDATE"); File.WriteAllText(logPath, sb.ToString()); return failed == 0 ? 0 : 2; }

                var staged = Path.Combine(SelfUpdate.StagingDir, $"PWRuLauncher-{latest.Version}.exe");
                Updates.DownloadAsync(latest.Exe.Url, staged, latest.Exe.Size, new Progress<double>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
                Check(File.Exists(staged) && new FileInfo(staged).Length == latest.Exe.Size, "downloaded size matches asset size");
                if (latest.ExeSha != null)
                {
                    var expected = Updates.ParseShaText(Updates.FetchTextAsync(latest.ExeSha.Url, CancellationToken.None).GetAwaiter().GetResult());
                    Check(expected != null && expected == Updates.Sha256File(staged), "sha256 matches published");
                }
                else sb.AppendLine("no sha256 asset (skipped)");
                if (swap && failed == 0)
                {
                    SelfUpdate.SwapAndStart(exe, staged, passthrough.ToString().Trim());
                    sb.AppendLine("swapped; new exe started with: " + passthrough.ToString().Trim());
                }
            }
            catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); failed++; }
            sb.AppendLine(failed == 0 ? "RESULT OK" : $"RESULT FAILED ({failed})");
            File.WriteAllText(logPath, sb.ToString());
            return failed == 0 ? 0 : 2;
        }

        public static int Run(string[] args, string gameRoot)
        {
            var sb = new StringBuilder();
            int failed = 0;
            void Check(bool ok, string what) { sb.AppendLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failed++; }
            var exe = typeof(SelfTest).Assembly.Location;
            var logPath = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "PWRuLauncher.selftest.log");
            try
            {
                string? payloadPath = null;
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "--payload") payloadPath = args[i + 1].Trim('"', '\'');

                sb.AppendLine("autodetect: " + (GameLocator.AutoDetect(Path.GetDirectoryName(exe))?.Root ?? "<none>"));
                var game = GameInstall.FromAnyPath(gameRoot);
                Check(game != null, "game root resolves: " + gameRoot);
                if (game == null) throw new Exception("no game");
                sb.AppendLine("game: " + game.Root + " steam=" + game.IsSteamCopy);

                using var payload = Payload.TryOpen(exe, payloadPath) ?? throw new Exception("no payload");
                var m = payload.Manifest;
                sb.AppendLine($"payload: {payload.Source} v{m.Version} voice={m.VoiceSize} subs={m.SubsSize} stamp={m.BundleStamp} movies={string.Join(";", m.Movies)}");

                var inst = new Installer(payload, game);
                var st0 = inst.Inspect();
                sb.AppendLine($"before: voice={st0.VoiceInstalled} text={st0.TextInstalled} intro={st0.IntroInstalled} present={st0.VoicePresent}/{st0.TextPresent}/{st0.IntroPresent}");
                if (st0.VoicePresent || st0.TextPresent || st0.IntroPresent)
                {
                    // копия не чистая (прошлый прогон или другая версия) — сначала снять всё, иначе «оригинал» интро будет нашим файлом
                    inst.RemoveVoiceAsync().GetAwaiter().GetResult();
                    inst.RemoveTextAsync().GetAwaiter().GetResult();
                    inst.RemoveIntroAsync().GetAwaiter().GetResult();
                    sb.AppendLine("pre-clean: removed previous install");
                }

                var progress = new Progress<InstallProgress>(_ => { });
                var moviePath = Path.Combine(game.MoviesDir, "HumbleIntro_PW.mp4");
                long origSize = File.Exists(moviePath) ? new FileInfo(moviePath).Length : -1;

                // интро — базовый слой: ставится вместе с любым компонентом, снимается когда сняты оба
                inst.InstallIntroAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
                inst.InstallTextAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
                Check(new FileInfo(Path.Combine(game.ModsDir, Installer.SubsPak)).Length == m.SubsSize, "subs pak size");
                Check(File.Exists(Path.Combine(game.Win64Dir, "dwmapi.dll")) && File.Exists(Path.Combine(game.Win64Dir, "UE4SS.dll")), "UE4SS dlls");
                Check(Directory.Exists(Path.Combine(game.Win64Dir, "Mods", "PWTextRU")) && !Directory.Exists(Path.Combine(game.Win64Dir, "Mods", "PWBadge")), "lua mods (no PWBadge)");
                Check(!File.Exists(Path.Combine(game.LogicModsDir, "ModOK.pak")), "no ModOK.pak");
                Check(File.Exists(moviePath + Installer.MovieBackupSuffix) && new FileInfo(moviePath + Installer.MovieBackupSuffix).Length == origSize, "intro backup keeps original");
                Check(new FileInfo(moviePath).Length == payload.Archive.GetEntry("text/ue4ss/Movies/HumbleIntro_PW.mp4")!.Length, "intro replaced");
                Check(File.ReadAllText(Path.Combine(game.Win64Dir, "Mods", Installer.BundleMarker)).Trim() == m.BundleStamp.ToString(), "bundle marker");
                Check(File.ReadAllLines(Path.Combine(game.MoviesDir, Installer.MoviesMarker))[1] == "HumbleIntro_PW.mp4", "movies marker lists intro");
                var st1 = inst.Inspect();
                Check(st1.TextInstalled && !st1.VoiceInstalled && st1.IntroInstalled, "inspect after text: text=on voice=off intro=on");

                inst.InstallVoiceAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
                Check(new FileInfo(Path.Combine(game.ModsDir, Installer.VoicePak)).Length == m.VoiceSize, "voice pak size");
                var st2 = inst.Inspect();
                Check(st2.TextInstalled && st2.VoiceInstalled, "inspect after voice: both on");

                // повторная установка интро поверх (как «обновление»): бэкап не должен затереться
                inst.InstallIntroAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
                Check(new FileInfo(moviePath + Installer.MovieBackupSuffix).Length == origSize, "reinstall keeps original backup");

                inst.RemoveVoiceAsync().GetAwaiter().GetResult();
                Check(!File.Exists(Path.Combine(game.ModsDir, Installer.VoicePak)), "voice removed");
                Check(inst.Inspect().IntroInstalled && File.Exists(moviePath + Installer.MovieBackupSuffix), "intro stays while text is on");
                inst.RemoveTextAsync().GetAwaiter().GetResult();
                Check(!File.Exists(Path.Combine(game.ModsDir, Installer.SubsPak)), "subs removed");
                Check(!File.Exists(Path.Combine(game.Win64Dir, "dwmapi.dll")) && !Directory.Exists(Path.Combine(game.Win64Dir, "Mods")), "UE4SS removed");
                inst.RemoveIntroAsync().GetAwaiter().GetResult();
                Check(File.Exists(moviePath) && new FileInfo(moviePath).Length == origSize && !File.Exists(moviePath + Installer.MovieBackupSuffix), "intro restored, backup gone");
                Check(!File.Exists(Path.Combine(game.MoviesDir, Installer.MoviesMarker)), "movies marker gone");
                var st3 = inst.Inspect();
                Check(!st3.Any, "inspect after removal: nothing");
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION " + ex);
                failed++;
            }
            sb.AppendLine(failed == 0 ? "RESULT OK" : $"RESULT FAILED ({failed})");
            File.WriteAllText(logPath, sb.ToString());
            return failed == 0 ? 0 : 2;
        }
    }
}
