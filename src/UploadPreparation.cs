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

    // One shared queue across YouTube/TikTok. Callers rename picked files in place before PrepareAsync when needed.
    public static class UploadPreparation {
        static readonly SemaphoreSlim slots = new SemaphoreSlim(2, 2);
        static readonly object sync = new object();
        static readonly Dictionary<string, CancellationTokenSource> jobs = new Dictionary<string, CancellationTokenSource>();
        static readonly Dictionary<string, UploadPreparationProgress> states = new Dictionary<string, UploadPreparationProgress>();
        public static string Key(string platform, string account, string market) => platform + "|" + market + "|" + account;
        public static string DatedFolder(Preferences settings, string stage, string platform, string account, DateTime added) =>
            Path.Combine(UploadStaging.Root(settings), stage, UploadStaging.SanitizeDirName(platform), added.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), UploadStaging.SanitizeDirName(account));
        public static string YouTubeKey(YouTubeChannel channel) => Key("YouTube", string.IsNullOrWhiteSpace(channel.ChannelId) ? channel.ProfileId : channel.ChannelId, channel.Market);
        public static string TikTokKey(TikTokAccount account) => Key("TikTok", account.ProfileId, account.Market);
        public static void DeleteOutputs(IEnumerable<string> files) { foreach (string file in files ?? Enumerable.Empty<string>()) try { if (File.Exists(file)) File.Delete(file); } catch { } }
        public sealed class ClaimedSource {
            public string OriginalDirectory, ClaimedPath, FileName;
        }
        /// <summary>Moves picked files out of the shared folder immediately, before preparation finishes.</summary>
        public static List<ClaimedSource> ClaimFromSharedFolder(IEnumerable<string> sources, string claimFolder) {
            if (string.IsNullOrWhiteSpace(claimFolder)) throw new Exception("Не задана папка для забранных исходников.");
            Directory.CreateDirectory(claimFolder);
            var claimed = new List<ClaimedSource>();
            var failed = new List<string>();
            foreach (string file in (sources ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase)) {
                string path = FullPathSafe(file);
                if (path == "" || !File.Exists(path)) { failed.Add(Path.GetFileName(file ?? "") + " (не найден)"); continue; }
                string origin = Path.GetDirectoryName(path) ?? "";
                Exception last = null;
                string dest = null;
                for (int attempt = 0; attempt < 8; attempt++) {
                    try {
                        dest = AssemblyFiles.SafeImportDestination(claimFolder, path);
                        // Reserve a name for both files: an older transcript must not block this claim.
                        while (File.Exists(Path.ChangeExtension(dest, ".txt")))
                            dest = Path.Combine(claimFolder, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N").Substring(0,8) + Path.GetExtension(path));
                        File.Move(path, dest);
                        claimed.Add(new ClaimedSource { OriginalDirectory = origin, ClaimedPath = dest, FileName = Path.GetFileName(path) });
                        string transcript = Path.ChangeExtension(path, ".txt");
                        if (File.Exists(transcript)) {
                            try { File.Move(transcript, Path.ChangeExtension(dest, ".txt")); }
                            catch { File.Move(dest, path); claimed.RemoveAt(claimed.Count - 1); throw; }
                        }
                        last = null; break;
                    } catch (Exception ex) {
                        last = ex;
                        Thread.Sleep(80 + attempt * 70);
                    }
                }
                if (last != null) failed.Add(Path.GetFileName(path) + " (" + last.Message + ")");
            }
            if (failed.Count > 0) {
                RestoreClaimed(claimed);
                throw new Exception("Не удалось сразу убрать исходники из общей папки: " + string.Join("; ", failed));
            }
            return claimed;
        }
        public static void RestoreClaimed(IEnumerable<ClaimedSource> claimed) {
            foreach (var item in claimed ?? Enumerable.Empty<ClaimedSource>()) {
                if (item == null || string.IsNullOrWhiteSpace(item.ClaimedPath) || !File.Exists(item.ClaimedPath)) continue;
                try {
                    string folder = string.IsNullOrWhiteSpace(item.OriginalDirectory) ? Path.GetDirectoryName(item.ClaimedPath) : item.OriginalDirectory;
                    Directory.CreateDirectory(folder);
                    string dest = AssemblyFiles.SafeImportDestination(folder, Path.Combine(folder, item.FileName));
                    while (File.Exists(Path.ChangeExtension(dest, ".txt")))
                        dest = Path.Combine(folder, Path.GetFileNameWithoutExtension(item.FileName) + "-" + Guid.NewGuid().ToString("N").Substring(0,8) + Path.GetExtension(item.FileName));
                    File.Move(item.ClaimedPath, dest);
                    string transcript = Path.ChangeExtension(item.ClaimedPath, ".txt");
                    if (File.Exists(transcript)) File.Move(transcript, Path.ChangeExtension(dest, ".txt"));
                } catch (Exception ex) { AppDiagnostics.Write("Подготовка", "", "", "restore_source_error", "Не удалось вернуть исходник: " + item.ClaimedPath + " · " + ex.Message, "error"); }
            }
        }
        public static void DiscardClaimed(IEnumerable<ClaimedSource> claimed) {
            foreach (var item in claimed ?? Enumerable.Empty<ClaimedSource>()) {
                if (item == null || string.IsNullOrWhiteSpace(item.ClaimedPath)) continue;
                try {
                    if (File.Exists(item.ClaimedPath)) File.Delete(item.ClaimedPath);
                    string transcript = Path.ChangeExtension(item.ClaimedPath, ".txt");
                    if (File.Exists(transcript)) File.Delete(transcript);
                } catch { }
            }
        }
        public sealed class ConsumeResult {
            public int Removed;
            public readonly List<string> KeptAssigned = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public string Summary {
                get {
                    if (Failed.Count > 0) return "убрано " + Removed + ", не удалось: " + string.Join("; ", Failed);
                    if (KeptAssigned.Count > 0) return "убрано " + Removed + ", оставлены ещё назначенные: " + KeptAssigned.Count;
                    return "убрано " + Removed;
                }
            }
        }
        /// <summary>Moves taken originals out of the shared folder after a prepared copy is assigned. Keeps any path still referenced elsewhere.</summary>
        public static int ConsumeSources(IEnumerable<string> sources, IEnumerable<string> keep, Func<string, bool> stillReferenced = null, string takenRoot = null) {
            return ConsumeSourcesDetailed(sources, keep, stillReferenced, takenRoot).Removed;
        }
        public static ConsumeResult ConsumeSourcesDetailed(IEnumerable<string> sources, IEnumerable<string> keep, Func<string, bool> stillReferenced = null, string takenRoot = null) {
            var preserve = new HashSet<string>((keep ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(FullPathSafe).Where(p => p != ""), StringComparer.OrdinalIgnoreCase);
            var result = new ConsumeResult();
            string sink = string.IsNullOrWhiteSpace(takenRoot) ? Path.Combine(Path.GetTempPath(), "VideoBatch-taken") : takenRoot;
            foreach (string file in (sources ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase)) {
                if (string.IsNullOrWhiteSpace(file)) continue;
                string path = FullPathSafe(file);
                if (path == "" || preserve.Contains(path)) continue;
                if (stillReferenced != null && stillReferenced(path)) { result.KeptAssigned.Add(Path.GetFileName(path)); continue; }
                if (!File.Exists(path)) { result.Removed++; continue; }
                Exception last = null;
                for (int attempt = 0; attempt < 8; attempt++) {
                    try {
                        Directory.CreateDirectory(sink);
                        string dest = Path.Combine(sink, Path.GetFileName(path));
                        dest = AssemblyFiles.SafeImportDestination(sink, dest);
                        File.Move(path, dest);
                        try { File.Delete(dest); } catch { /* moved out of the shared folder is enough */ }
                        result.Removed++; last = null; break;
                    } catch (Exception ex) {
                        last = ex;
                        try { File.Delete(path); result.Removed++; last = null; break; } catch (Exception del) { last = del; }
                        Thread.Sleep(80 + attempt * 70);
                    }
                }
                if (last != null && File.Exists(path)) result.Failed.Add(Path.GetFileName(path) + " (" + last.Message + ")");
            }
            return result;
        }
        static string FullPathSafe(string path) {
            try { return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path); } catch { return ""; }
        }
        public static bool IsAssignedVideo(Preferences prefs, string path) {
            if (prefs == null || string.IsNullOrWhiteSpace(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }
            foreach (var channel in prefs.YouTubeChannels ?? new List<YouTubeChannel>()) {
                if (SamePath(channel.Video, full)) return true;
                foreach (var item in channel.Items ?? new List<YouTubeItem>()) if (item != null && SamePath(item.Video, full)) return true;
            }
            foreach (var account in prefs.TikTokAccounts ?? new List<TikTokAccount>())
                foreach (var item in account.Items ?? new List<TikTokItem>()) if (item != null && SamePath(item.Video, full)) return true;
            return false;
        }
        public static bool IsAssignedVideo(string path) => IsAssignedVideo(Store.Load(), path);
        static bool SamePath(string left, string right) {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
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
            DateTime added = DateTime.Now;
            if (sources.Length > 0) {
                string sourceDate = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(sources[0])));
                if (DateTime.TryParseExact(sourceDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime claimedDate)) added = claimedDate;
            }
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
                    if (!ToolsLocator.TryResolve(out string ffmpeg, out string ffprobe, out _)) throw new Exception("Для подготовки видео нужны ffmpeg.exe и ffprobe.exe в папке tools полного комплекта VideoBatch. Запустите приложение из папки установки. Исходники не изменены.");
                    string folder = DatedFolder(settings, "prepared", platform, account, added);
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
                        if(effects)await ProcessingEngine.Accelerate(ffmpeg,args,media.Width,media.Height,linked.Token).ConfigureAwait(false);
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
                        linked.Token.ThrowIfCancellationRequested();
                        string transcript = Path.ChangeExtension(source,".txt");
                        if (File.Exists(transcript)) File.Copy(transcript,Path.ChangeExtension(output,".txt"),false);
                        File.Move(partial, output); partial = null; outputs.Add(output);
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
