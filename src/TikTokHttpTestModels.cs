using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace VideoBatch.TikTokHttpTest {
    public static class TikTokHttpTestPaths {
        public static readonly string MainRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoBatchDesktop");
        public static readonly string TestRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoBatchDesktop-TikTokHttpTest");
        public static readonly string MainSettings = Path.Combine(MainRoot, "settings.xml");
        public static readonly string SnapshotSettings = Path.Combine(TestRoot, "settings.snapshot.xml");
        public static readonly string Jobs = Path.Combine(TestRoot, "jobs");
        public static readonly string Log = Path.Combine(TestRoot, "tiktok-http-test.log");
        public static readonly string ErrorLog = Path.Combine(TestRoot, "errors.txt");
    }

    [XmlRoot("Preferences")]
    public sealed class ImportedPreferences {
        public string ProtectedDolphinToken = "";
        public int DolphinPort = 3001;
        [XmlArray("TikTokAccounts"), XmlArrayItem("TikTokAccount")]
        public List<ImportedTikTokAccount> TikTokAccounts = new List<ImportedTikTokAccount>();
    }

    public sealed class ImportedTikTokAccount {
        public bool Enabled = true;
        public string Name = "", ProfileId = "", ExpectedIp = "", Market = "RU", Caption = "";
        public override string ToString() {
            string n = string.IsNullOrWhiteSpace(Name) ? "TikTok" : Name.Trim();
            return n + " · " + (ProfileId ?? "").Trim();
        }
    }

    public sealed class SettingsSnapshot {
        public ImportedPreferences Preferences;
        public string SourceHash = "";

        public static SettingsSnapshot CreateReadOnlyCopy() {
            if (!File.Exists(TikTokHttpTestPaths.MainSettings))
                throw new Exception("Не найден рабочий settings.xml.");
            Directory.CreateDirectory(TikTokHttpTestPaths.TestRoot);
            byte[] bytes = File.ReadAllBytes(TikTokHttpTestPaths.MainSettings);
            string hash = Hash(bytes);
            File.WriteAllBytes(TikTokHttpTestPaths.SnapshotSettings, bytes);
            ImportedPreferences preferences;
            using (var input = File.OpenRead(TikTokHttpTestPaths.SnapshotSettings))
                preferences = (ImportedPreferences)new XmlSerializer(typeof(ImportedPreferences)).Deserialize(input);
            if (preferences == null) throw new Exception("Копия настроек пуста.");
            if (preferences.TikTokAccounts == null) preferences.TikTokAccounts = new List<ImportedTikTokAccount>();
            return new SettingsSnapshot { Preferences = preferences, SourceHash = hash };
        }

        public void AssertSourceUnchanged() {
            if (!string.Equals(SourceHash, HashFile(TikTokHttpTestPaths.MainSettings), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Рабочий settings.xml изменился во время теста.");
        }

        static string HashFile(string path) { return Hash(File.ReadAllBytes(path)); }
        static string Hash(byte[] bytes) {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        public static string Unprotect(string protectedValue) {
            try {
                if (string.IsNullOrWhiteSpace(protectedValue)) return "";
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
            } catch { return ""; }
        }
    }

    [DataContract]
    public sealed class HttpTikTokTestJob {
        [DataMember] public string profileId, expectedIp, market, publishMode = "immediate";
        [DataMember] public int localPort;
        [DataMember] public bool keepProfileOpen = true;
        [DataMember] public HttpTikTokTestItem[] items;
    }

    [DataContract]
    public sealed class HttpTikTokTestItem {
        [DataMember] public string localJobId, video, caption, publishMode = "immediate";
        [DataMember] public long scheduledUnixSeconds;
    }

    [DataContract]
    public sealed class WorkerMessage {
        [DataMember] public string stage, text, error, ip, url, diagnosticFile, transport;
        [DataMember] public double percent;
        [DataMember] public bool success, keptOpen;
    }
}
