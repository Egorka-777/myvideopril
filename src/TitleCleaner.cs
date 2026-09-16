using System.Text.RegularExpressions;

namespace VideoBatch {
    /// <summary>Очистка заголовка только перед отправкой в UploadJob (не для UI и не для базы).</summary>
    public static class TitleCleaner {
        static readonly Regex LeadingIndex=new Regex(@"^\s*\d+\.?\s+",RegexOptions.Compiled);

        /// <summary>Убирает префикс «1. », «2 », «10. » в начале. Середину строки не трогает.</summary>
        public static string CleanForUpload(string title){
            if(string.IsNullOrWhiteSpace(title))return "";
            string t=title.Trim();
            while(true){
                var m=LeadingIndex.Match(t);
                if(!m.Success)break;
                t=t.Substring(m.Length).Trim();
            }
            return t;
        }
    }
}
