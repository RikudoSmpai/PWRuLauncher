using System;
using Avalonia;

namespace PWRuLauncher
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // --updated-from <старый exe>: мы — свежескачанная версия, убираем старый файл
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == PWRuLauncher.Core.SelfUpdate.UpdatedFromArg) PWRuLauncher.Core.SelfUpdate.CleanupOld(args[i + 1].Trim('"', '\''));
            // --selftest <папка игры> [--payload <zip>]: прогон установщика без окна, отчёт в PWRuLauncher.selftest.log
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--selftest") return SelfTest.Run(args, args[i + 1].Trim('"', '\''));
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--selftest-install") return SelfTest.RunInstallOnly(args, args[i + 1].Trim('"', '\''));
            // --selftest-update <url latest.json> [--swap [аргументы новому exe]]: проверка/скачивание/подмена без окна
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--selftest-update") return SelfTest.RunUpdate(args, i + 1);
            try
            {
                NativeLoader.Ensure();
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // окно без консоли: падение на старте иначе было бы немым
                try
                {
                    var log = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? ".", "PWRuLauncher.log");
                    System.IO.File.AppendAllText(log, $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n");
                }
                catch { }
                return 1;
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
