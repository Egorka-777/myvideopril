using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace VideoBatch {
    public sealed class AccountRecord {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "", Platform = "YouTube", Language = "RU", Country = "", Status = "Проверить";
        public string Url = "", Login = "", Password = "", AccessNotes = "", Proxy = "", PurchaseUrl = "", Notes = "";
        public DateTime UpdatedUtc = DateTime.UtcNow;
        public AccountRecord Copy() { return (AccountRecord)MemberwiseClone(); }
    }

    public sealed class AccountLibraryData {
        public int Version = 1;
        public List<AccountRecord> Accounts = new List<AccountRecord>();
    }

    /// <summary>Independent notebook. Never modifies upload profiles, settings.xml or browser sessions.</summary>
    public sealed class AccountLibraryStore {
        static readonly byte[] Header = Encoding.ASCII.GetBytes("VBAC1\n");
        static readonly XmlSerializer Serializer = new XmlSerializer(typeof(AccountLibraryData));
        public static readonly string[] Statuses = { "Рабочий", "Проверить", "Не работает", "Заблокирован", "Архив" };
        public readonly string Path;
        public AccountLibraryStore(string root = null) { Path = System.IO.Path.Combine(root ?? Store.Root, "accounts.vault"); }

        public List<AccountRecord> Load() { return Locked(() => Read().Accounts.Select(a => a.Copy()).ToList()); }
        public void Upsert(AccountRecord record) {
            Validate(record);
            Change(data => {
                var copy = record.Copy(); copy.UpdatedUtc = DateTime.UtcNow;
                int index = data.Accounts.FindIndex(a => a.Id == copy.Id);
                if (index < 0) data.Accounts.Add(copy); else data.Accounts[index] = copy;
            });
        }
        public void Remove(string id) { Change(data => data.Accounts.RemoveAll(a => a.Id == id)); }
        public void SetStatus(string id, string status) {
            if (!Statuses.Contains(status)) throw new ArgumentException("Выбери статус из списка.");
            Change(data => {
                var a = data.Accounts.FirstOrDefault(x => x.Id == id);
                if (a == null) throw new InvalidOperationException("Аккаунт уже удалён. Обнови список.");
                a.Status = status; a.UpdatedUtc = DateTime.UtcNow;
            });
        }
        public int Import(IEnumerable<AccountRecord> records) {
            var incoming = records.Select(r => r.Copy()).ToList(); incoming.ForEach(Validate);
            int added = 0;
            Change(data => {
                foreach (var record in incoming) {
                    if (data.Accounts.Any(a => SameAccount(a, record))) continue;
                    record.Id = Guid.NewGuid().ToString("N"); record.UpdatedUtc = DateTime.UtcNow;
                    data.Accounts.Add(record); added++;
                }
            });
            return added;
        }
        public static bool SameAccount(AccountRecord a, AccountRecord b) {
            if (a.Platform != b.Platform || a.Language != b.Language) return false;
            if (!string.IsNullOrWhiteSpace(a.Url) && !string.IsNullOrWhiteSpace(b.Url))
                return string.Equals(a.Url.Trim().TrimEnd('/'), b.Url.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            return string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Login.Trim(), b.Login.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        public static void Validate(AccountRecord a) {
            if (a == null || string.IsNullOrWhiteSpace(a.Name)) throw new ArgumentException("Укажи название аккаунта.");
            if (a.Platform != "YouTube" && a.Platform != "TikTok") throw new ArgumentException("Выбери YouTube или TikTok.");
            if (a.Language != "RU" && a.Language != "EN") throw new ArgumentException("Выбери язык RU или EN.");
            if (!Statuses.Contains(a.Status)) throw new ArgumentException("Выбери статус аккаунта.");
            if (string.IsNullOrWhiteSpace(a.Id)) throw new ArgumentException("У аккаунта нет идентификатора.");
            ValidateUrl(a.Url); ValidateUrl(a.PurchaseUrl);
        }
        public static void ValidateUrl(string value) {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new ArgumentException("Ссылка должна начинаться с https:// или http://.");
        }
        public static bool Matches(AccountRecord a, string query) {
            var words = (query ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string text = string.Join(" ", a.Name, a.Url, a.Country, a.Notes, a.Login, a.Platform, a.Language, a.Status, a.PurchaseUrl);
            return words.All(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        void Change(Action<AccountLibraryData> mutate) {
            Locked(() => { var data = Read(); mutate(data); Write(data); return true; });
        }
        T Locked<T>(Func<T> action) {
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(Path).ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, "Local\\VideoBatchAccounts_" + key)) {
                bool held = false;
                try {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(8)); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Аккаунты открыты в другом окне. Попробуй ещё раз.");
                    return action();
                } finally { if (held) mutex.ReleaseMutex(); }
            }
        }
        AccountLibraryData Read() {
            if (!File.Exists(Path)) return new AccountLibraryData();
            byte[] plain = null;
            try {
                var bytes = File.ReadAllBytes(Path);
                if (bytes.Length <= Header.Length || !bytes.Take(Header.Length).SequenceEqual(Header)) throw new InvalidDataException();
                plain = ProtectedData.Unprotect(bytes.Skip(Header.Length).ToArray(), null, DataProtectionScope.CurrentUser);
                using (var stream = new MemoryStream(plain)) {
                    var data = (AccountLibraryData)Serializer.Deserialize(stream);
                    if (data.Version != 1 || data.Accounts == null) throw new InvalidDataException();
                    if (data.Accounts.Any(a => a == null || string.IsNullOrWhiteSpace(a.Id)) || data.Accounts.Select(a => a.Id).Distinct().Count() != data.Accounts.Count)
                        throw new InvalidDataException();
                    return data;
                }
            } catch (Exception e) when (e is CryptographicException || e is InvalidDataException || e is InvalidOperationException) {
                throw new IOException("Не удалось открыть сохранённые аккаунты. Файл повреждён или создан под другим пользователем Windows. Он не будет перезаписан. Сохрани accounts.vault и accounts.vault.bak для восстановления.", e);
            } finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
        }
        void Write(AccountLibraryData data) {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            byte[] plain = null;
            string temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new MemoryStream()) {
                    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize })) Serializer.Serialize(writer, data);
                    plain = stream.ToArray();
                }
                var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.Write(Header, 0, Header.Length); stream.Write(encrypted, 0, encrypted.Length); stream.Flush(true);
                }
                if (File.Exists(Path)) File.Replace(temp, Path, Path + ".bak"); else File.Move(temp, Path);
            } finally {
                if (plain != null) Array.Clear(plain, 0, plain.Length);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
