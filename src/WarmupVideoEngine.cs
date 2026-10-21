using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class WarmupVideoJob {
        public string Market = "RU", Prompt = "", BackgroundFile = "", OutputFolder = "";
        public int Count = 1;
        /// <summary>Связки: один анекдот на один фон (ручной ввод, без OpenRouter).</summary>
        public List<string> Jokes;
        public List<string> BackgroundFiles;
        public WarmupSubtitleStyle Subtitles;
        public string OpenRouterKey = "", ElevenLabsKey = "", OpenRouterModel = "openai/gpt-4o-mini";

        internal bool UsesManualBundles => Jokes != null && Jokes.Any(j => !string.IsNullOrWhiteSpace(j));
    }

    public static class WarmupVideoEngine {
        public const double MaximumSeconds = 10;
        public const int SpeechAttempts = 3;
        internal static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
        internal static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v" };
        public static bool IsVideo(string path) => VideoExt.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());
        public static bool IsBackground(string path) => IsVideo(path) || ImageExt.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());
        public static string DefaultOutputFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch", "Прогрев");
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        static string Number(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);

        public static async Task<IReadOnlyList<string>> GenerateAsync(WarmupVideoJob job, Action<string> log, CancellationToken ct) {
            Validate(job);
            if (!ToolsLocator.TryResolve(out var ffmpeg, out var ffprobe, out var hint)) throw new Exception(hint);
            try {
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                    return await GenerateAsync(job, log, ct, client, ffmpeg, ffprobe).ConfigureAwait(false);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                throw new Exception("Сервис генерации не ответил за 2 минуты. Проверьте подключение и повторите позже.");
            }
        }

        static void Validate(WarmupVideoJob job) {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (string.IsNullOrWhiteSpace(job.ElevenLabsKey)) throw new Exception("Укажите API-ключ ElevenLabs в «Настройки».");
            if (string.IsNullOrWhiteSpace(job.OutputFolder)) throw new Exception("Выберите папку для готовых роликов.");
            if (job.UsesManualBundles) {
                var jokes = job.Jokes.Select(j => (j ?? "").Trim()).Where(j => j.Length > 0).ToList();
                var backgrounds = (job.BackgroundFiles ?? new List<string>()).Where(p => IsBackground(p) && File.Exists(p)).ToList();
                if (jokes.Count == 0) throw new Exception("Заполните хотя бы один анекдот в связке.");
                if (backgrounds.Count != jokes.Count)
                    throw new Exception("Число фонов (" + backgrounds.Count + ") должно совпадать с числом анекдотов (" + jokes.Count + "). Добавьте или удалите файлы фона.");
                if (jokes.Count > 100) throw new Exception("Не больше 100 связок за один запуск.");
                return;
            }
            if (string.IsNullOrWhiteSpace(job.OpenRouterKey)) throw new Exception("Укажите API-ключ OpenRouter в «Настройки».");
            if (string.IsNullOrWhiteSpace(job.Prompt)) throw new Exception("Напишите, о чём придумать анекдот.");
            if (job.Count < 1 || job.Count > 100) throw new Exception("Количество роликов: от 1 до 100.");
            if (!IsBackground(job.BackgroundFile) || !File.Exists(job.BackgroundFile)) throw new Exception("Выберите существующий файл фона: фото или видео.");
        }

        // Injectable HTTP transport for offline integration tests; production always uses official endpoints.
        internal static async Task<IReadOnlyList<string>> GenerateAsync(WarmupVideoJob job, Action<string> log, CancellationToken ct, HttpClient client, string ffmpeg, string ffprobe) {
            Validate(job); ct.ThrowIfCancellationRequested(); log = log ?? (_ => { });
            Directory.CreateDirectory(job.OutputFolder);
            var manualJokes = job.UsesManualBundles ? job.Jokes.Select(j => (j ?? "").Trim()).Where(j => j.Length > 0).ToList() : null;
            var manualBackgrounds = job.UsesManualBundles ? job.BackgroundFiles.Where(p => IsBackground(p) && File.Exists(p)).ToList() : null;
            int total = job.UsesManualBundles ? manualJokes.Count : job.Count;
            foreach (var bg in job.UsesManualBundles ? (IEnumerable<string>)manualBackgrounds : new[] { job.BackgroundFile })
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-i", bg, "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-" }, ct, null, 30).ConfigureAwait(false);
            var outputs = new List<string>();
            string work = Path.Combine(Path.GetTempPath(), "VideoBatch-warmup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string partial = null;
            try {
                log("Получаю доступные голоса ElevenLabs…");
                var voices = await FetchVoicesAsync(client, job.ElevenLabsKey, ct).ConfigureAwait(false);
                var rnd = new Random(); var previous = new List<string>();
                for (int i = 0; i < total; i++) {
                    ct.ThrowIfCancellationRequested();
                    string prefix = "[" + (i + 1) + "/" + total + "] ";
                    string backgroundFile = job.UsesManualBundles ? manualBackgrounds[i] : job.BackgroundFile;
                    string voice = voices[rnd.Next(voices.Count)], text = "";
                    string mp3 = Path.Combine(work, "speech.mp3"), wav = Path.Combine(work, "speech.wav");
                    double duration = 0; WarmupAlignment alignment = null;
                    for (int attempt = 0; attempt < SpeechAttempts; attempt++) {
                        if (job.UsesManualBundles) {
                            text = manualJokes[i];
                            log(prefix + (attempt == 0 ? "Озвучиваю ваш текст через ElevenLabs…" : "Повторная озвучка (тот же текст)…"));
                        } else {
                            log(prefix + (attempt == 0 ? "Придумываю короткий анекдот…" : "Сокращаю анекдот целиком, без обрезки речи…"));
                            text = await FetchTextAsync(client, job, previous, text, duration, ct).ConfigureAwait(false);
                            log(prefix + "Озвучиваю через ElevenLabs…");
                        }
                        alignment = await SynthesizeAsync(client, job.ElevenLabsKey, voice, text, mp3, job.Subtitles?.Enabled == true, ct).ConfigureAwait(false);
                        // Decode MP3 delay/padding once. The PCM sample count, not an estimated text length,
                        // drives the video. No silence padding, speed change or speech trimming.
                        await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", mp3, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", wav }, ct, null, 30).ConfigureAwait(false);
                        duration = await AudioDurationAsync(ffprobe, wav, ct).ConfigureAwait(false);
                        log(prefix + "Длина озвучки: " + duration.ToString("0.00", CultureInfo.CurrentCulture) + " с");
                        if (duration <= MaximumSeconds) break;
                    }
                    if (duration > MaximumSeconds) throw new Exception(job.UsesManualBundles
                        ? "Анекдот " + (i + 1) + " не укладывается в 10 секунд. Сократите текст. Речь не обрезана; уже готовые ролики сохранены."
                        : "Не удалось получить озвучку до 10 секунд за 3 попытки. Упростите тему анекдота. Речь не обрезана; уже готовые ролики сохранены.");
                    string name = "Прогрев-" + (job.Market == "EN" ? "EN" : "RU") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4";
                    string output = Path.Combine(job.OutputFolder, name);
                    partial = Path.Combine(job.OutputFolder, ".processing-" + name);
                    log(prefix + "Собираю ролик по длине озвучки…");
                    List<WarmupCaption> captions = null;
                    if (job.Subtitles?.Enabled == true) {
                        log(prefix + "Накладываю субтитры по таймкодам озвучки…");
                        captions = WarmupSubtitles.Rasterize(WarmupSubtitles.Cues(alignment, duration, job.Subtitles.WordsPerCaption), job.Subtitles, work);
                    }
                    await Core.Tool(ffmpeg, RenderArguments(backgroundFile, wav, partial, duration, captions), ct, null, 180).ConfigureAwait(false);
                    await VerifyOutputAsync(ffprobe, partial, duration, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    File.Move(partial, output); partial = null;
                    outputs.Add(output); previous.Add(text);
                    log(prefix + "Готово: " + name);
                }
            } finally {
                if (partial != null) { try { File.Delete(partial); } catch { } }
                try { Directory.Delete(work, true); } catch { }
            }
            return outputs;
        }

        internal static async Task<double> AudioDurationAsync(string ffprobe, string audio, CancellationToken ct) {
            string raw = await Core.Tool(ffprobe, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration", "-of", "default=noprint_wrappers=1:nokey=1", audio }, ct, null, 15).ConfigureAwait(false);
            if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new Exception("Не удалось измерить длительность озвучки.");
            return value;
        }

        internal static IEnumerable<string> RenderArguments(string background, string audio, string output, double duration, List<WarmupCaption> captions = null) {
            if (duration <= 0 || duration > MaximumSeconds || double.IsNaN(duration)) throw new Exception("Озвучка должна длиться не больше 10 секунд.");
            var args = new List<string> { "-v", "error", "-y", "-filter_complex_threads", "1", "-filter_threads", "1" };
            if (IsVideo(background)) args.AddRange(new[] { "-stream_loop", "-1" });
            else args.AddRange(new[] { "-loop", "1", "-framerate", "30" });
            args.AddRange(new[] { "-i", background, "-i", audio });
            if (captions != null) foreach (var cue in captions) args.AddRange(new[] { "-i", cue.ImagePath });
            const string backgroundFilter = "setpts=PTS-STARTPTS,scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30,format=yuv420p";
            if (captions != null && captions.Count > 0) {
                var filter = new StringBuilder("[0:v:0]" + backgroundFilter + "[base0];");
                for (int i = 0; i < captions.Count; i++) {
                    filter.Append("[base" + i + "][" + (i + 2) + ":v:0]overlay=0:0:enable='gte(t," + Number(captions[i].Start) + ")*lt(t," + Number(captions[i].End) + ")'[base" + (i + 1) + "]");
                    if (i + 1 < captions.Count) filter.Append(";");
                }
                args.AddRange(new[] { "-filter_complex", filter.ToString(), "-map", "[base" + captions.Count + "]" });
            } else args.AddRange(new[] { "-vf", backgroundFilter, "-map", "0:v:0" });
            args.AddRange(new[] { "-map", "1:a:0", "-map_metadata", "-1", "-c:v", "libx264", "-threads", "4", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-af", "asetpts=PTS-STARTPTS",
                "-t", Number(duration), "-movflags", "+faststart", output });
            return args;
        }

        internal static async Task VerifyOutputAsync(string ffprobe, string output, double duration, CancellationToken ct) {
            var raw = await Core.Probe(ffprobe, output, ct).ConfigureAwait(false);
            var streams = raw.streams ?? new ProbeStream[0];
            var video = streams.SingleOrDefault(s => s.codec_type == "video");
            var audio = streams.SingleOrDefault(s => s.codec_type == "audio");
            double fileDuration = Core.Parse(raw.format?.duration), audioDuration = Core.Parse(audio?.duration);
            // MP4 frame grid is 1/30 s; AAC uses 1024-sample packets. Allow container rounding only.
            if (video == null || audio == null || video.width != 1080 || video.height != 1920 || fileDuration > MaximumSeconds + .001 || Math.Abs(fileDuration - duration) > .04 || Math.Abs(audioDuration - duration) > .025)
                throw new Exception("Проверка готового MP4 не пройдена: длительность или дорожки не совпадают с озвучкой.");
        }

        static async Task<string> FetchTextAsync(HttpClient client, WarmupVideoJob job, List<string> previous, string tooLong, double seconds, CancellationToken ct) {
            string prompt = "Write ONE complete short clean joke in " + (job.Market == "EN" ? "English" : "Russian") + ". Topic/instructions: " + job.Prompt.Trim()
                + ". Aim for natural speech lasting 6–7 seconds, strictly under 10 seconds. Usually 10–16 words; fewer for long words. Keep the setup AND punchline, no explanation, no title, no stage directions. Return ONLY a JSON object {\"text\":\"the joke\"}.";
            if (previous.Count > 0) prompt += " Do not repeat these jokes: " + Json().Serialize(previous);
            if (!string.IsNullOrEmpty(tooLong)) prompt += " Previous joke: " + Json().Serialize(tooLong) + " took " + Number(seconds) + " seconds. Rewrite substantially shorter (aim 4–6 seconds) retaining a complete joke.";
            var payload = new { model = string.IsNullOrWhiteSpace(job.OpenRouterModel) ? "openai/gpt-4o-mini" : job.OpenRouterModel,
                messages = new[] { new { role = "user", content = prompt } }, max_tokens = 200 };
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")) {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + job.OpenRouterKey);
                request.Headers.TryAddWithoutValidation("X-Title", "VideoBatch");
                request.Content = new StringContent(Json().Serialize(payload), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false)) {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    CheckHttp(response, "OpenRouter"); ct.ThrowIfCancellationRequested();
                    return ParseText(body);
                }
            }
        }
        internal static string ParseText(string body) {
            try {
                var root = Json().Deserialize<RouterResponse>(body);
                var choice = root.choices.Single();
                if (choice.finish_reason != "stop") throw new Exception();
                string raw = choice.message.content.Trim();
                if (raw.StartsWith("```")) { int start = raw.IndexOf('\n'); raw = raw.Substring(start + 1); int end = raw.LastIndexOf("```", StringComparison.Ordinal); if (end >= 0) raw = raw.Substring(0, end); }
                string text = Json().Deserialize<JokeResponse>(raw).text?.Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Length > 300) throw new Exception();
                return text;
            } catch { throw new Exception("OpenRouter вернул неполный анекдот или неверный ответ. Попробуйте ещё раз."); }
        }
        public sealed class RouterResponse { public Choice[] choices { get; set; } }
        public sealed class Choice { public string finish_reason { get; set; } public Message message { get; set; } }
        public sealed class Message { public string content { get; set; } }
        public sealed class JokeResponse { public string text { get; set; } }
        public sealed class VoiceResponse { public Voice[] voices { get; set; } public bool has_more { get; set; } public string next_page_token { get; set; } }
        public sealed class Voice { public string voice_id { get; set; } }

        static async Task<List<string>> FetchVoicesAsync(HttpClient client, string key, CancellationToken ct) {
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v2/voices?page_size=100")) {
                request.Headers.TryAddWithoutValidation("xi-api-key", key);
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false)) {
                    CheckHttp(response, "ElevenLabs: список голосов");
                    var data = Json().Deserialize<VoiceResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var ids = (data.voices ?? new Voice[0]).Select(v => v.voice_id).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
                    if (ids.Count == 0) throw new Exception("В ElevenLabs нет доступных голосов. Проверьте доступ ключа к голосам.");
                    return ids;
                }
            }
        }
        static async Task<WarmupAlignment> SynthesizeAsync(HttpClient client, string key, string voice, string text, string output, bool withCaptions, CancellationToken ct) {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/text-to-speech/" + Uri.EscapeDataString(voice) + (withCaptions ? "/with-timestamps" : "") + "?output_format=mp3_44100_128")) {
                request.Headers.TryAddWithoutValidation("xi-api-key", key);
                request.Content = new StringContent(Json().Serialize(new { text, model_id = "eleven_multilingual_v2" }), Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false)) {
                    CheckHttp(response, "ElevenLabs: озвучка");
                    byte[] bytes; WarmupAlignment alignment = null;
                    if (withCaptions) {
                        var speech = Json().Deserialize<TimedSpeech>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        bytes = Convert.FromBase64String(speech.audio_base64);
                        alignment = speech.normalized_alignment ?? speech.alignment;
                    } else bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (bytes.Length == 0) throw new Exception("ElevenLabs вернул пустую озвучку.");
                    File.WriteAllBytes(output, bytes); return alignment;
                }
            }
        }
        public sealed class TimedSpeech { public string audio_base64 { get; set; } public WarmupAlignment alignment { get; set; } public WarmupAlignment normalized_alignment { get; set; } }
        static void CheckHttp(HttpResponseMessage response, string service) {
            if (response.IsSuccessStatusCode) return;
            int code = (int)response.StatusCode;
            // Never put provider response bodies, request text or API keys into UI logs.
            string hint = code == 401 || code == 403 ? "Проверьте API-ключ и права доступа." : code == 402 ? "Проверьте баланс API." : code == 429 ? "Достигнут лимит API. Повторите позже." : "Сервис не выполнил запрос. Повторите позже.";
            throw new Exception(service + ": HTTP " + code + ". " + hint);
        }
    }
}
