using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.IO;
using System.Threading;

namespace VideoBatch {
    public sealed class AssemblyMixTracker {
        readonly Dictionary<string,int> usage=new Dictionary<string,int>();
        public int Usage(string axis,string hash) { int n; return usage.TryGetValue(axis+"|"+hash,out n) ? n : 0; }
        void Add(string axis,string hash) { string key=axis+"|"+hash; usage[key]=Usage(axis,hash)+1; }
        public long Score(AssemblyPlan p) {
            long score=Usage("music",p.MusicHash ?? "") + Usage("voice",p.VoiceoverHash ?? "");
            for (int i=0; i<p.Scenes.Count; i++) score+=Usage("sceneVoice:"+i,p.Scenes[i].VoiceoverHash ?? "");
            if (p.Background != null) score += 64L * Usage("background", p.Background.Hash);
            for (int i=0; i<p.Scenes.Count; i++) {score+=Usage("video:"+i,p.Scenes[i].Video.Hash); foreach (var layer in p.Scenes[i].Layers.Where(l => !l.Layer.Fixed)) score+=64L*Usage("image:"+layer.Layer.ObjectId,layer.Asset.Hash);}
            foreach(var layer in p.GlobalLayers.Where(l => !l.Layer.Fixed)) score+=64L*Usage("image:"+layer.Layer.ObjectId,layer.Asset.Hash);
            if(p.SecondaryVideo!=null) score+=Usage("secondary",p.SecondaryVideo.Hash);
            return score;
        }
        public void Touch(string axis, string hash) {
            if (string.IsNullOrEmpty(hash)) return;
            Add(axis, hash);
        }
        public void Record(AssemblyPlan p) {
            if (p.Background != null) Add("background", p.Background.Hash);
            if(!string.IsNullOrWhiteSpace(p.Voiceover))Add("voice",AssemblyFiles.Hash(p.Voiceover));
            for (int i=0;i<p.Scenes.Count;i++) if(!string.IsNullOrWhiteSpace(p.Scenes[i].Voiceover)) Add("sceneVoice:"+i,AssemblyFiles.Hash(p.Scenes[i].Voiceover));
            foreach(var layer in p.GlobalLayers.Where(l => !l.Layer.Fixed)) Add("image:"+layer.Layer.ObjectId,layer.Asset.Hash);
            if(p.SecondaryVideo!=null) Add("secondary",p.SecondaryVideo.Hash);
            foreach(var scene in p.Scenes) if(!string.IsNullOrEmpty(scene.MusicHash)) Add("sceneMusic",scene.MusicHash);
            Add("music",p.MusicHash ?? ""); for (int i=0; i<p.Scenes.Count; i++) {
                var scene = p.Scenes[i];
                if (scene.VideoSegments != null && scene.VideoSegments.Count > 0) {
                    for (int si = 0; si < scene.VideoSegments.Count; si++) {
                        var seg = scene.VideoSegments[si];
                        Add(seg.IsContinuation ? "videoCont:" + i : "video:" + i, seg.Video.Hash);
                    }
                } else Add("video:"+i, scene.Video.Hash);
                foreach (var layer in scene.Layers.Where(l => !l.Layer.Fixed)) Add("image:"+layer.Layer.ObjectId,layer.Asset.Hash);
            }
        }
    }
    public sealed class AssemblyCapacity {
        public BigInteger Variants;
        public bool Exact;
        public int BatchLimit;
        /// <summary>Saved signatures in the active history ledger (RU/EN/…).</summary>
        public int UsedCombinations;
        /// <summary>When Exact, remaining + blocked-for-this-template (same materials/scenes).</summary>
        public BigInteger TotalCombinations;
        public string Caption {
            get {
                string head = UsedCombinations > 0 ? "Использовано " + UsedCombinations.ToString("N0") + " · " : "";
                string body = (Exact ? "осталось " : "до ") + Variants.ToString("N0");
                string ofTotal = Exact && TotalCombinations > 0 && TotalCombinations >= Variants
                    ? " из " + TotalCombinations.ToString("N0")
                    : "";
                string batch = Variants != BatchLimit ? " · сейчас до " + BatchLimit.ToString("N0") + " роликов" : "";
                if (body.Length > 0) body = char.ToUpper(body[0]) + body.Substring(1);
                return head + body + ofTotal + " связок" + batch;
            }
        }
    }
    // Content identity matters: copies and renamed files do not create new combinations.
    public sealed class AssemblyInventory {
        readonly AssemblyTemplate template;
        readonly AssemblyHistory history;
        readonly List<List<AssemblyVideo>> videos = new List<List<AssemblyVideo>>();
        readonly Dictionary<AssemblyLayer,List<AssemblyAsset>> pictures = new Dictionary<AssemblyLayer,List<AssemblyAsset>>();
        readonly List<AssemblyAsset> music;
        readonly List<AssemblyAsset> backgrounds;
        IEnumerable<AssemblyAsset> BackgroundOptions => backgrounds.Count == 0 ? new AssemblyAsset[] { null } : backgrounds;
        readonly HashSet<string> speechHashes;
        readonly List<AssemblyVideo> secondary;
        readonly Dictionary<AssemblyScene,List<AssemblyAsset>> sceneMusic = new Dictionary<AssemblyScene,List<AssemblyAsset>>();
        bool Split => template.SplitLayout != "none";
        readonly HashSet<string> previous;
        readonly HashSet<string> projected = new HashSet<string>();
        readonly List<AssemblyPlan> savedPlans = new List<AssemblyPlan>();
        Dictionary<string,List<AssemblyPlan>> alternatives;
        const int EnumerationLimit = 50000;
        static bool CatalogMatches(HashSet<string> allowed, AssemblyVideo v) {
            return allowed.Contains(v.Path) || !string.IsNullOrEmpty(v.SourcePath) && allowed.Contains(v.SourcePath);
        }
        bool SecondaryEligible(AssemblyVideo v) {
            if (v.Duration <= 0) return false;
            if (template.LoopShortVideos) return true;
            if (template.Scenes.Any(s => template.UsesVideoDuration(s))) return true;
            for (int i = 0; i < template.Scenes.Count; i++) {
                var scene = template.Scenes[i];
                if (scene.IsPreview) continue;
                double need = template.ContinuousVideo ? template.Duration : RenderDuration(i);
                if (v.Duration + .001 >= need) return true;
            }
            return v.Duration + .001 >= template.Duration;
        }
        public AssemblyInventory(AssemblyTemplate t, IList<AssemblyVideo> source, AssemblyHistory h) {
            template = t; history = h; previous = new HashSet<string>(h.Combinations);
            speechHashes = AssemblySpeechPlan.SpeechHashes(t);
            var used = new HashSet<string>(h.UsedImages);
            for (int i = 0; i < t.Scenes.Count; i++) {
                var scene = t.Scenes[i]; double duration = RenderDuration(i);
                var allowed = new HashSet<string>(Split && t.SplitSharedPool ? t.SplitVideoPaths() : AssemblyFiles.Pool(t.Resolve(t.ContinuousVideo ? t.Videos : AssemblySceneVideoBuilder.PrimarySource(t, scene)),AssemblyFiles.VideoExtensions),StringComparer.OrdinalIgnoreCase);
                double required = t.ContinuousVideo ? t.Duration : duration;
                AssemblySceneVideoBuilder.Migrate(scene);
                bool flexibleSceneVideo = !t.ContinuousVideo && t.SceneTimingVersion > 0 && scene.VideoMode == "own"
                    && (t.HasSceneVoiceovers && scene.VoiceMode != "none" || scene.VideoPacks.Count > 0);
                videos.Add(source.Where(v => CatalogMatches(allowed, v) && (t.UsesVideoDuration(scene) && v.Duration > 0 || v.Duration + .001 >= required || flexibleSceneVideo && v.Duration > 0 || (t.LoopShortVideos || !t.ContinuousVideo && t.SceneTimingVersion>0 && scene.VideoMode=="own") && v.Duration > 0)).GroupBy(v => v.Hash).Select(g => g.First()).ToList());
                sceneMusic[scene] = Assets(t.Resolve(scene.Music),AssemblyFiles.MusicExtensions).Where(a => !speechHashes.Contains(a.Hash)).ToList();
                foreach (var l in scene.Layers.Where(l => l.Enabled)) {
                    if (l.IsText) { pictures.Add(l, TextAssets(l)); continue; }
                    var pool = Assets(t.Resolve(l.Source),t.PictureExtensions);
                    if (pool.Count == 0) {
                        if (t.PackStudioVersion == 0) throw new Exception("Нет изображений в источнике «" + l.Name + "».");
                        continue; // Optional empty pack is different from an exhausted unique pack.
                    }
                    if (l.Fixed) pool = new List<AssemblyAsset> { FixedAsset(l,pool) };
                    else if (l.Unique) pool = pool.Where(a => !used.Contains(a.Hash)).ToList();
                    pictures.Add(l,pool);
                }
            }
            foreach (var l in t.GlobalLayers.Where(l => l.Enabled)) {
                if (l.IsText) { pictures.Add(l, TextAssets(l)); continue; }
                var pool = Assets(t.Resolve(l.Source),t.PictureExtensions);
                if (pool.Count == 0) { if (t.PackStudioVersion == 0) throw new Exception("Нет изображений в источнике «" + l.Name + "»."); continue; }
                if (l.Fixed) pool = new List<AssemblyAsset> { FixedAsset(l,pool) };
                else if (l.Unique) pool = pool.Where(a => !used.Contains(a.Hash)).ToList();
                pictures.Add(l,pool);
            }
            if (Split && t.SplitSharedPool) {
                var sharedPaths = new HashSet<string>(t.SplitVideoPaths(), StringComparer.OrdinalIgnoreCase);
                secondary = source.Where(v => CatalogMatches(sharedPaths, v) && SecondaryEligible(v)).GroupBy(v => v.Hash).Select(g => g.First()).ToList();
            } else {
                var secondaryPaths = new HashSet<string>(AssemblyFiles.Pool(t.Resolve(t.SecondaryVideos),AssemblyFiles.VideoExtensions),StringComparer.OrdinalIgnoreCase);
                secondary = Split ? source.Where(v => CatalogMatches(secondaryPaths, v) && SecondaryEligible(v)).GroupBy(v => v.Hash).Select(g => g.First()).ToList() : new List<AssemblyVideo>();
            }
            backgrounds = Assets(t.Resolve(t.Backgrounds), AssemblyFiles.BackgroundExtensions);
            music = Assets(t.Resolve(t.Music),AssemblyFiles.MusicExtensions).Where(a => !string.IsNullOrWhiteSpace(a.Path) && !speechHashes.Contains(a.Hash)).ToList();
            savedPlans.AddRange(h.Records.Where(r=>!r.Pending).Select(r=>r.ToPlan()));
            foreach(string output in h.Outputs) {
                string recipe=AssemblyOutputFiles.FindRecipe(output); if(!File.Exists(recipe))continue;
                try { var p=AssemblyFiles.Load<AssemblyPlan>(recipe); savedPlans.RemoveAll(x=>x.Signature==p.Signature);savedPlans.Add(p); } catch { /* Keep the durable record when a diagnostic recipe is unavailable. */ }
            }
            foreach(var p in savedPlans) if(CanProject(p)) projected.Add(VisualSignature(SignatureText(p)));
        }
        static List<AssemblyAsset> TextAssets(AssemblyLayer layer) {
            return new List<AssemblyAsset> { new AssemblyAsset { Path = "", Hash = AssemblyFiles.HashText("text:" + layer.Text) } };
        }
        static List<AssemblyAsset> Assets(string path,string[] extensions) {
            return AssemblyFiles.Pool(path,extensions).Select(p => new AssemblyAsset { Path=p, Hash=AssemblyFiles.Hash(p) }).GroupBy(a => a.Hash).Select(g => g.First()).ToList();
        }
        public static AssemblyAsset FixedAsset(AssemblyLayer layer,List<AssemblyAsset> pool) {
            var asset = string.IsNullOrWhiteSpace(layer.FixedAssetHash) ? pool.FirstOrDefault() : pool.FirstOrDefault(a => a.Hash == layer.FixedAssetHash);
            if (asset == null) throw new Exception("Закреплённая картинка «" + layer.Name + "» отсутствует. Выбери новую в её пачке или включи «Меняется».");
            return asset;
        }
        double RenderDuration(int i) { return template.Scenes[i].Duration + (template.SceneTimingVersion==0 && i < template.Scenes.Count-1 && template.Transition != "cut" ? template.TransitionDuration : 0); }
        BigInteger RawBound() {
            BigInteger total = Math.Max(1, backgrounds.Count);
            foreach (var v in (template.ContinuousVideo ? videos.Take(1) : videos)) total *= v.Count;
            if (Split) total *= secondary.Count * (template.SplitFixHalves ? 1 : 2);
            foreach (var pool in pictures.Values) total *= pool.Count;
            return total * AudioBound();
        }
        BigInteger AudioBound() {
            BigInteger n=1;
            if(Identity.Music) { n*=Math.Max(1,music.Count); foreach(var pool in sceneMusic.Values)n*=Math.Max(1,pool.Count); }
            if(Identity.Voiceover) {if(template.HasSceneVoiceovers)foreach(var s in template.Scenes)n*=Math.Max(1,VoiceOptions(s).Count);else n*=Math.Max(1,VoiceOptions(null).Count);}
            return n;
        }
        BigInteger IdentityBound() {
            BigInteger n=AudioBound();
            if(Identity.Video) {
                if(template.ContinuousVideo) { if(template.Scenes.Any(s=>s.VideoInCombination)) n*=videos[0].Count; }
                else for(int i=0;i<videos.Count;i++) if(template.Scenes[i].VideoInCombination)n*=videos[i].Count;
                if(Split && Identity.SecondaryVideo)n*=secondary.Count*(template.SplitFixHalves?1:2);
            }
            if(Identity.Background)n*=Math.Max(1,backgrounds.Count);
            foreach(var entry in pictures)if(entry.Key.InCombination)n*=entry.Value.Count;
            return n;
        }
        static string SceneVideoSignature(AssemblyScenePlan s) {
            if (s.VideoSegments != null && s.VideoSegments.Count > 0)
                return string.Join(">", s.VideoSegments.Select(v => v.Video.Hash + "@" + v.Duration.ToString("0.###", Core.Inv)));
            return s.Video.Hash;
        }
        static string LegacySignatureText(AssemblyPlan p) {
            if (p.SplitSwapped && p.Scenes.Count == 1 && p.SecondaryVideo != null && (p.Scenes[0].VideoSegments == null || p.Scenes[0].VideoSegments.Count == 0)) {
                string swapped = p.SecondaryVideo.Hash + ":" + string.Join(",", p.Scenes[0].Layers.Select(l => l.Asset.Hash));
                if (p.GlobalLayers.Count > 0) swapped += "|global:" + string.Join(",", p.GlobalLayers.Select(l => l.Asset.Hash));
                return swapped + "|secondary:" + p.Scenes[0].Video.Hash + (p.Background == null ? "" : "|background:" + p.Background.Hash);
            }
            string raw = string.Join("|",p.Scenes.Select(s => SceneVideoSignature(s) + ":" + string.Join(",",s.Layers.Select(l => l.Asset.Hash))));
            if (p.GlobalLayers.Count > 0) raw += "|global:" + string.Join(",",p.GlobalLayers.Select(l => l.Asset.Hash));
            if (p.SecondaryVideo != null) raw += "|secondary:" + p.SecondaryVideo.Hash;
            if (p.SplitSwapped) raw += "|splitSwap:1";
            if (p.Background != null) raw += "|background:" + p.Background.Hash;
            return raw;
        }
        AssemblyCombinationSettings Identity => template.Combination ?? new AssemblyCombinationSettings();
        bool SelectedVideo => Identity.Video && (template.Scenes.Any(s=>s.VideoInCombination) || Split && Identity.SecondaryVideo);
        bool EmptyIdentity => !SelectedVideo && !Identity.Background && !Identity.Music && !Identity.Voiceover && pictures.Keys.All(l=>!l.InCombination);
        bool DefaultIdentity => Identity.Video && Identity.SecondaryVideo && template.Scenes.All(s=>s.VideoInCombination) && Identity.Background && !Identity.Music && !Identity.Voiceover && template.Scenes.SelectMany(s => s.Layers).Concat(template.GlobalLayers).All(l => l.InCombination);
        string SignatureText(AssemblyPlan p) {
            if (DefaultIdentity) return LegacySignatureText(p);
            var parts = new List<string> { "identity-v1" };
            if (SelectedVideo) {
                if(template.Scenes.All(s=>s.VideoInCombination) && Identity.SecondaryVideo) {
                    // Preserve existing split orientation equivalence for the all-video rule.
                    var videoOnly = new AssemblyPlan { Scenes=p.Scenes.Select(s => new AssemblyScenePlan { Video=s.Video, VideoSegments=s.VideoSegments }).ToList(), SecondaryVideo=p.SecondaryVideo, SplitSwapped=p.SplitSwapped };
                    parts.Add("video:" + LegacySignatureText(videoOnly));
                } else {
                    for(int i=0;i<Math.Min(p.Scenes.Count,template.Scenes.Count);i++)
                        if(template.Scenes[i].VideoInCombination) parts.Add("sceneVideo:"+i+":"+SceneVideoSignature(p.Scenes[i]));
                    if(Split && Identity.SecondaryVideo) parts.Add("secondary:"+(p.SecondaryVideo?.Hash??"")+"|swap:"+p.SplitSwapped);
                }
            }
            if (Identity.Background) parts.Add("background:" + (p.Background == null ? "" : p.Background.Hash));
            for(int i=0;i<Math.Min(p.Scenes.Count,template.Scenes.Count);i++) {
                var active=template.Scenes[i].Layers.Where(l=>l.Enabled && pictures.ContainsKey(l)).ToList();
                for(int j=0;j<Math.Min(active.Count,p.Scenes[i].Layers.Count);j++) if(active[j].InCombination) parts.Add("image:"+i+":"+j+":"+p.Scenes[i].Layers[j].Asset.Hash);
            }
            var globals=template.GlobalLayers.Where(l=>l.Enabled && pictures.ContainsKey(l)).ToList();
            for(int j=0;j<Math.Min(globals.Count,p.GlobalLayers.Count);j++) if(globals[j].InCombination) parts.Add("global:"+j+":"+p.GlobalLayers[j].Asset.Hash);
            if(Identity.Music) { parts.Add("music:"+(p.MusicHash??"")); for(int i=0;i<p.Scenes.Count;i++) parts.Add("sceneMusic:"+i+":"+(p.Scenes[i].MusicHash??"")); }
            if(Identity.Voiceover) { parts.Add("voice:"+VoiceHash(p.Voiceover,p.VoiceoverHash)); for(int i=0;i<p.Scenes.Count;i++)parts.Add("sceneVoice:"+i+":"+VoiceHash(p.Scenes[i].Voiceover,p.Scenes[i].VoiceoverHash)); }
            return string.Join("|",parts);
        }
        static string VoiceHash(string path,string hash) { return !string.IsNullOrEmpty(hash)?hash:!string.IsNullOrEmpty(path)&&File.Exists(path)?AssemblyFiles.Hash(path):""; }
        static AssemblyPlan CopyPlan(AssemblyPlan p) {
            return new AssemblyPlan { Signature=p.Signature, UniqueImages=new List<string>(p.UniqueImages), GlobalLayers=new List<AssemblyLayerPlan>(p.GlobalLayers), Background=p.Background, SecondaryVideo=p.SecondaryVideo, SplitSwapped=p.SplitSwapped, Music=p.Music, MusicHash=p.MusicHash, Voiceover=p.Voiceover, VoiceoverHash=p.VoiceoverHash,
                Scenes=p.Scenes.Select(s=>new AssemblyScenePlan { Scene=s.Scene, Video=s.Video, RenderDuration=s.RenderDuration, Layers=new List<AssemblyLayerPlan>(s.Layers), Music=s.Music, MusicHash=s.MusicHash, Voiceover=s.Voiceover, VoiceoverHash=s.VoiceoverHash }).ToList() };
        }
        List<AssemblyAsset> VoiceOptions(AssemblyScene scene) {
            var paths=scene==null?AssemblySpeechPlan.SharedVoiceOptions(template):AssemblySpeechPlan.SceneVoiceOptions(template,scene);
            if(scene!=null && scene.VoiceMode!="none" && scene.VoiceMode!="" && paths.Count==0) throw new Exception(scene.Name+": не найдены файлы озвучки.");
            return paths.Select(p=>new AssemblyAsset {Path=p,Hash=AssemblyFiles.Hash(p)}).GroupBy(a=>a.Hash).Select(g=>g.First()).ToList();
        }
        // Selected audio dimensions are concrete alternatives before signing, never random afterwards.
        IEnumerable<AssemblyPlan> AudioVariants(AssemblyPlan p, AssemblyMixTracker mix = null, Random random = null) {
            var setters=new List<Action<AssemblyAsset>>(); var pools=new List<List<AssemblyAsset>>();
            Action<List<AssemblyAsset>,Action<AssemblyAsset>,string> add=(pool,set,axis)=>{
                if (mix != null && (axis == "voice" || axis.StartsWith("sceneVoice:"))) pool=pool.OrderBy(a=>mix.Usage(axis,a.Hash)).ThenBy(a=>random.Next()).ToList();
                pools.Add(pool.Count==0?new List<AssemblyAsset>{null}:pool);setters.Add(set);
            };
            if(Identity.Music) {
                add(music,a=>{p.Music=a?.Path??"";p.MusicHash=a?.Hash??"";},"music");
                foreach(var s in p.Scenes) {var scene=s;add(sceneMusic[s.Scene],a=>{scene.Music=a?.Path??"";scene.MusicHash=a?.Hash??"";},"sceneMusic");}
            }
            if(Identity.Voiceover) {
                if(template.HasSceneVoiceovers) for(int i=0;i<p.Scenes.Count;i++) {var scene=p.Scenes[i];add(VoiceOptions(scene.Scene),a=>{scene.Voiceover=a?.Path??"";scene.VoiceoverHash=a?.Hash??"";},"sceneVoice:"+i);}
                else add(VoiceOptions(null),a=>{p.Voiceover=a?.Path??"";p.VoiceoverHash=a?.Hash??"";},"voice");
            }
            return WalkAudio(0,p,pools,setters);
        }
        IEnumerable<AssemblyPlan> WalkAudio(int index,AssemblyPlan p,List<List<AssemblyAsset>> pools,List<Action<AssemblyAsset>> setters) {
            if(index==pools.Count) {yield return CopyPlan(p);yield break;}
            foreach(var a in pools[index]) {setters[index](a);foreach(var candidate in WalkAudio(index+1,p,pools,setters))yield return candidate;}
        }
        List<AssemblyVideo> SecondaryOptions(AssemblyPlan plan) {
            if (!Split) return new List<AssemblyVideo> { null };
            var used = new HashSet<string>(plan.Scenes.Select(s => s.Video.Hash));
            return secondary.Where(v => !used.Contains(v.Hash)).ToList();
        }
        void AddVisualVariants(AssemblyPlan plan, List<AssemblyPlan> output) {
            foreach (var background in BackgroundOptions)
            foreach (var second in SecondaryOptions(plan)) {
                plan.Background = background;
                plan.SecondaryVideo = second;
                int swaps = Split && !template.SplitFixHalves ? 2 : 1;
                for (int swap = 0; swap < swaps; swap++) {
                    plan.SplitSwapped = swap == 1;
                    foreach(var candidate in AudioVariants(plan)) {
                        string raw=SignatureText(candidate); Sign(candidate);
                        if(!CombinationBlocked(raw)) output.Add(candidate);
                    }
                }
            }
        }
        static string VisualSignature(string raw) { return AssemblyFiles.HashText(raw); }
        void Sign(AssemblyPlan p) { p.Signature = VisualSignature(SignatureText(p)); }
        bool CombinationBlocked(string raw) {
            if (previous.Contains(VisualSignature(raw)) || projected.Contains(VisualSignature(raw))) return true;
            if (!DefaultIdentity) return false;
            foreach (var song in music) {
                if (string.IsNullOrEmpty(song.Hash)) continue;
                if (previous.Contains(AssemblyFiles.HashText(raw + "|music:" + song.Hash))) return true;
            }
            return false;
        }
        void AssignMusic(AssemblyPlan p,Random random,AssemblyMixTracker mix) {
            var pool = music.Where(a => !string.IsNullOrWhiteSpace(a.Path)).ToList();
            if (pool.Count == 0) { p.Music = ""; p.MusicHash = ""; return; }
            var song = pool.OrderBy(a => mix.Usage("music", a.Hash)).ThenBy(a => random.Next()).First();
            p.Music = song.Path; p.MusicHash = song.Hash;
        }
        public List<AssemblyPlan> Enumerate() {
            if (template.Scenes.Count==0) return new List<AssemblyPlan>();
            if (RawBound() > EnumerationLimit) return null;
            var output = new List<AssemblyPlan>();
            WalkScenes(0,new AssemblyPlan(),new HashSet<string>(),new HashSet<string>(),output);
            alternatives=output.GroupBy(p=>p.Signature).ToDictionary(g=>g.Key,g=>g.ToList());
            return alternatives.Values.Select(g=>g[0]).ToList();
        }
        void WalkScenes(int index,AssemblyPlan plan,HashSet<string> selectedVideo,HashSet<string> selectedImage,List<AssemblyPlan> output) {
            if (index == videos.Count) {
                var global = new AssemblyScenePlan { Layers=plan.GlobalLayers };
                WalkLayers(0,template.GlobalLayers.Where(l => l.Enabled && pictures.ContainsKey(l)).ToList(),global,plan,selectedImage,() => {
                    AddVisualVariants(plan, output);
                });
                return;
            }
            var available = template.ContinuousVideo && index>0 ? new List<AssemblyVideo> {plan.Scenes[0].Video} : videos[index].Where(v => !selectedVideo.Contains(v.Hash)).ToList();
            if (available.Count == 0 && template.AllowVideoReuse) available = videos[index];
            foreach (var v in available) {
                bool added = selectedVideo.Add(v.Hash);
                var scene = new AssemblyScenePlan { Scene=template.Scenes[index],Video=v,RenderDuration=RenderDuration(index) }; plan.Scenes.Add(scene);
                var layers = scene.Scene.Layers.Where(l => l.Enabled && pictures.ContainsKey(l)).ToList();
                WalkLayers(0,layers,scene,plan,selectedImage,() => WalkScenes(index+1,plan,selectedVideo,selectedImage,output));
                plan.Scenes.RemoveAt(plan.Scenes.Count-1); if (added) selectedVideo.Remove(v.Hash);
            }
        }
        void WalkLayers(int i,List<AssemblyLayer> layers,AssemblyScenePlan scene,AssemblyPlan plan,HashSet<string> selected,Action next) {
            if (i == layers.Count) { next(); return; }
            var l = layers[i];
            foreach (var a in pictures[l]) {
                if ((l.Unique && !l.IsText) && selected.Contains(a.Hash)) continue;
                scene.Layers.Add(new AssemblyLayerPlan { Layer=l,Asset=a });
                if ((l.Unique && !l.IsText)) { selected.Add(a.Hash); plan.UniqueImages.Add(a.Hash); }
                WalkLayers(i+1,layers,scene,plan,selected,next);
                scene.Layers.RemoveAt(scene.Layers.Count-1);
                if ((l.Unique && !l.IsText)) { selected.Remove(a.Hash); plan.UniqueImages.RemoveAt(plan.UniqueImages.Count-1); }
            }
        }
        public AssemblyPlanBatch CreateBatch(Random random,int count,AssemblyMixTracker mix,CancellationToken ct) {
            if (template.Scenes.Count == 0) return new AssemblyPlanBatch {Limit="Добавь хотя бы одну сцену перед сборкой."};
            var candidates=Enumerate();
            if (candidates!=null) return Take(candidates,random,count,mix,ct);
            var batch=new AssemblyPlanBatch(); var used=new HashSet<string>(history.UsedImages); var combinations=new HashSet<string>(history.Combinations);
            int target=(int)BigInteger.Min(Math.Min(count,UniqueLimit(used)),IdentityBound());
            for (int i=0; i<target; i++) {
                ct.ThrowIfCancellationRequested();
                var plan=FindNext(used,combinations,mix,random,ct,target-i-1);
                if (plan==null && target-i-1>0) plan=FindNext(used,combinations,mix,random,ct,0);
                if (plan==null) break;
                Complete(plan,random,mix); batch.Plans.Add(plan); combinations.Add(plan.Signature); used.UnionWith(plan.UniqueImages);
            }
            if (batch.Plans.Count<count) batch.Limit=LimitReason();
            return batch;
        }
        string LimitReason() {
            if (videos.Any(v => v.Count == 0) || Split && secondary.Count == 0) return "Нет подходящего видео для одной из сцен. Добавь фон или уменьши длительность.";
            if (Split && template.SplitSharedPool && secondary.Count < 2) return "Для общей пачки нужно минимум 2 разных видео.";
            return pictures.Any(p => p.Key.Unique && !p.Key.IsText) ? "Закончились новые связки или материалы без повторов." : "Исчерпаны разные сочетания материалов.";
        }
        public AssemblyPlanBatch Take(List<AssemblyPlan> candidates,Random random,int count,AssemblyMixTracker mix = null,CancellationToken ct = default(CancellationToken)) {
            mix=mix ?? new AssemblyMixTracker();
            var batch=new AssemblyPlanBatch(); var used=new HashSet<string>(history.UsedImages);
            for (int i=candidates.Count-1; i>0; i--) { int j=random.Next(i+1); var p=candidates[i]; candidates[i]=candidates[j]; candidates[j]=p; }
            int target=Math.Min(count,Math.Min(candidates.Count,UniqueLimit(used)));
            while (batch.Plans.Count<target) {
                ct.ThrowIfCancellationRequested(); AssemblyPlan best=null; long bestScore=long.MaxValue;
                foreach (var p in candidates.SelectMany(candidate => alternatives != null && alternatives.ContainsKey(candidate.Signature) ? alternatives[candidate.Signature] : new List<AssemblyPlan>{candidate})) {
                    ct.ThrowIfCancellationRequested();
                    if (p.UniqueImages.Any(used.Contains)) continue;
                    long score=mix.Score(p);
                    if (score>=bestScore) continue;
                    var after=new HashSet<string>(used); after.UnionWith(p.UniqueImages);
                    if (UniqueLimit(after)<target-batch.Plans.Count-1) continue;
                    best=p; bestScore=score; if (score==0) break;
                }
                if (best==null) best=candidates.SelectMany(candidate => alternatives != null && alternatives.ContainsKey(candidate.Signature) ? alternatives[candidate.Signature] : new List<AssemblyPlan>{candidate}).Where(p => !p.UniqueImages.Any(used.Contains)).OrderBy(mix.Score).FirstOrDefault();
                if (best==null) break;
                candidates.RemoveAll(p=>p.Signature==best.Signature);
                used.UnionWith(best.UniqueImages); Complete(best,random,mix); batch.Plans.Add(best);
            }
            if (batch.Plans.Count<count) batch.Limit=LimitReason();
            return batch;
        }
        void Complete(AssemblyPlan p,Random random,AssemblyMixTracker mix) {
            AssemblyTimeline.Complete(template,p,random);
            AssemblyTimeline.ApplyVideoDuration(template,p);
            if (!Identity.Music) AssignMusic(p,random,mix);
            double time=0;
            foreach(var scene in p.Scenes) {
                if (template.ContinuousVideo) scene.VideoStart=time;
                time+=scene.Scene.Duration+(template.SceneTimingVersion>0 && template.Transition!="cut"?template.TransitionSeconds:0);
                if (!Identity.Music) {
                    var pool=sceneMusic[scene.Scene];
                    var song=pool.OrderBy(a => mix.Usage("sceneMusic",a.Hash)).ThenBy(a => random.Next()).FirstOrDefault();
                    scene.Music=song==null?"":song.Path;scene.MusicHash=song==null?"":song.Hash;
                }
            }
            if (!Identity.Voiceover && template.HasSceneVoiceovers) {
                p.Voiceover = "";
                for(int i=0;i<p.Scenes.Count;i++) p.Scenes[i].Voiceover = AssemblySpeechPlan.SelectSceneVoiceover(template,p.Scenes[i].Scene,i,random,mix);
            } else if (!Identity.Voiceover) p.Voiceover=AssemblySpeechPlan.SelectVoiceover(template,random,mix);
            p.VoiceoverHash=VoiceHash(p.Voiceover,p.VoiceoverHash);
            foreach(var scene in p.Scenes) scene.VoiceoverHash=VoiceHash(scene.Voiceover,scene.VoiceoverHash);
            p.Captions=template.Captions.Select(c => new WarmupCaption {Text=c.Text,Start=c.Start,End=c.End}).ToList();
            mix.Record(p);
        }
        AssemblyPlan FindNext(HashSet<string> used,HashSet<string> combinations,AssemblyMixTracker mix,Random random,CancellationToken ct,int reserve) {
            if(EmptyIdentity && (CombinationBlocked("identity-v1") || combinations.Contains(VisualSignature("identity-v1"))))return null;
            AssemblyPlan found=null; var plan=new AssemblyPlan(); var chosenVideo=new HashSet<string>(); var chosenImage=new HashSet<string>();
            Func<int,bool> scenes=null; Func<int,List<AssemblyLayer>,List<AssemblyLayerPlan>,Func<bool>,bool> layers=null;
            layers=(i,list,target,next) => {
                ct.ThrowIfCancellationRequested(); if (i==list.Count) return next();
                var l=list[i]; var pool=pictures[l].Where(a => !(l.Unique && !l.IsText) || !used.Contains(a.Hash) && !chosenImage.Contains(a.Hash)).OrderBy(a => mix.Usage("image:"+l.ObjectId,a.Hash)).ThenBy(a => random.Next()).ToList();
                foreach (var a in pool) {
                    target.Add(new AssemblyLayerPlan {Layer=l,Asset=a}); if ((l.Unique && !l.IsText)) {chosenImage.Add(a.Hash); plan.UniqueImages.Add(a.Hash);}
                    if (layers(i+1,list,target,next)) return true;
                    target.RemoveAt(target.Count-1); if ((l.Unique && !l.IsText)) {chosenImage.Remove(a.Hash); plan.UniqueImages.RemoveAt(plan.UniqueImages.Count-1);}
                }
                return false;
            };
            scenes=i => {
                ct.ThrowIfCancellationRequested();
                if (i==videos.Count) {
                    if (reserve>0) {var after=new HashSet<string>(used); after.UnionWith(plan.UniqueImages); if (UniqueLimit(after)<reserve) return false;}
                    return layers(0,template.GlobalLayers.Where(l => l.Enabled && pictures.ContainsKey(l)).ToList(),plan.GlobalLayers,() => {
                        foreach (var background in BackgroundOptions.OrderBy(a => a == null ? 0 : mix.Usage("background", a.Hash)).ThenBy(a => random.Next()))
                        foreach (var second in SecondaryOptions(plan).OrderBy(v => v == null ? 0 : mix.Usage("secondary", v.Hash)).ThenBy(v => random.Next())) {
                            plan.Background = background;
                            plan.SecondaryVideo = second;
                            int swaps = Split && !template.SplitFixHalves ? 2 : 1;
                            for (int swap = 0; swap < swaps; swap++) {
                                plan.SplitSwapped = swap == 1;
                                foreach(var candidate in AudioVariants(plan,mix,random)) {
                                    string raw=SignatureText(candidate); Sign(candidate);
                                    if(combinations.Contains(candidate.Signature) || CombinationBlocked(raw)) continue;
                                    if (reserve > 0) { var after = new HashSet<string>(used); after.UnionWith(candidate.UniqueImages); if (UniqueLimit(after) < reserve) continue; }
                                    found=candidate; return true;
                                }
                            }
                        }
                        return false;
                    });
                }
                var pool=template.ContinuousVideo && i>0 ? new List<AssemblyVideo> {plan.Scenes[0].Video} : videos[i].Where(v => !chosenVideo.Contains(v.Hash)).ToList(); if (pool.Count==0 && template.AllowVideoReuse) pool=videos[i];
                foreach (var v in pool.OrderBy(v => mix.Usage("video:"+i,v.Hash)).ThenBy(v => random.Next())) {
                    bool added=chosenVideo.Add(v.Hash); var sp=new AssemblyScenePlan {Scene=template.Scenes[i],Video=v,RenderDuration=RenderDuration(i)}; plan.Scenes.Add(sp);
                    var ls=sp.Scene.Layers.Where(l => l.Enabled && pictures.ContainsKey(l)).ToList();
                    if (layers(0,ls,sp.Layers,() => scenes(i+1))) return true;
                    plan.Scenes.RemoveAt(plan.Scenes.Count-1); if (added) chosenVideo.Remove(v.Hash);
                }
                return false;
            };
            scenes(0); return found;
        }
        // Repeated bipartite matching accounts for shared/overlapping unique image packs.
        int UniqueLimit(HashSet<string> used) {
            var pools = pictures.Where(p => p.Key.Unique && !p.Key.IsText).Select(p => p.Value.Select(a => a.Hash).Where(h => !used.Contains(h)).Distinct().ToList()).ToList();
            if (pools.Count == 0) return int.MaxValue;
            if (pools.SelectMany(p => p).GroupBy(h => h).All(g => g.Count()==1)) return Math.Min(int.MaxValue,pools.Min(p => p.Count));
            if (pools.All(p => new HashSet<string>(p).SetEquals(pools[0]))) return Math.Min(int.MaxValue,pools[0].Count/pools.Count);
            int high = Math.Min(int.MaxValue,Math.Min(pools.Min(p => p.Count),pools.SelectMany(p => p).Distinct().Count()/pools.Count)), low=0;
            while (low < high) { int mid=low+(high-low+1)/2; if (CanMatch(pools,mid)) low=mid; else high=mid-1; }
            return low;
        }
        static bool CanMatch(List<List<string>> pools,int copies) {
            var slots = Enumerable.Range(0,copies).SelectMany(_ => pools).OrderBy(p => p.Count).ToList(); var owners=new Dictionary<string,int>();
            for (int i=0; i<slots.Count; i++) if (!Match(i,slots,owners,new HashSet<string>())) return false;
            return true;
        }
        static bool Match(int slot,List<List<string>> slots,Dictionary<string,int> owners,HashSet<string> visited) {
            foreach (var hash in slots[slot]) { if (!visited.Add(hash)) continue; int prior; if (!owners.TryGetValue(hash,out prior) || Match(prior,slots,owners,visited)) { owners[hash]=slot; return true; } }
            return false;
        }
        public AssemblyCapacity Calculate() {
            if (template.Scenes.Count == 0) return new AssemblyCapacity {Exact=true,Variants=0,BatchLimit=0};
            var list = Enumerate(); var result = new AssemblyCapacity { Exact=list != null,Variants=list == null ? IdentityBound() : list.Count,BatchLimit=UniqueLimit(new HashSet<string>(history.UsedImages)) };
            if (list == null && DefaultIdentity) {
                // The usual studio uses a common video pool. Count its ordered choices exactly.
                bool disjoint=videos.SelectMany(v => v.Select(a => a.Hash)).GroupBy(h => h).All(g => g.Count()==1);
                bool same = videos.All(v => new HashSet<string>(v.Select(a => a.Hash)).SetEquals(videos[0].Select(a => a.Hash)));
                bool noSharedUnique = pictures.Where(p => p.Key.Unique && !p.Key.IsText).SelectMany(p => p.Value.Select(a => a.Hash)).GroupBy(h => h).All(g => g.Count()==1);
                if ((template.ContinuousVideo || disjoint || same && (template.AllowVideoReuse || videos[0].Count >= videos.Count)) && noSharedUnique && (!Split || !secondary.Any(v => videos.Any(pool => pool.Any(a => a.Hash == v.Hash))))) {
                    int n=videos[0].Count; BigInteger count=template.ContinuousVideo?n:1;
                    if (!template.ContinuousVideo) for (int i=0; i<videos.Count; i++) count *= disjoint ? videos[i].Count : (i<n ? n-i : n);
                    if (Split) {
                        count *= secondary.Count;
                        if (!template.SplitFixHalves) count *= 2;
                    }
                    foreach (var pool in pictures.Values) count *= pool.Count;
                    count *= Math.Max(1, backgrounds.Count);
                    result.Variants=count; result.Exact=true;
                    SubtractHistory(result);
                }
            }
            if (list == null && !DefaultIdentity && IdentityBound() == 1) {
                var next=FindNext(new HashSet<string>(history.UsedImages),new HashSet<string>(history.Combinations),new AssemblyMixTracker(),new Random(0),CancellationToken.None,0);
                result.Variants=next==null?0:1; result.Exact=true;
            }
            if (list != null && previous.Any(entry => !LedgerEntryResolved(entry, list)))
                result.Exact = false;
            result.BatchLimit=(int)BigInteger.Min(result.BatchLimit,result.Variants);
            return result;
        }
        static bool IsVisualSignature(string entry) {
            return !string.IsNullOrEmpty(entry) && entry.Length == 64 && entry.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        }
        bool LedgerEntryResolved(string entry, List<AssemblyPlan> list) {
            if (history.Records.Any(r => r.Signature == entry)) return DefaultIdentity || savedPlans.Any(p=>p.Signature==entry && CanProject(p));
            if (list.Any(p => p.Signature == entry)) return true;
            if (IsVisualSignature(entry) && history.Combinations.Contains(entry) && list.All(p => p.Signature != entry)) return DefaultIdentity;
            return false;
        }
        void SubtractHistory(AssemblyCapacity capacity) {
            var resolved=new HashSet<string>(); var blocked=new HashSet<string>();
            foreach (var p in savedPlans) {
                if (!Compatible(p)) continue;
                string raw=SignatureText(p), visual=VisualSignature(raw);
                if (!CombinationBlocked(raw) && !previous.Contains(p.Signature) && !previous.Contains(visual)) continue;
                blocked.Add(visual);
                resolved.Add(visual);
                if (previous.Contains(p.Signature)) resolved.Add(p.Signature);
                foreach (var song in music) {
                    if (string.IsNullOrEmpty(song.Hash)) continue;
                    string legacy=AssemblyFiles.HashText(raw+"|music:"+song.Hash);
                    if (previous.Contains(legacy)) resolved.Add(legacy);
                }
            }
            capacity.Variants=BigInteger.Max(0,capacity.Variants-blocked.Count);
            capacity.Exact=previous.All(resolved.Contains);
        }
        bool CanProject(AssemblyPlan plan) {
            if(plan.Scenes.Count!=template.Scenes.Count) return false;
            for(int i=0;i<plan.Scenes.Count;i++) if(template.Scenes[i].Layers.Count(l=>l.Enabled && pictures.ContainsKey(l) && l.InCombination)>0 && plan.Scenes[i].Layers.Count!=template.Scenes[i].Layers.Count(l=>l.Enabled && pictures.ContainsKey(l))) return false;
            if(template.GlobalLayers.Any(l=>l.Enabled && pictures.ContainsKey(l) && l.InCombination) && plan.GlobalLayers.Count!=template.GlobalLayers.Count(l=>l.Enabled && pictures.ContainsKey(l)))return false;
            // Older records lack split/global/audio details; use them only when the selected axes are recoverable.
            if(Identity.Video && Identity.SecondaryVideo && Split && plan.SecondaryVideo==null)return false;
            if(Identity.Music && history.Records.Any(r=>r.Signature==plan.Signature && r.IdentityPlan==null) && template.Scenes.Any(s=>sceneMusic[s].Count>0) && plan.Scenes.All(s=>string.IsNullOrEmpty(s.MusicHash)))return false;
            if(Identity.Voiceover && history.Records.Any(r=>r.Signature==plan.Signature && r.IdentityPlan==null) && string.IsNullOrEmpty(plan.VoiceoverHash) && string.IsNullOrEmpty(plan.Voiceover) && plan.Scenes.All(s=>string.IsNullOrEmpty(s.VoiceoverHash) && string.IsNullOrEmpty(s.Voiceover)))return false;
            return true;
        }
        bool Compatible(AssemblyPlan plan) {
            if (plan.Scenes.Count!=videos.Count) return false;
            if (backgrounds.Count == 0 ? plan.Background != null : plan.Background == null || !backgrounds.Any(a => a.Hash == plan.Background.Hash)) return false;
            var used=new HashSet<string>(); var images=new HashSet<string>();
            if(Split && (plan.SecondaryVideo==null || !secondary.Any(v => v.Hash==plan.SecondaryVideo.Hash) || plan.Scenes.Any(s => s.Video.Hash == plan.SecondaryVideo.Hash)) || !Split && plan.SecondaryVideo!=null) return false;
            if (plan.SplitSwapped && (!Split || template.SplitFixHalves)) return false;
            var globals=template.GlobalLayers.Where(l => l.Enabled && pictures.ContainsKey(l)).ToList();
            if(globals.Count!=plan.GlobalLayers.Count)return false;
            for(int j=0;j<globals.Count;j++) {var l=globals[j];string hash=plan.GlobalLayers[j].Asset.Hash;if(!pictures[l].Any(a => a.Hash==hash) || (l.Unique && !l.IsText) && !images.Add(hash))return false;}
            for (int i=0; i<videos.Count; i++) {
                var scene=plan.Scenes[i]; var eligible=template.ContinuousVideo && i>0 ? new List<AssemblyVideo>{plan.Scenes[0].Video} : videos[i].Where(v => !used.Contains(v.Hash)).ToList();
                if (eligible.Count==0 && template.AllowVideoReuse) eligible=videos[i];
                if (!eligible.Any(v => v.Hash==scene.Video.Hash)) return false;
                used.Add(scene.Video.Hash);
                var layers=template.Scenes[i].Layers.Where(l => l.Enabled && pictures.ContainsKey(l)).ToList();
                if (layers.Count!=scene.Layers.Count) return false;
                for (int j=0; j<layers.Count; j++) {
                    string hash=scene.Layers[j].Asset.Hash; var l=layers[j];
                    if (!pictures[l].Any(a => a.Hash==hash) || (l.Unique && !l.IsText) && !images.Add(hash)) return false;
                }
            }
            return true;
        }
    }
}
