using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VideoBatch {
    public static class AudioSubtitleTests {
        static void Check(bool ok, string message) { if (!ok) throw new Exception("Audio subtitles: " + message); }
        static WarmupAlignment Alignment(string first = "Привет") => new WarmupAlignment {
            characters = new[] { first + " ", "мир " }, character_start_times_seconds = new[] { .2, 1.1 }, character_end_times_seconds = new[] { .7, 1.7 }
        };
        static void Cache(string audio, string text = "Импортированная речь") {
            string hash = AssemblyFiles.Hash(audio), file = LocalAudioTranscription.CacheFile(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, new JavaScriptSerializer().Serialize(new AssemblySpeechPlan.SpeechTiming {
                AudioHash = hash, Text = text, Alignment = Alignment(), Source = "faster-whisper/small"
            }));
        }
        public static bool RunSelfTests() {
            string previous = Store.Root, folder = Path.Combine(Path.GetTempPath(), "vb-audio-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                Store.Root = folder; string source = Path.Combine(folder, "мой голос.mp3"); File.WriteAllText(source, "original audio bytes");
                var template = new AssemblyTemplate(); Check(!AssemblySpeechPlan.HasTiming(source), "new imported audio has no timing");
                Cache(source); string hash = AssemblyFiles.Hash(source);
                var cues = AssemblySpeechPlan.CaptionsForAudio(template, source, "unrelated manually entered text", 2);
                Check(cues.Count == 1 && cues[0].Words.Count == 2 && cues[0].Words[1].Start == 1.1, "recognition words, pauses and active-word timestamps");
                Check(!File.Exists(source + ".timing.json") && AssemblyFiles.Hash(source) == hash, "cache never changes imported audio or writes beside it");
                AssemblySpeechPlan.SaveTiming(source, "Provider text", Alignment("ElevenLabs"));
                Check(AssemblySpeechPlan.CaptionsForAudio(template, source, "Provider text", 2)[0].Text.StartsWith("ElevenLabs"), "valid ElevenLabs timing takes precedence");
                File.WriteAllText(source, "trimmed/replaced audio bytes"); Check(!AssemblySpeechPlan.HasTiming(source), "replaced/cut audio cannot reuse old source timing");
                Cache(source); string replacement = AssemblyFiles.Hash(source);
                File.WriteAllText(LocalAudioTranscription.CacheFile(replacement), "{broken"); Check(!AssemblySpeechPlan.HasTiming(source), "corrupt timing is a cache miss");
                var cancelled = new CancellationToken(true); bool stopped = false;
                try { LocalAudioTranscription.PrepareAsync(new[] { source }, cancelled).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "cancel before model/process launch");
                Console.WriteLine("PASS audio cache: source hashes, trimmed audio invalidation, ElevenLabs compatibility, imported word timestamps, corrupted cache and cancellation.");
                return true;
            } finally { Store.Root = previous; try { Directory.Delete(folder, true); } catch { } }
        }
        public static bool RunIntegrationTests(string ffmpeg, string probe, string fixtures, string root) => Integration(ffmpeg, probe, fixtures, root).GetAwaiter().GetResult();
        static async Task<bool> Integration(string ffmpeg, string probe, string fixtures, string root) {
            string previous = Store.Root; Store.Root = root;
            string folder = Path.Combine(root, "integration"); Directory.CreateDirectory(folder);
            var ct = CancellationToken.None;
            try {
                var files = new[] { "voice-en.mp3", "voice-en.m4a", "voice-ru.wav" }.Select(name => Path.Combine(fixtures, name)).ToArray();
                var hashes = files.ToDictionary(f => f, AssemblyFiles.Hash);
                var events = new List<string>(); Action<string> report = message => { events.Add(message); Console.WriteLine(message); };
                await LocalAudioTranscription.PrepareAsync(files, ct, report);
                foreach (string file in files) Check(AssemblySpeechPlan.HasTiming(file) && AssemblyFiles.Hash(file) == hashes[file], "real speech: " + Path.GetFileName(file));
                var template = new AssemblyTemplate();
                double enSeconds = Core.AudioDuration(await Core.Probe(probe, files[0], ct)), ruSeconds = Core.AudioDuration(await Core.Probe(probe, files[2], ct));
                var english = AssemblySpeechPlan.CaptionsForAudio(template, files[0], null, enSeconds);
                var russian = AssemblySpeechPlan.CaptionsForAudio(template, files[2], null, ruSeconds);
                Check(string.Join(" ", english.Select(c => c.Text)).ToLowerInvariant().Contains("country"), "actual English ASR words");
                Check(string.Join(" ", russian.Select(c => c.Text)).Count(c => c >= 'а' && c <= 'я') > 15, "actual Russian ASR, no translation");
                string runtime = Path.Combine(root, "subtitles", "runtime.json"); File.Move(runtime, runtime + ".hidden");
                try { await LocalAudioTranscription.PrepareAsync(files, ct, message => Check(message.Contains("уже сохранены"), "cache is used without Python setup")); }
                finally { File.Move(runtime + ".hidden", runtime); }
                // Old timing from an uncut source must not be used for a cut file.
                string cut = Path.Combine(folder, "cut.mp3");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", files[0], "-t", "6", cut }, ct, null, 30);
                File.Copy(LocalAudioTranscription.CacheFile(hashes[files[0]]), cut + ".timing.json");
                Check(!AssemblySpeechPlan.HasTiming(cut), "old timing from full-length audio rejected after cutting");
                await LocalAudioTranscription.PrepareAsync(new[] { cut }, ct, report);
                double cutSeconds = Core.AudioDuration(await Core.Probe(probe, cut, ct));
                Check(AssemblySpeechPlan.CaptionsForAudio(template, cut, null, cutSeconds).All(c => c.End <= cutSeconds), "new timestamps fit cut audio");
                string video = Path.Combine(folder, "background.mp4");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=navy:s=240x320:r=20:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", video }, ct, null, 30);
                var t = new AssemblyTemplate { Materials = folder, Videos = video, Width = 240, Height = 320, Fps = 20, SceneTimingVersion = 1, PackStudioVersion = 1,
                    AllowVideoReuse = true, LoopShortVideos = true, Transition = "cut", RenderMode = "cpu", SubtitleStyle = SubtitlePresets.BuiltIn().First(p => p.Id == "bold-green").Style.Copy() };
                t.Scenes.Add(new AssemblyScene { Duration = 1, VoiceMode = "own", Voiceover = files[0] });
                t.Scenes.Add(new AssemblyScene { Duration = 1, VoiceMode = "own", Voiceover = files[2] });
                var catalog = new List<AssemblyVideo> { new AssemblyVideo { Path = video, Hash = AssemblyFiles.Hash(video), Duration = 1 } };
                var plan = AssemblyPlanner.Create(t, catalog, new AssemblyHistory(), new Random(3), 1).Plans.Single();
                // Exercise the renderer's automatic recognition path, not just the button.
                string badCache = LocalAudioTranscription.CacheFile(hashes[files[0]]);
                var invalid = new JavaScriptSerializer().Deserialize<AssemblySpeechPlan.SpeechTiming>(File.ReadAllText(badCache));
                invalid.Alignment.character_end_times_seconds[invalid.Alignment.character_end_times_seconds.Length - 1] = enSeconds + 60;
                File.WriteAllText(badCache, new JavaScriptSerializer().Serialize(invalid));
                string output = Path.Combine(folder, "two-scene-subtitles.mp4");
                await AssemblyRender.Run(t, plan, ffmpeg, probe, output, Path.Combine(folder, "work"), (a, b, c) => { throw new Exception("No image layers expected"); }, ct, report);
                var media = Core.VideoInfo(await Core.Probe(probe, output, ct));
                double secondStart = Math.Ceiling(enSeconds * t.Fps - .00001) / t.Fps;
                Check(media.HasAudio && Math.Abs(media.VideoDuration - plan.RollDuration(t)) < .15, "real MP4 audio-driven duration");
                Check(plan.Captions.Any(c => c.Start >= secondStart && c.Words.Any(w => w.Start >= secondStart)), "second-scene words and captions share offsets");
                Check(events.Any(m => m.Contains("%")), "recognition emits progress");
                // Shared voiceover uses exactly the same cached recognition.
                var shared = new AssemblyTemplate { Voiceover = files[0], SubtitleStyle = t.SubtitleStyle.Copy() };
                var preview = await AssemblySpeechPlan.BuildCaptionsFromSpeechAsync(shared, probe, ct);
                Check(preview.Count == english.Count && preview[0].Words.Count > 0, "shared imported voice preview");
                var scenePreview = await AssemblySpeechPlan.BuildCaptionsFromSpeechAsync(t, probe, ct);
                Check(scenePreview.Any(c => c.Start >= secondStart), "per-scene preview reads cached imported speech");
                // Silence reports a useful failure instead of creating invented subtitles.
                string silence = Path.Combine(folder, "silence.wav");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "2", silence }, ct, null, 30);
                bool noSpeech = false;
                try { await LocalAudioTranscription.PrepareAsync(new[] { silence }, ct, report); } catch (Exception e) { noSpeech = e.Message.Contains("Речь не найдена"); }
                Check(noSpeech && !AssemblySpeechPlan.HasTiming(silence), "no speech is never accepted as valid captions");
                using (var stop = new CancellationTokenSource()) {
                    string kept = File.ReadAllText(LocalAudioTranscription.CacheFile(hashes[files[1]])); bool cancelled = false;
                    try { await LocalAudioTranscription.PrepareAsync(new[] { files[1] }, stop.Token, message => { if (message.StartsWith("Распознаю")) stop.Cancel(); }, force: true); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && File.ReadAllText(LocalAudioTranscription.CacheFile(hashes[files[1]])) == kept, "running cancellation preserves the previous completed timing");
                }
                foreach (string file in files) Check(AssemblyFiles.Hash(file) == hashes[file] && !File.Exists(file + ".timing.json"), "external sources remain unchanged");
                Console.WriteLine("PASS real CPU ASR: MP3, M4A, WAV, RU/EN, no ElevenLabs API, no reprocessing cached audio, cut-audio repair, automatic render fallback, word styles, two scenes, silent input and running cancellation.");
                return true;
            } finally { Store.Root = previous; }
        }
        public static bool RunUiTests(string folder) {
            Directory.CreateDirectory(folder);
            using (var form = new AudioSubtitleProgressDialog(new[] { "Пример озвучки.mp3" })) Capture(form, Path.Combine(folder, "audio-recognition.png"));
            using (var form = new SubtitleStyleDialog(new WarmupSubtitleStyle(), canRecognizeAudio: true)) {
                form.Size = new Size(850, 590); Capture(form, Path.Combine(folder, "subtitle-recognition-button.png"));
                var button = Descendants(form).OfType<Button>().Single(b => b.Text == "Распознать аудио");
                Check(button.Visible, "recognition action visible at minimum window size");
            }
            return true;
        }
        static IEnumerable<Control> Descendants(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (var child in Descendants(c)) yield return child; } }
        static void Capture(Form form, string file) {
            form.Show(); Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(file); }
            foreach (Control c in Descendants(form).Where(c => c.Visible && !(c.Parent is ScrollableControl scroll && scroll.AutoScroll))) Check(c.Right <= c.Parent.ClientSize.Width + 2 && c.Bottom <= c.Parent.ClientSize.Height + 2, "clipped control: " + c.Text + " " + c.Bounds + " parent=" + c.Parent.ClientSize);
        }
    }
}
