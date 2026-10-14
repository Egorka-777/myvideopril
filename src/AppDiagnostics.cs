using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Text;
using System.Text.RegularExpressions;
namespace VideoBatch {
    public static class AppDiagnostics {
        static readonly object gate=new object();
        public static string Folder => Path.Combine(Store.Root,"diagnostics");
        public static string Clean(string text) {
            string s=LiveDiagnostics.Sanitize(text);
            s=Regex.Replace(s,@"(https?://)[^/\s@]+@","$1[скрыто]@",RegexOptions.IgnoreCase);
            return Regex.Replace(s,@"([?&](?:token|key|password|signature|sig)=)[^&\s]+","$1[скрыто]",RegexOptions.IgnoreCase);
        }
        public static void Write(string platform,string account,string task,string stage,string message,string level="info") {
            lock(gate)try {
                Directory.CreateDirectory(Folder);
                var record=new Dictionary<string,string>{{"time",DateTime.Now.ToString("o")},{"platform",platform??""},{"account",account??""},{"task",task??""},{"stage",stage??""},{"level",level},{"message",Clean(message)}};
                File.AppendAllText(Path.Combine(Folder,"app-"+DateTime.Now.ToString("yyyyMMdd")+".jsonl"),new JavaScriptSerializer().Serialize(record)+Environment.NewLine,Encoding.UTF8);
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        public static string Level(string stage) => stage=="error"?"error":stage=="manual_check"||stage=="thumbnail_warning"?"warning":"info";
    }
}
