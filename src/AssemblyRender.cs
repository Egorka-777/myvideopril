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
        public int FilterThreads => Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
        internal static string RasterKey(AssemblyTemplate t, AssemblyLayerPlan p) {
            var l = p.Layer;
            return AssemblyFiles.HashText(string.Join("|", t.Width, t.Height, l.ObjectId, p.Asset.Hash, Path.GetFullPath(p.Asset.Path),
                Core.N(l.X), Core.N(l.Y), Core.N(l.Width), Core.N(l.Height), Core.N(l.Opacity), l.Font,
                Core.N(l.FontSize), Core.N(l.Radius), Core.N(l.Padding)));
        }
    }
    public static class AssemblyRender {
        static string Fill(int width,int height,int fps) => "fps="+fps+",scale="+width+":"+height+":force_original_aspect_ratio=increase,crop="+width+":"+height+",setsar=1,format=yuv420p";
        static bool HasAudio(AssemblyPlan p) => !string.IsNullOrWhiteSpace(p.Music) || !string.IsNullOrWhiteSpace(p.Voiceover) || p.Scenes.Any(s => !string.IsNullOrWhiteSpace(s.Music));
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
        // Image inputs contain one frame. overlay repeats that frame, so PNG is not
        // decoded 30 times per second. All layers are composited BEFORE the transition.
        public static List<string> Arguments(AssemblyTemplate t, AssemblyPlan p, string encoder, string script, string output, AssemblyRenderContext context) {
            if (p.Scenes.Count == 0) throw new Exception("Добавь хотя бы одну сцену перед сборкой.");
            double dur = p.RollDuration(t);
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", context.FilterThreads.ToString() };
            var graph = new List<string>(); int input = 0;
            bool transitions = p.Scenes.Count > 1 && t.Transition != "cut" && t.TransitionDuration > 0;
            bool split=t.SplitLayout!="none",transparentScenes=t.ContinuousVideo || split;
            int continuous=-1;
            if(t.ContinuousVideo) {
                continuous=input++; var video=p.Scenes[0].Video;
                if(t.LoopShortVideos && video.Duration+.001<dur)args.AddRange(new[]{"-stream_loop","-1"});
                args.AddRange(new[]{"-threads","1","-t",Core.N(dur),"-i",video.Path});
                graph.Add("["+continuous+":v:0]setpts=PTS-STARTPTS,"+Fill(t.Width,t.Height,t.Fps)+",trim=duration="+Core.N(dur)+"[continuous]");
            }
            for (int i = 0; i < p.Scenes.Count; i++) {
                var s = p.Scenes[i]; string current = "base" + i;
                if(transparentScenes) graph.Add("color=c=black@0:s="+t.Width+"x"+t.Height+":r="+t.Fps+":d="+Core.N(AssemblyTimeline.ContentDuration(t,s))+",format=yuva444p["+current+"]");
                if(!t.ContinuousVideo) {
                    int video=input++;
                    if (AssemblyTimeline.LoopInput(t,s)) args.AddRange(new[] { "-stream_loop", "-1" });
                    args.AddRange(new[] { "-threads", "1", "-ss", Core.N(AssemblyTimeline.SourceStart(t,s)), "-t", Core.N(AssemblyTimeline.ContentDuration(t,s)), "-i", s.Video.Path });
                    graph.Add("[" + video + ":v:0]setpts=PTS-STARTPTS,"+Fill(t.Width,t.Height,t.Fps)+","+(t.SceneTimingVersion>0?"tpad=stop_mode=clone:stop_duration="+Core.N(s.Scene.Duration)+",":"")+"trim=duration=" + Core.N(AssemblyTimeline.ContentDuration(t,s)) + "[" + (split && !t.ContinuousVideo ? "videoBase" + i : current) + "]");
                }
                for (int j = 0; j < s.Layers.Count; j++) {
                    var layer = s.Layers[j]; int picture = input++;
                    args.AddRange(new[] { "-threads", "1", "-framerate", t.Fps.ToString(), "-i", layer.Raster });
                    string next = "overlay" + i + "_" + j;
                    double end = layer.Layer.End == 0 || Math.Abs(layer.Layer.End - s.Scene.Duration) < .001 ? AssemblyTimeline.ContentDuration(t,s) : layer.Layer.End;
                    graph.Add("[" + current + "][" + picture + ":v:0]overlay=0:0:format="+(transparentScenes?"auto":"yuv420")+":repeatlast=1:eof_action=repeat:enable='between(t,"
                        + Core.N(layer.Layer.Start) + "," + Core.N(end) + ")'[" + next + "]"); current = next;
                }
                graph.Add("[" + current + "]"+AssemblyTimeline.Padding(t,s)+"settb=1/" + t.Fps + ",setpts=PTS-STARTPTS,format=" + (transparentScenes ? "yuva444p" : transitions ? "yuv444p" : "yuv420p") + "[s" + i + "]");
                if(split && !t.ContinuousVideo)graph.Add("[videoBase"+i+"]"+AssemblyTimeline.Padding(t,s)+"settb=1/"+t.Fps+",setpts=PTS-STARTPTS,format="+(transitions?"yuv444p":"yuv420p")+"[videoScene"+i+"]");
            }
            string joined;
            if (!transitions) {
                joined = "joined";
                graph.Add(string.Join("", Enumerable.Range(0, p.Scenes.Count).Select(i => "[s" + i + "]")) + "concat=n=" + p.Scenes.Count + ":v=1:a=0[" + joined + "]");
            } else {
                joined = "s0"; double offset = 0;
                for (int i = 1; i < p.Scenes.Count; i++) {
                    offset += p.Scenes[i - 1].Scene.Duration+(t.SceneTimingVersion>0?p.Scenes[i-1].IncomingPad:0); string next = "joined" + i;
                    graph.Add("[" + joined + "][s" + i + "]xfade=" + AssemblyEngine.TransitionFilter(AssemblyTimeline.Transition(t,p.Scenes[i-1])) + ":duration=" + Core.N(t.TransitionSeconds)
                        + ":offset=" + Core.N(offset) + "[" + next + "]"); joined = next;
                }
            }
            string sceneOverlay=transparentScenes?joined:null;
            if(t.ContinuousVideo)joined="continuous";
            else if(split) {
                joined="videoScene0";
                if(!transitions) {graph.Add(string.Join("",Enumerable.Range(0,p.Scenes.Count).Select(i=>"[videoScene"+i+"]"))+"concat=n="+p.Scenes.Count+":v=1:a=0[videoJoined]");joined="videoJoined";}
                else {double offset=0;for(int i=1;i<p.Scenes.Count;i++){offset+=p.Scenes[i-1].Scene.Duration+(t.SceneTimingVersion>0?p.Scenes[i-1].IncomingPad:0);string next="videoJoined"+i;graph.Add("["+joined+"][videoScene"+i+"]xfade="+AssemblyEngine.TransitionFilter(AssemblyTimeline.Transition(t,p.Scenes[i-1]))+":duration="+Core.N(t.TransitionSeconds)+":offset="+Core.N(offset)+"["+next+"]");joined=next;}}
            }
            if(split) {
                if(p.SecondaryVideo==null)throw new Exception("Не выбрано второе видео для разделённого экрана.");
                int second=input++;
                if(t.LoopShortVideos && p.SecondaryVideo.Duration+.001<dur)args.AddRange(new[]{"-stream_loop","-1"});
                args.AddRange(new[]{"-threads","1","-t",Core.N(dur),"-i",p.SecondaryVideo.Path});
                bool vertical=t.SplitLayout=="vertical" || t.SplitLayout=="top-bottom";
                int dimension=vertical?t.Height:t.Width;
                int feather=(int)Math.Round(dimension*t.SplitSoftness/2)*2;
                int span=Math.Min(dimension,((dimension+feather+3)/4)*2);
                int width=vertical?t.Width:span,height=vertical?span:t.Height;
                graph.Add("["+joined+"]"+Fill(width,height,t.Fps)+",pad="+t.Width+":"+t.Height+":0:0,format=yuv444p[splitA]");
                graph.Add("["+second+":v:0]setpts=PTS-STARTPTS,"+Fill(width,height,t.Fps)+",pad="+t.Width+":"+t.Height+":"+(vertical?"0:"+(t.Height-height):(t.Width-width)+":0")+",format=yuv444p[splitB]");
                string coordinate=vertical?"Y":"X";
                string weight=feather==0?"gte("+coordinate+","+dimension/2+")":"clip(("+coordinate+"-"+Core.N((dimension-feather)/2.0)+")/"+feather+",0,1)";
                graph.Add("[splitA][splitB]blend=all_expr='A*(1-("+weight+"))+B*("+weight+")':shortest=1[split]");joined="split";
            }
            if(sceneOverlay!=null) {graph.Add("["+joined+"]["+sceneOverlay+"]overlay=0:0:format=yuv420:shortest=1[sceneComposited]");joined="sceneComposited";}
            for(int j=0;j<p.GlobalLayers.Count;j++) {
                var layer=p.GlobalLayers[j];int picture=input++;
                args.AddRange(new[]{"-threads","1","-framerate",t.Fps.ToString(),"-i",layer.Raster});
                string next="global"+j;
                double end=layer.Layer.End==0?dur:Math.Min(layer.Layer.End,dur);
                graph.Add("["+joined+"]["+picture+":v:0]overlay=0:0:format=yuv420:repeatlast=1:eof_action=repeat:enable='gte(t,"+Core.N(layer.Layer.Start)+")*lt(t,"+Core.N(end)+")'["+next+"]");joined=next;
            }
            if(BurnCaptions(t,p)) for(int j=0;j<p.Captions.Count;j++) {
                var cue=p.Captions[j];int picture=input++;string next="caption"+j;
                args.AddRange(new[]{"-threads","1","-framerate",t.Fps.ToString(),"-i",cue.ImagePath});
                graph.Add("["+picture+":v:0]scale="+t.Width+":"+t.Height+"[captionImage"+j+"]");
                graph.Add("["+joined+"][captionImage"+j+"]overlay=0:0:format=yuv420:repeatlast=1:eof_action=repeat:enable='gte(t,"+Core.N(cue.Start)+")*lt(t,"+Core.N(cue.End)+")'["+next+"]");joined=next;
            }
            graph.Add("[" + joined + "]format=yuv420p[vout]");
            var audio=new List<string>();
            if(!string.IsNullOrWhiteSpace(p.Music)) {
                int index=input++;args.AddRange(new[]{"-stream_loop","-1","-i",p.Music});
                double fade=Math.Min(.2,dur/4);
                graph.Add("["+index+":a:0]asetpts=PTS-STARTPTS,volume="+Core.N(t.MusicVolume)+",afade=t=in:d="+Core.N(fade)+",afade=t=out:st="+Core.N(dur-fade)+":d="+Core.N(fade)+",apad,atrim=duration="+Core.N(dur)+"[music]");audio.Add("music");
            }
            double sceneTime=0;
            for(int i=0;i<p.Scenes.Count;i++) {
                var scene=p.Scenes[i];
                if(!string.IsNullOrWhiteSpace(scene.Music)) {
                    int index=input++;args.AddRange(new[]{"-stream_loop","-1","-i",scene.Music});string label="sceneAudio"+i;
                    graph.Add("["+index+":a:0]asetpts=PTS-STARTPTS,atrim=duration="+Core.N(scene.Scene.Duration)+",volume="+Core.N(scene.Scene.MusicVolume)+",adelay="+Math.Round(sceneTime*1000).ToString(Core.Inv)+":all=1,apad,atrim=duration="+Core.N(dur)+"["+label+"]");audio.Add(label);
                }
                sceneTime+=scene.Scene.Duration+(t.SceneTimingVersion>0 && t.Transition!="cut"?t.TransitionSeconds:0);
            }
            if(!string.IsNullOrWhiteSpace(p.Voiceover)) {
                int index=input++;args.AddRange(new[]{"-i",p.Voiceover});
                graph.Add("["+index+":a:0]asetpts=PTS-STARTPTS,volume=1,atrim=duration="+Core.N(dur)+"[voiceover]");audio.Add("voiceover");
            }
            bool music=audio.Count>0;
            string mixDuration = !string.IsNullOrWhiteSpace(p.Voiceover) ? "shortest" : "longest";
            if(music)graph.Add(string.Join("",audio.Select(a=>"["+a+"]"))+"amix=inputs="+audio.Count+":duration="+mixDuration+":normalize=0,atrim=duration="+Core.N(dur)+"[audio]");
            File.WriteAllText(script, string.Join(";\n", graph), new UTF8Encoding(false));
            args.AddRange(new[] { context.GraphFileOption, script, "-map", "[vout]" });
            if (music) args.AddRange(new[] { "-map", "[audio]", "-c:a", "aac", "-b:a", "192k" }); else args.Add("-an");
            args.AddRange(EncoderArguments(encoder, context.EncoderThreads));
            args.AddRange(new[] { "-t", Core.N(dur), "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output });
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
            progress?.Invoke("Готовлю озвучку и субтитры…");
            await AssemblySpeechPlan.Prepare(t, p, ffmpeg, probe, work, ct).ConfigureAwait(false);
            double roll = p.RollDuration(t);
            report.Duration = roll;
            progress?.Invoke("Готовлю изображения…");
            foreach (var layer in p.Scenes.SelectMany(scene => scene.Layers).Concat(p.GlobalLayers)) {
                ct.ThrowIfCancellationRequested(); string key = AssemblyRenderContext.RasterKey(t, layer);
                if (context.Rasters.TryGetValue(key, out string cached) && File.Exists(cached)) { layer.Raster = cached; report.RasterCacheHits++; continue; }
                string path = Path.Combine(cache, key + ".png");
                try { rasterize(t, layer, path); } catch (Exception e) { throw new Exception("Не удалось прочитать картинку «" + Path.GetFileName(layer.Asset.Path) + "»: " + e.Message, e); }
                layer.Raster = path; context.Rasters[key] = path;
            }
            if (BurnCaptions(t, p)) {
                p.Captions = WarmupSubtitles.FitToDuration(p.Captions, roll);
                foreach (var cue in p.Captions) {
                    if (double.IsNaN(cue.Start) || double.IsInfinity(cue.Start) || double.IsNaN(cue.End) || double.IsInfinity(cue.End) || cue.Start < 0 || cue.End <= cue.Start || cue.End > roll + .001)
                        throw new Exception("Субтитры не попали в длительность ролика (" + roll.ToString("0.#", Core.Inv) + " с). Открой «Субтитры» и проверь начало/конец строк.");
                }
                if (p.Captions.Count > 0)
                    WarmupSubtitles.Rasterize(p.Captions, t.SubtitleStyle, work);
            }
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
                var args = Arguments(t, p, context.Encoder, Path.Combine(work, "render.ffgraph"), output, context);
                await Core.Tool(ffmpeg, args, ct, line => {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) fraction?.Invoke(Math.Min(.99, Math.Max(0, Core.Parse(line.Substring(12)) / 1000000 / roll)));
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
            if (Math.Abs(m.VideoDuration - roll) > .15 || m.Width != t.Width || m.Height != t.Height) throw new Exception("Готовый ролик не прошёл проверку длительности или размера кадра.");
            if (HasAudio(p) && !m.HasAudio) throw new Exception("В готовом ролике отсутствует звуковая дорожка.");
            report.ValidationSeconds = stage.Elapsed.TotalSeconds; report.TotalSeconds = total.Elapsed.TotalSeconds; context.LastReport = report;
            fraction?.Invoke(1); progress?.Invoke("Готово за " + report.TotalSeconds.ToString("0.0", Core.Inv) + " с · " + report.Encoder);
        }
    }
}
