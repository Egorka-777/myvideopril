using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class AssemblyEngine {
        public static async Task<List<AssemblyVideo>> ReadVideos(AssemblyTemplate t, string probe, CancellationToken ct) {
            var paths = t.Scenes.SelectMany(s => AssemblyFiles.Pool(t.Resolve(string.IsNullOrWhiteSpace(s.Videos) ? t.Videos : s.Videos), AssemblyFiles.VideoExtensions)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) throw new Exception("Не найдены видео. Выберите папку материалов и заполните папки видео.");
            var result = new List<AssemblyVideo>();
            foreach (var path in paths) {
                ct.ThrowIfCancellationRequested();
                var m = Core.VideoInfo(await Core.Probe(probe, path, ct).ConfigureAwait(false));
                result.Add(new AssemblyVideo { Path = path, Duration = m.VideoDuration, Hash = AssemblyFiles.Hash(path) });
            }
            if (!string.IsNullOrWhiteSpace(t.Music)) {
                string path = t.Resolve(t.Music);
                if (!File.Exists(path)) throw new Exception("Не найден музыкальный файл: " + path);
                Core.AudioDuration(await Core.Probe(probe, path, ct).ConfigureAwait(false));
            }
            return result;
        }
        public static List<string> SceneArguments(AssemblyTemplate t, AssemblyScenePlan s, string output) {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", "1", "-ss", Core.N(s.VideoStart), "-t", Core.N(s.RenderDuration), "-i", s.Video.Path };
            foreach (var layer in s.Layers) args.AddRange(new[] { "-loop", "1", "-framerate", t.Fps.ToString(), "-i", layer.Raster });
            var graph = new List<string>();
            graph.Add("[0:v:0]setpts=PTS-STARTPTS,scale=" + t.Width + ":" + t.Height + ":force_original_aspect_ratio=increase,crop=" + t.Width + ":" + t.Height + ",setsar=1,fps=" + t.Fps + ",format=yuv420p,trim=duration=" + Core.N(s.RenderDuration) + "[base]");
            string current = "base";
            for (int i = 0; i < s.Layers.Count; i++) {
                var layer = s.Layers[i]; string next = "v" + i;
                double end = layer.Layer.End == 0 ? s.RenderDuration : layer.Layer.End;
                graph.Add("[" + current + "][" + (i + 1) + ":v:0]overlay=0:0:format=auto:enable='between(t," + Core.N(layer.Layer.Start) + "," + Core.N(end) + ")'[" + next + "]");
                current = next;
            }
            graph.Add("[" + current + "]format=yuv420p[out]");
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
                    offset += p.Scenes[i - 1].Scene.Duration; string next = "joined" + i;
                    graph.Add("[" + current + "][s" + i + "]xfade=transition=" + t.Transition + ":duration=" + Core.N(t.TransitionDuration) + ":offset=" + Core.N(offset) + "[" + next + "]"); current = next;
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
        public static async Task Render(AssemblyTemplate t, AssemblyPlan p, string ffmpeg, string ffprobe, string output, string work, Action<AssemblyTemplate, AssemblyLayerPlan, string> rasterize, CancellationToken ct, Action<string> progress) {
            Directory.CreateDirectory(work); var paths = new List<string>();
            for (int i = 0; i < p.Scenes.Count; i++) {
                ct.ThrowIfCancellationRequested(); var scene = p.Scenes[i];
                for (int j = 0; j < scene.Layers.Count; j++) {
                    string png = Path.Combine(work, "layer-" + i + "-" + j + ".png"); rasterize(t, scene.Layers[j], png); scene.Layers[j].Raster = png;
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
            if (string.IsNullOrWhiteSpace(t.Output)) throw new Exception("Выберите папку готовых роликов.");
            Directory.CreateDirectory(t.Output); Directory.CreateDirectory(historyFolder);
            string historyPath = Path.Combine(historyFolder, t.Id + ".xml");
            var result = new BatchResult(); string work = Path.Combine(t.Output, ".assembly-" + Guid.NewGuid().ToString("N"));
            try {
                // A second process may not allocate the same headlines while the batch is rendering.
                using (var gate = new FileStream(historyPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                    var history = File.Exists(historyPath) ? AssemblyFiles.Load<AssemblyHistory>(historyPath) : new AssemblyHistory();
                    progress.Report(new Update("Проверяю материалы…", 0));
                    var videos = await ReadVideos(t, ffprobe, ct).ConfigureAwait(false);
                    var batch = AssemblyPlanner.Create(t, videos, history, new Random(), t.Count);
                    if (batch.Plans.Count == 0) throw new Exception(batch.Limit);
                    for (int i = 0; i < batch.Plans.Count; i++) {
                        ct.ThrowIfCancellationRequested(); var plan = batch.Plans[i];
                        string itemWork = Path.Combine(work, i.ToString()); Directory.CreateDirectory(itemWork);
                        string partial = Path.Combine(itemWork, "result.mp4");
                        string destination = Path.Combine(t.Output, "assembled-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4");
                        try {
                            await Render(t, plan, ffmpeg, ffprobe, partial, itemWork, rasterize, ct, s => progress.Report(new Update("Ролик " + (i + 1) + " из " + batch.Plans.Count + " · " + s, 100.0 * i / batch.Plans.Count))).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            // Keep an exact, readable recipe next to each completed video.
                            AssemblyFiles.Save(destination + ".assembly.xml", plan);
                            File.Move(partial, destination);
                            history.UsedImages.AddRange(plan.UniqueImages); history.Combinations.Add(plan.Signature); history.Outputs.Add(destination);
                            try { AssemblyFiles.Save(historyPath, history); } catch { File.Delete(destination); File.Delete(destination + ".assembly.xml"); throw; }
                            result.Outputs.Add(destination);
                        } catch (OperationCanceledException) { throw; }
                        catch (Exception e) { result.Errors.Add("Ролик " + (i + 1) + ": " + e.Message); break; }
                    }
                    if (batch.Limit != "") result.Errors.Add("Собрано меньше запрошенного: " + batch.Limit);
                }
            } catch (OperationCanceledException) { result.Cancelled = true; }
            catch (IOException e) { result.Errors.Add("Не удалось открыть файлы или историю. Проверьте, не запущена ли другая сборка этого шаблона. " + e.Message); }
            catch (Exception e) { result.Errors.Add(e.Message); }
            finally { if (Directory.Exists(work)) try { Directory.Delete(work, true); } catch { } }
            progress.Report(new Update((result.Cancelled ? "Остановлено. " : "Готово. ") + "Роликов: " + result.Outputs.Count + (result.Errors.Count > 0 ? ". " + result.Errors[0] : ""), 100));
            return result;
        }
    }
}
