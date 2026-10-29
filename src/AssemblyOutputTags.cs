using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class AssemblyOutputTags {
        public static readonly string[] DefaultTitlePhrases = {
            "pocket option ai trading", "pocket option ai trading bot", "pocket option signals", "ai trading bot",
            "pocket option trading bot", "pocket option bot", "ai trading pocket option", "pocket option",
            "best ai trading bot", "pocket option ai trading strategy", "ai trading bot pocket option", "ai trading",
            "automated trading strategy", "binary options", "pocket option trading", "pocket option trading strategy",
            "binary options trading", "binary options strategy", "pocket option ai", "pocket option strategy",
            "binary options bot", "ai bot trading pocket option", "binary options for beginners", "otc trading pocket option",
            "binary options trading robot", "pocket option ai bot", "ai trading bot for pocket option", "binary options pocket option",
            "pocket option for beginners", "tutorial trading pocket option", "pocket option ai trading bots",
            "free ai trading bot pocket option", "binary options signals"
        };

        public static List<string> ParseTitleInput(string text) {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();
            foreach (var part in parts) {
                string title = ToTitle(part);
                if (title.Length == 0) continue;
                if (title.Length > 100) throw new Exception("Заголовок длиннее 100 символов: " + title);
                if (title.Contains("<") || title.Contains(">")) throw new Exception("Заголовок не должен содержать < или >.");
                if (!result.Any(x => string.Equals(x, title, StringComparison.OrdinalIgnoreCase))) result.Add(title);
            }
            return result;
        }

        public static string ToTitle(string phrase) {
            string trimmed = (phrase ?? "").Trim();
            if (trimmed.Length == 0) return "";
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(trimmed.ToLower(CultureInfo.CurrentCulture));
        }

        public static List<string> NormalizeBank(IEnumerable<string> items) {
            return (items ?? Enumerable.Empty<string>()).Select(x => ToTitle(x)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string PickTitle(IList<string> bank, Random random, HashSet<string> usedInBatch) {
            if (bank == null || bank.Count == 0) return null;
            if (usedInBatch != null && usedInBatch.Count < bank.Count) {
                string title;
                int guard = 0;
                do { title = bank[random.Next(bank.Count)]; } while (usedInBatch.Contains(title) && ++guard < 64);
                usedInBatch.Add(title);
                return title;
            }
            return bank[random.Next(bank.Count)];
        }

        public static async Task<string[]> Apply(string ffmpeg, string ffprobe, string videoPath, string title, AssemblyOutputMetadata metadata, Random random, CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) throw new IOException("Не найден ролик для записи метаданных.");
            var meta=metadata ?? AssemblyOutputMetadata.Default();
            var sample=meta.Sample(random,DateTime.Now);
            bool hasTitle=!string.IsNullOrWhiteSpace(title);
            bool needsTags=hasTitle || sample!=null && (sample.HasCaptureDate || !string.IsNullOrEmpty(sample.Brand) || !string.IsNullOrWhiteSpace(sample.Software));
            if (!needsTags) return VideoMetadata.Report(await Core.Probe(ffprobe,videoPath,ct).ConfigureAwait(false));
            string temp=videoPath+".tags-"+Guid.NewGuid().ToString("N")+".mp4";
            var args=new List<string> {"-hide_banner","-nostdin","-y","-loglevel","error","-i",videoPath,"-map","0","-c","copy","-map_metadata","-1","-map_metadata:s","-1","-map_chapters","-1"};
            if (hasTitle) args.AddRange(new[] {"-metadata","title="+title,"-metadata","description="+title});
            VideoMetadata.Add(args,sample,true);
            args.AddRange(new[] {"-movflags","+faststart+use_metadata_tags",temp});
            try {
                await Core.Tool(ffmpeg,args,ct,null,600).ConfigureAwait(false);
                var probe=await Core.Probe(ffprobe,temp,ct).ConfigureAwait(false);
                VideoMetadata.Verify(probe,sample,title); ct.ThrowIfCancellationRequested();
                File.Replace(temp,videoPath,null);
                // Capture date lives in container metadata only; keep real filesystem times so Explorer sort-by-date shows new renders.
                return VideoMetadata.Report(probe);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
