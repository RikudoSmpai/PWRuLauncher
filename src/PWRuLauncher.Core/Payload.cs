using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PWRuLauncher.Core
{
    /// <summary>manifest.txt внутри payload: key=value, по строке на ключ.</summary>
    public sealed class Manifest
    {
        public string Version { get; private set; } = "0";
        public string BuildDate { get; private set; } = "";
        public long VoiceSize { get; private set; }
        public long SubsSize { get; private set; }
        /// <summary>Штамп UE4SS-слоя, пишется в PWRU_bundle.info.</summary>
        public long BundleStamp { get; private set; }
        public IReadOnlyList<string> Movies { get; private set; } = Array.Empty<string>();

        public static Manifest Parse(string text)
        {
            var m = new Manifest();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "version": m.Version = val; break;
                    case "build_date": m.BuildDate = val; break;
                    case "voice_size": m.VoiceSize = long.Parse(val, CultureInfo.InvariantCulture); break;
                    case "subs_size": m.SubsSize = long.Parse(val, CultureInfo.InvariantCulture); break;
                    case "bundle_stamp": m.BundleStamp = long.Parse(val, CultureInfo.InvariantCulture); break;
                    case "movies":
                        m.Movies = val.Length == 0 ? Array.Empty<string>() : val.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                        break;
                }
            }
            return m;
        }
    }

    /// <summary>
    /// Бандл модов. В релизе — zip, дописанный в хвост exe (трейлер: 8 байт длины + магия PWRUPAY1).
    /// В разработке — payload.zip рядом с exe или путь из аргумента --payload.
    /// </summary>
    public sealed class Payload : IDisposable
    {
        public const string Magic = "PWRUPAY1";
        public const string VoiceEntry = "voice/PW_RU_Voice.pak";
        public const string SubsEntry = "text/subs/PW_RU_Subs.pak";
        public const string Ue4ssPrefix = "text/ue4ss/";
        public const string MoviesPrefix = "text/ue4ss/Movies/";

        private readonly Stream _stream;
        public ZipArchive Archive { get; }
        public Manifest Manifest { get; }
        public string Source { get; }

        private Payload(Stream stream, string source)
        {
            _stream = stream;
            Source = source;
            Archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var man = Archive.GetEntry("manifest.txt") ?? throw new InvalidDataException("В бандле нет manifest.txt");
            using var r = new StreamReader(man.Open(), Encoding.UTF8);
            Manifest = Manifest.Parse(r.ReadToEnd());
        }

        public static Payload? TryOpen(string exePath, string? overridePath)
        {
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                return new Payload(File.OpenRead(overridePath!), overridePath!);

            var fromExe = OpenAppended(exePath);
            if (fromExe != null) return fromExe;

            var dir = Path.GetDirectoryName(exePath) ?? ".";
            var side = Path.Combine(dir, "payload.zip");
            if (File.Exists(side)) return new Payload(File.OpenRead(side), side);
            return null;
        }

        private static Payload? OpenAppended(string exePath)
        {
            FileStream? fs = null;
            try
            {
                fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < 16) { fs.Dispose(); return null; }
                fs.Seek(-16, SeekOrigin.End);
                var trailer = new byte[16];
                if (fs.Read(trailer, 0, 16) != 16) { fs.Dispose(); return null; }
                if (Encoding.ASCII.GetString(trailer, 8, 8) != Magic) { fs.Dispose(); return null; }
                var len = BitConverter.ToInt64(trailer, 0);
                var start = fs.Length - 16 - len;
                if (len <= 0 || start < 0) { fs.Dispose(); return null; }
                return new Payload(new SubStream(fs, start, len), exePath);
            }
            catch
            {
                fs?.Dispose();
                return null;
            }
        }

        public long TotalBytes(string prefix)
        {
            long total = 0;
            foreach (var e in Archive.Entries)
                if (e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith("/")) total += e.Length;
            return total;
        }

        public void Dispose() { Archive.Dispose(); _stream.Dispose(); }
    }

    /// <summary>Окно чтения внутри большого файла: ZipArchive видит только хвост exe.</summary>
    internal sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start, _length;
        private long _pos;

        public SubStream(Stream inner, long start, long length) { _inner = inner; _start = start; _length = length; _pos = 0; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = Math.Max(0, Math.Min(value, _length)); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remain = _length - _pos;
            if (remain <= 0) return 0;
            if (count > remain) count = (int)remain;
            _inner.Seek(_start + _pos, SeekOrigin.Begin);
            var n = _inner.Read(buffer, offset, count);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => _length + offset,
            };
            Position = target;
            return _pos;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
