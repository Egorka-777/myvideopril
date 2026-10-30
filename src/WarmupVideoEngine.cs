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
        /// <summary>Ручные тексты и пул фонов; использованные пары сохраняются отдельно по рынкам.</summary>
        public List<string> Jokes;
        public List<string> BackgroundFiles;
        public WarmupSubtitleStyle Subtitles;
        public string OpenRouterKey = "", ElevenLabsKey = "", OpenRouterModel = "openai/gpt-4o-mini", HttpProxy = "";

        internal bool UsesManualBundles => Jokes != null && Jokes.Any(j => !string.IsNullOrWhiteSpace(j));
    }

    public sealed class WarmupBundleRecord {
        public string Signature = "", Output = "";
    }
    public sealed class WarmupBundleHistoryData {
        public List<WarmupBundleRecord> Records = new List<WarmupBundleRecord>();
    }
    public sealed class WarmupBundlePlan {
        public string Joke, Background, Signature;
    }
    public sealed class WarmupBundleInventory {
        public int Total, Used;
        public int Remaining => Total - Used;
        public List<WarmupBundlePlan> Next = new List<WarmupBundlePlan>();
    }
    public static class WarmupBundleHistory {
        static readonly object gate = new object();
        static string HistoryPath(string market) => Path.Combine(Store.Root, "warmup-history-" + (market == "EN" ? "en" : "ru") + ".xml");
        static WarmupBundleHistoryData Load(string market) {
            string file = HistoryPath(market);
            if (!File.Exists(file)) return new WarmupBundleHistoryData();
            using (var stream = File.OpenRead(file)) return (WarmupBundleHistoryData)new System.Xml.Serialization.XmlSerializer(typeof(WarmupBundleHistoryData)).Deserialize(stream);
        }
        public static WarmupBundleInventory Inventory(string market, IEnumerable<string> jokes, IEnumerable<string> backgrounds) {
            lock (gate) {
                var texts = jokes.Select(j => (j ?? "").Trim()).Where(j => j.Length > 0).Distinct().ToList();
                var assets = backgrounds.Where(File.Exists).Select(p => new { Path = p, Hash = AssemblyFiles.Hash(p) }).GroupBy(a => a.Hash).Select(g => g.First()).ToList();
                var used = new HashSet<string>(Load(market).Records.Select(r => r.Signature));
                var result = new WarmupBundleInventory { Total = texts.Count * assets.Count };
                var selected = new Dictionary<string, int>();
                for (int i = 0; i < texts.Count; i++) {
                    var candidates = assets.Select((a, n) => new { Asset = a, Index = n, Signature = AssemblyFiles.HashText(texts[i] + "|" + a.Hash) }).ToList();
                    result.Used += candidates.Count(c => used.Contains(c.Signature));
                    var next = candidates.Where(c => !used.Contains(c.Signature))
                        .OrderBy(c => selected.ContainsKey(c.Asset.Hash) ? selected[c.Asset.Hash] : 0)
                        .ThenBy(c => (c.Index - i % Math.Max(1, assets.Count) + assets.Count) % Math.Max(1, assets.Count)).FirstOrDefault();
                    if (next == null) continue;
                    result.Next.Add(new WarmupBundlePlan { Joke = texts[i], Background = next.Asset.Path, Signature = next.Signature });
                    selected[next.Asset.Hash] = (selected.ContainsKey(next.Asset.Hash) ? selected[next.Asset.Hash] : 0) + 1;
                }
                return result;
            }
        }
        public static void Complete(string market, WarmupBundlePlan plan, string output) {
            lock (gate) {
                var data = Load(market);
                if (data.Records.Any(r => r.Signature == plan.Signature)) throw new InvalidOperationException("Эта связка уже создана; повтор не записан.");
                data.Records.Add(new WarmupBundleRecord { Signature = plan.Signature, Output = output });
                string file = HistoryPath(market), temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                Directory.CreateDirectory(Store.Root);
                try {
                    using (var stream = File.Create(temp)) new System.Xml.Serialization.XmlSerializer(typeof(WarmupBundleHistoryData)).Serialize(stream, data);
                    if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
                } finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }

    public static class WarmupVideoEngine {
        /// <summary>Только защита от явно битых/бесконечных файлов, не лимит сценария.</summary>
        public const double MaximumSeconds = 600;
        internal static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
        internal static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v" };
        public static bool IsVideo(string path) => VideoExt.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());
        public static bool IsBackground(string path) => IsVideo(path) || ImageExt.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());
        public static string DefaultOutputFolder => VideoBatchOutputPaths.WarmupRoot;
        public static string DefaultOutputFolderFor(string market) => VideoBatchOutputPaths.WarmupMarket(market);
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        static string Number(double n) => n.ToString("0.#########", CultureInfo.InvariantCulture);

        public static async Task<IReadOnlyList<string>> GenerateAsync(WarmupVideoJob job, Action<string> log, CancellationToken ct) {
            Validate(job);
            if (!ToolsLocator.TryResolve(out var ffmpeg, out var ffprobe, out var hint)) throw new Exception(hint);
            try {
                using (var router = OutboundHttp.Create(job.HttpProxy, TimeSpan.FromMinutes(2), allowAutoRedirect: true))
                using (var eleven = OutboundHttp.Create(job.HttpProxy, TimeSpan.FromMinutes(2), allowAutoRedirect: false))
                    return await GenerateAsync(job, log, ct, router, eleven, ffmpeg, ffprobe).ConfigureAwait(false);
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
                if (backgrounds.Count == 0) throw new Exception("Добавьте хотя бы один существующий фон.");
                if (backgrounds.Count != (job.BackgroundFiles ?? new List<string>()).Count) throw new Exception("Один из файлов фона не найден или имеет неподдерживаемый формат.");
                if (jokes.Count > 100) throw new Exception("Не больше 100 связок за один запуск.");
                return;
            }
            if (string.IsNullOrWhiteSpace(job.OpenRouterKey)) throw new Exception("Укажите API-ключ OpenRouter в «Настройки».");
            if (string.IsNullOrWhiteSpace(job.Prompt)) throw new Exception("Напишите, о чём придумать анекдот.");
            if (job.Count < 1 || job.Count > 100) throw new Exception("Количество роликов: от 1 до 100.");
            if (!IsBackground(job.BackgroundFile) || !File.Exists(job.BackgroundFile)) throw new Exception("Выберите существующий файл фона: фото или видео.");
        }

        // Injectable HTTP transport for offline integration tests; production always uses official endpoints.
        internal static async Task<IReadOnlyList<string>> GenerateAsync(WarmupVideoJob job, Action<string> log, CancellationToken ct, HttpClient routerClient, HttpClient elevenClient, string ffmpeg, string ffprobe) {
            Validate(job); ct.ThrowIfCancellationRequested(); log = log ?? (_ => { });
            Directory.CreateDirectory(job.OutputFolder);
            var inventory = job.UsesManualBundles ? WarmupBundleHistory.Inventory(job.Market, job.Jokes, job.BackgroundFiles) : null;
            var plans = inventory?.Next;
            if (inventory != null) {
                log("Связки · всего " + inventory.Total + " · использовано " + inventory.Used + " · осталось " + inventory.Remaining);
                if (plans.Count == 0) throw new InvalidOperationException("Все связки этих анекдотов и фонов уже использованы. Добавьте новые тексты или фоны.");
            }
            var manualJokes = plans?.Select(p => p.Joke).ToList();
            var manualBackgrounds = plans?.Select(p => p.Background).Distinct().ToList();
            int total = job.UsesManualBundles ? plans.Count : job.Count;
            foreach (var bg in job.UsesManualBundles ? (IEnumerable<string>)manualBackgrounds : new[] { job.BackgroundFile })
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-i", bg, "-map", "0:v:0", "-frames:v", "1", "-f", "null", "-" }, ct, null, 30).ConfigureAwait(false);
            var outputs = new List<string>();
            string work = Path.Combine(Path.GetTempPath(), "VideoBatch-warmup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string partial = null;
            try {
                log("Получаю доступные голоса ElevenLabs…");
                var voices = await FetchVoicesAsync(elevenClient, job.ElevenLabsKey, ct).ConfigureAwait(false);
                var rnd = new Random(); var previous = new List<string>();
                for (int i = 0; i < total; i++) {
                    ct.ThrowIfCancellationRequested();
                    string prefix = "[" + (i + 1) + "/" + total + "] ";
                    string backgroundFile = job.UsesManualBundles ? plans[i].Background : job.BackgroundFile;
                    string voice = voices[rnd.Next(voices.Count)], text = "";
                    string mp3 = Path.Combine(work, "speech.mp3"), wav = Path.Combine(work, "speech.wav");
                    double duration = 0; WarmupAlignment alignment = null;
                    if (job.UsesManualBundles) {
                        text = manualJokes[i];
                        log(prefix + "Озвучиваю ваш текст через ElevenLabs…");
                    } else {
                        log(prefix + "Придумываю анекдот…");
                        text = await FetchTextAsync(routerClient, job, previous, ct).ConfigureAwait(false);
                        log(prefix + "Озвучиваю через ElevenLabs…");
                    }
                    alignment = await SynthesizeAsync(elevenClient, job.ElevenLabsKey, voice, text, mp3, job.Subtitles?.Enabled == true, ct).ConfigureAwait(false);
                    await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", mp3, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-c:a", "pcm_s16le", wav }, ct, null, 30).ConfigureAwait(false);
                    duration = await AudioDurationAsync(ffprobe, wav, ct).ConfigureAwait(false);
                    log(prefix + "Длина озвучки: " + duration.ToString("0.00", CultureInfo.CurrentCulture) + " с");
                    if (duration <= 0 || duration > MaximumSeconds || double.IsNaN(duration))
                        throw new Exception("Озвучка " + (i + 1) + " имеет недопустимую длительность (" + duration.ToString("0.##", CultureInfo.CurrentCulture) + " с).");
                    string name = VideoBatchOutputPaths.WarmupVideoFileName(job.Market);
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
                    if (job.UsesManualBundles) {
                        try { WarmupBundleHistory.Complete(job.Market, plans[i], output); }
                        catch (Exception e) { throw new IOException("Ролик создан: " + output + ", но история связки не сохранена. Остановлено: " + e.Message, e); }
                    }
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
            if (duration <= 0 || duration > MaximumSeconds || double.IsNaN(duration)) throw new Exception("Недопустимая длительность озвучки для сборки ролика.");
            var args = new List<string> { "-v", "error", "-y", "-filter_complex_threads", "1", "-filter_threads", "1" };
            if (IsVideo(background)) args.AddRange(new[] { "-stream_loop", "-1" });
            else args.AddRange(new[] { "-loop", "1", "-framerate", "30" });
            args.AddRange(new[] { "-i", background, "-i", audio });
            string captionTrack = captions?.FirstOrDefault()?.TrackPath;
            if (!string.IsNullOrEmpty(captionTrack)) args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", captionTrack });
            else if (captions != null) foreach (var cue in captions) args.AddRange(new[] { "-i", cue.ImagePath });
            const string backgroundFilter = "setpts=PTS-STARTPTS,scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30,format=yuv420p";
            if (!string.IsNullOrEmpty(captionTrack)) {
                args.AddRange(new[] { "-filter_complex", "[0:v:0]" + backgroundFilter + "[base];[2:v:0]fps=30,format=rgba[captions];[base][captions]overlay=0:0:repeatlast=1:eof_action=repeat[captioned]", "-map", "[captioned]" });
            } else if (captions != null && captions.Count > 0) {
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

        static async Task<string> FetchTextAsync(HttpClient client, WarmupVideoJob job, List<string> previous, CancellationToken ct) {
            string prompt = "Write ONE complete clean joke in " + (job.Market == "EN" ? "English" : "Russian") + ". Topic/instructions: " + job.Prompt.Trim()
                + ". Keep the setup AND punchline, no explanation, no title, no stage directions. Return ONLY a JSON object {\"text\":\"the joke\"}.";
            if (previous.Count > 0) prompt += " Do not repeat these jokes: " + Json().Serialize(previous);
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
            string location = response.Headers.Location?.ToString() ?? "";
            if (code == 302 || code == 301 || code == 307 || code == 308 || location.IndexOf("22497891312401", StringComparison.OrdinalIgnoreCase) >= 0 || location.IndexOf("restrict", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception(service + ": доступ с вашего IP закрыт ElevenLabs (санкции/регион). Укажите HTTP(S)-прокси с зарубежным IP в «Настройки → нейросеть и озвучка» или проверьте exit-IP прокси.");
            // Never put provider response bodies, request text or API keys into UI logs.
            string hint = code == 401 ? "Проверьте API-ключ." : code == 403 && service.StartsWith("ElevenLabs", StringComparison.Ordinal)
                ? "Часто это блок по региону (не ключ). Укажите HTTP(S)-прокси с зарубежным IP в настройках."
                : code == 403 ? "Проверьте API-ключ и права доступа." : code == 402 ? "Проверьте баланс API." : code == 429 ? "Достигнут лимит API. Повторите позже." : "Сервис не выполнил запрос. Повторите позже.";
            throw new Exception(service + ": HTTP " + code + ". " + hint);
        }
    }
}
