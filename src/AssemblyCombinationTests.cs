using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
namespace VideoBatch {
    public static class AssemblyCombinationTests {
        static void Check(bool value,string message) {if(!value)throw new Exception(message);}
        static void CheckPreviewIdentity(string root) {
            var t=new AssemblyTemplate {Materials=root,Videos="clips",Transition="cut",PackStudioVersion=1,AllowVideoReuse=true};
            Directory.CreateDirectory(t.Resolve("clips")); Directory.CreateDirectory(t.Resolve("intros"));
            for(int i=0;i<12;i++)File.WriteAllText(t.Resolve("clips/clip"+i+".mp4"),"clip"+i);
            for(int i=0;i<2;i++)File.WriteAllText(t.Resolve("intros/intro"+i+".mp4"),"intro"+i);
            t.Scenes.Add(new AssemblyScene {Name="Превью",IsPreview=true,Videos="intros",VideoMode="own",Duration=2});
            t.Scenes.Add(new AssemblyScene {Name="Нарезки",Duration=2});
            var videos=new[]{"clips","intros"}.SelectMany(f=>AssemblyFiles.Pool(t.Resolve(f),AssemblyFiles.VideoExtensions)).Select(p=>new AssemblyVideo {Path=p,Hash=AssemblyFiles.Hash(p),Duration=10}).ToList();
            Func<AssemblyHistory,AssemblyInventory> inventory=h=>new AssemblyInventory(t,videos,h);
            Check(inventory(new AssemblyHistory()).Calculate().Variants==24,"two previews initially multiply twelve clips");
            var oldPlan=inventory(new AssemblyHistory()).CreateBatch(new Random(4),1,new AssemblyMixTracker(),CancellationToken.None).Plans[0];
            t.Scenes[0].VideoInCombination=false; t.Combination.Background=false;
            Check(inventory(new AssemblyHistory()).Calculate().Variants==12,"preview excluded from count");
            var batch=inventory(new AssemblyHistory()).CreateBatch(new Random(7),30,new AssemblyMixTracker(),CancellationToken.None);
            Check(batch.Plans.Count==12 && batch.Plans.Select(p=>p.Scenes[1].Video.Hash).Distinct().Count()==12,"each clip rendered once");
            Check(batch.Plans.Select(p=>p.Scenes[0].Video.Hash).Distinct().Count()==2,"excluded previews still alternate");
            var history=new AssemblyHistory(); history.Combinations.Add(oldPlan.Signature);history.Records.Add(AssemblyHistoryRecord.FromPlan(oldPlan,Path.Combine(root,"old-output.mp4"),t.Id));
            Check(inventory(history).Calculate().Variants==11,"previous all-axis history projects onto selected scene");
            var remaining=inventory(history).CreateBatch(new Random(8),30,new AssemblyMixTracker(),CancellationToken.None);
            Check(remaining.Plans.Count==11 && remaining.Plans.All(p=>p.Scenes[1].Video.Hash!=oldPlan.Scenes[1].Video.Hash),"used clip not repeated after rule change");
            File.WriteAllText(t.Resolve("intros/new-intro.mp4"),"new intro"); videos.Add(new AssemblyVideo {Path=t.Resolve("intros/new-intro.mp4"),Hash=AssemblyFiles.Hash(t.Resolve("intros/new-intro.mp4")),Duration=10});
            Check(inventory(history).Calculate().Variants==11,"adding preview does not renew clips");
            string xml=Path.Combine(root,"scene-rule.xml"); AssemblyMarketProfiles.EnsureSeeded(t); AssemblyMarketProfiles.Capture(t,AssemblyProfile.Ru); AssemblyFiles.Save(xml,t);
            var restored=AssemblyFiles.Load<AssemblyTemplate>(xml); AssemblyMarketProfiles.Apply(restored,AssemblyProfile.Ru);
            Check(!restored.Scenes[0].VideoInCombination && restored.Scenes[1].VideoInCombination,"scene identity saved per profile");
            Console.WriteLine("Scene identity PASS: 12 clips / 2 previews, no repeats, alternating intros, old history, extra preview and profile XML.");
        }
        static IEnumerable<System.Windows.Forms.Control> Controls(System.Windows.Forms.Control parent) {
            foreach(System.Windows.Forms.Control child in parent.Controls) {yield return child; foreach(var nested in Controls(child))yield return nested;}
        }
        public static bool RunUiTests() {
            string previous=Store.Root, root=Path.Combine(Path.GetTempPath(),"combination-ui-"+Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(root); Store.Root=root;
                var t=AssemblyTemplate.Defaults(); t.Materials=Path.Combine(root,"materials"); t.Output=Path.Combine(root,"output");
                t.Scenes[0].IsPreview=true; AssemblyFiles.Save(Path.Combine(root,"assembly-template.xml"),t);
                Exception failure=null; int visited=0;
                using(var window=new AssemblyWindow(true)) using(var timer=new System.Windows.Forms.Timer {Interval=200}) {
                    window.Show(); System.Windows.Forms.Application.DoEvents();
                    var current=(AssemblyTemplate)typeof(AssemblyWindow).GetField("template",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(window);
                    var originalSettings=current.Combination;
                    var originalValues=new[]{originalSettings.Video,originalSettings.SecondaryVideo,originalSettings.Background,originalSettings.Music,originalSettings.Voiceover};
                    var originalVideos=current.Scenes.Select(s=>s.VideoInCombination).ToArray();
                    var originalLayers=current.Scenes.SelectMany(s=>s.Layers).Concat(current.GlobalLayers).Select(l=>l.InCombination).ToArray();
                    timer.Tick+=(s,e)=>{
                        var dialog=System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault(f=>f.Name=="AssemblyCombinationDialog"); if(dialog==null)return;
                        timer.Stop(); try {
                            var controls=Controls(dialog).ToArray();
                            var labelMethod=typeof(AssemblyWindow).GetMethod("SceneLabel",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                            for(int i=0;i<current.Scenes.Count;i++) {
                                var group=controls.First(c=>c.Name=="CombinationScene"+i);
                                Check(controls.First(c=>c.Name=="CombinationScene"+i+"Title").Text==(string)labelMethod.Invoke(window,new object[]{current.Scenes[i],i}),"scene heading matches editor");
                                Check(Controls(group).OfType<System.Windows.Forms.CheckBox>().Count()==1+current.Scenes[i].Layers.Count,"scene voice count is read-only");
                                Check(controls.First(c=>c.Name=="CombinationSceneVoice"+i).Text.StartsWith("Озвучка · "+AssemblySpeechPlan.SceneVoiceOptions(current,current.Scenes[i]).Count+" файлов"),"scene voice count matches speech options");
                            }
                            Check(controls.First(c=>c.Name=="CombinationScene0Title").Text=="Превью","preview editor label");
                            Check(controls.First(c=>c.Name=="CombinationScene1Title").Text=="Сцена 1","scene numbering excludes preview");
                            foreach(var size in new[]{dialog.MinimumSize,new System.Drawing.Size(1100,800)}) {
                                dialog.Size=size; dialog.PerformLayout(); System.Windows.Forms.Application.DoEvents();
                                Check(!((System.Windows.Forms.Panel)controls.First(c=>c.Name=="CombinationScroll")).HorizontalScroll.Visible,"dialog has no horizontal overflow");
                                foreach(var box in controls.OfType<System.Windows.Forms.CheckBox>())
                                    Check(box.Left>=0 && box.Right<=box.Parent.ClientSize.Width && box.Width>=box.PreferredSize.Width,"rule toggle fits at dialog width");
                            }
                            Check(controls.OfType<System.Windows.Forms.ListBox>().Any(),"UI file lists visible");
                            ((System.Windows.Forms.Button)controls.First(c=>c.Name=="CombinationMainVideos")).PerformClick();
                            Check(!((System.Windows.Forms.CheckBox)controls.First(c=>c.Name=="CombinationSceneVideo0")).Checked,"UI preset excludes preview");
                            Check(((System.Windows.Forms.CheckBox)controls.First(c=>c.Name=="CombinationSceneVideo1")).Checked,"UI preset includes main scene");
                            Check(!((System.Windows.Forms.CheckBox)controls.First(c=>c.Name=="CombinationMusic")).Checked && !((System.Windows.Forms.CheckBox)controls.First(c=>c.Name=="CombinationVoiceover")).Checked,"preset clears shared audio rules");
                            if(visited==0) {
                                foreach(var box in controls.OfType<System.Windows.Forms.CheckBox>())box.Checked=!box.Checked;
                                ((System.Windows.Forms.Button)controls.First(c=>c.Name=="CancelCombinationSettings")).PerformClick();
                            } else {
                                ((System.Windows.Forms.CheckBox)controls.First(c=>c.Name=="CombinationMusic")).Checked=true;
                                ((System.Windows.Forms.CheckBox)controls.First(c=>c.Name=="CombinationVoiceover")).Checked=true;
                                ((System.Windows.Forms.Button)controls.First(c=>c.Name=="SaveCombinationSettings")).PerformClick();
                            }
                            visited++;
                        } catch(Exception ex) {failure=ex; dialog.Close();}
                    };
                    var open=typeof(AssemblyWindow).GetMethod("OpenCombinationSettings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                    timer.Start(); open.Invoke(window,null);
                    if(failure!=null)throw failure; Check(visited==1,"cancel dialog opened");
                    Check(object.ReferenceEquals(current.Combination,originalSettings) && originalValues.SequenceEqual(new[]{current.Combination.Video,current.Combination.SecondaryVideo,current.Combination.Background,current.Combination.Music,current.Combination.Voiceover}),"cancel preserves shared settings");
                    Check(originalVideos.SequenceEqual(current.Scenes.Select(s=>s.VideoInCombination)) && originalLayers.SequenceEqual(current.Scenes.SelectMany(s=>s.Layers).Concat(current.GlobalLayers).Select(l=>l.InCombination)),"cancel preserves scene and layer flags");
                    timer.Start(); open.Invoke(window,null);
                    if(failure!=null)throw failure; Check(visited==2,"save dialog opened");
                    Check(!current.Scenes[0].VideoInCombination && current.Scenes[1].VideoInCombination && !current.Combination.Background,"preset saved");
                    Check(current.Combination.Music && current.Combination.Voiceover,"shared audio rules saved"); window.Close();
                }
                Console.WriteLine("Combination UI PASS: scene labels, responsive rows, read-only scene voices, shared audio, preset, cancel and save."); return true;
            } finally {Store.Root=previous; if(Directory.Exists(root))Directory.Delete(root,true);}
        }
        public static bool RunSelfTests() {
            string root=Path.Combine(Path.GetTempPath(),"combination-tests-"+Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(root);
                var t=new AssemblyTemplate {Materials=root,Videos="videos",Music="music",Backgrounds="backgrounds",Voiceover="voice",Transition="cut",PackStudioVersion=1,AllowVideoReuse=true};
                var scene=new AssemblyScene {Name="one",Duration=2};t.Scenes.Add(scene);
                var layer=new AssemblyLayer {Source="images"};scene.Layers.Add(layer);
                var global=new AssemblyLayer {Source="global"};t.GlobalLayers.Add(global);
                foreach(string folder in new[]{"videos","music","backgrounds","voice","images","global","sceneMusic"}) {
                    Directory.CreateDirectory(t.Resolve(folder));for(int i=0;i<2;i++)File.WriteAllText(t.Resolve(folder+"/"+i+(folder=="videos"?".mp4":folder=="music"||folder=="voice"||folder=="sceneMusic"?".wav":".png")),folder+i);
                }
                CheckPreviewIdentity(root);
                var videos=AssemblyFiles.Pool(t.Resolve(t.Videos),AssemblyFiles.VideoExtensions).Select(p=>new AssemblyVideo {Path=p,Hash=AssemblyFiles.Hash(p),Duration=10}).ToList();
                Func<AssemblyHistory,AssemblyInventory> inv=h=>new AssemblyInventory(t,videos,h);
                var initial=inv(new AssemblyHistory()).Enumerate();Check(initial.Count==16,"default visual dimensions");
                var first=initial.First(); string legacy=AssemblyFiles.HashText(first.Scenes[0].Video.Hash+":"+first.Scenes[0].Layers[0].Asset.Hash+"|global:"+first.GlobalLayers[0].Asset.Hash+"|background:"+first.Background.Hash);
                Check(first.Signature==legacy,"default signature compatibility");
                t.Combination.Video=false;t.Combination.Background=false;layer.InCombination=false;global.InCombination=false;
                Check(inv(new AssemblyHistory()).Calculate().Variants==1,"all off count");
                var one=inv(new AssemblyHistory()).CreateBatch(new Random(1),20,new AssemblyMixTracker(),CancellationToken.None);Check(one.Plans.Count==1,"all off batch");Check(one.Plans[0].Scenes[0].Layers.Count==1&&one.Plans[0].GlobalLayers.Count==1&&one.Plans[0].Background!=null&&!string.IsNullOrEmpty(one.Plans[0].Music)&&!string.IsNullOrEmpty(one.Plans[0].Voiceover),"excluded materials retained");
                t.Combination.Music=true;t.Combination.Voiceover=true;scene.Music="sceneMusic";
                var audio=inv(new AssemblyHistory()).CreateBatch(new Random(2),100,new AssemblyMixTracker(),CancellationToken.None);Check(audio.Plans.Count==8,"global music scene music and voice Cartesian product");
                Check(audio.Plans.Select(p=>p.MusicHash+"/"+p.Scenes[0].MusicHash+"/"+p.VoiceoverHash).Distinct().Count()==8,"selected audio survives completion");
                var h=new AssemblyHistory();h.Combinations.Add(audio.Plans[0].Signature);h.Records.Add(AssemblyHistoryRecord.FromPlan(audio.Plans[0],Path.Combine(root,"output.mp4"),t.Id));
                Check(inv(h).Calculate().Variants==7,"history audio count");
                t.Combination.Music=false;t.Combination.Voiceover=false;Check(inv(h).Calculate().Variants==0,"changed axes project history");
                // Scene voice is a separate concrete dimension; silent scenes add no alternatives.
                scene.VoiceMode="own";scene.Voiceover="voice";t.Combination.Voiceover=true;
                var sceneVoices=inv(new AssemblyHistory()).CreateBatch(new Random(3),10,new AssemblyMixTracker(),CancellationToken.None);Check(sceneVoices.Plans.Count==2&&sceneVoices.Plans.Select(p=>p.Scenes[0].VoiceoverHash).Distinct().Count()==2,"scene voice enumeration");
                scene.VoiceMode="";t.Combination.Voiceover=false;
                // Force the lazy path with a large ignored image pack, without losing render materials.
                for(int i=2;i<7000;i++) File.WriteAllText(t.Resolve("images/"+i+".png"),"large-image-"+i);
                Check(inv(new AssemblyHistory()).Enumerate()==null,"large raw pool uses lazy search");
                var lazy=inv(new AssemblyHistory()).CreateBatch(new Random(4),5,new AssemblyMixTracker(),CancellationToken.None);Check(lazy.Plans.Count==1&&lazy.Plans[0].Scenes[0].Layers.Count==1,"lazy all off one identity");
                var exhausted=new AssemblyHistory();exhausted.Combinations.Add(lazy.Plans[0].Signature);Check(inv(exhausted).Calculate().Variants==0,"large all off exhausted capacity");
                t.Combination.Music=true;var lazyAudio=inv(new AssemblyHistory()).CreateBatch(new Random(4),8,new AssemblyMixTracker(),CancellationToken.None);Check(lazyAudio.Plans.Count==4,"lazy audio Cartesian product");t.Combination.Music=false;
                // A large visual pool must not restart selected voice alternatives at the first file.
                for(int i=2;i<13;i++) File.WriteAllText(t.Resolve("voice/"+i+".wav"),"voice"+i);
                t.Combination.Video=true;layer.InCombination=true;t.Combination.Voiceover=true;
                scene.VoiceMode="own";scene.Voiceover="voice";
                var balancedScene=inv(new AssemblyHistory()).CreateBatch(new Random(7),26,new AssemblyMixTracker(),CancellationToken.None);
                Check(balancedScene.Plans.Count==26 && balancedScene.Plans.Take(13).Select(p=>p.Scenes[0].VoiceoverHash).Distinct().Count()==13,"lazy scene voices rotate through all 13 files");
                Check(balancedScene.Plans.GroupBy(p=>p.Scenes[0].VoiceoverHash).All(g=>g.Count()==2),"lazy scene voice usage balanced");
                scene.VoiceMode="";
                var balancedShared=inv(new AssemblyHistory()).CreateBatch(new Random(7),26,new AssemblyMixTracker(),CancellationToken.None);
                Check(balancedShared.Plans.Take(13).Select(p=>p.VoiceoverHash).Distinct().Count()==13,"lazy shared voices rotate through all 13 files");
                // The enumerated path must also count voice usage when scoring alternatives.
                var voiceScore=new AssemblyMixTracker();voiceScore.Record(balancedShared.Plans[0]);
                var unused=balancedShared.Plans.First(p=>p.VoiceoverHash!=balancedShared.Plans[0].VoiceoverHash);
                var sameVisual=new AssemblyPlan {VoiceoverHash=unused.VoiceoverHash};
                long unusedScore=voiceScore.Score(sameVisual);sameVisual.VoiceoverHash=balancedShared.Plans[0].VoiceoverHash;
                Check(voiceScore.Score(sameVisual)>unusedScore,"voice repetition affects candidate score");
                t.Combination.Video=false;layer.InCombination=false;t.Combination.Voiceover=false;
                string xml=Path.Combine(root,"template.xml");AssemblyMarketProfiles.EnsureSeeded(t);AssemblyMarketProfiles.Capture(t,AssemblyProfile.Ru);t.Combination.Video=true;layer=t.Scenes[0].Layers[0];layer.InCombination=true;AssemblyMarketProfiles.Capture(t,AssemblyProfile.En);AssemblyFiles.Save(xml,t);t=AssemblyFiles.Load<AssemblyTemplate>(xml);AssemblyMarketProfiles.Apply(t,AssemblyProfile.Ru);Check(!t.Combination.Video&&!t.Scenes[0].Layers[0].InCombination,"RU XML independent settings");AssemblyMarketProfiles.Apply(t,AssemblyProfile.En);Check(t.Combination.Video&&t.Scenes[0].Layers[0].InCombination,"EN XML independent settings");
                string history=Path.Combine(root,"history.xml");AssemblyFiles.Save(history,h);h=AssemblyFiles.Load<AssemblyHistory>(history);Check(h.Records[0].ToPlan().VoiceoverHash==audio.Plans[0].VoiceoverHash,"durable audio hash");
                return true;
            } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
        }
    }
}
