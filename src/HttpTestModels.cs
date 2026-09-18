using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace VideoBatch.HttpTest {
    public static class HttpTestPaths {
        public static readonly string MainRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VideoBatchDesktop");
        public static readonly string TestRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VideoBatchDesktop-HttpTest");
        public static readonly string MainSettings=Path.Combine(MainRoot,"settings.xml");
        public static readonly string SnapshotSettings=Path.Combine(TestRoot,"settings.snapshot.xml");
        public static readonly string Jobs=Path.Combine(TestRoot,"jobs");
        public static readonly string Log=Path.Combine(TestRoot,"http-test.log");
        public static readonly string ErrorLog=Path.Combine(TestRoot,"errors.txt");
    }

    [XmlRoot("Preferences")]
    public sealed class ImportedPreferences {
        public string ProtectedDolphinToken="";
        public int DolphinPort=3001;
        [XmlArray("YouTubeChannels"),XmlArrayItem("YouTubeChannel")]
        public List<ImportedYouTubeChannel> YouTubeChannels=new List<ImportedYouTubeChannel>();
    }

    public sealed class ImportedYouTubeChannel {
        public bool Enabled=true;
        public string Name="",ProfileId="",ExpectedIp="",Market="RU",Kind="long",ChannelUrl="";
        public override string ToString() {
            string name=string.IsNullOrWhiteSpace(Name)?"Без имени":Name.Trim();
            string kind=string.Equals(Kind,"shorts",StringComparison.OrdinalIgnoreCase)?"Shorts":"Long";
            return name+" · "+(Market??"RU")+" · "+kind+" · "+ProfileId;
        }
    }

    public sealed class SettingsSnapshot {
        public ImportedPreferences Preferences;
        public string SourceHash="";

        public static SettingsSnapshot CreateReadOnlyCopy() {
            if(!File.Exists(HttpTestPaths.MainSettings))
                throw new Exception("Не найден рабочий settings.xml. Сначала запустите обычный VideoBatch и сохраните Dolphin/аккаунты.");
            Directory.CreateDirectory(HttpTestPaths.TestRoot);
            byte[] bytes=null;
            string before="",after="";
            for(int attempt=0;attempt<3;attempt++) {
                before=HashFile(HttpTestPaths.MainSettings);
                using(var source=new FileStream(HttpTestPaths.MainSettings,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                    bytes=new byte[source.Length];
                    int offset=0,read;
                    while(offset<bytes.Length&&(read=source.Read(bytes,offset,bytes.Length-offset))>0)offset+=read;
                    if(offset!=bytes.Length)throw new IOException("Не удалось полностью прочитать рабочие настройки.");
                }
                after=HashFile(HttpTestPaths.MainSettings);
                if(string.Equals(before,after,StringComparison.OrdinalIgnoreCase))break;
                if(attempt==2)throw new IOException("VideoBatch меняет настройки прямо сейчас. Закройте окно сохранения и повторите.");
            }
            string temp=HttpTestPaths.SnapshotSettings+".tmp";
            File.WriteAllBytes(temp,bytes);
            if(File.Exists(HttpTestPaths.SnapshotSettings))File.Replace(temp,HttpTestPaths.SnapshotSettings,null);
            else File.Move(temp,HttpTestPaths.SnapshotSettings);

            ImportedPreferences preferences;
            using(var input=File.OpenRead(HttpTestPaths.SnapshotSettings))
                preferences=(ImportedPreferences)new XmlSerializer(typeof(ImportedPreferences)).Deserialize(input);
            if(preferences==null)throw new Exception("Копия настроек пуста.");
            if(preferences.YouTubeChannels==null)preferences.YouTubeChannels=new List<ImportedYouTubeChannel>();
            if(preferences.DolphinPort<1||preferences.DolphinPort>65535)preferences.DolphinPort=3001;
            return new SettingsSnapshot{Preferences=preferences,SourceHash=before};
        }

        public void AssertSourceUnchanged() {
            string current=HashFile(HttpTestPaths.MainSettings);
            if(!string.Equals(SourceHash,current,StringComparison.OrdinalIgnoreCase))
                throw new Exception("Рабочий settings.xml изменился во время теста другим процессом. Тестовая программа его не записывала; обновите снимок перед повтором.");
        }

        static string HashFile(string path) {
            using(var sha=SHA256.Create())
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","");
        }

        public static string Unprotect(string protectedValue) {
            try {
                if(string.IsNullOrWhiteSpace(protectedValue))return "";
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue),null,DataProtectionScope.CurrentUser));
            } catch { return ""; }
        }
    }

    public static class HttpTestTitle {
        public static string FromFile(string path) {
            string title=Path.GetFileNameWithoutExtension(path)??"";
            title=Regex.Replace(title,@"^\s*\d+[\s._\-–—)]*","");
            title=Regex.Replace(title,@"[._]+", " | ");
            title=Regex.Replace(title,@"\s*\|\s*", " | ");
            title=Regex.Replace(title,@"(?:\s*\|\s*){2,}", " | ");
            title=Regex.Replace(title,@"\s+", " ").Trim(' ','|','-','–','—');
            return title;
        }
    }

    [DataContract]
    public sealed class HttpUploadJob {
        [DataMember]public string profileId,expectedIp,expectedChannelId,video,title;
        [DataMember]public int localPort;
        [DataMember]public long scheduledUnixSeconds;
    }

    [DataContract]
    public sealed class HttpWorkerMessage {
        [DataMember]public string stage,text,error,url,videoId,ip;
        [DataMember]public double percent;
        [DataMember]public bool success,keptOpen;
    }
}
