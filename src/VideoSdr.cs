using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    /// <summary>HDR sources are converted to cached SDR proxies automatically (no dialogs).</summary>
    public static class VideoSdr {
        public static bool IsHdr(ProbeResult raw) {
            var v = (raw?.streams ?? new ProbeStream[0]).FirstOrDefault(x => x.codec_type == "video" && (x.disposition == null || x.disposition.attached_pic != 1));
            if (v == null) return false;
            string t = (v.color_transfer ?? "").Trim();
            return t.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) || t.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
        }

        static string CacheRoot => Path.Combine(Store.Root, "assembly-sdr-cache");

        static string CachePath(string source) {
            var info = new FileInfo(source);
            string key = AssemblyFiles.HashText(Path.GetFullPath(source) + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
            return Path.Combine(CacheRoot, key + ".mp4");
        }

        static bool CacheFresh(string source, string cache) {
            if (!File.Exists(cache)) return false;
            string marker = cache + ".source.txt";
            if (!File.Exists(marker)) return false;
            try {
                var lines = File.ReadAllLines(marker);
                var info = new FileInfo(source);
                return lines.Length >= 2
                    && string.Equals(lines[0], Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)
                    && lines[1] == info.LastWriteTimeUtc.Ticks.ToString();
            } catch { return false; }
        }

        public static async Task<string> EnsureReady(string ffmpeg, string ffprobe, string source, CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return source;
            var raw = await Core.Probe(ffprobe, source, ct).ConfigureAwait(false);
            if (!IsHdr(raw)) return source;
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) throw new Exception("HDR-видео «" + Path.GetFileName(source) + "» требует ffmpeg для автоматической конвертации в SDR.");
            Directory.CreateDirectory(CacheRoot);
            string cache = CachePath(source);
            if (CacheFresh(source, cache)) return cache;
            string temp = cache + ".part-" + Guid.NewGuid().ToString("N") + ".mp4";
            try {
                await Transcode(ffmpeg, ffprobe, source, temp, ct).ConfigureAwait(false);
                if (File.Exists(cache)) File.Delete(cache);
                File.Move(temp, cache);
                File.WriteAllLines(cache + ".source.txt", new[] { Path.GetFullPath(source), new FileInfo(source).LastWriteTimeUtc.Ticks.ToString() });
                return cache;
            } finally {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        static async Task Transcode(string ffmpeg, string ffprobe, string source, string dest, CancellationToken ct) {
            string[] filters = {
                "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p",
                "tonemap=tonemap=hable:desat=0,format=yuv420p",
                "format=yuv420p"
            };
            Exception last = null;
            foreach (string vf in filters) {
                try {
                    await Core.Tool(ffmpeg, new[] {
                        "-y", "-hide_banner", "-nostdin", "-loglevel", "error", "-i", source,
                        "-vf", vf, "-c:v", "libx264", "-preset", "fast", "-crf", "18", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", dest
                    }, ct, null, 0).ConfigureAwait(false);
                    if (!IsHdr(await Core.Probe(ffprobe, dest, ct).ConfigureAwait(false))) return;
                    last = new Exception("Конвертация HDR не дала SDR.");
                } catch (Exception e) { last = e; }
            }
            throw new Exception("Не удалось автоматически перевести «" + Path.GetFileName(source) + "» в SDR.", last);
        }

        public static async Task WarmFolder(string ffmpeg, string ffprobe, string folder, CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            foreach (string path in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)) {
                ct.ThrowIfCancellationRequested();
                string ext = Path.GetExtension(path);
                if (ext == null || !AssemblyFiles.VideoExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase))) continue;
                try {
                    var raw = await Core.Probe(ffprobe, path, ct).ConfigureAwait(false);
                    if (IsHdr(raw)) await EnsureReady(ffmpeg, ffprobe, path, ct).ConfigureAwait(false);
                } catch { /* best effort */ }
            }
        }
    }
}
