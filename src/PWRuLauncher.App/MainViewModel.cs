using System;
using System.ComponentModel;
using Component = PWRuLauncher.Core.Component;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PWRuLauncher.Core;

namespace PWRuLauncher
{
    public enum Phase { Install, Installing, Ready, Update, Error }

    /// <summary>Состояние окна: Установить → Распаковка → Играть, Обновить, и Ошибка (игра не найдена).</summary>
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private readonly Payload? _payload;
        private GameInstall? _game;
        private Installer? _installer;
        private CancellationTokenSource? _cts;
        private bool _suppressToggles;

        private readonly string _exePath;
        private readonly string? _releasesUrl;
        private ReleaseInfo? _latest;

        /// <summary>Окно просит закрыться (после подмены exe на новую версию).</summary>
        public event Action? RequestClose;

        public MainViewModel(string exePath, string? payloadOverride, string? releasesUrl = null, bool autoCheckUpdates = true, string? gameOverride = null)
        {
            _exePath = exePath;
            _releasesUrl = releasesUrl;
            var exeDir = Path.GetDirectoryName(exePath);
            try { _payload = Payload.TryOpen(exePath, payloadOverride); }
            catch (Exception ex) { _payload = null; ErrorDetail = "Бандл повреждён: " + ex.Message; }

            Version = _payload != null ? "v" + _payload.Manifest.Version : "dev";
            UpdateStatus = "Версия " + Updates.CurrentVersion + ". Обновления не проверялись.";
            if (autoCheckUpdates) _ = CheckUpdatesAsync(silent: true);
            _game = gameOverride != null ? GameInstall.FromAnyPath(gameOverride) : GameLocator.AutoDetect(exeDir);
            GamePath = _game?.Root ?? "";
            PathValid = _game != null;
            if (_game == null)
            {
                PathOpen = true;                 // автопоиск не сработал: путь открыт сразу (шов из хэндоффа)
                PathHint = "Автопоиск не нашёл игру. Укажите папку игры.";
            }
            Refresh();
        }

        // ------------------------------------------------------------------ свойства окна
        public string Version { get; }

        private Phase _phase;
        public Phase Phase { get => _phase; private set { if (Set(ref _phase, value)) RaisePhaseFlags(); } }
        public bool IsInstall => Phase == Phase.Install;
        public bool IsInstalling => Phase == Phase.Installing;
        public bool IsReady => Phase == Phase.Ready;
        public bool IsUpdate => Phase == Phase.Update;
        public bool IsError => Phase == Phase.Error;
        /// <summary>Ничего не выбрано и ничего не стоит — игра запускается в оригинале, кнопка остаётся «Играть».</summary>
        public bool NothingSelected => !VoiceOn && !TextOn;
        public bool PlayMode => IsReady || (IsInstall && NothingSelected);
        public bool PrimaryEnabled => IsInstall || IsReady || IsUpdate;
        public string PrimaryLabel => PlayMode ? "ИГРАТЬ" : IsUpdate ? "ОБНОВИТЬ" : "УСТАНОВИТЬ";
        public bool ShowPlayIcon => PlayMode && !IsError && !IsInstalling;
        public bool ShowInstallIcon => (IsInstall && !NothingSelected) || IsUpdate;

        // «О программе» и обновления с GitHub
        private bool _aboutOpen;
        public bool AboutOpen { get => _aboutOpen; set => Set(ref _aboutOpen, value); }
        public string AppVersionText => "Версия " + Updates.CurrentVersion;
        public string RepoUrl => Updates.RepoUrl;
        public string ReleasesUrl => Updates.ReleasesPageUrl;
        private string _updateStatus = "";
        public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
        private string _updateActionLabel = "Проверить обновления";
        public string UpdateActionLabel { get => _updateActionLabel; private set => Set(ref _updateActionLabel, value); }
        private bool _updateBusy;
        public bool UpdateActionEnabled => !_updateBusy && !IsInstalling;
        /// <summary>Тихая точка на кнопке «i»: нашли новую версию при старте, в основном окне ни строки.</summary>
        private bool _updateDot;
        public bool UpdateDot { get => _updateDot; private set => Set(ref _updateDot, value); }
        private string _progressSource = "Копирование с диска";
        public string ProgressSource { get => _progressSource; private set => Set(ref _progressSource, value); }

        private string _errorLabel = "ИГРА НЕ НАЙДЕНА";
        public string ErrorLabel { get => _errorLabel; private set => Set(ref _errorLabel, value); }
        private string _errorDetail = "";
        public string ErrorDetail { get => _errorDetail; private set => Set(ref _errorDetail, value); }

        private string _progressStatus = "";
        public string ProgressStatus { get => _progressStatus; private set => Set(ref _progressStatus, value); }
        private double _progressFraction;
        public double ProgressFraction
        {
            get => _progressFraction;
            private set { if (Set(ref _progressFraction, value)) { Raise(nameof(ProgressWidth)); Raise(nameof(ProgressPercent)); } }
        }
        public double ProgressWidth => Math.Round(368 * Math.Max(0, Math.Min(1, ProgressFraction)));
        public string ProgressPercent => ((int)Math.Round(100 * Math.Max(0, Math.Min(1, ProgressFraction)))).ToString();

        // компоненты
        private bool _voiceOn = true, _textOn = true;
        public bool VoiceOn { get => _voiceOn; set { if (Set(ref _voiceOn, value)) OnToggle(Component.Voice, value); } }
        public bool TextOn { get => _textOn; set { if (Set(ref _textOn, value)) OnToggle(Component.Text, value); } }
        public bool TogglesEnabled => IsInstall || IsReady || IsUpdate;

        private string _voiceStatus = "", _textStatus = "";
        public string VoiceStatus { get => _voiceStatus; private set => Set(ref _voiceStatus, value); }
        public string TextStatus { get => _textStatus; private set => Set(ref _textStatus, value); }
        public bool StatusesVisible => !IsInstall;

        private string _componentsSummary = "Выбрать компоненты";
        public string ComponentsSummary { get => _componentsSummary; private set => Set(ref _componentsSummary, value); }
        private string _componentsHint = "";
        public string ComponentsHint { get => _componentsHint; private set => Set(ref _componentsHint, value); }

        private bool _componentsOpen;
        public bool ComponentsOpen { get => _componentsOpen; set => Set(ref _componentsOpen, value); }

        // путь
        private bool _pathOpen;
        public bool PathOpen { get => _pathOpen; set => Set(ref _pathOpen, value); }
        private string _gamePath = "";
        public string GamePath { get => _gamePath; set { if (Set(ref _gamePath, value)) OnPathEdited(value); } }
        private bool _pathValid;
        public bool PathValid { get => _pathValid; private set { if (Set(ref _pathValid, value)) Raise(nameof(PathInvalid)); } }
        public bool PathInvalid => !PathValid;
        private string _pathHint = "";
        public string PathHint { get => _pathHint; private set => Set(ref _pathHint, value); }
        public bool BrowseEnabled => !IsInstalling;

        // ------------------------------------------------------------------ логика
        /// <summary>Пересчитать фазу из фактов на диске.</summary>
        private void Refresh(bool keepToggles = false)
        {
            if (_payload == null)
            {
                _installer = null;
                ErrorLabel = "БАНДЛ НЕ НАЙДЕН";
                if (string.IsNullOrEmpty(ErrorDetail)) ErrorDetail = "Рядом с лаунчером нет payload.zip, и в exe ничего не вшито.";
                Phase = Phase.Error;
                return;
            }
            if (_game == null)
            {
                _installer = null;
                ErrorLabel = "ИГРА НЕ НАЙДЕНА";
                ErrorDetail = "";
                Phase = Phase.Error;
                return;
            }
            _installer = new Installer(_payload, _game);
            var st = _installer.Inspect();
            _suppressToggles = true;
            if (st.AnyOutdated)
            {
                // после обновления лаунчера в игре стоит прошлая версия модов — переставить включённые
                _voiceOn = st.VoicePresent; _textOn = st.TextPresent;
                Raise(nameof(VoiceOn)); Raise(nameof(TextOn));
                Phase = Phase.Update;
            }
            else if (st.Any)
            {
                _voiceOn = st.VoiceInstalled; _textOn = st.TextInstalled;
                Raise(nameof(VoiceOn)); Raise(nameof(TextOn));
                Phase = Phase.Ready;
            }
            else
            {
                // после снятия компонентов руками тумблеры не взводим обратно — иначе кажется, что ничего не произошло
                if (!keepToggles) { _voiceOn = true; _textOn = true; }
                Raise(nameof(VoiceOn)); Raise(nameof(TextOn));
                Phase = Phase.Install;
            }
            _suppressToggles = false;
            RaiseSelectionFlags();
            UpdateComponentTexts();
        }

        private void UpdateComponentTexts()
        {
            switch (Phase)
            {
                case Phase.Install:
                    ComponentsSummary = "Выбрать компоненты";
                    VoiceStatus = ""; TextStatus = "";
                    ComponentsHint = VoiceOn && TextOn ? "Оба включены — установить всё."
                                   : VoiceOn ? "Только озвучка: субтитры и интерфейс останутся английскими."
                                   : TextOn ? "Только текст и интерфейс, без озвучки."
                                   : "Ничего не выбрано — игра запустится в оригинале.";
                    break;
                case Phase.Installing:
                    ComponentsSummary = "Выбранные компоненты";
                    VoiceStatus = VoiceOn ? "Копируется" : "Пропущено";
                    TextStatus = TextOn ? "Копируется" : "Пропущено";
                    ComponentsHint = "Копирование с диска. Дождитесь завершения.";
                    break;
                case Phase.Ready:
                    ComponentsSummary = "Установленные компоненты";
                    VoiceStatus = VoiceOn ? "Установлено" : "Не установлено";
                    TextStatus = TextOn ? "Установлено" : "Не установлено";
                    ComponentsHint = "Выключить — вернуть оригинал.";
                    break;
                case Phase.Update:
                {
                    var st = _installer?.Inspect();
                    ComponentsSummary = "Установленные компоненты";
                    VoiceStatus = !VoiceOn ? "Не установлено" : (st?.VoiceOutdated ?? false) ? "Есть новая версия" : "Установлено";
                    TextStatus = !TextOn ? "Не установлено" : (st?.TextOutdated ?? false) ? "Есть новая версия" : "Установлено";
                    ComponentsHint = "Вышла новая версия локализации — обновить установленное.";
                    break;
                }
                default:
                    ComponentsSummary = "Выбрать компоненты";
                    ComponentsHint = "";
                    break;
            }
            Raise(nameof(PrimaryEnabled));
        }

        public async Task PrimaryAsync()
        {
            if (PlayMode)
            {
                try { GameLauncher.Launch(_game!); }
                catch (Exception ex) { ComponentsHint = "Не удалось запустить игру: " + ex.Message; }
                return;
            }
            if (_installer == null) return;
            if (IsUpdate)
            {
                var st = _installer.Inspect();
                await RunInstallAsync(VoiceOn && st.VoiceOutdated, TextOn && st.TextOutdated);
                return;
            }
            if (!IsInstall) return;
            await RunInstallAsync(VoiceOn, TextOn);
        }

        /// <summary>Установка выбранного: озвучка и текст по очереди, общий прогресс по байтам.</summary>
        private async Task RunInstallAsync(bool voice, bool text)
        {
            if (_installer == null || _payload == null) return;
            _cts = new CancellationTokenSource();
            Phase = Phase.Installing;
            UpdateComponentTexts();
            ProgressFraction = 0; ProgressStatus = "Подготовка…";
            long voiceBytes = voice ? _payload.TotalBytes("voice/") : 0;
            long textBytes = text ? _payload.TotalBytes("text/") : 0;
            long total = Math.Max(1, voiceBytes + textBytes);
            try
            {
                if (voice)
                {
                    var p = new Progress<InstallProgress>(x => { ProgressStatus = x.Status; ProgressFraction = x.Fraction * voiceBytes / total; });
                    await _installer.InstallVoiceAsync(p, _cts.Token);
                    VoiceStatus = "Установлено";
                }
                if (text)
                {
                    var p = new Progress<InstallProgress>(x => { ProgressStatus = x.Status; ProgressFraction = (voiceBytes + x.Fraction * textBytes) / total; });
                    await _installer.InstallTextAsync(p, _cts.Token);
                    TextStatus = "Установлено";
                }
                await EnsureIntroAsync();
                ProgressFraction = 1; ProgressStatus = "Готово";
                await Task.Delay(350);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ErrorDetail = ex.Message;
            }
            _cts = null;
            Refresh();
            if (!string.IsNullOrEmpty(ErrorDetail) && Phase != Phase.Error)
            {
                ComponentsHint = ErrorDetail;   // ошибка копирования показывается под компонентами, окно остаётся рабочим
                ErrorDetail = "";
            }
        }

        /// <summary>Интро VGS — базовый слой: стоит, пока установлен хоть один компонент, снимается когда сняты оба.</summary>
        private async Task EnsureIntroAsync()
        {
            if (_installer == null) return;
            var st = _installer.Inspect();
            bool want = st.VoiceInstalled || st.TextInstalled;
            if (want && !st.IntroInstalled)
            {
                var p = new Progress<InstallProgress>(x => { ProgressStatus = x.Status; });
                await _installer.InstallIntroAsync(p, _cts?.Token ?? CancellationToken.None);
            }
            else if (!want && st.IntroInstalled)
            {
                await _installer.RemoveIntroAsync();
            }
        }

        /// <summary>Тумблер после установки = действие: выключил — вернул оригинал, включил — поставил с диска.</summary>
        private async void OnToggle(Component c, bool on)
        {
            RaiseSelectionFlags();
            if (_suppressToggles) return;
            if (IsInstall) { UpdateComponentTexts(); return; }
            if (!(IsReady || IsUpdate) || _installer == null) return;
            if (on)
            {
                await RunInstallAsync(c == Component.Voice, c == Component.Text);
                return;
            }
            try
            {
                if (c == Component.Voice) await _installer.RemoveVoiceAsync(); else await _installer.RemoveTextAsync();
                await EnsureIntroAsync();
            }
            catch (Exception ex) { ComponentsHint = ex.Message; return; }
            Refresh(keepToggles: true);
            ComponentsHint = c == Component.Voice
                ? "Озвучка удалена, в игре оригинальные голоса."
                : "Субтитры и интерфейс удалены, интро возвращено.";
        }

        private void OnPathEdited(string value)
        {
            var g = GameInstall.FromAnyPath(value);
            PathValid = g != null;
            if (g == null)
            {
                PathHint = string.IsNullOrWhiteSpace(value) ? "Укажите папку игры." : "В этой папке нет Project Wingman.";
                if (_game != null) { _game = null; Refresh(); }
                return;
            }
            PathHint = g.IsSteamCopy ? "Steam-копия: игра запустится через Steam." : "Папка игры найдена.";
            if (_game == null || !string.Equals(_game.Root, g.Root, StringComparison.OrdinalIgnoreCase))
            {
                _game = g;
                Refresh();
            }
        }

        public void SetPathFromPicker(string folder) => GamePath = folder;

        /// <summary>Режим показа: --demo install|installing|ready|error. Диск не трогается.</summary>
        public void ApplyDemo(string demo)
        {
            _suppressToggles = true;
            _installer = null;
            switch (demo)
            {
                case "installing":
                    Phase = Phase.Installing; _voiceOn = true; _textOn = true;
                    ProgressStatus = "Распаковываю озвучку…"; ProgressFraction = 0.67;
                    ComponentsOpen = true;
                    break;
                case "ready":
                    Phase = Phase.Ready; _voiceOn = true; _textOn = true; ComponentsOpen = true;
                    break;
                case "error":
                    Phase = Phase.Error; ErrorLabel = "ИГРА НЕ НАЙДЕНА"; ErrorDetail = "";
                    GamePath = ""; PathValid = false; PathOpen = true;
                    PathHint = "Автопоиск не нашёл игру. Укажите папку игры.";
                    break;
                case "nothing":
                    Phase = Phase.Install; _voiceOn = false; _textOn = false; ComponentsOpen = true;
                    break;
                case "about":
                    Phase = Phase.Ready; _voiceOn = true; _textOn = true; AboutOpen = true;
                    UpdateStatus = "Доступна версия 1.1.0 (у вас " + Updates.CurrentVersion + ")."; UpdateActionLabel = "Скачать и установить"; UpdateDot = true;
                    break;
                case "update":
                    Phase = Phase.Update; _voiceOn = true; _textOn = true; ComponentsOpen = true;
                    break;
                default:
                    Phase = Phase.Install; _voiceOn = true; _textOn = true; ComponentsOpen = true;
                    break;
            }
            if (Phase != Phase.Error && string.IsNullOrEmpty(GamePath)) { GamePath = @"D:\Games\Project Wingman"; PathValid = true; }
            Raise(nameof(VoiceOn)); Raise(nameof(TextOn));
            RaiseSelectionFlags();
            UpdateComponentTexts();
            _suppressToggles = false;
        }

        // ------------------------------------------------------------------ INotifyPropertyChanged
        private void RaisePhaseFlags()
        {
            foreach (var n in new[] { nameof(IsInstall), nameof(IsInstalling), nameof(IsReady), nameof(IsUpdate), nameof(IsError),
                                      nameof(PrimaryEnabled), nameof(PrimaryLabel), nameof(TogglesEnabled),
                                      nameof(StatusesVisible), nameof(BrowseEnabled), nameof(UpdateActionEnabled),
                                      nameof(NothingSelected), nameof(PlayMode), nameof(ShowPlayIcon), nameof(ShowInstallIcon) })
                Raise(n);
        }

        // ------------------------------------------------------------------ обновления с GitHub
        /// <summary>Проверка последнего релиза. silent — при старте: молча, только точка на «i».</summary>
        public async Task CheckUpdatesAsync(bool silent)
        {
            if (_updateBusy) return;
            _updateBusy = true; Raise(nameof(UpdateActionEnabled));
            if (!silent) UpdateStatus = "Проверяю обновления…";
            try
            {
                var latest = await Updates.FetchLatestAsync(_releasesUrl, CancellationToken.None);
                _latest = latest;
                var cur = Updates.CurrentVersion;
                var v = latest.Version;
                if (v != null && v > cur && latest.Exe != null)
                {
                    UpdateStatus = $"Доступна версия {v} (у вас {cur}).";
                    UpdateActionLabel = "Скачать и установить";
                    UpdateDot = true;
                    AboutOpen = true;          // нашли новую версию — панель открывается сама (решение фаундера)
                }
                else
                {
                    UpdateStatus = $"У вас последняя версия ({cur}).";
                    UpdateActionLabel = "Проверить обновления";
                    UpdateDot = false;
                }
            }
            catch (Exception ex)
            {
                if (!silent) UpdateStatus = "Не удалось проверить обновления: " + Short(ex);
                _latest = null;
            }
            finally { _updateBusy = false; Raise(nameof(UpdateActionEnabled)); }
        }

        /// <summary>Кнопка в панели: либо проверить, либо скачать найденную версию и подменить себя.</summary>
        public async Task UpdateActionAsync()
        {
            var latest = _latest;
            if (latest?.Exe == null || latest.Version == null || latest.Version <= Updates.CurrentVersion)
            {
                await CheckUpdatesAsync(silent: false);
                return;
            }
            if (IsInstalling || _updateBusy) return;
            _updateBusy = true; Raise(nameof(UpdateActionEnabled));
            var prevPhase = Phase;
            _cts = new CancellationTokenSource();
            Phase = Phase.Installing;
            ProgressSource = "GitHub";
            ProgressStatus = $"Скачиваю версию {latest.Version}…"; ProgressFraction = 0;
            try
            {
                var staged = Path.Combine(SelfUpdate.StagingDir, $"PWRuLauncher-{latest.Version}.exe");
                var p = new Progress<double>(f => ProgressFraction = f);
                await Updates.DownloadAsync(latest.Exe.Url, staged, latest.Exe.Size, p, _cts.Token);
                if (latest.Exe.Size > 0 && new FileInfo(staged).Length != latest.Exe.Size)
                    throw new IOException("размер скачанного файла не совпадает");
                if (latest.ExeSha != null)
                {
                    ProgressStatus = "Проверяю подпись…";
                    var expected = Updates.ParseShaText(await Updates.FetchTextAsync(latest.ExeSha.Url, _cts.Token));
                    var actual = Updates.Sha256File(staged);
                    if (expected == null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("sha256 не совпадает с опубликованным");
                }
                ProgressStatus = "Перезапуск…"; ProgressFraction = 1;
                await Task.Delay(300);
                SelfUpdate.SwapAndStart(_exePath, staged, "");
                RequestClose?.Invoke();
                return;
            }
            catch (Exception ex)
            {
                UpdateStatus = "Обновление не удалось: " + Short(ex);
                AboutOpen = true;
            }
            finally
            {
                _cts = null;
                ProgressSource = "Копирование с диска";
                _updateBusy = false; Raise(nameof(UpdateActionEnabled));
            }
            Phase = prevPhase;
            Refresh(keepToggles: true);
        }

        private static string Short(Exception ex)
        {
            var e = ex is AggregateException a && a.InnerException != null ? a.InnerException : ex;
            if (e is System.Net.Http.HttpRequestException || e is System.Net.WebException) return "нет связи с GitHub.";
            if (e is TaskCanceledException) return "истекло время ожидания.";
            return e.Message;
        }

        private void RaiseSelectionFlags()
        {
            foreach (var n in new[] { nameof(NothingSelected), nameof(PlayMode), nameof(PrimaryEnabled), nameof(PrimaryLabel),
                                      nameof(ShowPlayIcon), nameof(ShowInstallIcon) })
                Raise(n);
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name!);
            return true;
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
