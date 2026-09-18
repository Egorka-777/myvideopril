using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VideoBatch {
    public static class TaskQueueStatus {
        public const string Waiting = "ожидает";
        public const string Preparing = "подготовка";
        public const string ProfileStart = "запуск профиля";
        public const string IpCheck = "проверка IP";
        public const string YoutubeConfirm = "подтверждение YouTube";
        public const string Uploading = "загрузка";
        public const string FileAccepted = "файл принят";
        public const string Creating = "создание ролика";
        public const string Scheduling = "настройка публикации";
        public const string Done = "выполнено";
        public const string Stopped = "остановлено";
        public const string Error = "ошибка";
        public const string ManualCheck = "требуется ручная проверка";
        public const string Interrupted = "прервано — требуется проверка";
    }

    [DataContract]
    public sealed class TaskQueueItem {
        [DataMember] public string Id = "";
        [DataMember] public string Platform = "YouTube";
        [DataMember] public string Account = "";
        [DataMember] public string ProfileId = "";
        [DataMember] public string File = "";
        [DataMember] public string UploadMethod = "HTTP";
        [DataMember] public string ScheduledAt = "";
        [DataMember] public double Progress;
        [DataMember] public string Stage = "";
        [DataMember] public string Status = TaskQueueStatus.Waiting;
        [DataMember] public string Result = "";
        [DataMember] public string Url = "";
        [DataMember] public string DiagnosticPath = "";
        [DataMember] public string LastConfirmedStage = "";
        [DataMember] public string UpdatedAt = "";
    }

    [DataContract]
    public sealed class TaskEventLog {
        [DataMember] public string Account = "";
        [DataMember] public string Action = "";
        [DataMember] public string Result = "";
        [DataMember] public string Time = "";
        [DataMember] public string Url = "";
    }

    [DataContract]
    sealed class TaskQueueFile {
        [DataMember] public List<TaskQueueItem> Items = new List<TaskQueueItem>();
        [DataMember] public List<TaskEventLog> Events = new List<TaskEventLog>();
    }

    public static class TaskQueueStore {
        static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(TaskQueueFile));
        static string PathFile => System.IO.Path.Combine(Store.Root, "task-queue.json");

        public static event Action Changed;

        public static List<TaskQueueItem> Load() {
            try {
                if (!File.Exists(PathFile)) return new List<TaskQueueItem>();
                using (var f = File.OpenRead(PathFile))
                    return ((TaskQueueFile)Serializer.ReadObject(f)).Items ?? new List<TaskQueueItem>();
            } catch { return new List<TaskQueueItem>(); }
        }

        static TaskQueueFile LoadAll() {
            try {
                if (!File.Exists(PathFile)) return new TaskQueueFile();
                using (var f = File.OpenRead(PathFile))
                    return (TaskQueueFile)Serializer.ReadObject(f);
            } catch { return new TaskQueueFile(); }
        }

        public static void Save(List<TaskQueueItem> items, List<TaskEventLog> events = null) {
            var file = LoadAll();
            file.Items = items ?? new List<TaskQueueItem>();
            if (events != null) file.Events = events;
            Directory.CreateDirectory(Store.Root);
            string temp = PathFile + ".tmp";
            using (var f = File.Create(temp)) Serializer.WriteObject(f, file);
            if (File.Exists(PathFile)) File.Replace(temp, PathFile, null);
            else File.Move(temp, PathFile);
            Changed?.Invoke();
        }

        public static void RecoverAfterCrash() {
            var file = LoadAll();
            bool changed = false;
            foreach (var t in file.Items) {
                if (IsActive(t.Status)) {
                    t.Status = TaskQueueStatus.Interrupted;
                    t.Result = "Прервано при закрытии приложения";
                    t.UpdatedAt = DateTime.Now.ToString("o");
                    changed = true;
                }
            }
            if (changed) Save(file.Items, file.Events);
        }

        static bool IsActive(string status) {
            if (string.IsNullOrWhiteSpace(status)) return false;
            if (status == TaskQueueStatus.Done || status == TaskQueueStatus.Error || status == TaskQueueStatus.Stopped) return false;
            if (status == TaskQueueStatus.ManualCheck || status == TaskQueueStatus.Interrupted) return false;
            return true;
        }

        public static int ActiveCount() {
            return Load().Count(t => IsActive(t.Status));
        }

        public static TaskQueueItem Upsert(string id, Action<TaskQueueItem> edit) {
            var file = LoadAll();
            var item = file.Items.FirstOrDefault(x => x.Id == id);
            if (item == null) {
                item = new TaskQueueItem { Id = id };
                file.Items.Add(item);
            }
            edit(item);
            item.UpdatedAt = DateTime.Now.ToString("o");
            Save(file.Items, file.Events);
            return item;
        }

        public static void LogEvent(string account, string action, string result, string url = "") {
            var file = LoadAll();
            file.Events.Insert(0, new TaskEventLog {
                Account = account,
                Action = action,
                Result = result,
                Time = DateTime.Now.ToString("HH:mm dd.MM"),
                Url = url ?? ""
            });
            if (file.Events.Count > 200) file.Events = file.Events.Take(200).ToList();
            Save(file.Items, file.Events);
        }

        public static List<TaskEventLog> RecentEvents(int max) {
            return LoadAll().Events.Take(max).ToList();
        }

        public static string NewId() {
            return Guid.NewGuid().ToString("N");
        }

        public static string MapStage(string workerStage) {
            switch ((workerStage ?? "").ToLowerInvariant()) {
                case "start":
                case "diagnostic": return TaskQueueStatus.Preparing;
                case "dolphin": return TaskQueueStatus.ProfileStart;
                case "ip": return TaskQueueStatus.IpCheck;
                case "youtube": return TaskQueueStatus.YoutubeConfirm;
                case "upload": return TaskQueueStatus.Uploading;
                case "video_created": return TaskQueueStatus.Creating;
                case "schedule": return TaskQueueStatus.Scheduling;
                case "done": return TaskQueueStatus.Done;
                case "manual_check": return TaskQueueStatus.ManualCheck;
                case "error": return TaskQueueStatus.Error;
                default: return workerStage ?? "";
            }
        }
    }
}
