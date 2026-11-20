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
            internal string Body, Url, ResponseRows = "{\"segments\":[{\"id\":2,\"text\":\"Мир.\"},{\"id\":1,\"text\":\"Привет.\"}]}";
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                Calls++; Url = request.RequestUri.ToString(); Body = await request.Content.ReadAsStringAsync();
                Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "secret", "OpenRouter bearer credential");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope(ResponseRows)) };
            }
        }
        public static bool RunSelfTests() {
            Check(AppPlatformNav.IsNavVisible(AppPlatform.Studio, NavSection.AutoTranslation), "autotranslation visible in actual Studio navigation filter");
            foreach (var platform in new[] { AppPlatform.YouTube, AppPlatform.TikTok, AppPlatform.Telegram })
                Check(!AppPlatformNav.IsNavVisible(platform, NavSection.AutoTranslation), "autotranslation not exposed in unrelated workspace: " + platform);
            string folder = Path.Combine(Path.GetTempPath(), "vb-translation-unit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                string source = Path.Combine(folder, "source.mp4"); File.WriteAllText(source, "fixture"); var p = Project(folder, source);
                var previousRecognition = Project(Path.Combine(folder, "previous"), source);
                previousRecognition.Segments[0].TranslationKey = VideoTranslationEngine.TranslationKey(previousRecognition.Segments[0], previousRecognition.Options);
                previousRecognition.Segments[0].AudioKey = VideoTranslationEngine.SpeechKey("Привет.", "voice");
                previousRecognition.Segments[0].AudioPath = "paid.mp3";
                var recognized = Project(Path.Combine(folder, "recognized"), source);
                recognized.Segments[0].Start = .8; recognized.Segments[0].End = 1.8;
                recognized.Segments.ForEach(s => { s.Text = ""; s.TranslationKey = ""; });
                recognized.Segments.Insert(0, new VideoTranslationSegment { Id = 3, Start = .1, End = .4, Original = "Missing speech!" });
                VideoTranslationEngine.RestoreRecognizedTranslations(recognized, previousRecognition);
                Check(recognized.Segments[0].Text == "" && recognized.Segments[1].Text == "Привет." && recognized.Segments[1].Start == .8,
                    "recognition restores missing speech and refreshes timestamps instead of returning stale project rows");
                Check(recognized.Segments[1].AudioPath == "paid.mp3" && recognized.Segments[1].AudioKey == previousRecognition.Segments[0].AudioKey,
                    "retimed unchanged utterance retains paid speech without generation");
                Check(recognized.Segments[1].TranslationKey == VideoTranslationEngine.TranslationKey(recognized.Segments[1], recognized.Options),
                    "retimed preserved translation uses refreshed timing key");
                var repeated = Project(Path.Combine(folder, "repeated"), source);
                repeated.Segments.ForEach(s => { s.Original = "Hello."; s.Text = ""; });
                VideoTranslationEngine.RestoreRecognizedTranslations(repeated, previousRecognition);
                Check(repeated.Segments[0].Text == "Привет." && repeated.Segments[1].Text == "", "only exact timed match reused when utterances repeat");
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
                var shorteningHandler = new Handler { ResponseRows = "{\"segments\":[{\"id\":1,\"text\":\"Привет!\"}]}" };
                using (var http = new HttpClient(shorteningHandler)) using (var api = new GeminiTranslationClient("secret", http)) {
                    string result = api.ShortenAsync(p.Segments[0], p.Options, 2.4, CancellationToken.None).GetAwaiter().GetResult();
                    var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(shorteningHandler.Body);
                    var messages = (System.Collections.IList)body["messages"];
                    var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>((string)((Dictionary<string, object>)messages[1])["content"]);
                    var row = rows.Single();
                    Check(shorteningHandler.Calls == 1 && result == "Привет!" && Convert.ToInt32(row["id"]) == 1 && Convert.ToDouble(row["windowSeconds"]) == 1 && Convert.ToDouble(row["speechSeconds"]) == 2.4 && (string)row["original"] == p.Segments[0].Original && (string)row["previousText"] == p.Segments[0].Text,
                        "one shortening request includes id, immutable window, original, previous translation and measured speech duration");
                    Check(shorteningHandler.Url == "https://openrouter.ai/api/v1/chat/completions" && shorteningHandler.Body.Contains("json_schema") && (string)body["model"] == "google/gemini-2.5-flash", "shortening uses same official structured API without retries");
                }
                var speechWords = VideoTranslationEngine.SpeechWords(new VideoTranslationSegment { Start = 2, End = 4, Text = "Привет, мир!" },
                    new WarmupAlignment { characters = new[] { "Привет ", "мир " }, character_start_times_seconds = new[] { 0d, .5 }, character_end_times_seconds = new[] { .4, 1d } }, 2, 1, 0, 2);
                var shiftedWords = VideoTranslationEngine.SpeechWords(new VideoTranslationSegment { Start = 2.5, End = 3.5, Text = "Один два" },
                    new WarmupAlignment { characters = new[] { " ", "Один", " ", "два", " " }, character_start_times_seconds = new[] { 0d, .3, .5, .7, 1d }, character_end_times_seconds = new[] { .3, .5, .7, 1d, 1.5 } }, 1.5, .5, .3, .35);
                Check(shiftedWords[0].Start == 2.5 && Math.Abs(shiftedWords[1].Start - 2.7) < 1e-9 && Math.Abs(shiftedWords[1].End - 2.85) < 1e-9,
                    "caption times subtract PCM trim offset before local tempo scaling, never shift source onset");
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
        static async Task SynchronousTimingTests(string ffmpeg, string ffprobe, string root, string source, CancellationToken ct) {
            // Lossless fixture makes onset/end assertions sample-exact, unlike MP3/AAC
            // ringing. The integration below separately exercises actual MP3 paid artifacts.
            string padded = Path.Combine(root, "padded.wav"), raw = Path.Combine(root, "padded.pcm");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=if(between(n\\,14400\\,33599)\\,0.1*cos(2*PI*880*(n-14400)/48000)\\,0):s=48000:d=1.5", "-c:a", "pcm_s16le", padded }, ct);
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", padded, "-f", "s16le", raw }, ct);
            var alignment = new WarmupAlignment { characters = new[] { " ", "Т", "е", "с", "т", " " },
                character_start_times_seconds = new[] { 0d, .28, .4, .5, .6, .72 }, character_end_times_seconds = new[] { .28, .4, .5, .6, .72, 1.5 } };
            var bounds = VideoTranslationEngine.SpeechBounds(raw, alignment);
            Check(bounds.Item1 == 14400 && bounds.Item2 == 33600, "remove artificial leading/trailing silence, including digital silence inside aligned bounds, sample-exact");
            string paddedMp3 = Path.Combine(root, "padded.mp3"), mp3Pcm = Path.Combine(root, "padded-mp3.pcm");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", padded, "-c:a", "libmp3lame", "-b:a", "128k", paddedMp3 }, ct);
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", paddedMp3, "-ac", "1", "-ar", "48000", "-f", "s16le", mp3Pcm }, ct);
            var mp3Bounds = VideoTranslationEngine.SpeechBounds(mp3Pcm, alignment);
            Check(mp3Bounds.Item1 > 12000 && mp3Bounds.Item1 <= 14400 && mp3Bounds.Item2 >= 33600 && mp3Bounds.Item2 < 36000,
                "real MP3 decoder silence is removed while codec ringing around audible tone is preserved");
            var pcm = File.ReadAllBytes(raw);
            // Quiet consonants outside provider timestamps must not be clipped.
            pcm[12000 * 2] = 2; pcm[35000 * 2] = 2; File.WriteAllBytes(raw, pcm);
            bounds = VideoTranslationEngine.SpeechBounds(raw, alignment);
            Check(bounds.Item1 == 12000 && bounds.Item2 == 35001, "preserve low-level acoustic content outside imperfect provider bounds without arbitrary pre-roll");
            var p = Project(Path.Combine(root, "onset"), source); int calls = 0;
            Func<string, Task<ElevenLabsSpeech>> generate = text => { calls++; return Task.FromResult(new ElevenLabsSpeech { Text = text, Audio = File.ReadAllBytes(padded), NormalizedAlignment = alignment }); };
            await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, null, generate);
            string timeline = Path.Combine(p.Directory, "render", "timeline.wav");
            var bytes = File.ReadAllBytes(timeline);
            foreach (var segment in p.Segments) {
                int start = (int)Math.Round(segment.Start * 48000), end = start + 19200;
                Check(BitConverter.ToInt16(bytes, 44 + start * 2) != 0 && BitConverter.ToInt16(bytes, 44 + (start - 1) * 2) == 0, "audible translated onset is exactly immutable source onset");
                Check(BitConverter.ToInt16(bytes, 44 + (end - 1) * 2) != 0 && BitConverter.ToInt16(bytes, 44 + end * 2) == 0, "trimmed phrase end is sample-exact, no padding-induced tempo fitting");
            }
            var artifacts = p.Segments.SelectMany(s => new[] { s.AudioPath, s.AudioPath + ".speech.json" }).ToDictionary(path => path, File.ReadAllBytes);
            await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, null, generate);
            Check(calls == 2 && artifacts.All(a => a.Value.SequenceEqual(File.ReadAllBytes(a.Key))), "cached render leaves original paid bytes and receipts untouched");
            string envelope = Path.Combine(root, "envelope.pcm");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "aevalsrc=0.5:s=48000:d=4", "-af", VideoTranslationEngine.DuckFilter(new[] { Tuple.Create(.5, .9), Tuple.Create(.96, 1.2), Tuple.Create(2.5, 2.9) }), "-f", "s16le", envelope }, ct);
            var gains = File.ReadAllBytes(envelope);
            Func<double, double> gain = time => BitConverter.ToInt16(gains, (int)Math.Round(time * 48000) * 2) / 16384d;
            Check(Math.Abs(gain(.44) - 1) < .0001 && Math.Abs(gain(.475) - .575) < .0001 && Math.Abs(gain(.5) - .15) < .0001,
                "original is full outside local ramp, smoothly ducks before exact speech onset");
            Check(Math.Abs(gain(1.225) - .575) < .0001 && Math.Abs(gain(1.26) - 1) < .0001 && Math.Abs(gain(1.5) - 1) < .0001,
                "original restores smoothly after speech and remains unchanged in gaps");
            Check(gain(.93) >= .15 && gain(.93) < 1, "overlapping attack/release ramps do not compound ducking");
            for (int i = 1; i < gains.Length / 2; i++) Check(Math.Abs(BitConverter.ToInt16(gains, i * 2) - BitConverter.ToInt16(gains, (i - 1) * 2)) <= 7, "duck envelope has no abrupt sample/frame gain jump");
            Console.WriteLine("PASS synchronous voiceover: sample-exact padded-tone onset/end, preserved quiet boundary audio, caption offset/tempo, unchanged paid artifacts, smooth sample-level 15% duck/restore including overlapping ramps.");
        }
        static async Task ShorteningTests(string ffmpeg, string ffprobe, string root, string source, string shortAudio, string longAudio, CancellationToken ct) {
            var p = Project(Path.Combine(root, "shortening"), source); int generations = 0, requests = 0;
            p.Segments[0].Text = "Очень длинная переведённая реплика.";
            p.Segments[0].TranslationKey = VideoTranslationEngine.TranslationKey(p.Segments[0], p.Options);
            string originalText = p.Segments[0].Text;
            Func<string, Task<ElevenLabsSpeech>> generate = text => {
                generations++; bool over = text == originalText;
                return Task.FromResult(new ElevenLabsSpeech { Text = text, Audio = File.ReadAllBytes(over ? longAudio : shortAudio), Alignment = new WarmupAlignment {
                    characters = new[] { text }, character_start_times_seconds = new[] { 0d }, character_end_times_seconds = new[] { over ? 1.2 : .4 } } });
            };
            Func<VideoTranslationSegment, double, Task<string>> shorten = (s, seconds) => {
                requests++; Check(s.Id == 1 && s.Text == originalText && Math.Abs(seconds - 1.2) < .01, "shorten only overlong segment with trimmed measured seconds");
                return Task.FromResult("Кратко.");
            };
            await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, null, generate, shorten);
            Check(generations == 3 && requests == 1 && p.Segments[0].Text == "Кратко." && p.Segments[1].Start == 2.5 && p.Segments[0].TranslationKey == VideoTranslationEngine.TranslationKey(p.Segments[0], p.Options),
                "one shortened replacement updates valid translation and preserves future onset");
            string oldPath = VideoTranslationEngine.SpeechAudioPath(Path.Combine(p.Directory, "speech"), VideoTranslationEngine.SpeechKey(originalText, "voice"));
            var artifacts = Directory.GetFiles(Path.Combine(p.Directory, "speech")).Where(path => path.EndsWith(".mp3") || path.EndsWith(".speech.json")).ToDictionary(path => path, File.ReadAllBytes);
            Check(File.Exists(oldPath) && File.Exists(oldPath + ".speech.json"), "original paid result survives shortened replacement");
            p = VideoTranslationEngine.Load(p.Directory);
            await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, null, generate, shorten);
            Check(generations == 3 && requests == 1 && artifacts.All(a => a.Value.SequenceEqual(File.ReadAllBytes(a.Key))), "resume uses saved shortened text and paid receipts without new API calls");
            // Re-selecting the old translation still reuses the already-paid shortened artifact.
            p.Segments[0].Text = originalText;
            await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, null, generate, shorten);
            Check(generations == 3 && requests == 2, "shortened-text cache reused without replacement synthesis");
            var unchanged = Project(Path.Combine(root, "unchanged"), source); unchanged.Segments[0].Text = originalText;
            int before = generations, unchangedRequests = 0;
            Func<VideoTranslationSegment, double, Task<string>> same = (s, seconds) => { unchangedRequests++; return Task.FromResult(s.Text); };
            await VideoTranslationEngine.RenderCore(unchanged, unchanged.Options, ffmpeg, ffprobe, ct, null, generate, same);
            Check(generations == before + 2 && unchangedRequests == 1, "unchanged shortening fits existing audio without regeneration");
            unchanged = VideoTranslationEngine.Load(unchanged.Directory);
            await VideoTranslationEngine.RenderCore(unchanged, unchanged.Options, ffmpeg, ffprobe, ct, null, generate, same);
            Check(generations == before + 2 && unchangedRequests == 1, "unchanged-text attempt is persisted across resume, no repeated paid shortening");
            var residual = Project(Path.Combine(root, "residual"), source); residual.Segments[0].Text = originalText;
            int residualRequests = 0, residualGeneration = 0;
            Func<string, Task<ElevenLabsSpeech>> stillLong = text => {
                residualGeneration++; return Task.FromResult(new ElevenLabsSpeech { Text = text, Audio = File.ReadAllBytes(longAudio), Alignment = new WarmupAlignment {
                    characters = new[] { text }, character_start_times_seconds = new[] { 0d }, character_end_times_seconds = new[] { 1.2 } } });
            };
            await VideoTranslationEngine.RenderCore(residual, residual.Options, ffmpeg, ffprobe, ct, null, stillLong, (s, seconds) => { residualRequests++; return Task.FromResult("Сокращено."); });
            Check(residualRequests == 2 && residualGeneration == 3 && residual.Segments[1].Start == 2.5, "each overrun has one bounded shortening and replacement then residual tempo fit");
            await VideoTranslationEngine.RenderCore(VideoTranslationEngine.Load(residual.Directory), residual.Options, ffmpeg, ffprobe, ct, null, stillLong,
                (s, seconds) => { throw new Exception("Unexpected repeated shortening on resume"); });
            Check(residualGeneration == 3, "residual overrun resume reuses paid speech and does not shorten again");
            var failed = Project(Path.Combine(root, "shortening-failure"), source); failed.Segments[0].Text = originalText;
            int failureRequests = 0;
            Fails(() => VideoTranslationEngine.RenderCore(failed, failed.Options, ffmpeg, ffprobe, ct, null, generate,
                (s, seconds) => { failureRequests++; throw new Exception("shortening failed"); }).GetAwaiter().GetResult(), "shortening failed");
            Check(failureRequests == 1 && failed.Output == "" && File.Exists(failed.Segments[0].AudioPath + ".speech.json"), "shortening failure propagates without fallback success; initial paid receipt retained");
            var replacementFailure = Project(Path.Combine(root, "replacement-failure"), source); replacementFailure.Segments[0].Text = originalText;
            int replacementCalls = 0, replacementRequests = 0;
            Func<VideoTranslationSegment, double, Task<string>> accepted = (s, seconds) => { replacementRequests++; return Task.FromResult("После сбоя."); };
            Fails(() => VideoTranslationEngine.RenderCore(replacementFailure, replacementFailure.Options, ffmpeg, ffprobe, ct, null,
                text => { replacementCalls++; if (text == "После сбоя.") throw new Exception("replacement synthesis failed"); return generate(text); }, accepted).GetAwaiter().GetResult(), "replacement synthesis failed");
            replacementFailure = VideoTranslationEngine.Load(replacementFailure.Directory);
            Check(replacementCalls == 2 && replacementRequests == 1 && replacementFailure.Segments[0].Text == "После сбоя." && replacementFailure.Output == "", "replacement synthesis error propagates with accepted shortened text persisted");
            await VideoTranslationEngine.RenderCore(replacementFailure, replacementFailure.Options, ffmpeg, ffprobe, ct, null, generate, accepted);
            Check(replacementRequests == 1, "resume after replacement failure does not pay for shortening again");
            Console.WriteLine("PASS bounded shortening: measured request, one replacement, unchanged text, shortened cache reuse, residual fit, resume without paid calls, preserved original artifacts and propagated errors.");
        }
        public static bool RunIntegrationTests(string ffmpeg, string ffprobe, string root) => Integration(ffmpeg, ffprobe, root).GetAwaiter().GetResult();
        static async Task<bool> Integration(string ffmpeg, string ffprobe, string root) {
            Directory.CreateDirectory(root); var ct = CancellationToken.None;
            string source = Path.Combine(root, "source.mp4"), shortAudio = Path.Combine(root, "short.mp3"), longAudio = Path.Combine(root, "long.mp3");
            await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=navy:s=240x320:r=25:d=4", "-f", "lavfi", "-i", "sine=frequency=220:duration=4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source }, ct);
            foreach (var item in new[] { new { Path = shortAudio, Seconds = "0.4" }, new { Path = longAudio, Seconds = "1.2" } })
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=880:duration=" + item.Seconds, "-c:a", "libmp3lame", item.Path }, ct);
            await SynchronousTimingTests(ffmpeg, ffprobe, root, source, ct);
            await ShorteningTests(ffmpeg, ffprobe, root, source, shortAudio, longAudio, ct);
            // Reproduce the reported 197-character project path: old speech temp
            // was 277 characters and receipt temp was 322 on this existing project.
            string projectFolder = Path.Combine(root, new string('p', Math.Max(1, 197 - Path.GetFullPath(root).Length - 1)));
            var p = Project(projectFolder, source); int calls = 0;
            Func<string, Task<ElevenLabsSpeech>> speech = text => { calls++; return Task.FromResult(new ElevenLabsSpeech { Audio = File.ReadAllBytes(shortAudio), Text = text, Alignment = new WarmupAlignment { characters = new[] { text }, character_start_times_seconds = new[] { .01 }, character_end_times_seconds = new[] { .35 } } }); };
            await VideoTranslationEngine.RenderCore(p, p.Options, ffmpeg, ffprobe, ct, Console.WriteLine, speech);
            Check(calls == 2 && File.Exists(p.Output), "real encode with mocked paid generation");
            Check(p.Segments.All(s => s.AudioPath.Length < 260 && (s.AudioPath + ".speech.json").Length < 260), "reported long project path saves audio and receipt below MAX_PATH");
            string timeline = Path.Combine(projectFolder, "render", "timeline.wav");
            using (var stream = File.OpenRead(timeline)) { stream.Position = 44; var bytes = new byte[24000]; stream.Read(bytes, 0, bytes.Length); Check(bytes.All(b => b == 0), "translation-only track has silent initial gap"); stream.Position = 44 + 48000; stream.Read(bytes, 0, bytes.Length); Check(bytes.Any(b => b != 0), "speech starts inside first fixed window"); stream.Position = 44 + 48000 * 2; stream.Read(bytes, 0, bytes.Length); Check(bytes.All(b => b == 0), "inter-segment gap stays silent"); }
            string decoded = Path.Combine(root,"mixed.pcm");
            await Core.Tool(ffmpeg,new[]{"-v","error","-y","-i",p.Output,"-ac","1","-ar","48000","-f","s16le",decoded},ct);
            var samples=File.ReadAllBytes(decoded);
            Func<double,double,double> amplitude=(start,frequency)=>{
                int first=(int)(start*48000),count=9600;double real=0,imaginary=0;
                for(int i=0;i<count;i++){double value=BitConverter.ToInt16(samples,(first+i)*2)/32768d;double angle=2*Math.PI*frequency*i/48000;real+=value*Math.Cos(angle);imaginary+=value*Math.Sin(angle);}
                return 2*Math.Sqrt(real*real+imaginary*imaginary)/count;
            };
            double originalGap=amplitude(.1,220),originalUnderSpeech=amplitude(.6,220);
            Check(originalGap>.05 && originalUnderSpeech/originalGap>.1 && originalUnderSpeech/originalGap<.2,"original sound retained in gap and ducked to 15% during speech");
            Check(amplitude(.6,880)>.05 && amplitude(.1,880)<.005,"translated speech begins only at original fixed onset");
            Check(amplitude(1.1,220)/originalGap>.9,"original volume restored after actual translated audio ends");
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
            Console.WriteLine("PASS translation real FFmpeg: fixed-window PCM timeline, original audio ducked under speech, MP4, shared subtitle burn-in, resume, targeted text edit, cached local tempo fitting and long-path final metadata replacement. No live paid API called."); return true;
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
