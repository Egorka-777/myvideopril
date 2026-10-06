using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using VideoBatch;

class Program {
    sealed class Quiet : IProgress<Update> { public void Report(Update update) { } }
    static int Main(string[] args) {
        try {
            if (args[0] == "self-test") { bool ok = AssemblyRegressionTests.RunPlannerTests(); Console.WriteLine(ok); return ok ? 0 : 1; }
            var t = AssemblyFiles.Load<AssemblyTemplate>(args[1]);
            Action<AssemblyTemplate, AssemblyLayerPlan, string> raster = args.Length > 5 && args[5] == "portable" ? CopyRaster : AssemblyRaster.Save;
            using (var ct = new CancellationTokenSource()) {
                if (args.Length > 6) ct.CancelAfter(int.Parse(args[6]));
                var result = AssemblyEngine.Run(t, args[2], args[3], args[4], raster, new Quiet(), ct.Token).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true }));
            }
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void CopyRaster(AssemblyTemplate t, AssemblyLayerPlan plan, string path) { File.Copy(plan.Asset.Path, path); }
}
