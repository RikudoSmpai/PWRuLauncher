using System;
using System.Diagnostics;
using System.IO;

namespace PWRuLauncher.Core
{
    /// <summary>
    /// «Удалить всё», последний шаг: сам лаунчер. Работающий exe и его native-библиотеки удалить нельзя,
    /// поэтому скрытый cmd ждёт выхода процесса (до ~30 с, попытка раз в секунду) и удаляет exe, его
    /// .old от обновления, логи рядом с ним и %LOCALAPPDATA%\PWRuLauncher (native-библиотеки, install.log,
    /// скачанные обновления). Ничего больше: удаляются только эти имена.
    /// </summary>
    public static class SelfRemove
    {
        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PWRuLauncher");

        /// <summary>Файлы рядом с exe, которые удаляются вместе с ним.</summary>
        public static string[] Files(string exePath)
        {
            var dir = Path.GetDirectoryName(exePath) ?? ".";
            return new[]
            {
                exePath,
                exePath + SelfUpdate.OldSuffix,
                Path.Combine(dir, "PWRuLauncher.log"),
                Path.Combine(dir, "PWRuLauncher.selftest.log"),
                Path.Combine(dir, "PWRuLauncher.update.log"),
            };
        }

        /// <summary>Команда для cmd /c. null — путь, который cmd прочтёт не так (знак %), тогда удаляем не мы.</summary>
        public static string? Command(string exePath, string dataDir)
        {
            var files = Files(exePath);
            foreach (var p in files)
                if (p.IndexOf('%') >= 0 || p.IndexOf('"') >= 0) return null;
            if (dataDir.IndexOf('%') >= 0 || dataDir.IndexOf('"') >= 0) return null;
            string Q(string p) => "\"" + p + "\"";
            var del = "del /f /q " + string.Join(" ", Array.ConvertAll(files, Q)) + " >nul 2>&1";
            var rd = "rmdir /s /q " + Q(dataDir) + " >nul 2>&1";
            // в cmd /c (не в .bat) переменная цикла пишется одним %; exit — выход из cmd, как только всё удалено
            return "for /l %i in (1,1,30) do @(ping -n 2 127.0.0.1 >nul & " + del + " & " + rd +
                   " & if not exist " + Q(exePath) + " if not exist " + Q(dataDir) + " exit)";
        }

        /// <summary>Запускает удаление; вызывающий сразу после этого закрывается. false — не запущено.</summary>
        public static bool Schedule(string exePath)
        {
            var cmd = Command(exePath, DataDir);
            if (cmd == null) return false;
            try
            {
                var shell = Environment.GetEnvironmentVariable("ComSpec");
                if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = "cmd.exe";
                var psi = new ProcessStartInfo(shell!, "/d /c " + cmd)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),   // не папка лаунчера: её cmd держал бы открытой
                };
                Process.Start(psi);
                InstallLog.Write("удалить всё: лаунчер удалит себя после выхода (" + exePath + ")");
                return true;
            }
            catch (Exception ex)
            {
                InstallLog.Write("удалить всё: не запустил удаление лаунчера: " + ex.Message);
                return false;
            }
        }
    }
}
