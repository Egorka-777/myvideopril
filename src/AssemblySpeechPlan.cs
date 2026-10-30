using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class AssemblySpeechPlan {
        public sealed class SpeechTiming {
            public string AudioHash, Text;
            public WarmupAlignment Alignment;
        }
        public static void SaveTiming(string audio, string text, WarmupAlignment alignment) {
            File.WriteAllText(audio + ".timing.json", new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(
                new SpeechTiming { AudioHash = AssemblyFiles.Hash(audio), Text = text, Alignment = alignment }));
        }
        public static List<AssemblySpeechLine> AvailableLines(AssemblyTemplate t) => (t.SpeechLines ?? new List<AssemblySpeechLine>())
            .Where(l => !string.IsNullOrWhiteSpace(l.AudioPath) && File.Exists(t.Resolve(l.AudioPath))).ToList();

        // A folder means a pool of alternatives, not a playlist to concatenate.
        public static List<string> SharedVoiceOptions(AssemblyTemplate t) {
            var manual = AssemblyFiles.Pool(t.Resolve(t.Voiceover), AssemblyFiles.MusicExtensions);
            return manual.Count > 0 ? manual : AvailableLines(t).Select(l => t.Resolve(l.AudioPath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static List<string> SceneVoiceOptions(AssemblyTemplate t, AssemblyScene scene) {
            if (scene == null) return new List<string>();
            if (scene.VoiceMode == "own") return AssemblyFiles.Pool(t.Resolve(scene.Voiceover), AssemblyFiles.MusicExtensions);
            if (scene.VoiceMode == "shared") return SharedVoiceOptions(t);
            return new List<string>();
        }
        static string PickOption(IEnumerable<string> paths, string axis, Random random, AssemblyMixTracker mix) =>
            paths.OrderBy(f => mix.Usage(axis, AssemblyFiles.Hash(f))).ThenBy(f => random.Next()).FirstOrDefault() ?? "";

        public static string SelectVoiceover(AssemblyTemplate t, Random random, AssemblyMixTracker mix) =>
            PickOption(SharedVoiceOptions(t), "voice", random, mix);

        public static string SelectSceneVoiceover(AssemblyTemplate t, AssemblyScene scene, int index, Random random, AssemblyMixTracker mix) {
            if (scene.VoiceMode == "" || scene.VoiceMode == "none") return "";
            var options = SceneVoiceOptions(t, scene);
            if (options.Count == 0) throw new Exception(scene.Name + ": не найдены файлы озвучки. Добавь MP3/WAV/M4A или выбери «Без озвучки».");
            return PickOption(options, "sceneVoice:" + index, random, mix);
        }
        public static HashSet<string> SpeechHashes(AssemblyTemplate t) {
            var paths = SharedVoiceOptions(t).Concat(AvailableLines(t).Select(l => t.Resolve(l.AudioPath)))
                .Concat((t.Scenes ?? new List<AssemblyScene>()).SelectMany(s => SceneVoiceOptions(t, s)));
            return new HashSet<string>(paths.Select(AssemblyFiles.Hash));
        }
        public static async Task Prepare(AssemblyTemplate t, AssemblyPlan p, string ffmpeg, string probe, string work, CancellationToken ct) {
            p.RenderDuration = 0;
            if (t.HasSceneVoiceovers) {
                // Each scene gets the duration of the selected voice file + one second, not
                // the longest clip in its pool. Silent scenes retain their manual duration.
                p.Voiceover = "";
                var captions = new List<WarmupCaption>(p.Captions ?? new List<WarmupCaption>());
                double start = 0;
                for (int i = 0; i < p.Scenes.Count; i++) {
                    ct.ThrowIfCancellationRequested();
                    var scene = p.Scenes[i];
                    scene.VoiceSeconds = 0;
                    scene.ContentSeconds = 0;
                    if (!string.IsNullOrWhiteSpace(scene.Voiceover)) {
                        if (!File.Exists(scene.Voiceover)) throw new Exception("Не найдена озвучка сцены: " + scene.Voiceover);
                        scene.VoiceSeconds = Core.AudioDuration(await Core.Probe(probe, scene.Voiceover, ct).ConfigureAwait(false));
                        if (scene.VoiceSeconds <= 0) throw new Exception("Не удалось определить длительность озвучки: " + scene.Voiceover);
                        scene.ContentSeconds = Math.Ceiling((scene.VoiceSeconds + 1) * t.Fps - 0.00001) / t.Fps;
                        if (t.SubtitleStyle?.Enabled == true && File.Exists(scene.Voiceover + ".timing.json")) {
                            var line = AvailableLines(t).FirstOrDefault(l => string.Equals(t.Resolve(l.AudioPath), scene.Voiceover, StringComparison.OrdinalIgnoreCase));
                            foreach (var cue in CaptionsForAudio(t, scene.Voiceover, line?.Text, scene.VoiceSeconds)) {
                                cue.Start += start;
                                cue.End += start;
                                captions.Add(cue);
                            }
                        }
                    }
                    if (t.ContinuousVideo) scene.VideoStart = start;
                    double sceneSeconds = AssemblyTimeline.SceneSeconds(scene);
                    scene.RenderDuration = sceneSeconds + (t.SceneTimingVersion > 0
                        ? scene.IncomingPad + scene.OutgoingPad
                        : i < p.Scenes.Count - 1 && t.Transition != "cut" ? t.TransitionDuration : 0);
                    start += sceneSeconds + (t.SceneTimingVersion > 0 && t.Transition != "cut" ? t.TransitionSeconds : 0);
                }
                p.RenderDuration = start - (p.Scenes.Count > 0 && t.SceneTimingVersion > 0 && t.Transition != "cut" ? t.TransitionSeconds : 0);
                p.Captions = captions;
            } else {
                if (string.IsNullOrWhiteSpace(p.Voiceover)) p.Voiceover = await ResolveVoiceoverAsync(t, ffmpeg, probe, work, ct).ConfigureAwait(false);
                double voiceSeconds = 0;
                if (!string.IsNullOrWhiteSpace(p.Voiceover) && File.Exists(p.Voiceover)) {
                    voiceSeconds = Core.AudioDuration(await Core.Probe(probe, p.Voiceover, ct).ConfigureAwait(false));
                    if (voiceSeconds <= 0) throw new Exception("Не удалось определить длительность озвучки: " + p.Voiceover);
                    p.RenderDuration = Math.Ceiling((voiceSeconds + 1) * t.Fps - 0.00001) / t.Fps;
                    if (p.RenderDuration > t.Duration && p.Scenes.Count > 0) {
                        var last = p.Scenes[p.Scenes.Count - 1];
                        last.ContentSeconds = last.Scene.Duration + (p.RenderDuration - t.Duration);
                        last.RenderDuration += p.RenderDuration - t.Duration;
                    }
                }
                if (t.SubtitleStyle?.Enabled == true && !string.IsNullOrWhiteSpace(p.Voiceover)) {
                    var line = AvailableLines(t).FirstOrDefault(l => string.Equals(t.Resolve(l.AudioPath), p.Voiceover, StringComparison.OrdinalIgnoreCase));
                    p.Captions = CaptionsForAudio(t, p.Voiceover, line?.Text, voiceSeconds);
                }
            }
            p.Captions = t.SubtitleStyle?.Enabled == true
                ? WarmupSubtitles.FitToDuration(p.Captions, p.RollDuration(t))
                : new List<WarmupCaption>();
        }
        public static Task<string> ResolveVoiceoverAsync(AssemblyTemplate t, string ffmpeg, string probe, string work, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(SharedVoiceOptions(t).FirstOrDefault() ?? "");
        }
        // Alternatives are not a sequence; use the longest one only as an editor hint.
        public static async Task<double?> ResolveVoiceoverDurationAsync(AssemblyTemplate t, string probe, CancellationToken ct) {
            var files = SharedVoiceOptions(t);
            if (files.Count == 0) return null;
            double longest = 0;
            foreach (string path in files) longest = Math.Max(longest, Core.AudioDuration(await Core.Probe(probe, path, ct).ConfigureAwait(false)));
            return longest;
        }
        public static List<WarmupCaption> CaptionsForAudio(AssemblyTemplate t, string audio, string text, double duration) {
            string file = audio + ".timing.json";
            if (!File.Exists(file)) throw new Exception("У озвучки «" + Path.GetFileName(audio) + "» нет точных таймкодов. Создайте новую озвучку в ElevenLabs или отключите субтитры. Старые MP3 автоматически в API не отправляются.");
            var timing = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<SpeechTiming>(File.ReadAllText(file));
            if (timing == null || timing.AudioHash != AssemblyFiles.Hash(audio) || text != null && timing.Text != text)
                throw new Exception("Текст или аудиофайл изменились после генерации таймкодов. Создайте новую озвучку для синхронных субтитров.");
            return WarmupSubtitles.Cues(timing.Alignment, duration, t.SubtitleStyle?.WordsPerCaption ?? 3);
        }
        public static async Task<List<WarmupCaption>> BuildCaptionsFromSpeechAsync(AssemblyTemplate t, string probe, CancellationToken ct) {
            string audio = await ResolveVoiceoverAsync(t, "", probe, "", ct).ConfigureAwait(false);
            var line = AvailableLines(t).FirstOrDefault(l => string.Equals(t.Resolve(l.AudioPath), audio, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(audio) || !File.Exists(audio + ".timing.json")) return new List<WarmupCaption>();
            double length = Core.AudioDuration(await Core.Probe(probe, audio, ct).ConfigureAwait(false));
            return CaptionsForAudio(t, audio, line?.Text, length);
        }
    }
}
