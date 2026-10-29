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

        public static string SelectVoiceover(AssemblyTemplate t, Random random, AssemblyMixTracker mix) {
            string manual = t.ResolveAudio(t.Voiceover);
            if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) return manual;
            return AvailableLines(t).Select(l => t.Resolve(l.AudioPath)).OrderBy(f => mix.Usage("voice", AssemblyFiles.Hash(f)))
                .ThenBy(f => random.Next()).FirstOrDefault() ?? "";
        }
        public static HashSet<string> SpeechHashes(AssemblyTemplate t) {
            var hashes = new HashSet<string>(AvailableLines(t).Select(l => AssemblyFiles.Hash(t.Resolve(l.AudioPath))));
            string manual = t.ResolveAudio(t.Voiceover);
            if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) hashes.Add(AssemblyFiles.Hash(manual));
            return hashes;
        }
        public static async Task Prepare(AssemblyTemplate t, AssemblyPlan p, string ffmpeg, string probe, string work, CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(p.Voiceover)) p.Voiceover = await ResolveVoiceoverAsync(t, ffmpeg, probe, work, ct).ConfigureAwait(false);
            p.RenderDuration = 0;
            double voiceSeconds = 0;
            if (!string.IsNullOrWhiteSpace(p.Voiceover) && File.Exists(p.Voiceover)) {
                voiceSeconds = Core.AudioDuration(await Core.Probe(probe, p.Voiceover, ct).ConfigureAwait(false));
                if (voiceSeconds > 0 && voiceSeconds + .001 < t.Duration) p.RenderDuration = voiceSeconds;
            }
            if (t.SubtitleStyle?.Enabled == true && !string.IsNullOrWhiteSpace(p.Voiceover)) {
                var line = AvailableLines(t).FirstOrDefault(l => string.Equals(t.Resolve(l.AudioPath), p.Voiceover, StringComparison.OrdinalIgnoreCase));
                p.Captions = CaptionsForAudio(t, p.Voiceover, line?.Text, voiceSeconds);
            }
            p.Captions = t.SubtitleStyle?.Enabled == true ? WarmupSubtitles.FitToDuration(p.Captions, p.RollDuration(t)) : new List<WarmupCaption>();
        }
        public static Task<string> ResolveVoiceoverAsync(AssemblyTemplate t, string ffmpeg, string probe, string work, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            string manual = t.ResolveAudio(t.Voiceover);
            return Task.FromResult(!string.IsNullOrWhiteSpace(manual) && File.Exists(manual) ? manual : AvailableLines(t).Select(l => t.Resolve(l.AudioPath)).FirstOrDefault() ?? "");
        }
        // Alternatives are not a sequence. The longest file is the editor's duration reference.
        public static async Task<double?> ResolveVoiceoverDurationAsync(AssemblyTemplate t, string probe, CancellationToken ct) {
            string manual = t.ResolveAudio(t.Voiceover);
            var files = !string.IsNullOrWhiteSpace(manual) && File.Exists(manual) ? new List<string> { manual } : AvailableLines(t).Select(l => t.Resolve(l.AudioPath)).ToList();
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
