using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class AssemblyEngine {
        // The inputs here are fully composited scene clips, so every image moves with its video.
        public static string TransitionFilter(string key) {
            if (key == "swipe") return "transition=smoothleft";
            if (key != "zoomsoft" && key != "zoomout" && key != "pull") return "transition=" + key;
            string progress="(1-P)", outgoing, incoming;
            if (key == "zoomout") { outgoing="(1-.22*"+progress+"*"+progress+")"; incoming="(1+.22*P*P)"; }
            else { string strength=key=="pull" ? "1.4" : ".28"; outgoing="(1+"+strength+"*"+progress+"*"+progress+")"; incoming="(1+"+strength+"*P*P)"; }
            string a=Sample("a",outgoing), b=Sample("b",incoming), weight="(P*P*(3-2*P))";
            return "transition=custom:expr='"+a+"*"+weight+"+"+b+"*(1-"+weight+")'";
        }
        static string Sample(string source,string scale) {
            string x="((X-W/2)/"+scale+"+W/2)", y="((Y-H/2)/"+scale+"+H/2)";
            string coordinates="min(W-1,max(0,"+x+")),min(H-1,max(0,"+y+"))";
            return "if(eq(PLANE,0),"+source+"0("+coordinates+"),if(eq(PLANE,1),"+source+"1("+coordinates+"),if(eq(PLANE,2),"+source+"2("+coordinates+"),"+source+"3("+coordinates+"))))";
        }
        public static async Task<List<AssemblyVideo>> ReadVideos(AssemblyTemplate t, string ffmpeg, string probe, CancellationToken ct) {
            if (t.Scenes.Count == 0) return new List<AssemblyVideo>();
            for (int i=0;i<t.Scenes.Count;i++) if (AssemblyFiles.Pool(t.Resolve(t.ContinuousVideo?t.Videos:t.VideoSource(t.Scenes[i])),AssemblyFiles.VideoExtensions).Count==0) throw new Exception("Нет видео для сцены "+(i+1)+". Выбери общие видео или загрузи свои для этой сцены.");
            if(t.SplitLayout!="none" && AssemblyFiles.Pool(t.Resolve(t.SecondaryVideos),AssemblyFiles.VideoExtensions).Count==0)throw new Exception("Добавь второе видео для разделённого экрана.");
            var paths = t.Scenes.SelectMany(s => AssemblyFiles.Pool(t.Resolve(t.ContinuousVideo?t.Videos:t.VideoSource(s)), AssemblyFiles.VideoExtensions)).Concat(t.SplitLayout!="none"?AssemblyFiles.Pool(t.Resolve(t.SecondaryVideos),AssemblyFiles.VideoExtensions):new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) throw new Exception("Не найдены видео. Нажми «Видео» слева и добавь хотя бы один файл.");
            var result = new List<AssemblyVideo>();
            foreach (var path in paths) {
                ct.ThrowIfCancellationRequested();
                string ready = await VideoSdr.EnsureReady(ffmpeg, probe, path, ct).ConfigureAwait(false);
                var m = Core.VideoInfo(await Core.Probe(probe, ready, ct).ConfigureAwait(false));
                result.Add(new AssemblyVideo { Path = ready, Duration = m.VideoDuration, Hash = AssemblyFiles.Hash(ready) });
            }
            var audioPaths=AssemblyFiles.Pool(t.Resolve(t.Music),AssemblyFiles.MusicExtensions).Concat(t.Scenes.SelectMany(s => AssemblyFiles.Pool(t.Resolve(s.Music),AssemblyFiles.MusicExtensions))).ToList();
            foreach (var line in t.SpeechLines ?? new List<AssemblySpeechLine>()) {
                string speech = t.Resolve(line.AudioPath);
                if (!string.IsNullOrWhiteSpace(speech) && File.Exists(speech)) audioPaths.Add(speech);
            }
            if(!string.IsNullOrWhiteSpace(t.Voiceover)) {string voice=t.ResolveAudio(t.Voiceover);if(!string.IsNullOrWhiteSpace(voice)&&File.Exists(voice))audioPaths.Add(voice);}
            foreach (string path in audioPaths.Distinct(StringComparer.OrdinalIgnoreCase)) {
                try { Core.AudioDuration(await Core.Probe(probe, path, ct).ConfigureAwait(false)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { throw new Exception("Не удалось прочитать музыку «" + Path.GetFileName(path) + "»: " + e.Message, e); }
            }
            if (t.PackStudioVersion == 0 && !string.IsNullOrWhiteSpace(t.Music) && !File.Exists(t.Resolve(t.Music))) throw new Exception("Не найден музыкальный файл: " + t.Resolve(t.Music));
            return result;
        }
        public static List<string> SceneArguments(AssemblyTemplate t, AssemblyScenePlan s, string output) {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", "1" };
            if (AssemblyTimeline.LoopInput(t,s)) args.AddRange(new[] { "-stream_loop", "-1" });
            args.AddRange(new[] { "-ss", Core.N(AssemblyTimeline.SourceStart(t,s)), "-t", Core.N(AssemblyTimeline.ContentDuration(t,s)), "-i", s.Video.Path });
            foreach (var layer in s.Layers) args.AddRange(new[] { "-loop", "1", "-framerate", t.Fps.ToString(), "-i", layer.Raster });
            var graph = new List<string>();
            graph.Add("[0:v:0]setpts=PTS-STARTPTS,scale=" + t.Width + ":" + t.Height + ":force_original_aspect_ratio=increase,crop=" + t.Width + ":" + t.Height + ",setsar=1,fps=" + t.Fps + ",format=yuv420p,"+(t.SceneTimingVersion>0?"tpad=stop_mode=clone:stop_duration="+Core.N(s.Scene.Duration)+",":"")+"trim=duration=" + Core.N(AssemblyTimeline.ContentDuration(t,s)) + "[base]");
            string current = "base";
            for (int i = 0; i < s.Layers.Count; i++) {
                var layer = s.Layers[i]; string next = "v" + i;
                double end = layer.Layer.End == 0 || Math.Abs(layer.Layer.End-s.Scene.Duration)<.001 ? AssemblyTimeline.ContentDuration(t,s) : layer.Layer.End;
                graph.Add("[" + current + "][" + (i + 1) + ":v:0]overlay=0:0:format=auto:enable='between(t," + Core.N(layer.Layer.Start) + "," + Core.N(end) + ")'[" + next + "]");
                current = next;
            }
            graph.Add("[" + current + "]"+AssemblyTimeline.Padding(t,s)+"format=yuv420p[out]");
            args.AddRange(new[] { "-filter_complex", string.Join(";", graph), "-map", "[out]", "-an", "-t", Core.N(s.RenderDuration), "-c:v", "libx264", "-preset", "fast", "-crf", "20", "-threads", "2", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output });
            return args;
        }
        public static List<string> JoinArguments(AssemblyTemplate t, AssemblyPlan p, IList<string> scenes, string output) {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", "1" };
            foreach (string scene in scenes) args.AddRange(new[] { "-i", scene });
            bool music = !string.IsNullOrWhiteSpace(p.Music);
            if (music) args.AddRange(new[] { "-stream_loop", "-1", "-i", p.Music });
            var graph = new List<string>();
            for (int i = 0; i < scenes.Count; i++) graph.Add("[" + i + ":v:0]settb=1/" + t.Fps + ",setpts=PTS-STARTPTS,fps=" + t.Fps + ",format=yuv420p[s" + i + "]");
            string current;
            if (t.Transition == "cut" || t.TransitionDuration == 0) {
                graph.Add(string.Join("", Enumerable.Range(0, scenes.Count).Select(i => "[s" + i + "]")) + "concat=n=" + scenes.Count + ":v=1:a=0[vout]"); current = "vout";
            } else {
                current = "s0"; double offset = 0;
                for (int i = 1; i < scenes.Count; i++) {
                    offset += p.Scenes[i - 1].Scene.Duration+(t.SceneTimingVersion>0?p.Scenes[i-1].IncomingPad:0); string next = "joined" + i;
                    graph.Add("[" + current + "][s" + i + "]xfade=" + TransitionFilter(AssemblyTimeline.Transition(t,p.Scenes[i-1])) + ":duration=" + Core.N(t.TransitionSeconds) + ":offset=" + Core.N(offset) + "[" + next + "]"); current = next;
                }
            }
            if (music) {
                double fade = Math.Min(.2, t.Duration / 4);
                graph.Add("[" + scenes.Count + ":a:0]asetpts=PTS-STARTPTS,volume=" + Core.N(t.MusicVolume) + ",afade=t=in:d=" + Core.N(fade) + ",afade=t=out:st=" + Core.N(t.Duration - fade) + ":d=" + Core.N(fade) + ",apad,atrim=duration=" + Core.N(t.Duration) + "[audio]");
            }
            args.AddRange(new[] { "-filter_complex", string.Join(";", graph), "-map", "[" + current + "]" });
            if (music) args.AddRange(new[] { "-map", "[audio]", "-c:a", "aac", "-b:a", "192k" }); else args.Add("-an");
            args.AddRange(new[] { "-t", Core.N(t.Duration), "-c:v", "libx264", "-preset", "fast", "-crf", "20", "-threads", "2", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output });
            return args;
        }
        public static Task Render(AssemblyTemplate t, AssemblyPlan p, string ffmpeg, string ffprobe, string output, string work, Action<AssemblyTemplate, AssemblyLayerPlan, string> rasterize, CancellationToken ct, Action<string> progress, AssemblyRenderContext context = null, Action<double> fraction = null) {
            return AssemblyRender.Run(t, p, ffmpeg, ffprobe, output, work, rasterize, ct, progress, context, fraction);
        }
        // Retained only as a reference for controlled before/after benchmarks.
        public static async Task RenderLegacy(AssemblyTemplate t, AssemblyPlan p, string ffmpeg, string ffprobe, string output, string work, Action<AssemblyTemplate, AssemblyLayerPlan, string> rasterize, CancellationToken ct, Action<string> progress) {
            if(t.ContinuousVideo || t.SplitLayout!="none" || p.GlobalLayers.Count>0 || !string.IsNullOrWhiteSpace(p.Voiceover) || p.Scenes.Any(s => !string.IsNullOrWhiteSpace(s.Music)) || t.SubtitleStyle.Enabled && p.Captions.Count>0) {
                await AssemblyRender.Run(t,p,ffmpeg,ffprobe,output,work,rasterize,ct,progress).ConfigureAwait(false);return;
            }
            Directory.CreateDirectory(work); var paths = new List<string>();
            for (int i = 0; i < p.Scenes.Count; i++) {
                ct.ThrowIfCancellationRequested(); var scene = p.Scenes[i];
                for (int j = 0; j < scene.Layers.Count; j++) {
                    string png = Path.Combine(work, "layer-" + i + "-" + j + ".png"); try { rasterize(t, scene.Layers[j], png); }
                    catch (Exception e) { throw new Exception("Не удалось прочитать картинку «" + Path.GetFileName(scene.Layers[j].Asset.Path) + "» в сцене " + (i + 1) + ": " + e.Message,e); }
                    scene.Layers[j].Raster = png;
                }
                string path = Path.Combine(work, "scene-" + i + ".mp4"); paths.Add(path);
                progress?.Invoke("Сцена " + (i + 1) + " из " + p.Scenes.Count);
                await Core.Tool(ffmpeg, SceneArguments(t, scene, path), ct, null, 900).ConfigureAwait(false);
            }
            progress?.Invoke("Переходы и музыка…");
            await Core.Tool(ffmpeg, JoinArguments(t, p, paths, output), ct, null, 1800).ConfigureAwait(false);
            var m = Core.VideoInfo(await Core.Probe(ffprobe, output, ct).ConfigureAwait(false));
            if (Math.Abs(m.VideoDuration - t.Duration) > .15 || m.Width != t.Width || m.Height != t.Height) throw new Exception("Готовый ролик не прошёл проверку длительности или размера кадра.");
            if (!string.IsNullOrWhiteSpace(p.Music) && !m.HasAudio) throw new Exception("В готовом ролике отсутствует музыка.");
        }
        public static async Task<BatchResult> Run(AssemblyTemplate t, string ffmpeg, string ffprobe, string historyFolder, Action<AssemblyTemplate, AssemblyLayerPlan, string> rasterize, IProgress<Update> progress, CancellationToken ct) {
            t.Validate();
            if (t.Scenes.Count==0) throw new Exception("Добавь хотя бы одну сцену перед сборкой.");
            if (string.IsNullOrWhiteSpace(t.Output)) throw new Exception("Выберите папку готовых роликов.");
            Directory.CreateDirectory(t.Output);
            string historyScope = t.HistoryScope;
            AssemblyHistoryStore.EnsureFolder(historyFolder, historyScope);
            string historyPath = AssemblyHistoryStore.PathFor(historyFolder, historyScope);
            var result = new BatchResult(); string work = Path.Combine(t.Output, ".assembly-" + Guid.NewGuid().ToString("N")), limitNote="";
            try {
                // One lock per ledger so RU and EN batches can run without clobbering each other.
                using (var gate = new FileStream(historyPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                    var history = AssemblyHistoryStore.Load(historyFolder, historyScope);
                    progress.Report(new Update("Проверяю материалы…", 0));
                    var videos = await ReadVideos(t, ffmpeg, ffprobe, ct).ConfigureAwait(false);
                    var random=new Random(); var mix=new AssemblyMixTracker();
                    var usedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var renderContext = new AssemblyRenderContext { CacheDirectory = Path.Combine(work, "raster-cache") };
                    while (result.Outputs.Count<t.Count) {
                        ct.ThrowIfCancellationRequested();
                        var batch = AssemblyPlanner.Create(t, videos, history, random, Math.Min(256,t.Count-result.Outputs.Count),mix,ct);
                        if (batch.Plans.Count == 0) { if (result.Outputs.Count==0) throw new Exception(batch.Limit); limitNote=batch.Limit; break; }
                        for (int i = 0; i < batch.Plans.Count; i++) {
                            int global=result.Outputs.Count;
                            ct.ThrowIfCancellationRequested(); var plan = batch.Plans[i];
                            string itemWork = Path.Combine(work, global.ToString()); Directory.CreateDirectory(itemWork);
                            string partial = Path.Combine(itemWork, "result.mp4");
                            string assembledStem = "assembled-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                            string destination = AssemblyFiles.SafeImportDestination(t.Output, Path.Combine(t.Output, assembledStem + ".mp4"));
                            try {
                                double renderFraction = 0;
                                await Render(t, plan, ffmpeg, ffprobe, partial, itemWork, rasterize, ct,
                                    s => progress.Report(new Update("Ролик " + (global + 1) + " из " + t.Count + " · " + s, 100.0 * (global + renderFraction) / t.Count)), renderContext,
                                    f => { renderFraction = f; progress.Report(new Update("Ролик " + (global + 1) + " из " + t.Count + " · " + (f * 100).ToString("0", Core.Inv) + "%", 100.0 * (global + f) / t.Count)); }).ConfigureAwait(false);
                                ct.ThrowIfCancellationRequested();
                                // Tag and validate the private file before making it visible as a result.
                                string chosenTitle=AssemblyOutputTags.PickTitle(t.TitleBank,random,usedTitles);
                                var metadata=await AssemblyOutputTags.Apply(ffmpeg,ffprobe,partial,chosenTitle,t.OutputMetadata,random,ct).ConfigureAwait(false);
                                ct.ThrowIfCancellationRequested();
                                AssemblyHistoryRecord reservation=null; bool published=false;
                                try {
                                    AssemblyOutputFiles.Prepare(destination);
                                    AssemblyFiles.Save(AssemblyOutputFiles.Report(destination,".assembly.xml"),plan);
                                    AssemblyFiles.Save(AssemblyOutputFiles.Report(destination,".performance.xml"),renderContext.LastReport);
                                    File.WriteAllLines(AssemblyOutputFiles.Report(destination,".metadata.txt"),metadata,System.Text.Encoding.UTF8);
                                    reservation=AssemblyHistoryStore.Reserve(historyFolder,history,plan,destination,t.Id,partial,historyScope);
                                    ct.ThrowIfCancellationRequested();
                                    File.Move(partial,destination); published=true; result.Outputs.Add(destination);
                                    AssemblyOutputFiles.DeleteLegacySidecars(destination);
                                    AssemblyHistoryStore.Commit(historyFolder,history,reservation,historyScope);
                                } finally {
                                    if (!published) {
                                        AssemblyOutputFiles.DeleteReports(destination);
                                        if (reservation!=null) AssemblyHistoryStore.Release(historyFolder,history,reservation,historyScope);
                                    }
                                    // Once published, its durable reservation survives any commit failure.
                                }
                                try {Directory.Delete(itemWork,true);} catch (IOException) { }
                            } catch (OperationCanceledException) { throw; }
                            catch (Exception e) { result.Errors.Add("Ролик " + (global + 1) + ": " + e.Message); break; }
                        }
                        if (batch.Limit != "") { limitNote = " Запрошено " + t.Count + ": " + batch.Limit; break; }
                        if (result.Errors.Count>0) break;
                    }
                }
            } catch (OperationCanceledException) { result.Cancelled = true; }
            catch (IOException e) { result.Errors.Add("Не удалось открыть файлы или историю. Проверьте, не запущена ли другая сборка этого шаблона. " + e.Message); }
            catch (Exception e) { result.Errors.Add(e.Message); }
            finally { if (Directory.Exists(work)) try { Directory.Delete(work, true); } catch { } }
            progress.Report(new Update((result.Cancelled ? "Остановлено. " : "Готово. ") + "Роликов: " + result.Outputs.Count + (result.Errors.Count > 0 ? ". " + result.Errors[0] : limitNote), 100));
            return result;
        }
    }
}
