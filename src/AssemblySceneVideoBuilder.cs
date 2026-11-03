using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoBatch {
    public sealed class AssemblySceneVideoSegment {
        public AssemblyVideo Video;
        public double Duration;
        public bool IsContinuation;
    }

    public static class AssemblySceneVideoBuilder {
        public static List<AssemblySceneVideoPack> Packs(AssemblyTemplate t, AssemblyScene scene) {
            Migrate(scene);
            return new[] { new AssemblySceneVideoPack { Source = t.VideoSource(scene), Mix = scene.MixVideos } }.Concat(scene.VideoPacks).ToList();
        }
        public static void Migrate(AssemblyScene scene) {
            if (scene.VideoPacks == null) scene.VideoPacks = new List<AssemblySceneVideoPack>();
            if (!string.IsNullOrWhiteSpace(scene.ContinuationVideos)) {
                scene.VideoPacks.Insert(0, new AssemblySceneVideoPack { Source = scene.ContinuationVideos });
                scene.ContinuationVideos = "";
            }
        }
        /// <summary>Only the first pack may start a scene; mix flags apply to fill clips after the first one.</summary>
        public static string PrimarySource(AssemblyTemplate t, AssemblyScene scene) => t.VideoSource(scene);

        static List<AssemblyVideo> Pool(AssemblyTemplate t, string folder, IList<AssemblyVideo> catalog) {
            if (string.IsNullOrWhiteSpace(folder)) return new List<AssemblyVideo>();
            var paths = AssemblyFiles.Pool(t.Resolve(folder), AssemblyFiles.VideoExtensions);
            var list = new List<AssemblyVideo>();
            foreach (string path in paths) {
                var hit = catalog.FirstOrDefault(v => string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase));
                if (hit != null && hit.Duration > 0) list.Add(hit);
            }
            return list.GroupBy(v => v.Hash).Select(g => g.First()).ToList();
        }

        static AssemblyVideo Pick(List<AssemblyVideo> pool, string axis, Random random, AssemblyMixTracker mix) {
            if (pool == null || pool.Count == 0) return null;
            mix = mix ?? new AssemblyMixTracker();
            return pool.OrderBy(v => mix.Usage(axis, v.Hash)).ThenBy(v => random.Next()).First();
        }

        public static void EnsureSegments(AssemblyTemplate t, AssemblyPlan p, IList<AssemblyVideo> catalog, Random random, AssemblyMixTracker mix = null) {
            if (t == null || p == null || catalog == null) return;
            mix = mix ?? new AssemblyMixTracker();
            for (int i = 0; i < p.Scenes.Count; i++) {
                var scene = p.Scenes[i];
                scene.VideoSegments = new List<AssemblySceneVideoSegment>();
                scene.PrimaryVideoEnd = 0;
                double need = AssemblyTimeline.ContentDuration(t, scene);
                if (need <= 0) need = scene.Scene.Duration;
                if (need <= 0 || scene.Video == null) continue;

                bool chain = !t.ContinuousVideo && t.SceneTimingVersion > 0 && scene.Scene.VideoMode == "own";
                if (!chain && scene.Video.Duration + .001 >= need) {
                    scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = scene.Video, Duration = need, IsContinuation = false });
                    scene.PrimaryVideoEnd = need;
                    continue;
                }
                if (!chain) continue;

                var packs = Packs(t, scene.Scene);
                var primary = Pool(t, packs[0].Source, catalog);
                if (primary.Count == 0 || !primary.Any(v => v.Hash == scene.Video.Hash))
                    throw new Exception(scene.Scene.Name + ": первый клип не принадлежит «Видео 1».");
                var fillGroups = new List<KeyValuePair<List<AssemblyVideo>, bool>>();
                for (int pi = 1; pi < packs.Count;) {
                    var group = new List<AssemblyVideo>();
                    bool mixing = packs[pi].Mix;
                    int start = pi;
                    do {
                        group.AddRange(Pool(t, packs[pi].Source, catalog)); pi++;
                    } while (mixing && pi < packs.Count && packs[pi].Mix);
                    if (group.Count > 0) fillGroups.Add(new KeyValuePair<List<AssemblyVideo>, bool>(group.GroupBy(v => v.Hash).Select(g => g.First()).ToList(), pi - start > 1));
                }
                if (fillGroups.Count == 0) fillGroups.Add(new KeyValuePair<List<AssemblyVideo>, bool>(primary, true));
                double remaining = need;
                double firstTake = Math.Min(scene.Video.Duration, remaining);
                scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = scene.Video, Duration = firstTake });
                scene.PrimaryVideoEnd = firstTake;
                remaining -= firstTake;
                int fillIndex = 0;
                while (remaining > .001) {
                    var pool = fillGroups[fillIndex].Key;
                    var next = Pick(pool, "videoCont:" + i, random, mix);
                    if (next == null) break;
                    double take = Math.Min(next.Duration, remaining);
                    scene.VideoSegments.Add(new AssemblySceneVideoSegment { Video = next, Duration = take, IsContinuation = true });
                    mix?.Touch("videoCont:" + i, next.Hash);
                    remaining -= take;
                    if (!fillGroups[fillIndex].Value && fillIndex < fillGroups.Count - 1) fillIndex++;
                }
                if (remaining > .05)
                    throw new Exception(scene.Scene.Name + ": не хватает видео, чтобы заполнить " + need.ToString("0.#") + " с. Добавьте ролики в следующие пачки.");
            }
        }

        public static string LayerOverlayEnable(AssemblyTemplate t, AssemblyScenePlan scene, AssemblyLayer layer) {
            double content = AssemblyTimeline.ContentDuration(t, scene);
            double start = Math.Max(0, layer.Start);
            double end = layer.End == 0 || Math.Abs(layer.End - scene.Scene.Duration) < .001 ? content : Math.Min(layer.End, content);
            string part = (layer.ImagePart ?? "").Trim().ToLowerInvariant();
            if (part == "" || part == "all") return "between(t," + Core.N(start) + "," + Core.N(end) + ")";
            double split = scene.PrimaryVideoEnd > 0 ? scene.PrimaryVideoEnd : content;
            if (part == "primary") {
                end = Math.Min(end, split);
                if (end <= start) end = split;
                return "between(t," + Core.N(start) + "," + Core.N(end) + ")";
            }
            if (part == "continuation") {
                start = Math.Max(start, split);
                if (end <= start) end = content;
                return "between(t," + Core.N(start) + "," + Core.N(end) + ")";
            }
            return "between(t," + Core.N(start) + "," + Core.N(end) + ")";
        }
    }
}
