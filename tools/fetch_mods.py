#!/usr/bin/env python3
"""Скачивает моды по mods/manifest.json и сверяет sha256 — то, что попадёт в exe релиза.

    python tools/fetch_mods.py mods/manifest.json dist/mods

manifest.json:
    { "voice": {"url": "...", "sha256": "..."}, "subs": {...}, "ue4ss": {...} }
Локальный путь вместо url тоже принимается (file:///... или обычный путь) — для сборки у себя.
"""
import hashlib, json, os, shutil, sys, urllib.request

NAMES = {"voice": "PW_RU_Voice.pak", "subs": "PW_RU_Subs.pak", "ue4ss": "PW_RU_UE4SS.zip"}

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

def main():
    manifest, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    spec = json.load(open(manifest, encoding="utf-8"))
    for key, name in NAMES.items():
        entry = spec[key]
        dest = os.path.join(out, name)
        src = entry["url"]
        if src.startswith(("http://", "https://")):
            req = urllib.request.Request(src, headers={"User-Agent": "PWRuLauncher-release/1.0"})
            with urllib.request.urlopen(req) as r, open(dest, "wb") as f:
                shutil.copyfileobj(r, f)
        else:
            shutil.copyfile(src[7:] if src.startswith("file://") else src, dest)
        got = sha256(dest)
        if got != entry["sha256"].lower():
            sys.exit(f"{name}: sha256 mismatch\n  expected {entry['sha256']}\n  got      {got}")
        print(f"{name}: {os.path.getsize(dest)} bytes, sha256 ok")

if __name__ == "__main__":
    main()
