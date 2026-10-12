using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoBatch {
    // Stored independently of MP4s and diagnostic recipes. Identity is the ordered
    // material hashes + music, never a filename, title, timestamp or device label.
    public class AssemblyHistoryScene {
        public string VideoHash;
        public List<string> Images = new List<string>();
    }
    public class AssemblyHistoryRecord {
        public string Signature, MusicHash, Output, TemplateId, StagedPath;
        public bool Pending;
        public List<string> UniqueImages = new List<string>();
        public List<AssemblyHistoryScene> Scenes = new List<AssemblyHistoryScene>();
        public static AssemblyHistoryRecord FromPlan(AssemblyPlan plan, string output, string templateId) {
            return new AssemblyHistoryRecord {
                Signature=plan.Signature, MusicHash=plan.MusicHash, Output=output, TemplateId=templateId,
                UniqueImages=new List<string>(plan.UniqueImages),
                Scenes=plan.Scenes.Select(s => new AssemblyHistoryScene { VideoHash=s.Video.Hash, Images=s.Layers.Select(l => l.Asset.Hash).ToList() }).ToList()
            };
        }
        public AssemblyPlan ToPlan() {
            return new AssemblyPlan { Signature=Signature, MusicHash=MusicHash,
                Scenes=Scenes.Select(s => new AssemblyScenePlan { Video=new AssemblyVideo { Hash=s.VideoHash },
                    Layers=s.Images.Select(h => new AssemblyLayerPlan { Asset=new AssemblyAsset { Hash=h } }).ToList() }).ToList() };
        }
    }
    public static class AssemblyOutputFiles {
        public static string Folder(string output) { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), ".videobatch-reports"); }
        public static string Report(string output, string suffix) { return Path.Combine(Folder(output), Path.GetFileName(output)+suffix); }
        public static string FindRecipe(string output) {
            string current=Report(output,".assembly.xml");
            return File.Exists(current) ? current : output+".assembly.xml";
        }
        public static void Prepare(string output) {
            string folder=Folder(output); Directory.CreateDirectory(folder);
            if (Environment.OSVersion.Platform==PlatformID.Win32NT) File.SetAttributes(folder,File.GetAttributes(folder)|FileAttributes.Hidden);
        }
        public static void DeleteReports(string output) {
            foreach (string suffix in new[] {".assembly.xml",".performance.xml",".metadata.txt",".title.txt"}) {
                string path=Report(output,suffix); if (File.Exists(path)) File.Delete(path);
            }
        }
    }
    public static class AssemblyHistoryStore {
        public static string PathFor(string folder) { return Path.Combine(folder,"all-studios.xml"); }
        public static AssemblyHistory Load(string folder) {
            Directory.CreateDirectory(folder); string path=PathFor(folder);
            // A stale backup cannot establish whether the last published video was used.
            if (!File.Exists(path) && File.Exists(path+".bak")) throw new IOException("Журнал связок отсутствует, но есть резервная копия. Восстанови историю перед сборкой; повторы не разрешены.");
            var history=File.Exists(path) ? AssemblyFiles.Load<AssemblyHistory>(path) : new AssemblyHistory();
            if (File.Exists(path) && history.LedgerVersion!=1) throw new IOException("Неизвестная версия журнала связок. Сборка остановлена, чтобы не допустить повторов.");
            Validate(history);
            // Migration is a union: preserve opaque old signatures even without recipes.
            foreach (string backup in Directory.GetFiles(folder,"*.xml.bak"))
                if (!File.Exists(backup.Substring(0,backup.Length-4))) throw new IOException("Отсутствует файл старой истории: "+System.IO.Path.GetFileName(backup)+". Восстанови его перед сборкой.");
            foreach (string legacy in Directory.GetFiles(folder,"*.xml").Where(p => !p.Equals(path,StringComparison.OrdinalIgnoreCase))) {
                var old=AssemblyFiles.Load<AssemblyHistory>(legacy); Validate(old);
                history.Combinations.AddRange(old.Combinations); history.UsedImages.AddRange(old.UsedImages); history.Outputs.AddRange(old.Outputs);
                history.Records.AddRange(old.Records);
                foreach (string output in old.Outputs) {
                    string recipe=AssemblyOutputFiles.FindRecipe(output); if (!File.Exists(recipe)) continue;
                    try {
                        var plan=AssemblyFiles.Load<AssemblyPlan>(recipe);
                        if (old.Combinations.Contains(plan.Signature) && !history.Records.Any(r => r.Signature==plan.Signature))
                            history.Records.Add(AssemblyHistoryRecord.FromPlan(plan,output,System.IO.Path.GetFileNameWithoutExtension(legacy)));
                    } catch { /* Signature stays blocked; only the exact capacity may be unavailable. */ }
                }
            }
            // The private staged file survives a crash before the final rename. After
            // rename it is absent, even if someone has since moved/deleted the result.
            foreach (var record in history.Records.Where(r => r.Pending).ToList()) {
                if (!File.Exists(record.Output) && !string.IsNullOrEmpty(record.StagedPath) && File.Exists(record.StagedPath)) history.Records.Remove(record);
                else { record.Pending=false; Consume(history,record); } // uncertain = block, never silently reuse
            }
            history.Combinations=history.Combinations.Distinct().ToList(); history.UsedImages=history.UsedImages.Distinct().ToList();
            history.Outputs=history.Outputs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            history.Records=history.Records.GroupBy(r => r.Signature).Select(g => g.First()).ToList(); history.LedgerVersion=1;
            return history;
        }
        static void Validate(AssemblyHistory history) {
            if (history==null || history.Combinations==null || history.UsedImages==null || history.Outputs==null || history.Records==null ||
                history.Records.Any(r => r==null || string.IsNullOrWhiteSpace(r.Signature) || string.IsNullOrWhiteSpace(r.Output) || r.Scenes==null || r.UniqueImages==null || r.Scenes.Any(s => s==null || s.Images==null || string.IsNullOrWhiteSpace(s.VideoHash))))
                throw new IOException("Повреждена история связок. Сборка остановлена, чтобы не допустить повторов.");
        }
        public static void Save(string folder, AssemblyHistory history) { Validate(history); history.LedgerVersion=1; AssemblyFiles.Save(PathFor(folder),history); }
        public static AssemblyHistoryRecord Reserve(string folder, AssemblyHistory history, AssemblyPlan plan, string output, string templateId, string stagedPath) {
            if (history.Combinations.Contains(plan.Signature) || history.Records.Any(r => r.Signature==plan.Signature)) throw new IOException("Эта связка уже использована. Повторная сборка запрещена.");
            var record=AssemblyHistoryRecord.FromPlan(plan,output,templateId); record.Pending=true; record.StagedPath=stagedPath; history.Records.Add(record);
            try { Save(folder,history); } catch { history.Records.Remove(record); throw; }
            return record;
        }
        static void Consume(AssemblyHistory history, AssemblyHistoryRecord record) {
            if (!history.Combinations.Contains(record.Signature)) history.Combinations.Add(record.Signature);
            foreach (string hash in record.UniqueImages) if (!history.UsedImages.Contains(hash)) history.UsedImages.Add(hash);
            if (!history.Outputs.Contains(record.Output)) history.Outputs.Add(record.Output);
        }
        public static void Commit(string folder, AssemblyHistory history, AssemblyHistoryRecord record) { record.Pending=false; Consume(history,record); Save(folder,history); }
        public static void Release(string folder, AssemblyHistory history, AssemblyHistoryRecord record) { history.Records.Remove(record); Save(folder,history); }
    }
}
