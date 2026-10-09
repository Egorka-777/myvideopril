using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using VideoBatch;

class Program {
    sealed class Quiet : IProgress<Update> {
        public CancellationTokenSource CancelOnEncode;
        public void Report(Update update) { if (CancelOnEncode != null && update.Text.EndsWith("%") && update.Percent > 0 && update.Percent < 100) CancelOnEncode.Cancel(); }
    }
    static int Main(string[] args) {
        try {
            if (args[0] == "self-test") { bool ok = AssemblyRegressionTests.RunPlannerTests() && AssemblyRegressionTests.RunCapacityTests() && AssemblyRegressionTests.RunObjectsTests() && AssemblyRegressionTests.RunRoutingTests(); Console.WriteLine(ok); return ok ? 0 : 1; }
            var t = AssemblyFiles.Load<AssemblyTemplate>(args[1]);
            Action<AssemblyTemplate, AssemblyLayerPlan, string> raster = args.Length > 5 && args[5] == "portable" ? CopyRaster : AssemblyRaster.Save;
            if (args[0] == "benchmark" || args[0] == "fallback") {
                t.RenderMode = "cpu"; t.Validate(); Directory.CreateDirectory(args[4]);
                var videos = AssemblyEngine.ReadVideos(t, args[3], CancellationToken.None).GetAwaiter().GetResult();
                var batch = AssemblyPlanner.Create(t, videos, new AssemblyHistory(), new Random(42), 1);
                if (batch.Plans.Count != 1) throw new Exception(batch.Limit);
                var p = batch.Plans.Single();
                string root = args[4]; var context = new AssemblyRenderContext();
                if (args[0] == "fallback") {
                    t.RenderMode = "auto"; typeof(AssemblyRenderContext).GetField("Encoder", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(context, "hardware_missing_test_encoder");
                    AssemblyEngine.Render(t,p,args[2],args[3],Path.Combine(root,"fallback.mp4"),Path.Combine(root,"fallback-work"),raster,CancellationToken.None,null,context).GetAwaiter().GetResult();
                    Console.WriteLine(JsonSerializer.Serialize(context.LastReport, new JsonSerializerOptions { IncludeFields = true })); return 0;
                }
                var oldTimes = new double[3]; var newTimes = new double[3];
                for (int i=0; i<3; i++) {
                    // Alternate order to reduce warm-cache / CPU-temperature bias.
                    foreach (bool legacy in i % 2 == 0 ? new[] {true,false} : new[] {false,true}) {
                        string output = Path.Combine(root,legacy ? "reference.mp4" : "single-pass.mp4");
                        string work = Path.Combine(root, "work-" + legacy + i); var clock=Stopwatch.StartNew();
                        if (legacy) AssemblyEngine.RenderLegacy(t,p,args[2],args[3],output,work,raster,CancellationToken.None,null).GetAwaiter().GetResult();
                        else AssemblyEngine.Render(t,p,args[2],args[3],output,work,raster,CancellationToken.None,null,context).GetAwaiter().GetResult();
                        (legacy ? oldTimes : newTimes)[i]=clock.Elapsed.TotalSeconds;
                    }
                }
                Console.WriteLine(JsonSerializer.Serialize(new {LegacySeconds=oldTimes,SinglePassSeconds=newTimes,Report=context.LastReport},new JsonSerializerOptions {IncludeFields=true})); return 0;
            }
            using (var ct = new CancellationTokenSource()) {
                if (args.Length > 6) ct.CancelAfter(int.Parse(args[6]));
                if (args[0] == "cancel-encode") t.RenderMode = "cpu";
                var progress = new Quiet { CancelOnEncode = args[0] == "cancel-encode" ? ct : null };
                var result = AssemblyEngine.Run(t, args[2], args[3], args[4], raster, progress, ct.Token).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true }));
            }
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void CopyRaster(AssemblyTemplate t, AssemblyLayerPlan plan, string path) { File.Copy(plan.Asset.Path, path); }
}
