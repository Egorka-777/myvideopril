using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VideoBatch {
    public static class WarmupVideoTests {
        static void Check(bool value, string name) { if (!value) throw new Exception("Warmup video: " + name); }
        static JavaScriptSerializer Json() => new JavaScriptSerializer();
        static string Reply(string joke, string finish = "stop") => Json().Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content = Json().Serialize(new { text = joke }) } } } });
        sealed class FakeApi : HttpMessageHandler {
            public byte[] Short, Long; public int TextCalls, SpeechCalls, VoiceCalls, LongCount;
            public bool AlwaysLong, FailAuth, CancelTts; public CancellationTokenSource Cancel;
            public string Market = "RU";
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                if (request.RequestUri.Host == "openrouter.ai") {
                    TextCalls++; string raw = await request.Content.ReadAsStringAsync();
                    Check(request.Headers.Authorization.Parameter == "fixture-router-key", "OpenRouter auth");
                    var data = Json().Deserialize<Dictionary<string, object>>(raw);
                    var message = (Dictionary<string, object>)((object[])data["messages"])[0];
                    string prompt = (string)message["content"];
                    Check(prompt.Contains(Market == "RU" ? "Russian" : "English") && prompt.Contains("6–7") && prompt.Contains("10 seconds"), "language and speech constraint");
                    if (TextCalls > 1 && SpeechCalls <= LongCount) Check(prompt.Contains("Rewrite substantially shorter"), "long speech requests new complete joke");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply((Market == "RU" ? "Короткий анекдот, кавычки: \"пример\". Номер " : "A short complete joke. Number ") + TextCalls), Encoding.UTF8, "application/json") };
                }
                Check(request.RequestUri.Host == "api.elevenlabs.io" && request.Headers.GetValues("xi-api-key").Single() == "fixture-eleven-key", "ElevenLabs auth stays on official host");
                if (request.Method == HttpMethod.Get) {
                    VoiceCalls++; Check(request.RequestUri.AbsolutePath == "/v2/voices", "current voices endpoint");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"voices\":[{\"voice_id\":\"fixture-voice\"}]}") };
                }
                SpeechCalls++;
                var speech = Json().Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
                Check((string)speech["model_id"] == "eleven_multilingual_v2" && ((string)speech["text"]).Contains("Number") == (Market == "EN"), "unmodified parsed joke sent to TTS");
                if (FailAuth) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("SECRET fixture-eleven-key must never enter logs") };
                if (CancelTts) { Cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
                byte[] bytes = AlwaysLong || SpeechCalls <= LongCount ? Long : Short;
                if (request.RequestUri.AbsolutePath.EndsWith("/with-timestamps")) {
                    string spoken = (string)speech["text"];
                    var alignment = Alignment(spoken, .25, 1.5);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json().Serialize(new { audio_base64 = Convert.ToBase64String(bytes), normalized_alignment = alignment })) };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
        }
        public static bool RunIntegrationTests(string ffmpeg, string ffprobe) => RunIntegrationAsync(ffmpeg, ffprobe).GetAwaiter().GetResult();
        static async Task RunTool(string ffmpeg, params string[] args) => await Core.Tool(ffmpeg, args, CancellationToken.None, null, 45);
        static async Task<bool> RunIntegrationAsync(string ffmpeg, string ffprobe) {
            string temp = Path.Combine(Path.GetTempPath(), "vb-warmup-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try {
                // Deliberately non-frame-aligned narration. The background video has its own unwanted audio.
                string photo = Path.Combine(temp, "фон с пробелом.png"), video = Path.Combine(temp, "фон-видео.mp4"), shortAudio = Path.Combine(temp, "short.mp3"), longAudio = Path.Combine(temp, "long.mp3");
                await RunTool(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "color=red:s=160x90", "-frames:v", "1", photo);
                await RunTool(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=24:d=1", "-f", "lavfi", "-i", "sine=frequency=2000:duration=1", "-c:v", "libx264", "-c:a", "aac", video);
                await RunTool(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2.173", "-c:a", "libmp3lame", shortAudio);
                await RunTool(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=10.17", "-c:a", "libmp3lame", longAudio);
                Check(WarmupVideoEngine.DefaultOutputFolder.EndsWith(Path.Combine("VideoBatch", "Прогрев")), "named output folder");
                Check(WarmupVideoEngine.ParseText(Reply("Кавычки \"да\", перенос\nстроки")) == "Кавычки \"да\", перенос\nстроки", "JSON escaping preserved");
                bool rejected = false; try { WarmupVideoEngine.ParseText(Reply("incomplete", "length")); } catch { rejected = true; } Check(rejected, "truncated AI answer rejected before paid speech");
                foreach (string market in new[] { "RU", "EN" }) {
                    var api = new FakeApi { Short = File.ReadAllBytes(shortAudio), Long = File.ReadAllBytes(longAudio), LongCount = market == "RU" ? 1 : 0, Market = market };
                    string output = Path.Combine(temp, "результаты " + market); Directory.CreateDirectory(output);
                    string existing = Path.Combine(output, "warmup-RU-001.mp4"); File.WriteAllText(existing, "keep existing output");
                    var job = new WarmupVideoJob { Market = market, Prompt = "Тестовая тема", Count = market == "RU" ? 2 : 1, BackgroundFile = market == "RU" ? photo : video, Subtitles = market == "EN" ? new WarmupSubtitleStyle { Position = 72 } : null, OutputFolder = output, OpenRouterKey = "fixture-router-key", ElevenLabsKey = "fixture-eleven-key" };
                    using (var client = new HttpClient(api)) {
                        var files = await WarmupVideoEngine.GenerateAsync(job, _ => { }, CancellationToken.None, client, ffmpeg, ffprobe);
                        Check(files.Count == job.Count && files.Distinct().Count() == job.Count && files.All(File.Exists), "requested number, distinct names and output files");
                        Check(api.SpeechCalls == job.Count + api.LongCount && api.TextCalls == api.SpeechCalls, "only long narration retried");
                        Check(File.ReadAllText(existing) == "keep existing output", "old files are not overwritten");
                        Check(!Directory.GetFiles(output, ".processing-*").Any(), "no partial results");
                        foreach (var file in files) {
                            await WarmupVideoEngine.VerifyOutputAsync(ffprobe, file, 2.173, CancellationToken.None);
                            // Compare decoded output to generated source: background tone must not leak into final audio.
                            string pcm = Path.Combine(temp, "decoded.wav");
                            await RunTool(ffmpeg, "-v", "error", "-y", "-i", file, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", pcm);
                            Check(Math.Abs(await WarmupVideoEngine.AudioDurationAsync(ffprobe, pcm, CancellationToken.None) - 2.173) < .03, "full speech, no artificial half-second padding");
                            Check(Math.Abs(ToneFrequency(pcm) - 440) < 5, "only narration, not background audio");
                            if (market == "EN") {
                                string frame = Path.Combine(temp, "caption-frame.png");
                                await RunTool(ffmpeg, "-v", "error", "-y", "-ss", "0.5", "-i", file, "-frames:v", "1", frame);
                                using (var image = new Bitmap(frame)) Check(image.GetPixel(540, 1382).GetBrightness() < .3, "caption plate actually burned into final MP4");
                            }
                        }
                    }
                }
                var timed = WarmupSubtitles.Cues(Alignment("One two three four.", .3, 1.2), 2, 2);
                Check(timed.Count == 2 && timed[0].Text == "One two" && timed[1].Text == "three four." && timed[0].Start == .3 && timed[0].End <= timed[1].Start, "word grouping uses provider timing without overlap");
                bool noTiming = false; try { WarmupSubtitles.Cues(null, 2, 3); } catch { noTiming = true; } Check(noTiming, "missing timings cannot silently invent synchronization");
                foreach (string plate in new[] { "none", "solid", "rounded" }) {
                    using (var bitmap = WarmupSubtitles.Paint("Проверка English", new WarmupSubtitleStyle { Plate = plate, PlateColor = "#FF0000", TextColor = "#00FF00", Opacity = 100 })) {
                        Check(bitmap.GetPixel(0, 0).A == 0, "caption canvas is transparent");
                        Check(Enumerable.Range(1200, 350).Any(y => bitmap.GetPixel(540, y).A > 0), "subtitle centered in configured position");
                    }
                }
                // Boundary near 10 s: container rounding is allowed, output is never longer than 10 s.
                string boundaryWav = Path.Combine(temp, "boundary.wav"), boundary = Path.Combine(temp, "boundary.mp4");
                await RunTool(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "sine=duration=9.999", "-ar", "48000", boundaryWav);
                double boundarySeconds = await WarmupVideoEngine.AudioDurationAsync(ffprobe, boundaryWav, CancellationToken.None);
                await Core.Tool(ffmpeg, WarmupVideoEngine.RenderArguments(video, boundaryWav, boundary, boundarySeconds), CancellationToken.None, null, 90);
                await WarmupVideoEngine.VerifyOutputAsync(ffprobe, boundary, boundarySeconds, CancellationToken.None);
                foreach (string failure in new[] { "long", "auth", "cancel", "bad-background" }) {
                    string output = Path.Combine(temp, failure); var lines = new List<string>();
                    using (var cancel = new CancellationTokenSource()) {
                        var api = new FakeApi { Short = File.ReadAllBytes(shortAudio), Long = File.ReadAllBytes(longAudio), AlwaysLong = failure == "long", FailAuth = failure == "auth", CancelTts = failure == "cancel", Cancel = cancel };
                        using (var client = new HttpClient(api)) {
                            string bad = Path.Combine(temp, "broken.png"); File.WriteAllText(bad, "not an image");
                            var job = new WarmupVideoJob { Prompt = "Тема", BackgroundFile = failure == "bad-background" ? bad : photo, OutputFolder = output, OpenRouterKey = "fixture-router-key", ElevenLabsKey = "fixture-eleven-key" };
                            bool failed = false;
                            try { await WarmupVideoEngine.GenerateAsync(job, lines.Add, cancel.Token, client, ffmpeg, ffprobe); }
                            catch (OperationCanceledException) { Check(failure == "cancel", "only explicit cancellation"); failed = true; }
                            catch (Exception e) { Check(failure != "cancel", "cancellation remains cancellation"); Check(!e.Message.Contains("SECRET") && !e.Message.Contains("fixture-eleven-key"), "provider credentials/bodies not logged"); failed = true; }
                            Check(failed && !Directory.GetFiles(output, "*.mp4").Any(), "failed job has no misleading finished file");
                            Check(api.SpeechCalls == (failure == "long" ? 3 : failure == "bad-background" ? 0 : 1), "bounded attempts, preflight before credit use");
                        }
                    }
                }
                Console.WriteLine("Warmup integration PASS: real photo/video render, audio clock, near-10s boundary, RU/EN payloads, shorten retry, no overwrite, invalid source, API errors and cancellation. No paid/live APIs called."); return true;
            } finally { try { Directory.Delete(temp, true); } catch { } }
        }
        static WarmupAlignment Alignment(string text, double start, double length) => new WarmupAlignment {
            characters = text.Select(c => c.ToString()).ToArray(),
            character_start_times_seconds = Enumerable.Range(0, text.Length).Select(i => start + length * i / text.Length).ToArray(),
            character_end_times_seconds = Enumerable.Range(0, text.Length).Select(i => start + length * (i + 1) / text.Length).ToArray()
        };
        static double ToneFrequency(string wav) {
            using (var r = new BinaryReader(File.OpenRead(wav))) {
                r.BaseStream.Position = 12;
                while (r.BaseStream.Position + 8 <= r.BaseStream.Length) {
                    string kind = Encoding.ASCII.GetString(r.ReadBytes(4)); int length = r.ReadInt32(); long next = r.BaseStream.Position + length + (length % 2);
                    if (kind == "data") {
                        int count = length / 2, crossings = 0; short last = r.ReadInt16();
                        for (int i = 1; i < count; i++) { short sample = r.ReadInt16(); if (last <= 0 && sample > 0) crossings++; last = sample; }
                        return crossings / (count / 48000.0);
                    }
                    r.BaseStream.Position = next;
                }
            }
            throw new Exception("PCM data missing");
        }
        static IEnumerable<Control> All(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var item in All(child)) yield return item; } }
        public static bool RunUiTests(string folder) {
            string old = Store.Root, temp = Path.Combine(Path.GetTempPath(), "vb-warmup-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp); Directory.CreateDirectory(folder);
            try {
                Store.Root = temp;
                string photo = Path.Combine(temp, "фон.png");
                using (var bitmap = new Bitmap(180, 320)) { using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.FromArgb(50, 75, 90)); g.FillEllipse(Brushes.DarkGoldenrod, 35, 70, 110, 110); } bitmap.Save(photo); }
                var prefs = new Preferences { WarmupVideoBackgroundFileRu = photo, WarmupVideoBackgroundFileEn = photo, WarmupVideoCountRu = 3, WarmupVideoCountEn = 2, WarmupVideoPromptRu = "Короткий смешной анекдот про трейдеров, без мата.", WarmupVideoPromptEn = "Short clean jokes about traders." };
                using (var form = new Form { ClientSize = new Size(936, 700), Font = Theme.FontBody }) using (var panel = new WarmupVideoPanel(prefs)) {
                    form.Controls.Add(panel); form.Show(); Application.DoEvents();
                    Check(!All(panel).Any(c => c.Text == "Макс. длина ролика" || c.Text == "Сохранить"), "automatic duration/save have no duplicate controls");
                    var prompt = All(panel).OfType<TextBox>().Single(c => c.Name == "warmup-prompt");
                    prompt.Text = "Новая тема";
                    var save = typeof(WarmupVideoPanel).GetMethod("SaveFields", BindingFlags.Instance | BindingFlags.NonPublic); save.Invoke(panel, null);
                    All(panel).OfType<Button>().Single(b => b.Text == "Настроить").PerformClick();
                    All(panel).OfType<NumericUpDown>().Single(c => c.Name == "warmup-subtitle-size").Value = 64;
                    save.Invoke(panel, null);
                    var loaded = Store.Load(); Check(loaded.WarmupVideoPromptRu == "Новая тема" && loaded.WarmupVideoCountRu == 3 && loaded.WarmupSubtitlesRu.FontSize == 64, "fields and subtitle editor persist");
                    All(panel).OfType<Button>().Single(b => b.Text == "EN").PerformClick(); Application.DoEvents();
                    Check(prompt.Text == prefs.WarmupVideoPromptEn && All(panel).OfType<NumericUpDown>().Single(c => c.Name == "warmup-subtitle-size").Value == 56, "market fields and styles remain separate");
                    // Collapse settings for the overview; capture expanded editor separately in the wide layout.
                    All(panel).OfType<Button>().Single(b => b.Text == "Настроить").PerformClick();
                    foreach (var size in new[] { new Size(820, 620), new Size(936, 700), new Size(1200, 800) }) {
                        form.ClientSize = size; Application.DoEvents();
                        foreach (var button in All(panel).OfType<Button>().Where(b => b.Visible)) {
                            Check(button.Left >= 0 && button.Top >= 0 && button.Right <= button.Parent.ClientSize.Width + 1 && button.Bottom <= button.Parent.ClientSize.Height + 1, "button clipped in parent: " + button.Text);
                            var origin = panel.PointToClient(button.PointToScreen(Point.Empty));
                            // Scrollable settings can extend below viewport; horizontal clipping is never allowed.
                            Check(origin.X >= 0 && origin.X + button.Width <= panel.Width, "horizontal button clipping: " + button.Text);
                        }
                        using (var bmp = new Bitmap(panel.Width, panel.Height)) { panel.DrawToBitmap(bmp, panel.ClientRectangle); bmp.Save(Path.Combine(folder, "warmup-" + size.Width + ".png")); }
                    }
                    All(panel).OfType<Button>().Single(b => b.Text == "Настроить").PerformClick(); Application.DoEvents();
                    // Scroll the inline editor into view, leaving the primary actions visible.
                    var editor = All(panel).OfType<NumericUpDown>().Single(c => c.Name == "warmup-subtitle-size");
                    var scroll = All(panel).OfType<Panel>().First(p => p.AutoScroll && p.Contains(editor)); scroll.ScrollControlIntoView(editor); Application.DoEvents();
                    using (var bmp = new Bitmap(panel.Width, panel.Height)) { panel.DrawToBitmap(bmp, panel.ClientRectangle); bmp.Save(Path.Combine(folder, "warmup-subtitles.png")); }
                    form.Close();
                }
                Console.WriteLine("Warmup UI PASS: compact/wide layouts, button bounds, independent markets, settings persistence."); return true;
            } finally { Store.Root = old; try { Directory.Delete(temp, true); } catch { } }
        }
    }
}
