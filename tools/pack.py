#!/usr/bin/env python3
"""Собирает payload (бандл модов) и, если задан exe, дописывает его в хвост exe.

    python tools/pack.py --voice PW_RU_Voice.pak --subs PW_RU_Subs.pak --ue4ss PW_RU_UE4SS.zip \
        --version 1.0.0 --out payload.zip [--exe dist/PWRuLauncher.exe]

Формат payload.zip:
    manifest.txt                 key=value
    voice/PW_RU_Voice.pak
    text/subs/PW_RU_Subs.pak
    text/ue4ss/Win64/**          (из PW_RU_UE4SS.zip как есть)
    text/ue4ss/Movies/**
    text/ue4ss/LogicMods/**      (если есть)
Трейлер в exe: 8 байт длины payload (LE) + b"PWRUPAY1".
"""
import argparse, datetime, os, struct, zipfile

MAGIC = b"PWRUPAY1"

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--voice", required=True)
    ap.add_argument("--subs", required=True)
    ap.add_argument("--ue4ss", required=True, help="PW_RU_UE4SS.zip (корни Win64/, Movies/, LogicMods/)")
    ap.add_argument("--version", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--exe", help="если задан: скопировать exe в --exe-out и дописать payload в хвост")
    ap.add_argument("--exe-out")
    a = ap.parse_args()

    ue = zipfile.ZipFile(a.ue4ss)
    movies = sorted(n.split("/", 1)[1] for n in ue.namelist() if n.startswith("Movies/") and not n.endswith("/"))
    manifest = "\n".join([
        f"version={a.version}",
        f"build_date={datetime.datetime.now():%Y-%m-%d}",
        f"voice_size={os.path.getsize(a.voice)}",
        f"subs_size={os.path.getsize(a.subs)}",
        f"bundle_stamp={os.path.getsize(a.ue4ss)}",
        f"movies={';'.join(movies)}",
    ]) + "\n"

    # паки и mp4 уже сжаты — кладём без сжатия, чтобы читать с диска напрямую
    with zipfile.ZipFile(a.out, "w", zipfile.ZIP_STORED) as z:
        z.writestr("manifest.txt", manifest)
        z.write(a.voice, "voice/PW_RU_Voice.pak")
        z.write(a.subs, "text/subs/PW_RU_Subs.pak")
        for info in ue.infolist():
            if info.filename.endswith("/"):
                continue
            z.writestr("text/ue4ss/" + info.filename, ue.read(info.filename))
    size = os.path.getsize(a.out)
    print(f"payload: {a.out} {size} bytes; {len(movies)} movie(s); manifest:\n{manifest}")

    if a.exe:
        out = a.exe_out or a.exe
        data = open(a.exe, "rb").read()
        # уже есть трейлер — срезаем старый payload
        if data[-8:] == MAGIC:
            (plen,) = struct.unpack("<q", data[-16:-8])
            data = data[: len(data) - 16 - plen]
        with open(out, "wb") as f:
            f.write(data)
            f.write(open(a.out, "rb").read())
            f.write(struct.pack("<q", size) + MAGIC)
        print(f"exe: {out} {os.path.getsize(out)} bytes")

if __name__ == "__main__":
    main()
