using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace VideoBatch {
    public static class AssemblyDifferenceTests {
        /// <summary>Real ffmpeg/ffprobe regression. Only creates a unique child of root, removed in finally; never deploys.</summary>
        public static bool Run(string ffmpeg, string probe, string root) {
            return RunAsync(ffmpeg, probe, root).GetAwaiter().GetResult();
        }
        static async Task<bool> RunAsync(string ffmpeg, string probe, string root) {
            string folder = Path.Combine(root, "difference-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                var ct = CancellationToken.None;
                string source = Path.Combine(folder, "source.mp4"), tagged = Path.Combine(folder, "tagged.mp4"), different = Path.Combine(folder, "different.mp4"),
                    edit = Path.Combine(folder, "shift-crop.mp4"), repeat = Path.Combine(folder, "repeat.mp4"), silent = Path.Combine(folder, "silent.mp4");
                await Generate(ffmpeg, source, "testsrc2=size=160x120:rate=12:duration=8", "sine=frequency=440:sample_rate=16000:duration=8", ct);
                await Generate(ffmpeg, different, "color=c=white:size=160x120:rate=12:duration=8", "sine=frequency=1500:sample_rate=16000:duration=8", ct);
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", source, "-vf", "scale=128:96", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-c:a", "aac", "-metadata", "title=Changed tags only plus reencode", tagged }, ct);
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-ss", "2", "-i", source, "-t", "4", "-vf", "crop=140:104,scale=160:120", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", edit }, ct);
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-stream_loop", "1", "-i", edit, "-c", "copy", repeat }, ct);
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", source, "-c:v", "copy", "-an", silent }, ct);
                string metadataOnly = Path.Combine(folder, "metadata-only.mp4");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", source, "-c", "copy", "-metadata", "title=Metadata criterion test", "-metadata", "comment=Different tags", metadataOnly }, ct);
                File.SetLastWriteTimeUtc(metadataOnly, File.GetLastWriteTimeUtc(source));
                var metadataReport = await Analyze(ffmpeg, probe, metadataOnly, source, folder);
                Check(metadataReport.Completed && metadataReport.DifferencePercent > 0 && metadataReport.DifferencePercent < 3.5 &&
                    metadataReport.Criteria.First(x => x.Key == "metadata").EarnedPoints > 0 && metadataReport.Comparisons[0].VisualSimilarity > 99.9 &&
                    metadataReport.Comparisons[0].AudioSimilarity > 99.9, "Metadata-only changes must earn small explicit points without content novelty");
                var identical = await Analyze(ffmpeg, probe, source, source, folder);
                var reencoded = await Analyze(ffmpeg, probe, tagged, source, folder);
                var changed = await Analyze(ffmpeg, probe, different, source, folder);
                var shifted = await Analyze(ffmpeg, probe, edit, source, folder);
                var repeated = await Analyze(ffmpeg, probe, repeat, edit, folder);
                var noAudio = await Analyze(ffmpeg, probe, silent, silent, folder);
                var removedAudio = await Analyze(ffmpeg, probe, silent, source, folder);
                Check(identical.Completed && identical.DifferencePercent < .1, "Identical content was not recognized");
                Check(reencoded.Completed && reencoded.DifferencePercent < 15, "Reencoding/tags inflated content difference");
                Check(changed.Completed && changed.DifferencePercent > 60 && changed.Comparisons[0].VisualSimilarity < 30 && changed.Comparisons[0].AudioSimilarity < 30, "Distinct visual/audio content was not distinguished");
                Check(shifted.Completed && shifted.Comparisons[0].VisualSimilarity > 65 && shifted.Comparisons[0].AudioSimilarity > 90, "Shifted cropped edit not matched");
                Check(shifted.Comparisons[0].Fragments.Any(x => x.Kind == "video" && x.ReferenceStart >= 1.5), "Time offset not exposed");
                Check(repeated.Completed && repeated.DifferencePercent < 10 && repeated.Comparisons[0].Fragments.Count(x => x.Kind == "video") >= 2, "Repeated fragments not exposed");
                Check(noAudio.Completed && noAudio.Comparisons[0].AudioSimilarity == null && noAudio.DifferencePercent < .1, "Absent audio must be explicitly inapplicable");
                Check(removedAudio.Completed && removedAudio.Comparisons[0].AudioSimilarity == 0 && removedAudio.DifferencePercent >= 30, "Removed audio was ignored");
                var missing = await Analyze(ffmpeg, probe, source, Path.Combine(folder, "missing.mp4"), folder);
                Check(!missing.Completed && missing.DifferencePercent == null && missing.Comparisons[0].Status == "failed" && missing.Comparisons[0].Error.Length > 0, "Missing comparison reported success");
                var empty = await AssemblyDifferenceAnalysis.Analyze(ffmpeg, probe, source, new string[0], new string[0], folder, ct);
                Check(!empty.Completed && empty.DifferencePercent == null, "Empty comparison reported success");
                using (var cancellation = new CancellationTokenSource()) {
                    cancellation.Cancel();
                    var cancelled = await AssemblyDifferenceAnalysis.Analyze(ffmpeg, probe, source, new[] { source }, null, folder, cancellation.Token);
                    Check(!cancelled.Completed && cancelled.Status == "cancelled" && cancelled.DifferencePercent == null, "Cancellation reported success");
                }
                // Two known pieces in a reordered montage: union-of-samples must not average unrelated references into novelty.
                string montage = Path.Combine(folder, "montage.mp4");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", different, "-i", source, "-filter_complex", "[0:v][0:a][1:v][1:a]concat=n=2:v=1:a=1[v][a]", "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", montage }, ct);
                var union = await AssemblyDifferenceAnalysis.Analyze(ffmpeg, probe, montage, new[] { source, different }, new[] { tagged, edit, repeat, silent }, folder, ct);
                Check(union.Completed && union.Comparisons.Count == 6 && union.DifferencePercent < 10, "Known multi-source montage or previous output enumeration failed");
                var savedPlan = new AssemblyPlan { Scenes = new System.Collections.Generic.List<AssemblyScenePlan> {
                    new AssemblyScenePlan { Video = new AssemblyVideo { Path = source } } } };
                AssemblyFiles.Save(AssemblyOutputFiles.Report(montage, ".assembly.xml"), savedPlan);
                Check(!File.Exists(AssemblyOutputFiles.Report(montage, ".difference.xml")), "Analysis should not exist before explicit request");
                var requested = await AssemblyDifferenceAnalysis.AnalyzeSaved(ffmpeg, probe, montage, ct);
                Check(requested.Completed && File.Exists(AssemblyOutputFiles.Report(montage, ".difference.xml")) && requested.Comparisons.Any(c => c.File == source), "On-demand analysis and report persistence");
                string longFile = Path.Combine(folder, "two-minutes.mp4");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-stream_loop", "14", "-i", source, "-t", "120", "-c", "copy", longFile }, ct);
                string longCopy = Path.Combine(folder, "two-minutes-copy.mp4"); File.Copy(longFile, longCopy); File.SetLastWriteTimeUtc(longCopy, File.GetLastWriteTimeUtc(longFile));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var longReport = await Analyze(ffmpeg, probe, longFile, longCopy, folder);
                Check(longReport.Completed && longReport.DifferencePercent < .1 && longReport.OutputMedia.VisualSamples >= 239, "Two-minute media analysis failed");
                Console.WriteLine("Two-minute real-media self-comparison: " + watch.Elapsed.TotalSeconds.ToString("0.00", Core.Inv) + " seconds; " + longReport.OutputMedia.VisualSamples + " visual samples");
                var serializer = new XmlSerializer(typeof(AssemblyDifferenceReport));
                string reportFile = Path.Combine(folder, "report.difference.xml");
                using (var file = File.Create(reportFile)) serializer.Serialize(file, union);
                AssemblyDifferenceReport copy;
                using (var file = File.OpenRead(reportFile)) copy = (AssemblyDifferenceReport)serializer.Deserialize(file);
                Check(copy.Completed && copy.Comparisons.Count == union.Comparisons.Count && AssemblyDifferenceAnalysis.Format(copy).Contains("МЕТОДИКА") &&
                    copy.Criteria.Count == 5 && copy.Criteria.Sum(x => x.Weight) == 100 && Math.Abs(copy.Criteria.Sum(x => x.EarnedPoints ?? 0) - copy.DifferencePercent.Value) < .0001 &&
                    copy.Comparisons.All(x => Math.Abs(x.Criteria.Sum(c => c.EarnedPoints ?? 0) - x.DifferencePercent.Value) < .0001), "XML/weighted points roundtrip failed");
                Check(noAudio.Criteria.First(x => x.Key == "audio").EarnedPoints == null, "Unavailable audio awarded points");
                foreach (string key in new[] { "duration", "technical", "metadata" }) Check(Math.Abs(union.Criteria.First(x => x.Key == key).RawDifferencePercent.Value -
                    union.Comparisons.Min(x => x.Criteria.First(c => c.Key == key).RawDifferencePercent.Value)) < .0001, "Non-content criterion did not select nearest reference");
                Console.WriteLine("Metadata-only weighted difference: " + metadataReport.DifferencePercent);
                Console.WriteLine("Difference tests: identical={0}, reencoded={1}, different={2}, shifted/cropped={3}, repeated={4}, montage={5}",
                    identical.DifferencePercent, reencoded.DifferencePercent, changed.DifferencePercent, shifted.DifferencePercent, repeated.DifferencePercent, union.DifferencePercent);
                return true;
            } finally { Directory.Delete(folder, true); }
        }
        static Task Generate(string ffmpeg, string output, string visual, string audio, CancellationToken ct) {
            return Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", visual, "-f", "lavfi", "-i", audio,
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", output }, ct);
        }
        static Task<AssemblyDifferenceReport> Analyze(string ffmpeg, string probe, string output, string source, string work) {
            return AssemblyDifferenceAnalysis.Analyze(ffmpeg, probe, output, new[] { source }, null, work, CancellationToken.None);
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception("Difference regression: " + message); }
    }
}
