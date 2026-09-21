using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

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
    public sealed class TaskQueueFile {
        [DataMember] public List<TaskQueueItem> Items = new List<TaskQueueItem>();
        [DataMember] public List<TaskEventLog> Events = new List<TaskEventLog>();
    }

    public static class TaskQueueStore {
        public static event Action Changed {
            add { TaskQueueWriter.Changed += value; }
            remove { TaskQueueWriter.Changed -= value; }
        }

        public static List<TaskQueueItem> Load() => TaskQueueWriter.Items();

        static TaskQueueFile LoadAll() => TaskQueueWriter.Snapshot();

        public static void Save(List<TaskQueueItem> items, List<TaskEventLog> events = null) {
            foreach (var item in items ?? new List<TaskQueueItem>()) {
                string id = item.Id;
                TaskQueueWriter.Upsert(id, t => {
                    t.Platform = item.Platform; t.Account = item.Account; t.ProfileId = item.ProfileId;
                    t.File = item.File; t.UploadMethod = item.UploadMethod; t.ScheduledAt = item.ScheduledAt;
                    t.Progress = item.Progress; t.Stage = item.Stage; t.Status = item.Status;
                    t.Result = item.Result; t.Url = item.Url; t.DiagnosticPath = item.DiagnosticPath;
                    t.LastConfirmedStage = item.LastConfirmedStage; t.UpdatedAt = item.UpdatedAt;
                }, immediate: true);
            }
            if (events != null) {
                foreach (var ev in events)
                    TaskQueueWriter.LogEvent(ev.Account, ev.Action, ev.Result, ev.Url, immediate: false);
                TaskQueueWriter.FlushNow();
            }
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

        public static int ActiveCount() => Load().Count(t => IsActive(t.Status));

        public static TaskQueueItem Upsert(string id, Action<TaskQueueItem> edit, bool immediate = false) {
            return TaskQueueWriter.Upsert(id, edit, immediate);
        }

        public static void LogEvent(string account, string action, string result, string url = "") {
            TaskQueueWriter.LogEvent(account, action, result, url, immediate: true);
        }

        public static List<TaskEventLog> RecentEvents(int max) {
            return LoadAll().Events.Take(max).ToList();
        }

        public static string NewId() => Guid.NewGuid().ToString("N");

        public static string MapStage(string workerStage) {
            switch ((workerStage ?? "").ToLowerInvariant()) {
                case "start":
                case "diagnostic": return TaskQueueStatus.Preparing;
                case "dolphin": return TaskQueueStatus.ProfileStart;
                case "ip": return TaskQueueStatus.IpCheck;
                case "youtube": return TaskQueueStatus.YoutubeConfirm;
                case "upload": return TaskQueueStatus.Uploading;
                case "video_created": return TaskQueueStatus.Creating;
                case "schedule":
                case "metadata": return TaskQueueStatus.Scheduling;
                case "thumbnail":
                case "thumbnail_warning": return TaskQueueStatus.Scheduling;
                case "done": return TaskQueueStatus.Done;
                case "manual_check": return TaskQueueStatus.ManualCheck;
                case "error": return TaskQueueStatus.Error;
                default: return workerStage ?? "";
            }
        }
    }
}
