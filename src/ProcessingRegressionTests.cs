using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace VideoBatch {
    public static class ProcessingRegressionTests {
        public static bool RunSelfTests() {
            string temp = Path.Combine(Path.GetTempPath(), "videobatch-processing-" + Guid.NewGuid().ToString("N"));
            string oldRoot = Store.Root;
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var settings = new ProcessingSettings(); var random = new Random(1234);
                var media = new Media { Duration = 10, Width = 320, Height = 180, Fps = "30/1", VideoBitrate = 2, VideoIndex = 0, HasAudio = true, AudioIndex = 1 };
                var legacy = new Profile { Speed = 1.01, Crop = 1.2 };
                string original = string.Join("|", Core.Arguments("a.mp4", "b.mp4", legacy, media, ""));
                ProcessingEngine.Apply(legacy, settings, media, random, DateTime.Now);
                if (legacy.Processing != null || original != string.Join("|", Core.Arguments("a.mp4", "b.mp4", legacy, media, ""))) return false;
                var prefs = new Preferences { SettingsVersion = 2, ProtectedDolphinToken = "encrypted-fixture", Output = temp, VideoCount = 7 };
                prefs.Processing = settings; prefs.Ranges.Volume = 83; prefs.Profiles[0].Crop = 2.3;
                var channel = new YouTubeChannel { ProfileId = "keep", Name = "keep" }; prefs.YouTubeChannels.Add(channel);
                settings.Rotation.Enabled = settings.Trim.Enabled = settings.Pitch.Enabled = settings.Volume.Enabled = true;
                settings.DeviceMetadata = settings.CaptureDateEnabled = true; settings.Brands = new string[] { "OnePlus" };
                settings.CaptureDaysBack = 30;
                var now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Local); double previous = -1; bool varied = false;
                for (int i = 0; i < 300; i++) {
                    var profile = new Profile(); ProcessingEngine.Apply(profile, settings, media, random, now); var s = profile.Processing;
                    if (s.Rotation < 0 || s.Rotation > 2 || s.Pitch < .98 || s.Pitch > 1.02 || s.Volume < .85 || s.Volume > 1.1) return false;
                    if (s.TrimStart < 0 || s.TrimEnd < 0 || s.TrimStart + s.TrimEnd < .1 - 1e-8 || s.TrimStart + s.TrimEnd > .2 + 1e-8) return false;
                    if (s.CaptureDate > now || s.CaptureDate < now.AddDays(-30) || s.Brand != "OnePlus" || s.Software != "OxygenOS 14") return false;
                    varied |= previous >= 0 && previous != s.Rotation; previous = s.Rotation;
                }
                if (!varied) return false;
                // Explicit fixed ranges stay exact; changing date time independently cannot add future dates.
                var fixedSettings = new ProcessingSettings(); fixedSettings.Speed.Enabled = true;
                fixedSettings.Speed.Values = new Range(125,125); fixedSettings.CaptureDateEnabled = true;
                fixedSettings.CaptureDaysBack = 0; fixedSettings.RandomizeCaptureTime = false;
                var fixedProfile = new Profile(); ProcessingEngine.Apply(fixedProfile,fixedSettings,media,random,now);
                if(fixedProfile.Speed!=1.25 || fixedProfile.Processing.CaptureDate!=now) return false;
                // Metadata alone must not activate old Long color fields, which were unused in that mode.
                var metadataOnly = new ProcessingSettings { DeviceMetadata=true };
                var longColor = new Profile { Brightness=.2, Contrast=.6 };
                ProcessingEngine.Apply(longColor,metadataOnly,media,random,now);
                if(longColor.Brightness!=0 || longColor.Contrast!=1) return false;
                string preset = Path.Combine(temp, "effects.xml"); ProcessingPreset.Save(preset, settings);
                var loaded = ProcessingPreset.Load(preset);
                if (!loaded.Pitch.Enabled || loaded.Brands.Single() != "OnePlus" || loaded.CaptureDaysBack != 30) return false;
                // Missing new XML fields must initialize to disabled, without changing legacy processing or accounts.
                Store.Save(prefs); string xml = File.ReadAllText(Store.Config);
                int begin = xml.IndexOf("<Processing>", StringComparison.Ordinal), end = xml.IndexOf("</Processing>", begin, StringComparison.Ordinal);
                if (begin < 0 || end < begin) return false;
                File.WriteAllText(Store.Config, xml.Remove(begin, end + "</Processing>".Length - begin));
                var restored = Store.Load();
                if (!restored.Processing.IsEnabled || restored.Ranges.Volume != 83 || restored.Profiles[0].Crop != 2.3 || restored.ProtectedDolphinToken != "encrypted-fixture" || restored.VideoCount != 7 || restored.YouTubeChannels.Single().ProfileId != "keep") return false;
                restored.Processing = loaded; Store.Save(restored);
                if (!Store.Load().Processing.Pitch.Enabled) return false;
                var invalid = Store.Clone(settings); invalid.Brands = new string[0];
                try { invalid.Validate(); return false; } catch (Exception) { }
                invalid = Store.Clone(settings); invalid.Rotation.Values = new Range(2, 1);
                try { invalid.Validate(); return false; } catch (Exception) { }
                invalid = Store.Clone(settings); invalid.Pitch.Values.Min = double.NaN;
                try { invalid.Validate(); return false; } catch (Exception) { }
                // Source settings remain owned by their existing instance; the processing window must not reload a stale copy.
                if (prefs.YouTubeChannels.Single() != channel || prefs.Ranges.Volume != 83 || prefs.Profiles[0].Crop != 2.3) return false;
                return true;
            } finally { Store.Root = oldRoot; if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }
        public static bool RunUiSelfTests() {
            string oldRoot=Store.Root;
            string temp=Path.Combine(Path.GetTempPath(),"videobatch-processing-ui-"+Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp);Store.Root=temp;
                var prefs = new Preferences();
                using (var window = new MainWindow(prefs)) {
                    if (!ReferenceEquals(typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window), prefs)) return false;
                }
                using (var dialog = new ProcessingDialog(ProcessingSettings.MildDefaults())) {
                    int tabs = 0, ranges = 0;
                    Action<Control> walk = null; walk = c => {
                        if (c is TabControl t) tabs += t.TabPages.Count;
                        if (c is NumericUpDown) ranges++;
                        foreach (Control child in c.Controls) walk(child);
                    };
                    walk(dialog);
                    return tabs == 4 && ranges == 39 && dialog.Settings.IsEnabled;
                }
            } finally { Store.Root=oldRoot;if(Directory.Exists(temp))Directory.Delete(temp,true); }
        }
    }
}
