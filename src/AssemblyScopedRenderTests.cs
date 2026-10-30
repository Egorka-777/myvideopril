using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    // Explicit FFmpeg integration regression; callable by the existing reflection test host.
    public static class AssemblyScopedRenderTests {
        static void Require(bool condition,string message) { if(!condition)throw new Exception(message); }
        public static bool Run(string ffmpeg,string ffprobe) {
            string root=Path.Combine(Path.GetTempPath(),"assembly-scoped-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try { RunAsync(ffmpeg,ffprobe,root).GetAwaiter().GetResult();return true; }
            finally {Directory.Delete(root,true);}
        }
        static async Task RunAsync(string ffmpeg,string probe,string root) {
            var ct=CancellationToken.None;
            Func<string,List<string>,Task> tool=async (name,args)=>{ await Core.Tool(name,args,ct,null,60).ConfigureAwait(false); };
            string a=Path.Combine(root,"a.mp4"),b=Path.Combine(root,"b.mp4"),voice=Path.Combine(root,"voice.wav");
            // A changes colour at one second: the second scene must not restart the red section.
            await tool(ffmpeg,new List<string>{"-y","-v","error","-f","lavfi","-i","color=c=red:s=240x320:r=20:d=1","-f","lavfi","-i","color=c=blue:s=240x320:r=20:d=3","-filter_complex","[0:v][1:v]concat=n=2:v=1:a=0[v]","-map","[v]","-c:v","libx264","-pix_fmt","yuv420p",a});
            await tool(ffmpeg,new List<string>{"-y","-v","error","-f","lavfi","-i","color=c=lime:s=240x320:r=20:d=4","-c:v","libx264","-pix_fmt","yuv420p",b});
            await tool(ffmpeg,new List<string>{"-y","-v","error","-f","lavfi","-i","sine=frequency=440:duration=4",voice});
            AssemblySpeechPlan.SaveTiming(voice,"Test",new WarmupAlignment {characters=new[]{"T","e","s","t"},character_start_times_seconds=new[]{.3,.4,.5,.6},character_end_times_seconds=new[]{.4,.5,.6,.7}});
            string image=Path.Combine(root,"global.png");
            using(var bmp=new Bitmap(240,320)) {using(var g=Graphics.FromImage(bmp)){g.Clear(Color.Transparent);g.FillRectangle(Brushes.White,10,10,30,30);}bmp.Save(image,ImageFormat.Png);}
            var t=new AssemblyTemplate {Materials=root,Videos=a,Width=240,Height=320,Fps=20,SceneTimingVersion=1,PackStudioVersion=1,ContinuousVideo=true,Transition="fade",TransitionDuration=.2,RenderMode="cpu",Voiceover=voice,Music=voice,SecondaryVideos=b};
            t.Scenes.Add(new AssemblyScene{Duration=1});t.Scenes.Add(new AssemblyScene{Duration=1,Music=voice});
            t.GlobalLayers.Add(new AssemblyLayer{Source=image,Unique=true});
            t.SubtitleStyle.Enabled=true;t.Captions.Add(new AssemblyCaption{Text="Проверка",Start=.3,End=.7});
            var videos=new List<AssemblyVideo>{new AssemblyVideo{Path=a,Hash=AssemblyFiles.Hash(a),Duration=4},new AssemblyVideo{Path=b,Hash=AssemblyFiles.Hash(b),Duration=4}};
            var plan=AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(4),1).Plans.Single();
            Require(plan.Scenes.Select(s=>s.Video.Hash).Distinct().Count()==1,"Continuous source changed");
            Require(plan.GlobalLayers.Count==1 && plan.UniqueImages.Count==1,"Global layer was selected more than once");
            Require(string.IsNullOrEmpty(plan.Music) && plan.Scenes.All(s=>string.IsNullOrEmpty(s.Music)),"Voice leaked into music pools");
            Require(Math.Abs(plan.Scenes[1].VideoStart-1.2)<.001,"Continuous scene offset lost transition time");
            var history=new AssemblyHistory();history.UsedImages.AddRange(plan.UniqueImages);history.Combinations.Add(plan.Signature);
            Require(AssemblyPlanner.Create(t,videos,history,new Random(4),1).Plans.Count==0,"Global unique image ignored history");
            Action<AssemblyTemplate,AssemblyLayerPlan,string> raster=(template,layer,path)=>File.Copy(layer.Asset.Path,path);
            foreach(string layout in new[]{"none","horizontal","vertical"}) {
                t.SplitLayout=layout;
                plan=AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(4),1).Plans.Single();
                string work=Path.Combine(root,"work-"+layout),output=Path.Combine(root,layout+".mp4");
                await AssemblyRender.Run(t,plan,ffmpeg,probe,output,work,raster,ct,null).ConfigureAwait(false);
                var info=Core.VideoInfo(await Core.Probe(probe,output,ct).ConfigureAwait(false));
                Require(info.HasAudio && Math.Abs(info.VideoDuration-plan.RollDuration(t))<.15,"Output duration or mixed audio invalid");
                string frame=Path.Combine(root,layout+".png");
                await tool(ffmpeg,new List<string>{"-y","-v","error","-ss","1.6","-i",output,"-frames:v","1",frame});
                using(var bmp=new Bitmap(frame)) {
                    Color first=bmp.GetPixel(60,80),second=bmp.GetPixel(180,240),global=bmp.GetPixel(20,20);
                    Require(first.B>first.R+80,"Whole-video source restarted or became opaque during transition");
                    Require(global.R>210 && global.G>210 && global.B>210,"Global image disappeared after scene join");
                    if(layout!="none") Require(second.G>second.R+80 && second.G>second.B+80,"Secondary split source missing");
                }
                if(layout!="none") {
                    t.SplitSoftness=0;
                    plan=AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(4),1).Plans.Single();
                    await AssemblyRender.Run(t,plan,ffmpeg,probe,Path.Combine(root,layout+"-hard.mp4"),Path.Combine(root,"hard-"+layout),raster,ct,null).ConfigureAwait(false);
                    t.SplitSoftness=.08;
                }
            }
            // One voice variant per output, original subtitle times, and separately controlled music.
            string secondVoice=Path.Combine(root,"voice-second.wav"),music=Path.Combine(root,"music.wav");
            await tool(ffmpeg,new List<string>{"-y","-v","error","-f","lavfi","-i","sine=frequency=660:duration=1.8",secondVoice});
            await tool(ffmpeg,new List<string>{"-y","-v","error","-f","lavfi","-i","sine=frequency=220:duration=4",music});
            var speech=new AssemblyTemplate {Materials=root,Videos=a,Width=240,Height=320,Fps=20,SceneTimingVersion=1,PackStudioVersion=1,ContinuousVideo=true,Transition="cut",RenderMode="cpu",Music=root,MusicVolume=.15,VoiceoverVolume=.1};
            speech.Scenes.Add(new AssemblyScene {Duration=3});
            speech.SpeechLines.Add(new AssemblySpeechLine {AudioPath=voice,Text="One two"});
            speech.SpeechLines.Add(new AssemblySpeechLine {AudioPath=secondVoice,Text="Three four"});
            speech.SubtitleStyle.Enabled=true;
            foreach(var line in speech.SpeechLines) AssemblySpeechPlan.SaveTiming(line.AudioPath,line.Text,new WarmupAlignment {
                characters=line.Text.Select(c=>c.ToString()).ToArray(),
                character_start_times_seconds=line.Text.Select((c,i)=>.2+i*.1).ToArray(),
                character_end_times_seconds=line.Text.Select((c,i)=>.3+i*.1).ToArray() });
            var tracker=new AssemblyMixTracker();var rng=new Random(12);var usedVoices=new HashSet<string>();
            for(int i=0;i<2;i++) {
                var chosen=AssemblyPlanner.Create(speech,videos,new AssemblyHistory(),rng,1,tracker).Plans.Single();
                usedVoices.Add(chosen.Voiceover);
                Require(chosen.Music==music,"Speech file selected as background music");
                string work=Path.Combine(root,"speech-work-"+i);Directory.CreateDirectory(work);
                await AssemblySpeechPlan.Prepare(speech,chosen,ffmpeg,probe,work,ct);
                Require(chosen.Captions.Count==1 && Math.Abs(chosen.Captions[0].Start-.2)<.001,"Provider subtitle start changed");
                string graph=Path.Combine(work,"graph.txt");AssemblyRender.Arguments(speech,chosen,"libx264",graph,Path.Combine(root,"speech-"+i+".mp4"),new AssemblyRenderContext());
                string filters=File.ReadAllText(graph);
                Require(filters.Contains("volume=1,apad,atrim") && filters.Contains("volume=0.15,") && !filters.Contains("volume=0.1,"),"Voice is not 100% or music slider ignored");
                await AssemblyRender.Run(speech,chosen,ffmpeg,probe,Path.Combine(root,"speech-"+i+".mp4"),work,raster,ct,null);
                var actual=Core.VideoInfo(await Core.Probe(probe,Path.Combine(root,"speech-"+i+".mp4"),ct));
                Require(actual.HasAudio && Math.Abs(actual.VideoDuration-chosen.RollDuration(speech))<.15,"Speech render duration differs");
            }
            Require(usedVoices.Count==2,"Voice variants were concatenated or not rotated");
            // Two scenes use two independent voice pools; each selected WAV controls its own
            // scene duration (+ 1 second), even when the source videos are shorter.
            var sceneSpeech = new AssemblyTemplate {Materials=root,Videos=a,Width=240,Height=320,Fps=20,
                SceneTimingVersion=1,PackStudioVersion=1,AllowVideoReuse=true,LoopShortVideos=true,
                Transition="cut",RenderMode="cpu"};
            sceneSpeech.Scenes.Add(new AssemblyScene {Duration=2,VoiceMode="own",Voiceover=voice});
            sceneSpeech.Scenes.Add(new AssemblyScene {Duration=2,VoiceMode="own",Voiceover=secondVoice});
            var perScene = AssemblyPlanner.Create(sceneSpeech,videos,new AssemblyHistory(),new Random(9),1).Plans.Single();
            Require(string.IsNullOrEmpty(perScene.Voiceover) && perScene.Scenes[0].Voiceover==voice
                && perScene.Scenes[1].Voiceover==secondVoice,"Scene voices were not selected separately");
            string sceneWork=Path.Combine(root,"scene-speech-work");Directory.CreateDirectory(sceneWork);
            await AssemblySpeechPlan.Prepare(sceneSpeech,perScene,ffmpeg,probe,sceneWork,ct);
            Require(Math.Abs(perScene.Scenes[0].ContentSeconds-5)<.06
                && Math.Abs(perScene.Scenes[1].ContentSeconds-2.8)<.06
                && Math.Abs(perScene.RollDuration(sceneSpeech)-7.8)<.06,"Scene duration is not audio length plus one second");
            var sceneGraph=Path.Combine(sceneWork,"scene-graph.txt");
            AssemblyRender.Arguments(sceneSpeech,perScene,"libx264",sceneGraph,Path.Combine(root,"scene-voice.mp4"),new AssemblyRenderContext());
            string sceneFilters=File.ReadAllText(sceneGraph);
            Require(sceneFilters.Contains("sceneVoice0") && sceneFilters.Contains("sceneVoice1")
                && sceneFilters.Contains("adelay=5000"),"Scene voice tracks or offsets missing");
            string sceneOutput=Path.Combine(root,"scene-voice.mp4");
            await AssemblyRender.Run(sceneSpeech,perScene,ffmpeg,probe,sceneOutput,sceneWork,raster,ct,null);
            var sceneVideo=Core.VideoInfo(await Core.Probe(probe,sceneOutput,ct));
            Require(sceneVideo.HasAudio && Math.Abs(sceneVideo.VideoDuration-7.8)<.15,"Per-scene speech output duration/audio invalid");
            await AssemblySpeechPlan.Prepare(sceneSpeech,perScene,ffmpeg,probe,sceneWork,ct);
            Require(Math.Abs(perScene.RollDuration(sceneSpeech)-7.8)<.06,"Re-preparing scene voice changed duration");
            var clipped=WarmupSubtitles.FitToDuration(new[]{new WarmupCaption {Text="early",Start=.5,End=1},new WarmupCaption {Text="late",Start=4,End=5}},2);
            Require(clipped.Count==1 && clipped[0].Start==.5 && clipped[0].End==1,"Subtitle timeline was accelerated to fit video");
            // Large pools use backtracking instead of enumeration; shared unique packs still reserve distinct images.
            for(int i=0;i<3;i++)t.Scenes.Add(new AssemblyScene{Duration=1});
            t.Transition="cut";t.LoopShortVideos=true;
            var largeVideos=new List<AssemblyVideo>();
            for(int i=0;i<9;i++){string path=Path.Combine(root,"pool-"+i+".mp4");File.WriteAllText(path,"synthetic-pool-"+i);largeVideos.Add(new AssemblyVideo{Path=path,Hash=AssemblyFiles.Hash(path),Duration=10});}
            largeVideos.Add(videos[1]);
            t.Videos=root;t.ContinuousVideo=false;t.AllowVideoReuse=true;
            string pictures=Path.Combine(root,"pictures");Directory.CreateDirectory(pictures);
            for(int i=0;i<4;i++)File.WriteAllText(Path.Combine(pictures,i+".png"),"picture-"+i);
            t.GlobalLayers[0].Source=pictures;t.Scenes[0].Layers.Add(new AssemblyLayer{Source=pictures,Unique=true});
            var inventory=new AssemblyInventory(t,largeVideos,new AssemblyHistory());
            Require(inventory.Enumerate()==null,"Large-plan regression did not exercise backtracking");
            var large=inventory.CreateBatch(new Random(4),2,new AssemblyMixTracker(),ct);
            Require(large.Plans.Count==2 && large.Plans.SelectMany(p=>p.UniqueImages).Distinct().Count()==4,"Shared global/scene unique selection failed in backtracking");
            // Ordinary per-scene source selection remains independent of continuous mode.
            t.Scenes.RemoveRange(2,3);t.Scenes[0].Layers.Clear();t.Videos=a;t.Transition="fade";t.ContinuousVideo=false;t.SplitLayout="none";t.AllowVideoReuse=true;t.GlobalLayers.Clear();
            plan=AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(4),1).Plans.Single();
            Require(plan.Scenes.All(s=>s.VideoStart==0),"Scene-local source offset changed");
            await AssemblyRender.Run(t,plan,ffmpeg,probe,Path.Combine(root,"scene-local.mp4"),Path.Combine(root,"local"),raster,ct,null).ConfigureAwait(false);
        }
    }
}
