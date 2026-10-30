using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace VideoBatch {
    public static class SubtitleUploadTests {
        static void Check(bool value, string name) { if (!value) throw new Exception(name); }
        static string Hash(string file) { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(file)) return BitConverter.ToString(hash.ComputeHash(stream)); }
        static List<WarmupCaption> Sample() => WarmupSubtitles.Cues(new WarmupAlignment {
            characters = new[] { "Твой ", "стиль ", "твои ", "слова" }, character_start_times_seconds = new[] { 0d, .7, 1.4, 2.1 }, character_end_times_seconds = new[] { .6, 1.3, 2d, 2.8 }
        }, 3, 4);
        public static bool RunSelfTests() {
            string folder = Path.Combine(Path.GetTempPath(), "vb-subtitle-style-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                var presets = SubtitlePresets.BuiltIn(); Check(presets.Count == 7 && presets.Select(p => p.Id).Distinct().Count() == 7, "preset catalog");
                var legacy = (WarmupSubtitleStyle)new XmlSerializer(typeof(WarmupSubtitleStyle)).Deserialize(new StringReader("<WarmupSubtitleStyle><FontSize>50</FontSize><Plate>none</Plate></WarmupSubtitleStyle>"));
                legacy.Normalize(); Check(legacy.Highlight == "none" && legacy.OutlineWidth == 0 && legacy.FontSize == 50, "old style migration");
                var style = presets.First(p => p.Id == "bold-green").Style.Copy(); string file = Path.Combine(folder, "presets.xml");
                var custom = SubtitlePresets.Save("Мой зелёный", style, file); style.AccentColor = "#FF0000";
                var saved = SubtitlePresets.Load(file).Single(p => p.Id == custom.Id);
                Check(saved.Style.AccentColor == "#32F56B" && saved.Style.Highlight == "active" && saved.Style.FontFamily == "Impact", "custom style copy and persistence");
                var cues = Sample(); Check(cues.Single().Words.Count == 4 && cues[0].Words[2].Start == 1.4, "provider word timestamps");
                var clipped = WarmupSubtitles.FitToDuration(cues, 1.8); Check(clipped[0].Words.Count == 3 && clipped[0].Words.Last().End == 1.8 && cues[0].Words.Count == 4, "timing trim copies words");
                var green = presets.First(p => p.Id == "bold-green").Style.Copy(); green.WordsPerCaption = 4;
                var frames = SubtitlePresets.Frames(cues, green); Check(frames.Count == 4 && frames[1].ActiveWord == 1 && frames[1].Start == .7, "word highlights use real time");
                Check(frames.First().Start == cues[0].Start && frames.Last().End == cues[0].End, "highlight duration unchanged");
                var pop = SubtitlePresets.Frames(cues, presets.First(p => p.Id == "word-pop").Style);
                Check(pop.Any(c => c.TextScale < 1) && pop.Any(c => c.TextScale == 1) && pop.All(c => c.Text.Split(' ').Length == 1), "single word pop frames");
                var manual = SubtitlePresets.Frames(new[] { new WarmupCaption { Text = "Ручная фраза", Start = 1, End = 2 } }, green);
                Check(manual.Count == 1 && manual[0].ActiveWord == -1, "manual captions do not invent word timing");
                foreach (var preset in presets) {
                    using (var image = WarmupSubtitles.Paint("ТВОЙ СТИЛЬ YOUR STYLE", preset.Style, 1080, 1920)) { Check(image.Width == 1080, "caption bitmap"); image.Save(Path.Combine(folder, preset.Id + ".png")); }
                }
                var rendered = WarmupSubtitles.Rasterize(Sample(), green, folder, 540, 960);
                using (var image = Image.FromFile(rendered[0].ImagePath)) Check(image.Size == new Size(540, 960), "export uses canvas dimensions");
                for (int i = 0; i < 100; i++) {
                    var profile = UploadPreparation.ProfileForUpload(ProcessingSettings.MildDefaults(), new Random(i), DateTime.Now, true);
                    var sample = profile.Processing;
                    Check(profile.Speed == 1 && profile.Crop == 0 && profile.Gray == 0 && sample.TrimStart == 0 && sample.TrimEnd == 0 && sample.Rotation == 0 && sample.FilmDust == 0 && sample.Noise == 0 && !sample.Mirror && sample.Zoom == 1 && sample.FrameScale == 1 && sample.Volume == 1 && sample.Pitch == 1 && sample.Tempo == 1, "publish profile excludes strong effects");
                    Check(Math.Abs(profile.Brightness) <= .001 && Math.Abs(profile.Contrast - 1) <= .001001, "publish color limits");
                    var media = new Media { Duration = 3, Width = 128, Height = 224, Fps = "25/1", VideoIndex = 0, AudioIndex = 1, HasAudio = true };
                    var args = UploadPreparation.Arguments("input.mp4", "output.mp4", profile, media, "test-edition", true);
                    string all = string.Join(" ", args); Check(!all.Contains("rotate=") && !all.Contains("noise=") && !all.Contains("hflip") && !all.Contains("zoompan") && !all.Contains("drawgrid"), "publish filter exclusions");
                    var copy = UploadPreparation.Arguments("input.mp4", "output.mp4", profile, media, "copy-edition", false);
                    Check(copy.Contains("copy") && !copy.Contains("-vf") && copy.Contains("edition_id=copy-edition"), "metadata-only remux");
                }
                return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }
        sealed class Callback : IProgress<UploadPreparationProgress> {
            readonly Action<UploadPreparationProgress> action;
            public Callback(Action<UploadPreparationProgress> value) { action = value; }
            public void Report(UploadPreparationProgress value) => action(value);
        }
        public static bool RunIntegrationTests(string ffmpeg, string ffprobe) => Integration(ffmpeg, ffprobe).GetAwaiter().GetResult();
        static async Task<bool> Integration(string ffmpeg, string ffprobe) {
            string folder = Path.Combine(Path.GetTempPath(), "vb-upload-preparation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                string source = Path.Combine(folder, "источник.mp4");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=128x224:rate=25:duration=3.2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3.2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata", "location=old-private-location", "-movflags", "+use_metadata_tags", source }, CancellationToken.None, null, 40);
                string original = Hash(source); var settings = new Preferences { UploadStagingFolder = Path.Combine(folder, "upload"), Processing = ProcessingSettings.MildDefaults() };
                var keys = new[] { "test-profile-a", "test-profile-b", "test-profile-c" }; int peak = 0; var seen = new Dictionary<string, int>(); var gate = new object();
                var progress = new Callback(state => { lock (gate) {
                    if (seen.TryGetValue(state.Key, out int previous)) Check(state.Percent >= previous, "monotonic progress"); seen[state.Key] = state.Percent;
                    peak = Math.Max(peak, keys.Count(key => UploadPreparation.State(key)?.Stage != null && UploadPreparation.State(key)?.Stage != "queued"));
                } });
                var results = await Task.WhenAll(keys.Select(key => UploadPreparation.PrepareAsync(key, "YouTube", key, new[] { source }, settings, progress)));
                Check(peak <= 2 && seen.Count == 3 && seen.Values.All(v => v == 100), "two-slot queue and completed progress");
                var outputs = results.SelectMany(x => x).ToList(); Check(outputs.Count == 3 && outputs.Select(Hash).Distinct().Count() == 3 && Hash(source) == original, "per-account editions preserve source");
                foreach (var output in outputs) {
                    var raw = await Core.Probe(ffprobe, output, CancellationToken.None); var media = Core.VideoInfo(raw);
                    Check(media.Width == 128 && media.Height == 224 && Math.Abs(media.Duration - 3.2) < .15 && media.HasAudio, "output media contract");
                    Check(raw.format.tags.ContainsKey("edition_id") && !raw.format.tags.ContainsKey("location"), "rewritten embedded metadata");
                }
                settings.UploadPreparationSubtleEffects = false;
                string copy = (await UploadPreparation.PrepareAsync("copy", "TikTok", "copy", new[] { source }, settings, null))[0];
                var sourceHash = await Core.Tool(ffmpeg, new[] { "-v", "error", "-i", source, "-map", "0:v:0", "-c", "copy", "-f", "hash", "-hash", "sha256", "pipe:1" }, CancellationToken.None, null, 30);
                var copyHash = await Core.Tool(ffmpeg, new[] { "-v", "error", "-i", copy, "-map", "0:v:0", "-c", "copy", "-f", "hash", "-hash", "sha256", "pipe:1" }, CancellationToken.None, null, 30);
                Check(sourceHash == copyHash, "metadata-only encoded video stays identical");
                bool failed = false;
                try { await UploadPreparation.PrepareAsync("bad-batch", "YouTube", "bad", new[] { source, Path.Combine(folder, "missing.mp4") }, settings, null); } catch { failed = true; }
                string bad = Path.Combine(settings.UploadStagingFolder, "prepared", "YouTube", "bad");
                Check(failed && !UploadPreparation.IsBusy("bad-batch") && (!Directory.Exists(bad) || Directory.GetFiles(bad).Length == 0), "failed batch cleanup");
                using (var ct = new CancellationTokenSource()) {
                    ct.Cancel(); bool cancelled = false;
                    try { await UploadPreparation.PrepareAsync("cancelled", "TikTok", "cancelled", new[] { source }, settings, null, ct.Token); } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && !UploadPreparation.IsBusy("cancelled"), "cancellation releases queue");
                }
                return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }
        public static bool RunUiTests(string folder) {
            string oldRoot = Store.Root, temp = Path.Combine(Path.GetTempPath(), "vb-subtitle-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp); Directory.CreateDirectory(folder);
            try {
                Store.Root = temp;
                using (var background = new Bitmap(1080, 1920)) {
                    using (var g = Graphics.FromImage(background)) { g.Clear(Color.FromArgb(20, 35, 45)); using (var brush = new SolidBrush(Color.FromArgb(35, 100, 80))) g.FillEllipse(brush, 80, 300, 920, 920); }
                    foreach (var preset in SubtitlePresets.BuiltIn()) using (var dialog = new SubtitleStyleDialog(preset.Style, Sample(), background)) {
                        dialog.Show(); Application.DoEvents();
                        using (var image = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(folder, preset.Id + ".png")); }
                        Check(dialog.Controls.Count > 0 && dialog.Style.PresetId == preset.Id, "dialog selected style"); dialog.Close();
                    }
                }
                return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
            finally { Store.Root = oldRoot; try { Directory.Delete(temp, true); } catch { } }
        }
    }
}
