using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VideoBatch {
    public static class AssemblyRegressionTests {
        public static bool RunTextAndCornerModelTests() {
            string root = Path.Combine(Path.GetTempPath(), "text-corner-model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                var legacy = new System.Xml.Serialization.XmlSerializer(typeof(AssemblyLayer));
                var old = (AssemblyLayer)legacy.Deserialize(new StringReader("<AssemblyLayer><Radius>28</Radius></AssemblyLayer>"));
                if (old.Kind != "image" || old.CornerRadius != 0 || old.Radius != 28) throw new Exception("Legacy corner defaults changed.");
                string video = Path.Combine(root, "video.mp4"); File.WriteAllText(video, "fixture");
                var layer = new AssemblyLayer { Kind="text", Text="Привет\nWindows", Font="Arial", FontSize=30, X=.1, Y=.2, Width=.8, Height=.6,
                    TextColor="#FFCC00", TextOutlineColor="#1200FF", TextOutlineWidth=2, TextBold=true, TextItalic=true, Opacity=.5, CornerRadius=17 };
                var t = new AssemblyTemplate { Materials=root, Videos=video, Width=180, Height=180, Transition="cut", PackStudioVersion=1 };
                t.Scenes.Add(new AssemblyScene { Duration=1, Layers=new System.Collections.Generic.List<AssemblyLayer> {layer} });
                t.GlobalLayers.Add(new AssemblyLayer { Kind="text", Text="Global", FontSize=22 });
                t.Validate();
                var plans = AssemblyPlanner.Create(t, new[] { new AssemblyVideo { Path=video, Hash="video", Duration=2 } }, new AssemblyHistory(), new Random(3), 1).Plans;
                if (plans.Count != 1 || plans[0].Scenes[0].Layers.Count != 1 || plans[0].GlobalLayers.Count != 1 || plans[0].Scenes[0].Layers[0].Asset.Path != "") throw new Exception("Source-free text planning failed.");
                using (var image = AssemblyRaster.Create(t, new AssemblyLayerPlan { Layer=layer })) {
                    if (image.GetPixel(0,0).A != 0) throw new Exception("Text canvas is not transparent.");
                    int colored=0;
                    for(int y=0;y<image.Height;y++) for(int x=0;x<image.Width;x++) if(image.GetPixel(x,y).A>0) colored++;
                    if (colored < 100) throw new Exception("Windows font text did not render.");
                }
                var key=AssemblyRenderContext.RasterKey(t,new AssemblyLayerPlan {Layer=layer}); layer.Text="Changed";
                if(key==AssemblyRenderContext.RasterKey(t,new AssemblyLayerPlan {Layer=layer})) throw new Exception("Text raster cache did not invalidate.");
                key=AssemblyRenderContext.RasterKey(t,new AssemblyLayerPlan {Layer=layer}); layer.TextColor="#00FFFF";
                if(key==AssemblyRenderContext.RasterKey(t,new AssemblyLayerPlan {Layer=layer})) throw new Exception("Color raster cache did not invalidate.");
                using(var image=new Bitmap(40,30,System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                    using(var g=Graphics.FromImage(image))g.Clear(Color.FromArgb(128,255,0,0));
                    AssemblyRaster.ApplyCornerMask(image,10);
                    if(image.GetPixel(0,0).A!=0 || image.GetPixel(20,15).A!=128 || image.GetPixel(20,0).A!=128)throw new Exception("Rounded mask replaced source alpha.");
                }
                AssemblyMarketProfiles.Capture(t,AssemblyProfile.Ru);
                string file=Path.Combine(root,"settings.xml");AssemblyFiles.Save(file,t);
                var copy=AssemblyFiles.Load<AssemblyTemplate>(file);AssemblyMarketProfiles.Apply(copy,AssemblyProfile.Ru);
                var restored=copy.Scenes[0].Layers[0];
                if(!restored.IsText || restored.Text!=layer.Text || restored.TextColor!=layer.TextColor || restored.TextOutlineColor!=layer.TextOutlineColor || restored.TextOutlineWidth!=2 || !restored.TextBold || !restored.TextItalic || restored.CornerRadius!=17)throw new Exception("Text/corner profile or XML settings lost.");
                AssemblyWorkspace.Prepare(copy);
                if(copy.Scenes[0].Layers[0].Source!="")throw new Exception("Workspace assigned a source pack to text.");
                Console.WriteLine("Text/corner model PASS: legacy XML, source-free scene/global planning, transparent Windows text, cache, alpha mask, profile/XML and workspace.");return true;
            } finally { Directory.Delete(root,true); }
        }
        public static bool RunSceneOverlayAndCaptionRenderTests(string ffmpeg, string probe) {
            string root = Path.Combine(Path.GetTempPath(), "scene-caption-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var ct = System.Threading.CancellationToken.None;
            try {
                string video = Path.Combine(root, "blue.mp4"), picture = Path.Combine(root, "red.png");
                Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=180x320:r=30:d=1", "-c:v", "libx264", video }, ct).GetAwaiter().GetResult();
                using (var image = new Bitmap(40, 40)) { using (var g = Graphics.FromImage(image)) g.Clear(Color.Red); image.Save(picture); }
                var t = new AssemblyTemplate { Width=180, Height=320, Fps=30, SceneTimingVersion=1, Transition="cut", RenderMode="cpu" };
                t.SubtitleStyle = new WarmupSubtitleStyle { FontFamily="Arial", FontSize=28, Position=50, Plate="solid", PlateColor="#00FF00", Opacity=100, Motion="none" };
                var p = new AssemblyPlan();
                for (int i=0; i<3; i++) {
                    var scene = new AssemblyScene { Duration=1, VoiceMode="none" }; t.Scenes.Add(scene);
                    var s = new AssemblyScenePlan { Scene=scene, Video=new AssemblyVideo { Path=video, Duration=1 } }; p.Scenes.Add(s);
                    if (i==2) foreach (double y in new[] { .1, .75 }) s.Layers.Add(new AssemblyLayerPlan {
                        Layer=new AssemblyLayer { X=.2, Y=y, Width=.3, Height=.15, PreserveAspectRatio=false },
                        Asset=new AssemblyAsset { Path=picture, Hash=AssemblyFiles.Hash(picture) }
                    });
                }
                AssemblyTimeline.Complete(t,p,new Random(1));
                t.Captions.Add(new AssemblyCaption { Text="TEST", Start=.1, End=.8 });
                string output=Path.Combine(root,"output.mp4");
                AssemblyRender.Run(t,p,ffmpeg,probe,output,Path.Combine(root,"work"),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                foreach (string time in new[] { "0.4", "2", "2.5" }) {
                    string frame=Path.Combine(root,"frame.png");
                    Core.Tool(ffmpeg,new[] { "-v","error","-y","-ss",time,"-i",output,"-frames:v","1",frame },ct).GetAwaiter().GetResult();
                    using(var image=new Bitmap(frame)) {
                        int green=0; for(int y=120;y<200;y++) for(int x=0;x<180;x++) { var c=image.GetPixel(x,y); if(c.G>150 && c.R<100 && c.B<100)green++; }
                        if(time=="0.4" ? green<100 : green>0) throw new Exception("Caption scene boundary failed at "+time);
                        if(time!="0.4" && (image.GetPixel(50,45).R<180 || image.GetPixel(50,255).R<180)) throw new Exception("Zero-start objects did not appear together at "+time);
                    }
                }
                Console.WriteLine("Real render PASS: both zero-start objects visible on first frame of scene 3; captions visible in scene 1 only."); return true;
            } finally { Directory.Delete(root,true); }
        }
        public static bool RunTextAndCornerRenderTests(string ffmpeg,string probe) {
            string root=Path.Combine(Path.GetTempPath(),"text-corner-render-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var ct=System.Threading.CancellationToken.None;
            try {
                string background=Path.Combine(root,"blue.mp4"), still=Path.Combine(root,"overlay.png"), clip=Path.Combine(root,"overlay.mov");
                Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","color=c=blue:s=180x180:r=30:d=1.2","-c:v","libx264",background},ct).GetAwaiter().GetResult();
                using(var image=new Bitmap(80,60,System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                    using(var g=Graphics.FromImage(image)) {g.Clear(Color.FromArgb(128,255,0,0));using(var b=new SolidBrush(Color.Lime))g.FillRectangle(b,65,0,15,60);}
                    image.Save(still,System.Drawing.Imaging.ImageFormat.Png);
                }
                Core.Tool(ffmpeg,new[]{"-v","error","-y","-loop","1","-i",still,"-t","1","-r","30","-c:v","qtrle","-pix_fmt","argb",clip},ct).GetAwaiter().GetResult();
                foreach(string source in new[]{still,clip}) foreach(double radius in new[]{0d,18d}) {
                    string suffix=Path.GetExtension(source).Substring(1)+radius;
                    var layer=new AssemblyLayer {Source=source,X=.2,Y=.3,Width=.6,Height=.4,PreserveAspectRatio=false,CornerRadius=radius,Opacity=.6,GreenScreen=true,
                        Start=.1,End=1.1,AppearanceTransition="fade",AppearanceDuration=.2,ExitTransition="fade",ExitDuration=.2};
                    var t=new AssemblyTemplate {Width=180,Height=180,Fps=30,SceneTimingVersion=1,Transition="cut",RenderMode="cpu"};
                    var scene=new AssemblyScene {Duration=1.2};t.Scenes.Add(scene);
                    var plan=new AssemblyPlan();var s=new AssemblyScenePlan {Scene=scene,Video=new AssemblyVideo {Path=background,Duration=1.2}};plan.Scenes.Add(s);
                    s.Layers.Add(new AssemblyLayerPlan {Layer=layer,Asset=new AssemblyAsset {Path=source,Hash=AssemblyFiles.Hash(source)}});
                    var text=new AssemblyLayer {Kind="text",Text="TEXT",Font="Arial",FontSize=26,TextBold=true,TextColor="#FFFFFF",X=0,Y=0,Width=1,Height=.22};
                    plan.GlobalLayers.Add(new AssemblyLayerPlan {Layer=text});
                    AssemblyTimeline.Complete(t,plan,new Random(1));
                    string output=Path.Combine(root,suffix+".mp4");
                    AssemblyRender.Run(t,plan,ffmpeg,probe,output,Path.Combine(root,suffix),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                    Color Sample(double at,int x,int y) {
                        string png=Path.Combine(root,"frame.png");
                        Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss",Core.N(at),"-i",output,"-frames:v","1",png},ct).GetAwaiter().GetResult();
                        using(var image=new Bitmap(png))return image.GetPixel(x,y);
                    }
                    var center=Sample(.6,90,90);var corner=Sample(.6,38,56);var keyed=Sample(.6,135,90);
                    if(center.R<60 || center.R>95 || center.B<150 || center.B>195)throw new Exception(suffix+" source alpha/opacity incorrect: "+center);
                    if(radius>0 ? corner.R>15 || corner.B<225 : corner.R<50)throw new Exception(suffix+" corner mask/legacy square incorrect: "+corner);
                    if(keyed.R>15 || keyed.G>15 || keyed.B<225)throw new Exception(suffix+" chromakey alpha incorrect: "+keyed);
                    if(Sample(.133,90,90).R>=center.R-10 || Sample(1.066,90,90).R>=center.R-10)throw new Exception(suffix+" fade animation lost.");
                    string textFrame=Path.Combine(root,"text.png");Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss","0.6","-i",output,"-frames:v","1",textFrame},ct).GetAwaiter().GetResult();
                    using(var image=new Bitmap(textFrame)) {
                        int white=0;for(int y=0;y<40;y++)for(int x=0;x<180;x++){var c=image.GetPixel(x,y);if(c.R>190 && c.G>190)white++;}
                        if(white<80)throw new Exception("Source-free global text missing in full render.");
                    }
                }
                Console.WriteLine("Text/corner real FFmpeg PASS: PNG and animated alpha MOV, radius 0/18 after scaling, opacity, chromakey, entrance/exit fades and source-free global Windows text.");return true;
            } finally {Directory.Delete(root,true);}
        }
        public static bool RunCornerMaskFilterTests(string ffmpeg) {
            string root=Path.Combine(Path.GetTempPath(),"corner-mask-filter-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var ct=System.Threading.CancellationToken.None;
            string Legacy(string label,double radius,int width,int height) {
                string r=Core.N(Math.Min(radius,Math.Min(width,height)/2.0));
                string dx="max(abs(X+0.5-W/2)-(W/2-"+r+"),0)",dy="max(abs(Y+0.5-H/2)-(H/2-"+r+"),0)";
                return ",format=rgba,split["+label+"Rgb]["+label+"Alpha];["+label+"Alpha]alphaextract,geq=lum='lum(X,Y)*clip("+r+"+0.5-sqrt(pow("+dx+",2)+pow("+dy+",2)),0,1)'["+label+"Mask];["+label+"Rgb]["+label+"Mask]alphamerge,format=rgba";
            }
            try {
                if(AssemblyOverlayMedia.CornerFilter("zero",0,41,31)!="")throw new Exception("Zero radius must remain a no-op.");
                foreach(string rate in new[]{"1","24","30","60","30000/1001"}) foreach(double radius in new[]{7.25,100d}) {
                    const int width=41,height=31,frames=6;
                    string input="testsrc=s=41x31:r="+rate+",format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='mod(X*7+Y*11+N*31,256)',trim=end_frame=6,setpts=PTS+2/TB";
                    byte[][] alpha=new byte[2][];string[][] timestamps=new string[2][];
                    for(int variant=0;variant<2;variant++) {
                        string file=Path.Combine(root,"alpha"+variant+".raw");
                        string filter="[0:v]null"+(variant==0?Legacy("old",radius,width,height):AssemblyOverlayMedia.CornerFilter("new",radius,width,height))+",alphaextract,split[raw][hash]";
                        string hash=Core.Tool(ffmpeg,new[]{"-v","error","-y","-filter_complex_threads","1","-f","lavfi","-i",input,"-filter_complex",filter,
                            "-map","[raw]","-fps_mode","passthrough","-c:v","rawvideo","-f","rawvideo",file,"-map","[hash]","-fps_mode","passthrough","-f","framemd5","-"},ct,null,30).GetAwaiter().GetResult();
                        alpha[variant]=File.ReadAllBytes(file);
                        timestamps[variant]=hash.Split('\n').Where(x=>x.Length>0 && x[0]!='#').Select(x=>string.Join(",",x.Split(',').Take(5))).ToArray();
                    }
                    if(alpha.Any(x=>x.Length!=width*height*frames) || timestamps[0].Length!=frames || !timestamps[0].SequenceEqual(timestamps[1]))throw new Exception("Corner filter changed finite EOF, frame count or timestamps at "+rate+" fps.");
                    int antialias=0;
                    for(int i=0;i<alpha[0].Length;i++) {
                        if(Math.Abs(alpha[0][i]-alpha[1][i])>1)throw new Exception("Corner alpha differs by more than one quantization level at "+rate+" fps, radius "+radius+", byte "+i);
                        int pixel=i%(width*height),x=pixel%width,y=pixel/width,n=i/(width*height);
                        int sourceAlpha=(x*7+y*11+n*31)%256;
                        if(x==width/2 && y==height/2 && alpha[1][i]!=sourceAlpha)throw new Exception("Corner filter replaced interior source alpha.");
                        if(alpha[1][i]>0 && alpha[1][i]<sourceAlpha)antialias++;
                    }
                    if(antialias==0)throw new Exception("Rounded edge antialiasing disappeared.");
                }
                double[] seconds=new double[2];
                for(int variant=0;variant<2;variant++) {
                    string filter="[0:v]null"+(variant==0?Legacy("old",90,1080,1920):AssemblyOverlayMedia.CornerFilter("new",90,1080,1920))+"[out]";
                    var clock=System.Diagnostics.Stopwatch.StartNew();
                    Core.Tool(ffmpeg,new[]{"-v","error","-filter_complex_threads","1","-f","lavfi","-i","color=c=red@0.5:s=1080x1920:r=30,format=rgba,trim=end_frame=12",
                        "-filter_complex",filter,"-map","[out]","-f","null","-"},ct,null,120).GetAwaiter().GetResult();
                    seconds[variant]=clock.Elapsed.TotalSeconds;
                }
                Console.WriteLine("Corner mask real FFmpeg PASS: finite EOF without output duration/frame cap, unchanged shifted PTS and 6 frames at 1/24/30/60/29.97 fps, odd dimensions, fractional/clamped radius, varying source alpha and antialias <=1 level.");
                Console.WriteLine("Corner mask benchmark 1080x1920, 12 frames, one filter thread, process wall time: legacy="+Core.N(seconds[0])+"s optimized="+Core.N(seconds[1])+"s speedup="+Core.N(seconds[0]/seconds[1])+"x.");
                return true;
            } finally {Directory.Delete(root,true);}
        }
        public static bool RunPerSceneDurationRenderTests(string ffmpeg,string probe) {
            string root=Path.Combine(Path.GetTempPath(),"scene-duration-render-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var ct=System.Threading.CancellationToken.None;
            try {
                var t=new AssemblyTemplate {Width=180,Height=180,Fps=30,SceneTimingVersion=1,Transition="fade",TransitionDuration=.3,RenderMode="cpu"};
                var p=new AssemblyPlan(); int i=0;
                foreach(double seconds in new[]{1.2,2.4,1.8}) {
                    string file=Path.Combine(root,"source"+i+".mp4");
                    Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","color=c="+(i==0?"red":i==1?"blue":"green")+":s=180x180:r=30:d="+Core.N(seconds),"-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration="+Core.N(seconds),"-c:v","libx264","-c:a","aac","-shortest",file},ct).GetAwaiter().GetResult();
                    var scene=new AssemblyScene {Duration=i==1?1:4,DurationByVideo=i!=1};t.Scenes.Add(scene);
                    p.Scenes.Add(new AssemblyScenePlan {Scene=scene,Video=new AssemblyVideo {Path=file,Duration=seconds,HasAudio=true}});i++;
                }
                AssemblyTimeline.Complete(t,p,new Random(1));AssemblyTimeline.ApplyVideoDuration(t,p);
                string output=Path.Combine(root,"output.mp4");AssemblyRender.Run(t,p,ffmpeg,probe,output,Path.Combine(root,"work"),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                var media=Core.VideoInfo(Core.Probe(probe,output,ct).GetAwaiter().GetResult());
                if(Math.Abs(media.VideoDuration-4.6)>.06 || !media.HasAudio)throw new Exception("Mixed per-scene render duration/audio incorrect: "+media.VideoDuration);
                Console.WriteLine("Per-scene real FFmpeg PASS: full 1.2s clip + fixed 1s scene + full 1.8s clip + two transitions = 4.6s, audio present.");
                t.Transition="cut";t.Scenes.Clear();
                var objectScene=new AssemblyScene {Duration=12.5,DurationByVideo=true};t.Scenes.Add(objectScene);
                var pack=new AssemblyLayer {Name="Объект 1",Source=root,X=0,Y=.5,Width=1,Height=.5};
                var extra=new AssemblyLayer {Name="Объект 2",X=0,Y=0,Width=.2,Height=.2};
                objectScene.Layers.AddRange(new[]{pack,extra});
                foreach(double seconds in new[]{2.4,1.8}) {
                    var selected=new AssemblyScenePlan {Scene=objectScene,Video=p.Scenes[0].Video};
                    selected.Layers.Add(new AssemblyLayerPlan {Layer=pack,Asset=new AssemblyAsset {Path=Path.Combine(root,seconds==2.4?"source1.mp4":"source2.mp4")}});
                    selected.Layers.Add(new AssemblyLayerPlan {Layer=extra,Asset=new AssemblyAsset {Path=Path.Combine(root,"source1.mp4")}});
                    var objectPlan=new AssemblyPlan();objectPlan.Scenes.Add(selected);
                    AssemblyTimeline.Complete(t,objectPlan,new Random(1));AssemblyTimeline.ApplyVideoDuration(t,objectPlan);
                    if(Math.Abs(objectPlan.RollDuration(t)-seconds)>.001)throw new Exception("Object duration still follows background or second object");
                    string objectOutput=Path.Combine(root,"object-"+Core.N(seconds)+".mp4");
                    AssemblyRender.Run(t,objectPlan,ffmpeg,probe,objectOutput,Path.Combine(root,"work-"+Core.N(seconds)),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                    var actual=Core.VideoInfo(Core.Probe(probe,objectOutput,ct).GetAwaiter().GetResult());
                    if(Math.Abs(actual.VideoDuration-seconds)>.06)throw new Exception("Object-driven render duration incorrect: "+actual.VideoDuration);
                }
                t.Transition="fade";
                var previewScene=new AssemblyScene {IsPreview=true,Duration=.5};
                var previewObject=new AssemblyLayer {Name="Preview video",X=0,Y=0,Width=1,Height=1};
                previewScene.Layers.Add(previewObject);
                t.Scenes.Insert(0,previewScene);
                var introPlan=new AssemblyScenePlan {Scene=previewScene,Video=p.Scenes[0].Video};
                introPlan.Layers.Add(new AssemblyLayerPlan {Layer=previewObject,Asset=new AssemblyAsset {Path=Path.Combine(root,"source1.mp4")}});
                var mainPlan=new AssemblyScenePlan {Scene=objectScene,Video=p.Scenes[0].Video};
                mainPlan.Layers.Add(new AssemblyLayerPlan {Layer=pack,Asset=new AssemblyAsset {Path=Path.Combine(root,"source2.mp4")}});
                var previewPlan=new AssemblyPlan();previewPlan.Scenes.AddRange(new[]{introPlan,mainPlan});
                AssemblyTimeline.Complete(t,previewPlan,new Random(1));
                AssemblySpeechPlan.Prepare(t,previewPlan,ffmpeg,probe,root,ct).GetAwaiter().GetResult();
                AssemblySpeechPlan.Prepare(t,previewPlan,ffmpeg,probe,root,ct).GetAwaiter().GetResult();
                if(Math.Abs(introPlan.ContentSeconds-2.4)>.001 || Math.Abs(mainPlan.ContentSeconds-1.8)>.001 || Math.Abs(AssemblyTimeline.MainStart(t,previewPlan)-2.7)>.001)throw new Exception("Object preview/main source durations or transition boundary incorrect");
                string previewOutput=Path.Combine(root,"preview-object.mp4");
                AssemblyRender.Run(t,previewPlan,ffmpeg,probe,previewOutput,Path.Combine(root,"preview-work"),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                if(Math.Abs(Core.VideoInfo(Core.Probe(probe,previewOutput,ct).GetAwaiter().GetResult()).VideoDuration-4.5)>.06)throw new Exception("Full preview object + transition + main object render duration incorrect");
                Console.WriteLine("Preview object real FFmpeg PASS: stored .5s scene plays full 2.4s object, .3s transition, full 1.8s main object; repeated preparation preserves timing.");
                var migration=new AssemblyTemplate {Materials=Path.Combine(root,"materials"),Videos=Path.Combine(root,"source0.mp4"),Backgrounds=Path.Combine(root,"source1.mp4"),PackStudioVersion=1,ObjectStudioVersion=1};
                AssemblyWorkspace.Prepare(migration);
                if(migration.Backgrounds!="" || AssemblyFiles.Pool(migration.Resolve(migration.Videos),AssemblyFiles.VideoExtensions).Count!=2)throw new Exception("Background sources not unified");
                string migrated=migration.Videos;AssemblyWorkspace.Prepare(migration);
                if(migration.Videos!=migrated)throw new Exception("Repeated background migration");
                var emptyOwn=new AssemblyScene {VideoMode="own",Videos=Path.Combine(root,"empty-own")};
                Directory.CreateDirectory(emptyOwn.Videos);
                if(migration.VideoSource(emptyOwn)!=emptyOwn.Videos)throw new Exception("Cleared scene silently reused shared background");
                migration.Videos="";migration.Scenes.Add(objectScene);
                if(migration.VideoSource(objectScene)!=root)throw new Exception("Scene objects fallback without background failed");
                AssemblyWorkspace.ClearBackgroundVideos(migration);
                if(AssemblyFiles.Pool(migration.Resolve(migration.Videos),AssemblyFiles.VideoExtensions).Count!=0 || migration.Backgrounds!="")throw new Exception("Background clear retained media");
                migration.Scenes.Clear();AssemblyWorkspace.Insert(migration,0);AssemblyWorkspace.Prepare(migration);
                var restored=Store.Clone(migration);AssemblyWorkspace.Prepare(restored);
                if(AssemblyFiles.Pool(restored.Resolve(restored.VideoSource(restored.Scenes[0])),AssemblyFiles.VideoExtensions).Count!=0)throw new Exception("Cleared video returned after new scene/reload");
                Console.WriteLine("Clear PASS: own source does not fall back to shared video; clear background + remove scenes + add scene + XML reload stays empty.");
                Console.WriteLine("Object duration real FFmpeg PASS: 1.2s main video, first object variants 2.4/1.8s, independent second object; output follows selected first object. Background migration and object-only source fallback PASS.");return true;
            } finally {Directory.Delete(root,true);}
        }
        public static bool RunPerSceneDurationTests() {
            var t=new AssemblyTemplate {SceneTimingVersion=1,Transition="fade",TransitionDuration=.3};
            var intro=new AssemblyScene {IsPreview=true,Duration=3};
            var a=new AssemblyScene {Duration=4,DurationByVideo=true};
            var b=new AssemblyScene {Duration=5};
            var c=new AssemblyScene {Duration=4,DurationByVideo=true};
            t.Scenes.AddRange(new[]{intro,a,b,c}); t.Validate();
            var p=new AssemblyPlan();
            foreach(var scene in t.Scenes)p.Scenes.Add(new AssemblyScenePlan {Scene=scene,Video=new AssemblyVideo {Duration=scene==a?18:scene==c?35:10}});
            AssemblyTimeline.Complete(t,p,new Random(1)); AssemblyTimeline.ApplyVideoDuration(t,p);
            if(AssemblyTimeline.SceneSeconds(p.Scenes[0])!=10 || AssemblyTimeline.SceneSeconds(p.Scenes[1])!=18 || AssemblyTimeline.SceneSeconds(p.Scenes[2])!=5 || AssemblyTimeline.SceneSeconds(p.Scenes[3])!=35 || Math.Abs(p.RollDuration(t)-68.9)>.001)throw new Exception("Per-scene duration or transition sum failed");
            var xml=new System.Xml.Serialization.XmlSerializer(typeof(AssemblyTemplate));var text=new StringWriter();xml.Serialize(text,t);
            var copy=(AssemblyTemplate)xml.Deserialize(new StringReader(text.ToString()));
            AssemblyMarketProfiles.Capture(copy,AssemblyProfile.Ru);AssemblyMarketProfiles.Apply(copy,AssemblyProfile.Ru);
            if(!copy.Scenes[1].DurationByVideo || copy.Scenes[2].DurationByVideo || !copy.Scenes[3].DurationByVideo)throw new Exception("Scene timing XML/profile copy failed");
            var legacy=new AssemblyTemplate {DurationByVideo=true};legacy.Scenes.AddRange(new[]{new AssemblyScene {IsPreview=true},new AssemblyScene(),new AssemblyScene()});
            legacy.MigrateVideoDuration();if(legacy.DurationByVideo || !legacy.Scenes[1].DurationByVideo || legacy.Scenes[2].DurationByVideo)throw new Exception("Legacy duration migration failed");
            Console.WriteLine("Per-scene duration PASS: full 10s intro, 18s video, fixed 5s scene, 35s video, transitions, XML and legacy migration.");return true;
        }
        public static bool RunTransparentEntranceTests(string ffmpeg, string root) {
            string picture=Path.Combine(root,"transparent-entrance.png"), output=Path.Combine(root,"transparent-entrance.rgba");
            const int width=96, height=160, frameBytes=width*height*4, frames=15;
            string Sample(string source,string scale) {
                string xy="min(W-1,max(0,((X-W/2)/"+scale+"+W/2))),min(H-1,max(0,((Y-H/2)/"+scale+"+H/2)))";
                return "if(eq(PLANE,0),"+source+"0("+xy+"),if(eq(PLANE,1),"+source+"1("+xy+"),if(eq(PLANE,2),"+source+"2("+xy+"),"+source+"3("+xy+"))))";
            }
            try {
                using(var image=new Bitmap(width,height,System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
                    for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
                        int alpha=x>=13 && x<75 && y>=51 && y<109 ? new[]{1,64,128,220,255}[(x+y)%5] : 0;
                        image.SetPixel(x,y,Color.FromArgb(alpha,(x*7)%256,(y*11)%256,((x+y)*13)%256));
                    }
                    image.Save(picture,System.Drawing.Imaging.ImageFormat.Png);
                }
                foreach(string effect in new[]{"cut","fade","swipe","smoothleft","zoomin"})
                    if(AssemblyEngine.TransitionFilter(effect)!=AssemblyEngine.TransitionFilter(effect,true)) throw new Exception("Non-custom entrance changed: "+effect);
                foreach(string effect in new[]{"zoomsoft","zoomout","pull"}) {
                    string strength=effect=="pull"?"1.4":effect=="zoomout"?".22":".28";
                    string outgoing="(1"+(effect=="zoomout"?"-":"+")+strength+"*(1-P)*(1-P))", incoming="(1+"+strength+"*P*P)", weight="(P*P*(3-2*P))";
                    string expected="transition=custom:expr='"+Sample("a",outgoing)+"*"+weight+"+"+Sample("b",incoming)+"*(1-"+weight+")'";
                    string baseline=AssemblyEngine.TransitionFilter(effect), optimized=AssemblyEngine.TransitionFilter(effect,true);
                    if(baseline!=expected) throw new Exception("Scene transition expression changed: "+effect);
                    if(optimized!="transition=custom:expr='"+Sample("b",incoming)+"*(1-"+weight+")'") throw new Exception("Overlay expression did not remove only the zero term: "+effect);
                    var template=new AssemblyTemplate {Width=width,Height=height,Fps=30};
                    var layer=new AssemblyLayerPlan {Layer=new AssemblyLayer {AppearanceTransition=effect,AppearanceDuration=.35},Asset=new AssemblyAsset {Path=picture},Raster=picture};
                    var graph=new System.Collections.Generic.List<string>(); var args=new System.Collections.Generic.List<string>(); int input=0;
                    AssemblyRender.AppendLayerOverlay(template,layer,0,.5,"1","base","over","yuv420",args,ref input,graph);
                    if(!string.Join(";",graph).Contains("xfade="+optimized+":duration=")) throw new Exception("Static overlay did not select specialized expression: "+effect);
                    graph.Clear(); args.Clear();
                    // Exercise animated graph generation with the same known-size image fixture; moving pixels are checked below.
                    typeof(AssemblyRender).GetMethod("AppendAnimatedLayerOverlay",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,
                        new object[]{template,layer,0d,.5,"1","base","over","yuv420",args,0,graph});
                    if(!string.Join(";",graph).Contains("xfade="+optimized+":duration=")) throw new Exception("Animated overlay did not select specialized expression: "+effect);
                    foreach(bool moving in new[]{false,true}) {
                        byte[] reference=null;
                        foreach(string transition in new[]{baseline,optimized}) {
                            string filters="color=c=black@0:s=96x160:r=30:d=0.5,format=gbrap,settb=1/30[a];[0:v]format=gbrap,loop=loop=-1:size=1:start=0,trim=duration=0.5,setpts=N/(30*TB),fps=30,settb=1/30"
                                +(moving?",hflip=enable='gte(n,6)'":"")+",premultiply=inplace=1[b];[a][b]xfade="+transition+":duration=0.35:offset=0,unpremultiply=inplace=1,format=rgba[o]";
                            Core.Tool(ffmpeg,new[]{"-v","error","-nostdin","-y","-filter_complex_threads","1","-threads","1","-framerate","30","-i",picture,"-filter_complex",filters,"-map","[o]","-frames:v","15","-threads","1","-f","rawvideo",output},System.Threading.CancellationToken.None,null,30).GetAwaiter().GetResult();
                            var pixels=File.ReadAllBytes(output);
                            if(pixels.Length!=frameBytes*frames) throw new Exception("Transition frame count changed");
                            if(reference==null) reference=pixels;
                            else if(!reference.SequenceEqual(pixels)) throw new Exception("RGBA transition frames differ: "+effect+", moving="+moving);
                        }
                        for(int pos=3;pos<frameBytes;pos+=4) if(reference[pos]!=0) throw new Exception("First frame is not transparent");
                        foreach(int frame in new[]{5,14}) {
                            bool alpha=false;
                            for(int pos=frame*frameBytes+3;pos<(frame+1)*frameBytes;pos+=4) if(reference[pos]>0 && reference[pos]<255) {alpha=true;break;}
                            if(!alpha) throw new Exception("Partial alpha lost at frame "+frame);
                        }
                        Console.WriteLine("PASS transparent entrance: "+effect+", moving="+moving+", all 15 RGBA frames byte-identical (first/middle/last included)");
                    }
                }
                return true;
            } finally {if(File.Exists(picture))File.Delete(picture);if(File.Exists(output))File.Delete(output);}
        }
        public static bool RunImageAppearanceTests() {
            string root = Path.Combine(Path.GetTempPath(), "image-timing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                string video = Path.Combine(root, "one.mp4"); File.WriteAllText(video, "video");
                string other = Path.Combine(root, "two.mp4"); File.WriteAllText(other, "other");
                string images = Path.Combine(root, "images"); Directory.CreateDirectory(images);
                string image = Path.Combine(images, "badge.png"); File.WriteAllText(image, "image");
                File.WriteAllText(Path.Combine(images, "second.png"), "second image");
                var layer = new AssemblyLayer { Source=images, Start=8, AppearanceTransition="swipe", AppearanceDuration=.5, GreenScreen=true, PlaybackMode="repeat", RepeatPause=120, PhotoDuration=4.5, ExitTransition="fade", ExitDuration=.7 };
                var peer = new AssemblyLayer { X=.3, End=20, Source="unchanged" };
                peer.CopyAnimationFrom(layer, true);
                if (!peer.IndividualAnimation || peer.Start != 8 || peer.AppearanceTransition != "swipe" || peer.PlaybackMode != "repeat" || peer.ExitDuration != .7 || peer.X != .3 || peer.End != 20 || peer.Source != "unchanged") throw new Exception("Individual animation copied unrelated object fields");
                layer.Start = 0; layer.AppearanceTransition = "cut";
                peer.CopyAnimationFrom(layer, false);
                if (peer.IndividualAnimation || peer.Start != 0 || peer.AppearanceTransition != "cut") throw new Exception("Common animation did not overwrite individual settings");
                var fresh = new AssemblyLayer();
                if (fresh.Start != 0 || fresh.PlaybackMode != "continuous" || fresh.AppearanceTransition != "cut" || fresh.ExitTransition != "cut" || fresh.IndividualAnimation) throw new Exception("New object must show immediately for the whole scene");
                layer.Start = 8; layer.AppearanceTransition = "swipe"; layer.IndividualAnimation = true;
                var t = new AssemblyTemplate { Materials=root, Videos=root, DurationByVideo=true, Transition="cut", RandomVideoStart=true };
                t.Scenes.Add(new AssemblyScene { Duration=4, Layers=new System.Collections.Generic.List<AssemblyLayer> {layer} });
                var videos = new[] { new AssemblyVideo { Path=video, Hash="a", Duration=.6 }, new AssemblyVideo { Path=other, Hash="b", Duration=71.25 } };
                var mix = new AssemblyMixTracker();
                var history = new AssemblyHistory();
                var capacity = new AssemblyInventory(t,videos,history).Calculate();
                if (capacity.BatchLimit != 4 || capacity.Variants != 4) throw new Exception("Source-length mode capped combinations by video count");
                var batch = AssemblyPlanner.Create(t,videos,history,new Random(2),3,mix);
                if (batch.Plans.Count != 3 || batch.Plans.Any(p => p.RollDuration(t) != p.Scenes[0].Video.Duration || p.Scenes[0].VideoStart != 0)) throw new Exception("Requested count or source duration failed");
                history.Combinations.AddRange(batch.Plans.Select(p=>p.Signature));
                string ledger = Path.Combine(root,"history.xml"); AssemblyFiles.Save(ledger,history); history=AssemblyFiles.Load<AssemblyHistory>(ledger);
                var remaining = AssemblyPlanner.Create(t,videos,history,new Random(3),10,mix);
                if (remaining.Plans.Count != 1 || remaining.Plans.Any(p=>history.Combinations.Contains(p.Signature))) throw new Exception("Combination history was not preserved");
                history.Combinations.Add(remaining.Plans[0].Signature);
                if (new AssemblyInventory(t,videos,history).Calculate().BatchLimit != 0) throw new Exception("Used combinations still available");
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.Ru);
                string path = Path.Combine(root,"settings.xml"); AssemblyFiles.Save(path,t);
                var copy = AssemblyFiles.Load<AssemblyTemplate>(path); AssemblyMarketProfiles.Apply(copy,AssemblyProfile.Ru);
                var restored = copy.Scenes[0].Layers[0];
                if (!restored.IndividualAnimation) throw new Exception("Individual animation marker lost in XML");
                if (!copy.DurationByVideo || restored.Start != 8 || restored.AppearanceTransition != "swipe" || restored.AppearanceDuration != .5 || !restored.GreenScreen || restored.PlaybackMode!="repeat" || restored.RepeatPause!=120 || restored.PhotoDuration!=4.5 || restored.ExitTransition!="fade" || restored.ExitDuration!=.7) throw new Exception("Timing settings lost in profile/XML");
                return true;
            } finally { Directory.Delete(root,true); }
        }
        public static bool RunSplitSeamRenderTests(string ffmpeg, string probe) {
            string root = Path.Combine(Path.GetTempPath(), "split-seam-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var ct = System.Threading.CancellationToken.None;
            try {
                string red = Path.Combine(root, "red.mp4"), blue = Path.Combine(root, "blue.mp4");
                foreach (string color in new[] { "red", "blue" })
                    Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=" + color + ":s=320x240:r=20:d=0.4", "-c:v", "libx264", Path.Combine(root, color + ".mp4") }, ct).GetAwaiter().GetResult();
                foreach (string layout in new[] { "horizontal", "vertical" }) foreach (double ratio in new[] { .3, .7 }) foreach (double softness in new[] { 0d, .5 }) {
                    var t = new AssemblyTemplate { Width=320, Height=240, Fps=20, SplitLayout=layout, SplitRatio=ratio, SplitSoftness=softness, Transition="cut", RenderMode="cpu", SceneTimingVersion=1 };
                    bool vertical = AssemblySplit.IsVertical(layout);
                    AssemblySplit.PaneSizes(t, out _, out _, out int line, out int feather);
                    if (softness == 0 && feather != 0) throw new Exception("Zero softness is not a hard seam.");
                    string graph = "[0:v]" + AssemblySplit.PaneFilter(t, false, 1) + "[splitA];[1:v]" + AssemblySplit.PaneFilter(t, true, 1) + "[splitB];" + AssemblySplit.BlendFilter(vertical, line, feather, t.Width, t.Height);
                    if (!graph.Contains("maskedmerge") || !graph.Contains("trim=end_frame=1") || graph.Contains("blend=all_expr")) throw new Exception("Split lost its precomputed native mask.");
                    string name = layout + ratio + "-" + softness;
                    string preview = Path.Combine(root, "preview.png");
                    Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-filter_complex_threads", "1", "-i", red, "-i", blue, "-filter_complex", graph, "-map", "[split]", "-frames:v", "1", "-threads", "1", preview }, ct).GetAwaiter().GetResult();
                    void Check(string frame) {
                        using (var image = new Bitmap(frame)) {
                            for (int pos = Math.Max(0, line - feather / 2 - 4); pos <= Math.Min((vertical ? t.Height : t.Width) - 1, line + feather / 2 + 4); pos++) {
                                var c = image.GetPixel(vertical ? t.Width / 2 : pos, vertical ? pos : t.Height / 2);
                                double weight = feather == 0 ? (pos < line ? 0 : 1) : Math.Max(0, Math.Min(1, (pos - line + feather / 2.0) / feather));
                                int tolerance = feather == 0 && Math.Abs(pos - line) <= 4 ? 60 : 25;
                                if (Math.Abs(c.R - 253 * (1 - weight)) > tolerance || Math.Abs(c.B - 254 * weight) > tolerance || c.G > 25 || c.R + c.B < 210)
                                    throw new Exception(name + " seam contains black or wrong color at " + pos + ": " + c);
                                if (pos == line && feather > 0 && (c.R < 105 || c.B < 105)) throw new Exception(name + " middle does not contain both videos.");
                            }
                        }
                    }
                    Check(preview);
                    var scene = new AssemblyScene { Duration=.4 }; t.Scenes.Add(scene);
                    var plan = new AssemblyPlan { SecondaryVideo=new AssemblyVideo { Path=blue, Duration=.4 } };
                    plan.Scenes.Add(new AssemblyScenePlan { Scene=scene, Video=new AssemblyVideo { Path=red, Duration=.4 } });
                    AssemblyTimeline.Complete(t, plan, new Random(1));
                    string output = Path.Combine(root, name + ".mp4");
                    AssemblyRender.Run(t, plan, ffmpeg, probe, output, Path.Combine(root, name), AssemblyRaster.Save, ct, null).GetAwaiter().GetResult();
                    string framePath = Path.Combine(root, "export.png");
                    Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-ss", "0.2", "-i", output, "-frames:v", "1", "-threads", "1", framePath }, ct).GetAwaiter().GetResult();
                    Check(framePath);
                    var info = Core.VideoInfo(Core.Probe(probe, output, ct).GetAwaiter().GetResult());
                    if (Math.Abs(info.VideoDuration - .4) > .06) throw new Exception(name + " split changed duration.");
                }
                foreach (string layout in new[] { "horizontal", "vertical" }) foreach (double ratio in new[] { .3, .7 }) foreach (double zoom in new[] { 1d, 1.15 }) foreach (bool secondary in new[] { false, true }) {
                    var t = new AssemblyTemplate { Width=320, Height=240, Fps=20, SplitLayout=layout, SplitRatio=ratio };
                    string[] frames = new string[2];
                    for (int variant = 0; variant < 2; variant++) {
                        t.SplitSoftness = variant == 0 ? 0 : .5;
                        frames[variant] = Path.Combine(root, "framing" + variant + ".png");
                        Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x480:r=20:d=0.1", "-vf", AssemblySplit.PaneFilter(t, secondary, zoom), "-frames:v", "1", "-threads", "1", frames[variant] }, ct).GetAwaiter().GetResult();
                    }
                    AssemblySplit.PaneSizes(t, out _, out _, out int line, out _);
                    bool vertical = AssemblySplit.IsVertical(layout);
                    using (var hard = new Bitmap(frames[0])) using (var soft = new Bitmap(frames[1]))
                        for (int y = 4; y < t.Height - 4; y += 7) for (int x = 4; x < t.Width - 4; x += 7) {
                            int pos = vertical ? y : x;
                            if (secondary ? pos < line + 4 : pos >= line - 4) continue;
                            var a = hard.GetPixel(x, y); var b = soft.GetPixel(x, y);
                            if (Math.Abs(a.R-b.R)>4 || Math.Abs(a.G-b.G)>4 || Math.Abs(a.B-b.B)>4) throw new Exception("Soft seam moved/scaled original pane: " + layout + " ratio=" + ratio + " zoom=" + zoom + " secondary=" + secondary + " at " + x + "," + y + " " + a + " vs " + b);
                        }
                }
                Console.WriteLine("Split seam real FFmpeg PASS: preview and full export, both orientations, 30/70 and 70/30, zero/wide softness, RGB gradient without black, unchanged duration and original pane framing/scale at zoom 1/1.15.");
                return true;
            } finally { Directory.Delete(root, true); }
        }

        public static bool RunSplitPackTests() {
            string root = Path.Combine(Path.GetTempPath(), "split-packs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try {
                string a = Path.Combine(root, "a.mp4"), b = Path.Combine(root, "b.mp4"), copy = Path.Combine(root, "renamed.mp4");
                File.WriteAllText(a, "one"); File.WriteAllText(b, "two"); File.Copy(a, copy);
                var videos = new[] { a, b, copy }.Select(path => new AssemblyVideo { Path = path, Hash = AssemblyFiles.Hash(path), Duration = 10 }).ToList();
                var t = new AssemblyTemplate { Materials = root, Videos = root, SecondaryVideos = root, SplitLayout = "vertical", AllowVideoReuse = true, Transition = "cut" };
                t.Scenes.Add(new AssemblyScene { Duration = 2 });
                var plans = AssemblyPlanner.Create(t, videos, new AssemblyHistory(), new Random(4), 10).Plans;
                if (plans.Count != 2 || plans.Any(p => p.Scenes.Any(s => s.Video.Hash == p.SecondaryVideo.Hash))) throw new Exception("Duplicate clip in both panes");
                var history = new AssemblyHistory(); history.Combinations.AddRange(plans.Select(p => p.Signature));
                if (AssemblyPlanner.Create(t, videos, history, new Random(4), 10).Plans.Count != 0) throw new Exception("Repeated split recipe");
                // Exercise non-enumerating search too, both with and without split.
                foreach (bool split in new[] { false, true }) {
                    t.SplitLayout = split ? "vertical" : "none";
                    var many = Enumerable.Range(0, 260).Select(i => new AssemblyVideo { Path = a, Hash = "hash" + i, Duration = 10 }).ToList();
                    t.Scenes.Add(new AssemblyScene { Duration = 2 });
                    if (AssemblyPlanner.Create(t, many, new AssemblyHistory(), new Random(2), 2).Plans.Count != 2) throw new Exception("Large inventory search failed");
                    t.Scenes.RemoveAt(1);
                }
                // A secondary-only file must also be eligible as the primary in shared mode.
                t.SplitLayout = "vertical"; t.Videos = a; t.SecondaryVideos = b; t.SplitSharedPool = true;
                plans = AssemblyPlanner.Create(t, videos, new AssemblyHistory(), new Random(2), 10).Plans;
                if (plans.Count != 2 || plans.Select(p => p.Scenes[0].Video.Hash).Distinct().Count() != 2) throw new Exception("Shared pool is one-sided");
                t.PrimaryNativeAudio = false; t.SecondaryNativeVolume = .37;
                AssemblyMarketProfiles.Capture(t, AssemblyProfile.Ru);
                string saved = Path.Combine(root, "template.xml"); AssemblyFiles.Save(saved, t);
                var restored = AssemblyFiles.Load<AssemblyTemplate>(saved);
                if (restored.PrimaryNativeAudio || Math.Abs(restored.SecondaryNativeVolume - .37) > .001) throw new Exception("Native audio settings lost");
                return true;
            } finally { Directory.Delete(root, true); }
        }
        public static bool RunPreviewAudioTests(string ffmpeg, string probe) {
            string root=Path.Combine(Path.GetTempPath(),"preview-audio-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var ct=System.Threading.CancellationToken.None;
            try {
                string intro=Path.Combine(root,"intro.mp4"), main=Path.Combine(root,"main.mp4"), music=Path.Combine(root,"music.wav");
                foreach(var item in new[]{new {Path=intro,Color="blue",Hz=440,Seconds=1},new {Path=main,Color="red",Hz=880,Seconds=2}})
                    Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","color=c="+item.Color+":s=180x180:r=30:d="+item.Seconds,"-f","lavfi","-i","sine=frequency="+item.Hz+":sample_rate=48000:duration="+item.Seconds,"-c:v","libx264","-c:a","aac","-shortest",item.Path},ct).GetAwaiter().GetResult();
                Core.Tool(ffmpeg,new[]{"-v","error","-y","-f","lavfi","-i","sine=frequency=1320:sample_rate=48000:duration=3",music},ct).GetAwaiter().GetResult();
                var t=new AssemblyTemplate {Width=180,Height=180,Fps=30,SceneTimingVersion=1,DurationByVideo=true,Transition="fade",TransitionDuration=.3,RenderMode="cpu",PrimaryNativeAudio=true,PrimaryNativeVolume=1,MusicVolume=1};
                var a=new AssemblyScene {IsPreview=true,Duration=1,VideoMode="own",Videos=intro}; var b=new AssemblyScene {Duration=2,VideoMode="own",Videos=main}; t.Scenes.Add(a);t.Scenes.Add(b);
                var p=new AssemblyPlan {Music=music}; p.Scenes.Add(new AssemblyScenePlan {Scene=a,Video=new AssemblyVideo {Path=intro,Duration=1,HasAudio=true}});p.Scenes.Add(new AssemblyScenePlan {Scene=b,Video=new AssemblyVideo {Path=main,Duration=2,HasAudio=true}});
                AssemblyTimeline.Complete(t,p,new Random(1)); AssemblyTimeline.ApplyVideoDuration(t,p);
                if(Math.Abs(p.Scenes[1].IncomingPad-.3)>.001 || Math.Abs(AssemblyTimeline.MainStart(t,p)-1.3)>.001)throw new Exception("Preview transition pad/main start lost");
                string output=Path.Combine(root,"result.mp4");
                AssemblyRender.Run(t,p,ffmpeg,probe,output,Path.Combine(root,"work"),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                double Amplitude(double time,int hz) {
                    string pcm=Path.Combine(root,"sample.pcm");
                    Core.Tool(ffmpeg,new[]{"-v","error","-y","-ss",Core.N(time),"-i",output,"-t","0.2","-vn","-ac","1","-ar","48000","-f","s16le",pcm},ct).GetAwaiter().GetResult();
                    byte[] bytes=File.ReadAllBytes(pcm); double re=0,im=0; int n=bytes.Length/2;
                    for(int i=0;i<n;i++){double value=BitConverter.ToInt16(bytes,i*2)/32768d; double phase=2*Math.PI*hz*i/48000;re+=value*Math.Cos(phase);im+=value*Math.Sin(phase);}return 2*Math.Sqrt(re*re+im*im)/n;
                }
                double own=Amplitude(.3,440), earlyMain=Amplitude(.3,880), earlyMusic=Amplitude(.3,1320), laterMain=Amplitude(1.4,880), laterMusic=Amplitude(1.6,1320);
                if(own<.04 || earlyMain>.005 || earlyMusic>.005 || laterMain<.04 || laterMusic<.04)throw new Exception("Audio overlap: "+string.Join(",",own,earlyMain,earlyMusic,laterMain,laterMusic));
                // Stress the real settings that previously leaked into intro audio.
                t.VoicePitchSemitones=1.8;t.PrimaryNativeVolume=.5;
                t.OutputProcessing=new ProcessingSettings();
                t.OutputProcessing.Pitch.Enabled=true;t.OutputProcessing.Pitch.Values=new Range(120,120);
                t.OutputProcessing.Volume.Enabled=true;t.OutputProcessing.Volume.Values=new Range(50,50);
                t.OutputProcessing.Speed.Enabled=true;t.OutputProcessing.Speed.Values=new Range(102,102);
                AssemblyRender.Run(t,p,ffmpeg,probe,output,Path.Combine(root,"stress"),AssemblyRaster.Save,ct,null).GetAwaiter().GetResult();
                AssemblyProcessing.Apply(t,ffmpeg,probe,output,new Random(4),ct,AssemblyTimeline.MainStart(t,p)).GetAwaiter().GetResult();
                double cleanIntro=Amplitude(.3,440), shiftedIntro=Amplitude(.3,880), changedMain=Amplitude(1.7,(int)Math.Round(880*Math.Pow(2,1.8/12)*1.2));
                if(cleanIntro<.07 || shiftedIntro>.005 || changedMain<.01)throw new Exception("Intro effects leaked or main effects missing: "+string.Join(",",cleanIntro,shiftedIntro,changedMain));
                if(Math.Abs(Core.VideoInfo(Core.Probe(probe,output,ct).GetAwaiter().GetResult()).VideoDuration-3.3)>.06)throw new Exception("Postprocessing shifted intro boundary");
                t.SubtitleStyle.Enabled=true;t.Captions.Add(new AssemblyCaption {Text="Main",Start=.1,End=.5});
                AssemblySpeechPlan.Prepare(t,p,ffmpeg,probe,Path.Combine(root,"work"),ct).GetAwaiter().GetResult();
                if(p.Captions.Count!=1 || Math.Abs(p.Captions[0].Start-1.4)>.01)throw new Exception("Main subtitles were not shifted after intro");
                AssemblySpeechPlan.Prepare(t,p,ffmpeg,probe,Path.Combine(root,"work"),ct).GetAwaiter().GetResult();
                if(Math.Abs(p.Captions[0].Start-1.4)>.01)throw new Exception("Subtitle shift accumulated on repeat");
                string fusedWork=Path.Combine(root,"fused");Directory.CreateDirectory(fusedWork);
                WarmupSubtitles.Rasterize(p.Captions,t.SubtitleStyle,fusedWork,t.Width,t.Height);
                var fusedProcessing=AssemblyProcessing.Prepare(t,new Media {Width=t.Width,Height=t.Height,Fps=t.Fps+"/1",Duration=p.RollDuration(t)},new Random(4),AssemblyTimeline.MainStart(t,p));
                Core.Tool(ffmpeg,AssemblyRender.Arguments(t,p,"libx264",Path.Combine(fusedWork,"render.ffgraph"),output,new AssemblyRenderContext(),fusedProcessing),ct).GetAwaiter().GetResult();
                cleanIntro=Amplitude(.3,440); changedMain=Amplitude(1.7,(int)Math.Round(880*Math.Pow(2,1.8/12)*1.2));
                if(cleanIntro<.07||changedMain<.01||Math.Abs(Core.VideoInfo(Core.Probe(probe,output,ct).GetAwaiter().GetResult()).VideoDuration-3.3)>.06)throw new Exception("Fused intro protection or main audio timing");
                string fusedGraph=File.ReadAllText(Path.Combine(root,"fused","render.ffgraph"));
                if(fusedGraph.IndexOf("[captioned]format=yuv420p[vout]",StringComparison.Ordinal)<0||!fusedGraph.Contains("[vout]setpts="))throw new Exception("Fused effects moved before captions");
                Console.WriteLine("Preview real FFmpeg: raw and fused intro protection, main effects, captions before processing, repeat stable");return true;
            } finally {Directory.Delete(root,true);}
        }
        public static bool RunPreviewSceneTests() {
            string temp = Path.Combine(Path.GetTempPath(), "preview-scene-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp);
                var preview = new AssemblyScene { Name = "Превью", IsPreview = true, Duration = 2, VideoMode = "own", Folder = "Сцены/Превью" };
                preview.Videos = Path.Combine(preview.Folder, "Видео");
                var main = new AssemblyScene { Name = "Сцена 1", Duration = 5, Folder = "Сцены/Сцена-01" };
                var t = new AssemblyTemplate { Materials = temp, Videos = "Видео", DurationByVideo = true, SceneTimingVersion = 1, Transition = "fade", TransitionDuration = .35 };
                t.Scenes.Add(preview); t.Scenes.Add(main);
                AssemblyWorkspace.Prepare(t);
                t.Validate();
                string previewVideo = t.Resolve(Path.Combine(preview.Videos, "intro.mp4"));
                string mainVideo = t.Resolve("Видео/main.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(previewVideo)); Directory.CreateDirectory(Path.GetDirectoryName(mainVideo));
                File.WriteAllText(previewVideo, "pv"); File.WriteAllText(mainVideo, "mn");
                var videos = new[] {
                    new AssemblyVideo { Path = previewVideo, Hash = "pv", Duration = 2.4 },
                    new AssemblyVideo { Path = mainVideo, Hash = "mn", Duration = 9.1 }
                };
                var batch = AssemblyPlanner.Create(t, videos, new AssemblyHistory(), new Random(3), 1);
                if (batch.Plans.Count != 1) throw new Exception("Preview scene planner failed: " + batch.Limit);
                var plan = batch.Plans[0];
                if (plan.Scenes.Count != 2 || !plan.Scenes[0].Scene.IsPreview || plan.Scenes[0].ContentSeconds != 2.4 || plan.Scenes[1].ContentSeconds != 9.1) throw new Exception("Preview/main durations not applied");
                if (Math.Abs(plan.RenderDuration - (2.4 + 9.1 + t.TransitionSeconds)) > .001) throw new Exception("Roll duration with preview: " + plan.RenderDuration);
                if (Math.Abs(plan.Scenes[0].RenderDuration - (2.4 + t.TransitionSeconds)) > .001 || Math.Abs(AssemblyTimeline.MainStart(t, plan) - (2.4 + t.TransitionSeconds)) > .001) throw new Exception("Full preview lost transition padding/main start");
                t.DurationByVideo = false;
                preview.Duration = 5; // Stored editor hint must not reject or extend a shorter selected clip.
                batch = AssemblyPlanner.Create(t, videos, new AssemblyHistory(), new Random(3), 1);
                if (batch.Plans.Count != 1) throw new Exception("Short preview source rejected: " + batch.Limit);
                plan = batch.Plans[0];
                if (!t.HasVideoDuration || !t.UsesVideoDuration(preview) || plan.Scenes[0].ContentSeconds != 2.4 || AssemblyTimeline.SceneSeconds(plan.Scenes[1]) != 5) throw new Exception("Preview-only source timing changed manual main duration");
                AssemblySpeechPlan.Prepare(t, plan, "", "", temp, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                AssemblySpeechPlan.Prepare(t, plan, "", "", temp, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                if (plan.Scenes[0].ContentSeconds != 2.4 || Math.Abs(plan.RollDuration(t) - (2.4 + 5 + t.TransitionSeconds)) > .001) throw new Exception("Repeated preparation truncated preview");
                t.Scenes[1].IsPreview = true;
                try { t.Validate(); return false; } catch (Exception) { }
                return true;
            } finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }
        public static bool RunPlannerTests() {
            string temp = Path.Combine(Path.GetTempPath(), "assembly-tests-" + Guid.NewGuid().ToString("N"));
            try {
                var t = LegacyTemplate(); t.Materials = temp; t.Output = Path.Combine(temp, "output"); t.Validate();
                if (t.Scenes.Count != 3 || t.Duration != 17 || !t.Scenes[2].Layers[0].Fixed || !t.Scenes[0].Layers[0].Unique || !t.Scenes[1].Layers[0].Unique) return false;
                foreach (string folder in new[] { "Видео", "Торговля", "Заголовки", "Заголовки-2", "TG-бот", "Призыв" }) Directory.CreateDirectory(Path.Combine(temp, folder));
                for (int i = 0; i < 4; i++) File.WriteAllText(Path.Combine(temp, "Видео", i + ".mp4"), "video" + i);
                for (int i = 0; i < 2; i++) File.WriteAllText(Path.Combine(temp, "Торговля", i + ".mp4"), "trade" + i);
                foreach (string folder in new[] { "Заголовки", "Заголовки-2" }) for (int i = 0; i < 3; i++) File.WriteAllText(Path.Combine(temp, folder, i + ".txt"), folder + i);
                File.WriteAllText(Path.Combine(temp, "Заголовки", "renamed.txt"), "Заголовки0\r\n");
                File.WriteAllText(Path.Combine(temp, "TG-бот", "bot.txt"), "fixed bot"); File.WriteAllText(Path.Combine(temp, "Призыв", "cta.txt"), "shared CTA");
                var videos = Directory.GetFiles(temp, "*.mp4", SearchOption.AllDirectories).Select(p => new AssemblyVideo { Path = p, Hash = AssemblyFiles.Hash(p), Duration = 12 }).ToList();
                var history = new AssemblyHistory(); var batch = AssemblyPlanner.Create(t, videos, history, new Random(14), 2);
                if (batch.Plans.Count != 2 || batch.Plans.SelectMany(p => p.UniqueImages).Distinct().Count() != 4) return false;
                foreach (var plan in batch.Plans) {
                    if (plan.Scenes.Select(s => s.Video.Hash).Distinct().Count() != 3) return false;
                    if (Path.GetDirectoryName(plan.Scenes[1].Video.Path) != Path.Combine(temp, "Торговля")) return false;
                    if (plan.Scenes[0].RenderDuration != 5.35 || plan.Scenes[1].RenderDuration != 6.35 || plan.Scenes[2].RenderDuration != 6) return false;
                    history.UsedImages.AddRange(plan.UniqueImages); history.Combinations.Add(plan.Signature);
                }
                string path = Path.Combine(temp, "history.xml"); AssemblyFiles.Save(path, history); history = AssemblyFiles.Load<AssemblyHistory>(path);
                var next = AssemblyPlanner.Create(t, videos, history, new Random(22), 10);
                if (next.Plans.Count != 1 || next.Limit == "" || next.Plans[0].UniqueImages.Any(h => history.UsedImages.Contains(h))) return false;
                // One shared pool used twice in a video needs two distinct headline files.
                t.Scenes[2].Layers[1].Source = "Заголовки-2"; t.Scenes[2].Layers[1].Unique = true;
                var shared = AssemblyPlanner.Create(t, videos, new AssemblyHistory(), new Random(7), 10);
                if (shared.Plans.Count != 1) return false;
                // Free placement permits crossing the canvas edge, but not non-finite coordinates or an invalid timeline.
                t.Scenes[0].Layers[0].X = .95;
                t.Validate();
                t.Scenes[0].Layers[0].X = double.NaN;
                try { t.Validate(); return false; } catch (Exception) { }
                t.Scenes[0].Layers[0].X = .08; t.Scenes[0].Layers[0].Start = 5;
                try { t.Validate(); return false; } catch (Exception) { }
                return true;
            } finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }
        public static bool RunRasterTests() {
            string temp = Path.Combine(Path.GetTempPath(), "assembly-raster-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try {
                string image = Path.Combine(temp, "transparent.png"), text = Path.Combine(temp, "text.txt");
                using (var b = new Bitmap(40, 20)) { using (var g = Graphics.FromImage(b)) { g.Clear(Color.Transparent); g.FillRectangle(Brushes.Red, 10, 5, 20, 10); } b.Save(image, System.Drawing.Imaging.ImageFormat.Png); }
                File.WriteAllText(text, "Проверка русского заголовка\nи переноса строки");
                var t = AssemblyTemplate.Defaults(); t.Width = 360; t.Height = 640;
                var layer = new AssemblyLayer { X = .1, Y = .2, Width = .8, Height = .3, Radius = 18, Padding = 12, FontSize = 42 };
                using (var b = AssemblyRaster.Create(t, new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = image } })) {
                    if (b.GetPixel(0, 0).A != 0 || b.GetPixel(180, 224).R < 200 || b.GetPixel(180, 224).A < 200) return false;
                }
                using (var b = AssemblyRaster.Create(t, new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = text } })) {
                    if (b.GetPixel(0, 0).A != 0 || b.GetPixel(36, 128).A != 0 || b.GetPixel(50, 150).A < 200) return false;
                }
                // Every source aspect must fit inside the configured object box, not cover and overflow it.
                var fitLayer = new AssemblyLayer { X = .1, Y = .2, Width = .8, Height = .3, Opacity = .6 };
                var sizes = new[] { new Size(160,40), new Size(40,160), new Size(80,80) };
                var expected = new[] { new RectangleF(36,188,288,72), new RectangleF(156,128,48,192), new RectangleF(84,128,192,192) };
                for (int i = 0; i < sizes.Length; i++) {
                    string source = Path.Combine(temp, "aspect-" + i + ".png");
                    using (var b = new Bitmap(sizes[i].Width,sizes[i].Height)) {
                        using (var g = Graphics.FromImage(b)) {
                            g.Clear(Color.Red);
                            g.FillRectangle(Brushes.Lime,b.Width/2,0,b.Width/2,b.Height/2);
                            g.FillRectangle(Brushes.Blue,0,b.Height/2,b.Width/2,b.Height/2);
                            g.FillRectangle(Brushes.Yellow,b.Width/2,b.Height/2,b.Width/2,b.Height/2);
                        }
                        b.Save(source,System.Drawing.Imaging.ImageFormat.Png);
                    }
                    var bounds = AssemblyRaster.FitImageBounds(new RectangleF(36,128,288,192),sizes[i]);
                    if (Math.Abs(bounds.X-expected[i].X)>.01 || Math.Abs(bounds.Y-expected[i].Y)>.01 || Math.Abs(bounds.Width-expected[i].Width)>.01 || Math.Abs(bounds.Height-expected[i].Height)>.01) throw new Exception("Image fit geometry changed");
                    using (var b = AssemblyRaster.Create(t,new AssemblyLayerPlan { Layer = fitLayer, Asset = new AssemblyAsset { Path = source } })) {
                        for (int y=0; y<b.Height; y++) for (int x=0; x<b.Width; x++)
                            if (b.GetPixel(x,y).A>128 && !expected[i].Contains(x+.5f,y+.5f)) throw new Exception("Image escaped its fitted bounds");
                        var colors = new[] {Color.Red,Color.Lime,Color.Blue,Color.Yellow};
                        for (int corner=0; corner<4; corner++) {
                            var pixel = b.GetPixel((int)(bounds.X+bounds.Width*(corner%2==0?.1:.9)),(int)(bounds.Y+bounds.Height*(corner<2?.1:.9)));
                            if (pixel.A<145 || pixel.A>160 || Math.Abs(pixel.R-colors[corner].R)>5 || Math.Abs(pixel.G-colors[corner].G)>5 || Math.Abs(pixel.B-colors[corner].B)>5) throw new Exception("Image edge content or opacity was lost during fit");
                        }
                    }
                }
                var scaled=new AssemblyLayer{Source=image,X=.1,Y=.2,Width=1,Height=.15};
                double before=AssemblyRaster.ImageScalePercent(t,scaled);
                if(before>=100) throw new Exception("Legacy hidden box reported as full image");
                AssemblyRaster.SetImageScale(t,scaled,100);
                var single=AssemblyRaster.LayerImageBounds(t,scaled,new Size(40,20));
                if(Math.Abs(single.Width-t.Width)>.01 || Math.Abs(AssemblyRaster.ImageScalePercent(t,scaled)-100)>.01) throw new Exception("Single image cannot reach frame width");
                scaled.Source=temp;
                AssemblyRaster.SetImageScale(t,scaled,100);
                foreach(var size in sizes) {
                    var bounds=AssemblyRaster.LayerImageBounds(t,scaled,size);
                    if(bounds.Left<-.01 || bounds.Top<-.01 || bounds.Right>t.Width+.01 || bounds.Bottom>t.Height+.01) throw new Exception("Pack image escaped frame");
                    if(Math.Abs(bounds.Width/size.Width-single.Width/40)<.01) throw new Exception("Pack scale did not account for larger images");
                }
                var small=AssemblyRaster.LayerImageBounds(t,scaled,new Size(40,20));
                var large=AssemblyRaster.LayerImageBounds(t,scaled,new Size(80,40));
                if(Math.Abs(large.Width-small.Width*2)>.01) throw new Exception("Relative image sizes lost");
                AssemblyRaster.SetImageScale(t,scaled,50);
                if(Math.Abs(AssemblyRaster.ImageScalePercent(t,scaled)-50)>.01) throw new Exception("Pack slider roundtrip");
                using(var bitmap=AssemblyRaster.Create(t,new AssemblyLayerPlan{Layer=scaled,Asset=new AssemblyAsset{Path=image}})) {
                    var bounds=AssemblyRaster.LayerImageBounds(t,scaled,new Size(40,20));
                    if(bitmap.GetPixel((int)(bounds.X+bounds.Width/2),(int)(bounds.Y+bounds.Height/2)).A<200) throw new Exception("Scaled raster disagrees with preview geometry");
                }
                return true;
            } finally { Directory.Delete(temp, true); }
        }
        static AssemblyTemplate LegacyTemplate() {
            var t = new AssemblyTemplate();
            t.Scenes.Add(new AssemblyScene { Name = "1. Hook", Duration = 5, Layers = { new AssemblyLayer { Name = "Заголовок", Source = "Заголовки", Unique = true } } });
            t.Scenes.Add(new AssemblyScene { Name = "2. Торговля", Duration = 6, Videos = "Торговля", Layers = { new AssemblyLayer { Name = "Второй заголовок", Source = "Заголовки-2", Unique = true } } });
            t.Scenes.Add(new AssemblyScene { Name = "3. TG-бот и призыв", Duration = 6, Layers = { new AssemblyLayer { Name = "Скриншот TG-бота", Source = "TG-бот", Fixed = true }, new AssemblyLayer { Name = "Финальный призыв", Source = "Призыв" } } });
            return t;
        }
        public static bool RunPackTests() {
            string temp = Path.Combine(Path.GetTempPath(),"assembly-packs-" + Guid.NewGuid().ToString("N"));
            try {
                var t = AssemblyTemplate.Defaults(); t.Materials = temp; t.Output = Path.Combine(temp,"out"); AssemblyWorkspace.Prepare(t); t.Validate();
                if (t.Scenes.Count != 4 || Math.Abs(t.Duration-(20+3*t.TransitionSeconds))>.001 || t.Scenes.Select(s => s.Folder).Distinct().Count() != 4) return false;
                if (t.Scenes.Any(s => !Directory.Exists(t.Resolve(s.Layers[0].Source)))) return false;
                File.WriteAllText(t.Resolve("Видео/a.mp4"),"a"); File.WriteAllText(t.Resolve("Музыка/a.wav"),"music-a"); File.WriteAllText(t.Resolve("Музыка/b.wav"),"music-b");
                var videos = new[] { new AssemblyVideo { Path = t.Resolve("Видео/a.mp4"), Hash = "video-a", Duration = 1 } };
                // Four empty image packs and one short video must still produce a usable plan.
                var batch = AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(31),2);
                if (batch.Plans.Count != 1 || batch.Plans.Any(p => p.Scenes.Count != 4 || p.Scenes.Any(s => s.Layers.Count != 0))) return false;
                // Music alone must not create a second identical visual combination.
                if(batch.Plans.Select(p => p.Signature).Distinct().Count()!=1)return false;
                var history = new AssemblyHistory(); history.Combinations.AddRange(batch.Plans.Select(p => p.Signature));
                if (AssemblyPlanner.Create(t,videos,history,new Random(13),1).Plans.Count != 0) return false;
                // Empty packs are optional, but populated packs marked unique still honor history.
                var l = t.Scenes[0].Layers[0]; l.Unique = true;
                File.WriteAllText(t.Resolve(Path.Combine(l.Source,"a.png")),"picture-a"); File.WriteAllText(t.Resolve(Path.Combine(l.Source,"b.png")),"picture-b");
                File.WriteAllText(t.Resolve(Path.Combine(l.Source,"ignored.txt")),"This is not an image.");
                batch = AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(14),10);
                if (batch.Plans.Count != 2 || batch.Plans.SelectMany(p => p.UniqueImages).Distinct().Count() != 2 || batch.Limit == "") return false;
                // Legacy empty headline folders stop being required after migration, without losing used assets/history identity.
                var legacy = LegacyTemplate(); legacy.Materials = temp; legacy.Output = t.Output; string id = legacy.Id;
                AssemblyWorkspace.Prepare(legacy);
                if (legacy.Id != id || legacy.Scenes.Count != 3 || !legacy.Scenes[0].Layers[0].Unique || legacy.Scenes[0].Layers[0].Source != "Заголовки") return false;
                batch = AssemblyPlanner.Create(legacy,videos,new AssemblyHistory(),new Random(1),1); if (batch.Plans.Count != 1) return false;
                string folder = t.Scenes[1].Folder; AssemblyWorkspace.Insert(t,1); if (t.Scenes[2].Folder != folder) return false;
                string longStem = "ССЫЛКА НА AI БОТ — В ПРОФИЛЕ " + new string('X', 180);
                string musicFolder = t.Resolve("Музыка"); Directory.CreateDirectory(musicFolder);
                string safeDest = AssemblyFiles.SafeImportDestination(musicFolder, Path.Combine(musicFolder, longStem + ".mp3"));
                if (safeDest.Length > 259 || Path.GetFileName(safeDest).Length >= longStem.Length) return false;
                File.WriteAllText(safeDest, "music-long-name");
                if (!File.Exists(safeDest)) return false;
                return true;
            } finally { if (Directory.Exists(temp)) Directory.Delete(temp,true); }
        }
        public static bool RunUiTests() { return StudioCheck(null); }
        public static bool RunObjectsTests() {
            string temp=Path.Combine(Path.GetTempPath(),"assembly-objects-"+Guid.NewGuid().ToString("N"));
            try {
                var t=AssemblyTemplate.Defaults(); t.Materials=temp; t.Scenes.RemoveAt(3); AssemblyWorkspace.Prepare(t);
                var videos=new System.Collections.Generic.List<AssemblyVideo>();
                for (int i=0; i<7; i++) {string path=t.Resolve("Видео/"+i+".mp4"); File.WriteAllText(path,"video-"+i); videos.Add(new AssemblyVideo {Path=path,Hash=AssemblyFiles.Hash(path),Duration=9}); File.WriteAllText(t.Resolve("Музыка/"+i+".wav"),"music-"+i);}
                for (int scene=0; scene<3; scene++) for (int i=0; i<(scene==2?12:11); i++) File.WriteAllText(t.Resolve(Path.Combine(t.Scenes[scene].Layers[0].Source,i+".png")),"picture-"+scene+"-"+i);
                var changing=t.Scenes[2].Layers[0]; changing.Y=.74; changing.Height=.16;
                string source=Path.Combine(t.Scenes[2].Folder,"CTA"); Directory.CreateDirectory(t.Resolve(source)); string pin=t.Resolve(Path.Combine(source,"fixed.png")); File.WriteAllText(pin,"constant-cta");
                var fixedLayer=new AssemblyLayer {Source=source,Fixed=true,FixedAssetHash=AssemblyFiles.Hash(pin),Width=.65,Height=.42,X=.175,Y=.12}; t.Scenes[2].Layers.Insert(0,fixedLayer);
                var h=new AssemblyHistory(); var capacity=new AssemblyInventory(t,videos,h).Calculate();
                if (!capacity.Exact || capacity.Variants!=304920 || capacity.BatchLimit!=304920) throw new Exception("Expected P(7,3)*11*11*12 = 304,920 visual combinations (music excluded), without a 1000-roll cap");
                t.Count=1001; t.Validate(); var plans=AssemblyPlanner.Create(t,videos,h,new Random(18),11).Plans;
                if (plans.Count!=11 || plans.Select(p => p.Signature).Distinct().Count()!=11) throw new Exception("Expected eleven unique output combinations");
                for (int scene=0; scene<3; scene++) {
                    int layer=scene==2 ? 1 : 0;
                    if (plans.Select(p => p.Scenes[scene].Layers[layer].Asset.Hash).Distinct().Count()!=11) throw new Exception("A changing object's images repeated before its pool was exhausted");
                    if (plans.Any(p => p.Scenes[scene].Layers.Count!=(scene==2 ? 2 : 1))) throw new Exception("An object rendered more than one image from its pool");
                    if (plans.Take(3).Select(p => p.Scenes[scene].Video.Hash).Distinct().Count()!=3) throw new Exception("Backgrounds did not shuffle by scene");
                }
                if (plans.Take(3).Select(p => p.MusicHash).Distinct().Count()!=3 || plans.Any(p => p.Scenes[2].Layers[0].Asset.Hash!=fixedLayer.FixedAssetHash)) throw new Exception("Music or fixed CTA selection incorrect");
                var mix=new AssemblyMixTracker(); var first=AssemblyPlanner.Create(t,videos,h,new Random(2),6,mix);
                h.Combinations.AddRange(first.Plans.Select(p => p.Signature)); var second=AssemblyPlanner.Create(t,videos,h,new Random(3),6,mix);
                if (first.Plans.Concat(second.Plans).Take(11).Select(p => p.Scenes[0].Layers[0].Asset.Hash).Distinct().Count()!=11) throw new Exception("A rendering chunk reset image shuffling");
                // Old accidental fixed multi-image pools change; a single CTA and geometry survive.
                t.ObjectStudioVersion=0; var l=t.Scenes[0].Layers[0]; l.Fixed=true; l.FixedAssetHash=AssemblyFiles.Hash(AssemblyFiles.Pool(t.Resolve(l.Source),t.PictureExtensions)[0]); l.Width=.6; l.Y=.12;
                AssemblyWorkspace.Prepare(t); if (l.Fixed || l.Width!=.6 || l.Y!=.12 || !fixedLayer.Fixed || t.ObjectStudioVersion!=1) throw new Exception("Object migration changed layout or failed to release a fixed multi-image pool");
                t.Scenes[1].Layers.Clear(); AssemblyWorkspace.Prepare(t); if (t.Scenes[1].Layers.Count!=0) throw new Exception("Deleted objects returned during workspace preparation");
                return true;
            } finally {if (Directory.Exists(temp)) Directory.Delete(temp,true);}
        }
        public static bool RunRoutingTests() {
            string temp=Path.Combine(Path.GetTempPath(),"assembly-routing-"+Guid.NewGuid().ToString("N"));
            try {
                var t=AssemblyTemplate.Defaults();t.Materials=temp;t.Output=Path.Combine(temp,"out");t.Scenes.RemoveRange(2,2);AssemblyWorkspace.Prepare(t);
                var videos=new System.Collections.Generic.List<AssemblyVideo>();
                Action<string,string> video=(source,content) => {Directory.CreateDirectory(Path.GetDirectoryName(t.Resolve(source)));File.WriteAllText(t.Resolve(source),content);videos.Add(new AssemblyVideo {Path=t.Resolve(source),Hash=AssemblyFiles.Hash(t.Resolve(source)),Duration=12});};
                video("Видео/shared.mp4","shared");
                var first=t.Scenes[0];var second=t.Scenes[1]; first.Videos=Path.Combine(first.Folder,"Видео");second.Videos=Path.Combine(second.Folder,"Видео");first.VideoMode=second.VideoMode="own";
                video(Path.Combine(first.Videos,"a.mp4"),"first-a");video(Path.Combine(first.Videos,"b.mp4"),"first-b");video(Path.Combine(second.Videos,"a.mp4"),"second-a");video(Path.Combine(second.Videos,"b.mp4"),"second-b");
                var h=new AssemblyHistory();var c=new AssemblyInventory(t,videos,h).Calculate();if (!c.Exact || c.Variants!=4 || c.BatchLimit!=4) throw new Exception("Two independent video pools should produce four combinations");
                var batch=AssemblyPlanner.Create(t,videos,h,new Random(6),2);
                if (batch.Plans.Count!=2 || batch.Plans.Any(p => Path.GetDirectoryName(p.Scenes[0].Video.Path)!=t.Resolve(first.Videos) || Path.GetDirectoryName(p.Scenes[1].Video.Path)!=t.Resolve(second.Videos))) throw new Exception("Scene-specific backgrounds escaped their assigned scenes");
                foreach (var scene in new[] {0,1}) if (batch.Plans.Select(p => p.Scenes[scene].Video.Hash).Distinct().Count()!=2) throw new Exception("Own video alternatives did not rotate");
                AssemblyWorkspace.Insert(t,0);t.Scenes[0].Layers.Clear(); if (t.Scenes[1]!=first || t.VideoSource(first)!=first.Videos) throw new Exception("Insertion shifted a video assignment");
                t.Scenes.RemoveAt(0);t.Scenes.Reverse(); var saved=Path.Combine(temp,"studio.xml");AssemblyFiles.Save(saved,t); t=AssemblyFiles.Load<AssemblyTemplate>(saved);AssemblyWorkspace.Prepare(t);
                if (t.Scenes[0].Videos!=second.Videos || t.Scenes[1].Videos!=first.Videos || t.Scenes.Any(scene => scene.VideoMode!="own")) throw new Exception("Reorder or save lost own video sources");
                var own=t.Scenes[0];string retained=own.Videos;own.VideoMode="shared";if (t.VideoSource(own)!=t.Videos) throw new Exception("Shared mode did not use the common pool");own.VideoMode="own";
                foreach (string path in Directory.GetFiles(t.Resolve(retained))) File.Delete(path);
                if (t.VideoSource(own)!=retained || AssemblyPlanner.Create(t,videos,h,new Random(1),1).Plans.Count!=0) throw new Exception("Empty own videos silently fell back to shared backgrounds");
                t.Scenes.Clear();t.Validate();AssemblyWorkspace.Prepare(t);AssemblyFiles.Save(saved,t);t=AssemblyFiles.Load<AssemblyTemplate>(saved);t.Validate();
                c=new AssemblyInventory(t,videos,h).Calculate(); if (t.Scenes.Count!=0 || c.Variants!=0 || c.BatchLimit!=0 || AssemblyPlanner.Create(t,videos,h,new Random(1),1).Plans.Count!=0) throw new Exception("An empty studio could generate phantom outputs");
                AssemblyWorkspace.Insert(t,0);t.Validate();if (t.Scenes.Count!=1) throw new Exception("Could not add a scene back to an empty studio");return true;
            } finally {if (Directory.Exists(temp)) Directory.Delete(temp,true);}
        }
        public static bool RunCapacityTests() {
            string temp=Path.Combine(Path.GetTempPath(),"assembly-capacity-"+Guid.NewGuid().ToString("N"));
            try {
                var t=AssemblyTemplate.Defaults(); t.Materials=temp; AssemblyWorkspace.Prepare(t); t.Scenes.RemoveRange(2,2);
                for (int i=0; i<3; i++) File.WriteAllText(t.Resolve("Видео/"+i+".mp4"),"video-"+i);
                var v=Directory.GetFiles(t.Resolve("Видео")).Select(p => new AssemblyVideo {Path=p,Hash=AssemblyFiles.Hash(p),Duration=10}).ToList();
                var l=t.Scenes[0].Layers[0];
                for (int i=0; i<2; i++) File.WriteAllText(t.Resolve(Path.Combine(l.Source,i+".png")),"image-"+i);
                File.Copy(t.Resolve(Path.Combine(l.Source,"0.png")),t.Resolve(Path.Combine(l.Source,"copy.png")));
                File.WriteAllText(t.Resolve("Музыка/a.wav"),"music-a"); File.WriteAllText(t.Resolve("Музыка/b.wav"),"music-b"); File.Copy(t.Resolve("Музыка/a.wav"),t.Resolve("Музыка/copy.wav"));
                var h=new AssemblyHistory(); var capacity=new AssemblyInventory(t,v,h).Calculate();
                if (!capacity.Exact || capacity.Variants!=12 || capacity.BatchLimit!=12) throw new Exception("Capacity should deduplicate content and count 3*2*2=12 (music excluded)");
                t.SplitLayout="vertical"; t.SplitSharedPool=false; t.SecondaryVideos="";
                AssemblyWorkspace.EnsureSplitSharedPool(t);
                if (!t.SplitSharedPool) throw new Exception("Split with videos in one pack must auto-enable shared pool");
                var splitCapacity=new AssemblyInventory(t,v,h).Calculate();
                if (splitCapacity.Variants<1) throw new Exception("Shared split pool should produce linkages");
                t.SplitSharedPool=false; t.SplitLayout="vertical"; t.Scenes[0].Duration=21; t.Scenes[0].DurationByVideo=true; t.SecondaryVideos="Видео-низ";
                Directory.CreateDirectory(t.Resolve(t.SecondaryVideos));
                string bottom=t.Resolve(t.SecondaryVideos+"/short.mp4"); File.WriteAllText(bottom,"bottom");
                var vb=Directory.GetFiles(t.Resolve("Видео")).Select(p => new AssemblyVideo {Path=p,Hash=AssemblyFiles.Hash(p),Duration=25}).ToList();
                vb.Add(new AssemblyVideo {Path=bottom,Hash=AssemblyFiles.Hash(bottom),Duration=8});
                var splitBottom=new AssemblyInventory(t,vb,h).Calculate();
                if (splitBottom.Variants<1) throw new Exception("Split secondary should count when scene length follows source video");
                t.SplitLayout="none"; t.SplitSharedPool=false;
                var batch=AssemblyPlanner.Create(t,v,h,new Random(5),12); if (batch.Plans.Count!=12 || batch.Plans.Select(p => p.Signature).Distinct().Count()!=12) throw new Exception("Exhaustive small-space allocation lost combinations");
                h.Combinations.AddRange(batch.Plans.Take(11).Select(p => p.Signature));
                if (new AssemblyInventory(t,v,h).Calculate().Variants!=1 || AssemblyPlanner.Create(t,v,h,new Random(8),10).Plans.Count!=1) throw new Exception("Last remaining combination was lost");
                l.Fixed=true; l.FixedAssetHash=AssemblyFiles.Hash(t.Resolve(Path.Combine(l.Source,"1.png")));
                batch=AssemblyPlanner.Create(t,v,new AssemblyHistory(),new Random(1),10);
                if (batch.Plans.Any(p => p.Scenes[0].Layers[0].Asset.Hash!=l.FixedAssetHash)) throw new Exception("Pinned CTA changed");
                l.FixedAssetHash="missing"; try { new AssemblyInventory(t,v,new AssemblyHistory()); throw new Exception("Missing fixed asset should fail"); } catch (Exception e) { if (!e.Message.Contains("Закреплённая")) throw; }
                l.Fixed=false; l.FixedAssetHash=""; l.Unique=true;
                t.Scenes[1].Layers[0].Source=l.Source; t.Scenes[1].Layers[0].Unique=true;
                capacity=new AssemblyInventory(t,v,new AssemblyHistory()).Calculate(); if (capacity.BatchLimit!=1 || capacity.Variants!=12) throw new Exception("Shared unique pool needs two distinct pictures per roll");
                h=new AssemblyHistory(); h.UsedImages.Add(AssemblyFiles.Hash(t.Resolve(Path.Combine(l.Source,"0.png"))));
                capacity=new AssemblyInventory(t,v,h).Calculate(); if (capacity.BatchLimit!=0 || capacity.Variants!=0) throw new Exception("Exhausted unique pack was treated as optional");
                // Large common pools are counted without enumerating billions of combinations.
                l.Unique=false; t.Scenes[1].Layers[0].Unique=false;
                for (int i=3; i<10; i++) {string path=t.Resolve("Видео/"+i+".mp4"); File.WriteAllText(path,"video-"+i); v.Add(new AssemblyVideo {Path=path,Hash=AssemblyFiles.Hash(path),Duration=10});}
                t.Scenes.Add(new AssemblyScene {Duration=5}); t.Scenes.Add(new AssemblyScene {Duration=5});
                capacity=new AssemblyInventory(t,v,new AssemblyHistory()).Calculate(); if (!capacity.Exact || capacity.Variants!=20160) throw new Exception("Expected P(10,4)*2*2=20160 (music excluded)");
                batch=AssemblyPlanner.Create(t,v,new AssemblyHistory(),new Random(4),1);
                string output=Path.Combine(temp,"known.mp4"); AssemblyFiles.Save(output+".assembly.xml",batch.Plans[0]); h=new AssemblyHistory(); h.Outputs.Add(output); h.Combinations.Add(batch.Plans[0].Signature);
                capacity=new AssemblyInventory(t,v,h).Calculate(); if (!capacity.Exact || capacity.Variants!=20159) throw new Exception("Compatible history recipe was not subtracted");
                h.Combinations.Add("opaque-unresolved-" + Guid.NewGuid().ToString("N")); capacity=new AssemblyInventory(t,v,h).Calculate(); if (capacity.Variants!=20159) throw new Exception("Opaque old history must keep count bounded");
                return true;
            } finally { if (Directory.Exists(temp)) Directory.Delete(temp,true); }
        }
        static object Field(object obj, string name) { return obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj); }
        static object Invoke(object obj, string name, params object[] args) { return obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj, args); }
        static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control c) {
            foreach (System.Windows.Forms.Control child in c.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; }
        }
        public static bool RunSceneVoiceModeTests() {
            var t = AssemblyTemplate.Defaults();
            t.Materials = Path.Combine(Path.GetTempPath(), "vb-scene-auto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(t.Materials);
            t.Scenes[0].Voiceover = Path.Combine("Сцены", "Сцена-01", "Озвучка");
            t.Scenes[0].VoiceMode = "";
            t.Scenes[1].VoiceMode = "";
            Directory.CreateDirectory(t.Resolve(t.Scenes[0].Voiceover));
            File.WriteAllText(Path.Combine(t.Resolve(t.Scenes[0].Voiceover), "a.mp3"), "voice");
            AssemblySpeechPlan.NormalizeSceneVoiceModes(t);
            if (t.Scenes[0].VoiceMode != "own" || t.Scenes[1].VoiceMode != "none" || !t.HasSceneVoiceovers) throw new Exception("Scene voice auto mode failed");
            var shared = AssemblyTemplate.Defaults();
            shared.Materials = Path.Combine(Path.GetTempPath(), "vb-shared-voice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(shared.Materials);
            shared.Voiceover = "Озвучка/Общая";
            Directory.CreateDirectory(shared.Resolve(shared.Voiceover));
            File.WriteAllText(Path.Combine(shared.Resolve(shared.Voiceover), "roll.mp3"), "voice");
            AssemblySpeechPlan.NormalizeSceneVoiceModes(shared);
            if (shared.HasSceneVoiceovers) throw new Exception("Shared-only voice must stay whole-roll");
            var bind = AssemblyTemplate.Defaults();
            bind.Materials = Path.Combine(Path.GetTempPath(), "vb-scene-bind-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bind.Materials);
            bind.Scenes[0].Voiceover = ""; bind.Scenes[0].VoiceMode = "";
            bind.Scenes[1].Voiceover = ""; bind.Scenes[1].VoiceMode = "";
            string sceneTwo = Path.Combine(bind.Scenes[1].Folder, "Озвучка-auto");
            Directory.CreateDirectory(bind.Resolve(sceneTwo));
            File.WriteAllText(Path.Combine(bind.Resolve(sceneTwo), "b.mp3"), "voice");
            AssemblySpeechPlan.NormalizeSceneVoiceModes(bind);
            if (bind.Scenes[1].VoiceMode != "own" || bind.Scenes[0].VoiceMode != "none" || bind.Scenes[1].Voiceover != sceneTwo) throw new Exception("Scene voice folder discovery failed");
            return true;
        }
        public static bool RunTextOverlayUiTests() {
            string previous=Store.Root, temp=Path.Combine(Path.GetTempPath(),"text-overlay-ui-"+Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root=temp;
                var initial=AssemblyTemplate.Defaults(); initial.Materials=Path.Combine(temp,"materials"); initial.Output=Path.Combine(temp,"output");
                AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),initial);
                using(var window=new AssemblyWindow(true)) {
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    Exception failure=null;
                    using(var timer=new System.Windows.Forms.Timer {Interval=60}) {
                        timer.Tick+=(s,e) => {
                            var dialog=System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault(f=>f.Name=="AssemblyTextDialog");
                            if(dialog==null)return; timer.Stop();
                            try {
                                All(dialog).OfType<System.Windows.Forms.TextBox>().Single(c=>c.Name=="OverlayText").Text="Своя надпись\nВторая строка";
                                var font=All(dialog).OfType<System.Windows.Forms.ComboBox>().Single(c=>c.Name=="OverlayFont");
                                using(var fonts=new System.Drawing.Text.InstalledFontCollection()) if(fonts.Families.Any(f=>!font.Items.Contains(f.Name)))throw new Exception("System fonts missing");
                                All(dialog).OfType<System.Windows.Forms.NumericUpDown>().Single(c=>c.Name=="OverlayFontSize").Value=72;
                                All(dialog).OfType<System.Windows.Forms.CheckBox>().Single(c=>c.Name=="OverlayItalic").Checked=true;
                                All(dialog).OfType<System.Windows.Forms.NumericUpDown>().Single(c=>c.Name=="OverlayOutlineWidth").Value=3;
                                All(dialog).OfType<System.Windows.Forms.ComboBox>().Single(c=>c.Name=="OverlayPlate").SelectedIndex=1;
                                All(dialog).OfType<System.Windows.Forms.NumericUpDown>().Single(c=>c.Name=="OverlayPlateOpacity").Value=65;
                                All(dialog).OfType<System.Windows.Forms.NumericUpDown>().Single(c=>c.Name=="OverlayLineSpacing").Value=120;
                                All(dialog).OfType<System.Windows.Forms.Button>().Single(c=>c.Name=="ApplyOverlayText").PerformClick();
                            } catch(Exception ex) { failure=ex; dialog.DialogResult=System.Windows.Forms.DialogResult.Cancel; }
                        };
                        timer.Start(); Invoke(window,"AddTextObject");
                    }
                    if(failure!=null)throw failure;
                    var current=(AssemblyTemplate)Field(window,"template"); var layer=current.Scenes[0].Layers.Single(l=>l.Kind=="text");
                    if(layer.Text!="Своя надпись\nВторая строка" || layer.FontSize!=72 || !layer.TextItalic || layer.TextOutlineWidth!=3 || layer.TextPlate!="rounded" || layer.TextPlateOpacity!=65 || layer.TextLineSpacing!=120)throw new Exception("Text dialog did not apply");
                    var canvas=(AssemblyCanvas)Field(window,"canvas");
                    var images=(System.Collections.Generic.List<Bitmap>)Field(canvas,"images");
                    if(images.Count==0 || images[images.Count-1]==null)throw new Exception("Text canvas raster missing");
                    var layerPlan=new AssemblyLayerPlan {Layer=layer,Asset=new AssemblyAsset {Path=""}};
                    string rasterKey=AssemblyRenderContext.RasterKey(current,layerPlan);
                    layer.TextPlateOpacity=64;
                    if(rasterKey==AssemblyRenderContext.RasterKey(current,layerPlan))throw new Exception("Plate opacity missing in raster cache key");
                    layer.TextPlateOpacity=65; layer.TextLineSpacing=119;
                    if(rasterKey==AssemblyRenderContext.RasterKey(current,layerPlan))throw new Exception("Line spacing missing in raster cache key");
                    layer.TextLineSpacing=120;
                    var box=(RectangleF)typeof(AssemblyCanvas).GetMethod("Box",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(canvas,new object[]{layer});
                    var point=Point.Round(new PointF(box.Left+box.Width/2,box.Top+box.Height/2)); double x=layer.X,y=layer.Y;
                    typeof(AssemblyCanvas).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(canvas,new object[]{new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,1,point.X,point.Y,0)});
                    typeof(AssemblyCanvas).GetMethod("OnMouseMove",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(canvas,new object[]{new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,0,point.X+25,point.Y+20,0)});
                    typeof(AssemblyCanvas).GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(canvas,new object[]{new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,1,point.X+25,point.Y+20,0)});
                    if(layer.X<=x || layer.Y<=y)throw new Exception("Free text drag failed");
                    using(var timer=new System.Windows.Forms.Timer {Interval=60}) {
                        timer.Tick+=(s,e) => {
                            var dialog=System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault(f=>f.Name=="AssemblyTextDialog");
                            if(dialog==null)return; timer.Stop();
                            All(dialog).OfType<System.Windows.Forms.TextBox>().Single(c=>c.Name=="OverlayText").Text="Cancelled change"; dialog.DialogResult=System.Windows.Forms.DialogResult.Cancel;
                        }; timer.Start(); Invoke(window,"EditTextObject");
                    }
                    if(layer.Text=="Cancelled change")throw new Exception("Cancel changed saved text");
                    var image=new AssemblyLayer {Name="Radius test",Source="radius-test",CornerRadius=0}; current.Scenes[0].Layers.Add(image);
                    Invoke(window,"SelectObject",current.Scenes[0].Layers.Count-1);
                    using(var timer=new System.Windows.Forms.Timer {Interval=60}) {
                        timer.Tick+=(s,e) => {
                            var dialog=System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault(f=>f.Name=="OverlayCornerDialog");
                            if(dialog==null)return; timer.Stop();
                            All(dialog).OfType<System.Windows.Forms.NumericUpDown>().Single(c=>c.Name=="OverlayCornerRadius").Value=48;
                            All(dialog).OfType<System.Windows.Forms.Button>().Single(c=>c.Name=="ApplyCornerRadius").PerformClick();
                        }; timer.Start(); Invoke(window,"EditCornerRadius");
                    }
                    if(image.CornerRadius!=48)throw new Exception("Corner radius dialog failed");
                    Invoke(window,"Save");
                    var saved=AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml"));
                    var restored=saved.Scenes[0].Layers.Single(l=>l.Kind=="text");
                    if(restored.Text!=layer.Text || restored.X!=layer.X || restored.FontSize!=72 || !restored.TextItalic || restored.TextPlate!="rounded" || restored.TextPlateOpacity!=65 || restored.TextLineSpacing!=120 || saved.Scenes[0].Layers.Last().CornerRadius!=48)throw new Exception("Text/radius XML state lost");
                    AssemblyMarketProfiles.Capture(saved,AssemblyProfile.Ru); AssemblyMarketProfiles.Apply(saved,AssemblyProfile.Ru);
                    var profileText=saved.Scenes[0].Layers.Single(l=>l.Kind=="text");
                    if(profileText.TextPlate!="rounded" || profileText.TextPlateOpacity!=65 || profileText.TextLineSpacing!=120 || saved.Scenes[0].Layers.Last().CornerRadius!=48)throw new Exception("Profile switch lost text style/radius");
                    window.Close();
                }
                Console.WriteLine("Text overlay UI PASS: system fonts, multiline text, typography, canvas raster, free drag, cancel, corners, saved XML/profile"); return true;
            } finally {Store.Root=previous; if(Directory.Exists(temp))Directory.Delete(temp,true);}
        }
        public static bool RunImageAppearanceUiTests() {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(), "appearance-ui-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var initial = AssemblyTemplate.Defaults(); initial.Scenes.RemoveRange(1,3);
                initial.Materials=Path.Combine(temp,"materials"); initial.Output=Path.Combine(temp,"output");
                AssemblyWorkspace.Prepare(initial); AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),initial);
                using (var window = new AssemblyWindow(true)) {
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    var current=(AssemblyTemplate)Field(window,"template");
                    var toggle=All(window).OfType<System.Windows.Forms.CheckBox>().Single(c=>c.Name=="DurationByVideo");
                    var slider=All(window).Single(c=>c.Name=="SceneDurationSlider");
                    if (!toggle.Visible || !All(window).Any(c=>c.Name=="ObjectAppearance" && c.Visible)) throw new Exception("Timing controls are not visible");
                    toggle.Checked=true; System.Windows.Forms.Application.DoEvents();
                    if (!current.Scenes[0].DurationByVideo || slider.Enabled) throw new Exception("Source duration toggle did not apply");
                    Invoke(window,"ApplyCapacity",new AssemblyCapacity {Exact=true,Variants=90,BatchLimit=90});
                    var quantity=All(window).OfType<System.Windows.Forms.Button>().Single(c=>c.Name=="AssemblyCount");
                    var value=(System.Windows.Forms.NumericUpDown)Field(window,"count");
                    if (!quantity.Enabled || value.Maximum != 90) throw new Exception("Count selection disabled or capped in source-length mode");
                    value.Value=37;
                    Invoke(window,"Save");
                    if (AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml")).Count != 37) throw new Exception("Requested count not saved");
                    if (!AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml")).Scenes[0].DurationByVideo) throw new Exception("Duration mode not saved");
                    toggle.Checked=false;
                    if (!slider.Enabled) throw new Exception("Manual duration did not restore");
                    toggle.Checked=true; Invoke(window,"InsertScene",1); System.Windows.Forms.Application.DoEvents();
                    if(current.Scenes.Count!=2 || !current.Scenes[0].DurationByVideo || current.Scenes[1].DurationByVideo || toggle.Checked)throw new Exception("Adding scene must preserve independent duration settings");
                    toggle.Checked=true; Invoke(window,"SelectScene",0); System.Windows.Forms.Application.DoEvents();
                    if(!toggle.Checked || !current.Scenes[1].DurationByVideo)throw new Exception("Source duration selection lost when switching scenes");
                    Invoke(window,"SelectScene",1); toggle.Checked=false; Invoke(window,"SelectScene",0);
                    if(!toggle.Checked)throw new Exception("Disabling second scene changed first scene");
                    using (var pool=(System.Windows.Forms.Form)typeof(AssemblyWindow).GetMethod("CreatePoolWindow",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[]{2})) {
                        pool.Show(window); System.Windows.Forms.Application.DoEvents();
                        var green=All(pool).OfType<System.Windows.Forms.CheckBox>().Single(c=>c.Name=="ObjectGreenScreen");
                        if (green.Checked) throw new Exception("Green screen must be opt-in");
                        green.Checked=true;
                        if (!current.Scenes[0].Layers[0].GreenScreen) throw new Exception("Green screen toggle did not apply");
                        pool.Close();
                    }
                    Invoke(window,"Save");
                    var saved=AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml"));
                    if (!saved.MarketRu.Scenes[0].Layers[0].GreenScreen) throw new Exception("Green screen toggle not saved in profile");
                    window.Close();
                }
                return true;
            } finally { Store.Root=previous; if(Directory.Exists(temp))Directory.Delete(temp,true); }
        }
        public static bool RunVideoPackUiTests() {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(), "assembly-packs-ui-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var initial = AssemblyTemplate.Defaults(); initial.Materials=Path.Combine(temp,"materials"); initial.Output=Path.Combine(temp,"output");
                AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),initial);
                using (var window = new AssemblyWindow(true)) {
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    var current=(AssemblyTemplate)Field(window,"template");
                    current.Scenes[0].VideoMode="own";
                    current.Scenes[0].Videos="primary";
                    current.Scenes[0].VoiceMode="shared";
                    Invoke(window,"RefreshInfo");
                    var slider=All(window).Single(c=>c.Name=="SceneDurationSlider");
                    if(slider.Enabled)throw new Exception("Voice-driven duration slider remains enabled");
                    current.Scenes[0].VoiceMode="none"; Invoke(window,"RefreshInfo");
                    if(!slider.Enabled)throw new Exception("Silent scene duration slider is disabled");
                    current.Scenes[0].ContinuationVideos="old-continuation";
                    Invoke(window,"RefreshChips");
                    var add=All(window).OfType<System.Windows.Forms.Button>().Single(b=>b.Name=="AddVideoPack");
                    if(add.Text!="+" || !add.Visible)throw new Exception("Video pack add button must be a visible plus");
                    if(current.Scenes[0].VideoPacks.Count!=1 || !All(window).Any(c=>c.Name=="SceneVideoPack2"))throw new Exception("Continuation chip migration failed");
                    using(var pool=(System.Windows.Forms.Form)typeof(AssemblyWindow).GetMethod("CreatePoolWindow",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[]{4})) {
                        pool.Show(window);System.Windows.Forms.Application.DoEvents();
                        var toggle=All(pool).OfType<System.Windows.Forms.CheckBox>().Single(c=>c.Text=="Смешивать с другими");
                        toggle.Checked=true;
                        if(!current.Scenes[0].VideoPacks[0].Mix)throw new Exception("Mix toggle did not apply");
                        pool.Close();
                    }
                    Invoke(window,"Save");window.Close();
                }
                var saved=AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml"));
                if(!saved.MarketRu.Scenes[0].VideoPacks[0].Mix)throw new Exception("Video pack settings were not persisted");
                return true;
            } finally { Store.Root=previous; if(Directory.Exists(temp))Directory.Delete(temp,true); }
        }
        public static bool RunAudioSettingsUiTests() {
            string previous=Store.Root,temp=Path.Combine(Path.GetTempPath(),"assembly-audio-ui-"+Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root=temp;
                var initial=AssemblyTemplate.Defaults(); initial.Materials=Path.Combine(temp,"materials"); initial.Output=Path.Combine(temp,"output");
                AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),initial);
                using(var window=new AssemblyWindow(true)) {
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    var current=(AssemblyTemplate)Field(window,"template");
                    using(var dialog=new System.Windows.Forms.Form()) {
                        var body=new System.Windows.Forms.FlowLayoutPanel(); dialog.Controls.Add(body);
                        typeof(AssemblyWindow).GetMethod("AddAudioLevelingControls",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[]{body,dialog});
                        if(!All(dialog).Any(c=>c.Name=="AudioLevelCheck") || !All(dialog).Any(c=>c.Name=="AudioLevelAlign")) throw new Exception("Audio check/align controls missing");
                        if(All(dialog).Any(c=>c.Name=="PrimaryAudioSlider")) throw new Exception("Global native volume remains in per-scene sound UI");
                        current.Scenes[0].NativeAudioVolume=.37; current.Scenes[0].BedMusicVolume=.19;
                        if(Math.Abs(current.SceneNativeAudioVolume(current.Scenes[0])-.37)>.001 || Math.Abs(current.SceneBedMusicVolume(current.Scenes[0])-.19)>.001) throw new Exception("Per-scene sound levels failed");
                        if(All(dialog).OfType<System.Windows.Forms.Button>().Single(c=>c.Name=="AudioLevelAlign").Enabled) throw new Exception("Align enabled without measurement");
                    }
                    window.Close();
                }
                Console.WriteLine("Audio settings UI PASS: leveling controls, no global volume, independent scene levels"); return true;
            } finally { Store.Root=previous; if(Directory.Exists(temp)) Directory.Delete(temp,true); }
        }
        public static bool RunSubtitleSettingsTests() {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(), "assembly-subtitle-ui-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var initial = AssemblyTemplate.Defaults(); initial.Materials = Path.Combine(temp,"materials"); initial.Output = Path.Combine(temp,"output");
                AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),initial);
                using (var window = new AssemblyWindow(true)) {
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    var current = (AssemblyTemplate)Field(window,"template");
                    current.SubtitleStyle.Enabled = true; Invoke(window,"RefreshSubtitleControls");
                    var button = All(window).OfType<System.Windows.Forms.Button>().Single(b => b.Name == "AssemblySubtitles");
                    if (!button.Visible || button.Text != "Субтитры · настройки") throw new Exception("Subtitle settings button is not visible");
                    if (All(window).Any(c => c.Name == "SubtitlePositionSlider")) throw new Exception("Subtitle slider remains in scene controls");
                    button.PerformClick(); System.Windows.Forms.Application.DoEvents();
                    if (!((System.Windows.Forms.Panel)Field(window,"subtitlePanel")).Visible || ((System.Windows.Forms.TableLayoutPanel)Field(window,"sceneEditor")).Visible) throw new Exception("Subtitle settings did not open");
                    ((System.Windows.Forms.NumericUpDown)Field(window,"subtitlePanelPosition")).Value = 63;
                    if (current.SubtitleStyle.Position != 63) throw new Exception("Subtitle position setting did not apply");
                    Invoke(window,"LeaveSubtitleWorkspace");
                    if (!((System.Windows.Forms.TableLayoutPanel)Field(window,"sceneEditor")).Visible) throw new Exception("Return to scenes failed");
                    window.Close();
                }
                return true;
            } finally { Store.Root = previous; if (Directory.Exists(temp)) Directory.Delete(temp,true); }
        }
        public static bool RunCustomProfileUiTests() {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(), "assembly-profile-ui-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var initial = AssemblyTemplate.Defaults(); initial.Materials = Path.Combine(temp, "materials"); initial.Output = Path.Combine(temp, "output");
                AssemblyFiles.Save(Path.Combine(temp, "assembly-template.xml"), initial);
                string firstId;
                var sharedPreferences=new Preferences();Store.Save(sharedPreferences);
                using (var window = new AssemblyWindow(true, preferences: sharedPreferences)) {
                    if(!ReferenceEquals(Field(window,"assemblyPrefs"),sharedPreferences))throw new Exception("Embedded studio must share shell preferences, not save a stale queue snapshot");
                    sharedPreferences.TikTokAccounts.Add(new TikTokAccount{ProfileId="queue-during-studio",Items=new System.Collections.Generic.List<TikTokItem>{new TikTokItem{Caption="Keep progress",HttpVideoId="123",Published=true}}});
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    var current = (AssemblyTemplate)Field(window, "template");
                    int originalScenes = current.Scenes.Count;
                    for (int i = 0; i < 2; i++) {
                        Exception dialogFailure = null;
                        using (var timer = new System.Windows.Forms.Timer { Interval = 50 }) {
                            timer.Tick += (s,e) => {
                                var dialog = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault(f => f.Name == "AssemblyProfileNameDialog");
                                if (dialog == null) return;
                                timer.Stop();
                                try {
                                    var name = All(dialog).OfType<System.Windows.Forms.TextBox>().Single(c => c.Name == "AssemblyProfileName");
                                    var create = All(dialog).OfType<System.Windows.Forms.Button>().Single(c => c.Name == "AssemblyCreateProfile");
                                    if (create.Enabled) throw new Exception("Empty name enables Create");
                                    name.Text = "Тест студии " + (i + 1);
                                    create.PerformClick();
                                } catch (Exception ex) { dialogFailure = ex; dialog.DialogResult = System.Windows.Forms.DialogResult.Cancel; }
                            };
                            timer.Start();
                            All(window).OfType<System.Windows.Forms.Button>().Single(b => b.Name == "AssemblyAddProfile").PerformClick();
                        }
                        if (dialogFailure != null) throw dialogFailure;
                        System.Windows.Forms.Application.DoEvents();
                        if (current.CustomProfiles.Count != i + 1 || current.Scenes.Count != 0 || current.Videos != "" || current.Count != 1) throw new Exception("Plus/name/create did not activate an empty persisted profile");
                    }
                    firstId = current.CustomProfiles[0].Id;
                    var secondId = current.CustomProfiles[1].Id;
                    var tabs = (System.Windows.Forms.FlowLayoutPanel)Field(window, "profileTabs");
                    if (tabs.Height > 64 || tabs.AutoScroll || tabs.WrapContents || !(tabs is AssemblyHorizontalStrip)) throw new Exception("Profile tabs must use one compact horizontal strip without native scrollbars");
                    All(window).OfType<System.Windows.Forms.Button>().Single(b => b.Name == "AssemblyProfileRu").PerformClick();
                    if (current.Scenes.Count != originalScenes || current.Materials != initial.Materials) throw new Exception("RU scenes/materials changed while creating profiles");
                    All(window).OfType<System.Windows.Forms.Button>().Single(b => b.Name == "AssemblyCustomProfile-" + firstId).PerformClick();
                    current.MusicVolume = .37; Invoke(window, "Save");
                    All(window).OfType<System.Windows.Forms.Button>().Single(b => b.Name == "AssemblyCustomProfile-" + secondId).PerformClick();
                    if (current.Count != 1 || current.Scenes.Count != 0) throw new Exception("First custom data contaminated second custom");
                    All(window).OfType<System.Windows.Forms.Button>().Single(b => b.Name == "AssemblyCustomProfile-" + firstId).PerformClick();
                    if (Math.Abs(current.MusicVolume - .37) > .001 || Store.Load().AssemblyStudioProfile != firstId) throw new Exception("Switch did not persist custom editor or preference");
                    var keptQueue=Store.Load().TikTokAccounts.Single(a=>a.ProfileId=="queue-during-studio").Items.Single();
                    if(!keptQueue.Published||keptQueue.HttpVideoId!="123")throw new Exception("Switching assembly profiles overwrote TikTok queue progress");
                    window.Close();
                }
                using (var reopened = new AssemblyWindow(true)) {
                    reopened.Show(); System.Windows.Forms.Application.DoEvents();
                    var current = (AssemblyTemplate)Field(reopened, "template");
                    if ((string)Field(reopened, "studioProfile") != firstId || Math.Abs(current.MusicVolume - .37) > .001 || current.Scenes.Count != 0 || current.CustomProfiles.Count != 2) throw new Exception("Reopen did not restore active custom profile: active=" + Field(reopened, "studioProfile") + ", expected=" + firstId + ", count=" + current.Count + ", scenes=" + current.Scenes.Count + ", profiles=" + current.CustomProfiles.Count);
                    if (All(reopened).Count(c => c.Name.StartsWith("AssemblyCustomProfile-")) != 2) throw new Exception("Custom tabs missing after reopen");
                    reopened.Close();
                }
                var prefs = Store.Load(); prefs.AssemblyStudioProfile = "custom-" + Guid.NewGuid().ToString("N"); Store.Save(prefs);
                using (var reopened = new AssemblyWindow(true)) {
                    if ((string)Field(reopened, "studioProfile") != AssemblyProfile.Ru || Store.Load().AssemblyStudioProfile != AssemblyProfile.Ru) throw new Exception("Missing saved profile preference was not repaired");
                    reopened.Close();
                }
                return true;
            } finally { Store.Root = previous; if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        }
        public static bool RunVisualEstimateUiTests() {
            string previous=Store.Root, temp=Path.Combine(Path.GetTempPath(),"visual-estimate-ui-"+Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root=temp;
                var initial=AssemblyTemplate.Defaults(); initial.Materials=Path.Combine(temp,"materials"); initial.Output=Path.Combine(temp,"output");
                AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),initial);
                using(var window=new AssemblyWindow(true)) {
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    var timer=(System.Windows.Forms.Timer)Field(window,"visualEstimateTimer"); timer.Stop();
                    var label=(System.Windows.Forms.Label)All(window).Single(c=>c.Name=="VisualDifferenceEstimate");
                    var details=All(window).Single(c=>c.Name=="VisualDifferenceDetails");
                    foreach(var size in new[]{new Size(1180,790),new Size(980,660)}) {
                        window.ClientSize=size; System.Windows.Forms.Application.DoEvents(); timer.Stop();
                        if(!label.Visible || !details.Visible || label.Height<20 || !label.Parent.ClientRectangle.Contains(label.Bounds) || !details.Parent.ClientRectangle.Contains(details.Bounds)) throw new Exception("Visual estimate controls clipped at "+size+": label="+label.Bounds+" visible="+label.Visible+" details="+details.Bounds+" visible="+details.Visible+" parent="+label.Parent.ClientRectangle);
                    }
                    label.Text="stale estimate";
                    Invoke(window,"Changed",false);
                    if(!timer.Enabled || !label.Text.Contains("пересчёт") || ((string)Field(window,"visualEstimateDetails")).Contains("40%")) throw new Exception("Layout change did not invalidate estimate");
                    timer.Stop();
                    label.Text="stale placement";
                    var canvas=Field(window,"canvas");
                    var placement=(Action)canvas.GetType().GetField("BaseVideoPlacementChanged",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(canvas);
                    placement();
                    if(!timer.Enabled || !label.Text.Contains("пересчёт")) throw new Exception("Dragging base video did not invalidate estimate");
                    timer.Stop();
                    var current=(AssemblyTemplate)Field(window,"template"); current.Scenes.Clear();
                    Invoke(window,"StartVisualEstimate");
                    if(!label.Text.Contains("добавьте сцену")) throw new Exception("Empty scene estimate not explained");
                    window.Dispose();
                    if(!(bool)Field(window,"visualEstimateDisposed")) throw new Exception("Estimate lifecycle did not stop on dispose");
                    return true;
                }
            } finally { Store.Root=previous; if(Directory.Exists(temp))Directory.Delete(temp,true); }
        }
        public static bool RunMediaEditorTests() { return MediaEditorCheck(null); }
        public static bool WriteMediaScreenshots(string folder) { Directory.CreateDirectory(folder); return MediaEditorCheck(folder); }
        static bool MediaEditorCheck(string screenshots) {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(), "assembly-media-ui-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var initial = AssemblyTemplate.Defaults(); initial.Materials = Path.Combine(temp,"materials"); initial.Output = Path.Combine(temp,"output");
                AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),initial);
                using (var window = new AssemblyWindow(true)) {
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    foreach (var size in new[] { new Size(1180,790), new Size(980,660) }) {
                        window.ClientSize = size; System.Windows.Forms.Application.DoEvents();
                        var bar = All(window).Single(c => c.Name == "AssemblyCompactBar");
                        foreach (System.Windows.Forms.Control button in bar.Controls)
                            if (!bar.ClientRectangle.Contains(button.Bounds)) throw new Exception("Material action is clipped at " + size + ": " + button.Name);
                        var actions = All(window).Single(c => c.Name == "Assemble").Parent;
                        foreach (System.Windows.Forms.Control button in actions.Controls)
                            if (button.Visible && !actions.ClientRectangle.Contains(button.Bounds)) throw new Exception("Output action is clipped at " + size + ": " + button.Name);
                        if (screenshots != null) Capture(window,Path.Combine(screenshots,"studio-" + size.Width + ".png"));
                    }
                    window.ClientSize = new Size(1180,790); System.Windows.Forms.Application.DoEvents();
                    Invoke(window,"Busy",true);
                    if (!All(window).Single(c => c.Text == "■ Стоп").Enabled || All(window).Single(c => c.Name == "Assemble").Enabled) throw new Exception("Busy state must leave Stop enabled and disable assembly");
                    Invoke(window,"Busy",false);
                    if (screenshots != null) Capture(window,Path.Combine(screenshots,"studio-four-profiles.png"));
                    var labels = All(window).OfType<System.Windows.Forms.Button>().Where(b => b.Name.StartsWith("AssemblyProfile")).Select(b => b.Text).ToArray();
                    if (labels.Length != 5 || !labels.SequenceEqual(new[] {"RU YT","EN YT","RU INS","EN INS","ТЕСТ"})) throw new Exception("Visible studio tabs do not match agreed names.");
                    var current = (AssemblyTemplate)Field(window,"template");
                    var layer = current.Scenes[0].Layers[0];
                    Invoke(window,"MoveImageScope");
                    if (current.GlobalLayers.Count != 1 || current.Scenes[0].Layers.Contains(layer)) throw new Exception("Image scope move duplicated or lost layer.");
                    Invoke(window,"SwitchProfile",AssemblyProfile.En);
                    if (current.GlobalLayers.Count != 0) throw new Exception("Whole-video layers leaked into another profile.");
                    Invoke(window,"SwitchProfile",AssemblyProfile.Ru);
                    if (current.GlobalLayers.Count != 1) throw new Exception("Whole-video layer was not restored.");
                    Invoke(window,"SetImageScope",true); Invoke(window,"MoveImageScope");
                    if (current.GlobalLayers.Count != 0 || current.Scenes[0].Layers.Count != 1) throw new Exception("Image could not return to its scene.");
                    {
                        var voiceType = typeof(AssemblyWindow).GetNestedType("AssemblyVoiceEditor", BindingFlags.NonPublic);
                        using (var form = (System.Windows.Forms.Form)Activator.CreateInstance(voiceType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { window }, null)) {
                            form.Show(window); System.Windows.Forms.Application.DoEvents();
                            if (screenshots != null) Capture(form, Path.Combine(screenshots, "studio-voice.png"));
                            form.Close();
                        }
                    }
                    var preview=(System.Windows.Forms.Panel)Field(window,"heroPreviewHost");
                    var settings=(System.Windows.Forms.Panel)Field(window,"contentHost");
                    if(preview.PointToScreen(Point.Empty).X >= settings.PointToScreen(Point.Empty).X || preview.Height<400) throw new Exception("Large preview is not on the left");
                    foreach (var editor in new[] { "OpenAudioEditor", "OpenVoiceWorkspace" }) {
                        Exception dialogFailure = null;
                        using (var timer = new System.Windows.Forms.Timer { Interval = 100 }) {
                            timer.Tick += (s,e) => {
                                var dialog = window.OwnedForms.FirstOrDefault(f => f.Modal);
                                if (dialog == null) return;
                                timer.Stop();
                                try {
                                    if (!preview.Visible) throw new Exception("Media dialog hides preview");
                                    if (screenshots != null) Capture(dialog,Path.Combine(screenshots,editor + ".png"));
                                } catch (Exception ex) { dialogFailure = ex; }
                                finally { dialog.Close(); }
                            };
                            timer.Start(); Invoke(window,editor);
                        }
                        if (dialogFailure != null) throw dialogFailure;
                        if (!((System.Windows.Forms.TableLayoutPanel)Field(window,"sceneEditor")).Visible) throw new Exception("Closing media dialog did not preserve scene controls");
                    }
                    string speechFile=Path.Combine(temp,"sample.mp3");File.WriteAllText(speechFile,"preview fixture");
                    current.SpeechLines.Add(new AssemblySpeechLine {Text="Проверка положения субтитров",AudioPath=speechFile});
                    current.SubtitleStyle.Enabled=true;
                    var subtitleButton = All(window).OfType<System.Windows.Forms.Button>().Single(b => b.Name == "AssemblySubtitles");
                    if (!subtitleButton.Visible || subtitleButton.Text != "Субтитры · настройки") throw new Exception("Subtitle settings button is not visible or clearly labelled");
                    if (All(window).Any(c => c.Name == "SubtitlePositionSlider")) throw new Exception("Subtitle position leaked into scene controls");
                    bool subtitleDialogOpened = false;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 100 }) {
                        timer.Tick += (s,e) => {
                            var dialog = window.OwnedForms.OfType<SubtitleStyleDialog>().FirstOrDefault();
                            if (dialog == null) return;
                            timer.Stop(); subtitleDialogOpened = true; dialog.Close();
                        };
                        timer.Start(); subtitleButton.PerformClick();
                    }
                    if (!subtitleDialogOpened) throw new Exception("Subtitle settings button did not open its dialog");
                    Invoke(window,"EnterSubtitleWorkspace"); System.Windows.Forms.Application.DoEvents();
                    if (!((System.Windows.Forms.Panel)Field(window,"subtitlePanel")).Visible || ((System.Windows.Forms.TableLayoutPanel)Field(window,"sceneEditor")).Visible) throw new Exception("Subtitle settings button did not open its workspace");
                    var subtitleCanvas=Field(window,"canvas");
                    if(string.IsNullOrWhiteSpace((string)Field(subtitleCanvas,"subtitlePreviewText")) || Field(subtitleCanvas,"subtitleOverlay")==null)throw new Exception("Enabled subtitle formatting has no visible preview");
                    if(!((System.Windows.Forms.Label)Field(window,"subtitlePanelMessage")).Text.Contains("Распознать аудио"))throw new Exception("Missing timing has no explanation");
                    var enabled=(System.Windows.Forms.CheckBox)Field(window,"subtitleEnabled");enabled.Checked=false;
                    if(Field(subtitleCanvas,"subtitleOverlay")!=null)throw new Exception("Disabled subtitles remain visible");
                    enabled.Checked=true;
                    if((((System.Windows.Forms.Panel)Field(window,"audioWorkspace"))?.Visible ?? false) || !preview.Visible)throw new Exception("Subtitle section overlaps music or hides preview");
                    if (screenshots != null) Capture(window, Path.Combine(screenshots, "studio-subtitles.png"));
                    Invoke(window, "LeaveSubtitleWorkspace");
                    Invoke(window,"Save"); window.Close();
                }
                var saved = AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml"));
                if (saved.MarketRu.GlobalLayers.Count != 0 || saved.MarketRu.Scenes[0].Layers.Count != 1) throw new Exception("Media scope persistence failed.");
                return true;
            } finally { Store.Root = previous; if (Directory.Exists(temp)) Directory.Delete(temp,true); }
        }
        public static bool RunStudioUiTests() { return StudioCheck(null); }
        public static bool WriteStudioScreenshots(string folder) { return StudioCheck(folder); }
        static bool StudioCheck(string screenshots) {
            string previous = Store.Root, temp = Path.Combine(Path.GetTempPath(),"assembly-studio-" + Guid.NewGuid().ToString("N"));
            Exception uiFailure=null;System.Threading.ThreadExceptionEventHandler errors=(sender,args) => uiFailure=args.Exception;System.Windows.Forms.Application.ThreadException+=errors;
            try {
                Directory.CreateDirectory(temp); Store.Root = temp;
                var t = AssemblyTemplate.Defaults(); t.Materials = Path.Combine(temp,"materials"); t.Output = Path.Combine(temp,"output");
                string asset = Path.Combine(temp,"image.png"), other = Path.Combine(temp,"second.png");
                using (var b = new Bitmap(700,160)) { using (var g = Graphics.FromImage(b)) { g.Clear(Color.Black); using (var font = new Font("Arial",38,FontStyle.Bold)) g.DrawString("Проверяю сигналы",font,Brushes.White,22,40); } b.Save(asset,System.Drawing.Imaging.ImageFormat.Png); }
                using (var b = new Bitmap(300,400)) { using (var g = Graphics.FromImage(b)) { g.Clear(Color.FromArgb(25,43,56)); using (var font = new Font("Arial",28)) g.DrawString("Мой бот",font,Brushes.White,35,150); } b.Save(other,System.Drawing.Imaging.ImageFormat.Png); }
                if (screenshots!=null) {
                    AssemblyWorkspace.Prepare(t);
                    if (!ToolsLocator.TryResolve(out string ffmpeg,out string probe,out string hint)) throw new Exception("Live studio screenshots require FFmpeg: "+hint);
                    foreach (string color in new[] {"darkgreen","darkblue","purple"}) Core.Tool(ffmpeg,new[] {"-v","error","-y","-f","lavfi","-i","color=c="+color+":s=360x640:r=30:d=0.4","-c:v","libx264","-threads","1",t.Resolve("Видео/"+color+".mp4")},System.Threading.CancellationToken.None,null,30).GetAwaiter().GetResult();
                    foreach (int frequency in new[] {440,660}) Core.Tool(ffmpeg,new[] {"-v","error","-y","-f","lavfi","-i","sine=frequency="+frequency+":duration=0.6",t.Resolve("Музыка/"+frequency+".wav")},System.Threading.CancellationToken.None,null,30).GetAwaiter().GetResult();
                }
                AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),t);
                using (var window = new AssemblyWindow(true)) {
                    window.Show(); window.SetBounds(0,0,1180,790); System.Windows.Forms.Application.DoEvents();
                    if (window.BackColor != Theme.Background || All(window).Any(c => c.Visible && (c is System.Windows.Forms.TabControl || c is System.Windows.Forms.TextBox))) throw new Exception("Studio checkpoint 1 failed");
                    var current = (AssemblyTemplate)Field(window,"template"); if (current.Scenes.Count != 4) throw new Exception("Studio checkpoint 2 failed");
                    Invoke(window,"SetRenderMode","cpu"); if (AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml")).RenderMode!="cpu") throw new Exception("CPU render mode did not persist");
                    Invoke(window,"RenderModeMenu"); System.Windows.Forms.Application.DoEvents();
                    var renderChoice=window.OwnedForms.First(f => f.Name=="StudioChoice");
                    if (screenshots!=null) Capture(renderChoice,Path.Combine(screenshots,"studio-render-mode-choice.png"));
                    All(renderChoice).OfType<System.Windows.Forms.Button>().Single(b => b.Text=="Автоматически").PerformClick();
                    WaitFor(() => current.RenderMode=="auto","Actual render-mode button did not change the saved template");
                    if (AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml")).RenderMode!="auto") throw new Exception("Automatic render mode did not persist");
                    if (!All(window).Any(c => c.Name=="Object0") || !All(window).Any(c => c.Name=="AddObject") || All(window).Any(c => c.Text=="10 роликов")) throw new Exception("Object selectors or the single-line footer are incorrect");
                    Invoke(window,"ImportPool",2,new string[] {asset,other},false);
                    var l = current.Scenes[0].Layers[0]; string source = l.Source;
                    if (AssemblyFiles.Pool(current.Resolve(source),AssemblyFiles.PictureExtensions).Count != 2 || !File.Exists(asset)) throw new Exception("Studio checkpoint 3 failed");
                    Invoke(window,"SetImageMode",2); if (!l.Unique || l.Fixed) throw new Exception("Studio checkpoint 4 failed");
                    Invoke(window,"SelectPicture",current.Resolve(Path.Combine(source,"second.png"))); Invoke(window,"SetImageMode",1);
                    string pin=l.FixedAssetHash; if (pin!=AssemblyFiles.Hash(other)) throw new Exception("Pinned image must use the selected thumbnail");
                    Invoke(window,"SetImageMode",2);
                    if (!All(window).Any(c => c.Name=="Transitions" && c.Visible) || !All(window).Any(c => c.Name=="AssemblyCapacity" && c.Visible)) throw new Exception("Transitions and capacity must be visible");
                    var card=All(window).First(c => c.Name=="SceneCard1"); if (card.ContextMenuStrip==null || !card.ContextMenuStrip.Items.Cast<System.Windows.Forms.ToolStripItem>().Any(item => item.Name=="DeleteScene")) throw new Exception("Scene right-click deletion missing");
                    Invoke(window,"SetTransition","pull");
                    var size = (System.Windows.Forms.NumericUpDown)Field(window,"width"); double ratio = l.Height/l.Width; size.Value = 70;
                    if (Math.Abs(l.Height/l.Width-ratio) > .001) throw new Exception("Studio checkpoint 5 failed");
                    var canvas = (AssemblyCanvas)Field(window,"canvas");
                    var frame = (RectangleF)typeof(AssemblyCanvas).GetProperty("Frame",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(canvas);
                    int px = (int)(frame.X+(l.X+l.Width/2)*frame.Width), py = (int)(frame.Y+(l.Y+l.Height/2)*frame.Height); double before = l.Y;
                    Invoke(canvas,"OnMouseDown",new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,1,px,py,0));
                    Invoke(canvas,"OnMouseMove",new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,0,px+12,py+10,0));
                    Invoke(canvas,"OnMouseUp",new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left,1,px+12,py+10,0));
                    if (l.Y <= before) throw new Exception("Studio checkpoint 6 failed");
                    var duration = (System.Windows.Forms.NumericUpDown)Field(window,"duration"); l.Start = 4; l.End = 5; duration.Value = .5m; current.Validate(); if (l.Start >= .5 || l.End != 0) throw new Exception("Studio checkpoint 7 failed"); duration.Value = 5;
                    Invoke(window,"SetFormat",1080,1350); Invoke(window,"Save"); if (AssemblyFiles.Load<AssemblyTemplate>(Path.Combine(temp,"assembly-template.xml")).Width != 1080) throw new Exception("Studio checkpoint 8 failed"); Invoke(window,"SetFormat",1080,1920);
                    if (screenshots != null) {
                        Directory.CreateDirectory(screenshots); l.Start = 0; l.Unique = false;
                        Invoke(window,"Save"); WaitFor(() => ((System.Windows.Forms.Label)Field(window,"capacityLabel")).Text.Contains("осталось 36 "),"Live capacity should show 18 video orders * 2 pictures = 36; music excluded");
                        var background = new Bitmap(360,640); using (var g = Graphics.FromImage(background)) { g.Clear(Color.FromArgb(16,26,25)); using (var pen = new Pen(Theme.Accent,3)) g.DrawLines(pen,new[] {new Point(0,420),new Point(70,380),new Point(120,430),new Point(190,250),new Point(240,270),new Point(320,130),new Point(360,155)}); } canvas.SetBackground(background);
                        Capture(window,Path.Combine(screenshots,"studio-scenes.png"));
                        using (var pool = (System.Windows.Forms.Form)Invoke(window,"CreatePoolWindow",2)) { pool.Show(window); System.Windows.Forms.Application.DoEvents(); Capture(pool,Path.Combine(screenshots,"studio-files.png")); pool.Close(); }
                        window.SetBounds(0,0,1000,700); System.Windows.Forms.Application.DoEvents();
                        var fourth = All(window).First(c => c.Name == "SceneCard3"); var strip = (AssemblyHorizontalStrip)Field(window,"sceneStack");
                        if (!fourth.Visible || fourth.Parent.Bottom > strip.ClientSize.Height - 18) throw new Exception("Fourth scene card is clipped at compact window size");
                        if (All(window).First(c => c.Name=="InsertScene4").Bottom>strip.ClientSize.Height - 18) throw new Exception("Final insertion button is clipped");
                        Capture(window,Path.Combine(screenshots,"studio-compact.png"));
                        var resultButton=All(window).First(c => c.Name=="Results"); var footer=(System.Windows.Forms.FlowLayoutPanel)Field(window,"footer"); if (resultButton.Right>footer.ClientSize.Width) throw new Exception("Single-line results button is clipped");
                        var number=(System.Windows.Forms.NumericUpDown)Field(window,"count"); number.Value=36; Invoke(window,"ChangeCount",1); if (number.Value!=36 || number.Maximum!=36) throw new Exception("Requested count exceeded the available combinations"); number.Value=3;
                        current.Count=1001; Invoke(window,"BindTemplateValues"); if (number.Value!=1001 || All(window).First(c => c.Name=="Assemble").Enabled) throw new Exception("Loading a studio retained the previous studio's count limit or allowed assembly before recalculation"); current.Count=3; Invoke(window,"BindTemplateValues"); number.Maximum=36;
                        Invoke(window,"FormatMenu"); System.Windows.Forms.Application.DoEvents(); var choice=window.OwnedForms.First(f => f.Name=="StudioChoice"); Capture(choice,Path.Combine(screenshots,"studio-format-choice.png")); choice.Close();
                        // A real two-layer final card: the top image stays, the bottom pack changes.
                        Invoke(window,"SelectScene",3); Invoke(window,"ImportPool",2,new string[] {other},false); Invoke(window,"SetImageMode",1);
                        var fixedLayer=current.Scenes[3].Layers[0]; fixedLayer.Width=.62; fixedLayer.Height=.42; fixedLayer.X=.19; fixedLayer.Y=.14;
                        string alternative=Path.Combine(temp,"another-headline.png");
                        using (var b=new Bitmap(700,160)) {using (var g=Graphics.FromImage(b)) {g.Clear(Color.Black);using (var font=new Font("Arial",38,FontStyle.Bold)) g.DrawString("Посмотри результат",font,Brushes.White,22,40);} b.Save(alternative,System.Drawing.Imaging.ImageFormat.Png);}
                        Invoke(window,"AddPack",(object)new string[] {asset,alternative}); var changing=current.Scenes[3].Layers[1]; changing.Width=.86; changing.Height=.16; changing.X=.07; changing.Y=.74;
                        Invoke(window,"RefreshCanvas");
                        Invoke(window,"SelectScene",3); Invoke(window,"SetImageMode",1); var finalBackground=new Bitmap(360,640); using (var g=Graphics.FromImage(finalBackground)) g.Clear(Color.FromArgb(16,26,25)); canvas.SetBackground(finalBackground);
                        WaitFor(() => ((System.Windows.Forms.Label)Field(window,"capacityLabel")).Text.StartsWith("Осталось 72 "),"Final card capacity did not refresh");
                        Capture(window,Path.Combine(screenshots,"studio-final-cta.png")); Invoke(window,"SelectScene",0);
                        var firstScene=current.Scenes[0];var secondScene=current.Scenes[1];
                        Invoke(window,"ImportPool",3,new string[] {current.Resolve("Видео/darkgreen.mp4")},false);string ownPath=firstScene.Videos;
                        Invoke(window,"SelectScene",1);Invoke(window,"ImportPool",3,new string[] {current.Resolve("Видео/darkblue.mp4")},false);
                        Invoke(window,"SelectScene",0);Invoke(window,"MoveScene",1);if (current.Scenes[1]!=firstScene || firstScene.Videos!=ownPath || firstScene.VideoMode!="own" || secondScene.VideoMode!="own") throw new Exception("Moving a scene lost its video assignment");Invoke(window,"MoveScene",-1);
                        WaitFor(() => ((System.Windows.Forms.Label)Field(window,"capacityLabel")).Text.StartsWith("Осталось "),"Own video capacity did not finish");
                        Capture(window,Path.Combine(screenshots,"studio-own-videos.png"));Invoke(window,"OpenVideoEditor");System.Windows.Forms.Application.DoEvents();var videoForm=window.OwnedForms.First(f => f.Text.StartsWith("Видео"));Capture(videoForm,Path.Combine(screenshots,"studio-video-source-choice.png"));videoForm.Close();
                        Invoke(window,"SetSceneVideoMode","shared");if (firstScene.Videos!=ownPath || current.VideoSource(firstScene)!=current.Videos) throw new Exception("Switching to shared discarded the own pack");Invoke(window,"SelectScene",1);Invoke(window,"SetSceneVideoMode","shared");Invoke(window,"SelectScene",0);
                    }
                    // Replacement changes the pool atomically and retains old originals/copies.
                    double oldWidth=l.Width, oldHeight=l.Height, oldX=l.X, oldY=l.Y;
                    string old = current.Resolve(source); Invoke(window,"ImportPool",2,new string[] {other},true);
                    if (l.Source == source || !Directory.Exists(old) || AssemblyFiles.Pool(current.Resolve(l.Source),AssemblyFiles.PictureExtensions).Count != 1) throw new Exception("Studio checkpoint 10 failed");
                    if (l.Width!=oldWidth || l.Height!=oldHeight || l.X!=oldX || l.Y!=oldY) throw new Exception("Replacing a pack reset its placement");
                    Invoke(window,"SetImageMode",1); if (l.FixedAssetHash!=pin) throw new Exception("Pinned hash was not saved");
                    Invoke(window,"AddPack",(object)new string[] {asset}); if (current.Scenes[0].Layers.Count != 2) throw new Exception("Studio checkpoint 11 failed");
                    Invoke(window,"AddObject"); var empty=current.Scenes[0].Layers[2]; Invoke(window,"Place",.72); double emptyY=empty.Y; Invoke(window,"ImportPool",2,new string[] {asset,other},false);
                    if (current.Scenes[0].Layers.Count!=3 || empty.Y!=emptyY || empty.Fixed) throw new Exception("Import created separate objects or reset prepared placement");
                    string removed=empty.Source; var objectCard=All(window).First(c => c.Name=="Object2"); objectCard.ContextMenuStrip.Show(objectCard,new Point(0,objectCard.Height)); System.Windows.Forms.Application.DoEvents();
                    ((System.Windows.Forms.ToolStripMenuItem)objectCard.ContextMenuStrip.Items.Cast<System.Windows.Forms.ToolStripItem>().First(item => item.Text=="Удалить объект")).PerformClick(); WaitFor(() => current.Scenes[0].Layers.Count==2,"Object context removal did not complete"); if (!Directory.Exists(current.Resolve(removed)) || current.Scenes[0].Layers.Count!=2) throw new Exception("Object removal deleted its files");
                    Invoke(window,"InsertScene",1); if (current.Scenes.Count != 5 || !Directory.Exists(current.Resolve(current.Scenes[1].Folder))) throw new Exception("Studio checkpoint 12 failed");
                    string addedFolder = current.Scenes[1].Folder; Invoke(window,"MoveScene",1); if (current.Scenes[2].Folder != addedFolder) throw new Exception("Studio checkpoint 13 failed");
                    var moved=All(window).First(c => c.Name=="SceneCard2"); moved.ContextMenuStrip.Show(moved,new Point(0,moved.Height)); System.Windows.Forms.Application.DoEvents();
                    ((System.Windows.Forms.ToolStripMenuItem)moved.ContextMenuStrip.Items.Cast<System.Windows.Forms.ToolStripItem>().First(item => item.Name=="DeleteScene")).PerformClick();
                    WaitFor(() => current.Scenes.Count==4,"Scene context deletion did not complete"); if (!Directory.Exists(current.Resolve(addedFolder)) || uiFailure!=null) throw new Exception("Scene context deletion failed",uiFailure);
                    Invoke(window,"Save"); window.Close();
                    WaitFor(() => Field(window,"capacityCancellation")==null && Field(window,"previewCancellation")==null,"Media checks did not stop when the studio closed");
                }
                using (var reopened = new AssemblyWindow(true)) {
                    var saved = (AssemblyTemplate)Field(reopened,"template");
                    if (saved.Scenes.Count != 4 || saved.Scenes[0].Layers.Count != 2 || AssemblyFiles.Pool(saved.Resolve(saved.Scenes[0].Layers[0].Source),AssemblyFiles.PictureExtensions).Count != 1) throw new Exception("Studio checkpoint 15 failed");
                    if (!saved.Scenes[0].Layers[0].Fixed || saved.Scenes[0].Layers[0].FixedAssetHash!=AssemblyFiles.Hash(other) || saved.Transition!="pull" || !saved.Scenes[0].Layers[0].PlacementConfigured) throw new Exception("Studio modes and transition did not persist");
                    reopened.Show();System.Windows.Forms.Application.DoEvents();var preserved=saved.Scenes.Select(scene => saved.Resolve(scene.Folder)).ToArray();
                    var settingsButton=All(reopened).First(control => control.Name=="AssemblySettings");var headerPanel=(System.Windows.Forms.Panel)Field(reopened,"header");if (settingsButton.Left<headerPanel.Width/2 || settingsButton.Right>headerPanel.Width) throw new Exception("Settings overlap the title on initial open");
                    while (saved.Scenes.Count>0) {
                        int before=saved.Scenes.Count;Invoke(reopened,"SelectScene",before-1);
                        System.Windows.Forms.ContextMenuStrip menu;
                        if (before%2==0) {Invoke(reopened,"SceneMenu");menu=(System.Windows.Forms.ContextMenuStrip)Field(reopened,"activeSceneMenu");}
                        else {var card=All(reopened).First(control => control.Name=="SceneCard"+(before-1));menu=card.ContextMenuStrip;menu.Show(card,new Point(0,card.Height));}
                        System.Windows.Forms.Application.DoEvents();((System.Windows.Forms.ToolStripMenuItem)menu.Items.Cast<System.Windows.Forms.ToolStripItem>().First(item => item.Name=="DeleteScene")).PerformClick();
                        WaitFor(() => saved.Scenes.Count==before-1,"Could not delete a scene down to an empty studio");System.Windows.Forms.Application.DoEvents();if (uiFailure!=null) throw new Exception("A menu was disposed during scene deletion",uiFailure);
                    }
                    Invoke(reopened,"Save");if (preserved.Any(path => !Directory.Exists(path)) || All(reopened).First(control => control.Name=="Assemble").Enabled) throw new Exception("Deleting the final scene removed files or left assembly enabled");
                    if (screenshots!=null) Capture(reopened,Path.Combine(screenshots,"studio-empty.png"));reopened.Close();
                }
                using (var emptyWindow=new AssemblyWindow(true)) {
                    emptyWindow.Show();System.Windows.Forms.Application.DoEvents();var empty=(AssemblyTemplate)Field(emptyWindow,"template");if (empty.Scenes.Count!=0) throw new Exception("An empty saved studio was replaced on reopen");
                    Invoke(emptyWindow,"SetTransition","fade");Invoke(emptyWindow,"InsertScene",0);if (empty.Scenes.Count!=1) throw new Exception("Could not restore a scene in an empty studio");emptyWindow.Close();
                    WaitFor(() => Field(emptyWindow,"capacityCancellation")==null && Field(emptyWindow,"previewCancellation")==null,"Empty-studio checks did not stop");
                }
                using (var metadata=new ProcessingDialog(new ProcessingSettings {DeviceMetadata=true,CaptureDateEnabled=true},true)) {
                    metadata.Show();System.Windows.Forms.Application.DoEvents();
                    if (metadata.BackColor!=Theme.Background || All(metadata).OfType<System.Windows.Forms.TabControl>().Single().TabPages.Count!=1) throw new Exception("Metadata editor exposes unrelated filters");
                    if (screenshots!=null) Capture(metadata,Path.Combine(screenshots,"studio-metadata.png"));metadata.Close();
                }
                // Exercise actual legacy load, automatic migration and backup.
                var legacy = LegacyTemplate(); legacy.Materials = t.Materials; legacy.Output = t.Output; AssemblyFiles.Save(Path.Combine(temp,"assembly-template.xml"),legacy);
                using (var migrated = new AssemblyWindow(true)) { var saved = (AssemblyTemplate)Field(migrated,"template"); if (saved.PackStudioVersion != 1 || saved.Id != legacy.Id || !Directory.GetFiles(temp,"assembly-template.xml.before-packs-*.xml").Any()) throw new Exception("Studio checkpoint 16 failed"); }
                var nav = new NavigationService(); int calls = 0; nav.Navigated += _ => calls++; nav.Navigate(NavSection.VideoAssembly); nav.Navigate(NavSection.VideoAssembly); return calls == 2;
            } finally { System.Windows.Forms.Application.ThreadException-=errors; Store.Root = previous; if (Directory.Exists(temp)) Directory.Delete(temp,true); }
        }
        static void Capture(System.Windows.Forms.Form form, string path) {
            form.PerformLayout(); System.Windows.Forms.Application.DoEvents();
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }
        static void WaitFor(Func<bool> condition,string failure) {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed.TotalSeconds<20) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            if (!condition()) throw new Exception(failure);
        }
    }
}
