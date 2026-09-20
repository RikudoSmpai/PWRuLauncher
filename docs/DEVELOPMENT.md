# Разработка

## Стек

- C# / .NET Framework 4.8 — стоит на любом Windows 10/11, отдельный рантайм не нужен.
- Avalonia 11 на Skia — окно рисуется само, без WebView и WPF.
- Fody.Costura — управляемые сборки вшиты в exe; нативные Skia/HarfBuzz/ANGLE лежат ресурсами
  `native.*.dll` и распаковываются в `%LOCALAPPDATA%\PWRuLauncher\native\` при первом запуске.
- Файлы локализации дописаны в хвост exe одним zip (трейлер: 8 байт длины + `PWRUPAY1`) и читаются
  по смещению, без распаковки во временную папку.

## Структура

```
src/PWRuLauncher.Core   поиск игры (Steam/GOG/типовые папки), чтение бандла, установщик, обновления, запуск
src/PWRuLauncher.App    окно: Установить → Распаковка → Играть / Обновить / Игра не найдена, панель «О программе»
tools/pack.py           сборка payload.zip из паков + UE4SS-zip и дописывание его в exe
tools/fetch_mods.py     скачивание модов по mods/manifest.json со сверкой sha256 (используется CI)
mods/manifest.json      какие файлы локализации вкладываются в релиз (url + sha256)
```

## Сборка у себя

```
dotnet publish src/PWRuLauncher.App -c Release -o dist/raw
python tools/pack.py --voice PW_RU_Voice.pak --subs PW_RU_Subs.pak --ue4ss PW_RU_UE4SS.zip \
    --version 1.0.0 --out dist/payload.zip \
    --exe dist/raw/PWRuLauncher.exe --exe-out dist/PWRuLauncher.exe
```

`PW_RU_UE4SS.zip` — бандл UE4SS-слоя с корнями `Win64/` (UE4SS + Lua-моды), `Movies/` (интро) и,
при необходимости, `LogicMods/`.

## Как лаунчер ложится в игру

- `ProjectWingman\Content\Paks\~mods\PW_RU_Voice.pak`, `PW_RU_Subs.pak`
- `ProjectWingman\Binaries\Win64\` — UE4SS (dwmapi.dll, UE4SS.dll, settings, `Mods\`), маркер
  `Mods\PWRU_bundle.info` (штамп бандла)
- `ProjectWingman\Content\Movies\HumbleIntro_PW.mp4` — интро; оригинал в `.pwru-orig`, маркер
  `PWRU_movies.info` (штамп + список подменённых роликов)

Установлен ли компонент, определяется по размеру файла (паки) и штампу в маркере (UE4SS, интро).
Маркеры совместимы с прежними установками локализации: чужая версия видна как «есть новая версия».
Интро — базовый слой: ставится, пока включён хоть один компонент, снимается, когда выключены оба.
Тумблер после установки — действие: выключил → файлы сняты, оригиналы возвращены; включил → поставлено с диска.

## Обновления и релизы

При старте лаунчер спрашивает `releases/latest`; если есть версия новее, панель «О программе» открывается
сама с кнопкой «Скачать и установить», а на кнопке «i» остаётся точка. Новый exe скачивается в
`%LOCALAPPDATA%\PWRuLauncher\update\`, сверяется с `PWRuLauncher.exe.sha256` из релиза, текущий exe
переименовывается в `.old`, новый встаёт на его место и запускается с `--updated-from`, после чего
убирает `.old`. Новая версия видит в игре файлы прошлой и показывает «Обновить».

Версия лаунчера = версия релиза. Релиз собирает GitHub Actions по тегу `vX.Y.Z`
(`.github/workflows/release.yml`): скачивает файлы локализации по `mods/manifest.json`, сверяет sha256,
собирает exe, дописывает бандл и публикует релиз с `PWRuLauncher.exe`, `PWRuLauncher.exe.sha256` и
самими файлами локализации. Новые файлы локализации = новый манифест + новый тег.

## Ключи для разработки

```
--payload path\to\payload.zip       бандл не из exe, а из файла
--game "D:\Games\Project Wingman"   папка игры вместо автопоиска
--demo install|installing|ready|update|error|nothing|about
                                    показать состояние окна, диск не трогается
--shot out.png                      окно рендерит себя в файл и закрывается
--no-update-check                   не ходить на GitHub при старте
--releases-url http://127.0.0.1:8765/latest.json
                                    другой источник релизов (тесты на имитации GitHub)
--selftest "<папка игры>" [--payload ...]
                                    сквозной прогон установщика без окна → PWRuLauncher.selftest.log
--selftest-install "<папка игры>"   поставить всё и выйти
--selftest-update <latest.json> [--swap <аргументы новому exe>]
                                    проверка/скачивание/sha256/подмена без окна → PWRuLauncher.update.log
```
