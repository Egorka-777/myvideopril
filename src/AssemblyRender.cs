using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public sealed class AssemblyRenderReport {
        public string Encoder = "", RequestedMode = "", Fallback = "";
        public int Width, Height, Fps, EncoderThreads, FilterThreads, RasterCacheHits;
        public double Duration, RasterSeconds, EncoderCheckSeconds, EncodeSeconds, ValidationSeconds, TotalSeconds;
        public int EncodePasses = 1, EncodeAttempts;
    }
    // One context per batch. No parallel jobs compete for memory or consume history twice.
    public sealed class AssemblyRenderContext {
        internal string Encoder;
        internal string CacheDirectory;
        internal readonly Dictionary<string, string> Rasters = new Dictionary<string, string>();
        public AssemblyRenderReport LastReport;
        public int EncoderThreads => Math.Min(8, Math.Max(1, Environment.ProcessorCount - 1));
        public int FilterThreads => Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
        internal static string RasterKey(AssemblyTemplate t, AssemblyLayerPlan p) {
            var l = p.Layer;
            return AssemblyFiles.HashText(string.Join("|", t.Width, t.Height, p.Asset.Hash, Path.GetFullPath(p.Asset.Path),
                Core.N(l.X), Core.N(l.Y), Core.N(l.Width), Core.N(l.Height), Core.N(l.Opacity), l.Font,
                Core.N(l.FontSize), Core.N(l.Radius), Core.N(l.Padding)));
        }
    }
    public static class AssemblyRender {
        public static List<string> EncoderArguments(string encoder, int threads) {
            var args = new List<string> { "-c:v", encoder };
            if (encoder == "h264_nvenc") args.AddRange(new[] { "-preset", "p4", "-rc", "vbr", "-cq", "20", "-b:v", "0" });
            else if (encoder == "h264_qsv") args.AddRange(new[] { "-preset", "fast", "-global_quality", "20" });
            else if (encoder == "h264_amf") args.AddRange(new[] { "-quality", "speed", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20" });
            else args.AddRange(new[] { "-preset", "veryfast", "-crf", "20", "-threads", threads.ToString() });
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            return args;
        }
        // Image inputs contain one frame. overlay repeats that frame, so PNG is not
        // decoded 30 times per second. All layers are composited BEFORE the transition.
        public static List<string> Arguments(AssemblyTemplate t, AssemblyPlan p, string encoder, string script, string output, AssemblyRenderContext context) {
            if (p.Scenes.Count == 0) throw new Exception("Добавь хотя бы одну сцену перед сборкой.");
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", context.FilterThreads.ToString() };
            var graph = new List<string>(); int input = 0;
            bool transitions = p.Scenes.Count > 1 && t.Transition != "cut" && t.TransitionDuration > 0;
            for (int i = 0; i < p.Scenes.Count; i++) {
                var s = p.Scenes[i]; int video = input++;
                if (t.LoopShortVideos && s.Video.Duration + .001 < s.RenderDuration) args.AddRange(new[] { "-stream_loop", "-1" });
                args.AddRange(new[] { "-threads", "1", "-ss", Core.N(s.VideoStart), "-t", Core.N(s.RenderDuration), "-i", s.Video.Path });
                string current = "base" + i;
                graph.Add("[" + video + ":v:0]setpts=PTS-STARTPTS,fps=" + t.Fps + ",scale=" + t.Width + ":" + t.Height
                    + ":force_original_aspect_ratio=increase,crop=" + t.Width + ":" + t.Height + ",setsar=1,format=yuv420p,trim=duration=" + Core.N(s.RenderDuration) + "[" + current + "]");
                for (int j = 0; j < s.Layers.Count; j++) {
                    var layer = s.Layers[j]; int picture = input++;
                    args.AddRange(new[] { "-threads", "1", "-framerate", t.Fps.ToString(), "-i", layer.Raster });
                    string next = "overlay" + i + "_" + j;
                    double end = layer.Layer.End == 0 || Math.Abs(layer.Layer.End - s.Scene.Duration) < .001 ? s.RenderDuration : layer.Layer.End;
                    graph.Add("[" + current + "][" + picture + ":v:0]overlay=0:0:format=yuv420:repeatlast=1:eof_action=repeat:enable='between(t,"
                        + Core.N(layer.Layer.Start) + "," + Core.N(end) + ")'[" + next + "]"); current = next;
                }
                graph.Add("[" + current + "]settb=1/" + t.Fps + ",setpts=PTS-STARTPTS,format=" + (transitions ? "yuv444p" : "yuv420p") + "[s" + i + "]");
            }
            string joined;
            if (!transitions) {
                joined = "joined";
                graph.Add(string.Join("", Enumerable.Range(0, p.Scenes.Count).Select(i => "[s" + i + "]")) + "concat=n=" + p.Scenes.Count + ":v=1:a=0[" + joined + "]");
            } else {
                joined = "s0"; double offset = 0;
                for (int i = 1; i < p.Scenes.Count; i++) {
                    offset += p.Scenes[i - 1].Scene.Duration; string next = "joined" + i;
                    graph.Add("[" + joined + "][s" + i + "]xfade=" + AssemblyEngine.TransitionFilter(t.Transition) + ":duration=" + Core.N(t.TransitionDuration)
                        + ":offset=" + Core.N(offset) + "[" + next + "]"); joined = next;
                }
            }
            graph.Add("[" + joined + "]format=yuv420p[vout]");
            bool music = !string.IsNullOrWhiteSpace(p.Music);
            if (music) {
                args.AddRange(new[] { "-stream_loop", "-1", "-i", p.Music });
                double fade = Math.Min(.2, t.Duration / 4);
                graph.Add("[" + input + ":a:0]asetpts=PTS-STARTPTS,volume=" + Core.N(t.MusicVolume) + ",afade=t=in:d=" + Core.N(fade)
                    + ",afade=t=out:st=" + Core.N(t.Duration - fade) + ":d=" + Core.N(fade) + ",apad,atrim=duration=" + Core.N(t.Duration) + "[audio]");
            }
            File.WriteAllText(script, string.Join(";\n", graph), new UTF8Encoding(false));
            args.AddRange(new[] { "-filter_complex_script", script, "-map", "[vout]" });
            if (music) args.AddRange(new[] { "-map", "[audio]", "-c:a", "aac", "-b:a", "192k" }); else args.Add("-an");
            args.AddRange(EncoderArguments(encoder, context.EncoderThreads));
            args.AddRange(new[] { "-t", Core.N(t.Duration), "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output });
            return args;
        }
        static async Task SelectEncoder(AssemblyTemplate t, string ffmpeg, string work, AssemblyRenderContext context, CancellationToken ct) {
            if (context.Encoder != null) return;
            context.Encoder = "libx264";
            if (t.RenderMode == "cpu") return;
            foreach (string encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" }) {
                ct.ThrowIfCancellationRequested(); string file = Path.Combine(work, "encoder-check.mp4");
                try {
                    var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i",
                        "color=c=black:s=" + t.Width + "x" + t.Height + ":r=" + t.Fps + ",format=yuv420p", "-frames:v", "2", "-an" };
                    args.AddRange(EncoderArguments(encoder, context.EncoderThreads)); args.Add(file);
                    await Core.Tool(ffmpeg, args, ct, null, 12).ConfigureAwait(false);
                    if (File.Exists(file) && new FileInfo(file).Length > 0) { context.Encoder = encoder; return; }
                } catch (OperationCanceledException) { throw; } catch { /* unavailable driver, encoder or dimensions: try the next device */ }
                finally { if (File.Exists(file)) File.Delete(file); }
            }
        }
        public static async Task Run(AssemblyTemplate t, AssemblyPlan p, string ffmpeg, string probe, string output, string work,
            Action<AssemblyTemplate, AssemblyLayerPlan, string> rasterize, CancellationToken ct, Action<string> progress,
            AssemblyRenderContext context = null, Action<double> fraction = null) {
            context = context ?? new AssemblyRenderContext(); var total = Stopwatch.StartNew(); var stage = Stopwatch.StartNew();
            var report = new AssemblyRenderReport { Width = t.Width, Height = t.Height, Fps = t.Fps, Duration = t.Duration,
                RequestedMode = t.RenderMode, EncoderThreads = context.EncoderThreads, FilterThreads = context.FilterThreads };
            Directory.CreateDirectory(work);
            string cache = context.CacheDirectory ?? Path.Combine(work, "raster-cache"); Directory.CreateDirectory(cache);
            progress?.Invoke("Готовлю изображения…");
            foreach (var scene in p.Scenes) foreach (var layer in scene.Layers) {
                ct.ThrowIfCancellationRequested(); string key = AssemblyRenderContext.RasterKey(t, layer);
                if (context.Rasters.TryGetValue(key, out string cached) && File.Exists(cached)) { layer.Raster = cached; report.RasterCacheHits++; continue; }
                string path = Path.Combine(cache, key + ".png");
                try { rasterize(t, layer, path); } catch (Exception e) { throw new Exception("Не удалось прочитать картинку «" + Path.GetFileName(layer.Asset.Path) + "»: " + e.Message, e); }
                layer.Raster = path; context.Rasters[key] = path;
            }
            report.RasterSeconds = stage.Elapsed.TotalSeconds; stage.Restart();
            progress?.Invoke("Проверяю ускорение…");
            await SelectEncoder(t, ffmpeg, work, context, ct).ConfigureAwait(false);
            report.EncoderCheckSeconds = stage.Elapsed.TotalSeconds; stage.Restart();
            async Task Encode() {
                report.Encoder = context.Encoder; report.EncodeAttempts++;
                progress?.Invoke("Собираю · " + (context.Encoder == "libx264" ? "процессор" : "видеокарта"));
                var args = Arguments(t, p, context.Encoder, Path.Combine(work, "render.ffgraph"), output, context);
                await Core.Tool(ffmpeg, args, ct, line => {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) fraction?.Invoke(Math.Min(.99, Math.Max(0, Core.Parse(line.Substring(12)) / 1000000 / t.Duration)));
                }, 3600).ConfigureAwait(false);
            }
            try { await Encode().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (context.Encoder != "libx264" && t.RenderMode == "auto") {
                report.Fallback = context.Encoder + ": " + e.Message;
                context.Encoder = "libx264"; fraction?.Invoke(0); progress?.Invoke("Видеокарта недоступна. Продолжаю на процессоре…");
                await Encode().ConfigureAwait(false);
            }
            report.EncodeSeconds = stage.Elapsed.TotalSeconds; stage.Restart(); ct.ThrowIfCancellationRequested();
            var m = Core.VideoInfo(await Core.Probe(probe, output, ct).ConfigureAwait(false));
            if (Math.Abs(m.VideoDuration - t.Duration) > .15 || m.Width != t.Width || m.Height != t.Height) throw new Exception("Готовый ролик не прошёл проверку длительности или размера кадра.");
            if (!string.IsNullOrWhiteSpace(p.Music) && !m.HasAudio) throw new Exception("В готовом ролике отсутствует музыка.");
            report.ValidationSeconds = stage.Elapsed.TotalSeconds; report.TotalSeconds = total.Elapsed.TotalSeconds; context.LastReport = report;
            fraction?.Invoke(1); progress?.Invoke("Готово за " + report.TotalSeconds.ToString("0.0", Core.Inv) + " с · " + report.Encoder);
        }
    }
}
