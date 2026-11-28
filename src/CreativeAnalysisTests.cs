using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    // Offline contract tests only. No real provider credentials or billable requests.
    public static class CreativeAnalysisTests {
        static void Check(bool value, string name) { if (!value) throw new Exception("Creative analysis: " + name); }
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        sealed class FakeApi : HttpMessageHandler {
            public int Calls;
            public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                ct.ThrowIfCancellationRequested(); Calls++; return Reply(request, ct);
            }
        }
        static HttpResponseMessage Reply(string text, HttpStatusCode code = HttpStatusCode.OK) => new HttpResponseMessage(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        static string Payload(string introStatus = "ready", int unreadable = 0) => Json().Serialize(new {
            opening_screen_text = "Исходная надпись", visual_context = "Наблюдаемая ситуация",
            intro = new { status = introStatus, spoken_text = introStatus == "ready" ? "Короткая завязка." : "", source_basis = introStatus == "ready" ? "screen_text" : "insufficient", source_quote = "Исходная надпись", reason = "" },
            comments = Enumerable.Range(1, 3).Select(i => new { id = i, status = i == unreadable ? "unreadable" : "ready", text = i == unreadable ? "" : "Комментарий " + i, reason = "" }).ToArray()
        });
        static string Envelope(string payload, string finish = "stop") => Json().Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content = payload } } } });
        static CreativeProject Project() => new CreativeProject {
            IntroAudio = "old-intro.mp3", Comments = Enumerable.Range(1, 3).Select(i => new CreativeComment { AudioPath = "old-" + i + ".mp3" }).ToList()
        };
        public static bool RunLanguageTests() {
            var project=Project();project.IntroText="Завязка";project.Source="source.mp4";project.DetectedLanguage="es";project.SourceTitle="Un título español";project.LanguageSource="title:"+project.Source+"\n"+project.SourceTitle;
            Check(project.IntroLanguage=="es","intro uses title language");project.Preset.AutoIntroLanguage=false;Check(project.IntroLanguage=="ru","manual fallback language");project.Preset.AutoIntroLanguage=true;
            for(int i=0;i<3;i++)project.Comments[i].Text="Комментарий "+i;
            var api=new FakeApi();api.Reply=async(req,ct)=>{
                string body=await req.Content.ReadAsStringAsync();Check(body.Contains("creative_translation")&&body.Contains("target_language"),"translation wire format");
                return Reply(Envelope(Json().Serialize(new{intro="A short hook.",comment1="First comment",comment2="Second comment",comment3="Third comment"})));
            };
            using(var http=new HttpClient(api)) {
                project.Preset.TranslateTexts=true;project.Preset.TranslationLanguage="en";Check(project.IntroLanguage=="en","translation overrides detected language");
                CreativePreparation.TranslateTextsAsync(project,"fixture","",CancellationToken.None,null,http).GetAwaiter().GetResult();
                Check(project.IntroText=="A short hook."&&project.Comments[2].Text=="Third comment","all four texts translated");
                Check(project.IntroAudio==""&&project.Comments.All(c=>c.AudioPath==""),"stale audio invalidated");
                CreativePreparation.TranslateTextsAsync(project,"fixture","",CancellationToken.None,null,http).GetAwaiter().GetResult();Check(api.Calls==1,"resume skips already translated text");
                string before=project.IntroText;project.Preset.TranslationLanguage="de";api.Reply=(req,ct)=>Task.FromResult(Reply(Envelope("{}")));
                bool failed=false;try{CreativePreparation.TranslateTextsAsync(project,"fixture","",CancellationToken.None,null,http).GetAwaiter().GetResult();}catch(Exception){failed=true;}
                Check(failed&&project.IntroText==before,"invalid translation leaves texts unchanged");
            }
            Console.WriteLine("Creative language PASS: audio language, fallback, target override, atomic translation, audio invalidation, resume, malformed response");return true;
        }
        public static bool RunParserDiagnosticsTests() {
            string rootBefore = Store.Root;
            string work = Path.Combine(Path.GetTempPath(), "VideoBatch-CreativeParser-" + Guid.NewGuid().ToString("N"));
            try {
                Store.Root = work;
                var cases = new Dictionary<string, string> {
                    { "{}", "opening_screen_text.required" },
                    { Payload().Replace("\"id\":2", "\"id\":1"), "comments[1].id.order" },
                    { Payload().Replace("Комментарий 3", ""), "comments[2].text.status_consistency" },
                    { Payload().Replace("\"reason\":\"\"", "\"other\":\"\""), "intro.reason.required" },
                    { Payload().Replace("\"source_quote\":\"Исходная надпись\"", "\"source_quote\":\"\""), "intro.source_quote.ready_empty" }
                };
                foreach (var item in cases) {
                    bool failed = false;
                    string raw = Json().Serialize(new {
                        id = "gen-fixture", model = "google/gemini-2.5-flash", provider = "Google AI Studio",
                        choices = new[] { new { finish_reason = "stop", message = new { content = item.Key + " " } } }
                    });
                    try { CreativeAnalysisClient.ParseResponse(raw); }
                    catch (Exception ex) { failed = ex.Message.Contains(item.Value); }
                    Check(failed, "precise validation: " + item.Value);
                }
                string log = string.Join("", Directory.GetFiles(AppDiagnostics.Folder).Select(File.ReadAllText));
                Check(log.Contains("gen-fixture") && log.Contains("Google AI Studio") && log.Contains("comments[1].id.order"), "response correlation and validation logged");
                Check(!log.Contains("Исходная надпись") && !log.Contains("Комментарий"), "no source text in diagnostics");
                // Real Google AI Studio envelopes include leading keepalive whitespace and
                // may return multiline OCR; neither is an invalid structured response.
                string accepted = "\n         " + Envelope(Payload().Replace("Комментарий 3", "Комментарий 3\\n\\n"));
                Check(CreativeAnalysisClient.ParseResponse(accepted).comments[2].text == "Комментарий 3\n\n", "observed real envelope and OCR formatting accepted unchanged");
                return true;
            } finally { Store.Root = rootBefore; if (Directory.Exists(work)) Directory.Delete(work, true); }
        }
        static void ContextTimingTests() {
            string previous = Store.Root, folder = Path.Combine(Path.GetTempPath(), "VideoBatch-ContextTiming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                Store.Root = folder;
                string audio = Path.Combine(folder, "silence.wav"); File.WriteAllText(audio, "fixture audio bytes");
                string hash = AssemblyFiles.Hash(audio), file = LocalAudioTranscription.ContextCacheFile(hash);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var timing = new LocalAudioTranscription.ContextTiming {
                    AudioHash = hash, Source = "faster-whisper/small", Version = LocalAudioTranscription.Version,
                    Status = "no_speech", Text = "", Alignment = new WarmupAlignment {
                        characters = new string[0], character_start_times_seconds = new double[0], character_end_times_seconds = new double[0]
                    }
                };
                File.WriteAllText(file, Json().Serialize(timing));
                Check(LocalAudioTranscription.ReadContextTiming(audio)?.Status == "no_speech", "explicit no speech context accepted");
                Check(LocalAudioTranscription.ContextCaptions(audio, 2).Count == 0, "no speech provides empty visual-only context");
                Check(!AssemblySpeechPlan.HasTiming(audio) && !File.Exists(LocalAudioTranscription.CacheFile(hash)), "context no speech never satisfies strict subtitle cache");
                LocalAudioTranscription.PrepareAsync(new[] { audio }, CancellationToken.None, allowEmpty: true).GetAwaiter().GetResult();
                timing.Status = "speech"; timing.Text = "Речь"; timing.Alignment = new WarmupAlignment {
                    characters = new[] { "Речь " }, character_start_times_seconds = new[] { .2 }, character_end_times_seconds = new[] { .7 }
                };
                File.WriteAllText(file, Json().Serialize(timing));
                Check(LocalAudioTranscription.ContextCaptions(audio, 2).Single().Text == "Речь", "speech context retains timed recognition");
                timing.Status = null; File.WriteAllText(file, Json().Serialize(timing));
                Check(LocalAudioTranscription.ReadContextTiming(audio) == null, "unstructured empty result is not no speech");
                File.WriteAllText(file, "broken-json"); bool failed = false;
                try { LocalAudioTranscription.ReadContextTiming(audio); } catch (ArgumentException) { failed = true; }
                Check(failed, "malformed worker result not swallowed as silence");
            } finally { Store.Root = previous; if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
        public static bool RunSelfTests() { RunParserDiagnosticsTests(); ContextTimingTests(); return RunAsync().GetAwaiter().GetResult(); }
        public static int Main() { try { RunSelfTests(); Console.WriteLine("Creative analysis offline tests: PASS"); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
        static async Task<bool> RunAsync() {
            var api = new FakeApi();
            using (var http = new HttpClient(api)) using (var client = new CreativeAnalysisClient("fixture-secret", httpClient: http)) {
                Check(api.Calls == 0, "no requests on construction");
                var input = new CreativeAnalysisInput { SourceTitle = "Useful Cooking Techniques", OriginalSpeechWithTimestamps = "[0–1] Речь", Frames = new List<CreativeVisionFrame> {
                    new CreativeVisionFrame { Seconds = 0, DataUrl = "data:image/png;base64,AQID" },
                    new CreativeVisionFrame { Seconds = 1, DataUrl = "data:image/png;base64,AQID" },
                    new CreativeVisionFrame { Seconds = 3, DataUrl = "data:image/png;base64,AQID" }
                }, CommentImages = new List<string> { "data:image/png;base64,BA==", "data:image/png;base64,BQ==", "data:image/png;base64,Bg==" } };
                api.Reply = async (request, ct) => {
                    Check(request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri == "https://openrouter.ai/api/v1/chat/completions", "official endpoint");
                    Check(request.Headers.Authorization.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "fixture-secret", "key only in header");
                    string raw = await request.Content.ReadAsStringAsync();
                    Check(!raw.Contains("fixture-secret"), "key not in body");
                    var body = Json().Deserialize<Dictionary<string, object>>(raw);
                    Check((string)body["model"] == "google/gemini-2.5-flash" && !(bool)body["stream"], "model and nonstreaming");
                    Check((bool)((Dictionary<string, object>)body["provider"])["require_parameters"], "no silently unsupported schema routing");
                    var format = (Dictionary<string, object>)body["response_format"];
                    var schema = (Dictionary<string, object>)format["json_schema"];
                    Check((string)format["type"] == "json_schema" && (bool)schema["strict"], "strict schema");
                    var rootSchema = (Dictionary<string, object>)schema["schema"];
                    Check(!(bool)rootSchema["additionalProperties"], "closed schema");
                    var introSchema = (Dictionary<string, object>)((Dictionary<string, object>)rootSchema["properties"])["intro"];
                    var spokenSchema = (Dictionary<string, object>)((Dictionary<string, object>)introSchema["properties"])["spoken_text"];
                    Check(Convert.ToInt32(spokenSchema["maxLength"]) == 44, "generated intro constrained in provider schema");
                    var messages = ((System.Collections.IEnumerable)body["messages"]).Cast<object>().ToArray();
                    string system = (string)((Dictionary<string, object>)messages[0])["content"];
                    Check(system.Contains("ОТДЕЛЬНОГО") && system.Contains("НЕ отменяет") && system.Contains("финальное заключение") && system.Contains("недоверенный"), "intro and trust boundaries");
                    Check(system.Contains("44 символов") && system.Contains("3 секунд"), "system prompt targets short intro");
                    var parts = ((System.Collections.IEnumerable)((Dictionary<string, object>)messages[1])["content"]).Cast<object>().ToArray();
                    var context = Json().Deserialize<Dictionary<string, object>>((string)((Dictionary<string, object>)parts[0])["text"]);
                    Check((string)context["optional_title"] == input.SourceTitle, "original title supplied as additional context");
                    Check(system.Contains("Обычное наблюдаемое действие") && system.Contains("не доказательство") && system.Contains("нельзя определить даже действие"), "visual actions suffice without inventing unsupported context");
                    Check(((string)context["target_brevity"]).Contains("44 символов") && ((string)context["target_brevity"]).Contains("3 секунд"), "request brevity target");
                    var images = parts.Cast<Dictionary<string, object>>().Where(p => (string)p["type"] == "image_url").ToArray();
                    Check(images.Length == 6, "three frames and three screenshots");
                    Check((string)((Dictionary<string, object>)images[3]["image_url"])["url"] == input.CommentImages[0] && (string)((Dictionary<string, object>)images[5]["image_url"])["url"] == input.CommentImages[2], "screen order");
                    Check(raw.Contains("original_speech_with_timestamps") && raw.Contains("optional_title"), "separate context data fields");
                    return Reply(Envelope(Payload()));
                };
                var result = await client.AnalyzeAsync(input, "google/gemini-2.5-flash", CancellationToken.None);
                Check(result.intro.spoken_text == "Короткая завязка." && result.comments[2].text == "Комментарий 3", "structured response");
                string boundaryIntro = new string('я', 21) + " " + new string('я', 22);
                var boundary = CreativeAnalysisClient.ParseResponse(Envelope(Payload().Replace("Короткая завязка.", boundaryIntro)));
                Check(boundary.intro.spoken_text == boundaryIntro && boundaryIntro.Length == 44, "44 generated characters including spaces accepted unchanged");
                var project = Project();
                CreativePreparation.ApplyAnalysis(project, result);
                Check(project.IntroText == "Короткая завязка." && project.Comments[0].Text == "Комментарий 1" && project.IntroAudio == "" && project.Comments[0].AudioPath == "", "atomic application invalidates audio");
                project.IntroText = new string('я', 60); project.Comments[1].Text = "Ручная правка";
                CreativePreparation.ApplyAnalysis(project, CreativeAnalysisClient.ParseResponse(Envelope(Payload("insufficient_context", 2))));
                Check(project.IntroText == new string('я', 60) && project.Comments[1].Text == "Ручная правка", "existing long manual texts survive insufficient OCR/context unchanged");
                int callsBeforeLength = api.Calls;
                string completeOcr = "Полный длинный комментарий без сокращений. " + new string('я', 160);
                api.Reply = async (request, ct) => {
                    if (api.Calls == callsBeforeLength + 1)
                        return Reply(Envelope(Payload().Replace("Короткая завязка.", boundaryIntro + "!!").Replace("Комментарий 3", completeOcr)));
                    string rewrite = await request.Content.ReadAsStringAsync();
                    var body = Json().Deserialize<Dictionary<string, object>>(rewrite);
                    var messages = ((System.Collections.IEnumerable)body["messages"]).Cast<Dictionary<string, object>>().ToArray();
                    Check(messages.All(m => m["content"] is string) && !rewrite.Contains("image_url") && !rewrite.Contains(completeOcr), "targeted rewrite is text-only without comment OCR");
                    Check(rewrite.Contains("Исходная надпись") && rewrite.Contains("generated_intro") && rewrite.Contains("maxLength"), "rewrite retains original context and length schema");
                    return Reply(Envelope(Json().Serialize(new { spoken_text = "Новая короткая завязка." })));
                };
                var shortened = await client.AnalyzeAsync(input, "google/gemini-2.5-flash", CancellationToken.None);
                Check(api.Calls == callsBeforeLength + 2 && shortened.intro.spoken_text == "Новая короткая завязка." && shortened.comments[2].text == completeOcr && shortened.intro.source_quote == "Исходная надпись", "46 generated characters rewritten once; full OCR and evidence unchanged");
                CreativePreparation.ApplyAnalysis(project, shortened);
                Check(project.IntroText == new string('я', 60), "manual long intro untouched by generated rewrite");
                foreach (string invalidRewrite in new[] { Envelope(Json().Serialize(new { spoken_text = boundaryIntro + "!" })), Envelope("{}"), Envelope(Json().Serialize(new { spoken_text = "" })), Envelope("{}", "length") }) {
                    int before = api.Calls; bool failed = false;
                    api.Reply = (request, ct) => Task.FromResult(Reply(api.Calls == before + 1 ? Envelope(Payload().Replace("Короткая завязка.", boundaryIntro + "!")) : invalidRewrite));
                    try { await client.AnalyzeAsync(input, "google/gemini-2.5-flash", CancellationToken.None); }
                    catch (Exception ex) { failed = ex.Message.Contains("intro.rewrite"); }
                    Check(failed && api.Calls == before + 2, "invalid rewrite fails without third request or truncation");
                }
                {
                    int before = api.Calls; bool failed = false;
                    api.Reply = (request, ct) => Task.FromResult(api.Calls == before + 1 ? Reply(Envelope(Payload().Replace("Короткая завязка.", boundaryIntro + "!"))) : Reply("private", HttpStatusCode.Forbidden));
                    try { await client.AnalyzeAsync(input, "google/gemini-2.5-flash", CancellationToken.None); }
                    catch (Exception ex) { failed = ex.Message.Contains("HTTP 403"); }
                    Check(failed && api.Calls == before + 2, "rewrite HTTP failure propagates without retry");
                }
                {
                    int before = api.Calls; bool failed = false;
                    api.Reply = (request, ct) => Task.FromResult(Reply(Envelope(Payload().Replace("Короткая завязка.", boundaryIntro + "!").Replace("\"id\":2", "\"id\":1"))));
                    try { await client.AnalyzeAsync(input, "google/gemini-2.5-flash", CancellationToken.None); }
                    catch (Exception ex) { failed = ex.Message.Contains("comments[1].id.order"); }
                    Check(failed && api.Calls == before + 1, "invalid OCR response fails before intro rewrite");
                }
                using (var cancelRewrite = new CancellationTokenSource()) {
                    int before = api.Calls; bool cancelled = false;
                    api.Reply = (request, ct) => {
                        cancelRewrite.Cancel();
                        return Task.FromResult(Reply(Envelope(Payload().Replace("Короткая завязка.", boundaryIntro + "!"))));
                    };
                    try { await client.AnalyzeAsync(input, "google/gemini-2.5-flash", cancelRewrite.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && api.Calls == before + 1, "cancellation prevents additional paid rewrite");
                }
                var empty = Project();
                bool unreadableFailed = false;
                try { CreativePreparation.ApplyAnalysis(empty, CreativeAnalysisClient.ParseResponse(Envelope(Payload("ready", 2)))); } catch (Exception ex) { unreadableFailed = ex.Message.Contains("Комментарий 2"); }
                Check(unreadableFailed && empty.IntroText == "" && empty.Comments.All(c => c.Text == ""), "unreadable OCR does not partially apply");
                foreach (string bad in new[] { "PRIVATE fixture-secret", "{}", Envelope(Payload(), "length"), "{\"error\":{\"message\":\"fixture-secret\"}}", Envelope(Payload().Replace("\"id\":2", "\"id\":1")), Envelope(Payload().Replace("Комментарий 3", "")) }) {
                    api.Reply = (request, ct) => Task.FromResult(Reply(bad));
                    int before = api.Calls; bool failed = false;
                    try { await client.AnalyzeAsync(input, "google/gemini-2.5-flash", CancellationToken.None); }
                    catch (Exception ex) { failed = true; Check(!ex.ToString().Contains("fixture-secret"), "safe parser error"); }
                    Check(failed && api.Calls == before + 1, "invalid/incomplete/duplicate response rejected without retry");
                }
                foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.PaymentRequired, HttpStatusCode.Forbidden, (HttpStatusCode)429, HttpStatusCode.Redirect }) {
                    api.Reply = (request, ct) => Task.FromResult(Reply("PRIVATE fixture-secret", code));
                    int before = api.Calls; bool failed = false;
                    try { await client.AnalyzeAsync(input, "google/gemini-2.5-flash", CancellationToken.None); }
                    catch (Exception ex) { failed = ex.Message.Contains("HTTP " + (int)code); Check(!ex.ToString().Contains("fixture-secret"), "safe HTTP error"); }
                    Check(failed && api.Calls == before + 1, "HTTP error no retries");
                }
                using (var cancel = new CancellationTokenSource()) {
                    cancel.Cancel(); int before = api.Calls; bool cancelled = false;
                    try { await client.AnalyzeAsync(input, "google/gemini-2.5-flash", cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && api.Calls == before, "cancellation before paid call");
                }
            }
            await SpeechTests();
            return true;
        }
        static async Task SpeechTests() {
            string work = Path.Combine(Path.GetTempPath(), "VideoBatch-CreativeAnalysisTests-" + Guid.NewGuid().ToString("N"));
            var api = new FakeApi();
            try {
                var project = Project(); project.IntroText = "Ручное вступление"; project.Preset.AutoIntroLanguage = false;
                for (int i = 0; i < 3; i++) project.Comments[i].Text = "Ручной комментарий " + (i + 1);
                project.Preset.Speech.VoiceId = "test-voice"; project.Preset.Speech.Speed = 1.1;
                string[] texts = new[] { project.IntroText }.Concat(project.Comments.Select(c => c.Text)).ToArray();
                bool introMeasured = false;
                // Synthetic API bytes are intentionally not valid MP3. Inject only the measurement
                // boundary; these contract tests do not claim to verify ffprobe or provider audio.
                Func<string, CancellationToken, Task<double>> shortIntro = (path, ct) => {
                    Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "duration checker receives saved intro audio");
                    introMeasured = true;
                    return Task.FromResult(3d);
                };
                api.Reply = async (request, ct) => {
                    Check(api.Calls == 1 || introMeasured, "intro duration approved before comment request");
                    Check(request.RequestUri.AbsoluteUri == "https://api.elevenlabs.io/v1/text-to-speech/test-voice/with-timestamps?output_format=mp3_44100_128", "existing ElevenLabs timed endpoint");
                    var body = Json().Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
                    Check((string)body["text"] == texts[api.Calls - 1] && (api.Calls==1?body.ContainsKey("language_code")&&(string)body["language_code"]=="ru":!body.ContainsKey("language_code")) && (string)body["model_id"] == "eleven_multilingual_v2", "manual text, order, language, model");
                    Check(Convert.ToDouble(((Dictionary<string, object>)body["voice_settings"])["speed"]) == 1.1, "preset voice settings");
                    return Reply("{\"audio_base64\":\"AQID\",\"alignment\":{\"characters\":[\"т\"],\"character_start_times_seconds\":[0],\"character_end_times_seconds\":[0.5]}}");
                };
                using (var http = new HttpClient(api)) {
                    await CreativePreparation.GenerateSpeechCoreAsync(project, work, "fixture-secret", "", CancellationToken.None, null, http, shortIntro);
                    Check(api.Calls == 4 && File.Exists(project.IntroAudio) && project.Comments.All(c => File.Exists(c.AudioPath) && File.Exists(c.AudioPath + ".timing.json")), "four distinct audio files and timings");
                    Check(Path.GetFileName(project.IntroAudio) == "intro.mp3" && Path.GetFileName(project.Comments[2].AudioPath) == "comment-3.mp3", "stable paths");
                    project.IntroText = "Изменённый текст";
                    api.Reply = (request, ct) => Task.FromResult(Reply("fixture-secret", HttpStatusCode.Forbidden));
                    bool failed = false;
                    try { await CreativePreparation.GenerateSpeechCoreAsync(project, work, "fixture-secret", "", CancellationToken.None, null, http, shortIntro); } catch (Exception ex) { failed = ex.Message.Contains("HTTP 403"); }
                    Check(failed && api.Calls == 5 && project.IntroAudio == "" && project.Comments.All(c => c.AudioPath == ""), "TTS failure clears all stale bindings and does not retry");
                    Check(!File.Exists(Path.Combine(work, "creative-audio", "intro.mp3")), "failed slot has no stale generated file");
                    int start = api.Calls;
                    api.Reply = (request, ct) => Task.FromResult(api.Calls == start + 2 ? Reply("private", HttpStatusCode.Forbidden) :
                        Reply("{\"audio_base64\":\"AQID\",\"alignment\":{\"characters\":[\"т\"],\"character_start_times_seconds\":[0],\"character_end_times_seconds\":[0.5]}}"));
                    failed = false;
                    try { await CreativePreparation.GenerateSpeechCoreAsync(project, work, "fixture-secret", "", CancellationToken.None, null, http, shortIntro); } catch (Exception ex) { failed = ex.Message.Contains("HTTP 403"); }
                    Check(failed && api.Calls == start + 2 && project.IntroAudio == "" && project.Comments.All(c => c.AudioPath == ""), "late TTS failure unbinds even completed slots");

                    // Duration failures must stop after the single paid intro, without clipping,
                    // retrying, changing existing text, or exposing partial batch audio bindings.
                    project.IntroText = new string('я', 60);
                    string manualIntro = project.IntroText;
                    api.Reply = async (request, ct) => {
                        var body = Json().Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
                        Check((string)body["text"] == manualIntro, "long existing intro sent unchanged");
                        return Reply("{\"audio_base64\":\"AQID\",\"alignment\":{\"characters\":[\"т\"],\"character_start_times_seconds\":[0],\"character_end_times_seconds\":[0.5]}}");
                    };
                    start = api.Calls; int measured = 0; string measuredPath = null;
                    failed = false;
                    try {
                        await CreativePreparation.GenerateSpeechCoreAsync(project, work, "fixture-secret", "", CancellationToken.None, null, http,
                            (path, ct) => {
                                measured++; measuredPath = path;
                                Check(api.Calls == start + 1 && project.Comments.All(c => c.AudioPath == ""), "measure before any comment billing");
                                return Task.FromResult(3.125);
                            });
                    } catch (Exception ex) {
                        failed = ex.Message.Contains("3.125") && ex.Message.Contains("3 с") && ex.Message.Contains("Сократите");
                    }
                    Check(failed && measured == 1 && api.Calls == start + 1 && project.IntroText == manualIntro && project.IntroAudio == "" && project.Comments.All(c => c.AudioPath == ""), "long audio reports actual duration and stops without retry or text overwrite");
                    Check(File.ReadAllBytes(measuredPath).SequenceEqual(new byte[] { 1, 2, 3 }), "rejected intro not truncated");

                    foreach (double invalid in new[] { 0d, double.NaN, double.PositiveInfinity }) {
                        start = api.Calls; failed = false;
                        try { await CreativePreparation.GenerateSpeechCoreAsync(project, work, "fixture-secret", "", CancellationToken.None, null, http, (path, ct) => Task.FromResult(invalid)); }
                        catch (Exception ex) { failed = ex.Message.Contains("Не удалось определить длительность"); }
                        Check(failed && api.Calls == start + 1 && project.IntroAudio == "" && project.Comments.All(c => c.AudioPath == ""), "invalid duration cannot approve comment billing");
                    }
                    start = api.Calls; failed = false;
                    try {
                        await CreativePreparation.GenerateSpeechCoreAsync(project, work, "fixture-secret", "", CancellationToken.None, null, http,
                            (path, ct) => { throw new Exception("fixture probe failure"); });
                    } catch (Exception ex) { failed = ex.Message == "fixture probe failure"; }
                    Check(failed && api.Calls == start + 1 && project.IntroAudio == "" && project.Comments.All(c => c.AudioPath == ""), "probe error visible and prevents comment billing");
                    using (var cancel = new CancellationTokenSource()) {
                        start = api.Calls; bool cancelled = false;
                        try {
                            await CreativePreparation.GenerateSpeechCoreAsync(project, work, "fixture-secret", "", cancel.Token, null, http,
                                (path, ct) => { cancel.Cancel(); return Task.FromResult(1d); });
                        } catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled && api.Calls == start + 1 && project.IntroAudio == "" && project.Comments.All(c => c.AudioPath == ""), "cancellation after probe prevents comment billing");
                    }
                }
            } finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
        }
    }
}
