using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace PWRuLauncher
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            var args = Environment.GetCommandLineArgs();
            string? payload = null, demo = null, releasesUrl = null, shot = null, game = null;
            bool autoCheck = true;
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[i + 1].Trim('"', '\'') : "";
                switch (args[i])
                {
                    case "--payload": payload = Next(); break;
                    case "--demo": demo = Next(); break;
                    case "--releases-url": releasesUrl = Next(); break;
                    case "--shot": shot = Next(); break;
                    case "--game": game = Next(); break;
                    case "--no-update-check": autoCheck = false; break;
                }
            }
            if ((demo != null || shot != null) && releasesUrl == null) autoCheck = false;   // показ/скриншот — без сети, если не задан свой источник
            _vm = new MainViewModel(args[0], payload, releasesUrl, autoCheck, game);
            if (demo != null) _vm.ApplyDemo(demo);
            _vm.RequestClose += () => Avalonia.Threading.Dispatcher.UIThread.Post(Close);
            DataContext = _vm;

            // --shot <png>: окно рендерит себя в файл и закрывается (проверка вида без захвата экрана)
            if (shot != null)
            {
                Opened += async (_, _) =>
                {
                    await System.Threading.Tasks.Task.Delay(releasesUrl != null ? 2000 : 600);
                    try
                    {
                        var size = new Avalonia.PixelSize((int)Width, (int)Height);
                        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
                        rtb.Render(this);
                        rtb.Save(shot);
                    }
                    catch (Exception ex) { System.IO.File.WriteAllText(shot + ".err.txt", ex.ToString()); }
                    Close();
                };
            }
        }

        // окно без системной рамки: тянем за шапку
        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();
        private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private async void Primary_Click(object? sender, RoutedEventArgs e) => await _vm.PrimaryAsync();

        private void Components_Click(object? sender, RoutedEventArgs e) => _vm.ComponentsOpen = !_vm.ComponentsOpen;
        private void Path_Click(object? sender, RoutedEventArgs e) => _vm.PathOpen = !_vm.PathOpen;

        private void About_Click(object? sender, RoutedEventArgs e) => _vm.AboutOpen = !_vm.AboutOpen;
        private void AboutClose_Click(object? sender, RoutedEventArgs e) => _vm.AboutOpen = false;
        private void Repo_Click(object? sender, RoutedEventArgs e) => OpenUrl(_vm.RepoUrl);
        private void Releases_Click(object? sender, RoutedEventArgs e) => OpenUrl(_vm.ReleasesUrl);
        private async void UpdateAction_Click(object? sender, RoutedEventArgs e) => await _vm.UpdateActionAsync();

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* браузера нет — молчим */ }
        }

        private async void Browse_Click(object? sender, RoutedEventArgs e)
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Папка Project Wingman",
                AllowMultiple = false,
            });
            var folder = picked.FirstOrDefault();
            if (folder == null) return;
            var path = folder.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) _vm.SetPathFromPicker(path!);
        }
    }
}
