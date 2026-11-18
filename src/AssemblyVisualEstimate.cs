using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public sealed class AssemblyVisualEstimateReport {
        public double? DifferencePercent, MinimumPercent;
        public string Summary = "", Details = "";
        public bool Completed;
        public int Samples;
    }

    // Independent of the weighted, post-render AssemblyDifferenceAnalysis index.
    public static class AssemblyVisualEstimate {
        public const double TargetPercent = 40;
        public const int SampleSide = 320;
        public const string MetricDetails = "Приблизительная доля заметно изменённых пикселей контрольных кадров, не процент уникальности. " +
            "Порог: среднеквадратичная RGB-разница с весами 0,2126/0,7152/0,0722 >= 24 из 255. " +
            "Кадры до 320 пикселей по длинной стороне; среднее взвешено длительностью интервалов сцен, минимум — по кадрам. " +
            "Исходник и будущая сборка сначала одинаково заполняют выходной кадр (scale/crop рендера), поэтому смена формата сама по себе баллов не даёт. " +
            "Музыка, голос, метаданные и техническое перекодирование не входят в процент. Это не гарантия для платформ и не индекс анализа готового файла.";

        sealed class FrameSource { public AssemblyVideo Video; public double Time; }
        sealed class LayerImage : IDisposable {
            public AssemblyLayerPlan Plan;
            public Bitmap Image;
            public double Start, End;
            public void Dispose() { Image.Dispose(); }
        }
        public static Task<AssemblyVisualEstimateReport> Analyze(AssemblyTemplate template, string ffmpeg, string ffprobe, string work, CancellationToken ct) {
            // Own the snapshot before returning; caller may immediately edit studio controls.
            var snapshot = Store.Clone(template);
            return Task.Run(() => AnalyzeSnapshot(snapshot, ffmpeg, ffprobe, work, ct), ct);
        }
        static async Task<AssemblyVisualEstimateReport> AnalyzeSnapshot(AssemblyTemplate t, string ffmpeg, string probe, string work, CancellationToken ct) {
            var report = new AssemblyVisualEstimateReport();
            string temp = null;
            var notes = new HashSet<string>();
            var images = new List<LayerImage>();
            try {
                ct.ThrowIfCancellationRequested();
                if (t.Scenes == null || t.Scenes.Count == 0) throw new Exception("Добавьте сцену и исходное видео.");
                if (t.Width <= 0 || t.Height <= 0) throw new Exception("Не задан размер выходного кадра.");
                if (t.SplitLayout != "none") throw new NotSupportedException("Предварительная метрика разделённого экрана пока недоступна; процент не рассчитан.");
                temp = Path.Combine(work, "visual-estimate-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                int w = Math.Max(2, (int)Math.Round(t.Width * Math.Min(1, (double)SampleSide / Math.Max(t.Width, t.Height)) / 2) * 2);
                int h = Math.Max(2, (int)Math.Round((double)t.Height / t.Width * w / 2) * 2);
                var catalog = new List<AssemblyVideo>();
                var cache = new Dictionary<string, AssemblyVideo>(StringComparer.OrdinalIgnoreCase);
                async Task<AssemblyVideo> Video(string path) {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(path)) throw new Exception("Нет исходного видео в выбранной пачке.");
                    if (!cache.TryGetValue(path, out AssemblyVideo v)) {
                        var m = Core.VideoInfo(await Core.Probe(probe, path, ct).ConfigureAwait(false));
                        v = new AssemblyVideo { Path = path, Duration = m.VideoDuration, Hash = AssemblyFiles.HashText(path) };
                        cache.Add(path, v); catalog.Add(v);
                    }
                    return v;
                }
                string FirstVideo(string source) => AssemblyFiles.Pool(t.Resolve(source), AssemblyFiles.VideoExtensions).FirstOrDefault();
                var plan = new AssemblyPlan();
                AssemblyVideo continuous = t.ContinuousVideo ? await Video(FirstVideo(t.Videos)).ConfigureAwait(false) : null;
                foreach (var scene in t.Scenes) {
                    var v = continuous ?? await Video(FirstVideo(t.VideoSource(scene))).ConfigureAwait(false);
                    var s = new AssemblyScenePlan { Scene = scene, Video = v, RenderDuration = scene.Duration, ContentSeconds = scene.Duration };
                    plan.Scenes.Add(s);
                    foreach (var pack in AssemblySceneVideoBuilder.Packs(t, scene).Skip(1)) {
                        string path = FirstVideo(pack.Source);
                        if (path != null) await Video(path).ConfigureAwait(false);
                    }
                }
                AssemblyTimeline.Complete(t, plan, new Random(7));
                AssemblyTimeline.ApplyVideoDuration(t, plan);
                AssemblySceneVideoBuilder.EnsureSegments(t, plan, catalog, new Random(7));
                double contentTotal = plan.Scenes.Sum(s => AssemblyTimeline.ContentDuration(t, s));
                double roll = t.HasVideoDuration ? contentTotal : Math.Max(contentTotal, t.Duration);
                if (roll <= 0 || double.IsNaN(roll) || double.IsInfinity(roll)) throw new Exception("Не удалось определить длительность сцен.");
                var backgroundPaths = AssemblyFiles.Pool(t.Resolve(t.Backgrounds), AssemblyFiles.BackgroundExtensions);
                Bitmap background = null;
                try {
                    if (backgroundPaths.Count > 0) {
                        string path = backgroundPaths[0];
                        if (AssemblyOverlayMedia.IsAnimated(path) || AssemblyFiles.VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                            notes.Add("Движущийся фон не учтён; его влияние не добавлено к проценту.");
                        else background = await Extract(ffmpeg, path, 0, t, w, h, false, true, temp, ct).ConfigureAwait(false);
                    }
                    var globals = PrepareLayers(t, t.GlobalLayers, null, roll, w, h, notes, images, ct);
                    double weighted = 0, minimum = 100, scenePosition = 0;
                    for (int si = 0; si < plan.Scenes.Count; si++) {
                        var scene = plan.Scenes[si];
                        double duration = AssemblyTimeline.ContentDuration(t, scene);
                        var locals = PrepareLayers(t, scene.Scene.Layers, scene, duration, w, h, notes, images, ct);
                        // Split at every known visibility boundary, not just equidistant frames:
                        // a 0.1s logo cannot acquire the weight of a whole scene.
                        var cuts = new SortedSet<double> { 0, duration, duration / 3, duration * 2 / 3 };
                        foreach (var layer in locals) AddLayerCuts(cuts, layer, 0, duration);
                        foreach (var layer in globals) AddLayerCuts(cuts, layer, scenePosition, duration);
                        double partTime = 0;
                        foreach (var segment in scene.VideoSegments) { partTime += segment.Duration; AddCut(cuts, partTime, duration); }
                        var times = cuts.ToArray();
                        for (int i = 0; i + 1 < times.Length; i++) {
                            ct.ThrowIfCancellationRequested();
                            double time = (times[i] + times[i + 1]) / 2;
                            FrameSource source = Source(t, scene, time, scenePosition);
                            using (var original = await Extract(ffmpeg, source.Video.Path, source.Time, t, w, h, false, false, temp, ct).ConfigureAwait(false))
                            using (var placed = await Extract(ffmpeg, source.Video.Path, source.Time, t, w, h, true, background != null && !t.BackgroundForeground, temp, ct).ConfigureAwait(false))
                            using (var future = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
                                using (var g = Graphics.FromImage(future)) {
                                    g.Clear(Color.Black);
                                    if (background != null && !t.BackgroundForeground) g.DrawImageUnscaled(background, 0, 0);
                                    g.DrawImageUnscaled(placed, 0, 0);
                                    // In ordinary scenes the real renderer composites local objects before the background.
                                    if (!t.ContinuousVideo) DrawLayers(g, locals, time);
                                    if (background != null && t.BackgroundForeground) g.DrawImageUnscaled(background, 0, 0);
                                    if (t.ContinuousVideo) DrawLayers(g, locals, time);
                                    DrawLayers(g, globals, scenePosition + time);
                                }
                                double difference = ChangedPixelPercent(original, future);
                                weighted += difference * (times[i + 1] - times[i]);
                                minimum = Math.Min(minimum, difference); report.Samples++;
                            }
                        }
                        scenePosition += duration + (t.SceneTimingVersion > 0 && t.Transition != "cut" ? t.TransitionSeconds : 0);
                    }
                    ct.ThrowIfCancellationRequested();
                    report.DifferencePercent = weighted / roll; report.MinimumPercent = minimum; report.Completed = true;
                    report.Summary = "Примерное визуальное отличие: " + report.DifferencePercent.Value.ToString("0.0", Core.Inv) + "% · минимум кадров " + minimum.ToString("0.0", Core.Inv) + "%" +
                        (minimum < TargetPercent ? " · есть кадры ниже ориентира 40% (сборка разрешена)." : " · ориентир 40% достигнут только в этой выборке.");
                } finally { background?.Dispose(); }
                notes.Add("Один детерминированный пример: первые файлы пачек, продолжения — фиксированный seed 7; история и резервы не читаются и не меняются. Другие связки могут отличаться.");
                notes.Add("Статические объекты растеризуются штатным AssemblyRaster; видео использует штатные Fill и AssemblyVideoPlacement. Наложение миниатюр — GDI+, возможны отличия краёв/цвета от FFmpeg.");
                if (t.Transition != "cut") notes.Add("Межсценовые переходы не моделируются и не получают баллов; их добавочная длительность учитывается с нулевым вкладом. Переходы продолжений не моделируются.");
                if (t.Insertions != null && t.Insertions.Count > 0) notes.Add("Вставки не учтены; процент относится только к основным сценам до вставок.");
                if (t.SubtitleStyle?.Enabled == true) notes.Add("Субтитры не учтены: до подготовки озвучки точная дорожка неизвестна.");
                if (t.OutputProcessing?.IsEnabled == true) notes.Add("Финальная обработка не учтена; её влияние не добавлено к проценту.");
                if (t.HasSceneVoiceovers || t.UsesWholeRollVoiceover) notes.Add("Использована текущая длительность сцен, без синтеза/подготовки озвучки; итоговый тайминг может измениться.");
            } catch (OperationCanceledException) { throw; }
            catch (Exception e) { report.Completed = false; report.DifferencePercent = null; report.MinimumPercent = null; report.Summary = "Визуальная оценка недоступна: " + e.Message; }
            finally {
                foreach (var image in images) image.Dispose();
                if (temp != null && Directory.Exists(temp)) {
                    try { Directory.Delete(temp, true); }
                    catch (Exception e) { notes.Add("Не удалось удалить временные файлы «" + temp + "»: " + e.Message); }
                }
                report.Details = MetricDetails + "\r\n" + string.Join("\r\n", notes.OrderBy(x => x, StringComparer.Ordinal));
            }
            return report;
        }
        static void AddLayerCuts(SortedSet<double> cuts, LayerImage item, double offset, double duration) {
            AddCut(cuts, item.Start - offset, duration); AddCut(cuts, item.End - offset, duration);
            var l = item.Plan.Layer;
            if (l.AppearanceTransition != "cut") AddCut(cuts, Math.Min(item.End, item.Start + l.AppearanceDuration) - offset, duration);
            if (l.ExitTransition != "cut") AddCut(cuts, Math.Max(item.Start, item.End - l.ExitDuration) - offset, duration);
        }
        static void AddCut(SortedSet<double> cuts, double time, double duration) { if (time > 0 && time < duration) cuts.Add(time); }
        static List<LayerImage> PrepareLayers(AssemblyTemplate t, IEnumerable<AssemblyLayer> layers, AssemblyScenePlan scene, double duration,
            int w, int h, HashSet<string> notes, List<LayerImage> owned, CancellationToken ct) {
            var result = new List<LayerImage>();
            foreach (var layer in layers.Where(l => l.Enabled)) {
                ct.ThrowIfCancellationRequested();
                var paths = layer.IsText ? new List<string> { "" } : AssemblyFiles.Pool(t.Resolve(layer.Source), t.PictureExtensions);
                if (paths.Count == 0) { notes.Add("Пустая пачка объекта «" + layer.Name + "» не участвует."); continue; }
                string path = paths[0];
                if (layer.Fixed && !string.IsNullOrWhiteSpace(layer.FixedAssetHash)) {
                    path = paths.FirstOrDefault(p => { ct.ThrowIfCancellationRequested(); return AssemblyFiles.Hash(p) == layer.FixedAssetHash; });
                    if (path == null) throw new Exception("Закреплённая картинка «" + layer.Name + "» отсутствует.");
                }
                if (AssemblyOverlayMedia.IsAnimated(path) || layer.PlaybackMode == "repeat") {
                    notes.Add("Анимация/повтор объекта «" + layer.Name + "» не учтены; процент не увеличен."); continue;
                }
                double start = Math.Max(0, layer.Start), end = layer.End == 0 ? duration : Math.Min(duration, layer.End);
                if (scene != null) AssemblySceneVideoBuilder.LayerOverlayRange(t, scene, layer, out start, out end);
                if (layer.PlaybackMode == "once") end = Math.Min(end, start + layer.PhotoDuration);
                var plan = new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = path } };
                using (var raster = AssemblyRaster.Create(t, plan)) {
                    var image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(image)) g.DrawImage(raster, new Rectangle(0, 0, w, h));
                    var item = new LayerImage { Plan = plan, Image = image, Start = start, End = end }; owned.Add(item); result.Add(item);
                }
                if (layer.AppearanceTransition != "cut" || layer.ExitTransition != "cut")
                    notes.Add("Появление/исчезновение «" + layer.Name + "»: fade приближён по времени; прочие эффекты в интервале входа пропущены без баллов.");
            }
            return result;
        }
        static void DrawLayers(Graphics g, IEnumerable<LayerImage> layers, double time) {
            foreach (var item in layers) {
                if (time < item.Start || time >= item.End) continue;
                var layer = item.Plan.Layer; double opacity = 1;
                double entrance = Math.Min(layer.AppearanceDuration, item.End - item.Start);
                if (layer.AppearanceTransition != "cut" && entrance > 0 && time < item.Start + entrance) {
                    if (layer.AppearanceTransition != "fade") continue;
                    opacity *= (time - item.Start) / entrance;
                }
                double exit = Math.Min(layer.ExitDuration, item.End - item.Start);
                if (layer.ExitTransition == "fade" && exit > 0) opacity *= Math.Min(1, (item.End - time) / exit);
                using (var attrs = new ImageAttributes()) {
                    var matrix = new ColorMatrix { Matrix33 = (float)opacity }; attrs.SetColorMatrix(matrix);
                    g.DrawImage(item.Image, new Rectangle(0, 0, item.Image.Width, item.Image.Height), 0, 0, item.Image.Width, item.Image.Height, GraphicsUnit.Pixel, attrs);
                }
            }
        }
        static FrameSource Source(AssemblyTemplate t, AssemblyScenePlan scene, double time, double position) {
            if (t.ContinuousVideo) return SourceTime(scene.Video, position + time, t.LoopShortVideos);
            if (scene.VideoSegments.Count > 0) {
                foreach (var segment in scene.VideoSegments) {
                    if (time < segment.Duration) return SourceTime(segment.Video, time, false);
                    time -= segment.Duration;
                }
                var last = scene.VideoSegments.Last(); return SourceTime(last.Video, last.Duration, false);
            }
            return SourceTime(scene.Video, AssemblyTimeline.SourceStart(t, scene) + time, AssemblyTimeline.LoopInput(t, scene));
        }
        static FrameSource SourceTime(AssemblyVideo video, double time, bool loop) {
            return new FrameSource { Video = video, Time = loop ? time % video.Duration : Math.Max(0, Math.Min(time, video.Duration - .05)) };
        }
        static async Task<Bitmap> Extract(string ffmpeg, string path, double time, AssemblyTemplate t, int w, int h, bool placement, bool alpha,
            string temp, CancellationToken ct) {
            string file = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".png");
            try {
                string filter = AssemblyRender.Fill(t.Width, t.Height, t.Fps);
                if (alpha && !placement) filter = filter.Replace("format=yuv420p", "format=rgba");
                if (placement) filter += AssemblyVideoPlacement.Filter(t, alpha);
                filter += ",scale=" + w + ":" + h + ":flags=area,format=rgba";
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-threads", "1", "-ss", Core.N(time), "-i", path,
                    "-an", "-vf", filter, "-frames:v", "1", "-threads", "1", file }, ct, null, 30).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                using (var image = Image.FromFile(file)) return new Bitmap(image);
            } finally { if (File.Exists(file)) File.Delete(file); }
        }
        public static double ChangedPixelPercent(Bitmap original, Bitmap future) {
            if (original.Size != future.Size) throw new ArgumentException("Размеры кадров различаются.");
            var rect = new Rectangle(Point.Empty, original.Size);
            var a = original.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try {
                var b = future.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try {
                    int changed = 0; var ar = new byte[original.Width * 4]; var br = new byte[ar.Length];
                    for (int y = 0; y < original.Height; y++) {
                        Marshal.Copy(IntPtr.Add(a.Scan0, a.Stride * y), ar, 0, ar.Length); Marshal.Copy(IntPtr.Add(b.Scan0, b.Stride * y), br, 0, br.Length);
                        for (int x = 0; x < ar.Length; x += 4) {
                            double db = ar[x] - br[x], dg = ar[x + 1] - br[x + 1], dr = ar[x + 2] - br[x + 2];
                            if (.2126 * dr * dr + .7152 * dg * dg + .0722 * db * db >= 24 * 24) changed++;
                        }
                    }
                    return 100.0 * changed / (original.Width * original.Height);
                } finally { future.UnlockBits(b); }
            } finally { original.UnlockBits(a); }
        }
    }
}
