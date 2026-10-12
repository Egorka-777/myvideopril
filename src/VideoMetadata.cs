using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VideoBatch {
    public static class VideoMetadata {
        public static string LocalStamp(DateTime date) {
            if (date.Kind==DateTimeKind.Unspecified) date=DateTime.SpecifyKind(date,DateTimeKind.Local);
            return new DateTimeOffset(date).ToString("yyyy-MM-ddTHH:mm:sszzz",CultureInfo.InvariantCulture);
        }
        public static void Add(List<string> args, ProcessingSample sample, bool audio) {
            if (sample==null) return;
            if (sample.HasCaptureDate) {
                string stamp=sample.CaptureDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",Core.Inv);
                args.AddRange(new[] {"-metadata","creation_time="+stamp,"-metadata:s:v:0","creation_time="+stamp});
                if (audio) args.AddRange(new[] {"-metadata:s:a:0","creation_time="+stamp});
                args.AddRange(new[] {"-metadata","creationdate="+LocalStamp(sample.CaptureDate)});
            }
            if (!string.IsNullOrEmpty(sample.Brand)) {
                args.AddRange(new[] {"-metadata","make="+sample.Brand,"-metadata","model="+sample.Model});
                if (sample.Brand=="Apple") {
                    args.AddRange(new[] {"-metadata","com.apple.quicktime.make="+sample.Brand,"-metadata","com.apple.quicktime.model="+sample.Model});
                    if (sample.HasCaptureDate) args.AddRange(new[] {"-metadata","com.apple.quicktime.creationdate="+LocalStamp(sample.CaptureDate)});
                } else args.AddRange(new[] {"-metadata","com.android.manufacturer="+sample.Brand,"-metadata","com.android.model="+sample.Model});
            }
            if (!string.IsNullOrWhiteSpace(sample.Software)) {
                args.AddRange(new[] {"-metadata","software="+sample.Software});
                if (sample.Brand=="Apple") args.AddRange(new[] {"-metadata","com.apple.quicktime.software="+sample.Software});
            }
        }
        public static void Verify(ProbeResult probe, ProcessingSample sample, string title, bool trackDates = true) {
            var tags=new Dictionary<string,string>(probe.format?.tags ?? new Dictionary<string,string>(),StringComparer.OrdinalIgnoreCase);
            Action<string,string> equal=(key,value) => {
                string actual;
                if (!tags.TryGetValue(key,out actual) || actual!=value) throw new Exception("Метаданные не сохранились в видео: "+key);
            };
            if (!string.IsNullOrWhiteSpace(title)) {equal("title",title); equal("description",title);}
            if (sample==null) return;
            if (!string.IsNullOrEmpty(sample.Brand)) {
                equal("make",sample.Brand); equal("model",sample.Model);
                equal(sample.Brand=="Apple" ? "com.apple.quicktime.make" : "com.android.manufacturer",sample.Brand);
                equal(sample.Brand=="Apple" ? "com.apple.quicktime.model" : "com.android.model",sample.Model);
            }
            if (!string.IsNullOrWhiteSpace(sample.Software)) equal("software",sample.Software);
            if (sample.HasCaptureDate) {
                equal("creationdate",LocalStamp(sample.CaptureDate));
                if (sample.Brand=="Apple") equal("com.apple.quicktime.creationdate",LocalStamp(sample.CaptureDate));
                var utc=new DateTimeOffset(sample.CaptureDate.ToUniversalTime());
                var containers=new List<Dictionary<string,string>> {tags};
                if (trackDates) containers.AddRange((probe.streams ?? new ProbeStream[0]).Where(s => s.codec_type=="video" || s.codec_type=="audio").Select(s => new Dictionary<string,string>(s.tags ?? new Dictionary<string,string>(),StringComparer.OrdinalIgnoreCase)));
                foreach (var container in containers) {
                    string stamp;
                    if (!container.TryGetValue("creation_time",out stamp)) throw new Exception("Дата не сохранилась в видео или аудиодорожке.");
                    foreach (string part in stamp.Split(';')) {
                        DateTimeOffset actual;
                        if (!DateTimeOffset.TryParse(part,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out actual) || Math.Abs((actual-utc).TotalSeconds)>1)
                            throw new Exception("Дата в готовом видео не совпадает с выбранной.");
                    }
                }
            }
        }
        public static string[] Report(ProbeResult probe) {
            var lines=new List<string> {"Метаданные прочитаны из готового видео; этот отчёт не нужен для публикации."};
            foreach (var tag in (probe.format?.tags ?? new Dictionary<string,string>()).OrderBy(p => p.Key)) lines.Add(tag.Key+"="+tag.Value);
            lines.Add("format_duration="+probe.format?.duration); lines.Add("format_bit_rate="+probe.format?.bit_rate);
            foreach (var stream in probe.streams ?? new ProbeStream[0]) {
                string prefix=stream.codec_type+"["+stream.index+"].";
                lines.Add(prefix+"codec="+stream.codec_name); lines.Add(prefix+"duration="+stream.duration); lines.Add(prefix+"bit_rate="+stream.bit_rate);
                if (stream.codec_type=="video") { lines.Add(prefix+"size="+stream.width+"x"+stream.height); lines.Add(prefix+"fps="+stream.avg_frame_rate); }
            }
            return lines.ToArray();
        }
    }
}
