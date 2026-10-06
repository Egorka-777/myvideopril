using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public sealed class LivePlaylist : IDisposable {
        public string DirectoryPath, Path;
        public double Duration;
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
    public static class YouTubeLiveMedia {
        public static string ConcatEntry(string path) => "file '" + path.Replace('\\', '/').Replace("'", "'\\''") + "'";
        public static IEnumerable<string> NormalizeArguments(string input, string output, int height, bool hasAudio) {
            var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y", "-i", input };
            if (!hasAudio) args.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000" });
            int width = height == 1080 ? 1920 : 1280;
            string rate = height == 1080 ? "6000k" : "4000k";
            args.AddRange(new[] { "-map", "0:v:0", "-map", hasAudio ? "0:a:0" : "1:a:0", "-vf",
                "scale=" + width + ":" + height + ":force_original_aspect_ratio=decrease,pad=" + width + ":" + height + ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-b:v", rate, "-minrate", rate, "-maxrate", rate,
                "-bufsize", height == 1080 ? "12000k" : "8000k", "-g", "60", "-keyint_min", "60", "-sc_threshold", "0",
                "-x264-params", "nal-hrd=cbr:force-cfr=1", "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
                "-af", "aresample=async=1:first_pts=0,apad", "-shortest", "-map_metadata", "-1", "-f", "mpegts", output });
            return args;
        }
        public static async Task<LivePlaylist> Prepare(LiveOptions options, string ffmpeg, string ffprobe, Action<string> log, CancellationToken ct) {
            var result = new LivePlaylist { DirectoryPath = System.IO.Path.Combine(Store.Root, "live-media", Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(result.DirectoryPath);
            try {
                string[] files = options.Files();
                var entries = new List<string> { "ffconcat version 1.0" };
                for (int i = 0; i < files.Length; i++) {
                    ct.ThrowIfCancellationRequested();
                    var info = Core.VideoInfo(await Core.Probe(ffprobe, files[i], ct).ConfigureAwait(false));
                    log("Подготовка " + (i + 1) + "/" + files.Length + ": " + System.IO.Path.GetFileName(files[i]));
                    string output = System.IO.Path.Combine(result.DirectoryPath, i.ToString("D5") + ".ts");
                    await Core.Tool(ffmpeg, NormalizeArguments(files[i], output, options.Height, info.HasAudio), ct).ConfigureAwait(false);
                    var prepared = Core.VideoInfo(await Core.Probe(ffprobe, output, ct).ConfigureAwait(false));
                    result.Duration += prepared.Duration;
                    entries.Add(ConcatEntry(output));
                }
                result.Path = System.IO.Path.Combine(result.DirectoryPath, "playlist.ffconcat");
                File.WriteAllLines(result.Path, entries, new UTF8Encoding(false));
                return result;
            } catch { result.Dispose(); throw; }
        }
        public static string[] StreamArguments(LivePlaylist playlist, bool loop, double offset, string ingestion) {
            var args = new List<string> { "-hide_banner", "-v", "warning", "-re" };
            if (loop) args.AddRange(new[] { "-stream_loop", "-1" });
            if (offset > 0) args.AddRange(new[] { "-ss", offset.ToString("0.###", CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", playlist.Path, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy",
                "-bsf:a", "aac_adtstoasc", "-progress", "pipe:1", "-nostats", "-rw_timeout", "15000000", "-f", "flv", ingestion });
            return args.ToArray();
        }
    }
    public interface ILiveEncoder : IDisposable {
        bool HasExited { get; }
        int ExitCode { get; }
        double Seconds { get; }
        DateTime LastProgress { get; }
        string Error { get; }
        Task Stop();
    }
    public sealed class LiveEncoder : ILiveEncoder {
        readonly Process process;
        readonly Task output, error;
        readonly object gate = new object();
        readonly Queue<string> errors = new Queue<string>();
        double seconds;
        DateTime lastProgress = DateTime.UtcNow;
        public double Seconds { get { lock (gate) return seconds; } }
        public DateTime LastProgress { get { lock (gate) return lastProgress; } }
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public string Error { get { lock (gate) return string.Join(" | ", errors); } }
        public LiveEncoder(string ffmpeg, string[] arguments, string ingestion) {
            process = new Process { StartInfo = new ProcessStartInfo(ffmpeg, string.Join(" ", arguments.Select(Core.Quote))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
            if (!process.Start()) { process.Dispose(); throw new InvalidOperationException("Не удалось запустить передачу видео."); }
            output = Task.Run(async () => {
                string line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                    if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) continue;
                    double value;
                    if (double.TryParse(line.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) lock (gate) {
                        value /= 1000000; if (value > seconds) { seconds = value; lastProgress = DateTime.UtcNow; }
                    }
                }
            });
            error = Task.Run(async () => {
                string line;
                string key = ingestion.Substring(ingestion.LastIndexOf('/') + 1);
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) {
                    line = line.Replace(ingestion, "[stream]"); if (!string.IsNullOrEmpty(key)) line = line.Replace(key, "[key]");
                    line = System.Text.RegularExpressions.Regex.Replace(line, @"rtmps?://\S+", "[stream]");
                    lock (gate) { if (errors.Count == 12) errors.Dequeue(); errors.Enqueue(line.Substring(0, Math.Min(300, line.Length))); }
                }
            });
        }
        public async Task Stop() {
            if (!process.HasExited) {
                try { await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false); }
                catch (IOException) { }
                if (!await Task.Run(() => process.WaitForExit(5000)).ConfigureAwait(false)) { process.Kill(); await Task.Run(() => process.WaitForExit()).ConfigureAwait(false); }
            }
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        public void Dispose() { process.Dispose(); }
    }
}
