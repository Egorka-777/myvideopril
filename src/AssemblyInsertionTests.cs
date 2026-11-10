using System;
using System.Collections.Generic;
using System.IO;

namespace VideoBatch {
    public static class AssemblyInsertionTests {
        public static bool RunSelfTests() {
            var template = AssemblyTemplate.Defaults();
            template.Materials = Path.GetTempPath();
            AssemblyMarketProfiles.EnsureSeeded(template);
            template.Insertions.Add(new AssemblyInsertion { Source = "ad.mp4", Start = 2, End = 4, RandomTime = true, ImageDuration = 3 });
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
                if (insertion.Source != "ad.mp4" || !insertion.RandomTime || insertion.Start != 2 || insertion.End != 4 || insertion.ImageDuration != 3)
                    throw new Exception("Advertising settings did not round trip through XML.");
                var custom = AssemblyMarketProfiles.AddEmpty(restored, "Empty");
                AssemblyMarketProfiles.Apply(restored, custom.Id);
                if (restored.Insertions.Count != 0) throw new Exception("Empty profile inherited advertising pauses.");
            } finally { if (File.Exists(file)) File.Delete(file); }
            return true;
        }
    }
}
