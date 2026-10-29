using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace VideoBatch {
    public enum LivePhase { Preparing, Starting, Live, Reconnecting, Stopping, Finished, Error, NeedsCleanup }
    public sealed class LiveAccount {
        public string LocalId = "", RemoteId = "", Name = "", ProtectedRefreshToken = "";
        // Empty Transport means a legacy OAuth account. Never silently migrate an unfinished OAuth broadcast.
        public string Transport = "", ProfileId = "";
        public int LocalPort = 3001;
    }
    public sealed class LiveJournalEntry {
        public string LocalId = "", RemoteId = "", OperationId = "", BroadcastId = "", StreamId = "";
        public string Transport = "", ProfileId = "";
        public int LocalPort = 3001;
        public bool BroadcastAttempted, StreamAttempted;
        // Set durably immediately before creation; old abandoned attempts lack this flag.
        public bool CreationPending;
        public bool RequiresConfirmedCleanup() {
            return CreationPending || !string.IsNullOrWhiteSpace(BroadcastId) || !string.IsNullOrWhiteSpace(StreamId);
        }
    }
    public sealed class LiveDialogDraft {
        public string LocalId = "", VideoFile = "", Title = "Прямой эфир", Description = "", Thumbnail = "", Tags = "", Privacy = "public";
        public bool MadeForKids;
        internal LiveDialogDraft Copy() { return (LiveDialogDraft)MemberwiseClone(); }
    }
    public sealed class LiveConfiguration {
        public string ClientId = "", ProtectedClientSecret = "";
        public List<LiveAccount> Accounts = new List<LiveAccount>();
        public List<LiveJournalEntry> Pending = new List<LiveJournalEntry>();
        public List<LiveDialogDraft> Drafts = new List<LiveDialogDraft>();
    }
    public sealed class LiveOptions {
        public string Folder = "", VideoFile = "", Title = "", Description = "", Thumbnail = "", Privacy = "public";
        public string[] Tags = new string[0];
        public bool MadeForKids;
        public int Height; // Legacy field retained for saved/test callers; new streams use source geometry.
        public string[] Files() {
            var extensions = new[] { ".mp4", ".mov", ".mkv", ".webm", ".m4v", ".avi", ".ts" };
            if (!string.IsNullOrWhiteSpace(VideoFile)) {
                if (!File.Exists(VideoFile) || !extensions.Contains(Path.GetExtension(VideoFile).ToLowerInvariant()))
                    throw new InvalidOperationException("Выберите существующий видеофайл для эфира.");
                return new[] { VideoFile };
            }
            if (!Directory.Exists(Folder)) throw new InvalidOperationException("Выберите видеофайл для эфира.");
            var files = Directory.GetFiles(Folder).Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("В выбранной папке нет видео. Подпапки не включаются.");
            return files;
        }
        public static int TagLength(IEnumerable<string> values) { var tags=values.Select(t => (t ?? "").Trim()).Where(t => t.Length>0).Distinct().ToArray(); return tags.Sum(t => t.Length+(t.Contains(" ")?2:0))+Math.Max(0,tags.Length-1); }
        public void Validate() {
            Files();
            Title = (Title ?? "").Trim();
            if (Title.Length < 1 || Title.Length > 100 || Title.Contains("<") || Title.Contains(">"))
                throw new InvalidOperationException("Название: от 1 до 100 символов, без < и >.");
            if (Encoding.UTF8.GetByteCount(Description ?? "") > 5000 || (Description ?? "").Contains("<") || (Description ?? "").Contains(">"))
                throw new InvalidOperationException("Описание: до 5000 байт UTF-8, без < и >.");
            if (!new[] { "public", "unlisted", "private" }.Contains(Privacy) || !new[] { 0, 720, 1080 }.Contains(Height))
                throw new InvalidOperationException("Неверные параметры эфира.");
            Tags = (Tags ?? new string[0]).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct().ToArray();
            int tagLength = TagLength(Tags);
            if (tagLength > 500 || Tags.Any(t => t.Contains("<") || t.Contains(">")))
                throw new InvalidOperationException("Теги: до 500 символов суммарно (с разделителями и кавычками для пробелов), без < и >.");
            if (!string.IsNullOrWhiteSpace(Thumbnail)) {
                if (!File.Exists(Thumbnail) || !new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(Thumbnail).ToLowerInvariant()))
                    throw new InvalidOperationException("Превью должно быть существующим JPG или PNG.");
                if (new FileInfo(Thumbnail).Length > 50 * 1024 * 1024) throw new InvalidOperationException("Превью должно быть не больше 50 МБ.");
                using (var image = System.Drawing.Image.FromFile(Thumbnail)) { if (image.Width < 1 || image.Height < 1) throw new InvalidOperationException("Не удалось прочитать превью."); }
            }
        }
    }
    public sealed class LiveView {
        public string LocalId, Name, Url, Detail;
        public LivePhase Phase;
        public bool Busy => Phase != LivePhase.Finished && Phase != LivePhase.Error;
        public string Label {
            get {
                switch (Phase) {
                    case LivePhase.Preparing: return "Подготовка видео";
                    case LivePhase.Starting: return "Запуск эфира";
                    case LivePhase.Live: return "В эфире";
                    case LivePhase.Reconnecting: return "Проверка связи";
                    case LivePhase.Stopping: return "Завершение эфира";
                    case LivePhase.NeedsCleanup: return "Проверить завершение";
                    case LivePhase.Error: return "Ошибка эфира";
                    default: return "Эфир завершён";
                }
            }
        }
    }
    public sealed class LiveStore {
        readonly object gate = new object();
        readonly string path;
        public LiveConfiguration Config { get; private set; }
        public LiveStore(string root = null) {
            path = Path.Combine(root ?? Store.Root, "youtube-live.xml");
            Config = File.Exists(path) ? Read(path) : new LiveConfiguration();
            Config.Accounts = Config.Accounts ?? new List<LiveAccount>();
            Config.Pending = Config.Pending ?? new List<LiveJournalEntry>();
            Config.Drafts = Config.Drafts ?? new List<LiveDialogDraft>();
        }
        static LiveConfiguration Read(string path) {
            try { using (var file = File.OpenRead(path)) return (LiveConfiguration)new XmlSerializer(typeof(LiveConfiguration)).Deserialize(file); }
            catch (Exception e) { throw new InvalidOperationException("Не удалось прочитать youtube-live.xml. Сохраните файл для диагностики; не запускайте повторные эфиры до проверки старых.", e); }
        }
        public LiveAccount Account(string localId) { lock (gate) return Config.Accounts.FirstOrDefault(a => a.LocalId == localId); }
        public LiveDialogDraft Draft(string localId) { lock (gate) return Config.Drafts.FirstOrDefault(d => d.LocalId == localId)?.Copy(); }
        public void SaveDrafts(IEnumerable<LiveDialogDraft> drafts) {
            lock (gate) {
                var saved = drafts.Select(d => d.Copy()).ToArray();
                var previous = Config.Drafts;
                Config.Drafts = previous.Where(d => !saved.Any(s => s.LocalId == d.LocalId)).Concat(saved).ToList();
                try { Save(); } catch { Config.Drafts = previous; throw; }
            }
        }
        public LiveJournalEntry[] PendingSnapshot() { lock (gate) return Config.Pending.ToArray(); }
        public void PruneAbandonedPending() {
            lock (gate) {
                foreach (var entry in Config.Pending.ToArray()) {
                    if (!entry.RequiresConfirmedCleanup()) Journal(entry, true);
                }
            }
        }
        public void SaveAccount(LiveAccount account) {
            lock (gate) {
                var previous = Config.Accounts;
                Config.Accounts = previous.Where(a => a.LocalId != account.LocalId).Concat(new[] { account }).ToList();
                try { Save(); } catch { Config.Accounts = previous; throw; }
            }
        }
        public void Journal(LiveJournalEntry entry, bool remove = false) {
            lock (gate) {
                var previous = Config.Pending;
                Config.Pending = previous.Where(e => e.OperationId != entry.OperationId).ToList();
                if (!remove) Config.Pending.Add(entry);
                try { Save(); } catch { Config.Pending = previous; throw; }
            }
        }
        public void Save() {
            lock (gate) {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                using (var file = File.Create(tmp)) new XmlSerializer(typeof(LiveConfiguration)).Serialize(file, Config);
                if (File.Exists(path)) File.Replace(tmp, path, path + ".bak"); else File.Move(tmp, path);
            }
        }
    }
}
