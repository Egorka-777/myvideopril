using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public class AssemblyInsertion {
        public string Source = "", VisualSource = "";
        public double VisualOffset;
        public bool VisualPlacementConfigured, VisualPreserveAspectRatio = true;
        public double VisualX, VisualY, VisualWidth = 1, VisualHeight = 1;
        public bool RandomTime, RandomWholeVideo;
        public double Start, End;
        public double ImageDuration = 5;
        public AssemblyInsertion Copy() => (AssemblyInsertion)MemberwiseClone();
    }

    public static class AssemblyInsertions {
        public static List<AssemblyInsertion> Copy(List<AssemblyInsertion> source) =>
            source == null ? new List<AssemblyInsertion>() : source.Select(i => i.Copy()).ToList();

        internal static bool IsImage(string path) => AssemblyFiles.PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && !AssemblyFiles.VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) && !AssemblyOverlayMedia.IsAnimated(path);

        internal static double AppearanceTime(AssemblyInsertion insertion, double mainDuration, Random random) {
            if (insertion.RandomWholeVideo) return random.NextDouble() * mainDuration;
            if (double.IsNaN(insertion.Start) || double.IsInfinity(insertion.Start) || insertion.Start < 0 ||
                insertion.RandomTime && (double.IsNaN(insertion.End) || double.IsInfinity(insertion.End) || insertion.End < insertion.Start))
                throw new Exception("Рекламная пауза: укажите корректную секунду или диапазон времени.");
            double latest = insertion.RandomTime ? insertion.End : insertion.Start;
            if (latest > mainDuration) throw new Exception("Рекламная пауза выходит за длительность основного ролика: " + Core.N(mainDuration) + " с.");
            return insertion.Start + (insertion.RandomTime ? random.NextDouble() * (insertion.End - insertion.Start) : 0);
        }

        sealed class Selected {
            public string Path, VisualPath;
            public int Input, VisualInput;
            public double AudioDelay, VisualDelay, VisualSeconds;
            public AssemblyInsertion Placement;
            public int Frame, Frames;
            public bool Image, Video, Audio;
        }

        public static async Task Apply(AssemblyTemplate t, string ffmpeg, string probe, string main, string output,
            CancellationToken ct, Random random, Action<string> progress = null) {
            var media = Core.VideoInfo(await Core.Probe(probe, main, ct).ConfigureAwait(false));
            int totalFrames = (int)Math.Round(media.VideoDuration * t.Fps);
            var selected = new List<Selected>();
            foreach (var insertion in t.Insertions) {
                var paths = AssemblyFiles.Pool(t.Resolve(insertion.Source), AssemblyFiles.VideoExtensions.Concat(AssemblyFiles.MusicExtensions).Concat(AssemblyFiles.PictureExtensions).Distinct().ToArray());
                if (paths.Count == 0) throw new Exception("Рекламная пауза: не найдены материалы «" + insertion.Source + "».");
                string path = paths[random.Next(paths.Count)];
                bool image = IsImage(path);
                var item = new Selected { Path = path, Image = image, Placement = insertion };
                double duration;
                if (image) {
                    duration = insertion.ImageDuration;
                    if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0) throw new Exception("Рекламная пауза: длительность фото должна быть положительной.");
                } else {
                    var info = await Core.Probe(probe, path, ct).ConfigureAwait(false);
                    item.Video = info.streams.Any(s => s.codec_type == "video" && (s.disposition == null || s.disposition.attached_pic != 1));
                    item.Audio = info.streams.Any(s => s.codec_type == "audio");
                    duration = item.Video ? Core.VideoInfo(info).VideoDuration : Core.AudioDuration(info);
                }
                if (!string.IsNullOrWhiteSpace(insertion.VisualSource)) {
                    if (!item.Audio || item.Video || item.Image) throw new Exception("Для связки выберите аудио в первой колонке, а анимацию / изображение — в колонке «Визуал».");
                    var visuals = AssemblyFiles.Pool(t.Resolve(insertion.VisualSource), AssemblyFiles.PictureExtensions.Concat(AssemblyFiles.VideoExtensions).Distinct().ToArray());
                    if (visuals.Count == 0) throw new Exception("Рекламная пауза: не найден визуал «" + insertion.VisualSource + "».");
                    if (double.IsNaN(insertion.VisualOffset) || double.IsInfinity(insertion.VisualOffset)) throw new Exception("Сдвиг анимации должен быть числом.");
                    item.VisualPath = visuals[random.Next(visuals.Count)];
                    item.AudioDelay = Math.Max(0, -insertion.VisualOffset); item.VisualDelay = Math.Max(0, insertion.VisualOffset);
                    item.VisualSeconds = duration;
                    duration += Math.Abs(insertion.VisualOffset);
                }
                double at = AppearanceTime(insertion, media.VideoDuration, random);
                item.Frame = (int)Math.Round(at * t.Fps, MidpointRounding.AwayFromZero);
                item.Frames = Math.Max(1, (int)Math.Ceiling(duration * t.Fps - .000001));
                selected.Add(item);
            }
            selected = selected.OrderBy(i => i.Frame).ToList();
            progress?.Invoke("Рекламные паузы…");
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", "1", "-i", main };
            int input = 1;
            foreach (var item in selected) {
                item.Input = input++;
                if (item.Image && !AssemblyOverlayMedia.IsAnimated(item.Path)) args.AddRange(new[] { "-loop", "1", "-framerate", t.Fps.ToString(Core.Inv) });
                args.AddRange(new[] { "-i", item.Path });
                if (item.VisualPath != null) {
                    item.VisualInput = input++;
                    bool animated = AssemblyOverlayMedia.IsAnimated(item.VisualPath) || AssemblyFiles.VideoExtensions.Contains(Path.GetExtension(item.VisualPath).ToLowerInvariant());
                    if (animated) { args.AddRange(new[] { "-stream_loop", "-1" }); args.AddRange(AssemblyOverlayMedia.DecoderArguments(item.VisualPath)); if (Path.GetExtension(item.VisualPath).Equals(".gif", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-ignore_loop", "1" }); }
                    else args.AddRange(new[] { "-loop", "1", "-framerate", t.Fps.ToString(Core.Inv) });
                    args.AddRange(new[] { "-t", Core.N(item.VisualSeconds), "-i", item.VisualPath });
                }
            }
            var graph = new List<string>();
            var pieces = new List<string>();
            int cursor = 0, part = 0;
            Action<int, int> appendMain = (start, end) => {
                if (end <= start) return;
                string key = "p" + part++;
                graph.Add("[0:v:0]trim=start_frame=" + start + ":end_frame=" + end + ",setpts=PTS-STARTPTS,setsar=1[" + key + "v]");
                graph.Add(Audio(media.HasAudio ? "0:a:0" : null, (double)start / t.Fps, (double)(end - start) / t.Fps, key + "a"));
                pieces.Add("[" + key + "v][" + key + "a]");
            };
            for (int i = 0; i < selected.Count; i++) {
                var item = selected[i];
                appendMain(cursor, item.Frame);
                string key = "ad" + i;
                double duration = (double)item.Frames / t.Fps;
                int frozen = Math.Max(0, item.Frame - 1);
                graph.Add("[0:v:0]trim=start_frame=" + frozen + ":end_frame=" + (frozen + 1) + ",setpts=PTS-STARTPTS,fps=" + t.Fps + ",tpad=stop_mode=clone:stop_duration=" + Core.N(duration) + ",trim=end_frame=" + item.Frames + ",setsar=1[" + key + "freeze]");
                if (item.VisualPath != null) {
                    var placement = item.Placement;
                    int visualWidth = placement.VisualPlacementConfigured ? Math.Max(2,(int)Math.Round(placement.VisualWidth*media.Width/2)*2) : media.Width;
                    int visualHeight = placement.VisualPlacementConfigured ? Math.Max(2,(int)Math.Round(placement.VisualHeight*media.Height/2)*2) : media.Height;
                    string visualX = placement.VisualPlacementConfigured ? Core.N(placement.VisualX*media.Width) : "(W-w)/2";
                    string visualY = placement.VisualPlacementConfigured ? Core.N(placement.VisualY*media.Height) : "(H-h)/2";
                    graph.Add("[" + item.VisualInput + ":v:0]setpts=PTS-STARTPTS,fps=" + t.Fps + ",scale=" + visualWidth + ":" + visualHeight + (placement.VisualPreserveAspectRatio ? ":force_original_aspect_ratio=decrease:force_divisible_by=2" : "") + ",setsar=1,format=rgba,trim=duration=" + Core.N(item.VisualSeconds) + ",setpts=PTS+" + Core.N(item.VisualDelay) + "/TB,fps=" + t.Fps + "[" + key + "asset]");
                    graph.Add("[" + key + "freeze][" + key + "asset]overlay=x=" + visualX + ":y=" + visualY + ":eof_action=pass:repeatlast=0:enable='gte(t," + Core.N(item.VisualDelay) + ")*lt(t," + Core.N(item.VisualDelay + item.VisualSeconds) + ")',format=yuv420p[" + key + "v]");
                } else if (item.Image || item.Video) {
                    graph.Add("[" + item.Input + ":v:0]setpts=PTS-STARTPTS,fps=" + t.Fps + ",scale=" + media.Width + ":" + media.Height + ":force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1,tpad=stop_mode=clone:stop_duration=" + Core.N(duration) + ",trim=end_frame=" + item.Frames + "[" + key + "asset]");
                    graph.Add("[" + key + "freeze][" + key + "asset]overlay=x=(W-w)/2:y=(H-h)/2:shortest=1,format=yuv420p[" + key + "v]");
                } else graph.Add("[" + key + "freeze]null[" + key + "v]");
                graph.Add(Audio(item.Audio ? item.Input + ":a:0" : null, 0, duration, key + "a", item.AudioDelay));
                pieces.Add("[" + key + "v][" + key + "a]");
                cursor = item.Frame;
            }
            appendMain(cursor, totalFrames);
            graph.Add(string.Join("", pieces) + "concat=n=" + pieces.Count + ":v=1:a=1[rawVideo][a]");
            graph.Add("[rawVideo]fps=" + t.Fps + ":eof_action=pass[v]");
            double expected = (double)(totalFrames + selected.Sum(i => i.Frames)) / t.Fps;
            var context = new AssemblyRenderContext();
            await AssemblyRender.SelectEncoder(t, ffmpeg, Path.GetDirectoryName(output), context, ct).ConfigureAwait(false);
            args.AddRange(new[] { "-filter_complex", string.Join(";", graph), "-map", "[v]", "-map", "[a]", "-t", Core.N(expected) });
            args.AddRange(AssemblyRender.EncoderArguments(context.Encoder, context.EncoderThreads));
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output });
            await Core.Tool(ffmpeg, args, ct, null, 1800).ConfigureAwait(false);
            var result = Core.VideoInfo(await Core.Probe(probe, output, ct).ConfigureAwait(false));
            if (Math.Abs(result.VideoDuration - expected) > .15 || result.Width != media.Width || result.Height != media.Height || !result.HasAudio)
                throw new Exception("Рекламная пауза: результат не прошёл проверку длительности, размера или звука.");
        }

        static string Audio(string input, double start, double duration, string label, double delay = 0) {
            string source = input == null ? "anullsrc=r=48000:cl=stereo," : "[" + input + "]atrim=start=" + Core.N(start) + ",asetpts=PTS-STARTPTS,";
            return source + "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo," + (delay > 0 ? "adelay=" + Math.Round(delay * 1000).ToString(Core.Inv) + ":all=1," : "") + "apad,atrim=duration=" + Core.N(duration) + ",asetpts=PTS-STARTPTS[" + label + "]";
        }
    }
}
