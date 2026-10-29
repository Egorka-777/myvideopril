using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class ElevenLabsVoice {
        public string voice_id { get; set; }
        public string name { get; set; }
        public Dictionary<string, string> labels { get; set; }
        public override string ToString() => string.IsNullOrWhiteSpace(name) ? voice_id : name;
    }

    // API names intentionally match the documented JSON. Null leaves a setting to the provider.
    public sealed class ElevenLabsVoiceSettings {
        public double? stability { get; set; } = .5;
        public double? similarity_boost { get; set; } = .75;
        public double? style { get; set; } = 0;
        public double? speed { get; set; } = 1;
        public bool? use_speaker_boost { get; set; } = true;
    }

    public sealed class ElevenLabsSpeech {
        public byte[] Audio { get; internal set; }
        public string Text { get; internal set; }
        public WarmupAlignment Alignment { get; internal set; }
        public WarmupAlignment NormalizedAlignment { get; internal set; }
        // Both provider alignments are retained; existing caption grouping uses spoken/normalized text.
        public WarmupAlignment SubtitleAlignment => NormalizedAlignment ?? Alignment;
    }

    /// <summary>Official API only. Construction/listing/settings never synthesize speech.
    /// GenerateAsync makes exactly one potentially billable request, with no retry or fallback.
    /// An injected HttpClient is caller-owned and must disable automatic redirects.
    /// </summary>
    public sealed class ElevenLabsClient : IDisposable {
        const string Root = "https://api.elevenlabs.io";
        readonly HttpClient client;
        readonly string key;
        readonly bool ownsClient;

        public ElevenLabsClient(string apiKey, string proxyUrl = "", HttpClient httpClient = null) {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Укажите API-ключ ElevenLabs.", nameof(apiKey));
            key = apiKey.Trim();
            ownsClient = httpClient == null;
            client = httpClient ?? OutboundHttp.Create(proxyUrl, TimeSpan.FromMinutes(3), false);
        }

        public async Task<List<ElevenLabsVoice>> ListMaleVoicesAsync(CancellationToken ct) {
            var voices = new List<ElevenLabsVoice>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            string token = null;
            do {
                string url = Root + "/v2/voices?page_size=100&gender=male&include_total_count=false";
                if (token != null) url += "&next_page_token=" + Uri.EscapeDataString(token);
                using (var request = Request(HttpMethod.Get, url))
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false)) {
                    CheckHttp(response, "список голосов");
                    var page = Parse<VoicePage>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    ct.ThrowIfCancellationRequested();
                    if (page.voices == null) throw new Exception("ElevenLabs: в ответе отсутствует список голосов.");
                    foreach (var voice in page.voices) {
                        string gender;
                        if (voice != null && !string.IsNullOrWhiteSpace(voice.voice_id) && voice.labels != null &&
                            voice.labels.TryGetValue("gender", out gender) && string.Equals(gender, "male", StringComparison.OrdinalIgnoreCase) && ids.Add(voice.voice_id))
                            voices.Add(voice);
                    }
                    if (!page.has_more) break;
                    token = page.next_page_token;
                    if (string.IsNullOrWhiteSpace(token) || !tokens.Add(token))
                        throw new Exception("ElevenLabs: некорректная пагинация списка голосов; список получен не полностью.");
                }
            } while (true);
            return voices;
        }

        public async Task<ElevenLabsVoiceSettings> GetVoiceSettingsAsync(string voiceId, CancellationToken ct) {
            using (var request = Request(HttpMethod.Get, Root + "/v1/voices/" + VoicePath(voiceId) + "/settings"))
            using (var response = await client.SendAsync(request, ct).ConfigureAwait(false)) {
                CheckHttp(response, "настройки голоса");
                var settings = Parse<ElevenLabsVoiceSettings>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                ct.ThrowIfCancellationRequested();
                return settings;
            }
        }

        public async Task<ElevenLabsSpeech> GenerateAsync(string text, string voiceId, ElevenLabsVoiceSettings settings, bool withTimestamps, CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Введите текст для озвучки.", nameof(text));
            string url = Root + "/v1/text-to-speech/" + VoicePath(voiceId) +
                (withTimestamps ? "/with-timestamps" : "") + "?output_format=mp3_44100_128";
            var body = new Dictionary<string, object> { { "text", text }, { "model_id", "eleven_multilingual_v2" } };
            if (settings != null) body.Add("voice_settings", settings);
            using (var request = Request(HttpMethod.Post, url)) {
                request.Content = new StringContent(Json().Serialize(body), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false)) {
                    CheckHttp(response, "озвучка");
                    var result = new ElevenLabsSpeech { Text = text };
                    if (withTimestamps) {
                        var speech = Parse<TimedSpeech>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        try { result.Audio = Convert.FromBase64String(speech.audio_base64 ?? ""); }
                        catch (FormatException) { throw new Exception("ElevenLabs: некорректный формат аудио в ответе."); }
                        result.Alignment = speech.alignment;
                        result.NormalizedAlignment = speech.normalized_alignment;
                        if (result.SubtitleAlignment == null) throw new Exception("ElevenLabs не вернул запрошенные таймкоды субтитров. Автоматического повтора нет.");
                    } else result.Audio = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (result.Audio.Length == 0) throw new Exception("ElevenLabs вернул пустую озвучку. Автоматического повтора нет.");
                    return result;
                }
            }
        }

        HttpRequestMessage Request(HttpMethod method, string url) {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("xi-api-key", key);
            return request;
        }
        static string VoicePath(string voiceId) {
            if (string.IsNullOrWhiteSpace(voiceId)) throw new ArgumentException("Выберите голос ElevenLabs.", nameof(voiceId));
            return Uri.EscapeDataString(voiceId);
        }
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static T Parse<T>(string json) where T : class {
            T value;
            // Provider bodies can contain sensitive request data; never attach them or parser exceptions to UI errors.
            try { value = Json().Deserialize<T>(json); }
            catch (ArgumentException) { throw new Exception("ElevenLabs: некорректный JSON в ответе API."); }
            catch (InvalidOperationException) { throw new Exception("ElevenLabs: некорректный JSON в ответе API."); }
            if (value == null) throw new Exception("ElevenLabs: пустой ответ API.");
            return value;
        }
        static void CheckHttp(HttpResponseMessage response, string operation) {
            if (response.IsSuccessStatusCode) return;
            int code = (int)response.StatusCode;
            string hint = code >= 300 && code < 400 ? "API перенаправляет запрос; проверьте регион и HTTP(S)-прокси." :
                code == 401 ? "Проверьте API-ключ." : code == 403 ? "Проверьте права API-ключа и регион IP/прокси." :
                code == 402 ? "Проверьте баланс API." : code == 429 ? "Достигнут лимит API." : "Сервис отклонил запрос.";
            throw new Exception("ElevenLabs (" + operation + "): HTTP " + code + ". " + hint + " Автоматического повтора нет.");
        }
        public void Dispose() { if (ownsClient) client.Dispose(); }

        public sealed class VoicePage {
            public ElevenLabsVoice[] voices { get; set; }
            public bool has_more { get; set; }
            public string next_page_token { get; set; }
        }
        public sealed class TimedSpeech {
            public string audio_base64 { get; set; }
            public WarmupAlignment alignment { get; set; }
            public WarmupAlignment normalized_alignment { get; set; }
        }
    }
}
