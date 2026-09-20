#!/usr/bin/env python3
"""Забирает файлы локализации по mods/manifest.json и сверяет sha256 — то, что попадёт в exe релиза.

    python tools/fetch_mods.py mods/manifest.json dist/mods

Манифест:
    { "source": {"tag": "mods-...", "release_id": 123},
      "voice": {"name": "PW_RU_Voice.pak", "asset_id": 456, "sha256": "...", "size": 1},
      "subs": {...}, "ue4ss": {...} }
Файлы лежат ассетами черновика релиза (снаружи не виден), поэтому скачивание идёт через API
под токеном из GITHUB_TOKEN. Редирект на хранилище проходится без заголовка Authorization —
иначе хранилище отвечает «Only one auth mechanism allowed».
Для сборки у себя вместо asset_id можно указать "url": обычный путь или https-ссылка.
"""
import hashlib, json, os, shutil, sys, urllib.error, urllib.parse, urllib.request

NAMES = {"voice": "PW_RU_Voice.pak", "subs": "PW_RU_Subs.pak", "ue4ss": "PW_RU_UE4SS.zip"}
API = os.environ.get("GITHUB_API_URL", "https://api.github.com").rstrip("/")
REPO = os.environ.get("GITHUB_REPOSITORY", "RikudoSmpai/PWRuLauncher")
TOKEN = os.environ.get("GITHUB_TOKEN", "")
UA = "PWRuLauncher-release/1.0"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def download_plain(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req) as r, open(dest, "wb") as f:
        shutil.copyfileobj(r, f)


def download_asset(asset_id, dest):
    if not TOKEN:
        sys.exit("нужен GITHUB_TOKEN: файлы лежат в черновике релиза")
    url = f"{API}/repos/{REPO}/releases/assets/{asset_id}"
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/octet-stream",
                                               "Authorization": "Bearer " + TOKEN,
                                               "X-GitHub-Api-Version": "2022-11-28"})
    opener = urllib.request.build_opener(NoRedirect)
    try:
        with opener.open(req) as r:              # без редиректа GitHub сам отдал тело
            with open(dest, "wb") as f:
                shutil.copyfileobj(r, f)
            return
    except urllib.error.HTTPError as e:
        if e.code not in (301, 302, 303, 307, 308):
            raise
        location = e.headers.get("Location")
    if not location:
        sys.exit(f"asset {asset_id}: редирект без Location")
    download_plain(location, dest)               # хранилище: подписанная ссылка, без Authorization


def main():
    manifest, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    spec = json.load(open(manifest, encoding="utf-8"))
    for key, name in NAMES.items():
        entry = spec[key]
        dest = os.path.join(out, name)
        if entry.get("asset_id"):
            download_asset(entry["asset_id"], dest)
        else:
            src = entry["url"]
            if src.startswith(("http://", "https://")):
                download_plain(src, dest)
            else:
                shutil.copyfile(src[7:] if src.startswith("file://") else src, dest)
        got = sha256(dest)
        if got != entry["sha256"].lower():
            sys.exit(f"{name}: sha256 mismatch\n  expected {entry['sha256']}\n  got      {got}")
        print(f"{name}: {os.path.getsize(dest)} bytes, sha256 ok")


if __name__ == "__main__":
    main()
