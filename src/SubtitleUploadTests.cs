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
                Check(!AssemblySpeechPlan.SubtitlesEnabled(new AssemblyTemplate()), "assembly skips subtitles when disabled");
                var on = new AssemblyTemplate(); on.SubtitleStyle.Enabled = true;
                Check(AssemblySpeechPlan.SubtitlesEnabled(on), "assembly subtitles flag");
                CheckRandomStyles(folder);
                var presets = SubtitlePresets.BuiltIn(); Check(presets.Count == 10 && presets.Select(p => p.Id).Distinct().Count() == 10, "preset catalog");
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
                foreach (string motion in new[] { "fade", "rise", "word-accent" }) {
                    var animated = green.Copy(); animated.Motion = motion; animated.Normalize();
                    Check(animated.Motion == motion, "animation normalization");
                    var sequence = SubtitlePresets.Frames(cues, animated);
                    Check(sequence.First().Start == cues[0].Start && sequence.Last().End == cues[0].End && sequence.Zip(sequence.Skip(1), (a, b) => a.End == b.Start).All(v => v), "animation continuous timing");
                    var first = SubtitlePresets.FrameAt(cues[0], animated, .02);
                    var middle = SubtitlePresets.FrameAt(cues[0], animated, .4);
                    var last = SubtitlePresets.FrameAt(cues[0], animated, cues[0].End - .02);
                    if (motion != "word-accent") Check(first.FrameOpacity < middle.FrameOpacity && last.FrameOpacity < middle.FrameOpacity, "fade both edges");
                    if (motion == "rise") Check(first.OffsetY > 0 && middle.OffsetY == 0, "rise settles");
                    if (motion == "word-accent") Check(first.ActiveWordScale > 1 && middle.ActiveWordScale == 1 && first.TextScale == 1 && sequence.Any(c => c.ActiveWord == 2 && c.ActiveWordScale > 1), "accent follows words without line scaling");
                    using (var a = SubtitleTypography.Paint(first, animated, 540, 960))
                    using (var b = SubtitleTypography.Paint(middle, animated, 540, 960)) {
                        Check(a.Size == b.Size, "animated raster size");
                        if (motion != "word-accent") {
                            long alphaA = 0, alphaB = 0;
                            for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) { alphaA += a.GetPixel(x, y).A; alphaB += b.GetPixel(x, y).A; }
                            Check(alphaA > 0 && alphaA < alphaB, "animated pixels fade");
                        }
                    }
                }
                var manual = SubtitlePresets.Frames(new[] { new WarmupCaption { Text = "Ручная фраза", Start = 1, End = 2 } }, green);
                Check(manual.Count == 1 && manual[0].ActiveWord == -1, "manual captions do not invent word timing");
                foreach (var preset in presets) {
                    using (var image = WarmupSubtitles.Paint("ТВОЙ СТИЛЬ YOUR STYLE", preset.Style, 1080, 1920)) { Check(image.Width == 1080, "caption bitmap"); image.Save(Path.Combine(folder, preset.Id + ".png")); }
                }
                var rendered = WarmupSubtitles.Rasterize(Sample(), green, folder, 540, 960);
                using (var image = Image.FromFile(rendered[0].ImagePath)) Check(image.Size == new Size(540, 960), "export uses canvas dimensions");
                Check(File.Exists(rendered[0].TrackPath), "one subtitle track instead of many live decoders");
                for (int i = 0; i < 100; i++) {
                    var profile = UploadPreparation.ProfileForUpload(ProcessingSettings.MildDefaults(), new Random(i), DateTime.Now, true);
                    var sample = profile.Processing;
                    Check(profile.Speed == 1 && profile.Crop == 0 && profile.Gray == 0 && sample.TrimStart == 0 && sample.TrimEnd == 0 && sample.Rotation == 0 && sample.FilmDust == 0 && sample.Noise == 0 && !sample.Mirror && sample.Zoom == 1 && sample.FrameScale == 1 && sample.Volume == 1 && sample.Pitch == 1 && sample.Tempo == 1, "publish profile excludes strong effects");
                    Check(Math.Abs(profile.Brightness) <= .001 && Math.Abs(profile.Contrast - 1) <= .001001, "publish color limits");
                    var media = new Media { Duration = 3, Width = 128, Height = 224, Fps = "25/1", VideoIndex = 0, AudioIndex = 1, HasAudio = true };
                    var args = UploadPreparation.Arguments("input.mp4", "output.mp4", profile, media, "test-edition", true);
                    string all = args[args.IndexOf("-vf") + 1]; Check(!all.Contains("rotate=") && !all.Contains("noise=") && !all.Contains("hflip") && !all.Contains("zoompan") && !all.Contains("drawgrid"), "publish filter exclusions");
                    var copy = UploadPreparation.Arguments("input.mp4", "output.mp4", profile, media, "copy-edition", false);
                    Check(copy.Contains("copy") && !copy.Contains("-vf") && copy.Contains("edition_id=copy-edition"), "metadata-only remux");
                }
                Console.WriteLine("Subtitle presets PASS: legacy XML, ten styles, fade/rise/word-accent animation, custom persistence, word timing, shared renderer and strict effect limits.");
                return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }
        static void CheckRandomStyles(string folder) {
            var source = new WarmupSubtitleStyle(); source.RandomStyle.Enabled = true;
            var random = new Random(42); var combinations = new HashSet<string>();
            for (int i = 0; i < 100; i++) {
                var style = SubtitleRandomSettings.ForVideo(source, random); var o = source.RandomStyle;
                Check(style.TextThickness >= o.ThicknessMin && style.TextThickness <= o.ThicknessMax && style.PlatePaddingX >= o.PaddingXMin && style.PlatePaddingX <= o.PaddingXMax && style.PlatePaddingY >= o.PaddingYMin && style.PlatePaddingY <= o.PaddingYMax, "random thickness and padding ranges");
                Check(style.FontSize >= o.SizeMin && style.FontSize <= o.SizeMax && style.Position >= o.PositionMin && style.Position <= o.PositionMax, "random size and position ranges");
                Check(style.WordsPerCaption >= o.WordsMin && style.WordsPerCaption <= o.WordsMax && style.OutlineWidth >= o.OutlineMin && style.OutlineWidth <= o.OutlineMax && style.Opacity >= o.OpacityMin && style.Opacity <= o.OpacityMax, "random caption ranges");
                Check(o.Fonts.Contains(style.FontFamily) && o.Colors.Contains(style.TextColor) && o.Motions.Contains(style.Motion) && o.Plates.Contains(style.Plate) && o.Highlights.Contains(style.Highlight), "random allowed choices");
                Check(!style.RandomStyle.Enabled && source.RandomStyle.Enabled && source.FontSize == 56, "random result does not mutate saved settings");
                combinations.Add(style.FontFamily + style.FontSize + style.Position + style.Motion);
            }
            Check(combinations.Count > 90, "per-video variation");
            var geometry = new WarmupSubtitleStyle { Plate = "solid", PlatePaddingX = 0, PlatePaddingY = 0, OutlineWidth = 0, TextThickness = 0 };
            var plain = WarmupSubtitles.PlateRect("Твой стиль", geometry, 1080, 1920);
            geometry.PlatePaddingX = 40; geometry.PlatePaddingY = 25;
            var padded = WarmupSubtitles.PlateRect("Твой стиль", geometry, 1080, 1920);
            Check(Math.Abs(padded.Width - plain.Width - 80) < .1 && Math.Abs(padded.Height - plain.Height - 50) < .1 && Math.Abs(padded.X + padded.Width / 2 - plain.X - plain.Width / 2) < .1 && Math.Abs(padded.Y + padded.Height / 2 - plain.Y - plain.Height / 2) < .1, "plate padding expands symmetrically around centered text");
            geometry.Plate = "none"; geometry.TextColor = "#FFFFFF";
            using (var thin = WarmupSubtitles.Paint("Твой стиль", geometry, 1080, 1920)) {
                geometry.TextThickness = 4;
                using (var thick = WarmupSubtitles.Paint("Твой стиль", geometry, 1080, 1920)) {
                    int a = 0, b = 0;
                    for (int y = 1250; y < 1500; y++) for (int x = 100; x < 980; x++) { if (thin.GetPixel(x, y).R > 200) a++; if (thick.GetPixel(x, y).R > 200) b++; }
                    Check(b > a, "thickness increases actual rasterized letter area");
                }
            }
            source.TextThickness = 3; source.PlatePaddingX = 40; source.PlatePaddingY = 25;
            var serializer = new XmlSerializer(typeof(WarmupSubtitleStyle)); var xml = new StringWriter(); serializer.Serialize(xml, source);
            var restored = (WarmupSubtitleStyle)serializer.Deserialize(new StringReader(xml.ToString()));
            Check(restored.RandomStyle.Enabled && restored.RandomStyle.Fonts.SequenceEqual(source.RandomStyle.Fonts) && restored.RandomStyle.PositionMax == 80, "random XML roundtrip");
            Check(restored.TextThickness == 3 && restored.PlatePaddingX == 40 && restored.PlatePaddingY == 25 && restored.RandomStyle.PaddingXMax == source.RandomStyle.PaddingXMax, "thickness and padding XML roundtrip");
            var copy = source.Copy(); copy.RandomStyle.Fonts[0] = "Impact"; Check(source.RandomStyle.Fonts[0] == "Segoe UI", "random settings deep copy");
            source.RandomStyle.SizeMin = source.RandomStyle.SizeMax = 61; source.RandomStyle.Fonts = new[] { "Impact" }; source.RandomStyle.Motions = new[] { "fade" };
            var t = new AssemblyTemplate { SubtitleStyle = source }; var item = t.WithVideoSubtitleStyle(random);
            Check(item.SubtitleStyle.FontSize == 61 && item.SubtitleStyle.FontFamily == "Impact" && item.SubtitleStyle.Motion == "fade" && t.SubtitleStyle.RandomStyle.Enabled, "assembly per-video style isolation");
            var frames = WarmupSubtitles.Rasterize(Sample(), item.SubtitleStyle, folder, 270, 480);
            Check(frames.Count > 0 && File.Exists(frames[0].TrackPath), "random style real raster track");
            source.RandomStyle.Enabled = false; Check(SubtitleRandomSettings.ForVideo(source, random).FontSize == source.FontSize, "disabled random preserves normal style");
            source.RandomStyle.Enabled = true; source.RandomStyle.Colors = new string[0]; bool failed = false;
            try { SubtitleRandomSettings.ForVideo(source, random); } catch (Exception) { failed = true; }
            Check(failed, "empty selections are not silently replaced");
            Console.WriteLine("Random subtitles PASS: 100 combinations, ranges, allowed choices, XML, isolated per-video style, actual raster and disabled mode.");
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
                var randomSource = SubtitlePresets.BuiltIn().First(p => p.Id == "bold-green").Style.Copy(); randomSource.RandomStyle.Enabled = true;
                var subtitles = WarmupSubtitles.Rasterize(Sample(), SubtitleRandomSettings.ForVideo(randomSource, new Random(17)), folder);
                string subtitled = Path.Combine(folder, "subtitled.mp4");
                await Core.Tool(ffmpeg, WarmupVideoEngine.RenderArguments(source, source, subtitled, 3.2, subtitles), CancellationToken.None, null, 90);
                await WarmupVideoEngine.VerifyOutputAsync(ffprobe, subtitled, 3.2, CancellationToken.None);
                var keys = new[] { "test-profile-a", "test-profile-b", "test-profile-c" }; int peak = 0; var seen = new Dictionary<string, int>(); var gate = new object();
                var progress = new Callback(state => { lock (gate) {
                    if (seen.TryGetValue(state.Key, out int previous)) Check(state.Percent >= previous, "monotonic progress"); seen[state.Key] = state.Percent;
                    peak = Math.Max(peak, keys.Select(UploadPreparation.State).Count(state => state != null && state.Stage != "queued"));
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
                string silentHdr = Path.Combine(folder, "hdr-tagged-silent.mp4");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=128x224:rate=25:duration=1", "-an", "-vf", "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc", "-c:v", "libx264", "-threads", "1", "-color_trc", "smpte2084", "-color_primaries", "bt2020", "-colorspace", "bt2020nc", silentHdr }, CancellationToken.None, null, 30);
                var hdrInput=await Core.Probe(ffprobe, silentHdr, CancellationToken.None);
                Check(VideoSdr.IsHdr(hdrInput), "HDR-tagged fixture transfer="+hdrInput.streams.First(s=>s.codec_type=="video").color_transfer);
                settings.UploadPreparationSubtleEffects = true;
                string hdrCopy = (await UploadPreparation.PrepareAsync("hdr", "YouTube", "hdr", new[] { silentHdr }, settings, null))[0];
                var hdrProbe = await Core.Probe(ffprobe, hdrCopy, CancellationToken.None);
                Check(VideoSdr.IsHdr(hdrProbe) && !hdrProbe.streams.Any(s => s.codec_type == "audio"), "HDR and silent source preserved");
                bool failed = false;
                try { await UploadPreparation.PrepareAsync("bad-batch", "YouTube", "bad", new[] { source, Path.Combine(folder, "missing.mp4") }, settings, null); } catch { failed = true; }
                string bad = Path.Combine(settings.UploadStagingFolder, "prepared", "YouTube", "bad");
                Check(failed && !UploadPreparation.IsBusy("bad-batch") && (!Directory.Exists(bad) || Directory.GetFiles(bad).Length == 0), "failed batch cleanup");
                using (var ct = new CancellationTokenSource()) {
                    ct.Cancel(); bool cancelled = false;
                    try { await UploadPreparation.PrepareAsync("cancelled", "TikTok", "cancelled", new[] { source }, settings, null, ct.Token); } catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && !UploadPreparation.IsBusy("cancelled"), "cancellation releases queue");
                }
                bool stopped = false;
                try { await UploadPreparation.PrepareAsync("stop-running", "TikTok", "stop-running", new[] { source }, settings,
                    new Callback(state => { if (state.Stage == "processing") UploadPreparation.Cancel(state.Key); })); }
                catch (OperationCanceledException) { stopped = true; }
                string stopFolder = Path.Combine(settings.UploadStagingFolder, "prepared", "TikTok", "stop-running");
                Check(stopped && !UploadPreparation.IsBusy("stop-running") && Directory.GetFiles(stopFolder).Length == 0 && Hash(source) == original, "running FFmpeg cancellation cleanup");
                Console.WriteLine("Upload preparation PASS: three profiles, peak workers=" + peak + ", unique embedded metadata, unchanged source, lossless remux, HDR/silent input, failed-batch cleanup and cancellation.");
                return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }
        public static bool RunRandomUiTests() {
            try {
                using (var dialog = new SubtitleStyleDialog(new WarmupSubtitleStyle())) {
                    dialog.Show(); Application.DoEvents();
                    ((Button)All(dialog).First(c => c is Button && c.Text == "Настройки")).PerformClick();
                    ((TrackBar)All(dialog).First(c => c.Name == "SubtitleTextThicknessSlider")).Value = 4;
                    Check(dialog.Style.TextThickness == 4, "thickness slider updates style");
                    ((NumericUpDown)All(dialog).First(c => c.Name == "SubtitleTextThickness")).Value = 2;
                    Check(((TrackBar)All(dialog).First(c => c.Name == "SubtitleTextThicknessSlider")).Value == 2, "manual number updates slider");
                    ((TrackBar)All(dialog).First(c => c.Name == "SubtitlePlatePaddingXSlider")).Value = 40;
                    ((NumericUpDown)All(dialog).First(c => c.Name == "SubtitlePlatePaddingY")).Value = 25;
                    Check(dialog.Style.PlatePaddingX == 40 && dialog.Style.PlatePaddingY == 25, "padding controls update style");
                    Check(All(dialog).OfType<NumericUpDown>().All(n => n.Parent.Controls.OfType<TrackBar>().Any()), "all numeric subtitle controls have sliders");
                    ((TrackBar)All(dialog).First(c => c.Name == "SubtitleRandomThicknessMinSlider")).Value = 2;
                    ((NumericUpDown)All(dialog).First(c => c.Name == "SubtitleRandomPaddingXMax")).Value = 50;
                    Check(dialog.Style.RandomStyle.ThicknessMin == 2 && dialog.Style.RandomStyle.PaddingXMax == 50, "random thickness and padding sliders");
                    ((CheckBox)All(dialog).First(c => c.Name == "SubtitleRandomEnabled")).Checked = true;
                    ((NumericUpDown)All(dialog).First(c => c.Name == "SubtitleRandomSizeMin")).Value = 58;
                    ((NumericUpDown)All(dialog).First(c => c.Name == "SubtitleRandomSizeMax")).Value = 66;
                    var fonts = (CheckedListBox)All(dialog).First(c => c.Name == "SubtitleRandomFonts");
                    for (int i = 0; i < fonts.Items.Count; i++) fonts.SetItemChecked(i, fonts.Items[i].ToString() == "Impact");
                    Application.DoEvents();
                    Check(dialog.Style.RandomStyle.Enabled && dialog.Style.RandomStyle.SizeMin == 58 && dialog.Style.RandomStyle.SizeMax == 66 && dialog.Style.RandomStyle.Fonts.SequenceEqual(new[] { "Impact" }), "random UI updates settings immediately");
                    All(dialog).First(c => c.Name == "SubtitlePreset-word-pop").GetType().GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(All(dialog).First(c => c.Name == "SubtitlePreset-word-pop"), new object[] { EventArgs.Empty });
                    Check(dialog.Style.RandomStyle.Enabled && dialog.Style.RandomStyle.SizeMin == 58, "preset selection preserves random ranges");
                    var saved = dialog.Style.Copy(); dialog.Close();
                    using (var reopened = new SubtitleStyleDialog(saved)) {
                        Check(((CheckBox)All(reopened).First(c => c.Name == "SubtitleRandomEnabled")).Checked && ((NumericUpDown)All(reopened).First(c => c.Name == "SubtitleRandomSizeMax")).Value == 66, "random UI reload");
                    }
                }
                Console.WriteLine("Random subtitle UI PASS: toggle, ranges, checked fonts, preset preservation and reopen."); return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
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
                        if (preset.Id == "bold-green") {
                            var card = All(dialog).First(c => c.Name == "SubtitlePreset-word-pop");
                            card.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(card, new object[] { EventArgs.Empty });
                            Check(dialog.Style.Motion == "pop" && dialog.Style.WordsPerCaption == 1, "preset selection applies all settings");
                            ((Button)All(dialog).First(c => c is Button && c.Text == "Настройки")).PerformClick(); Application.DoEvents();
                            using (var image = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(folder, "settings.png")); }
                        }
                        Check(dialog.Controls.Count > 0, "dialog controls"); dialog.Close();
                    }
                }
                return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
            finally { Store.Root = oldRoot; try { Directory.Delete(temp, true); } catch { } }
        }
        static IEnumerable<Control> All(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; } }
    }
}
