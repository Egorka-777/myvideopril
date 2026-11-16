using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public class AssemblyAudioLevel {
        public string Path = "", Error = "";
        public double Loudness, GainDb;
        public bool HasAudio, IsSilent;
        public long FileLength, LastWriteUtcTicks;
    }

    public static class AssemblyAudioLeveling {
        public static List<string> Sources(AssemblyTemplate t) {
            var sources = new List<string>();
            if (t.ContinuousVideo || t.Scenes.Count == 0) sources.Add(t.Videos);
            else foreach (var scene in t.Scenes) {
                sources.Add(t.VideoSource(scene));
                sources.Add(scene.ContinuationVideos);
                sources.AddRange((scene.VideoPacks ?? new List<AssemblySceneVideoPack>()).Select(p => p.Source));
            }
            // A shared split pool can supply either half; leveling is still applied only to the primary role.
            if (t.SplitLayout != "none" && t.SplitSharedPool) sources.Add(t.SecondaryVideos);
            return sources.Where(s => !string.IsNullOrWhiteSpace(s))
                .SelectMany(s => AssemblyFiles.Pool(t.Resolve(s), AssemblyFiles.VideoExtensions))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static async Task<List<AssemblyAudioLevel>> Measure(AssemblyTemplate t, string ffmpeg, string ffprobe, CancellationToken ct, Action<string> progress) {
            var paths = Sources(t);
            var result = new List<AssemblyAudioLevel>();
            for (int i = 0; i < paths.Count; i++) {
                ct.ThrowIfCancellationRequested();
                string path = paths[i];
                progress?.Invoke("Проверка громкости " + (i + 1) + "/" + paths.Count + ": " + System.IO.Path.GetFileName(path));
                var level = new AssemblyAudioLevel { Path = path };
                try {
                    var file = new FileInfo(path);
                    level.FileLength = file.Length; level.LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;
                    var probe = await Core.Probe(ffprobe, path, ct).ConfigureAwait(false);
                    level.HasAudio = (probe.streams ?? new ProbeStream[0]).Any(s => s.codec_type == "audio");
                    if (level.HasAudio) {
                        string metadata = await Core.Tool(ffmpeg, new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", path,
                            "-t", "20", "-map", "0:a:0", "-vn", "-af", "ebur128=metadata=1,ametadata=print:key=lavfi.r128.I:file=-", "-f", "null", "-" }, ct).ConfigureAwait(false);
                        var matches = Regex.Matches(metadata, @"lavfi\.r128\.I=(-?\d+(?:\.\d+)?)");
                        if (matches.Count == 0) throw new Exception("Недостаточно звука для измерения громкости.");
                        level.Loudness = double.Parse(matches[matches.Count - 1].Groups[1].Value, Core.Inv);
                        // EBU R128's absolute gate is -70 LUFS: silence/below-gate material cannot be safely boosted.
                        level.IsSilent = level.Loudness <= -69.9;
                    }
                } catch (OperationCanceledException) { throw; }
                catch (Exception ex) { level.Error = ex.Message; }
                result.Add(level);
            }
            return result;
        }

        public static void Align(AssemblyTemplate t, List<AssemblyAudioLevel> levels) {
            if (levels == null || levels.Count == 0) throw new Exception("Сначала проверьте громкость видео.");
            if (levels.Any(l => !string.IsNullOrEmpty(l.Error))) throw new Exception("Не все видео измерены: " + string.Join("; ", levels.Where(l => !string.IsNullOrEmpty(l.Error)).Select(l => System.IO.Path.GetFileName(l.Path) + ": " + l.Error)));
            var measured = levels.Where(Usable).ToList();
            if (measured.Count == 0) throw new Exception("В видео нет измеримого звука (тишина или нет звуковой дорожки).");
            double loudest = measured.Max(l => l.Loudness);
            var copy = Copy(levels);
            foreach (var level in copy) level.GainDb = Usable(level) ? loudest - level.Loudness : 0;
            t.NativeAudioLevels = copy;
            t.NativeAudioLeveled = true;
        }

        static bool Usable(AssemblyAudioLevel level) => level.HasAudio && !level.IsSilent && string.IsNullOrEmpty(level.Error)
            && !double.IsNaN(level.Loudness) && !double.IsInfinity(level.Loudness) && level.Loudness > -69.9;

        public static double GainFor(AssemblyTemplate t, string path) {
            if (!t.NativeAudioLeveled) return 0;
            var levels = t.NativeAudioLevels ?? new List<AssemblyAudioLevel>();
            var level = levels.FirstOrDefault(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
            // Existing HDR proxies retain a small source marker; never hash or scan source videos.
            if (level == null && File.Exists(path + ".source.txt")) {
                var marker = File.ReadAllLines(path + ".source.txt");
                if (marker.Length >= 2) level = levels.FirstOrDefault(l => string.Equals(l.Path, marker[0], StringComparison.OrdinalIgnoreCase)
                    && l.LastWriteUtcTicks.ToString(Core.Inv) == marker[1]);
            }
            if (level == null) throw new Exception("Видео не проверено по громкости: " + System.IO.Path.GetFileName(path) + ". Проверьте громкость заново.");
            var file = new FileInfo(level.Path);
            if (!file.Exists || file.Length != level.FileLength || file.LastWriteTimeUtc.Ticks != level.LastWriteUtcTicks)
                throw new Exception("Видео изменилось после проверки громкости: " + System.IO.Path.GetFileName(path) + ". Проверьте громкость заново.");
            return Usable(level) ? level.GainDb : 0;
        }

        public static List<AssemblyAudioLevel> Copy(List<AssemblyAudioLevel> levels) {
            return (levels ?? new List<AssemblyAudioLevel>()).Select(l => new AssemblyAudioLevel {
                Path = l.Path, Loudness = l.Loudness, GainDb = l.GainDb, Error = l.Error, HasAudio = l.HasAudio,
                IsSilent = l.IsSilent, FileLength = l.FileLength, LastWriteUtcTicks = l.LastWriteUtcTicks
            }).ToList();
        }
    }
}
