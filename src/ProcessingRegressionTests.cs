using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace VideoBatch {
    public static class ProcessingRegressionTests {
        public static bool RunExtendedEffects(string ffmpeg,string ffprobe) {
            string root=Path.Combine(Path.GetTempPath(),"vb-effects-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var ct=System.Threading.CancellationToken.None;
            try {
                string input=Path.Combine(root,"input.mp4");
                Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","testsrc2=s=320x180:r=30:d=2","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=2","-c:v","libx264","-c:a","aac","-metadata","comment=fixture-original",input},ct,null,60).GetAwaiter().GetResult();
                var media=Core.VideoInfo(Core.Probe(ffprobe,input,ct).GetAwaiter().GetResult());
                // Compare the shared composed RGB table against the original two filters
                // over every 8-bit input, including nearest-even rounding ties.
                foreach (double shift in new[] { -.09, -.000126, .000126, .09 }) {
                    var colorProfile = new Profile { Short=false, Resolution="source", Fps="source", Speed=1,
                        Processing=new ProcessingSample { RedShift=shift, GreenShift=shift, BlueShift=shift, ColorLut=.3, ColorPreset="vintage" } };
                    var colorArgs = ProcessingEngine.Arguments(input, "unused.mp4", colorProfile, media, null);
                    string filters = colorArgs[colorArgs.IndexOf("-vf")+1];
                    int begin = filters.IndexOf("lutrgb=", StringComparison.Ordinal), finish = filters.IndexOf("',", begin, StringComparison.Ordinal);
                    string composed = filters.Substring(begin, finish+1-begin);
                    string gain=Core.N(1+shift);
                    string original="colorchannelmixer=rr="+gain+":gg="+gain+":bb="+gain+",lutrgb=r='val*1.012+0.9':g='val+0.6':b='val*0.976+1.2'";
                    Func<string,string> digest = filter => Core.Tool(ffmpeg,new[] {"-v","error","-f","lavfi","-i","nullsrc=s=256x16,format=gbrp,geq=r=X:g=X:b=X","-vf","format=rgb24,"+filter,"-frames:v","1","-f","md5","-"},ct,null,30).GetAwaiter().GetResult().Trim();
                    if(digest(original)!=digest(composed))throw new Exception("Composed RGB table changed pixels: "+shift);
                }
                foreach(string preset in new[]{"warm","cool","film","vintage"}) {
                    var settings=new ProcessingSettings {ColorPreset=preset};
                    foreach(var item in settings.Parameters().SkipWhile(p=>!ReferenceEquals(p,settings.Gamma)))item.Enabled=true;
                    settings.Pitch.Enabled=settings.Sharpness.Enabled=true;
                    var copy=Store.Clone(settings);copy.Validate();
                    if(copy.ColorPreset!=preset||!copy.TemporalBlend.Enabled)throw new Exception("Extended settings roundtrip");
                    var profile=new Profile {Short=false,Resolution="source",Fps="source",Metadata="clear",Format="mp4",Speed=1};
                    ProcessingEngine.Apply(profile,copy,media,new Random(42),DateTime.Now);
                    string output=Path.Combine(root,preset+".mp4");
                    var args=ProcessingEngine.Arguments(input,output,profile,media,null);
                    string joined=string.Join(" ",args);
                    foreach(string filter in new[]{"geq=","tblend=","lutrgb=","gblur=","aecho=","equalizer=","lowpass=","highpass=","flanger=","unsharp="})
                        if(!joined.Contains(filter))throw new Exception("Missing filter "+filter);
                    string videoFilters=args[args.IndexOf("-vf")+1],audioFilters=args[args.IndexOf("-filter_complex")+1];
                    ProcessingEngine.Accelerate(ffmpeg,args,media.Width,media.Height,ct,Console.WriteLine).GetAwaiter().GetResult();
                    if(args[args.IndexOf("-vf")+1]!=videoFilters||args[args.IndexOf("-filter_complex")+1]!=audioFilters)throw new Exception("GPU selection changed effects");
                    Core.Tool(ffmpeg,args,ct,null,120).GetAwaiter().GetResult();
                    Console.WriteLine("Test encoder: "+args[args.IndexOf("-c:v")+1]);
                    var result=Core.VideoInfo(Core.Probe(ffprobe,output,ct).GetAwaiter().GetResult());
                    if(result.Width!=320||result.Height!=180||!result.HasAudio||Math.Abs(result.VideoDuration-2)>.06)throw new Exception("Effect output dimensions/audio/timing: "+result.VideoDuration);
                    Core.Tool(ffmpeg,new[]{"-v","error","-i",output,"-f","null","-"},ct,null,60).GetAwaiter().GetResult();
                    Console.WriteLine("Extended effects FFmpeg PASS: "+preset+", video/audio decoded, duration preserved");
                }
                if(ProcessingSettings.MildDefaults().Geq.Enabled||AssemblyProcessing.Defaults().ColorLut.Enabled)throw new Exception("New effects enabled implicitly");
                return true;
            } finally {Directory.Delete(root,true);}
        }
        public static bool RunFusedAssembly(string ffmpeg, string ffprobe) {
            string root=Path.Combine(Path.GetTempPath(),"vb-fused-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var ct=System.Threading.CancellationToken.None;
            try {
                string source=Path.Combine(root,"fixture.mp4");
                Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","testsrc2=s=160x96:r=25:d=2","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=2","-c:v","libx264","-c:a","aac",source},ct,null,30).GetAwaiter().GetResult();
                foreach(bool sound in new[]{true,false}) {
                    var settings=new ProcessingSettings();
                    foreach(var parameter in settings.Parameters().SkipWhile(x=>!ReferenceEquals(x,settings.Gamma))) parameter.Enabled=true;
                    settings.Speed.Enabled=true;settings.Speed.Values=new Range(125,125);
                    var t=new AssemblyTemplate {Materials=root,Width=160,Height=96,Fps=25,Transition="cut",SceneTimingVersion=1,RenderMode="cpu",PrimaryNativeAudio=sound,PrimaryNativeVolume=1,OutputProcessing=settings};
                    var scene=new AssemblyScene {Duration=2};t.Scenes.Add(scene);
                    var p=new AssemblyPlan {RenderDuration=2};p.Scenes.Add(new AssemblyScenePlan {Scene=scene,Video=new AssemblyVideo {Path=source,Duration=2,HasAudio=true},ContentSeconds=2,RenderDuration=2});
                    string work=Path.Combine(root,"work"+sound),output=Path.Combine(root,"fused"+sound+".mp4");
                    AssemblyEngine.Render(t,p,ffmpeg,ffprobe,output,work,AssemblyRaster.Save,ct,null,segmentRandom:new Random(7),processOutput:true).GetAwaiter().GetResult();
                    var media=Core.VideoInfo(Core.Probe(ffprobe,output,ct).GetAwaiter().GetResult());
                    if(Math.Abs(media.VideoDuration-1.6)>.06 || media.HasAudio!=sound || media.Width!=160 || media.Height!=96)throw new Exception("Fused duration/audio/canvas");
                    string graph=File.ReadAllText(Path.Combine(work,"render.ffgraph"));
                    if(!graph.Contains("[vout]setpts=")||!graph.Contains("geq=")||!graph.Contains("gblur=")||!graph.Contains("[processed_video]"))throw new Exception("Fused shared effects graph missing");
                    Core.Tool(ffmpeg,new[]{"-v","error","-i",output,"-f","null","-"},ct,null,30).GetAwaiter().GetResult();
                    if(sound) {
                        t.Insertions.Add(new AssemblyInsertion {Source=source,Start=1});
                        string inserted=Path.Combine(root,"inserted.mp4");
                        AssemblyEngine.Render(t,p,ffmpeg,ffprobe,inserted,Path.Combine(root,"insert-work"),AssemblyRaster.Save,ct,null,segmentRandom:new Random(7),processOutput:true).GetAwaiter().GetResult();
                        var ad=Core.VideoInfo(Core.Probe(ffprobe,inserted,ct).GetAwaiter().GetResult());
                        if(Math.Abs(ad.VideoDuration-3.2)>.06||!ad.HasAudio)throw new Exception("Insertion final processing boundary/speed");
                        var leveled=Store.Clone(t);leveled.NativeAudioLeveled=true;leveled.OutputProcessing.Volume.Enabled=leveled.OutputProcessing.AudioModulation.Enabled=true;
                        var sampled=AssemblyProcessing.Prepare(leveled,media,new Random(7),.5);
                        if(sampled.Profile.Speed!=1||sampled.Profile.Processing.Volume!=1||sampled.Profile.Processing.ModulationDepth!=0)throw new Exception("Prefix/leveling policy");
                    }
                    string raw=Path.Combine(root,"raw"+sound+".mp4");
                    t.Insertions.Clear();
                    AssemblyEngine.Render(t,p,ffmpeg,ffprobe,raw,Path.Combine(root,"raw-work"+sound),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                    if(Math.Abs(Core.VideoInfo(Core.Probe(ffprobe,raw,ct).GetAwaiter().GetResult()).VideoDuration-2)>.06)throw new Exception("Raw Render changed semantics");
                }
                Console.WriteLine("Fused assembly PASS: shared extended graph, speed, audio/no-audio, insertion final boundary, raw render, leveling/prefix policy");return true;
            } finally {Directory.Delete(root,true);}
        }
        public static bool RunAssemblySync(string ffmpeg, string ffprobe) {
            string root = Path.Combine(Path.GetTempPath(), "vb-assembly-sync-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                string input = Path.Combine(root, "markers.mp4");
                var ct = System.Threading.CancellationToken.None;
                string tone = "aevalsrc=0.5*sin(2*PI*440*t)*(between(t\\,5\\,5.25)+between(t\\,15\\,15.25)+between(t\\,30\\,30.25)+between(t\\,45\\,45.25)):s=48000:d=50";
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=180x320:r=25:d=50", "-f", "lavfi", "-i", tone, "-af", AssemblyRender.VoicePitchFilter(1.3).TrimEnd(','), "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", input }, ct, null, 120).GetAwaiter().GetResult();
                var settings = new ProcessingSettings(); settings.Tempo.Enabled = true; settings.Tempo.Values = new Range(102,102);
                settings.Pitch.Enabled = true; settings.Pitch.Values = new Range(102,102);
                var effective = AssemblyProcessing.Effective(settings);
                if (effective.Tempo.Enabled || !settings.Tempo.Enabled || AssemblyProcessing.Defaults().Tempo.Enabled) throw new Exception("Assembly tempo policy");
                AssemblyProcessing.Apply(new AssemblyTemplate { OutputProcessing = settings }, ffmpeg, ffprobe, input, new Random(3), ct).GetAwaiter().GetResult();
                string pcm = Path.Combine(root, "markers.pcm");
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", input, "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", pcm }, ct, null, 60).GetAwaiter().GetResult();
                byte[] data = File.ReadAllBytes(pcm);
                foreach (int second in new[] {5,15,30,45}) {
                    double weighted = 0, energy = 0;
                    for (int i = (second-1)*8000; i < (second+1)*8000 && i*2+1 < data.Length; i++) {
                        double sample = BitConverter.ToInt16(data,i*2); double power = sample*sample;
                        energy += power; weighted += power*i/8000;
                    }
                    double center = weighted/energy;
                    if (energy <= 0 || Math.Abs(center-(second+.125)) > .12) throw new Exception("Audio sync drift at " + second + ": " + center);
                    Console.WriteLine("Assembly sync PASS: marker=" + second + "s, actual=" + center.ToString("0.000") + "s");
                }
                return true;
            } catch (Exception ex) { Console.WriteLine(ex); return false; }
            finally { try { Directory.Delete(root,true); } catch { } }
        }
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
                    if (s.Rotation < 0 || s.Rotation > .25 || s.Pitch < .98 || s.Pitch > 1.02 || s.Volume < .85 || s.Volume > 1.1) return false;
                    if (s.TrimStart < 0 || s.TrimEnd < 0 || s.TrimStart + s.TrimEnd < .1 - 1e-8 || s.TrimStart + s.TrimEnd > .2 + 1e-8) return false;
                    if (s.CaptureDate > now || s.CaptureDate < now.AddDays(-30) || s.Brand != "OnePlus" || s.Software != "") return false;
                    varied |= previous >= 0 && previous != s.Rotation; previous = s.Rotation;
                }
                var canvasSettings = AssemblyProcessing.Defaults();
                foreach (double scale in new[] { 98.3499, 100.5768 }) {
                    canvasSettings.FrameScale.Values = new Range(scale, scale);
                    var canvasProfile = new Profile();
                    ProcessingEngine.Apply(canvasProfile, canvasSettings, media, new Random(2), now);
                    var arguments = ProcessingEngine.Arguments("a.mp4", "b.mp4", canvasProfile, media, null);
                    string filters = arguments[arguments.IndexOf("-vf") + 1];
                    if (!filters.EndsWith(",setsar=1", StringComparison.Ordinal)) return false;
                }
                if (!varied || ProcessingDevices.BrandNames.Length!=15 || ProcessingDevices.ModelCount!=148) return false;
                foreach (string brand in ProcessingDevices.BrandNames) {
                    var models=new System.Collections.Generic.HashSet<string>();
                    for (int i=0;i<500;i++) { var d=ProcessingDevices.Sample(new[] {brand},random); if (d.Brand!=brand || d.Software!="") return false; models.Add(d.Model); }
                    if (models.Count<7) return false;
                }
                settings.MetadataSoftware="Telegram (пользовательская метка)";
                var customSoftware=new Profile(); ProcessingEngine.Apply(customSoftware,settings,media,random,now);
                if (customSoftware.Processing.Software!=settings.MetadataSoftware) return false;
                var syntax=new[] {"-map","0:0","-metadata","creation_time=2026-10-12T09:30:00+07:00","-vf","scale=180:320","-metadata","description=https://t.me/example"};
                var normalized=(System.Collections.Generic.IEnumerable<string>)typeof(Core).GetMethod("NormalizeToolArguments",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] {syntax});
                if (!normalized.SequenceEqual(syntax)) return false;
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
        public static bool RunUnifiedUiTests() {
            foreach(bool shorts in new[]{false,true}) {
                var prefs=new Preferences {Shorts=shorts};Store.Normalize(prefs);
                prefs.Processing.Noise.Enabled=true;prefs.Processing.Noise.Values=new Range(1.2,2.3);
                prefs.Ranges.Volume=56;prefs.BackgroundDb=-17;
                string before=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(prefs);
                using(var dialog=new ProcessingDialog(prefs.Processing,exportSettings:prefs)) {
                    dialog.SelectAudioTab();
                    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    var tabs=(TabControl)typeof(ProcessingDialog).GetField("tabs",flags).GetValue(dialog);
                    if(tabs.SelectedTab.Text!="Аудио"||!tabs.TabPages.Cast<TabPage>().Any(p=>p.Text=="Экспорт"))throw new Exception("Unified tabs missing");
                    typeof(ProcessingDialog).GetMethod("Read",flags).Invoke(dialog,null);
                    if(!dialog.Settings.Noise.Enabled||dialog.Settings.Noise.Values.Min!=1.2)throw new Exception("Enabled effects lost");
                    if(shorts && dialog.ExportRanges.Volume!=56 || !shorts && dialog.MusicVolumeDb!=-17)throw new Exception("Music volume lost");
                    dialog.SelectMetadataTab();if(tabs.SelectedTab.Text!="Метаданные")throw new Exception("Metadata tab selection");
                    if(before!=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(prefs))throw new Exception("Dialog changed source before save");
                }
            }
            Console.WriteLine("Unified UI PASS: short/long export, audio shortcut, active effects and original settings preserved");return true;
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
                    typeof(ProcessingDialog).GetMethod("Read",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dialog,null);
                    return tabs == 4 && ranges == dialog.Settings.Parameters().Count()*2+7 && dialog.Settings.IsEnabled
                        && dialog.Settings.ColorPreset=="warm" && !dialog.Settings.Geq.Enabled;
                }
            } finally { Store.Root=oldRoot;if(Directory.Exists(temp))Directory.Delete(temp,true); }
        }
    }
}
