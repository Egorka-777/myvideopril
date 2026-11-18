using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class VideoTranslationTests {
        static void Check(bool ok, string message) { if (!ok) throw new Exception("Video translation: " + message); }
        static void Fails(Action action, string contains) {
            try { action(); } catch (Exception e) { Check(e.Message.Contains(contains), "expected " + contains + ", got " + e.Message); return; }
            throw new Exception("Expected failure: " + contains);
        }
        static VideoTranslationProject Project(string folder, string source, double duration = 4) {
            var p = new VideoTranslationProject { Directory = folder, Source = source, Duration = duration, Options = new VideoTranslationOptions { VoiceId = "voice" },
                Segments = new List<VideoTranslationSegment> { new VideoTranslationSegment { Id = 1, Start = .5, End = 1.5, Original = "Hello.", Text = "Привет." }, new VideoTranslationSegment { Id = 2, Start = 2.5, End = 3.5, Original = "World.", Text = "Мир." } } };
            using (var sha = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(source)) p.SourceHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            p.TimelineHash = VideoTranslationEngine.Timeline(p); return p;
        }
        static string Envelope(string rows, string finish = "stop") => new JavaScriptSerializer().Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content = rows } } } });
        sealed class Handler : HttpMessageHandler {
            internal int Calls;
            internal string Body, Url;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                Calls++; Url = request.RequestUri.ToString(); Body = await request.Content.ReadAsStringAsync();
                Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "secret", "OpenRouter bearer credential");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope("{\"segments\":[{\"id\":2,\"text\":\"Мир.\"},{\"id\":1,\"text\":\"Привет.\"}]}")) };
            }
        }
        public static bool RunSelfTests() {
            Check(AppPlatformNav.IsNavVisible(AppPlatform.Studio, NavSection.AutoTranslation), "autotranslation visible in actual Studio navigation filter");
            foreach (var platform in new[] { AppPlatform.YouTube, AppPlatform.TikTok, AppPlatform.Telegram })
                Check(!AppPlatformNav.IsNavVisible(platform, NavSection.AutoTranslation), "autotranslation not exposed in unrelated workspace: " + platform);
            string folder = Path.Combine(Path.GetTempPath(), "vb-translation-unit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                string source = Path.Combine(folder, "source.mp4"); File.WriteAllText(source, "fixture"); var p = Project(folder, source);
                string longSource = new string('s', 125) + ".mp4";
                Check(VideoTranslationEngine.ProjectDirectoryName(longSource, new DateTime(2026, 11, 18, 15, 29, 40)).Length == 64, "generated project directory limits source basename");
                string cache = Path.Combine(folder, "speech"); Directory.CreateDirectory(cache);
                string cacheKey = VideoTranslationEngine.SpeechKey("cached", "voice"), legacyAudio = Path.Combine(cache, cacheKey + ".mp3");
                Check(Path.GetFileNameWithoutExtension(VideoTranslationEngine.SpeechAudioPath(cache, cacheKey)).Length == 24, "new speech filename is compact");
                File.WriteAllText(legacyAudio, "paid artifact");
                Check(VideoTranslationEngine.SpeechAudioPath(cache, cacheKey) == legacyAudio, "existing full-key paid speech retains original path");
                var handler = new Handler();
                using (var http = new HttpClient(handler)) using (var api = new GeminiTranslationClient("secret", http)) {
                    var result = api.TranslateAsync(p.Segments, p.Options, CancellationToken.None).GetAwaiter().GetResult();
                    Check(result[1] == "Привет." && result[2] == "Мир.", "result matched by id, not array position");
                    var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(handler.Body);
                    Check(body.ContainsKey("messages") && body.ContainsKey("response_format") && handler.Body.Contains("json_schema") && (string)body["model"] == "google/gemini-2.5-flash", "OpenRouter structured schema and legacy model migration");
                    Check(handler.Url == "https://openrouter.ai/api/v1/chat/completions" && !handler.Url.Contains("secret") && handler.Calls == 1, "official endpoint without secret and no retries");
                    string russianKey = VideoTranslationEngine.TranslationKey(p.Segments[0], p.Options);
                    foreach (string language in VideoTranslationEngine.LanguageCodes) {
                        p.Options.TargetLanguage = language;
                        api.TranslateAsync(p.Segments, p.Options, CancellationToken.None).GetAwaiter().GetResult();
                        var payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(handler.Body);
                        var messages = (System.Collections.IList)payload["messages"];
                        string prompt = (string)((Dictionary<string, object>)messages[0])["content"];
                        Check(prompt.Contains("(" + language + ")") && prompt.Contains(VideoTranslationEngine.LanguageName(language)), "target language in Gemini prompt: " + language);
                        if (language != "ru") Check(VideoTranslationEngine.TranslationKey(p.Segments[0], p.Options) != russianKey, "language invalidates old translation: " + language);
                    }
                    p.Options.TargetLanguage = "ru";
                    Check(VideoTranslationEngine.SpeechModel("bn") == "eleven_v3" && VideoTranslationEngine.SpeechModel("hi") == "eleven_multilingual_v2", "Bengali uses documented supported model");
                    Check(VideoTranslationEngine.SpeechKey("same", "voice", "bn") != VideoTranslationEngine.SpeechKey("same", "voice", "ru"), "speech cache includes model");
                }
                var speechWords = (List<SubtitleWord>)typeof(VideoTranslationEngine).GetMethod("SpeechWords", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] {
                    new VideoTranslationSegment { Start = 2, End = 4, Text = "Привет, мир!" },
                    new WarmupAlignment { characters = new[] { "Привет ", "мир " }, character_start_times_seconds = new[] { 0d, .5 }, character_end_times_seconds = new[] { .4, 1d } }, 2d, 1d });
                Check(speechWords.Count == 2 && speechWords[0].Text == "Привет," && speechWords[0].Start == 2 && speechWords[1].Text == "мир!", "provider punctuation normalization preserves real word timing and editable text");
                var subtitleStyle = new WarmupSubtitleStyle { FontSize = 150, LineSpacing = 75, WordsPerCaption = 3 }; subtitleStyle.Normalize();
                Check(subtitleStyle.FontSize == 150 && subtitleStyle.LineSpacing == 75, "extended font size and line spacing");
                var untimed = new WarmupCaption { Text = "Один два три четыре пять шесть семь", Start = 2, End = 9 };
                var groups = SubtitlePresets.ForStyle(new[] { untimed }, subtitleStyle);
                Check(groups.All(c => c.Text.Split(' ').Length <= 3) && string.Join(" ", groups.Select(c => c.Text)) == untimed.Text, "untimed edited text respects word limit without lost words");
                Check(groups.First().Start == 2 && groups.Last().End <= 9, "untimed groups stay inside source window");
                var mixed = SubtitlePresets.ForStyle(new[] { new WarmupCaption { Text = "Начало", Start = 0, End = 1, Words = new List<SubtitleWord> { new SubtitleWord { Text = "Начало", Start = 0, End = 1 } } }, untimed }, subtitleStyle);
                Check(mixed.All(c => c.Text.Split(' ').Length <= 3), "one untimed cue does not disable grouping for the track");
                Fails(() => GeminiTranslationClient.ParseResponse(Envelope("{\"segments\":[]}", "MAX_TOKENS"), new[] { 1 }), "не завершён");
                Fails(() => GeminiTranslationClient.ParseResponse(Envelope("{\"segments\":[{\"id\":1,\"text\":\"a\"},{\"id\":1,\"text\":\"b\"}]}"), new[] { 1, 2 }), "повторяются");
                Fails(() => GeminiTranslationClient.ParseResponse(Envelope("{\"segments\":[{\"id\":8,\"text\":\"a\"}]}"), new[] { 1 }), "чужие");
                Fails(() => GeminiTranslationClient.ParseResponse("{broken", new[] { 1 }), "JSON");
                VideoTranslationEngine.EnsureFits(p.Segments[0], 48000, 48000);
                Fails(() => VideoTranslationEngine.EnsureFits(p.Segments[0], 48001, 48000), "не помещается");
                string key = VideoTranslationEngine.SpeechKey(p.Segments[0].Text, "voice"); p.Segments[0].AudioKey = key; p.Segments[0].AudioPath = "retained.mp3";
                p.Segments[1].AudioKey = VideoTranslationEngine.SpeechKey(p.Segments[1].Text, "voice"); p.Segments[1].AudioPath = "other.mp3";
                VideoTranslationEngine.Save(p); var loaded = VideoTranslationEngine.Load(folder); Check(loaded.Options.GeminiModel == "gemini-2.5-flash" && loaded.Segments[0].AudioPath == "retained.mp3", "project options and resume metadata");
                p.Segments[0].Text = "Изменено"; VideoTranslationEngine.Save(p);
                Check(p.Segments[0].AudioPath == "" && p.Segments[1].AudioPath == "other.mp3", "only edited segment audio invalidated");
                p.Options.TargetLanguage = "hi";
                Fails(() => VideoTranslationEngine.RenderCore(p, p.Options, "unused", "unused", CancellationToken.None, null, text => { throw new Exception("Unexpected paid generation"); }).GetAwaiter().GetResult(), "Язык перевода изменён");
                p.Options.TargetLanguage = "ru";
                p.Segments[0].Start += .01; Fails(() => VideoTranslationEngine.Save(p), "таймкоды"); p.Segments[0].Start -= .01;
                Check(VideoTranslationEngine.Srt(p.Segments).Contains("00:00:00,500 --> 00:00:01,500\nИзменено"), "SRT editable text and original window");
                var longProject = Project(Path.Combine(folder, "long"), source, 1800);
                longProject.Segments = Enumerable.Range(0, 1500).Select(i => new VideoTranslationSegment { Id = i + 1, Start = i * 1.2, End = i * 1.2 + 1, Original = "Long source " + i, Text = "Перевод " + i }).ToList();
                longProject.TimelineHash = VideoTranslationEngine.Timeline(longProject); VideoTranslationEngine.Save(longProject);
                var resumed = VideoTranslationEngine.Load(longProject.Directory);
                Check(resumed.Segments.Count == 1500 && resumed.Segments.Last().End == longProject.Segments.Last().End, "30 minute / 1500 segment project roundtrip without shifted timings");
                Console.WriteLine("PASS translation: official Gemini mock contract, id integrity, truncation, fixed timing, project save/load, targeted invalidation, SRT and 30 minute / 1500 segment roundtrip."); return true;
            } finally { Directory.Delete(folder, true); }
        }
        public static bool RunIntegrationTests(string ffmpeg, string ffprobe, string root) => Integration(ffmpeg, ffprobe, root).GetAwaiter().GetResult();
        static async Task<bool> Integration(string ffmpeg, string ffprobe, string root) {
            Directory.CreateDirectory(root); var ct = CancellationToken.None;
            string source = Path.Combine(root, "source.mp4"), shortAudio = Path.Combine(root, "short.mp3"), longAudio = Path.Combine(root, "long.mp3");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=navy:s=240x320:r=25:d=4", "-f", "lavfi", "-i", "sine=frequency=220:duration=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, ct);
            foreach (var item in new[] { new { Path = shortAudio, Seconds = "0.4" }, new { Path = longAudio, Seconds = "1.2" } })
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=880:duration=" + item.Seconds, "-c:a", "libmp3lame", item.Path }, ct);
            // Reproduce the reported 197-character project path: old speech temp
            // was 277 characters and receipt temp was 322 on this existing project.
            string projectFolder = Path.Combine(root, new string('p', Math.Max(1, 197 - Path.GetFullPath(root).Length - 1)));
            var p = Project(projectFolder, source); int calls = 0;
            Func<string, Task<ElevenLabsSpeech>> speech = text => { calls++; return Task.FromResult(new ElevenLabsSpeech { Audio = File.ReadAllBytes(shortAudio), Text = text, Alignment = new WarmupAlignment { characters = new[] { text }, character_start_times_seconds = new[] { .01 }, character_end_times_seconds = new[] { .35 } } }); };
            await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, Console.WriteLine, speech);
            Check(calls == 2 && File.Exists(p.Output), "real encode with mocked paid generation");
            Check(p.Segments.All(s => s.AudioPath.Length < 260 && (s.AudioPath + ".speech.json").Length < 260), "reported long project path saves audio and receipt below MAX_PATH");
            string timeline = Path.Combine(projectFolder, "render", "timeline.wav");
            using (var stream = File.OpenRead(timeline)) { stream.Position = 44; var bytes = new byte[24000]; stream.Read(bytes, 0, bytes.Length); Check(bytes.All(b => b == 0), "original sound removed: silent initial gap"); stream.Position = 44 + 48000; stream.Read(bytes, 0, bytes.Length); Check(bytes.Any(b => b != 0), "speech starts inside first fixed window"); stream.Position = 44 + 48000 * 2; stream.Read(bytes, 0, bytes.Length); Check(bytes.All(b => b == 0), "inter-segment gap stays silent"); }
            p.Options.BurnSubtitles = true;
            await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, Console.WriteLine, speech);
            Check(calls == 2, "burn-in render retry reuses audio without paid generation");
            p.Segments[0].Text = "Новый текст."; await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, Console.WriteLine, speech);
            Check(calls == 3, "editing one segment synthesizes only one replacement");
            p.Segments[0].Text = "Слишком долго.";
            Func<string, Task<ElevenLabsSpeech>> overrun = text => { calls++; return Task.FromResult(new ElevenLabsSpeech { Audio = File.ReadAllBytes(longAudio), Text = text, Alignment = new WarmupAlignment { characters = new[] { text }, character_start_times_seconds = new[] { .01 }, character_end_times_seconds = new[] { 1.1 } } }); };
            for (int i = 0; i < 2; i++) {
                await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, Console.WriteLine, overrun);
                using (var stream = File.OpenRead(timeline)) {
                    stream.Position = 44 + 48000 * 3;
                    var gap = new byte[24000]; stream.Read(gap, 0, gap.Length);
                    Check(gap.All(b => b == 0), "fitted speech cannot overrun into following gap");
                }
            }
            Check(calls == 4 && p.Segments[1].Start == 2.5, "overrun fitted locally and cached, no repeated paid request or timeline shift");
            // Exact final-stage failure: 197-char project + translated-studio.mp4
            // + .tags-<32-char GUID>.mp4 gave a 261-char File.Replace source.
            string studioOutput = Path.Combine(projectFolder, "translated-studio.mp4");
            File.Copy(p.Output, studioOutput, true);
            var metadata = new AssemblyOutputMetadata { DeviceMetadata = false, CaptureDate = false, Software = "" };
            await AssemblyOutputTags.Apply(ffmpeg, ffprobe, studioOutput, "Long path translation", metadata, new Random(17), ct);
            var tagged = await Core.Probe(ffprobe, studioOutput, ct);
            VideoMetadata.Verify(tagged, metadata.Sample(new Random(17), DateTime.Now), "Long path translation");
            Check(File.Exists(studioOutput), "metadata atomic replacement succeeds at reported 261-char legacy temp path");
            Console.WriteLine("PASS translation real FFmpeg: fixed-window PCM timeline, original audio removed, MP4, shared subtitle burn-in, resume, targeted text edit, cached local tempo fitting and long-path final metadata replacement. No live paid API called."); return true;
        }
#if VIDEO_TRANSLATION_TEST_HOST
        public static int Main(string[] args) {
            try {
                RunSelfTests();
                if (args.Length == 4 && args[0] == "--cached-project") {
                    var project = VideoTranslationEngine.Load(args[3]);
                    VideoTranslationEngine.RenderCore(project, project.Options, args[1], args[2], CancellationToken.None, Console.WriteLine,
                        text => { throw new Exception("Cached-only test: missing saved speech; paid API generation was not called."); }).GetAwaiter().GetResult();
                    var layout = AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(project.Directory, "montage.xml"));
                    project.Output = AssemblyWindow.RenderTranslationStudioAsync(layout, project.Output, Path.Combine(project.Directory, "translated-studio.mp4"),
                        Path.Combine(project.Directory, "montage-render"), args[1], args[2], CancellationToken.None, Console.WriteLine).GetAwaiter().GetResult();
                    VideoTranslationEngine.Save(project);
                } else if (args.Length == 3) RunIntegrationTests(args[0], args[1], args[2]);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
#endif
    }
}
