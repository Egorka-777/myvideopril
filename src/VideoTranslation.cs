using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class VideoTranslationOptions {
        public string TargetLanguage = "ru";
        public string Style = "Гоблинский", Profanity = "Без мата", Instructions = "", GeminiModel = "gemini-2.5-flash", VoiceId = "";
        public bool BurnSubtitles;
        public ProcessingSettings Processing = new ProcessingSettings();
        public WarmupSubtitleStyle SubtitleStyle = new WarmupSubtitleStyle();
    }
    public sealed class VideoTranslationSegment {
        public int Id;
        public double Start, End;
        public string Original = "", Text = "", TargetLanguage = "ru";
        public string TranslationKey = "", AudioKey = "", AudioPath = "";
    }
    public sealed class VideoTranslationProject {
        public int Version = 1;
        public string Source = "", Directory = "", Output = "", SourceHash = "", TimelineHash = "";
        public double Duration;
        public VideoTranslationOptions Options = new VideoTranslationOptions();
        public List<VideoTranslationSegment> Segments = new List<VideoTranslationSegment>();
        public List<string> BatchSources = new List<string>();
    }
    public static class VideoTranslationEngine {
        public static readonly string[] LanguageCodes = { "en", "ru", "hi", "bn", "es", "zh", "ar", "pt", "fr", "de" };
        public static readonly string[] LanguageNames = { "Английский", "Русский", "Хинди", "Бенгальский", "Испанский", "Китайский (мандаринский)", "Арабский", "Португальский", "Французский", "Немецкий" };
        public static string LanguageName(string code) {
            int index = Array.IndexOf(LanguageCodes, code);
            if (index < 0) throw new Exception("Выберите язык перевода из списка.");
            return LanguageNames[index];
        }
        public static readonly string[] Styles = { "Гоблинский", "Колхозный", "Матёрый спец", "Дерзкий блогер", "Интеллигентный сарказм" };
        public static readonly string[] ProfanityLevels = { "Без мата", "Умеренно", "Без цензуры" };
        public const string ProjectFileName = "translation.json";
        const int Rate = 48000;
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        internal static string Digest(string text) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        static string FileHash(string file) { using (var stream = File.OpenRead(file)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        internal static string Timeline(VideoTranslationProject p) => Digest(Json().Serialize(new { p.Duration, Segments = p.Segments.Select(s => new { s.Id, s.Start, s.End, s.Original }).ToArray() }));
        static T Copy<T>(T value) => Json().Deserialize<T>(Json().Serialize(value));
        internal static void ValidateOptions(VideoTranslationOptions o) {
            if (o == null || !Styles.Contains(o.Style) || !ProfanityLevels.Contains(o.Profanity)) throw new Exception("Выберите стиль перевода и отдельно уровень мата.");
            LanguageName(o.TargetLanguage);
            if (string.IsNullOrWhiteSpace(o.GeminiModel)) throw new Exception("Укажите модель Gemini.");
        }
        static void Validate(VideoTranslationProject p) {
            if (p == null || p.Version != 1 || p.Segments == null || p.Segments.Count == 0 || !Finite(p.Duration) || p.Duration <= 0) throw new Exception("Некорректный проект перевода или нет распознанной речи.");
            double end = 0; var ids = new HashSet<int>();
            foreach (var s in p.Segments) {
                if (s == null || !ids.Add(s.Id) || !Finite(s.Start) || !Finite(s.End) || s.Start < end || s.End <= s.Start || s.End > p.Duration)
                    throw new Exception("Неверные или пересекающиеся исходные окна реплик. Таймлайн не изменён.");
                end = s.End;
            }
            if (p.TimelineHash != Timeline(p)) throw new Exception("Изменены исходные таймкоды или оригинал. Разрешено редактировать только перевод; распознайте исходник заново.");
        }
        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        static void VerifySource(VideoTranslationProject p) {
            if (!File.Exists(p.Source) || FileHash(p.Source) != p.SourceHash) throw new Exception("Исходное видео отсутствует или изменилось. Нельзя использовать старые таймкоды для другого видео.");
        }
        public static void Save(VideoTranslationProject p) {
            Validate(p);
            // Editing text invalidates only this segment's active audio reference. The paid artifact remains on disk.
            foreach (var s in p.Segments) if (s.AudioKey != SpeechKey(s.Text, p.Options?.VoiceId, p.Options?.TargetLanguage)) { s.AudioKey = ""; s.AudioPath = ""; }
            System.IO.Directory.CreateDirectory(p.Directory);
            WriteJson(Path.Combine(p.Directory, ProjectFileName), p);
        }
        public static VideoTranslationProject Load(string path) {
            string file = System.IO.Directory.Exists(path) ? Path.Combine(path, ProjectFileName) : path;
            var p = Json().Deserialize<VideoTranslationProject>(File.ReadAllText(file));
            if (p != null) p.Directory = Path.GetDirectoryName(Path.GetFullPath(file));
            Validate(p); return p;
        }
        static void WriteJson(string path, object value) {
            // Keep the atomic-write temporary name independent of the destination length.
            string temp = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + ".tmp");
            try {
                File.WriteAllText(temp, Json().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static async Task<VideoTranslationProject> TranscribeAsync(string source, string projectDirectory, CancellationToken ct, Action<string> progress) {
            ct.ThrowIfCancellationRequested();
            string ffmpeg, probe, hint;
            if (!ToolsLocator.TryResolve(out ffmpeg, out probe, out hint)) throw new Exception(hint);
            return await Task.Run(async () => {
                source = Path.GetFullPath(source); projectDirectory = Path.GetFullPath(projectDirectory);
                string hash = FileHash(source), existing = Path.Combine(projectDirectory, ProjectFileName);
                if (File.Exists(existing)) {
                    var saved = Load(existing);
                    if (saved.SourceHash != hash) throw new Exception("Эта папка принадлежит другому исходнику. Выберите отдельную папку проекта.");
                    saved.Source = source; Save(saved); progress?.Invoke("Использую сохранённый проект перевода."); return saved;
                }
                var media = Core.VideoInfo(await Core.Probe(probe, source, ct).ConfigureAwait(false));
                if (!media.HasAudio) throw new Exception("В исходном видео нет звуковой дорожки для распознавания.");
                progress?.Invoke("Распознаю исходную речь локально; оригинальные таймкоды сохраняются.");
                await LocalAudioTranscription.PrepareAsync(new[] { source }, ct, progress).ConfigureAwait(false);
                var template = new AssemblyTemplate { SubtitleStyle = new WarmupSubtitleStyle { WordsPerCaption = 6 } };
                var cues = AssemblySpeechPlan.CaptionsForAudio(template, source, null, media.VideoDuration);
                var p = new VideoTranslationProject { Source = source, SourceHash = hash, Directory = projectDirectory, Duration = media.VideoDuration };
                int id = 0;
                // Group recognition captions into natural, bounded utterances, keeping silence outside speech.
                foreach (var cue in cues) {
                    var last = p.Segments.LastOrDefault();
                    if (last != null && cue.Start - last.End < .6 && cue.End - last.Start <= 12 && !".!?…".Contains(last.Original.TrimEnd().Last())) {
                        last.End = cue.End; last.Original += " " + cue.Text;
                    } else p.Segments.Add(new VideoTranslationSegment { Id = ++id, Start = cue.Start, End = cue.End, Original = cue.Text });
                }
                p.TimelineHash = Timeline(p); Save(p); return p;
            }, ct).ConfigureAwait(false);
        }
        internal static string TranslationKey(VideoTranslationSegment s, VideoTranslationOptions o) => Digest(Json().Serialize(new { s.Original, s.Start, s.End, o.Style, o.Profanity, o.Instructions, o.GeminiModel, o.TargetLanguage }));
        public static async Task TranslateAsync(VideoTranslationProject p, VideoTranslationOptions options, string geminiKey, CancellationToken ct, Action<string> progress, string proxyUrl = "") {
            Validate(p); ValidateOptions(options); ct.ThrowIfCancellationRequested();
            await Task.Run(() => VerifySource(p), ct).ConfigureAwait(false);
            p.Options = Copy(options); Save(p);
            using (var client = new GeminiTranslationClient(geminiKey, proxyUrl: proxyUrl)) {
                var pending = p.Segments.Where(s => s.TranslationKey != TranslationKey(s, options) || string.IsNullOrWhiteSpace(s.Text)).ToList();
                int done = p.Segments.Count - pending.Count;
                while (pending.Count > 0) {
                    ct.ThrowIfCancellationRequested();
                    var batch = new List<VideoTranslationSegment>(); int chars = 0;
                    foreach (var s in pending) { if (batch.Count >= 40 || batch.Count > 0 && chars + s.Original.Length > 12000) break; batch.Add(s); chars += s.Original.Length; }
                    progress?.Invoke("Gemini: перевод реплик " + (done + 1) + "–" + (done + batch.Count) + " / " + p.Segments.Count);
                    var result = await client.TranslateAsync(batch, options, ct).ConfigureAwait(false);
                    foreach (var s in batch) { s.Text = result[s.Id]; s.TargetLanguage = options.TargetLanguage; s.TranslationKey = TranslationKey(s, options); }
                    Save(p); done += batch.Count; pending.RemoveRange(0, batch.Count);
                }
            }
        }
        internal static string SpeechModel(string language) => language == "bn" ? "eleven_v3" : "eleven_multilingual_v2";
        internal static string SpeechKey(string text, string voice, string language = "ru") => Digest(Json().Serialize(new { text = text ?? "", voice = voice ?? "", model = SpeechModel(language), format = "mp3_44100_128", timestamps = true }));
        internal static string SpeechAudioPath(string folder, string key) {
            // Existing paid audio keeps its original name; new artifacts use a compact
            // name while the receipt still verifies the complete key and audio hash.
            string legacy = Path.Combine(folder, key + ".mp3");
            return File.Exists(legacy) ? legacy : Path.Combine(folder, key.Substring(0, 24) + ".mp3");
        }
        internal static string ProjectDirectoryName(string source, DateTime created) {
            string name = Path.GetFileNameWithoutExtension(source);
            return (name.Length > 48 ? name.Substring(0, 48) : name) + "-" + created.ToString("yyyyMMdd-HHmmss");
        }
        public static async Task RenderAsync(VideoTranslationProject p, VideoTranslationOptions options, string elevenKey, string proxy, string ffmpeg, string ffprobe, CancellationToken ct, Action<string> progress) {
            await RenderCore(p, options, ffmpeg, ffprobe, ct, progress, async text => {
                using (var client = new ElevenLabsClient(elevenKey, proxy)) return await client.GenerateAsync(text, options.VoiceId, null, true, ct, SpeechModel(options.TargetLanguage), options.TargetLanguage == "bn" ? "bn" : null).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        internal static async Task RenderCore(VideoTranslationProject p, VideoTranslationOptions options, string ffmpeg, string ffprobe, CancellationToken ct, Action<string> progress, Func<string, Task<ElevenLabsSpeech>> generate) {
            Validate(p); ValidateOptions(options); ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(options.VoiceId)) throw new Exception("Выберите голос ElevenLabs.");
            if (p.Segments.Any(s => string.IsNullOrWhiteSpace(s.Text))) throw new Exception("Сначала переведите все реплики или заполните перевод вручную.");
            if (p.Segments.Any(s => s.TargetLanguage != options.TargetLanguage)) throw new Exception("Язык перевода изменён. Сначала нажмите «Перевести через Gemini», затем создавайте озвучку.");
            var processing = options.Processing ?? new ProcessingSettings(); processing.Validate();
            if (processing.Trim.Enabled || processing.Speed.Enabled || processing.Tempo.Enabled) throw new Exception("Для перевода отключите подрезку, скорость видео и темп аудио: исходный таймлайн должен оставаться неизменным.");
            await Task.Run(() => VerifySource(p), ct).ConfigureAwait(false);
            p.Options = Copy(options); Save(p);
            string audioFolder = Path.Combine(p.Directory, "speech"), work = Path.Combine(p.Directory, "render");
            System.IO.Directory.CreateDirectory(audioFolder); System.IO.Directory.CreateDirectory(work);
            progress?.Invoke("Исходный звук будет полностью удалён, включая музыку и эффекты. Разделение дорожек не выполняется.");
            string timeline = Path.Combine(work, "timeline.wav");
            var captions = new List<WarmupCaption>();
            using (var wav = new FileStream(timeline, FileMode.Create, FileAccess.ReadWrite)) {
                long total = Samples(p.Duration); WriteWavHeader(wav, total);
                // Zero-filled fixed timeline; speech is written only inside its immutable window.
                wav.SetLength(44 + total * 2);
                foreach (var s in p.Segments) {
                    ct.ThrowIfCancellationRequested(); progress?.Invoke("Озвучка реплики " + s.Id + " / " + p.Segments.Count);
                    string key = SpeechKey(s.Text, options.VoiceId, options.TargetLanguage), audio = SpeechAudioPath(audioFolder, key), receipt = audio + ".speech.json";
                    ElevenLabsSpeech speech;
                    if (File.Exists(audio)) {
                        if (!File.Exists(receipt)) throw new Exception("Озвучка реплики " + s.Id + " сохранена, но нет метаданных: " + receipt + ". Автоматическая платная перегенерация запрещена.");
                        var saved = Json().Deserialize<SpeechReceipt>(File.ReadAllText(receipt));
                        if (saved == null || saved.Key != key || saved.AudioHash != FileHash(audio)) throw new Exception("Повреждён кэш озвучки реплики " + s.Id + ". Автоматической платной перегенерации нет.");
                        speech = new ElevenLabsSpeech { Text = s.Text, Alignment = saved.Alignment, NormalizedAlignment = saved.NormalizedAlignment };
                    } else {
                        speech = await generate(s.Text).ConfigureAwait(false);
                        // Persist a successful paid result BEFORE probing, captions, or any render step can fail.
                        string temp = audio + ".tmp"; File.WriteAllBytes(temp, speech.Audio); File.Move(temp, audio);
                        WriteJson(receipt, new SpeechReceipt { Key = key, AudioHash = FileHash(audio), Alignment = speech.Alignment, NormalizedAlignment = speech.NormalizedAlignment });
                    }
                    s.AudioKey = key; s.AudioPath = audio; Save(p);
                    AssemblySpeechPlan.SaveTiming(audio, s.Text, speech.SubtitleAlignment);
                    string pcm = Path.Combine(work, "segment.pcm");
                    await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-i", audio, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", pcm }, ct, null, 300).ConfigureAwait(false);
                    long length = new FileInfo(pcm).Length / 2, first = Samples(s.Start), window = Samples(s.End) - first;
                    double timingScale = 1;
                    if (length > window) {
                        double tempo = (double)length / window;
                        timingScale = 1 / tempo;
                        progress?.Invoke("Подгоняю реплику " + s.Id + " в исходное окно: темп ×" + Core.N(tempo) + ". Следующие таймкоды не меняются.");
                        string filter = string.Join(",", ProcessingEngine.TempoFilters(tempo)) + ",apad,atrim=end_sample=" + window;
                        await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-i", audio, "-vn", "-ac", "1", "-ar", "48000", "-af", filter, "-f", "s16le", pcm }, ct, null, 300).ConfigureAwait(false);
                        length = new FileInfo(pcm).Length / 2;
                    }
                    EnsureFits(s, length, window);
                    wav.Position = 44 + first * 2;
                    using (var input = File.OpenRead(pcm)) await input.CopyToAsync(wav, 81920, ct).ConfigureAwait(false);
                    File.Delete(pcm);
                    // Full editable speech text, no provider-normalized spelling substitution in SRT or burn-in.
                    captions.Add(new WarmupCaption { Start = s.Start, End = s.End, Text = s.Text,
                        Words = SpeechWords(s, speech.SubtitleAlignment, (double)length / Rate, timingScale) });
                }
            }
            string srt = Path.Combine(p.Directory, "translation.srt"); File.WriteAllText(srt, Srt(p.Segments), new UTF8Encoding(true));
            var media = Core.VideoInfo(await Core.Probe(ffprobe, p.Source, ct).ConfigureAwait(false));
            media.Duration = p.Duration;
            string partial = Path.Combine(work, "translated.partial.mp4"), processed = Path.Combine(work, "processed.mp4");
            try {
                // Reuse all existing processing filters with a replacement narration, never original audio.
                var profile = new Profile { Processing = new ProcessingSample() };
                ProcessingEngine.Apply(profile, processing, media, new Random(17), DateTime.Now);
                var args = ProcessingEngine.Arguments(p.Source, processed, profile, media, timeline);
                await ProcessingEngine.Accelerate(ffmpeg,args,media.Width,media.Height,ct,progress).ConfigureAwait(false);
                if (File.Exists(processed)) File.Delete(processed);
                progress?.Invoke("Собираю перевод на исходном таймлайне…");
                await Core.Tool(ffmpeg, args, ct, null, 7200).ConfigureAwait(false);
                if (options.BurnSubtitles) {
                    var style = (options.SubtitleStyle ?? new WarmupSubtitleStyle()).Copy(); style.Enabled = true; style.Normalize();
                    // The shared raster track preserves configured fonts, plates and highlights, without 1000 inputs.
                    WarmupSubtitles.Rasterize(captions, style, work, media.Width, media.Height);
                    string track = captions.FirstOrDefault()?.TrackPath;
                    if (string.IsNullOrEmpty(track)) throw new Exception("Не удалось построить единую дорожку субтитров без пересечений.");
                    var subtitleArgs=new List<string> { "-v", "error", "-nostdin", "-y", "-filter_complex_threads", "1", "-i", processed, "-f", "concat", "-safe", "0", "-i", track,
                        "-filter_complex", "[1:v:0]format=rgba[sub];[0:v:0][sub]overlay=0:0:eof_action=pass[v]", "-map", "[v]", "-map", "0:a:0", "-c:v", "libx264", "-preset", "fast", "-crf", "22", "-pix_fmt", "yuv420p", "-c:a", "copy", "-t", Core.N(p.Duration), "-movflags", "+faststart", partial };
                    await ProcessingEngine.Accelerate(ffmpeg,subtitleArgs,media.Width,media.Height,ct,progress).ConfigureAwait(false);
                    await Core.Tool(ffmpeg,subtitleArgs,ct,null,7200).ConfigureAwait(false);
                } else File.Move(processed, partial);
                var result = Core.VideoInfo(await Core.Probe(ffprobe, partial, ct).ConfigureAwait(false));
                if (!result.HasAudio || Math.Abs(result.VideoDuration - p.Duration) > .15) throw new Exception("Готовый перевод не прошёл проверку длительности или звуковой дорожки.");
                ct.ThrowIfCancellationRequested();
                string output = Path.Combine(p.Directory, "translated.mp4");
                if (File.Exists(output)) File.Replace(partial, output, null); else File.Move(partial, output);
                p.Output = output; Save(p); progress?.Invoke("Готово: " + output);
            } finally { if (File.Exists(partial)) File.Delete(partial); if (File.Exists(processed)) File.Delete(processed); }
        }
        static List<SubtitleWord> SpeechWords(VideoTranslationSegment s, WarmupAlignment alignment, double duration, double timingScale) {
            var cues = WarmupSubtitles.Cues(alignment, duration / timingScale, 6);
            var words = cues.SelectMany(c => c.Words).ToList();
            foreach (var word in words) { word.Start = s.Start + word.Start * timingScale; word.End = Math.Min(s.End, s.Start + word.End * timingScale); }
            // Providers normalize punctuation/spacing. Keep their word timings when
            // the word count matches, while displaying the user's exact words.
            var text = s.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Count != text.Length) return null;
            for (int i = 0; i < words.Count; i++) words[i].Text = text[i];
            return words;
        }
        internal static void EnsureFits(VideoTranslationSegment s, long samples, long window) {
            if (samples > window) throw new Exception("Реплика " + s.Id + ": озвучка " + Core.N((double)samples / Rate) + " с не помещается в исходное окно " + Core.N((double)window / Rate) + " с. Сократите перевод этой реплики. Озвучка сохранена; следующие реплики не сдвинуты.");
        }
        static long Samples(double seconds) => (long)Math.Round(seconds * Rate, MidpointRounding.AwayFromZero);
        static void WriteWavHeader(Stream stream, long samples) {
            if (samples * 2 > uint.MaxValue - 36) throw new Exception("Звуковая дорожка превышает формат WAV.");
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, true)) {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write((uint)(36 + samples * 2)); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(Rate); writer.Write(Rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write((uint)(samples * 2));
            }
        }
        internal static string Srt(IEnumerable<VideoTranslationSegment> segments) {
            var text = new StringBuilder(); int index = 0;
            foreach (var s in segments) text.Append(++index).Append('\n').Append(Stamp(s.Start)).Append(" --> ").Append(Stamp(s.End)).Append('\n').Append(s.Text.Replace("\r", "").Replace("\n\n", "\n")).Append("\n\n");
            return text.ToString();
        }
        static string Stamp(double seconds) { var t = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000)); return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") + "," + t.Milliseconds.ToString("000"); }
        public sealed class SpeechReceipt { public string Key, AudioHash; public WarmupAlignment Alignment, NormalizedAlignment; }
    }
}
