using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class InstagramPresetBatchJob {
        public bool Enabled { get; set; } = true;
        public string ProfileId { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string Username { get; set; } = "";
        public string PresetId { get; set; } = "";
    }

    /// <summary>Batch Instagram profile presets via saved WebView2 sessions. Jobs file: %LocalAppData%\VideoBatchDesktop\instagram\preset-batch.jobs.json</summary>
    public static class InstagramPresetBatchRunner {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static string JobsPath => Path.Combine(Store.Root, "instagram", "preset-batch.jobs.json");
        static string BundledPath => Path.Combine(Path.GetDirectoryName(typeof(InstagramPresetBatchRunner).Assembly.Location) ?? "",
            "tools", "profile-presets", "instagram-batch.jobs.json");

        public static List<InstagramPresetBatchJob> Load() {
            EnsureJobsFile();
            var list = Json.Deserialize<List<InstagramPresetBatchJob>>(File.ReadAllText(JobsPath, Encoding.UTF8));
            if (list == null) throw new InvalidDataException("Не удалось прочитать список пакетного оформления Instagram.");
            return list;
        }

        public static void EnsureJobsFile() {
            string dir = Path.GetDirectoryName(JobsPath);
            Directory.CreateDirectory(dir);
            if (File.Exists(JobsPath)) return;
            if (!File.Exists(BundledPath)) throw new FileNotFoundException("Не найден шаблон instagram-batch.jobs.json.", BundledPath);
            File.Copy(BundledPath, JobsPath);
        }

        public static async Task<IReadOnlyList<string>> RunAllAsync(Action<string> log = null) {
            var settings = Store.Load();
            string token = WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
            var presets = new ProfilePresetStore();
            var lines = new List<string>();
            void Report(string text) { log?.Invoke(text); lines.Add(text); }
            foreach (var job in Load().Where(j => j != null && j.Enabled)) {
                if (!System.Text.RegularExpressions.Regex.IsMatch(job.ProfileId ?? "", @"^\d+$"))
                    throw new InvalidOperationException("Некорректный ProfileId в списке: " + job.ProfileId);
                if (string.IsNullOrWhiteSpace(job.Username) || string.IsNullOrWhiteSpace(job.PresetId))
                    throw new InvalidOperationException("Для профиля " + job.ProfileId + " укажите Username и PresetId.");
                var preset = presets.Load().FirstOrDefault(p => p.Id == job.PresetId);
                if (preset == null) throw new InvalidOperationException("Пресет " + job.PresetId + " не найден.");
                if (presets.GetUsed("instagram", preset.Id)) {
                    Report("SKIP " + job.ProfileId + " · пресет " + preset.Id + " уже использован в Instagram.");
                    continue;
                }
                string name = string.IsNullOrWhiteSpace(job.ProfileName) ? job.ProfileId : job.ProfileName.Trim();
                string user = job.Username.Trim().TrimStart('@');
                var route = await TelegramProxyCatalog.FetchDolphin(token,
                    new[] { new KeyValuePair<string, string>(job.ProfileId, name) }, includeCatalog: false).ConfigureAwait(false);
                string key = "dolphin:profile:" + job.ProfileId;
                var proxy = route.Proxies.SingleOrDefault(p => p.source_id == key);
                if (proxy == null || string.IsNullOrWhiteSpace(proxy.url))
                    throw new InvalidOperationException("Нет прокси Dolphin для профиля " + job.ProfileId + ".");
                Report("START " + job.ProfileId + " @" + user + " → " + preset.Name);
                try {
                    await InstagramWebProfileEditor.ApplyAsync(job.ProfileId, name, proxy.url, user,
                        preset.Name, preset.Bio, preset.AvatarPath, Report).ConfigureAwait(false);
                    presets.MarkUsed("instagram", preset.Id, job.ProfileId);
                    Report("OK " + job.ProfileId);
                } catch (Exception error) {
                    Report("FAIL " + job.ProfileId + " · " + error.GetBaseException().Message);
                    throw;
                }
            }
            return lines;
        }
    }
}