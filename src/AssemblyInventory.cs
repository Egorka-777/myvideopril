using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.IO;

namespace VideoBatch {
    public sealed class AssemblyCapacity {
        public BigInteger Variants;
        public bool Exact;
        public int BatchLimit;
        public string Caption { get { return (Exact ? "Доступно " : "До ") + Variants.ToString("N0") + " связок · за раз до " + BatchLimit + " роликов"; } }
    }
    // Content identity matters: copies and renamed files do not create new combinations.
    public sealed class AssemblyInventory {
        readonly AssemblyTemplate template;
        readonly AssemblyHistory history;
        readonly List<List<AssemblyVideo>> videos = new List<List<AssemblyVideo>>();
        readonly Dictionary<AssemblyLayer,List<AssemblyAsset>> pictures = new Dictionary<AssemblyLayer,List<AssemblyAsset>>();
        readonly List<AssemblyAsset> music;
        readonly HashSet<string> previous;
        const int EnumerationLimit = 50000;
        public AssemblyInventory(AssemblyTemplate t, IList<AssemblyVideo> source, AssemblyHistory h) {
            template = t; history = h; previous = new HashSet<string>(h.Combinations);
            var used = new HashSet<string>(h.UsedImages);
            for (int i = 0; i < t.Scenes.Count; i++) {
                var scene = t.Scenes[i]; double duration = RenderDuration(i);
                var allowed = new HashSet<string>(AssemblyFiles.Pool(t.Resolve(t.VideoSource(scene)),AssemblyFiles.VideoExtensions),StringComparer.OrdinalIgnoreCase);
                videos.Add(source.Where(v => allowed.Contains(v.Path) && (v.Duration + .001 >= duration || t.LoopShortVideos && v.Duration > 0)).GroupBy(v => v.Hash).Select(g => g.First()).ToList());
                foreach (var l in scene.Layers.Where(l => l.Enabled)) {
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
            music = Assets(t.Resolve(t.Music),AssemblyFiles.MusicExtensions);
            if (music.Count == 0) music.Add(new AssemblyAsset { Path = "", Hash = "" });
        }
        static List<AssemblyAsset> Assets(string path,string[] extensions) {
            return AssemblyFiles.Pool(path,extensions).Select(p => new AssemblyAsset { Path=p, Hash=AssemblyFiles.Hash(p) }).GroupBy(a => a.Hash).Select(g => g.First()).ToList();
        }
        public static AssemblyAsset FixedAsset(AssemblyLayer layer,List<AssemblyAsset> pool) {
            var asset = string.IsNullOrWhiteSpace(layer.FixedAssetHash) ? pool.FirstOrDefault() : pool.FirstOrDefault(a => a.Hash == layer.FixedAssetHash);
            if (asset == null) throw new Exception("Закреплённая картинка «" + layer.Name + "» отсутствует. Выбери новую в её пачке или включи «Меняется».");
            return asset;
        }
        double RenderDuration(int i) { return template.Scenes[i].Duration + (i < template.Scenes.Count-1 && template.Transition != "cut" ? template.TransitionDuration : 0); }
        BigInteger RawBound() {
            BigInteger total = music.Count;
            foreach (var v in videos) total *= v.Count;
            foreach (var pool in pictures.Values) total *= pool.Count;
            return total;
        }
        static string SignatureText(AssemblyPlan p) { return string.Join("|",p.Scenes.Select(s => s.Video.Hash + ":" + string.Join(",",s.Layers.Select(l => l.Asset.Hash)))); }
        void Sign(AssemblyPlan p,AssemblyAsset song) {
            string raw = SignatureText(p); p.Music = song.Path; p.MusicHash=song.Hash;
            p.Signature = AssemblyFiles.HashText(raw + (template.PackStudioVersion > 0 ? "|music:" + song.Hash : ""));
        }
        public List<AssemblyPlan> Enumerate() {
            if (RawBound() > EnumerationLimit) return null;
            var output = new List<AssemblyPlan>();
            WalkScenes(0,new AssemblyPlan(),new HashSet<string>(),new HashSet<string>(),output);
            return output;
        }
        void WalkScenes(int index,AssemblyPlan plan,HashSet<string> selectedVideo,HashSet<string> selectedImage,List<AssemblyPlan> output) {
            if (index == videos.Count) {
                foreach (var song in music) {
                    Sign(plan,song);
                    if (previous.Contains(plan.Signature) || template.PackStudioVersion > 0 && previous.Contains(AssemblyFiles.HashText(SignatureText(plan)))) continue;
                    var copy = new AssemblyPlan { Signature=plan.Signature,Music=plan.Music,MusicHash=plan.MusicHash,UniqueImages=new List<string>(plan.UniqueImages) };
                    copy.Scenes = plan.Scenes.Select(s => new AssemblyScenePlan { Scene=s.Scene,Video=s.Video,RenderDuration=s.RenderDuration,Layers=new List<AssemblyLayerPlan>(s.Layers) }).ToList();
                    output.Add(copy);
                }
                return;
            }
            var available = videos[index].Where(v => !selectedVideo.Contains(v.Hash)).ToList();
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
                if (l.Unique && selected.Contains(a.Hash)) continue;
                scene.Layers.Add(new AssemblyLayerPlan { Layer=l,Asset=a });
                if (l.Unique) { selected.Add(a.Hash); plan.UniqueImages.Add(a.Hash); }
                WalkLayers(i+1,layers,scene,plan,selected,next);
                scene.Layers.RemoveAt(scene.Layers.Count-1);
                if (l.Unique) { selected.Remove(a.Hash); plan.UniqueImages.RemoveAt(plan.UniqueImages.Count-1); }
            }
        }
        public AssemblyPlanBatch Take(List<AssemblyPlan> candidates,Random random,int count) {
            var batch = new AssemblyPlanBatch(); var used = new HashSet<string>(history.UsedImages);
            for (int i = candidates.Count-1; i > 0; i--) { int j=random.Next(i+1); var p=candidates[i]; candidates[i]=candidates[j]; candidates[j]=p; }
            int target = Math.Min(count,Math.Min(candidates.Count,UniqueLimit(used)));
            foreach (var p in candidates) {
                if (batch.Plans.Count >= target) break;
                if (p.UniqueImages.Any(used.Contains)) continue;
                var after = new HashSet<string>(used); after.UnionWith(p.UniqueImages);
                if (UniqueLimit(after) < target-batch.Plans.Count-1) continue;
                foreach (var scene in p.Scenes) scene.VideoStart = template.RandomVideoStart ? random.NextDouble()*Math.Max(0,scene.Video.Duration-scene.RenderDuration) : 0;
                batch.Plans.Add(p); used=after;
            }
            if (batch.Plans.Count < count) batch.Limit = pictures.Any(p => p.Key.Unique) ? "Закончились новые связки или материалы без повторов." : "Исчерпаны разные сочетания материалов.";
            return batch;
        }
        // Repeated bipartite matching accounts for shared/overlapping unique image packs.
        int UniqueLimit(HashSet<string> used) {
            var pools = pictures.Where(p => p.Key.Unique).Select(p => p.Value.Select(a => a.Hash).Where(h => !used.Contains(h)).Distinct().ToList()).ToList();
            if (pools.Count == 0) return 1000;
            if (pools.SelectMany(p => p).GroupBy(h => h).All(g => g.Count()==1)) return Math.Min(1000,pools.Min(p => p.Count));
            if (pools.All(p => new HashSet<string>(p).SetEquals(pools[0]))) return Math.Min(1000,pools[0].Count/pools.Count);
            int high = Math.Min(1000,Math.Min(pools.Min(p => p.Count),pools.SelectMany(p => p).Distinct().Count()/pools.Count)), low=0;
            while (low < high) { int mid=(low+high+1)/2; if (CanMatch(pools,mid)) low=mid; else high=mid-1; }
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
            var list = Enumerate(); var result = new AssemblyCapacity { Exact=list != null,Variants=list == null ? RawBound() : list.Count,BatchLimit=UniqueLimit(new HashSet<string>(history.UsedImages)) };
            if (list == null) {
                // The usual studio uses a common video pool. Count its ordered choices exactly.
                bool same = videos.All(v => new HashSet<string>(v.Select(a => a.Hash)).SetEquals(videos[0].Select(a => a.Hash)));
                bool noSharedUnique = pictures.Where(p => p.Key.Unique).SelectMany(p => p.Value.Select(a => a.Hash)).GroupBy(h => h).All(g => g.Count()==1);
                if (same && noSharedUnique && (template.AllowVideoReuse || videos[0].Count >= videos.Count)) {
                    int n=videos[0].Count; BigInteger count=music.Count;
                    for (int i=0; i<videos.Count; i++) count *= i<n ? n-i : n;
                    foreach (var pool in pictures.Values) count *= pool.Count;
                    result.Variants=count; result.Exact=true;
                    SubtractHistory(result);
                }
            }
            result.BatchLimit=(int)BigInteger.Min(result.BatchLimit,result.Variants);
            return result;
        }
        void SubtractHistory(AssemblyCapacity capacity) {
            var resolved=new HashSet<string>(); var blocked=new HashSet<string>();
            foreach (string output in history.Outputs) {
                string file=output+".assembly.xml"; if (!File.Exists(file)) continue;
                try {
                    var p=AssemblyFiles.Load<AssemblyPlan>(file);
                    if (!previous.Contains(p.Signature)) continue;
                    resolved.Add(p.Signature);
                    if (!Compatible(p)) continue;
                    string raw=SignatureText(p), legacy=AssemblyFiles.HashText(raw);
                    foreach (var song in music) {
                        string signature=AssemblyFiles.HashText(raw+(template.PackStudioVersion>0 ? "|music:"+song.Hash : ""));
                        if (previous.Contains(signature) || template.PackStudioVersion>0 && previous.Contains(legacy)) blocked.Add(signature);
                    }
                    if (previous.Contains(legacy)) resolved.Add(legacy);
                } catch { /* Unreadable older recipes keep the display explicitly bounded. */ }
            }
            capacity.Variants=BigInteger.Max(0,capacity.Variants-blocked.Count);
            capacity.Exact=previous.All(resolved.Contains);
        }
        bool Compatible(AssemblyPlan plan) {
            if (plan.Scenes.Count!=videos.Count) return false;
            var used=new HashSet<string>(); var images=new HashSet<string>();
            for (int i=0; i<videos.Count; i++) {
                var scene=plan.Scenes[i]; var eligible=videos[i].Where(v => !used.Contains(v.Hash)).ToList();
                if (eligible.Count==0 && template.AllowVideoReuse) eligible=videos[i];
                if (!eligible.Any(v => v.Hash==scene.Video.Hash)) return false;
                used.Add(scene.Video.Hash);
                var layers=template.Scenes[i].Layers.Where(l => l.Enabled && pictures.ContainsKey(l)).ToList();
                if (layers.Count!=scene.Layers.Count) return false;
                for (int j=0; j<layers.Count; j++) {
                    string hash=scene.Layers[j].Asset.Hash; var l=layers[j];
                    if (!pictures[l].Any(a => a.Hash==hash) || l.Unique && !images.Add(hash)) return false;
                }
            }
            return true;
        }
    }
}
