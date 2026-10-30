using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class UploadPreparationProgress {
        public string Key, Stage, File;
        public int Percent, Index, Total;
        public string Caption => Stage == "queued" ? "Подготовка · в очереди" : "Подготовка · " + Percent + "% · " + Index + "/" + Total;
    }

    // One shared queue across YouTube/TikTok. No UI access, no changes to source files.
    public static class UploadPreparation {
        static readonly SemaphoreSlim slots = new SemaphoreSlim(2, 2);
        static readonly object sync = new object();
        static readonly Dictionary<string, CancellationTokenSource> jobs = new Dictionary<string, CancellationTokenSource>();
        static readonly Dictionary<string, UploadPreparationProgress> states = new Dictionary<string, UploadPreparationProgress>();
        public static string Key(string platform, string account, string market) => platform + "|" + market + "|" + account;
        public static string YouTubeKey(YouTubeChannel channel) => Key("YouTube", string.IsNullOrWhiteSpace(channel.ChannelId) ? channel.ProfileId : channel.ChannelId, channel.Market);
        public static string TikTokKey(TikTokAccount account) => Key("TikTok", account.ProfileId, account.Market);
        public static void DeleteOutputs(IEnumerable<string> files) { foreach (string file in files ?? Enumerable.Empty<string>()) try { if (File.Exists(file)) File.Delete(file); } catch { } }
        public static bool IsBusy(string key) { lock (sync) return jobs.ContainsKey(key); }
        public static UploadPreparationProgress State(string key) { lock (sync) { states.TryGetValue(key, out UploadPreparationProgress state); return state; } }
        public static void Cancel(string key) { lock (sync) if (jobs.TryGetValue(key, out CancellationTokenSource job)) job.Cancel(); }
        public static void RequireReady(string key) { if (IsBusy(key)) throw new Exception("Для выбранного аккаунта ещё идёт подготовка видео. Дождитесь завершения или отмените её."); }
        public static void AddSettingsMenu(ContextMenuStrip menu, Preferences settings) {
            var item = new ToolStripMenuItem("Едва заметная цветокоррекция") { CheckOnClick = true, Checked = settings.UploadPreparationSubtleEffects };
            item.CheckedChanged += (s, e) => { settings.UploadPreparationSubtleEffects = item.Checked; Store.Save(settings); }; menu.Items.Add(item);
            menu.Opening += (s, e) => item.Checked = settings.UploadPreparationSubtleEffects;
        }
        public static void ToggleEffects(IWin32Window owner, Preferences settings) {
            var result = MessageBox.Show(owner, "Добавлять едва заметную цветокоррекцию при подготовке?\n\nДа — минимальная коррекция цвета.\nНет — только метаданные, без перекодирования.\nПоворот, обрезка, пыль и шум не используются.", "Подготовка видео", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (result == DialogResult.Cancel) return; settings.UploadPreparationSubtleEffects = result == DialogResult.Yes; Store.Save(settings);
        }

        public static async Task<string[]> PrepareAsync(string key, string platform, string account, string[] files, Preferences settings, IProgress<UploadPreparationProgress> progress, CancellationToken cancellation = default(CancellationToken)) {
            var sources = (files ?? new string[0]).Select(Path.GetFullPath).ToArray();
            if (sources.Length == 0) throw new Exception("Выберите видео для подготовки.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lock (sync) {
                if (jobs.ContainsKey(key)) { linked.Dispose(); throw new Exception("Видео этого аккаунта уже готовятся."); }
                jobs.Add(key, linked);
            }
            bool acquired = false, completed = false;
            var outputs = new List<string>(); string partial = null;
            try {
                Report(key, "queued", 0, 0, sources.Length, "", progress);
                await slots.WaitAsync(linked.Token).ConfigureAwait(false); acquired = true;
                return await Task.Run(async () => {
                    if (!ToolsLocator.TryResolve(out string ffmpeg, out string ffprobe, out string reason)) throw new Exception(reason);
                    string folder = Path.Combine(UploadStaging.Root(settings), "prepared", UploadStaging.SanitizeDirName(platform), UploadStaging.SanitizeDirName(account));
                    Directory.CreateDirectory(folder);
                    var random = new Random(Guid.NewGuid().GetHashCode());
                    var metadata = settings.Processing ?? new ProcessingSettings();
                    for (int i = 0; i < sources.Length; i++) {
                        linked.Token.ThrowIfCancellationRequested(); string source = sources[i];
                        UploadStaging.ValidateReady(source, "Подготовка видео");
                        Report(key, "probe", i * 100 / sources.Length, i + 1, sources.Length, source, progress);
                        var before = await Core.Probe(ffprobe, source, linked.Token).ConfigureAwait(false);
                        var media = Describe(before);
                        bool effects = settings.UploadPreparationSubtleEffects && !VideoSdr.IsHdr(before);
                        string edition = Guid.NewGuid().ToString("N"), output = Path.Combine(folder, "video-" + edition + ".mp4");
                        partial = Path.Combine(folder, ".preparing-" + edition + ".mp4");
                        var profile = ProfileForUpload(metadata, random, DateTime.Now, effects);
                        var args = Arguments(source, partial, profile, media, edition, effects);
                        int index = i, last = -1;
                        await Core.Tool(ffmpeg, args, linked.Token, line => {
                            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                            double elapsed = Core.Parse(line.Substring(12)) / 1000000;
                            int percent = Math.Min(99, (int)(100 * (index + Math.Max(0, Math.Min(.99, elapsed / media.Duration))) / sources.Length));
                            if (percent == last) return; last = percent;
                            Report(key, "processing", percent, index + 1, sources.Length, source, progress);
                        }, Math.Max(120, (int)Math.Min(7200, media.Duration * 12))).ConfigureAwait(false);
                        var after = await Core.Probe(ffprobe, partial, linked.Token).ConfigureAwait(false);
                        var result = Describe(after);
                        if (Math.Abs(result.Duration - media.Duration) > .15 || result.Width != media.Width || result.Height != media.Height || result.HasAudio != media.HasAudio)
                            throw new Exception("После подготовки изменились длительность, размер или звуковая дорожка. Исходник сохранён.");
                        VideoMetadata.Verify(after, profile.Processing, null);
                        if (after.format?.tags == null || !after.format.tags.TryGetValue("edition_id", out string stored) || stored != edition)
                            throw new Exception("Не удалось сохранить метаданные подготовленного видео.");
                        linked.Token.ThrowIfCancellationRequested(); File.Move(partial, output); partial = null; outputs.Add(output);
                    }
                    Report(key, "ready", 100, sources.Length, sources.Length, sources.Last(), progress); completed = true; return outputs.ToArray();
                }, linked.Token).ConfigureAwait(false);
            } finally {
                try { if (partial != null && File.Exists(partial)) File.Delete(partial); } catch { }
                if (!completed) foreach (string file in outputs) { try { File.Delete(file); } catch { } }
                if (acquired) slots.Release();
                lock (sync) { jobs.Remove(key); states.Remove(key); } linked.Dispose();
            }
        }
        static void Report(string key, string stage, int percent, int index, int total, string file, IProgress<UploadPreparationProgress> target) {
            var state = new UploadPreparationProgress { Key = key, Stage = stage, Percent = percent, Index = index, Total = total, File = Path.GetFileName(file) };
            lock (sync) states[key] = state; target?.Report(state);
        }
        static Media Describe(ProbeResult raw) {
            // HDR is remuxed unchanged; the shared SDR-only descriptor is used just for dimensions/timing.
            var stream = raw.streams.First(s => s.codec_type == "video" && (s.disposition == null || s.disposition.attached_pic != 1));
            string transfer = stream.color_transfer;
            try { stream.color_transfer = null; return Core.VideoInfo(raw); } finally { stream.color_transfer = transfer; }
        }
        public static Profile ProfileForUpload(ProcessingSettings source, Random random, DateTime now, bool effects) {
            source = source ?? new ProcessingSettings();
            // Reuse metadata templates only. Strong Processing-tab effects are never inherited.
            var settings = new ProcessingSettings { DeviceMetadata = source.DeviceMetadata, Brands = source.Brands == null ? (string[])ProcessingDevices.BrandNames.Clone() : (string[])source.Brands.Clone(),
                CaptureDateEnabled = true, CaptureDaysBack = 0, RandomizeCaptureTime = false, MetadataSoftware = "VideoBatch" };
            var profile = new Profile { Format = "mp4", Metadata = "clear", Compression = "quality", Crf = 18, FileDate = "now" };
            ProcessingEngine.Apply(profile, settings, new Media { Duration = 1, Width = 1080, Height = 1920 }, random, now);
            if (effects) { profile.Brightness = (random.NextDouble() - .5) * .002; profile.Contrast = .999 + random.NextDouble() * .002; }
            return profile;
        }
        public static List<string> Arguments(string source, string output, Profile profile, Media media, string edition, bool effects) {
            List<string> args;
            if (effects) {
                args = ProcessingEngine.Arguments(source, output, profile, media, null);
                int preset = args.IndexOf("-preset"); if (preset >= 0) args[preset + 1] = "veryfast";
                args.InsertRange(args.Count - 1, new[] { "-threads", "2", "-filter_threads", "1", "-filter_complex_threads", "1" });
            } else {
                args = new List<string> { "-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-stats_period", "0.3", "-progress", "pipe:1", "-i", source,
                    "-map", "0:" + media.VideoIndex, "-c", "copy", "-map_metadata", "-1", "-map_metadata:s:v", "-1", "-map_metadata:s:a", "-1", "-map_chapters", "-1" };
                if (media.HasAudio) args.AddRange(new[] { "-map", "0:" + media.AudioIndex });
                VideoMetadata.Add(args, profile.Processing, media.HasAudio);
                args.AddRange(new[] { "-movflags", "+faststart+use_metadata_tags", output });
            }
            args.InsertRange(args.Count - 1, new[] { "-metadata", "edition_id=" + edition, "-metadata", "comment=Prepared by VideoBatch" });
            return args;
        }
        public static bool PaintProgress(DataGridViewCellPaintingEventArgs e, string key) {
            var state = State(key); if (state == null) return false;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
            var box = e.CellBounds; box.Inflate(-6, -5);
            TextRenderer.DrawText(e.Graphics, state.Caption, Theme.FontSmall, new Rectangle(box.X, box.Y, box.Width, Math.Max(18, box.Height - 8)), Theme.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            var bar = new Rectangle(box.X, box.Bottom - 4, box.Width, 4);
            using (var brush = new SolidBrush(Theme.Border)) e.Graphics.FillRectangle(brush, bar);
            using (var brush = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(brush, new Rectangle(bar.X, bar.Y, Math.Max(3, bar.Width * state.Percent / 100), bar.Height));
            e.Handled = true; return true;
        }
    }
}
