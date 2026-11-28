using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoBatch {
    public static class AssemblyInsertionTests {
        public static bool RunLinkedMedia(string ffmpeg, string probe) {
            string folder = Path.Combine(Path.GetTempPath(),"vb-linked-ad-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            var ct = System.Threading.CancellationToken.None;
            try {
                string main=Path.Combine(folder,"main.mp4"), visual=Path.Combine(folder,"visual.mp4"), audio=Path.Combine(folder,"audio.wav");
                Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","color=c=blue:s=180x320:r=25:d=3","-f","lavfi","-i","sine=frequency=220:duration=3","-c:v","libx264","-c:a","aac",main},ct,null,60).GetAwaiter().GetResult();
                Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","color=c=red:s=80x80:r=25:d=0.4","-c:v","libx264",visual},ct,null,60).GetAwaiter().GetResult();
                Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","sine=frequency=880:sample_rate=48000:duration=2",audio},ct,null,60).GetAwaiter().GetResult();
                var randomTemplate = new AssemblyTemplate { Fps=25, Materials=folder };
                randomTemplate.Insertions.Add(new AssemblyInsertion { Source=visual, RandomWholeVideo=true, ImageDuration=double.NaN });
                string randomOutput = Path.Combine(folder, "whole-video.mp4");
                AssemblyInsertions.Apply(randomTemplate,ffmpeg,probe,main,randomOutput,ct,new Random(7)).GetAwaiter().GetResult();
                var expectedRandom = new Random(7); expectedRandom.Next(1);
                double pauseAt = Math.Round(expectedRandom.NextDouble()*3*25,MidpointRounding.AwayFromZero)/25;
                foreach (double time in new[] { pauseAt-.12, pauseAt+.2, pauseAt+.52 }) {
                    string png = Path.Combine(folder,"whole-video.png");
                    Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss",Core.N(time),"-i",randomOutput,"-frames:v","1",png},ct,null,60).GetAwaiter().GetResult();
                    using (var image = new System.Drawing.Bitmap(png)) {
                        var pixel = image.GetPixel(90,160); bool red = time > pauseAt && time < pauseAt+.4;
                        if (red ? pixel.R<220 : pixel.B<220) throw new Exception("Whole-video insertion rendered at wrong position: "+time);
                    }
                }
                Console.WriteLine("Whole-video random insertion PASS: real FFmpeg, actual 3-second main, expected random position, video duration independent of photo field, main resumes");
                foreach (double offset in new[]{0d,1d,-1d}) {
                    var t = new AssemblyTemplate{Fps=25,Materials=folder}; t.Insertions.Add(new AssemblyInsertion{Source=audio,VisualSource=visual,VisualOffset=offset,Start=1});
                    string output=Path.Combine(folder,"out"+offset+".mp4"); AssemblyInsertions.Apply(t,ffmpeg,probe,main,output,ct,new Random(1)).GetAwaiter().GetResult();
                    var m=Core.VideoInfo(Core.Probe(probe,output,ct).GetAwaiter().GetResult());
                    if(Math.Abs(m.VideoDuration-(5+Math.Abs(offset)))>.12) throw new Exception("Linked insertion duration");
                    foreach(double time in new[]{.5,1.5,2.5,3.5,4.5}) {
                        string png=Path.Combine(folder,"check.png"); Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss",Core.N(time),"-i",output,"-frames:v","1",png},ct,null,60).GetAwaiter().GetResult();
                        using(var image=new System.Drawing.Bitmap(png)) {
                            double from=1+Math.Max(0,offset); bool red=time>=from&&time<from+2;
                            var pixel=image.GetPixel(90,160); if(red ? pixel.R<220 : pixel.B<220) throw new Exception("Visual timing/loop: "+offset+" @ "+time);
                        }
                    }
                    string pcm=Path.Combine(folder,"audio.pcm"); Core.Tool(ffmpeg,new[]{"-v","error","-y","-i",output,"-ac","1","-ar","8000","-f","s16le",pcm},ct,null,60).GetAwaiter().GetResult(); byte[] data=File.ReadAllBytes(pcm);
                    double audioStart=1+Math.Max(0,-offset);
                    foreach(double time in new[]{audioStart+.3,audioStart+1.6}) {
                        int crossings=0; for(int i=(int)(time*8000)+1;i<(int)((time+.2)*8000);i++) if(BitConverter.ToInt16(data,(i-1)*2)<=0&&BitConverter.ToInt16(data,i*2)>0)crossings++;
                        if(Math.Abs(crossings/.2-880)>20)throw new Exception("Linked audio timing: "+offset);
                    }
                    Console.WriteLine("Linked ad PASS: offset="+offset+", duration="+m.VideoDuration+", visual loop, audio start/end, main resumes");
                }
                var placed = new AssemblyTemplate { Fps=25, Materials=folder };
                placed.Insertions.Add(new AssemblyInsertion { Source=audio, VisualSource=visual, Start=1, VisualPlacementConfigured=true, VisualPreserveAspectRatio=false, VisualX=-.1, VisualY=.2, VisualWidth=.5, VisualHeight=.2 });
                string placedOutput=Path.Combine(folder,"placed.mp4"); AssemblyInsertions.Apply(placed,ffmpeg,probe,main,placedOutput,ct,new Random(1)).GetAwaiter().GetResult();
                string placedPng=Path.Combine(folder,"placed.png"); Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss","1.5","-i",placedOutput,"-frames:v","1",placedPng},ct,null,60).GetAwaiter().GetResult();
                using(var image=new System.Drawing.Bitmap(placedPng)) {
                    if(image.GetPixel(10,80).R<220 || image.GetPixel(90,80).B<220 || image.GetPixel(10,150).B<220)throw new Exception("Linked visual placement/stretch/crop");
                }
                string placementXml=Path.Combine(folder,"placement.xml"); AssemblyFiles.Save(placementXml,placed); var placementSaved=AssemblyFiles.Load<AssemblyTemplate>(placementXml).Insertions[0];
                if(!placementSaved.VisualPlacementConfigured || placementSaved.VisualPreserveAspectRatio || placementSaved.VisualX!=-.1 || placementSaved.VisualHeight!=.2)throw new Exception("Linked placement persistence");
                Console.WriteLine("Linked visual placement PASS: position, independent dimensions, off-frame crop, XML persistence");
                var videoPlaced = new AssemblyTemplate { Fps=25, Materials=folder };
                videoPlaced.Insertions.Add(new AssemblyInsertion { Source=visual, Start=1, VisualPlacementConfigured=true, VisualPreserveAspectRatio=false, VisualX=.1, VisualY=.2, VisualWidth=.3, VisualHeight=.2 });
                string videoPlacedOutput=Path.Combine(folder,"video-placed.mp4"); AssemblyInsertions.Apply(videoPlaced,ffmpeg,probe,main,videoPlacedOutput,ct,new Random(1)).GetAwaiter().GetResult();
                string videoPlacedPng=Path.Combine(folder,"video-placed.png"); Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss","1.2","-i",videoPlacedOutput,"-frames:v","1",videoPlacedPng},ct,null,60).GetAwaiter().GetResult();
                using(var image=new System.Drawing.Bitmap(videoPlacedPng)) {
                    if(image.GetPixel(30,80).R<220 || image.GetPixel(100,80).B<220 || image.GetPixel(30,180).B<220)throw new Exception("Standalone video insertion ignored placement");
                }
                Console.WriteLine("Standalone video insertion placement PASS: real FFmpeg, position and independent dimensions");
                var layer=new AssemblyLayer{PlaybackMode="repeat",RepeatInterval=1,RepeatPause=100,Start=.2,AppearanceTransition="cut",Source=visual,Width=.5,Height=.5,X=.25,Y=.25};
                if(AssemblyOverlayMedia.RepeatPeriod(layer,.4)!=1)throw new Exception("Period start-to-start");
                var scheduled=new AssemblyTemplate{Materials=folder,Width=180,Height=320,Fps=25,Transition="cut",SceneTimingVersion=1,PrimaryNativeAudio=false,RenderMode="cpu"};
                var source=new AssemblyScene{Duration=3}; source.Layers.Add(layer); scheduled.Scenes.Add(source);
                var scene=new AssemblyScenePlan{Scene=source,Video=new AssemblyVideo{Path=main,Duration=3},ContentSeconds=3,RenderDuration=3}; scene.Layers.Add(new AssemblyLayerPlan{Layer=layer,Asset=new AssemblyAsset{Path=visual}});
                var plan=new AssemblyPlan{RenderDuration=3}; plan.Scenes.Add(scene); string rendered=Path.Combine(folder,"periodic.mp4");
                AssemblyRender.Run(scheduled,plan,ffmpeg,probe,rendered,Path.Combine(folder,"periodic-work"),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                foreach(double time in new[]{.35,.85,1.35,1.85,2.35,2.85}) {
                    string png=Path.Combine(folder,"periodic.png"); Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss",Core.N(time),"-i",rendered,"-frames:v","1",png},ct,null,60).GetAwaiter().GetResult();
                    using(var image=new System.Drawing.Bitmap(png)) { bool red=time%1<.6; var pixel=image.GetPixel(90,160); if(red?pixel.R<220:pixel.B<220)throw new Exception("Periodic launch time: "+time); }
                }
                AssemblyMarketProfiles.Capture(scheduled,AssemblyProfile.Ru); string xml=Path.Combine(folder,"periodic.xml"); AssemblyFiles.Save(xml,scheduled); var saved=AssemblyFiles.Load<AssemblyTemplate>(xml); AssemblyMarketProfiles.Apply(saved,AssemblyProfile.Ru);
                if(saved.Scenes[0].Layers[0].RepeatInterval!=1)throw new Exception("Period persistence");
                Console.WriteLine("Periodic animation PASS: start-to-start launches, pauses, file duration, profile persistence");
                return true;
            }catch(Exception ex){Console.WriteLine(ex);Console.WriteLine(folder);return false;}
        }
        public static bool RunEditorUi() {
            string previous=Store.Root, root=Path.Combine(Path.GetTempPath(),"vb-ad-ui-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                Store.Root=root;
                var fixture=AssemblyTemplate.Defaults(); fixture.Materials=Path.Combine(root,"materials"); fixture.Output=Path.Combine(root,"output");
                AssemblyMarketProfiles.Capture(fixture,AssemblyProfile.Ru); AssemblyFiles.Save(Path.Combine(root,"assembly-template.xml"),fixture);
                using(var window=new AssemblyWindow(true,true)) {
                    var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
                    var template=(AssemblyTemplate)typeof(AssemblyWindow).GetField("template",flags).GetValue(window);
                    template.Insertions.Add(new AssemblyInsertion{Source="audio.wav",VisualSource="visual.mp4",VisualOffset=-2});
                    Exception failure=null; bool inspected=false;
                    using(var timer=new System.Windows.Forms.Timer{Interval=100}) {
                        timer.Tick+=(s,e)=>{
                            foreach(System.Windows.Forms.Form form in System.Windows.Forms.Application.OpenForms) if(form.Name=="AssemblyInsertions") {
                                timer.Stop(); try {
                                    var grid=(System.Windows.Forms.DataGridView)form.Controls[0];
                                    if(!grid.Columns.Contains("Visual")||!grid.Columns.Contains("Offset")||Convert.ToString(grid.Rows[0].Cells["Visual"].Value)!="visual.mp4")throw new Exception("Linked insertion UI values");
                                    var panel=(System.Windows.Forms.FlowLayoutPanel)form.Controls[2];
                                    var slider=System.Linq.Enumerable.OfType<AssemblySlider>(panel.Controls).Single(c=>c.Name=="InsertionOffsetSlider");
                                    if(slider.Width<300||!System.Linq.Enumerable.OfType<System.Windows.Forms.Button>(panel.Controls).Any(c=>c.Text=="Просмотреть связку"))throw new Exception("Insertion preview/slider UI");
                                    inspected=true;
                                }catch(Exception ex){failure=ex;} finally{form.Close();} break;
                            }
                        };
                        timer.Start(); typeof(AssemblyWindow).GetMethod("OpenInsertions",flags).Invoke(window,null);
                    }
                    if(failure!=null)throw failure; if(!inspected)throw new Exception("Insertion UI not opened");
                }
                Console.WriteLine("Linked insertion UI PASS: visual source, offset, slider and preview button");return true;
            } catch(Exception ex){Console.WriteLine(ex);return false;}
            finally{Store.Root=previous;}
        }
        static void RunOptionalTimingTests() {
            var template = new AssemblyTemplate { Materials = Path.GetTempPath() };
            Func<string, object, object, bool, object, AssemblyInsertion> read = (source, start, end, range, duration) =>
                AssemblyWindow.ReadInsertion(null, template, source, "", start, end, range, duration, "");
            var whole = read("ad.mp4", "", "", false, "");
            if (!whole.RandomWholeVideo || whole.VisualOffset != 0 || whole.ImageDuration != 5) throw new Exception("Blank insertion defaults");
            var wholeRange = read("ad.wav", " ", "", true, "not relevant");
            if (!wholeRange.RandomWholeVideo) throw new Exception("Blank range must use whole video");
            var fixedTime = read("ad.mp4", "0", "not relevant", false, "not relevant");
            if (fixedTime.RandomWholeVideo || fixedTime.Start != 0) throw new Exception("Explicit zero is fixed, not random");
            var rangeTime = read("ad.wav", "2,5", "4", true, "0");
            if (rangeTime.RandomWholeVideo || rangeTime.Start != 2.5 || rangeTime.End != 4) throw new Exception("Explicit range parsing");
            if (read("photo.png", "", "", false, "").ImageDuration != 5) throw new Exception("Blank photo duration default");
            foreach (Action invalid in new Action[] {
                () => read("ad.wav", "1", "", true, ""),
                () => read("ad.wav", "", "2", true, ""),
                () => read("ad.mp4", "-1", "", false, ""),
                () => read("ad.mp4", "3", "2", true, ""),
                () => read("photo.png", "", "", false, "0"),
                () => read("photo.png", "", "", false, "bad"),
                () => AssemblyWindow.ReadInsertion(null, template, "ad.wav", "visual.mp4", "", "", false, "", "bad")
            }) {
                bool rejected = false; try { invalid(); } catch (Exception) { rejected = true; }
                if (!rejected) throw new Exception("Invalid relevant insertion field accepted");
            }
            for (int seed = 0; seed < 12; seed++) {
                foreach (double mainDuration in new[] { .4, 3d, 91.75 }) {
                    double expected = new Random(seed).NextDouble() * mainDuration;
                    double actual = AssemblyInsertions.AppearanceTime(whole, mainDuration, new Random(seed));
                    if (actual != expected || actual < 0 || actual >= mainDuration) throw new Exception("Whole-video timing must use actual duration");
                }
                if (AssemblyInsertions.AppearanceTime(fixedTime, 10, new Random(seed)) != 0) throw new Exception("Fixed timing changed");
                if (AssemblyInsertions.AppearanceTime(rangeTime, 10, new Random(seed)) != 2.5 + new Random(seed).NextDouble() * 1.5) throw new Exception("Range timing changed");
            }
            var previous = new AssemblyInsertion { Start=7, End=9, ImageDuration=8, VisualPlacementConfigured=true, VisualX=.2 };
            var edited = AssemblyWindow.ReadInsertion(previous, template, "ad.wav", "visual.mp4", "7", "", false, "", "");
            if (edited.End != 9 || edited.ImageDuration != 8 || !edited.VisualPlacementConfigured || edited.VisualX != .2 || previous.RandomWholeVideo) throw new Exception("Editor lost inactive legacy values or visual placement");
            string folder = Path.Combine(Path.GetTempPath(), "vb-insertion-fields-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                File.WriteAllBytes(Path.Combine(folder, "photo.png"), new byte[0]);
                if (read(folder, "", "", false, "").ImageDuration != 5) throw new Exception("Mixed/photo folder defaults");
                bool rejected = false; try { read(folder, "", "", false, "bad"); } catch (Exception) { rejected = true; }
                if (!rejected) throw new Exception("Photo folder must validate duration");
                string file = Path.Combine(folder, "insertions.xml");
                var list = new List<AssemblyInsertion> { whole, fixedTime, rangeTime };
                AssemblyFiles.Save(file, list);
                var restored = AssemblyFiles.Load<List<AssemblyInsertion>>(file);
                if (!restored[0].RandomWholeVideo || restored[1].RandomWholeVideo || restored[1].Start != 0 || !restored[2].RandomTime || restored[2].End != 4 || !AssemblyInsertions.Copy(restored)[0].RandomWholeVideo) throw new Exception("Timing mode persistence/copy");
                File.WriteAllText(file, "<AssemblyInsertion><Start>0</Start><End>4</End><RandomTime>true</RandomTime></AssemblyInsertion>");
                var legacy = AssemblyFiles.Load<AssemblyInsertion>(file);
                if (legacy.RandomWholeVideo || !legacy.RandomTime || legacy.End != 4 || legacy.ImageDuration != 5) throw new Exception("Legacy range settings changed");
                File.WriteAllText(file, "<AssemblyInsertion><Start>0</Start></AssemblyInsertion>");
                legacy = AssemblyFiles.Load<AssemblyInsertion>(file);
                if (legacy.RandomWholeVideo || legacy.RandomTime || legacy.Start != 0) throw new Exception("Legacy fixed zero changed");
            } finally { Directory.Delete(folder, true); }
            Console.WriteLine("Optional insertion timing PASS: blank defaults, relevant fields, actual duration, fixed/range timing, legacy XML, copy and placement");
        }

        public static bool RunSelfTests() {
            RunOptionalTimingTests();
            var template = AssemblyTemplate.Defaults();
            template.Materials = Path.GetTempPath();
            AssemblyMarketProfiles.EnsureSeeded(template);
            template.Insertions.Add(new AssemblyInsertion { Source = "ad.mp4", VisualSource = "visual.webm", VisualOffset = -2, Start = 2, End = 4, RandomTime = true, ImageDuration = 3 });
            AssemblyMarketProfiles.Capture(template, AssemblyProfile.Ru);
            template.Insertions[0].Start = 9;
            AssemblyMarketProfiles.Apply(template, AssemblyProfile.En);
            if (template.Insertions.Count != 0) throw new Exception("Advertising pauses leaked into another profile.");
            AssemblyMarketProfiles.Apply(template, AssemblyProfile.Ru);
            if (template.Insertions.Count != 1 || template.Insertions[0].Start != 2) throw new Exception("Advertising profile was not deep copied.");
            string file = Path.Combine(Path.GetTempPath(), "insertions-" + Guid.NewGuid().ToString("N") + ".xml");
            try {
                AssemblyFiles.Save(file, template);
                var restored = AssemblyFiles.Load<AssemblyTemplate>(file);
                AssemblyMarketProfiles.Apply(restored, AssemblyProfile.Ru);
                var insertion = restored.Insertions[0];
                if (insertion.Source != "ad.mp4" || insertion.VisualSource != "visual.webm" || insertion.VisualOffset != -2 || !insertion.RandomTime || insertion.Start != 2 || insertion.End != 4 || insertion.ImageDuration != 3)
                    throw new Exception("Advertising settings did not round trip through XML.");
                var custom = AssemblyMarketProfiles.AddEmpty(restored, "Empty");
                AssemblyMarketProfiles.Apply(restored, custom.Id);
                if (restored.Insertions.Count != 0) throw new Exception("Empty profile inherited advertising pauses.");
            } finally { if (File.Exists(file)) File.Delete(file); }
            return true;
        }
    }
}
