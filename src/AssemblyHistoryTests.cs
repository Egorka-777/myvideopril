using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace VideoBatch {
    public static class AssemblyHistoryTests {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        public static bool RunSelfTests() {
            string root=Path.Combine(Path.GetTempPath(),"assembly-history-"+Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(root); string folder=Path.Combine(root,"history");
                var t=AssemblyTemplate.Defaults(); t.Materials=Path.Combine(root,"materials"); AssemblyWorkspace.Prepare(t);
                foreach (var scene in t.Scenes) scene.Layers.Clear();
                var videos=new List<AssemblyVideo>();
                for (int i=0;i<12;i++) {
                    string file=t.Resolve("Видео/"+i+".mp4"); File.WriteAllText(file,"video-"+i);
                    videos.Add(new AssemblyVideo {Path=file,Hash=AssemblyFiles.Hash(file),Duration=10});
                }
                for (int i=0;i<4;i++) File.WriteAllText(t.Resolve("Музыка/"+i+".wav"),"music-"+i);
                // 12*11*10*9*4 = 47,520; raw bound exceeds enumeration limit.
                var initial=new AssemblyInventory(t,videos,new AssemblyHistory()).Calculate();
                Check(initial.Exact && initial.Variants==47520,"large capacity");
                var plan=AssemblyPlanner.Create(t,videos,new AssemblyHistory(),new Random(3),1).Plans.Single();
                string output=Path.Combine(root,"published.mp4"); var h=AssemblyHistoryStore.Load(folder);
                string staged=Path.Combine(root,"private.mp4"); File.WriteAllText(staged,"completed private output");
                var record=AssemblyHistoryStore.Reserve(folder,h,plan,output,t.Id,staged);
                Check(!AssemblyHistoryStore.Load(folder).Combinations.Contains(plan.Signature),"unpublished crash must release");
                File.Move(staged,output);
                // Crash after rename, then an external move/delete before the next launch.
                File.Delete(output);
                var recovered=AssemblyHistoryStore.Load(folder);
                Check(recovered.Combinations.Contains(plan.Signature) && recovered.Records.All(r => !r.Pending),"published crash must commit");
                AssemblyHistoryStore.Save(folder,recovered); File.Delete(output);
                // Deleting video, changing studio ID and changing filenames cannot release a used tuple.
                t.Id="a-new-studio"; string renamed=t.Resolve("Видео/renamed.mp4");
                File.Move(videos[0].Path,renamed); videos[0].Path=renamed;
                recovered=AssemblyHistoryStore.Load(folder);
                var remaining=new AssemblyInventory(t,videos,recovered).Calculate();
                Check(remaining.Exact && remaining.Variants==47519,"count depends on durable record, not output recipe");
                Check(AssemblyPlanner.Create(t,videos,recovered,new Random(3),1).Plans.Single().Signature!=plan.Signature,"repeated seed must not repeat");
                // Old opaque signatures migrate intact even when their external recipe has disappeared.
                var old=new AssemblyHistory(); old.Combinations.Add("opaque-old-signature"); old.UsedImages.Add("old-image");
                AssemblyFiles.Save(Path.Combine(folder,"old-studio.xml"),old);
                recovered=AssemblyHistoryStore.Load(folder);
                Check(recovered.Combinations.Contains("opaque-old-signature") && recovered.UsedImages.Contains("old-image"),"legacy migration");
                var conservative=new AssemblyInventory(t,videos,recovered).Calculate();
                Check(!conservative.Exact && conservative.Variants==47519,"unknown old recipe requires an explicit upper bound");
                // The global gate serializes different studios, not just the same template ID.
                using (var gate=new FileStream(AssemblyHistoryStore.PathFor(folder)+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                    try { using(var other=new FileStream(AssemblyHistoryStore.PathFor(folder)+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) { } throw new Exception("gate allowed a second writer"); }
                    catch(IOException) { }
                }
                string ledger=AssemblyHistoryStore.PathFor(folder), exact=File.ReadAllText(ledger);
                File.WriteAllText(ledger,"broken XML");
                try { AssemblyHistoryStore.Load(folder); throw new Exception("corrupt history silently reset"); } catch(System.InvalidOperationException) { }
                Check(File.ReadAllText(ledger)=="broken XML","corrupt journal must not be overwritten");
                File.WriteAllText(ledger,exact); File.Delete(ledger);
                try { AssemblyHistoryStore.Load(folder); throw new Exception("stale backup silently restored"); } catch(IOException) { }
                return true;
            } finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
        }
    }
}
