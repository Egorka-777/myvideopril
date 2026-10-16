using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace VideoBatch {
    public sealed class WarmupVideoJob {
        public string Market = "RU";
        public string Prompt = "";
        public int Count = 10;
        public int MaxSeconds = 8;
        public string BackgroundsFolder = "";
        public string OutputFolder = "";
        public string OpenRouterKey = "";
        public string ElevenLabsKey = "";
        public string OpenRouterModel = "openai/gpt-4o-mini";
    }

    public static class WarmupVideoEngine {
        static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
        static readonly string[] VideoExt = { ".mp4", ".mov", ".mkv", ".webm" };

        public static async Task<IReadOnlyList<string>> GenerateAsync(WarmupVideoJob job, Action<string> log, CancellationToken ct) {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (string.IsNullOrWhiteSpace(job.OpenRouterKey)) throw new Exception("Укажите API-ключ OpenRouter в «Настройки».");
            if (string.IsNullOrWhiteSpace(job.ElevenLabsKey)) throw new Exception("Укажите API-ключ ElevenLabs в «Настройки».");
            if (string.IsNullOrWhiteSpace(job.Prompt)) throw new Exception("Напишите задачу для нейросети (о чём генерировать).");
            if (job.Count < 1 || job.Count > 100) throw new Exception("Количество роликов: от 1 до 100.");
            if (!ToolsLocator.TryResolve(out var ffmpeg, out _, out var hint)) throw new Exception(hint);

            Directory.CreateDirectory(job.OutputFolder);
            log("Запрашиваю тексты через OpenRouter…");
            var lines = await FetchTextsAsync(job, ct).ConfigureAwait(false);
            if (lines.Count == 0) throw new Exception("OpenRouter не вернул тексты.");
            log("Получено строк: " + lines.Count);

            log("Загружаю голоса ElevenLabs…");
            var voices = await FetchVoicesAsync(job.ElevenLabsKey, ct).ConfigureAwait(false);
            if (voices.Count == 0) throw new Exception("Не найдено голосов ElevenLabs. Проверьте ключ.");
            log("Голосов: " + voices.Count);

            var backgrounds = ListBackgrounds(job.BackgroundsFolder);
            if (backgrounds.Count == 0) log("Папка фона пуста — будет чёрный кадр 1080×1920.");
            var rnd = new Random();
            var outputs = new List<string>();
            string work = Path.Combine(Path.GetTempPath(), "VideoBatch-warmup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try {
                for (int i = 0; i < lines.Count; i++) {
                    ct.ThrowIfCancellationRequested();
                    string text = lines[i];
                    string voice = voices[rnd.Next(voices.Count)];
                    log("[" + (i + 1) + "/" + lines.Count + "] Озвучка…");
                    string mp3 = Path.Combine(work, "v" + i + ".mp3");
                    await SynthesizeAsync(job.ElevenLabsKey, voice, text, mp3, ct).ConfigureAwait(false);
                    string outFile = Path.Combine(job.OutputFolder, "warmup-" + job.Market + "-" + (i + 1).ToString("000") + ".mp4");
                    string bg = backgrounds.Count > 0 ? backgrounds[rnd.Next(backgrounds.Count)] : "";
                    RenderClip(ffmpeg, bg, mp3, outFile, job.MaxSeconds, work, i);
                    outputs.Add(outFile);
                    log("✓ " + Path.GetFileName(outFile));
                }
            } finally {
                try { Directory.Delete(work, true); } catch { }
            }
            return outputs;
        }

        static List<string> ListBackgrounds(string folder) {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return list;
            foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)) {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ImageExt.Contains(ext) || VideoExt.Contains(ext)) list.Add(f);
            }
            return list;
        }

        static async Task<List<string>> FetchTextsAsync(WarmupVideoJob job, CancellationToken ct) {
            string lang = job.Market == "EN" ? "English" : "Russian";
            string user = "Write exactly " + job.Count + " short lines in " + lang + " for vertical warmup videos (spoken aloud). "
                + "Topic: " + job.Prompt.Trim() + ". Each line max 220 characters. "
                + "Return ONLY a JSON array of strings, no markdown.";
            var payload = "{\"model\":\"" + JsonEscape(job.OpenRouterModel) + "\",\"messages\":[{\"role\":\"user\",\"content\":\"" + JsonEscape(user) + "\"}]}";
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) }) {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + job.OpenRouterKey);
                client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://videobatch.local");
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "VideoBatch");
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json")) {
                    var resp = await client.PostAsync("https://openrouter.ai/api/v1/chat/completions", content, ct).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new Exception("OpenRouter HTTP " + (int)resp.StatusCode + ": " + Trim(body, 300));
                    string text = ExtractAssistantContent(body);
                    return ParseLines(text, job.Count);
                }
            }
        }

        static string ExtractAssistantContent(string json) {
            var matches = Regex.Matches(json ?? "", "\"content\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (matches.Count == 0) throw new Exception("OpenRouter: не найден content в ответе.");
            return UnescapeJson(matches[matches.Count - 1].Groups[1].Value);
        }

        static List<string> ParseLines(string raw, int max) {
            raw = (raw ?? "").Trim();
            if (raw.StartsWith("```")) raw = Regex.Replace(raw, "^```[a-z]*\\n?", "", RegexOptions.IgnoreCase).TrimEnd('`', '\n', ' ');
            var list = new List<string>();
            try {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw)))
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(ms, new XmlDictionaryReaderQuotas())) {
                    var ser = new DataContractJsonSerializer(typeof(string[]));
                    if (ser.ReadObject(reader) is string[] arr)
                        list.AddRange(arr.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0));
                }
            } catch {
                foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                    var t = Regex.Replace(line, "^\\d+[.)\\s]+", "").Trim().Trim('"');
                    if (t.Length > 0) list.Add(t);
                }
            }
            if (list.Count > max) list = list.Take(max).ToList();
            return list;
        }

        static async Task<List<string>> FetchVoicesAsync(string key, CancellationToken ct) {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) }) {
                client.DefaultRequestHeaders.TryAddWithoutValidation("xi-api-key", key);
                var resp = await client.GetAsync("https://api.elevenlabs.io/v1/voices", ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new Exception("ElevenLabs HTTP " + (int)resp.StatusCode + ": " + Trim(body, 200));
                var ids = new List<string>();
                foreach (Match m in Regex.Matches(body, "\"voice_id\"\\s*:\\s*\"([^\"]+)\""))
                    ids.Add(m.Groups[1].Value);
                return ids.Distinct().ToList();
            }
        }

        static async Task SynthesizeAsync(string key, string voiceId, string text, string mp3Path, CancellationToken ct) {
            var payload = "{\"text\":\"" + JsonEscape(text) + "\",\"model_id\":\"eleven_multilingual_v2\"}";
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) }) {
                client.DefaultRequestHeaders.TryAddWithoutValidation("xi-api-key", key);
                using (var content = new StringContent(payload, Encoding.UTF8, "application/json")) {
                    var resp = await client.PostAsync("https://api.elevenlabs.io/v1/text-to-speech/" + voiceId + "?output_format=mp3_44100_128", content, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) {
                        string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new Exception("ElevenLabs TTS: " + (int)resp.StatusCode + " " + Trim(err, 120));
                    }
                    using (var fs = File.Create(mp3Path))
                        await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                }
            }
        }

        static void RenderClip(string ffmpeg, string background, string mp3, string output, int maxSec, string work, int index) {
            RunFfmpeg(ffmpeg, BuildFfmpegArgs(background, mp3, output, maxSec));
        }

        static string BuildFfmpegArgs(string background, string mp3, string output, int maxSec) {
            if (!string.IsNullOrWhiteSpace(background) && File.Exists(background) && VideoExt.Contains(Path.GetExtension(background).ToLowerInvariant()))
                return "-y -stream_loop -1 -i \"" + background + "\" -i \"" + mp3 + "\" -vf \"scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1\" -c:v libx264 -preset veryfast -crf 23 -c:a aac -shortest -t " + maxSec + " \"" + output + "\"";
            if (!string.IsNullOrWhiteSpace(background) && File.Exists(background))
                return "-y -loop 1 -i \"" + background + "\" -i \"" + mp3 + "\" -vf \"scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1\" -c:v libx264 -preset veryfast -crf 23 -c:a aac -shortest -t " + maxSec + " \"" + output + "\"";
            return "-y -f lavfi -i color=c=0x171717:s=1080x1920:r=30 -i \"" + mp3 + "\" -c:v libx264 -preset veryfast -crf 23 -c:a aac -shortest -t " + maxSec + " \"" + output + "\"";
        }

        static void RunFfmpeg(string ffmpeg, string args) {
            var psi = new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)) {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception("FFmpeg: " + Trim(err, 400));
            }
        }

        static string JsonEscape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        static string UnescapeJson(string s) => Regex.Unescape(s ?? "");
        static string Trim(string s, int max) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length <= max ? s : s.Substring(0, max) + "…"; }
    }
}
