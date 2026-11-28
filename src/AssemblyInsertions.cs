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

        internal sealed class AppendResult {
            public string VideoLabel, AudioLabel;
            public double Duration;
            public int NextInput;
        }

        internal static async Task<AppendResult> AppendAsync(AssemblyTemplate t, string probe, string videoLabel,
            string audioLabel, double duration, int width, int height, List<string> args, List<string> graph,
            int inputOffset, CancellationToken ct, Random random) {
            double mainDuration = duration;
            int totalFrames = (int)Math.Round(mainDuration * t.Fps);
            var selected = new List<Selected>();
            foreach (var insertion in t.Insertions) {
                var paths = AssemblyFiles.Pool(t.Resolve(insertion.Source), AssemblyFiles.VideoExtensions.Concat(AssemblyFiles.MusicExtensions).Concat(AssemblyFiles.PictureExtensions).Distinct().ToArray());
                if (paths.Count == 0) throw new Exception("Рекламная пауза: не найдены материалы «" + insertion.Source + "».");
                string path = paths[random.Next(paths.Count)];
                bool image = IsImage(path);
                var item = new Selected { Path = path, Image = image, Placement = insertion };
                double selectedDuration;
                if (image) {
                    selectedDuration = insertion.ImageDuration;
                    if (double.IsNaN(selectedDuration) || double.IsInfinity(selectedDuration) || selectedDuration <= 0) throw new Exception("Рекламная пауза: длительность фото должна быть положительной.");
                } else {
                    var info = await Core.Probe(probe, path, ct).ConfigureAwait(false);
                    item.Video = info.streams.Any(s => s.codec_type == "video" && (s.disposition == null || s.disposition.attached_pic != 1));
                    item.Audio = info.streams.Any(s => s.codec_type == "audio");
                    selectedDuration = item.Video ? Core.VideoInfo(info).VideoDuration : Core.AudioDuration(info);
                }
                if (!string.IsNullOrWhiteSpace(insertion.VisualSource)) {
                    if (!item.Audio || item.Video || item.Image) throw new Exception("Для связки выберите аудио в первой колонке, а анимацию / изображение — в колонке «Визуал».");
                    var visuals = AssemblyFiles.Pool(t.Resolve(insertion.VisualSource), AssemblyFiles.PictureExtensions.Concat(AssemblyFiles.VideoExtensions).Distinct().ToArray());
                    if (visuals.Count == 0) throw new Exception("Рекламная пауза: не найден визуал «" + insertion.VisualSource + "».");
                    if (double.IsNaN(insertion.VisualOffset) || double.IsInfinity(insertion.VisualOffset)) throw new Exception("Сдвиг анимации должен быть числом.");
                    item.VisualPath = visuals[random.Next(visuals.Count)];
                    item.AudioDelay = Math.Max(0, -insertion.VisualOffset); item.VisualDelay = Math.Max(0, insertion.VisualOffset);
                    item.VisualSeconds = selectedDuration;
                    selectedDuration += Math.Abs(insertion.VisualOffset);
                }
                double at = AppearanceTime(insertion, mainDuration, random);
                item.Frame = (int)Math.Round(at * t.Fps, MidpointRounding.AwayFromZero);
                item.Frames = Math.Max(1, (int)Math.Ceiling(selectedDuration * t.Fps - .000001));
                selected.Add(item);
            }
            selected = selected.OrderBy(i => i.Frame).ToList();
            if (selected.Count == 0)
                return new AppendResult { VideoLabel = videoLabel, AudioLabel = audioLabel, Duration = mainDuration, NextInput = inputOffset };
            string prefix = "insertion" + graph.Count + "_" + inputOffset + "_";
            int input = inputOffset;
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
            var pieces = new List<string>();
            int cursor = 0, part = 0, mainChunks = 0;
            foreach (var item in selected) {
                if (item.Frame > cursor) mainChunks++;
                cursor = item.Frame;
            }
            if (totalFrames > cursor) mainChunks++;
            var videoSources = Split(videoLabel, mainChunks + selected.Count, false, prefix + "sourceV", graph);
            var audioSources = Split(audioLabel, mainChunks, true, prefix + "sourceA", graph);
            cursor = 0;
            Action<int, int> appendMain = (start, end) => {
                if (end <= start) return;
                string key = prefix + "p" + part++;
                graph.Add("[" + videoSources.Dequeue() + "]trim=start_frame=" + start + ":end_frame=" + end + ",setpts=PTS-STARTPTS,setsar=1[" + key + "v]");
                graph.Add(Audio(audioSources.Dequeue(), (double)start / t.Fps, (double)(end - start) / t.Fps, key + "a"));
                pieces.Add("[" + key + "v][" + key + "a]");
            };
            for (int i = 0; i < selected.Count; i++) {
                var item = selected[i];
                appendMain(cursor, item.Frame);
                string key = prefix + "ad" + i;
                double adDuration = (double)item.Frames / t.Fps;
                int frozen = Math.Max(0, item.Frame - 1);
                graph.Add("[" + videoSources.Dequeue() + "]trim=start_frame=" + frozen + ":end_frame=" + (frozen + 1) + ",setpts=PTS-STARTPTS,fps=" + t.Fps + ",tpad=stop_mode=clone:stop_duration=" + Core.N(adDuration) + ",trim=end_frame=" + item.Frames + ",setsar=1[" + key + "freeze]");
                if (item.VisualPath != null) {
                    var placement = item.Placement;
                    int visualWidth = placement.VisualPlacementConfigured ? Math.Max(2,(int)Math.Round(placement.VisualWidth*width/2)*2) : width;
                    int visualHeight = placement.VisualPlacementConfigured ? Math.Max(2,(int)Math.Round(placement.VisualHeight*height/2)*2) : height;
                    string visualX = placement.VisualPlacementConfigured ? Core.N(placement.VisualX*width) : "(W-w)/2";
                    string visualY = placement.VisualPlacementConfigured ? Core.N(placement.VisualY*height) : "(H-h)/2";
                    graph.Add("[" + item.VisualInput + ":v:0]setpts=PTS-STARTPTS,fps=" + t.Fps + ",scale=" + visualWidth + ":" + visualHeight + (placement.VisualPreserveAspectRatio ? ":force_original_aspect_ratio=decrease:force_divisible_by=2" : "") + ",setsar=1,format=rgba,trim=duration=" + Core.N(item.VisualSeconds) + ",setpts=PTS+" + Core.N(item.VisualDelay) + "/TB,fps=" + t.Fps + "[" + key + "asset]");
                    graph.Add("[" + key + "freeze][" + key + "asset]overlay=x=" + visualX + ":y=" + visualY + ":eof_action=pass:repeatlast=0:enable='gte(t," + Core.N(item.VisualDelay) + ")*lt(t," + Core.N(item.VisualDelay + item.VisualSeconds) + ")',format=yuv420p[" + key + "v]");
                } else if (item.Image || item.Video) {
                    var placement = item.Placement;
                    int visualWidth = placement.VisualPlacementConfigured ? Math.Max(2, (int)Math.Round(placement.VisualWidth * width / 2) * 2) : width;
                    int visualHeight = placement.VisualPlacementConfigured ? Math.Max(2, (int)Math.Round(placement.VisualHeight * height / 2) * 2) : height;
                    string visualX = placement.VisualPlacementConfigured ? Core.N(placement.VisualX * width) : "(W-w)/2";
                    string visualY = placement.VisualPlacementConfigured ? Core.N(placement.VisualY * height) : "(H-h)/2";
                    graph.Add("[" + item.Input + ":v:0]setpts=PTS-STARTPTS,fps=" + t.Fps + ",scale=" + visualWidth + ":" + visualHeight + (!placement.VisualPlacementConfigured || placement.VisualPreserveAspectRatio ? ":force_original_aspect_ratio=decrease:force_divisible_by=2" : "") + ",setsar=1,tpad=stop_mode=clone:stop_duration=" + Core.N(adDuration) + ",trim=end_frame=" + item.Frames + "[" + key + "asset]");
                    graph.Add("[" + key + "freeze][" + key + "asset]overlay=x=" + visualX + ":y=" + visualY + ":shortest=1,format=yuv420p[" + key + "v]");
                } else graph.Add("[" + key + "freeze]null[" + key + "v]");
                graph.Add(Audio(item.Audio ? item.Input + ":a:0" : null, 0, adDuration, key + "a", item.AudioDelay));
                pieces.Add("[" + key + "v][" + key + "a]");
                cursor = item.Frame;
            }
            appendMain(cursor, totalFrames);
            // Keep demand for video and audio independent in a larger render graph.
            // A joint A/V concat can stall when a downstream xfade requests video
            // while an audio-only insertion waits for its paired audio output.
            var labels=pieces.Select(p=>p.Trim('[',']').Split(new[]{"]["},StringSplitOptions.None)).ToList();
            graph.Add(string.Concat(labels.Select(p=>"["+p[0]+"]")) + "concat=n=" + pieces.Count + ":v=1:a=0[" + prefix + "rawVideo]");
            graph.Add(string.Concat(labels.Select(p=>"["+p[1]+"]")) + "concat=n=" + pieces.Count + ":v=0:a=1[" + prefix + "audio]");
            graph.Add("[" + prefix + "rawVideo]fps=" + t.Fps + ":eof_action=pass[" + prefix + "video]");
            return new AppendResult {
                VideoLabel = prefix + "video", AudioLabel = prefix + "audio",
                Duration = (double)(totalFrames + selected.Sum(i => i.Frames)) / t.Fps, NextInput = input
            };
        }

        static Queue<string> Split(string label, int count, bool audio, string prefix, List<string> graph) {
            var labels = new Queue<string>();
            if (count == 0) {
                graph.Add("[" + label + "]" + (audio ? "anullsink" : "nullsink"));
            } else if (count == 1) labels.Enqueue(label);
            else {
                for (int i = 0; i < count; i++) labels.Enqueue(prefix + i);
                graph.Add("[" + label + "]" + (audio ? "asplit=" : "split=") + count +
                    string.Concat(labels.Select(l => "[" + l + "]")));
            }
            return labels;
        }

        public static async Task Apply(AssemblyTemplate t, string ffmpeg, string probe, string main, string output,
            CancellationToken ct, Random random, Action<string> progress = null, bool processOutput = false, double untouchedPrefix = 0, AssemblyRenderContext context = null) {
            var media = Core.VideoInfo(await Core.Probe(probe, main, ct).ConfigureAwait(false));
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", "1", "-i", main };
            var graph = new List<string>();
            string audioLabel = "0:a:0";
            if (!media.HasAudio) {
                audioLabel = "insertionSilence";
                graph.Add(Audio(null, 0, media.VideoDuration, audioLabel));
            }
            var appended = await AppendAsync(t, probe, "0:v:0", audioLabel, media.VideoDuration, media.Width, media.Height,
                args, graph, 1, ct, random).ConfigureAwait(false);
            progress?.Invoke("Рекламные паузы…");
            var processing = processOutput ? AssemblyProcessing.Prepare(t,new Media { Width=media.Width, Height=media.Height, Fps=t.Fps+"/1", Duration=appended.Duration, VideoDuration=appended.Duration, HasAudio=true },random,untouchedPrefix) : null;
            if (processing != null) {
                graph.Add("[" + appended.VideoLabel + "]format=yuv420p[insertionOutput]");
                processing.Append(graph,"insertionOutput",appended.AudioLabel);
            }
            double outputDuration = processing == null ? appended.Duration : processing.Duration;
            context = context ?? new AssemblyRenderContext();
            await AssemblyRender.SelectEncoder(t, ffmpeg, Path.GetDirectoryName(output), context, ct).ConfigureAwait(false);
            if (graph.Count > 0) args.AddRange(new[] { "-filter_complex", string.Join(";", graph) });
            args.AddRange(new[] { "-map", processing == null ? MapLabel(appended.VideoLabel) : "[processed_video]", "-map", processing == null ? MapLabel(appended.AudioLabel) : "[processed_audio]", "-t", Core.N(outputDuration) });
            args.AddRange(processing == null ? AssemblyRender.EncoderArguments(context.Encoder, context.EncoderThreads) : ProcessingEngine.FinalEncoderArguments(context.Encoder,processing.Profile,context.EncoderThreads));
            if (processing != null) args.AddRange(new[] { "-map_metadata", "-1", "-map_metadata:s:v", "-1", "-map_metadata:s:a", "-1", "-map_chapters", "-1", "-metadata:s:v:0", "rotate=0" });
            args.AddRange(new[] { "-c:a", "aac", "-b:a", processing == null ? "192k" : "160k", "-movflags", "+faststart", output });
            await Core.Tool(ffmpeg, args, ct, null, 1800).ConfigureAwait(false);
            var result = Core.VideoInfo(await Core.Probe(probe, output, ct).ConfigureAwait(false));
            if (Math.Abs(result.VideoDuration - outputDuration) > .15 || result.Width != media.Width || result.Height != media.Height || !result.HasAudio)
                throw new Exception("Рекламная пауза: результат не прошёл проверку длительности, размера или звука.");
        }

        static string MapLabel(string label) => label.IndexOf(':') >= 0 ? label : "[" + label + "]";

        static string Audio(string input, double start, double duration, string label, double delay = 0) {
            string source = input == null ? "anullsrc=r=48000:cl=stereo," : "[" + input + "]atrim=start=" + Core.N(start) + ",asetpts=PTS-STARTPTS,";
            return source + "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo," + (delay > 0 ? "adelay=" + Math.Round(delay * 1000).ToString(Core.Inv) + ":all=1," : "") + "apad,atrim=duration=" + Core.N(duration) + ",asetpts=PTS-STARTPTS[" + label + "]";
        }
    }
}
