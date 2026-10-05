using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using VideoBatch;

class Program {
    sealed class QuietProgress : IProgress<Update> { public void Report(Update update) { } }
    static int Main(string[] args) {
        try {
            if (args[0] == "self-test") {
                bool ok = ProcessingRegressionTests.RunSelfTests();
                Console.WriteLine("ProcessingRegressionTests: " + ok); return ok ? 0 : 1;
            }
            BatchJob job;
            using (var reader = XmlReader.Create(args[1], new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                job = (BatchJob)new XmlSerializer(typeof(BatchJob)).Deserialize(reader);
            Store.Root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1])), "test-settings");
            using (var cancel = new CancellationTokenSource()) {
                if (args.Length > 2) cancel.CancelAfter(int.Parse(args[2]));
                var result = Core.Run(job, new QuietProgress(), cancel.Token).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { IncludeFields = true }));
            }
            return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
