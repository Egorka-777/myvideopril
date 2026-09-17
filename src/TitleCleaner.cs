using System;
using System.IO;
using System.Text.RegularExpressions;

namespace VideoBatch {
    /// <summary>Единая очистка заголовка для YouTube перед UploadJob (не для UI и не для базы).</summary>
    public static class TitleCleaner {
        public const int MaxLength=100;

        static readonly Regex LeadingNumber=new Regex(
            @"^\s*(?:\d{1,3}[\.\)\-_]\s*|\d{1,3}\s+)",
            RegexOptions.Compiled);
        static readonly Regex PackSuffix=new Regex(@"(\s*[·•]\s*\+\d+\s*|\s+\+\d+\s*)$",RegexOptions.Compiled);
        static readonly Regex PipeNorm=new Regex(@"\s*\|\s*",RegexOptions.Compiled);
        static readonly Regex DotSeparator=new Regex(@"(?<!\d)\s+\.\s+(?!\d)",RegexOptions.Compiled);
        static readonly Regex EllipsisSep=new Regex(@"\s*\.\.\.\s*",RegexOptions.Compiled);
        static readonly Regex MultiPipe=new Regex(@"\|\s*\|+",RegexOptions.Compiled);
        static readonly Regex MultiSpace=new Regex(@"\s{2,}",RegexOptions.Compiled);

        /// <summary>Формирует заголовок YouTube с разделителем « | ».</summary>
        public static string CleanForUpload(string title){
            if(string.IsNullOrWhiteSpace(title))return "";
            string t=title.Trim();
            while(true){
                var m=LeadingNumber.Match(t);
                if(!m.Success)break;
                t=t.Substring(m.Length).Trim();
            }
            t=PackSuffix.Replace(t,"").Trim();
            t=EllipsisSep.Replace(t," | ");
            t=DotSeparator.Replace(t," | ");
            t=PipeNorm.Replace(t," | ");
            t=MultiPipe.Replace(t," | ");
            t=MultiSpace.Replace(t," ").Trim();
            t=TrimPipeEdges(t);
            if(t.Length>MaxLength)
                throw new Exception("Заголовок после очистки длиннее "+MaxLength+" символов ("+t.Length+"): «"+(t.Length>48?t.Substring(0,48)+"…":t)+"»");
            return t;
        }

        static string TrimPipeEdges(string t){
            while(t.StartsWith("|"))t=t.Substring(1).Trim();
            while(t.EndsWith("|"))t=t.Substring(0,t.Length-1).Trim();
            return t;
        }

        /// <summary>YouTube-заголовок → безопасное имя файла Windows ( « | » → « . » ).</summary>
        public static string MakeWindowsSafeTitle(string title){
            if(string.IsNullOrWhiteSpace(title))return "video";
            string t;
            try{t=CleanForUpload(title);}
            catch{t=(title??"").Trim();}
            var parts=t.Split(new[]{" | "},StringSplitOptions.RemoveEmptyEntries);
            var clean=new System.Collections.Generic.List<string>();
            foreach(var part in parts){
                string p=SanitizeFilePart(part);
                if(p.Length>0)clean.Add(p);
            }
            t=clean.Count>0?string.Join(" . ",clean):SanitizeFilePart(t);
            return string.IsNullOrWhiteSpace(t)?"video":t;
        }

        static string SanitizeFilePart(string part){
            string p=(part??"").Trim();
            if(p.Length==0)return "";
            p=p.Replace("?","");
            p=Regex.Replace(p,@":\s*"," . ");
            p=p.Replace("/",".").Replace("\\",".").Replace("*","");
            p=p.Replace("<","‹").Replace(">","›");
            p=Regex.Replace(p,"\"([^\"]+)\"","«$1»");
            p=p.Replace("\"","");
            foreach(var ch in Path.GetInvalidFileNameChars())p=p.Replace(ch,' ');
            p=MultiSpace.Replace(p," ").Trim();
            return p;
        }
    }
}
