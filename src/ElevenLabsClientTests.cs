using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class ElevenLabsClientTests {
        static void Check(bool value, string name) { if (!value) throw new Exception("ElevenLabs client: " + name); }
        sealed class FakeApi : HttpMessageHandler {
            public readonly List<string> Paths = new List<string>();
            public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                ct.ThrowIfCancellationRequested();
                Check(request.RequestUri.Host == "api.elevenlabs.io" && request.RequestUri.Scheme == "https", "official HTTPS host");
                Check(request.Headers.GetValues("xi-api-key").Single() == "fixture-secret", "key header");
                Paths.Add(request.RequestUri.PathAndQuery);
                return Reply(request, ct);
            }
        }
        static HttpResponseMessage Json(string body) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        public static bool RunSelfTests() => RunAsync().GetAwaiter().GetResult();
        static async Task<bool> RunAsync() {
            var api = new FakeApi();
            using (var http = new HttpClient(api)) using (var client = new ElevenLabsClient("fixture-secret", httpClient: http)) {
                Check(api.Paths.Count == 0, "construction does not call API");
                api.Reply = (request, ct) => Task.FromResult(Json(api.Paths.Count == 1
                    ? "{\"voices\":[{\"voice_id\":\"m1\",\"name\":\"Male One\",\"labels\":{\"gender\":\"male\"}},{\"voice_id\":\"f1\",\"labels\":{\"gender\":\"female\"}},{\"voice_id\":\"unknown\"}],\"has_more\":true,\"next_page_token\":\"a +&\"}"
                    : "{\"voices\":[{\"voice_id\":\"m1\",\"labels\":{\"gender\":\"male\"}},{\"voice_id\":\"m2\",\"labels\":{\"gender\":\"Male\"}}],\"has_more\":false}"));
                var voices = await client.ListMaleVoicesAsync(CancellationToken.None);
                Check(voices.Count == 2 && voices[0].ToString() == "Male One" && voices[1].voice_id == "m2", "male labels only and duplicate IDs removed");
                Check(api.Paths[0].Contains("gender=male") && api.Paths[0].Contains("page_size=100") && api.Paths[1].Contains("next_page_token=a%20%2B%26"), "pagination token escaped");
                api.Reply = (request, ct) => Task.FromResult(Json("{\"stability\":0.2,\"similarity_boost\":0.8,\"style\":0.3,\"speed\":1.1,\"use_speaker_boost\":false}"));
                var settings = await client.GetVoiceSettingsAsync("m1", CancellationToken.None);
                Check(api.Paths.Last() == "/v1/voices/m1/settings" && settings.stability == .2 && settings.use_speaker_boost == false, "read voice settings");
                string text = "Привет, \"мир\"!\nHello.";
                api.Reply = async (request, ct) => {
                    var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
                    var wire = (Dictionary<string, object>)body["voice_settings"];
                    Check((string)body["text"] == text && (string)body["model_id"] == "eleven_multilingual_v2", "text preserved and model fixed");
                    Check(Convert.ToDouble(wire["speed"]) == 1.1 && Convert.ToDouble(wire["stability"]) == .2 && !(bool)wire["use_speaker_boost"], "slider settings sent per request");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
                };
                var audio = await client.GenerateAsync(text, "m1", settings, false, CancellationToken.None);
                Check(audio.Audio.SequenceEqual(new byte[] { 1, 2, 3 }) && audio.Text == text && audio.SubtitleAlignment == null, "normal MP3 result");
                Check(api.Paths.Last() == "/v1/text-to-speech/m1?output_format=mp3_44100_128", "normal endpoint");
                api.Reply = (request, ct) => Task.FromResult(Json("{\"audio_base64\":\"AQID\",\"alignment\":{\"characters\":[\"4\"],\"character_start_times_seconds\":[0],\"character_end_times_seconds\":[0.5]},\"normalized_alignment\":{\"characters\":[\"f\",\"o\",\"u\",\"r\"],\"character_start_times_seconds\":[0,0.1,0.2,0.3],\"character_end_times_seconds\":[0.1,0.2,0.3,0.5]}}"));
                var timed = await client.GenerateAsync("4", "m1", null, true, CancellationToken.None);
                Check(api.Paths.Last() == "/v1/text-to-speech/m1/with-timestamps?output_format=mp3_44100_128", "timestamp endpoint");
                Check(timed.Alignment.characters.Single() == "4" && timed.SubtitleAlignment.characters.Length == 4 && timed.Text == "4", "both alignments and original text retained");
                var captions = WarmupSubtitles.Cues(timed.SubtitleAlignment, 1, 3);
                Check(captions.Count == 1 && captions[0].Text == "four" && captions[0].End == .5, "existing editable caption mechanism reused");
                foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, (HttpStatusCode)429, HttpStatusCode.Redirect }) {
                    api.Reply = (request, ct) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("fixture-secret PRIVATE TEXT") });
                    int before = api.Paths.Count;
                    try { await client.GenerateAsync(text, "m1", settings, false, CancellationToken.None); throw new Exception("expected HTTP failure"); }
                    catch (Exception ex) { Check(ex.Message.Contains("HTTP " + (int)status) && !ex.ToString().Contains("fixture-secret") && !ex.ToString().Contains("PRIVATE TEXT"), "safe visible HTTP failure"); }
                    Check(api.Paths.Count == before + 1, "no retry on HTTP failure");
                }
                foreach (string invalid in new[] { "{\"audio_base64\":\"AQID\"}", "{\"audio_base64\":\"!\"}", "{\"audio_base64\":\"\"}", "fixture-secret INVALID JSON" }) {
                    api.Reply = (request, ct) => Task.FromResult(Json(invalid));
                    int before = api.Paths.Count;
                    bool failed = false;
                    try { await client.GenerateAsync(text, "m1", null, true, CancellationToken.None); }
                    catch (Exception ex) { failed = true; Check(!ex.ToString().Contains("fixture-secret"), "parser error does not expose response"); }
                    Check(failed && api.Paths.Count == before + 1, "incomplete/malformed result rejected without retry");
                }
                api.Reply = (request, ct) => Task.FromResult(Json("{\"voices\":[],\"has_more\":true}"));
                bool paginationFailed = false;
                try { await client.ListMaleVoicesAsync(CancellationToken.None); } catch (Exception) { paginationFailed = true; }
                Check(paginationFailed, "incomplete pagination not returned as success");
                using (var cancel = new CancellationTokenSource()) {
                    cancel.Cancel(); int before = api.Paths.Count; bool cancelled = false;
                    try { await client.GenerateAsync(text, "m1", settings, false, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && api.Paths.Count == before, "cancelled request does not generate");
                }
            }
            return true;
        }
    }
}
