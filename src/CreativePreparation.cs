using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class CreativePreparation {
        /// <summary>Fills only empty text fields, atomically after the whole analysis is valid.
        /// Existing/manual text is authoritative. Returned OCR/context can be shown separately.
        /// No audio synthesis occurs here. Paths may be absolute or relative to projectDirectory.
        /// </summary>
        public static async Task<CreativeAnalysisResult> AnalyzeAsync(CreativeProject project, string ffmpeg, string probe,
            string projectDirectory, string apiKey, string proxy, CancellationToken ct, Action<string> progress = null, bool replaceIntro = false) {
            ct.ThrowIfCancellationRequested();
            ValidateProject(project);
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Укажите API-ключ OpenRouter.", nameof(apiKey));
            await PrepareLanguageAsync(project,ffmpeg,probe,projectDirectory,ct,progress,apiKey,proxy).ConfigureAwait(false);
            string source = project.Source;
            var comments = project.Comments.ToArray();
            var images = comments.Select(c => c.ImagePath).ToArray();
            string introBefore = project.IntroText;
            var textsBefore = comments.Select(c => c.Text).ToArray();
            string original = Resolve(projectDirectory, source);
            RequireFile(original, "исходное видео");
            foreach (string image in images) RequireFile(Resolve(projectDirectory, image), "скриншот комментария");
            string work = Path.Combine(Path.GetFullPath(projectDirectory), "creative-analysis", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            CreativeAnalysisResult result;
            try {
                progress?.Invoke("Читаю параметры исходного видео…");
                var info = await Core.Probe(probe, original, ct).ConfigureAwait(false);
                var video = (info.streams ?? new ProbeStream[0]).FirstOrDefault(s => s.codec_type == "video" && (s.disposition == null || s.disposition.attached_pic != 1));
                if (video == null) throw new Exception("В исходнике нет видеодорожки.");
                double duration = Core.Parse(video.duration);
                if (duration <= 0) duration = Core.Parse(info.format?.duration);
                if (duration <= 0) throw new Exception("Не удалось определить длительность исходного видео.");
                var input = new CreativeAnalysisInput { Language = project.Preset.Language, SourceTitle = project.SourceTitle };
                // Read the opening, not an arbitrary mid-video thumbnail: first frame,
                // 1 s and 3 s (proportionally spaced for very short originals).
                double last = Math.Min(3, duration * .75);
                double[] samples = new[] { 0d, Math.Min(1, last / 2), last };
                progress?.Invoke("Извлекаю смысловые кадры начала видео…");
                for (int i = 0; i < samples.Length; i++) {
                    string frame = Path.Combine(work, "opening-" + i + ".png");
                    await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-ss", Core.N(samples[i]), "-i", original,
                        "-map", "0:" + video.index, "-frames:v", "1", "-vf", "scale=w='min(1600,iw)':h='min(1600,ih)':force_original_aspect_ratio=decrease", frame }, ct, null, 120).ConfigureAwait(false);
                    RequireFile(frame, "кадр начала видео");
                    input.Frames.Add(new CreativeVisionFrame { Seconds = samples[i], DataUrl = await Task.Run(() => ImageDataUrl(frame), ct).ConfigureAwait(false) });
                }
                if ((info.streams ?? new ProbeStream[0]).Any(s => s.codec_type == "audio")) {
                    progress?.Invoke("Распознаю речь исходника для контекста…");
                    string wav = Path.Combine(work, "source-speech.wav");
                    await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-i", original, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav }, ct, null, 600).ConfigureAwait(false);
                    // Only an explicit no_speech result is normal; runtime/decode/model failures still abort.
                    await LocalAudioTranscription.PrepareAsync(new[] { wav }, ct, progress, allowEmpty: true).ConfigureAwait(false);
                    var cues = LocalAudioTranscription.ContextCaptions(wav, duration);
                    input.OriginalSpeechWithTimestamps = string.Join("\n", cues.Select(c => "[" + Core.N(c.Start) + "–" + Core.N(c.End) + "] " + c.Text));
                    if (cues.Count == 0) progress?.Invoke("Речь не распознана; контекст будет взят только из надписей и кадров.");
                } else {progress?.Invoke("У исходника нет аудиодорожки; контекст будет взят из надписей и кадров.");}
                input.Language=project.IntroLanguage;
                progress?.Invoke("Язык вступления и комментариев: "+input.Language);
                for (int i = 0; i < images.Length; i++) {
                    ct.ThrowIfCancellationRequested();
                    string image = Resolve(projectDirectory, images[i]);
                    input.CommentImages.Add(await Task.Run(() => ImageDataUrl(image), ct).ConfigureAwait(false));
                }
                progress?.Invoke("Читаю надписи и три комментария; готовлю отдельное вступление…");
                using (var client = new CreativeAnalysisClient(apiKey, proxy))
                    result = await client.AnalyzeAsync(input, project.Preset.Model, ct).ConfigureAwait(false);
            } finally {
                // Cleanup errors are visible through existing diagnostics, without hiding the original failure.
                try { Directory.Delete(work, true); }
                catch (Exception ex) { AppDiagnostics.Write("Креатив", "", "analysis", "cleanup", "Не удалось удалить временные материалы: " + ex.GetType().Name); }
            }
            ct.ThrowIfCancellationRequested();
            if (project.Source != source || project.Comments.Count != 3 || project.IntroText != introBefore ||
                comments.Where((c, i) => !ReferenceEquals(project.Comments[i], c) || c.ImagePath != images[i] || c.Text != textsBefore[i]).Any())
                throw new Exception("Материалы или тексты изменены во время анализа. Результат не применён; ручные правки сохранены.");
            ApplyPreparedAnalysis(project, result, replaceIntro);
            progress?.Invoke("Тексты подготовлены. Можно исправить их вручную перед озвучкой.");
            return result;
        }
        public static async Task PrepareLanguageAsync(CreativeProject project,string ffmpeg,string probe,string directory,CancellationToken ct,Action<string> progress=null,string apiKey=null,string proxy=null,System.Net.Http.HttpClient httpClient=null) {
            if(!project.Preset.AutoIntroLanguage||project.Preset.TranslateTexts)return;
            await CreativeOutputTitle.LoadSourceTitleAsync(project,probe,ct,progress).ConfigureAwait(false);
            string source="title:"+project.Source+"\n"+project.SourceTitle;
            if(project.LanguageSource==source&&!string.IsNullOrWhiteSpace(project.DetectedLanguage))return;
            progress?.Invoke("Определяю язык по исходному заголовку…");
            string detected;
            using(var client=new CreativeAnalysisClient(apiKey,proxy,httpClient))detected=await client.DetectTitleLanguageAsync(project.SourceTitle,project.Preset.Model,ct).ConfigureAwait(false);
            if(string.IsNullOrWhiteSpace(detected))throw new Exception("По исходному заголовку язык определить нельзя. Выберите язык вручную в настройках креатива.");
            project.IntroAudio="";foreach(var c in project.Comments)c.AudioPath="";
            project.DetectedLanguage=detected;project.LanguageSource=source;
            progress?.Invoke("Язык вступления и комментариев по заголовку: "+detected);
        }
        internal static string TextSignature(CreativeProject project) {
            string text=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{language=project.IntroLanguage,texts=new[]{project.IntroText}.Concat(project.Comments.Select(c=>c.Text)).ToArray()});
            using(var sha=System.Security.Cryptography.SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }
        public static async Task TranslateTextsAsync(CreativeProject project,string apiKey,string proxy,CancellationToken ct,Action<string> progress=null,System.Net.Http.HttpClient httpClient=null) {
            if((!project.Preset.TranslateTexts&&!project.Preset.AutoIntroLanguage)||project.TranslatedTextSignature==TextSignature(project))return;
            string language=project.IntroLanguage;
            if(!System.Text.RegularExpressions.Regex.IsMatch(language??"",@"\A[a-z]{2,3}\z"))throw new Exception("Не определён язык вступления и комментариев.");
            string[] before=new[]{project.IntroText}.Concat(project.Comments.Select(c=>c.Text)).ToArray();
            progress?.Invoke("Перевожу вступление и комментарии на "+language+"…");
            string[] translated;
            using(var client=new CreativeAnalysisClient(apiKey,proxy,httpClient))translated=await client.TranslateAsync(before,language,project.Preset.Model,ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if(language!=project.IntroLanguage||!before.SequenceEqual(new[]{project.IntroText}.Concat(project.Comments.Select(c=>c.Text))))throw new Exception("Тексты изменились во время перевода. Перевод не применён.");
            project.IntroText=translated[0];project.IntroAudio="";
            for(int i=0;i<3;i++){project.Comments[i].Text=translated[i+1];project.Comments[i].AudioPath="";}
            project.TranslatedTextSignature=TextSignature(project);
        }
        internal static void ApplyPreparedAnalysis(CreativeProject project, CreativeAnalysisResult result, bool replaceIntro) {
            if (!replaceIntro) { ApplyAnalysis(project, result); return; }
            if (result.intro.status != "ready") throw new Exception("Недостаточно контекста для нового вступления. Существующий текст сохранён.");
            var updated = Store.Clone(project);
            updated.IntroText = result.intro.spoken_text; updated.IntroAudio = "";
            ApplyAnalysis(updated, result);
            project.IntroText = updated.IntroText; project.IntroAudio = "";
            for(int i=0;i<3;i++){project.Comments[i].Text=updated.Comments[i].Text;project.Comments[i].AudioPath=updated.Comments[i].AudioPath;}
        }
        internal static void ApplyAnalysis(CreativeProject project, CreativeAnalysisResult result) {
            // Client has validated schema/order; reject all actionable missing-text cases before any mutation.
            if (string.IsNullOrWhiteSpace(project.IntroText) && result.intro.status != "ready")
                throw new Exception("Недостаточно подтверждённого контекста для вступления. Введите вступительный текст вручную. Тексты не изменены.");
            for (int i = 0; i < 3; i++)
                if (string.IsNullOrWhiteSpace(project.Comments[i].Text) && result.comments[i].status != "ready")
                    throw new Exception("Комментарий " + (i + 1) + ": " +
                        (result.comments[i].status == "no_text" ? "картинка без текста." : result.comments[i].status == "ambiguous" ? "неоднозначный выбор на скриншоте." : "текст невозможно надёжно прочитать.") +
                        " Введите текст вручную или замените скриншот. Тексты не изменены.");
            if (string.IsNullOrWhiteSpace(project.IntroText)) { project.IntroText = result.intro.spoken_text; project.IntroAudio = ""; }
            for (int i = 0; i < 3; i++) if (string.IsNullOrWhiteSpace(project.Comments[i].Text)) {
                project.Comments[i].Text = result.comments[i].text;
                project.Comments[i].AudioPath = "";
            }
        }

        /// <summary>Up to four potentially billable requests, sequentially, with no automatic retries.
        /// Actual intro audio must be no longer than three seconds before any comments are synthesized.
        /// Uses current text; the editor invalidates bindings when text or voice changes.
        /// Production resumes missing slots and preserves completed speech on failure/cancellation.
        /// The explicit non-resume test boundary retains full regeneration semantics.
        /// Renderer must probe actual MP3 durations; alignment is saved for existing caption helpers.
        /// </summary>
        public static Task GenerateSpeechAsync(CreativeProject project, string projectDirectory, string apiKey, string proxy,
            CancellationToken ct, Action<string> progress = null) => GenerateSpeechCoreAsync(project, projectDirectory, apiKey, proxy, ct, progress, null, resume: true);

        internal static async Task GenerateSpeechCoreAsync(CreativeProject project, string projectDirectory, string apiKey, string proxy,
            CancellationToken ct, Action<string> progress, System.Net.Http.HttpClient httpClient,
            Func<string, CancellationToken, Task<double>> introDuration = null, bool resume = false) {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (!resume) {
                project.IntroAudio = "";
                if (project.Comments != null) foreach (var c in project.Comments) if (c != null) c.AudioPath = "";
            }
            ct.ThrowIfCancellationRequested();
            ValidateProject(project);
            var comments = project.Comments.ToArray();
            string[] texts = new[] { project.IntroText }.Concat(comments.Select(c => c.Text)).ToArray();
            for (int i = 0; i < texts.Length; i++) if (string.IsNullOrWhiteSpace(texts[i]))
                throw new Exception((i == 0 ? "Вступление" : "Комментарий " + i) + ": введите текст для озвучки.");
            var speech = project.Preset.Speech;
            if (speech == null || string.IsNullOrWhiteSpace(speech.VoiceId)) throw new Exception("Выберите голос ElevenLabs в оформлении креатива.");
            var settings = new ElevenLabsVoiceSettings {
                stability = speech.Stability, similarity_boost = speech.Similarity, style = speech.Style,
                speed = speech.Speed, use_speaker_boost = speech.SpeakerBoost
            };
            string voice = speech.VoiceId, model = speech.ModelId;
            if (string.IsNullOrWhiteSpace(model)) throw new Exception("Укажите модель ElevenLabs в настройках озвучки.");
            if (introDuration == null) {
                if (!ToolsLocator.TryResolve(out _, out string probe, out string hint)) throw new Exception(hint);
                introDuration = async (path, token) => Core.AudioDuration(await Core.Probe(probe, path, token).ConfigureAwait(false));
            }
            string folder = Path.Combine(Path.GetFullPath(projectDirectory), "creative-audio", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
            using (var client = new ElevenLabsClient(apiKey, proxy, httpClient)) {
                for (int i = 0; i < 4; i++) {
                    ct.ThrowIfCancellationRequested();
                    string existing = i == 0 ? project.IntroAudio : comments[i - 1].AudioPath;
                    if (resume && !string.IsNullOrWhiteSpace(existing) && File.Exists(existing)) {
                        if (i == 0) {
                            double seconds = await introDuration(existing, ct).ConfigureAwait(false);
                            if (seconds > 0 && seconds <= 3) { progress?.Invoke("Вступление уже озвучено; использую готовый файл."); continue; }
                            project.IntroAudio = "";
                            throw new Exception("Сохранённое вступление длится " + Core.N(seconds) + " с. Нажмите «Подготовить текст», чтобы заменить его коротким вступлением. Озвучки комментариев сохранены.");
                        }
                        progress?.Invoke("Комментарий " + i + " уже озвучен; использую готовый файл."); continue;
                    }
                    if (i == 0) project.IntroAudio = ""; else comments[i - 1].AudioPath = "";
                    progress?.Invoke("Создаю озвучку " + (i + 1) + "/4: " + (i == 0 ? "вступление" : "комментарий " + i) + "…");
                    string path = Path.Combine(folder, i == 0 ? "intro.mp3" : "comment-" + i + ".mp3");
                    // These are service-owned outputs only; never delete a user's imported audio.
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path + ".timing.json")) File.Delete(path + ".timing.json");
                    var generated = await client.GenerateAsync(texts[i], voice, settings, true, ct, model, i==0||project.Preset.AutoIntroLanguage||project.Preset.TranslateTexts?project.IntroLanguage:null).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (project.Comments.Count != 3 || project.IntroText != texts[0] || comments.Where((c, n) => !ReferenceEquals(project.Comments[n], c) || c.Text != texts[n + 1]).Any()) {
                        project.IntroAudio = "";
                        foreach (var c in project.Comments) if (c != null) c.AudioPath = "";
                        throw new Exception("Тексты изменены во время озвучки. Старое аудио отключено; запустите озвучку для текущих текстов.");
                    }
                    string partial = path + ".partial";
                    try {
                        File.WriteAllBytes(partial, generated.Audio);
                        AssemblySpeechPlan.SaveTiming(partial, texts[i], generated.SubtitleAlignment);
                        ct.ThrowIfCancellationRequested();
                        File.Move(partial, path);
                        File.Move(partial + ".timing.json", path + ".timing.json");
                    } finally {
                        if (File.Exists(partial)) File.Delete(partial);
                        if (File.Exists(partial + ".timing.json")) File.Delete(partial + ".timing.json");
                    }
                    if (i == 0) {
                        progress?.Invoke("Проверяю фактическую длительность вступления…");
                        double seconds = await introDuration(path, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
                            throw new Exception("Не удалось определить длительность озвучки вступления. Озвучка комментариев не запускалась.");
                        if (seconds > 3)
                            throw new Exception("Озвучка вступления длится " + seconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) +
                                " с при максимуме 3 с. Сократите текст вступления и запустите озвучку заново. Озвучка комментариев не запускалась; автоматического повтора нет.");
                        project.IntroAudio = path;
                    } else comments[i - 1].AudioPath = path;
                }
            }
            } catch {
                if (!resume) {
                    project.IntroAudio = "";
                    if (project.Comments != null) foreach (var c in project.Comments) if (c != null) c.AudioPath = "";
                }
                throw;
            }
            progress?.Invoke("Все четыре озвучки готовы; ранее созданные файлы сохранены.");
        }
        static void ValidateProject(CreativeProject project) {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (project.Preset == null) throw new Exception("Выберите оформление креатива.");
            if (project.Comments == null || project.Comments.Count != 3 || project.Comments.Any(c => c == null))
                throw new Exception("Для креатива нужны ровно три отдельных комментария.");
        }
        static string Resolve(string directory, string path) => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(directory, path));
        static void RequireFile(string path, string label) { if (!File.Exists(path)) throw new FileNotFoundException("Не найден " + label + ".", path); }
        static string ImageDataUrl(string path) {
            // PNG keeps screenshot lettering intact, and supports UI-imported BMP as well.
            using (var image = Image.FromFile(path)) using (var png = new MemoryStream()) {
                image.Save(png, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
            }
        }
    }
}
