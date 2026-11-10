using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class AssemblyOverlayMedia {
        sealed class Metadata {
            public long Length, Modified;
            public Size Size;
            public string Codec;
            public double Duration;
        }
        static readonly Dictionary<string, Metadata> Cache = new Dictionary<string, Metadata>(StringComparer.OrdinalIgnoreCase);
        public static bool IsAnimated(string path) {
            string extension = Path.GetExtension(path ?? "").ToLowerInvariant();
            return extension == ".mov" || extension == ".webm" || extension == ".gif" || extension == ".mp4";
        }
        static void Tools(out string ffmpeg, out string probe) {
            if (!ToolsLocator.TryResolve(out ffmpeg, out probe, out string hint)) throw new Exception(hint);
        }
        static Metadata Info(string path) {
            var file = new FileInfo(Path.GetFullPath(path));
            lock (Cache) {
                if (Cache.TryGetValue(file.FullName, out Metadata cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc.Ticks) return cached;
                Tools(out _, out string probe);
                var raw = Core.Probe(probe, file.FullName, CancellationToken.None).GetAwaiter().GetResult();
                var video = (raw.streams ?? new ProbeStream[0]).FirstOrDefault(v => v.codec_type == "video" && (v.disposition == null || v.disposition.attached_pic != 1));
                if (video == null || video.width <= 0 || video.height <= 0) throw new Exception("В файле нет изображения: " + path);
                int width = video.width, height = video.height;
                double rotation = (video.side_data_list ?? new SideData[0]).Select(s => s.rotation).FirstOrDefault();
                if (video.tags != null && video.tags.ContainsKey("rotate")) rotation = Core.Parse(video.tags["rotate"]);
                if (Math.Abs(rotation) % 180 == 90) { int swap = width; width = height; height = swap; }
                double duration = Core.Parse(video.duration);
                if (duration <= 0 && raw.format != null) duration = Core.Parse(raw.format.duration);
                var result = new Metadata { Length = file.Length, Modified = file.LastWriteTimeUtc.Ticks, Size = new Size(width, height), Codec = video.codec_name, Duration = duration };
                Cache[file.FullName] = result;
                return result;
            }
        }
        public static Size Size(string path) {
            if (IsAnimated(path)) return Info(path).Size;
            using (var image = Image.FromFile(path)) return image.Size;
        }
        public static double Duration(string path) {
            double seconds = Info(path).Duration;
            if (seconds <= 0) throw new Exception("Не удалось определить длительность анимации: " + path);
            return seconds;
        }
        public static double PlaybackLength(AssemblyLayer layer, string path, double available) {
            return layer.PlaybackMode == "continuous" ? available : Math.Min(available, IsAnimated(path) ? Duration(path) : layer.PhotoDuration);
        }
        public static IEnumerable<string> DecoderArguments(string path) {
            // Native VP8/VP9 decoders discard the WebM alpha side stream.
            if (!Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase)) return new string[0];
            string codec = Info(path).Codec;
            return codec == "vp9" ? new[] { "-c:v", "libvpx-vp9" } : codec == "vp8" ? new[] { "-c:v", "libvpx" } : new string[0];
        }
        internal static string GreenScreenFilter(string label) {
            // colorkey replaces alpha, so multiply its mask by the source alpha.
            return "split=3[" + label + "Rgb][" + label + "Original][" + label + "Key];"
                + "[" + label + "Original]alphaextract[" + label + "Alpha];"
                + "[" + label + "Key]colorkey=0x00ff00:0.3:0.1,alphaextract[" + label + "Mask];"
                + "[" + label + "Alpha][" + label + "Mask]blend=all_expr='A*B/255'[" + label + "Combined];"
                + "[" + label + "Rgb][" + label + "Combined]alphamerge,format=rgba";
        }
        public static void ApplyGreenScreen(Bitmap image) {
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try {
                var row = new byte[image.Width * 4];
                for (int y = 0; y < image.Height; y++) {
                    IntPtr address = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(address, row, 0, row.Length);
                    for (int x = 0; x < row.Length; x += 4) {
                        double blue = row[x], green = row[x + 1] - 255, red = row[x + 2];
                        double distance = Math.Sqrt((red * red + green * green + blue * blue) / (3 * 255.0 * 255.0));
                        double mask = Math.Min(1, Math.Max(0, (distance - .3) / .1));
                        // FFmpeg quantizes the colorkey mask before multiplying it.
                        row[x + 3] = (byte)(row[x + 3] * (int)(mask * 255) / 255);
                    }
                    Marshal.Copy(row, 0, address, row.Length);
                }
            } finally { image.UnlockBits(data); }
        }
        public static Bitmap LoadFrame(string path, double seconds = 0, bool greenScreen = false) {
            if (!IsAnimated(path)) {
                using (var image = Image.FromFile(path)) {
                    var frame = new Bitmap(image);
                    if (greenScreen) ApplyGreenScreen(frame);
                    return frame;
                }
            }
            var metadata = Info(path);
            Tools(out string ffmpeg, out _);
            seconds = Math.Max(0, seconds);
            if (metadata.Duration > 0) seconds %= metadata.Duration;
            var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-threads", "1" };
            args.AddRange(DecoderArguments(path));
            args.AddRange(new[] { "-i", path, "-ss", Core.N(seconds), "-map", "0:v:0", "-frames:v", "1", "-an", "-vf", "format=rgba" + (greenScreen ? "," + GreenScreenFilter("previewKey") : ""), "-c:v", "png", "-f", "image2pipe", "pipe:1" });
            var start = new ProcessStartInfo(ffmpeg, string.Join(" ", args.Select(Core.Quote))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var process = new Process { StartInfo = start })
            using (var bytes = new MemoryStream()) {
                if (!process.Start()) throw new Exception("Не удалось запустить обработчик изображения.");
                var error = process.StandardError.ReadToEndAsync();
                var copy = process.StandardOutput.BaseStream.CopyToAsync(bytes);
                if (!Task.WhenAll(copy, error).Wait(TimeSpan.FromSeconds(45))) {
                    process.Kill();
                    throw new Exception("Обработчик изображения не ответил вовремя.");
                }
                process.WaitForExit();
                if (process.ExitCode != 0 || bytes.Length == 0) throw new Exception("Не удалось прочитать кадр «" + Path.GetFileName(path) + "»: " + error.GetAwaiter().GetResult());
                bytes.Position = 0;
                using (var image = Image.FromStream(bytes)) return new Bitmap(image);
            }
        }
    }
}
