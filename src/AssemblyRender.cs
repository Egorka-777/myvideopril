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
        internal string GraphFileOption = "-filter_complex_script";
        internal bool GraphSupportChecked;
        internal readonly Dictionary<string, string> Rasters = new Dictionary<string, string>();
        public AssemblyRenderReport LastReport;
        public int EncoderThreads => Math.Min(8, Math.Max(1, Environment.ProcessorCount - 1));
        public int FilterThreads => Math.Min(8, Math.Max(1, Environment.ProcessorCount - 1));
        internal static string RasterKey(AssemblyTemplate t, AssemblyLayerPlan p) {
            var l = p.Layer;
            return AssemblyFiles.HashText(string.Join("|", t.Width, t.Height, l.ObjectId, l.IsText ? "" : p.Asset.Hash, l.IsText ? "" : Path.GetFullPath(p.Asset.Path),
                Core.N(l.X), Core.N(l.Y), Core.N(l.Width), Core.N(l.Height), Core.N(l.Opacity), l.Font,
                Core.N(l.FontSize), Core.N(l.Radius), Core.N(l.Padding), l.GreenScreen,
                l.Kind, l.Text, l.TextColor, l.TextOutlineColor, Core.N(l.TextOutlineWidth), l.TextBold, l.TextItalic, l.TextUppercase,
                l.TextPlate, l.TextPlateColor, l.TextPlateOpacity, l.TextLineSpacing, Core.N(l.CornerRadius)));
        }
    }
    public static class AssemblyRender {
        internal static string Fill(int width,int height,int fps) => "fps="+fps+",scale="+width+":"+height+":force_original_aspect_ratio=increase,crop="+width+":"+height+",setsar=1,format=yuv420p";
        static IEnumerable<AssemblyVideo> SourceVideos(AssemblyPlan p) => p.Scenes.SelectMany(s => s.VideoSegments != null && s.VideoSegments.Count > 0 ? s.VideoSegments.Select(v => v.Video) : new[] { s.Video }).Concat(p.SecondaryVideo == null ? Enumerable.Empty<AssemblyVideo>() : new[] { p.SecondaryVideo });
        static bool NativeAudio(AssemblyTemplate t, AssemblyPlan p) => p.Scenes.Any(s => (t.SplitLayout != "none" && !s.Scene.IsPreview ? t.SplitPrimaryAudio(p) && t.SplitAudioVolume > 0 : t.SceneNativeAudioVolume(s.Scene) > 0) && (s.VideoSegments != null && s.VideoSegments.Count > 0 ? s.VideoSegments.Select(v => v.Video) : new[] { s.Video }).Any(v => v.HasAudio))
            || t.SplitLayout != "none" && !t.SplitPrimaryAudio(p) && t.SplitAudioVolume > 0 && p.SecondaryVideo?.HasAudio == true;
        internal static string VoicePitchFilter(double semitones) {
            if (Math.Abs(semitones) < .000001) return "";
            double ratio = Math.Pow(2, semitones / 12);
            return "aresample=48000,asetrate=" + Core.N(48000 * ratio) + ",aresample=48000,"
                + string.Join(",", ProcessingEngine.TempoFilters(1 / ratio)) + ",";
        }
        internal static void AppendNativeAudio(AssemblyVideo video, double start, double seconds, double delay, bool loop, double volume, double roll,
            List<string> args, ref int input, List<string> graph, List<string> audio, double pitchSemitones = 0, double gainDb = 0) {
            if (!video.HasAudio || volume <= 0 || seconds <= 0) return;
            int index = input++;
            if (loop) args.AddRange(new[] { "-stream_loop", "-1" });
            args.AddRange(new[] { "-ss", Core.N(start), "-t", Core.N(seconds), "-i", video.Path });
            string label = "native" + index;
            graph.Add("[" + index + ":a:0]asetpts=PTS-STARTPTS,atrim=duration=" + Core.N(seconds)
                + (gainDb > 0 ? ",volume=" + Core.N(gainDb) + "dB:precision=double" : "") + ",volume=" + Core.N(volume)
                + (Math.Abs(pitchSemitones) < .000001 ? "" : "," + VoicePitchFilter(pitchSemitones) + "apad,atrim=duration=" + Core.N(seconds))
                + (gainDb > 0 && volume * Math.Pow(10, gainDb / 20) > 1 ? ",alimiter=limit=0.95:level=false:latency=true" : "")
                + "[" + label + "Source]");
            AppendAudioDelay(graph, label + "Source", label, delay, roll);
            audio.Add(label);
        }
        static void AppendAudioDelay(List<string> graph, string source, string output, double delay, double roll) {
            if (delay > 0) {
                // Explicit silence avoids adelay losing its prefix after atempo in the shipped FFmpeg.
                graph.Add("anullsrc=r=48000:cl=stereo,atrim=duration=" + Core.N(delay) + "[" + output + "Silence]");
                graph.Add("[" + output + "Silence][" + source + "]concat=n=2:v=0:a=1,apad,atrim=duration=" + Core.N(roll) + "[" + output + "]");
            } else graph.Add("[" + source + "]apad,atrim=duration=" + Core.N(roll) + "[" + output + "]");
        }
        static bool HasAudio(AssemblyPlan p) => !string.IsNullOrWhiteSpace(p.Music) || !string.IsNullOrWhiteSpace(p.Voiceover) || p.Scenes.Any(s => !string.IsNullOrWhiteSpace(s.Music) || !string.IsNullOrWhiteSpace(s.Voiceover));
        public static void AppendSceneVideo(AssemblyTemplate t, AssemblyScenePlan s, List<string> args, ref int input, List<string> graph, string outputLabel, bool transparentPlacement = false) {
            double content = AssemblyTimeline.ContentDuration(t, s);
            if(t.SplitLayout=="none" && !t.HasBackgroundVideo(s.Scene) && s.Layers.Any(l=>l.Layer.Enabled && !l.Layer.IsText && l.Asset!=null && AssemblyOverlayMedia.IsAnimated(l.Asset.Path))) {
                graph.Add("color=c=black:s="+t.Width+"x"+t.Height+":r="+t.Fps+":d="+Core.N(content)+"["+outputLabel+"]");return;
            }
            var segments = s.VideoSegments != null && s.VideoSegments.Count > 0
                ? s.VideoSegments
                : new List<AssemblySceneVideoSegment> { new AssemblySceneVideoSegment { Video = s.Video, Duration = content, IsContinuation = false } };
            if (segments.Count == 1) {
                int video = input++;
                if (AssemblyTimeline.LoopInput(t, s)) args.AddRange(new[] { "-stream_loop", "-1" });
                args.AddRange(new[] { "-ss", Core.N(AssemblyTimeline.SourceStart(t, s)), "-t", Core.N(content), "-i", segments[0].Video.Path });
                graph.Add("[" + video + ":v:0]setpts=PTS-STARTPTS," + Fill(t.Width, t.Height, t.Fps) + ","
                    + (t.SceneTimingVersion > 0 || s.ContentSeconds > 0 ? "tpad=stop_mode=clone:stop_duration=" + Core.N(content) + "," : "")
                    + "trim=duration=" + Core.N(content) + AssemblyVideoPlacement.Filter(t, transparentPlacement) + "[" + outputLabel + "]");
                return;
            }
            bool blend = t.Transition != "cut" && t.TransitionSeconds > 0;
            double transition = blend ? Math.Min(t.TransitionSeconds, segments.Min(part => part.Duration) / 2) : 0;
            var labels = new List<string>();
            for (int si = 0; si < segments.Count; si++) {
                int video = input++;
                args.AddRange(new[] { "-t", Core.N(segments[si].Duration), "-i", segments[si].Video.Path });
                string label = outputLabel + "Part" + si;
                graph.Add("[" + video + ":v:0]setpts=PTS-STARTPTS," + Fill(t.Width, t.Height, t.Fps) + ",trim=duration=" + Core.N(segments[si].Duration)
                    + (si > 0 && transition > 0 ? ",tpad=start_mode=clone:start_duration=" + Core.N(transition) : "") + ",settb=1/" + t.Fps + ",format=yuv444p[" + label + "]");
                labels.Add("[" + label + "]");
            }
            string chainLabel = outputLabel + "Chain";
            if (transition <= 0) graph.Add(string.Join("", labels) + "concat=n=" + segments.Count + ":v=1:a=0[" + chainLabel + "]");
            else {
                string current = labels[0]; double offset = segments[0].Duration;
                for (int si = 1; si < segments.Count; si++) {
                    string next = si == segments.Count - 1 ? chainLabel : outputLabel + "Join" + si;
                    string effect = t.Transition == "random" ? AssemblyTimeline.RandomTransitions[(si - 1) % AssemblyTimeline.RandomTransitions.Length] : t.Transition;
                    graph.Add(current + labels[si] + "xfade=" + AssemblyEngine.TransitionFilter(effect) + ":duration=" + Core.N(transition) + ":offset=" + Core.N(offset-transition) + "[" + next + "]");
                    current = "[" + next + "]"; offset += segments[si].Duration;
                }
            }
            // Fractional source durations and xfade frame rounding must not leave
            // the scene shorter than its voice-driven boundary for the next xfade.
            graph.Add("[" + chainLabel + "]fps=" + t.Fps + ",tpad=stop_mode=clone:stop_duration=" + Core.N(content)
                + ",trim=duration=" + Core.N(content) + AssemblyVideoPlacement.Filter(t, transparentPlacement) + ",settb=1/" + t.Fps + ",setpts=PTS-STARTPTS[" + outputLabel + "]");
        }
        static bool BurnCaptions(AssemblyTemplate t, AssemblyPlan p) => t.SubtitleStyle?.Enabled == true && p.Captions != null && p.Captions.Count > 0;
        public static List<string> EncoderArguments(string encoder, int threads) {
            var args = new List<string> { "-c:v", encoder };
            if (encoder == "h264_nvenc") args.AddRange(new[] { "-preset", "p4", "-rc", "vbr", "-cq", "20", "-b:v", "0" });
            else if (encoder == "h264_qsv") args.AddRange(new[] { "-preset", "fast", "-global_quality", "20" });
            else if (encoder == "h264_amf") args.AddRange(new[] { "-quality", "speed", "-rc", "cqp", "-qp_i", "20", "-qp_p", "20" });
            else args.AddRange(new[] { "-preset", "veryfast", "-crf", "20", "-threads", threads.ToString() });
            args.AddRange(new[] { "-pix_fmt", "yuv420p" });
            return args;
        }
        static readonly Random AppearanceRandom = new Random();
        // Decode each PNG once. Only its short entrance is animated; overlay holds
        // the final frame afterwards. The background never enters this transition.
        public static void AppendLayerOverlay(AssemblyTemplate t, AssemblyLayerPlan layer, double start, double end,
            string enable, string current, string next, string format, List<string> args, ref int input, List<string> graph) {
            if (end <= start) { graph.Add("[" + current + "]null[" + next + "]"); return; }
            double playback = AssemblyOverlayMedia.PlaybackLength(layer.Layer, (layer.Layer.IsText ? "" : layer.Asset.Path), end - start);
            double period = AssemblyOverlayMedia.RepeatPeriod(layer.Layer, playback);
            if (layer.Layer.PlaybackMode == "repeat") playback = Math.Min(playback, period);
            int count = layer.Layer.PlaybackMode == "repeat" ? (int)Math.Ceiling((end-start)/period) : 1;
            for (int cycle=0; cycle<count; cycle++) {
                double from = start + cycle*period, until = Math.Min(end, from+playback);
                string output = cycle == count-1 ? next : next + "Cycle" + cycle;
                AppendLayerCycle(t, layer, from, until, "(" + enable + ")*gte(t," + Core.N(from) + ")*lt(t," + Core.N(until) + ")", current, output, format, args, ref input, graph);
                current = output;
            }
        }
        static void AppendLayerCycle(AssemblyTemplate t, AssemblyLayerPlan layer, double start, double end,
            string enable, string current, string next, string format, List<string> args, ref int input, List<string> graph) {
            if (AssemblyOverlayMedia.IsAnimated((layer.Layer.IsText ? "" : layer.Asset.Path))) {
                AppendAnimatedLayerOverlay(t, layer, start, end, enable, current, next, format, args, ref input, graph);
                return;
            }
            int picture = input++;
            args.AddRange(new[] { "-threads", "1", "-framerate", t.Fps.ToString(), "-i", layer.Raster });
            string source = picture + ":v:0";
            string effect = (layer.Layer.AppearanceTransition ?? "cut").Trim().ToLowerInvariant();
            double duration = Math.Min(layer.Layer.AppearanceDuration, end - start);
            if (effect != "cut" && effect != "" && duration > 0) {
                if (effect == "random") {
                    lock (AppearanceRandom) effect = AssemblyTimeline.RandomTransitions[AppearanceRandom.Next(AssemblyTimeline.RandomTransitions.Length)];
                }
                string animated = next + "Entrance";
                string seconds = Core.N(duration), length = Core.N(duration + 2.0 / t.Fps);
                // loop/setpts supplies timestamps but leaves an undefined frame-rate
                // declaration (1/0) on FFmpeg 7.1; xfade requires matching CFR inputs.
                string loop = "loop=loop=-1:size=1:start=0,trim=duration=" + length + ",setpts=N/(" + t.Fps + "*TB),fps=" + t.Fps;
                if (effect == "fade") {
                    graph.Add("[" + source + "]format=rgba," + loop + ",fade=t=in:st=0:d=" + seconds
                        + ":alpha=1,setpts=PTS+" + Core.N(start) + "/TB,fps=" + t.Fps + "[" + animated + "]");
                } else {
                    // xfade handles all four gbrap planes. Premultiplication prevents
                    // the transparent input from darkening semitransparent PNG edges.
                    string blank = next + "Blank", image = next + "Image";
                    graph.Add("color=c=black@0:s=" + t.Width + "x" + t.Height + ":r=" + t.Fps + ":d=" + length
                        + ",format=gbrap,settb=1/" + t.Fps + "[" + blank + "]");
                    graph.Add("[" + source + "]format=gbrap," + loop + ",settb=1/" + t.Fps + ",premultiply=inplace=1[" + image + "]");
                    graph.Add("[" + blank + "][" + image + "]xfade=" + AssemblyEngine.TransitionFilter(effect, transparentOutgoing: true) + ":duration=" + seconds
                        + ":offset=0,unpremultiply=inplace=1,format=rgba,setpts=PTS+" + Core.N(start) + "/TB,fps=" + t.Fps + "[" + animated + "]");
                }
                source = animated;
            }
            if (layer.Layer.ExitTransition == "fade") {
                string exiting = next + "Exit";
                double fade = Math.Min(layer.Layer.ExitDuration, end-start);
                graph.Add("[" + source + "]format=rgba," + (source == picture + ":v:0" ? "loop=loop=-1:size=1:start=0,setpts=N/(" + t.Fps + "*TB)+" + Core.N(start) + "/TB," : "tpad=stop_mode=clone:stop_duration=" + Core.N(end-start) + ",")
                    + "trim=end=" + Core.N(end) + ",fade=t=out:st=" + Core.N(end-fade) + ":d=" + Core.N(fade) + ":alpha=1,fps=" + t.Fps + "[" + exiting + "]");
                source = exiting;
            }
            graph.Add("[" + current + "][" + source + "]overlay=0:0:format=" + format
                + ":repeatlast=1:eof_action=repeat:enable='" + enable + "'[" + next + "]");
        }
        static void AppendAnimatedLayerOverlay(AssemblyTemplate t, AssemblyLayerPlan layer, double start, double end,
            string enable, string current, string next, string format, List<string> args, ref int input, List<string> graph) {
            double length = Math.Max(1.0 / t.Fps, end - start);
            int media = input++;
            if (layer.Layer.PlaybackMode == "continuous") args.AddRange(new[] { "-stream_loop", "-1" });
            args.AddRange(new[] { "-threads", "1" });
            if (Path.GetExtension(layer.Asset.Path).Equals(".gif", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-ignore_loop", "1" });
            args.AddRange(AssemblyOverlayMedia.DecoderArguments(layer.Asset.Path));
            args.AddRange(new[] { "-t", Core.N(length), "-i", layer.Asset.Path });
            var bounds = AssemblyRaster.LayerImageBounds(t, layer.Layer, AssemblyOverlayMedia.Size(layer.Asset.Path));
            int width = Math.Max(1, (int)Math.Round(bounds.Width)), height = Math.Max(1, (int)Math.Round(bounds.Height));
            int x = (int)Math.Round(bounds.X), y = (int)Math.Round(bounds.Y);
            int padX = Math.Max(0, x), padY = Math.Max(0, y), cropX = Math.Max(0, -x), cropY = Math.Max(0, -y);
            int canvasWidth = Math.Max(t.Width + cropX, width + padX), canvasHeight = Math.Max(t.Height + cropY, height + padY);
            string image = next + "Media", source = next + "Motion";
            graph.Add("[" + media + ":v:0]setpts=PTS-STARTPTS,fps=" + t.Fps + ",trim=duration=" + Core.N(length)
                + ",format=rgba," + (layer.Layer.GreenScreen ? AssemblyOverlayMedia.GreenScreenFilter(next + "Key") + "," : "")
                + "scale=" + width + ":" + height + ",setsar=1"
                + AssemblyOverlayMedia.CornerFilter(next + "Corner", layer.Layer.CornerRadius, width, height)
                + ",colorchannelmixer=aa=" + Core.N(layer.Layer.Opacity)
                + ",pad=" + canvasWidth + ":" + canvasHeight + ":" + padX + ":" + padY + ":color=black@0"
                + (canvasWidth != t.Width || canvasHeight != t.Height ? ",crop=" + t.Width + ":" + t.Height + ":" + cropX + ":" + cropY : "")
                + ",settb=1/" + t.Fps + ",setpts=N,fps=" + t.Fps + "[" + image + "]");
            string effect = (layer.Layer.AppearanceTransition ?? "cut").Trim().ToLowerInvariant();
            double duration = Math.Min(layer.Layer.AppearanceDuration, end - start);
            if (effect == "random" && duration > 0) {
                lock (AppearanceRandom) effect = AssemblyTimeline.RandomTransitions[AppearanceRandom.Next(AssemblyTimeline.RandomTransitions.Length)];
            }
            if (effect != "cut" && effect != "" && duration > 0) {
                if (effect == "fade") {
                    graph.Add("[" + image + "]fade=t=in:st=0:d=" + Core.N(duration) + ":alpha=1,setpts=PTS+" + Core.N(start) + "/TB[" + source + "]");
                } else {
                    string blank = next + "Blank", premultiplied = next + "Premultiplied";
                    graph.Add("color=c=black@0:s=" + t.Width + "x" + t.Height + ":r=" + t.Fps + ":d=" + Core.N(duration + 2.0 / t.Fps)
                        + ",format=gbrap,settb=1/" + t.Fps + "[" + blank + "]");
                    graph.Add("[" + image + "]format=gbrap,premultiply=inplace=1[" + premultiplied + "]");
                    graph.Add("[" + blank + "][" + premultiplied + "]xfade=" + AssemblyEngine.TransitionFilter(effect, transparentOutgoing: true) + ":duration=" + Core.N(duration)
                        + ":offset=0,unpremultiply=inplace=1,format=rgba,trim=duration=" + Core.N(length) + ",setpts=PTS+" + Core.N(start) + "/TB[" + source + "]");
                }
            } else graph.Add("[" + image + "]setpts=PTS+" + Core.N(start) + "/TB[" + source + "]");
            if (layer.Layer.ExitTransition == "fade") {
                string exiting = next + "Exit";
                double fade = Math.Min(layer.Layer.ExitDuration, length);
                graph.Add("[" + source + "]fade=t=out:st=" + Core.N(end-fade) + ":d=" + Core.N(fade) + ":alpha=1,fps=" + t.Fps + "[" + exiting + "]");
                source = exiting;
            }
            graph.Add("[" + current + "][" + source + "]overlay=0:0:format=" + format
                + ":repeatlast=0:eof_action=pass:enable='" + enable + "'[" + next + "]");
        }
        internal static string AppendSceneLayers(AssemblyTemplate t, AssemblyScenePlan scene, string current, string prefix,
            string format, List<string> args, ref int input, List<string> graph) {
            for (int j = 0; j < scene.Layers.Count; j++) {
                var layer = scene.Layers[j]; string next = prefix + j;
                AssemblySceneVideoBuilder.LayerOverlayRange(t, scene, layer.Layer, out double start, out double end);
                AppendLayerOverlay(t, layer, start, end, AssemblySceneVideoBuilder.LayerOverlayEnable(t, scene, layer.Layer),
                    current, next, format, args, ref input, graph);
                current = next;
            }
            return current;
        }
        internal static void AppendSceneLayerAudio(AssemblyTemplate t, AssemblyScenePlan scene, double position, double duration,
            Random pitchRandom, List<string> args, ref int input, List<string> graph, List<string> audio) {
            foreach(var layer in scene.Layers.Where(l=>l.Layer.Enabled && l.Layer.AudioEnabled && !l.Layer.IsText && l.Asset!=null && AssemblyOverlayMedia.HasAudio(l.Asset.Path))) {
                AssemblySceneVideoBuilder.LayerOverlayRange(t,scene,layer.Layer,out double start,out double end);
                if(end<=start)continue;
                double playback=AssemblyOverlayMedia.PlaybackLength(layer.Layer,layer.Asset.Path,end-start);
                double period=AssemblyOverlayMedia.RepeatPeriod(layer.Layer,playback);
                if(layer.Layer.PlaybackMode=="repeat")playback=Math.Min(playback,period);
                double pitch=layer.Layer.AudioPitch.Sample(pitchRandom,0);
                var video=new AssemblyVideo {Path=layer.Asset.Path,HasAudio=true,Duration=AssemblyOverlayMedia.Duration(layer.Asset.Path)};
                int cycles=layer.Layer.PlaybackMode=="repeat"?(int)Math.Ceiling((end-start)/period):1;
                for(int cycle=0;cycle<cycles;cycle++) {
                    double from=start+cycle*period,seconds=Math.Min(playback,end-from);
                    AppendNativeAudio(video,0,seconds,position+scene.IncomingPad+from,layer.Layer.PlaybackMode=="continuous",t.SceneNativeAudioVolume(scene.Scene),duration,args,ref input,graph,audio,pitch);
                }
            }
        }
        // Cut layers retain the single-frame optimized overlay path. Scene layers
        // are still composited before the separate scene-to-scene transition.
        public static List<string> Arguments(AssemblyTemplate t, AssemblyPlan p, string encoder, string script, string output, AssemblyRenderContext context, AssemblyProcessingPlan processing = null) {
            if (p.Scenes.Count == 0) throw new Exception("Добавь хотя бы одну сцену перед сборкой.");
            double dur = p.RollDuration(t);
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", context.FilterThreads.ToString() };
            var graph = new List<string>(); int input = 0;
            bool transitions = p.Scenes.Count > 1 && t.Transition != "cut" && t.TransitionDuration > 0;
            bool split=t.SplitLayout!="none",transparentScenes=t.ContinuousVideo || split;
            bool background = p.Background != null, sceneAlpha = transparentScenes || background;
            bool transparentPlacement = background;
            int continuous=-1;
            if(t.ContinuousVideo) {
                continuous=input++; var video=p.Scenes[0].Video;
                if(t.LoopShortVideos && video.Duration+.001<dur)args.AddRange(new[]{"-stream_loop","-1"});
                args.AddRange(new[]{"-t",Core.N(dur),"-i",video.Path});
                graph.Add("["+continuous+":v:0]setpts=PTS-STARTPTS,"+Fill(t.Width,t.Height,t.Fps)+",tpad=stop_mode=clone:stop_duration="+Core.N(dur)+",trim=duration="+Core.N(dur)+AssemblyVideoPlacement.Filter(t, transparentPlacement)+"[continuous]");
            }
            for (int i = 0; i < p.Scenes.Count; i++) {
                var s = p.Scenes[i]; string current = "base" + i;
                if(transparentScenes) graph.Add("color=c=black@0:s="+t.Width+"x"+t.Height+":r="+t.Fps+":d="+Core.N(AssemblyTimeline.ContentDuration(t,s))+",format=yuva444p["+current+"]");
                if(!t.ContinuousVideo) {
                    string videoOut = split && !t.ContinuousVideo ? "videoBase" + i : current;
                    AppendSceneVideo(t, s, args, ref input, graph, videoOut, background);
                }
                current = AppendSceneLayers(t, s, current, "overlay" + i + "_", sceneAlpha ? "auto" : "yuv420", args, ref input, graph);
                graph.Add("[" + current + "]"+AssemblyTimeline.Padding(t,s)+"settb=1/" + t.Fps + ",setpts=PTS-STARTPTS,fps=" + t.Fps + ",format=" + (sceneAlpha ? "yuva444p" : transitions ? "yuv444p" : "yuv420p") + "[s" + i + "]");
                if(split && !t.ContinuousVideo)graph.Add("[videoBase"+i+"]"+AssemblyTimeline.Padding(t,s)+"settb=1/"+t.Fps+",setpts=PTS-STARTPTS,fps="+t.Fps+",format="+(transitions?"yuv444p":"yuv420p")+"[videoScene"+i+"]");
            }
            string joined;
            if (!transitions) {
                joined = "joined";
                graph.Add(string.Join("", Enumerable.Range(0, p.Scenes.Count).Select(i => "[s" + i + "]")) + "concat=n=" + p.Scenes.Count + ":v=1:a=0[" + joined + "]");
            } else {
                joined = "s0"; double offset = 0;
                for (int i = 1; i < p.Scenes.Count; i++) {
                    offset += AssemblyTimeline.SceneSeconds(p.Scenes[i - 1])+(t.SceneTimingVersion>0?p.Scenes[i-1].IncomingPad:0); string next = "joined" + i;
                    graph.Add("[" + joined + "][s" + i + "]xfade=" + AssemblyEngine.TransitionFilter(AssemblyTimeline.Transition(t,p.Scenes[i-1])) + ":duration=" + Core.N(t.TransitionSeconds)
                        + ":offset=" + Core.N(offset) + ",fps=" + t.Fps + ":eof_action=pass[" + next + "]"); joined = next;
                }
            }
            string sceneOverlay=transparentScenes?joined:null;
            if(t.ContinuousVideo)joined="continuous";
            else if(split) {
                joined="videoScene0";
                if(!transitions) {graph.Add(string.Join("",Enumerable.Range(0,p.Scenes.Count).Select(i=>"[videoScene"+i+"]"))+"concat=n="+p.Scenes.Count+":v=1:a=0[videoJoined]");joined="videoJoined";}
                else {double offset=0;for(int i=1;i<p.Scenes.Count;i++){offset+=AssemblyTimeline.SceneSeconds(p.Scenes[i-1])+(t.SceneTimingVersion>0?p.Scenes[i-1].IncomingPad:0);string next="videoJoined"+i;graph.Add("["+joined+"][videoScene"+i+"]xfade="+AssemblyEngine.TransitionFilter(AssemblyTimeline.Transition(t,p.Scenes[i-1]))+":duration="+Core.N(t.TransitionSeconds)+":offset="+Core.N(offset)+",fps="+t.Fps+":eof_action=pass["+next+"]");joined=next;}}
            }
            if(split) {
                if(p.SecondaryVideo==null)throw new Exception("Не выбрано второе видео для разделённого экрана.");
                int second=input++;
                if(t.LoopShortVideos && p.SecondaryVideo.Duration+.001<dur)args.AddRange(new[]{"-stream_loop","-1"});
                args.AddRange(new[]{"-t",Core.N(dur),"-i",p.SecondaryVideo.Path});
                bool vertical = AssemblySplit.IsVertical(t.SplitLayout);
                AssemblySplit.PaneSizes(t, out int primarySpan, out int secondarySpan, out int splitLine, out int feather);
                double topZoom = p.SplitSwapped ? t.SplitSecondaryZoom : t.SplitPrimaryZoom;
                double bottomZoom = p.SplitSwapped ? t.SplitPrimaryZoom : t.SplitSecondaryZoom;
                string firstPane = AssemblySplit.PaneFilter(t, false, topZoom);
                string secondPane = AssemblySplit.PaneFilter(t, true, bottomZoom);
                if (p.SplitSwapped) {
                    graph.Add("[" + second + ":v:0]setpts=PTS-STARTPTS," + firstPane + ",tpad=stop_mode=clone:stop_duration=" + Core.N(dur) + "[splitA]");
                    graph.Add("[" + joined + "]" + secondPane + "[splitB]");
                } else {
                    graph.Add("[" + joined + "]" + firstPane + "[splitA]");
                    graph.Add("[" + second + ":v:0]setpts=PTS-STARTPTS," + secondPane + ",tpad=stop_mode=clone:stop_duration=" + Core.N(dur) + "[splitB]");
                }
                graph.Add(AssemblySplit.BlendFilter(vertical, splitLine, feather, t.Width, t.Height)); joined = "split";
            }
            if (transitions) {
                graph.Add("[" + joined + "]tpad=stop_mode=clone:stop_duration=" + Core.N(dur) + ",trim=duration=" + Core.N(dur) + "[timedScenes]");
                joined = "timedScenes";
            }
            if (background) {
                int media = input++;
                string extension = Path.GetExtension(p.Background.Path).ToLowerInvariant();
                if (AssemblyFiles.VideoExtensions.Contains(extension) || extension == ".gif") {
                    args.AddRange(new[] { "-stream_loop", "-1" });
                    if (extension == ".gif") args.AddRange(new[] { "-ignore_loop", "1" });
                } else args.AddRange(new[] { "-loop", "1", "-framerate", t.Fps.ToString() });
                args.AddRange(new[] { "-t", Core.N(dur), "-i", p.Background.Path });
                graph.Add("[" + media + ":v:0]setpts=PTS-STARTPTS," + (t.BackgroundForeground ? Fill(t.Width, t.Height, t.Fps).Replace("format=yuv420p", "format=yuva444p") : Fill(t.Width, t.Height, t.Fps))
                    + ",trim=duration=" + Core.N(dur) + "[background]");
                // Preserve media alpha for a foreground PNG frame; never key video colours.
                graph.Add((t.BackgroundForeground ? "[" + joined + "][background]" : "[background][" + joined + "]") + "overlay=0:0:format=yuv420:shortest=1[backgroundComposited]");
                joined = "backgroundComposited";
            }
            if(sceneOverlay!=null) {graph.Add("["+joined+"]["+sceneOverlay+"]overlay=0:0:format=yuv420:shortest=1[sceneComposited]");joined="sceneComposited";}
            for(int j=0;j<p.GlobalLayers.Count;j++) {
                var layer=p.GlobalLayers[j]; string next="global"+j;
                double start=Math.Max(0,layer.Layer.Start),end=layer.Layer.End==0?dur:Math.Min(layer.Layer.End,dur);
                AppendLayerOverlay(t,layer,start,end,"gte(t,"+Core.N(start)+")*lt(t,"+Core.N(end)+")",
                    joined,next,"yuv420",args,ref input,graph);joined=next;
            }
            string captionTrack = BurnCaptions(t,p) ? p.Captions[0].TrackPath : null;
            if (!string.IsNullOrEmpty(captionTrack)) {
                int picture = input++;
                args.AddRange(new[] { "-threads", "1", "-f", "concat", "-safe", "0", "-i", captionTrack });
                graph.Add("[" + picture + ":v:0]fps=" + t.Fps + ",format=rgba[captionTrack]");
                graph.Add("[" + joined + "][captionTrack]overlay=0:0:format=yuv420:repeatlast=1:eof_action=repeat[captioned]"); joined = "captioned";
            }
            else if(BurnCaptions(t,p)) for(int j=0;j<p.Captions.Count;j++) {
                var cue=p.Captions[j];int picture=input++;string next="caption"+j;
                args.AddRange(new[]{"-threads","1","-framerate",t.Fps.ToString(),"-i",cue.ImagePath});
                graph.Add("["+picture+":v:0]scale="+t.Width+":"+t.Height+"[captionImage"+j+"]");
                graph.Add("["+joined+"][captionImage"+j+"]overlay=0:0:format=yuv420:repeatlast=1:eof_action=repeat:enable='gte(t,"+Core.N(cue.Start)+")*lt(t,"+Core.N(cue.End)+")'["+next+"]");joined=next;
            }
            graph.Add("[" + joined + "]format=yuv420p[vout]");
            var audio=new List<string>();
            double mainStart = AssemblyTimeline.MainStart(t, p), mainSeconds = Math.Max(0, dur - mainStart);
            string mainDelay = mainStart > 0 ? ",adelay=" + Math.Round(mainStart * 1000).ToString(Core.Inv) + ":all=1" : "";
            var pitchRandom=new Random();
            double backgroundPitch=t.BackgroundAudioPitch.Sample(pitchRandom,0);
            double nativePitch = string.IsNullOrWhiteSpace(p.Voiceover) && !p.Scenes.Any(s => !string.IsNullOrWhiteSpace(s.Voiceover)) ? t.VoicePitchSemitones : 0;
            double NativeLevel(AssemblyScene scene) => split && !scene.IsPreview
                ? (t.SplitPrimaryAudio(p) ? t.SplitAudioVolume : 0) : t.SceneNativeAudioVolume(scene);
            if (p.Scenes.Any(s => NativeLevel(s.Scene) > 0)) {
                double position = 0;
                foreach (var scene in p.Scenes) {
                    double content = AssemblyTimeline.ContentDuration(t, scene);
                    bool preview = scene.Scene.IsPreview;
                    // With no background, animated layers supply the picture and their own audio.
                    // The independently selected scene.Video is only a timing candidate, not a second sound source.
                    bool objectOnly=!split && !t.HasBackgroundVideo(scene.Scene) && scene.Layers.Any(l=>l.Layer.Enabled && !l.Layer.IsText && l.Asset!=null && AssemblyOverlayMedia.IsAnimated(l.Asset.Path));
                    double level = objectOnly ? 0 : NativeLevel(scene.Scene);
                    double pitch = t.BackgroundAudioPitch.Enabled ? backgroundPitch : preview ? 0 : nativePitch;
                    double Gain(AssemblyVideo video) => preview ? 0 : AssemblyAudioLeveling.GainFor(t, video.Path);
                    double audioStart = position + (t.HasPreviewScene ? 0 : scene.IncomingPad);
                    double sourceStart = t.ContinuousVideo ? position + scene.IncomingPad : AssemblyTimeline.SourceStart(t, scene);
                    if (level > 0 && scene.VideoSegments != null && scene.VideoSegments.Count > 1) {
                        double segmentTime = 0;
                        foreach (var segment in scene.VideoSegments) {
                            AppendNativeAudio(segment.Video, 0, segment.Duration, audioStart + segmentTime, false, level, dur, args, ref input, graph, audio, pitch, Gain(segment.Video));
                            segmentTime += segment.Duration;
                        }
                    } else if (level > 0) AppendNativeAudio(scene.Video, sourceStart, content, audioStart, AssemblyTimeline.LoopInput(t, scene), level, dur, args, ref input, graph, audio, pitch, Gain(scene.Video));
                    position += AssemblyTimeline.SceneSeconds(scene) + (t.SceneTimingVersion > 0 && t.Transition != "cut" ? t.TransitionSeconds : 0);
                }
            }
            if (split && !t.SplitPrimaryAudio(p) && p.SecondaryVideo != null)
                AppendNativeAudio(p.SecondaryVideo, 0, mainSeconds, mainStart, t.LoopShortVideos && p.SecondaryVideo.Duration < mainSeconds, t.SplitAudioVolume, dur, args, ref input, graph, audio, t.BackgroundAudioPitch.Enabled ? backgroundPitch : nativePitch);
            double packPosition=0;
            foreach(var scene in p.Scenes) {
                AppendSceneLayerAudio(t, scene, packPosition, dur, pitchRandom, args, ref input, graph, audio);
                packPosition+=AssemblyTimeline.SceneSeconds(scene)+(t.SceneTimingVersion>0 && t.Transition!="cut"?t.TransitionSeconds:0);
            }
            if(!string.IsNullOrWhiteSpace(p.Music) && p.Scenes.Any(s=>t.SceneBedMusicVolume(s.Scene)>0)) {
                int index=input++; string label="bedMusic";
                args.AddRange(new[]{"-stream_loop","-1","-i",p.Music});
                double fade=Math.Min(.2,dur/4), musicTime=0;
                var musicEnds=new List<double>();
                foreach(var scene in p.Scenes) {
                    musicTime+=AssemblyTimeline.SceneSeconds(scene)+(t.SceneTimingVersion>0 && t.Transition!="cut"?t.TransitionSeconds:0);
                    musicEnds.Add(musicTime);
                }
                string level=Core.N(t.SceneBedMusicVolume(p.Scenes[p.Scenes.Count-1].Scene));
                for(int i=p.Scenes.Count-2;i>=0;i--)
                    level="if(lt(t,"+Core.N(musicEnds[i])+"),"+Core.N(t.SceneBedMusicVolume(p.Scenes[i].Scene))+","+level+")";
                graph.Add("["+index+":a:0]asetpts=PTS-STARTPTS,atrim=duration="+Core.N(dur)+",volume='"+level+"':eval=frame,afade=t=in:d="+Core.N(fade)+",afade=t=out:st="+Core.N(Math.Max(0,dur-fade))+":d="+Core.N(fade)+"["+label+"]");audio.Add(label);
            }
            double sceneTime=0;
            for(int i=0;i<p.Scenes.Count;i++) {
                var scene=p.Scenes[i];
                if(!scene.Scene.IsPreview && !string.IsNullOrWhiteSpace(scene.Music)) {
                    int index=input++;args.AddRange(new[]{"-stream_loop","-1","-i",scene.Music});string label="sceneAudio"+i;
                    graph.Add("["+index+":a:0]asetpts=PTS-STARTPTS,atrim=duration="+Core.N(AssemblyTimeline.SceneSeconds(scene))+",volume="+Core.N(scene.Scene.MusicVolume)+",adelay="+Math.Round(sceneTime*1000).ToString(Core.Inv)+":all=1,apad,atrim=duration="+Core.N(dur)+"["+label+"]");audio.Add(label);
                }
                if(!scene.Scene.IsPreview && !string.IsNullOrWhiteSpace(scene.Voiceover)) {
                    int index=input++;args.AddRange(new[]{"-i",scene.Voiceover});string label="sceneVoice"+i;
                    double voiceDur = AssemblyTimeline.SceneSeconds(scene);
                    graph.Add("["+index+":a:0]asetpts=PTS-STARTPTS,atrim=duration="+Core.N(voiceDur)+","+VoicePitchFilter(t.VoicePitchSemitones)+"apad,atrim=duration="+Core.N(voiceDur)+",volume=1["+label+"Source]");
                    AppendAudioDelay(graph,label+"Source",label,sceneTime,dur);audio.Add(label);
                }
                sceneTime+=AssemblyTimeline.SceneSeconds(scene)+(t.SceneTimingVersion>0 && t.Transition!="cut"?t.TransitionSeconds:0);
            }
            if(!string.IsNullOrWhiteSpace(p.Voiceover)) {
                int index=input++;args.AddRange(new[]{"-i",p.Voiceover});
                graph.Add("["+index+":a:0]asetpts=PTS-STARTPTS,"+VoicePitchFilter(t.VoicePitchSemitones)+"volume=1,atrim=duration="+Core.N(mainSeconds)+"[voiceoverSource]");
                AppendAudioDelay(graph,"voiceoverSource","voiceover",mainStart,dur);audio.Add("voiceover");
            }
            bool music=audio.Count>0;
            string mixDuration = "longest"; // Keep the one-second tail after speech.
            if(music)graph.Add(string.Join("",audio.Select(a=>"["+a+"]"))+"amix=inputs="+audio.Count+":duration="+mixDuration+":normalize=0,atrim=duration="+Core.N(dur)+"[audio]");
            if (processing != null) processing.Append(graph,"vout",music ? "audio" : null);
            File.WriteAllText(script, string.Join(";\n", graph), new UTF8Encoding(false));
            args.AddRange(new[] { context.GraphFileOption, script, "-map", processing == null ? "[vout]" : "[processed_video]" });
            if (music) args.AddRange(new[] { "-map", processing == null ? "[audio]" : "[processed_audio]", "-c:a", "aac", "-b:a", processing == null ? "192k" : "160k" }); else args.Add("-an");
            args.AddRange(processing == null ? EncoderArguments(encoder, context.EncoderThreads) : ProcessingEngine.FinalEncoderArguments(encoder,processing.Profile,context.EncoderThreads));
            if (processing != null) args.AddRange(new[] { "-map_metadata", "-1", "-map_metadata:s:v", "-1", "-map_metadata:s:a", "-1", "-map_chapters", "-1", "-metadata:s:v:0", "rotate=0" });
            args.AddRange(new[] { "-t", Core.N(processing == null ? dur : processing.Duration), "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output });
            return args;
        }
        internal static async Task SelectEncoder(AssemblyTemplate t, string ffmpeg, string work, AssemblyRenderContext context, CancellationToken ct) {
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
            AssemblyRenderContext context = null, Action<double> fraction = null, IList<AssemblyVideo> catalog = null, Random segmentRandom = null, AssemblyMixTracker segmentMix = null, bool processOutput = false) {
            t = t.WithVideoSubtitleStyle(segmentRandom ?? new Random());
            context = context ?? new AssemblyRenderContext(); var total = Stopwatch.StartNew(); var stage = Stopwatch.StartNew();
            var report = new AssemblyRenderReport { Width = t.Width, Height = t.Height, Fps = t.Fps, Duration = t.Duration,
                RequestedMode = t.RenderMode, EncoderThreads = context.EncoderThreads, FilterThreads = context.FilterThreads };
            Directory.CreateDirectory(work);
            string cache = context.CacheDirectory ?? Path.Combine(work, "raster-cache"); Directory.CreateDirectory(cache);
            progress?.Invoke(AssemblySpeechPlan.SubtitlesEnabled(t) ? "Готовлю озвучку и субтитры…" : "Готовлю озвучку…");
            await AssemblySpeechPlan.Prepare(t, p, ffmpeg, probe, work, ct, progress).ConfigureAwait(false);
            if (catalog != null && catalog.Count > 0)
                AssemblySceneVideoBuilder.EnsureSegments(t, p, catalog, segmentRandom ?? new Random(), segmentMix);
            // Loaded recipes may lack audio metadata; resolve before subtitle source selection.
            foreach (var video in SourceVideos(p).GroupBy(v => v.Path).Select(g => g.First())) {
                var media = Core.VideoInfo(await Core.Probe(probe, video.Path, ct).ConfigureAwait(false));
                foreach (var source in SourceVideos(p).Where(v => v.Path == video.Path)) source.HasAudio = media.HasAudio;
            }
            await AssemblySpeechPlan.PrepareVideoCaptions(t, p, ffmpeg, probe, work, ct, progress).ConfigureAwait(false);
            double roll = p.RollDuration(t);
            report.Duration = roll;
            progress?.Invoke("Готовлю изображения…");
            foreach (var layer in p.Scenes.SelectMany(scene => scene.Layers).Concat(p.GlobalLayers)) {
                ct.ThrowIfCancellationRequested();
                if (AssemblyOverlayMedia.IsAnimated((layer.Layer.IsText ? "" : layer.Asset.Path))) { layer.Raster = null; continue; }
                string key = AssemblyRenderContext.RasterKey(t, layer);
                if (context.Rasters.TryGetValue(key, out string cached) && File.Exists(cached)) { layer.Raster = cached; report.RasterCacheHits++; continue; }
                string path = Path.Combine(cache, key + ".png");
                try { rasterize(t, layer, path); } catch (Exception e) { throw new Exception("Не удалось прочитать картинку «" + Path.GetFileName((layer.Layer.IsText ? "" : layer.Asset.Path)) + "»: " + e.Message, e); }
                layer.Raster = path; context.Rasters[key] = path;
            }
            if (BurnCaptions(t, p)) {
                p.Captions = WarmupSubtitles.FitToDuration(p.Captions, roll);
                foreach (var cue in p.Captions) {
                    if (double.IsNaN(cue.Start) || double.IsInfinity(cue.Start) || double.IsNaN(cue.End) || double.IsInfinity(cue.End) || cue.Start < 0 || cue.End <= cue.Start || cue.End > roll + .001)
                        throw new Exception("Субтитры не попали в длительность ролика (" + roll.ToString("0.#", Core.Inv) + " с). Открой «Субтитры» и проверь начало/конец строк.");
                }
                if (p.Captions.Count > 0)
                    WarmupSubtitles.Rasterize(p.Captions, t.SubtitleStyle, work, t.Width, t.Height);
            }
            var processing = processOutput ? AssemblyProcessing.Prepare(t,new Media { Width=t.Width, Height=t.Height, Fps=t.Fps+"/1", Duration=roll, VideoDuration=roll },segmentRandom ?? new Random(),AssemblyTimeline.MainStart(t,p)) : null;
            double outputDuration = processing == null ? roll : processing.Duration;
            report.Duration = outputDuration;
            report.RasterSeconds = stage.Elapsed.TotalSeconds; stage.Restart();
            progress?.Invoke("Проверяю ускорение…");
            if (!context.GraphSupportChecked) {
                // New FFmpeg removed filter_complex_script in favour of the generic
                // file-valued option syntax. Inspect the actual installed binary once.
                string help = await Core.Tool(ffmpeg, new[] { "-hide_banner", "-h", "full" }, ct, null, 15).ConfigureAwait(false);
                context.GraphFileOption = help.Contains("-filter_complex_script") ? "-filter_complex_script" : "-/filter_complex";
                context.GraphSupportChecked = true;
            }
            await SelectEncoder(t, ffmpeg, work, context, ct).ConfigureAwait(false);
            report.EncoderCheckSeconds = stage.Elapsed.TotalSeconds; stage.Restart();
            async Task Encode() {
                report.Encoder = context.Encoder; report.EncodeAttempts++;
                progress?.Invoke("Собираю · " + (context.Encoder == "libx264" ? "процессор" : "видеокарта"));
                var args = Arguments(t, p, context.Encoder, Path.Combine(work, "render.ffgraph"), output, context, processing);
                await Core.Tool(ffmpeg, args, ct, line => {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) fraction?.Invoke(Math.Min(.99, Math.Max(0, Core.Parse(line.Substring(12)) / 1000000 / outputDuration)));
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
            if (Math.Abs(m.VideoDuration - outputDuration) > .15 || m.Width != t.Width || m.Height != t.Height) throw new Exception("Готовый ролик не прошёл проверку длительности или размера кадра.");
            if ((HasAudio(p) || NativeAudio(t, p) || p.Scenes.Any(s=>t.SceneNativeAudioVolume(s.Scene)>0 && s.Layers.Any(l=>l.Layer.Enabled && l.Layer.AudioEnabled && !l.Layer.IsText && l.Asset!=null && AssemblyOverlayMedia.HasAudio(l.Asset.Path)))) && !m.HasAudio) throw new Exception("В готовом ролике отсутствует звуковая дорожка.");
            report.ValidationSeconds = stage.Elapsed.TotalSeconds; report.TotalSeconds = total.Elapsed.TotalSeconds; context.LastReport = report;
            fraction?.Invoke(1); progress?.Invoke("Готово за " + report.TotalSeconds.ToString("0.0", Core.Inv) + " с · " + report.Encoder);
        }
    }
}
