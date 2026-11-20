using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class ProfilePreset {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Bio { get; set; } = "";
        public string AvatarPath { get; set; } = "";
        public ProfilePreset Copy() { return (ProfilePreset)MemberwiseClone(); }
    }

    public sealed class ProfilePresetUsage {
        public string Network { get; set; }
        public string PresetId { get; set; }
        public string AccountId { get; set; }
        public string UsedUtc { get; set; }
    }

    public sealed class ProfilePresetData {
        public int Version { get; set; } = 1;
        public List<ProfilePreset> Presets { get; set; } = new List<ProfilePreset>();
        public List<ProfilePresetUsage> Usage { get; set; } = new List<ProfilePresetUsage>();
        public List<string> DeletedIds { get; set; } = new List<string>();
    }

    /// <summary>JSON library. Usage and deleted IDs survive edits/deletes; Load returns absolute avatar paths.</summary>
    public sealed class ProfilePresetStore {
        public string Root { get; }
        public string DataPath { get { return Path.Combine(Root, "library.json"); } }
        readonly string seedRoot;
        // root is the application storage root, not the profile-presets subdirectory.
        public ProfilePresetStore(string root = null, string seedDirectory = null) {
            Root = Path.GetFullPath(Path.Combine(root ?? Store.Root, "profile-presets"));
            seedRoot = Path.GetFullPath(seedDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "profile-presets"));
        }
        public List<ProfilePreset> Load() {
            return Locked(() => Read().Presets.Select(Resolve).ToList());
        }
        public void Save(ProfilePreset preset) {
            Validate(preset);
            Locked(() => {
                var data = Read();
                if (data.DeletedIds.Contains(preset.Id)) throw new InvalidOperationException("Этот пресет удалён. Создай новый пресет.");
                var copy = preset.Copy();
                copy.AvatarPath = RelativeAvatar(ImportAvatar(copy.AvatarPath));
                int index = data.Presets.FindIndex(p => p.Id == copy.Id);
                if (index < 0) data.Presets.Add(copy); else data.Presets[index] = copy;
                Write(data); return true;
            });
        }
        public void Delete(string id) {
            RequireId(id);
            Locked(() => {
                var data = Read();
                data.Presets.RemoveAll(p => p.Id == id);
                if (!data.DeletedIds.Contains(id)) data.DeletedIds.Add(id);
                Write(data); return true;
            });
        }
        public bool GetUsed(string network, string id) { return GetUsage(network, id) != null; }
        public ProfilePresetUsage GetUsage(string network, string id) {
            network = NormalizeNetwork(network); RequireId(id);
            return Locked(() => Read().Usage.FirstOrDefault(u => u.Network == network && u.PresetId == id));
        }
        /// <summary>Call only after the social network confirms all profile changes. Never called by Save.</summary>
        public void MarkUsed(string network, string id, string accountId) {
            network = NormalizeNetwork(network); RequireId(id);
            if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("Не выбран аккаунт.");
            Locked(() => {
                var data = Read();
                var previous = data.Usage.FirstOrDefault(u => u.Network == network && u.PresetId == id);
                if (previous != null) {
                    if (previous.AccountId != accountId) throw new InvalidOperationException("Пресет уже использован в этой сети.");
                    return true;
                }
                // Keep the receipt even if another window deleted the preset while applying it.
                data.Usage.Add(new ProfilePresetUsage { Network = network, PresetId = id, AccountId = accountId, UsedUtc = DateTime.UtcNow.ToString("o") });
                Write(data); return true;
            });
        }
        public List<string> ListAvatars() {
            Load();
            string directory = Path.Combine(Root, "avatars");
            return Directory.Exists(directory) ? Directory.GetFiles(directory).Where(IsImagePath).OrderBy(p => p).ToList() : new List<string>();
        }
        public string ImportAvatar(string path) {
            ValidateAvatar(path);
            string full = Path.GetFullPath(path), directory = Path.Combine(Root, "avatars");
            Directory.CreateDirectory(directory);
            if (string.Equals(Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase)) return full;
            // Content identity avoids duplicate copies when saving unchanged fields.
            string hash;
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(full))
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            string destination = Path.Combine(directory, hash + "-" + Path.GetFileName(full));
            if (!File.Exists(destination)) File.Copy(full, destination);
            return destination;
        }
        public static void Validate(ProfilePreset preset) {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            RequireId(preset.Id);
            if (string.IsNullOrWhiteSpace(preset.Name)) throw new ArgumentException("Укажи имя профиля.");
            if (string.IsNullOrWhiteSpace(preset.Bio)) throw new ArgumentException("Заполни описание профиля.");
            ValidateAvatar(preset.AvatarPath);
        }
        public static string NormalizeNetwork(string network) {
            string value = (network ?? "").Trim().ToLowerInvariant();
            if (value != "tiktok" && value != "instagram" && value != "telegram") throw new ArgumentException("Выбери TikTok, Instagram или Telegram.");
            return value;
        }
        static void RequireId(string id) { if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("У пресета нет идентификатора."); }
        static bool IsImagePath(string path) {
            return new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant());
        }
        static void ValidateAvatar(string path) {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new ArgumentException("Выбери существующий файл аватара.");
            if (!IsImagePath(path)) throw new ArgumentException("Выбери изображение PNG, JPEG, GIF или BMP.");
            using (var image = Image.FromFile(path)) { if (image.Width < 1 || image.Height < 1) throw new ArgumentException("Пустое изображение аватара."); }
        }
        ProfilePreset Resolve(ProfilePreset preset) {
            var copy = preset.Copy();
            if (!Path.IsPathRooted(copy.AvatarPath)) copy.AvatarPath = Path.GetFullPath(Path.Combine(Root, copy.AvatarPath));
            return copy;
        }
        string RelativeAvatar(string path) { return "avatars/" + Path.GetFileName(path); }
        ProfilePresetData Read() {
            var serializer = new JavaScriptSerializer();
            if (!File.Exists(DataPath)) {
                string seed = Path.Combine(seedRoot, "presets.json");
                if (!File.Exists(seed)) throw new FileNotFoundException("Не найдена встроенная библиотека пресетов.", seed);
                var items = serializer.Deserialize<List<ProfilePreset>>(File.ReadAllText(seed, Encoding.UTF8));
                if (items == null) throw new InvalidDataException("Пустой файл встроенных пресетов.");
                var initial = new ProfilePresetData();
                foreach (var item in items) {
                    if (item == null) throw new InvalidDataException("Некорректный встроенный пресет.");
                    RequireId(item.Id);
                    if (initial.Presets.Any(p => p.Id == item.Id)) throw new InvalidDataException("Повтор идентификатора встроенного пресета.");
                    var copy = item.Copy();
                    copy.AvatarPath = Path.Combine(seedRoot, copy.AvatarPath ?? "");
                    Validate(copy);
                    copy.AvatarPath = RelativeAvatar(ImportAvatar(copy.AvatarPath));
                    initial.Presets.Add(copy);
                }
                string bundledAvatars = Path.Combine(seedRoot, "avatars");
                if (Directory.Exists(bundledAvatars)) foreach (string avatar in Directory.GetFiles(bundledAvatars).Where(IsImagePath)) ImportAvatar(avatar);
                Write(initial); return initial;
            }
            // Malformed data must surface to the user, never silently reset usage.
            var data = serializer.Deserialize<ProfilePresetData>(File.ReadAllText(DataPath, Encoding.UTF8));
            if (data == null || data.Version != 1 || data.Presets == null || data.Usage == null || data.DeletedIds == null
                || data.Presets.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.AvatarPath))
                || data.Presets.Select(p => p.Id).Distinct().Count() != data.Presets.Count
                || data.Usage.Any(u => u == null || string.IsNullOrWhiteSpace(u.PresetId) || string.IsNullOrWhiteSpace(u.AccountId)
                    || (u.Network != "tiktok" && u.Network != "instagram" && u.Network != "telegram")))
                throw new InvalidDataException("Повреждена библиотека пресетов. Сохранённые данные не изменены.");
            return data;
        }
        void Write(ProfilePresetData data) {
            Directory.CreateDirectory(Root);
            string temp = DataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(data));
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(DataPath)) File.Replace(temp, DataPath, DataPath + ".bak"); else File.Move(temp, DataPath);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        T Locked<T>(Func<T> action) {
            string key;
            using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Root.ToUpperInvariant()))).Replace("-", "");
            using (var mutex = new Mutex(false, "Local\\VideoBatchProfilePresets_" + key)) {
                bool held = false;
                try {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(8)); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("Библиотека пресетов занята другим окном.");
                    return action();
                } finally { if (held) mutex.ReleaseMutex(); }
            }
        }
    }
}
