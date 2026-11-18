using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class AssemblySpeechPlan {
        public static bool SubtitlesEnabled(AssemblyTemplate t) => t?.SubtitleStyle != null && t.SubtitleStyle.Enabled;
        public static string TranscriptText(AssemblyPlan plan, double mainStart) {
            var lines = (plan.Captions ?? new List<WarmupCaption>()).Where(c => c.End > mainStart && !string.IsNullOrWhiteSpace(c.Text))
                .OrderBy(c => c.Start).Select(c => c.Start < mainStart && c.Words != null && c.Words.Count > 0
                    ? string.Join(" ", c.Words.Where(w => w.Start >= mainStart).Select(w => w.Text)) : c.Text);
            return string.Join(" ", lines.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
        }
        public static void SaveTranscript(string video, AssemblyPlan plan, double mainStart) {
            string text = TranscriptText(plan, mainStart);
            if (string.IsNullOrWhiteSpace(text)) throw new Exception("Нет сохранённой транскрибации для ролика: " + Path.GetFileName(video));
            File.WriteAllText(Path.ChangeExtension(video, ".txt"), text + Environment.NewLine, new System.Text.UTF8Encoding(true));
        }
        public static async Task EnsureTranscript(AssemblyTemplate t, AssemblyPlan plan, string video, string ffmpeg, string work, CancellationToken ct, Action<string> progress) {
            if (!SubtitlesEnabled(t)) return;
            double start = AssemblyTimeline.MainStart(t, plan);
            if (!string.IsNullOrWhiteSpace(TranscriptText(plan, start))) return;
            progress?.Invoke("Готовлю текст основного видео без вступления…");
            string speech = Path.Combine(work, "transcript-main.wav");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-ss", Core.N(start), "-i", video, "-vn", "-ac", "1", "-ar", "16000", speech }, ct, null, 600).ConfigureAwait(false);
            var cues = await EnsureCaptionsForAudioAsync(t, speech, null, Math.Max(0, plan.RollDuration(t) - start), ct, progress).ConfigureAwait(false);
            foreach (var cue in cues) {
                cue.Start += start; cue.End += start;
                if (cue.Words != null) foreach (var word in cue.Words) { word.Start += start; word.End += start; }
            }
            plan.Captions = cues;
            if (string.IsNullOrWhiteSpace(TranscriptText(plan, start))) throw new Exception("Речь в основном видео не распознана; текстовый файл не создан.");
        }
        public sealed class SpeechTiming {
            public string AudioHash, Text, Source;
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
        public static int SceneOwnAudioCount(AssemblyTemplate t, AssemblyScene scene) {
            if (scene == null || string.IsNullOrWhiteSpace(scene.Voiceover)) return 0;
            return AssemblyFiles.Pool(t.Resolve(scene.Voiceover), AssemblyFiles.MusicExtensions).Count;
        }
        static string DiscoverSceneVoiceFolder(AssemblyTemplate t, AssemblyScene scene) {
            if (scene == null || string.IsNullOrWhiteSpace(scene.Folder)) return "";
            string sceneDir = t.Resolve(scene.Folder);
            if (!Directory.Exists(sceneDir)) return "";
            string best = "";
            int bestCount = 0;
            DateTime bestTime = DateTime.MinValue;
            foreach (string dir in Directory.GetDirectories(sceneDir)) {
                string name = Path.GetFileName(dir);
                if (name.Length == 0 || !name.StartsWith("Озвучка", StringComparison.OrdinalIgnoreCase)) continue;
                int count = AssemblyFiles.Pool(dir, AssemblyFiles.MusicExtensions).Count;
                if (count == 0) continue;
                DateTime time = Directory.GetLastWriteTimeUtc(dir);
                if (count > bestCount || count == bestCount && time > bestTime) {
                    best = Path.Combine(scene.Folder, name);
                    bestCount = count;
                    bestTime = time;
                }
            }
            return best;
        }
        /// <summary>Re-link scene voice pools saved on disk but missing from the template XML.</summary>
        public static void BindMissingSceneVoicePaths(AssemblyTemplate t) {
            if (t?.Scenes == null) return;
            foreach (var scene in t.Scenes) {
                if (SceneOwnAudioCount(t, scene) > 0) continue;
                string folder = DiscoverSceneVoiceFolder(t, scene);
                if (!string.IsNullOrWhiteSpace(folder)) scene.Voiceover = folder;
            }
        }
        /// <summary>If a scene has its own audio folder but VoiceMode was never set, enable per-scene voice for the project.</summary>
        public static void NormalizeSceneVoiceModes(AssemblyTemplate t) {
            if (t?.Scenes == null || t.Scenes.Count == 0) return;
            BindMissingSceneVoicePaths(t);
            bool configured = t.Scenes.Any(s => s.VoiceMode == "own" || s.VoiceMode == "shared" || s.VoiceMode == "none");
            if (configured) {
                foreach (var scene in t.Scenes)
                    if (scene.VoiceMode == "") scene.VoiceMode = "none";
                return;
            }
            if (!t.Scenes.Any(s => SceneOwnAudioCount(t, s) > 0)) return;
            foreach (var scene in t.Scenes)
                scene.VoiceMode = SceneOwnAudioCount(t, scene) > 0 ? "own" : "none";
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
        public static async Task Prepare(AssemblyTemplate t, AssemblyPlan p, string ffmpeg, string probe, string work, CancellationToken ct, Action<string> progress = null) {
            p.RenderDuration = 0;
            if (t.HasSceneVoiceovers) {
                // Each scene gets the duration of the selected voice file, not
                // the longest clip in its pool. Silent scenes retain their manual duration.
                p.Voiceover = "";
                var captions = (t.Captions ?? new List<AssemblyCaption>()).Select(c => new WarmupCaption { Text=c.Text, Start=c.Start, End=c.End }).ToList();
                double start = 0;
                for (int i = 0; i < p.Scenes.Count; i++) {
                    ct.ThrowIfCancellationRequested();
                    var scene = p.Scenes[i];
                    scene.VoiceSeconds = 0;
                    scene.ContentSeconds = 0;
                    if (scene.Scene.IsPreview) scene.Voiceover = "";
                    if (!string.IsNullOrWhiteSpace(scene.Voiceover)) {
                        if (!File.Exists(scene.Voiceover)) throw new Exception("Не найдена озвучка сцены: " + scene.Voiceover);
                        scene.VoiceSeconds = Core.AudioDuration(await Core.Probe(probe, scene.Voiceover, ct).ConfigureAwait(false));
                        if (scene.VoiceSeconds <= 0) throw new Exception("Не удалось определить длительность озвучки: " + scene.Voiceover);
                        scene.ContentSeconds = Math.Ceiling(scene.VoiceSeconds * t.Fps - 0.00001) / t.Fps;
                        if (t.SubtitleStyle?.Enabled == true) {
                            var line = AvailableLines(t).FirstOrDefault(l => string.Equals(t.Resolve(l.AudioPath), scene.Voiceover, StringComparison.OrdinalIgnoreCase));
                            foreach (var cue in await EnsureCaptionsForAudioAsync(t, scene.Voiceover, line?.Text, scene.VoiceSeconds, ct, progress).ConfigureAwait(false)) {
                                cue.Start += start;
                                cue.End += start;
                                foreach (var word in cue.Words) { word.Start += start; word.End += start; }
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
                // Prepare may be called more than once for a preview and its final render.
                p.Captions = (t.Captions ?? new List<AssemblyCaption>()).Select(c => new WarmupCaption { Text=c.Text, Start=c.Start, End=c.End }).ToList();
                foreach (var scene in p.Scenes) { scene.ContentSeconds = 0; scene.VoiceSeconds = 0; }
                if (string.IsNullOrWhiteSpace(p.Voiceover)) p.Voiceover = await ResolveVoiceoverAsync(t, ffmpeg, probe, work, ct).ConfigureAwait(false);
                double voiceSeconds = 0;
                if (!string.IsNullOrWhiteSpace(p.Voiceover) && File.Exists(p.Voiceover)) {
                    voiceSeconds = Core.AudioDuration(await Core.Probe(probe, p.Voiceover, ct).ConfigureAwait(false));
                    if (voiceSeconds <= 0) throw new Exception("Не удалось определить длительность озвучки: " + p.Voiceover);
                    p.RenderDuration = Math.Ceiling(voiceSeconds * t.Fps - 0.00001) / t.Fps;
                    if (p.RenderDuration > t.Duration && p.Scenes.Count > 0) {
                        var last = p.Scenes[p.Scenes.Count - 1];
                        last.ContentSeconds = last.Scene.Duration + (p.RenderDuration - t.Duration);
                        last.RenderDuration = AssemblyTimeline.SceneSeconds(last) + (t.SceneTimingVersion > 0 ? last.IncomingPad + last.OutgoingPad : 0);
                    }
                }
                if (t.SubtitleStyle?.Enabled == true && !string.IsNullOrWhiteSpace(p.Voiceover)) {
                    var line = AvailableLines(t).FirstOrDefault(l => string.Equals(t.Resolve(l.AudioPath), p.Voiceover, StringComparison.OrdinalIgnoreCase));
                    p.Captions = await EnsureCaptionsForAudioAsync(t, p.Voiceover, line?.Text, voiceSeconds, ct, progress).ConfigureAwait(false);
                }
            }
            // Source-length mode wins over voice timing: audio is trimmed/padded by the renderer.
            AssemblyTimeline.ApplyVideoDuration(t, p);
            double mainStart = AssemblyTimeline.MainStart(t, p);
            if (mainStart > 0 && !t.HasSceneVoiceovers) foreach (var cue in p.Captions) {
                cue.Start += mainStart; cue.End += mainStart;
                if (cue.Words != null) foreach (var word in cue.Words) { word.Start += mainStart; word.End += mainStart; }
            }
            p.Captions = t.SubtitleStyle?.Enabled == true
                ? WarmupSubtitles.FitToDuration(p.Captions, p.RollDuration(t))
                : new List<WarmupCaption>();
        }
        // Run only after the concrete video segments and final scene durations are known.
        public static async Task PrepareVideoCaptions(AssemblyTemplate t, AssemblyPlan p, string ffmpeg, string probe, string work, CancellationToken ct, Action<string> progress = null) {
            if (t.SubtitleStyle?.Enabled != true || !string.IsNullOrWhiteSpace(p.Voiceover)) return;
            var args = new List<string> { "-v", "error", "-nostdin", "-y" };
            var graph = new List<string>(); var audio = new List<string>(); int input = 0;
            double roll = p.RollDuration(t), position = 0;
            bool continuous = t.ContinuousVideo && p.Scenes.All(s => string.IsNullOrWhiteSpace(s.Voiceover));
            double NativeVolume(AssemblyScene scene) => t.SplitLayout != "none" ? (t.SplitPrimaryAudio(p) ? t.SplitAudioVolume : 0) : t.SceneNativeAudioVolume(scene);
            if (continuous) {
                if (NativeVolume(p.Scenes[0].Scene) > 0)
                    AssemblyRender.AppendNativeAudio(p.Scenes[0].Video, 0, roll, 0, t.LoopShortVideos && p.Scenes[0].Video.Duration < roll,
                        NativeVolume(p.Scenes[0].Scene), roll, args, ref input, graph, audio);
                if (t.SplitLayout != "none" && !t.SplitPrimaryAudio(p) && t.SplitAudioVolume > 0 && p.SecondaryVideo != null)
                    AssemblyRender.AppendNativeAudio(p.SecondaryVideo, 0, roll, 0, t.LoopShortVideos && p.SecondaryVideo.Duration < roll,
                        t.SplitAudioVolume, roll, args, ref input, graph, audio);
            }
            foreach (var scene in p.Scenes) {
                double content = AssemblyTimeline.ContentDuration(t, scene);
                double audioStart = position + (t.HasPreviewScene ? 0 : scene.IncomingPad);
                if (!scene.Scene.IsPreview && string.IsNullOrWhiteSpace(scene.Voiceover) && !continuous) {
                    double nativeVolume = NativeVolume(scene.Scene);
                    if (nativeVolume > 0) {
                        if (t.ContinuousVideo) {
                            AssemblyRender.AppendNativeAudio(scene.Video, position + scene.IncomingPad, content, audioStart,
                                t.LoopShortVideos && scene.Video.Duration < roll, nativeVolume, roll, args, ref input, graph, audio);
                        } else if (scene.VideoSegments != null && scene.VideoSegments.Count > 1) {
                            double offset = 0;
                            foreach (var segment in scene.VideoSegments) {
                                AssemblyRender.AppendNativeAudio(segment.Video, 0, segment.Duration, audioStart + offset,
                                    false, nativeVolume, roll, args, ref input, graph, audio);
                                offset += segment.Duration;
                            }
                        } else AssemblyRender.AppendNativeAudio(scene.Video, AssemblyTimeline.SourceStart(t, scene), content, audioStart,
                            AssemblyTimeline.LoopInput(t, scene), nativeVolume, roll, args, ref input, graph, audio);
                    }
                    if (t.SplitLayout != "none" && !t.SplitPrimaryAudio(p) && t.SplitAudioVolume > 0 && p.SecondaryVideo != null)
                        AssemblyRender.AppendNativeAudio(p.SecondaryVideo, position + scene.IncomingPad, content, audioStart,
                            t.LoopShortVideos && p.SecondaryVideo.Duration < roll, t.SplitAudioVolume, roll, args, ref input, graph, audio);
                }
                position += AssemblyTimeline.SceneSeconds(scene) + (t.SceneTimingVersion > 0 && t.Transition != "cut" ? t.TransitionSeconds : 0);
            }
            if (audio.Count == 0) {
                if (p.Captions.Count == 0) throw new Exception("Субтитры включены, но нет озвучки или включённого звука видео для распознавания.");
                return;
            }
            progress?.Invoke("Распознаю речь выбранных видео для этого ролика…");
            string speech = Path.Combine(work, "video-speech.wav");
            graph.Add(string.Join("", audio.Select(a => "[" + a + "]")) + "amix=inputs=" + audio.Count + ":duration=longest:normalize=0,atrim=duration=" + Core.N(roll) + "[speech]");
            args.AddRange(new[] { "-filter_complex_threads", "1", "-filter_complex", string.Join(";", graph), "-map", "[speech]", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", speech });
            await Core.Tool(ffmpeg, args, ct, null, 600).ConfigureAwait(false);
            var cues = await EnsureCaptionsForAudioAsync(t, speech, null, roll, ct, progress).ConfigureAwait(false);
            double mainStart = AssemblyTimeline.MainStart(t, p);
            p.Captions.AddRange(cues.Where(c => c.End > mainStart));
            if (mainStart > 0) foreach (var cue in p.Captions) {
                cue.Start = Math.Max(mainStart, cue.Start);
                if (cue.Words != null) {
                    cue.Words.RemoveAll(w => w.End <= mainStart);
                    foreach (var word in cue.Words) word.Start = Math.Max(mainStart, word.Start);
                }
            }
            p.Captions = WarmupSubtitles.FitToDuration(p.Captions.OrderBy(c => c.Start).ToList(), roll);
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
            var cues = CachedCaptions(t, audio, text, duration);
            if (cues != null) return cues;
            throw new Exception("У озвучки «" + Path.GetFileName(audio) + "» ещё нет субтитров. Нажмите «Субтитры → Распознать аудио». При сборке речь также распознаётся автоматически; повторная генерация в ElevenLabs не нужна.");
        }
        internal static List<WarmupCaption> CachedCaptions(AssemblyTemplate t, string audio, string text, double duration) {
            if (string.IsNullOrWhiteSpace(audio) || !File.Exists(audio)) return null;
            string hash = AssemblyFiles.Hash(audio);
            foreach (string file in new[] { audio + ".timing.json", LocalAudioTranscription.CacheFile(hash) }) {
                if (!File.Exists(file)) continue;
                try {
                    var timing = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<SpeechTiming>(File.ReadAllText(file));
                    if (timing?.AudioHash != hash || timing.Alignment == null) continue;
                    if (timing.Source != "faster-whisper/small" && text != null && timing.Text != text) continue;
                    double length = duration > 0 ? duration : (timing.Alignment.character_end_times_seconds ?? new double[0]).DefaultIfEmpty(0).Max();
                    return WarmupSubtitles.Cues(timing.Alignment, length, t.SubtitleStyle?.WordsPerCaption ?? 3);
                } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (Exception e) {
                    AppDiagnostics.Write("Субтитры", "", "speech", "cache", "Таймкоды не подходят к аудио: " + Path.GetFileName(audio) + " · " + e.Message);
                }
            }
            return null;
        }
        public static bool HasTiming(string audio) => CachedCaptions(new AssemblyTemplate(), audio, null, 0) != null;
        static async Task<List<WarmupCaption>> EnsureCaptionsForAudioAsync(AssemblyTemplate t, string audio, string text, double duration, CancellationToken ct, Action<string> progress) {
            var cues = CachedCaptions(t, audio, text, duration);
            if (cues != null) return cues;
            bool repair = HasTiming(audio);
            // Another render may have completed this same audio while we waited.
            if (repair) { cues = CachedCaptions(t, audio, text, duration); if (cues != null) return cues; }
            await LocalAudioTranscription.PrepareAsync(new[] { audio }, ct, progress, force: repair).ConfigureAwait(false);
            return CaptionsForAudio(t, audio, null, duration);
        }
        public static async Task<List<WarmupCaption>> BuildCaptionsFromSpeechAsync(AssemblyTemplate t, string probe, CancellationToken ct) {
            if (t.HasSceneVoiceovers) {
                var result = new List<WarmupCaption>(); double offset = 0;
                foreach (var scene in t.Scenes) {
                    ct.ThrowIfCancellationRequested();
                    string source = SceneVoiceOptions(t, scene).FirstOrDefault(); double seconds = scene.Duration;
                    if (source != null) {
                        double duration = Core.AudioDuration(await Core.Probe(probe, source, ct).ConfigureAwait(false));
                        seconds = Math.Ceiling(duration * t.Fps - .00001) / t.Fps;
                        foreach (var cue in CachedCaptions(t, source, null, duration) ?? new List<WarmupCaption>()) {
                            cue.Start += offset; cue.End += offset;
                            foreach (var word in cue.Words) { word.Start += offset; word.End += offset; }
                            result.Add(cue);
                        }
                    }
                    offset += seconds + (t.SceneTimingVersion > 0 && t.Transition != "cut" ? t.TransitionSeconds : 0);
                }
                return result;
            }
            string audio = await ResolveVoiceoverAsync(t, "", probe, "", ct).ConfigureAwait(false);
            var line = AvailableLines(t).FirstOrDefault(l => string.Equals(t.Resolve(l.AudioPath), audio, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(audio)) return new List<WarmupCaption>();
            double length = Core.AudioDuration(await Core.Probe(probe, audio, ct).ConfigureAwait(false));
            return CachedCaptions(t, audio, line?.Text, length) ?? new List<WarmupCaption>();
        }
    }
}
