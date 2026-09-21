using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    /// <summary>Single writer for task-queue.json — workers enqueue updates, one thread debounces disk writes.</summary>
    public static class TaskQueueWriter {
        enum PendingKind { Upsert, LogEvent }

        sealed class PendingOp {
            public PendingKind Kind;
            public string Id;
            public Action<TaskQueueItem> Edit;
            public bool Immediate;
            public string LogAccount, LogAction, LogResult, LogUrl;
        }

        static readonly object StateLock = new object();
        static readonly object WriteLock = new object();
        static readonly ConcurrentQueue<PendingOp> Queue = new ConcurrentQueue<PendingOp>();
        static TaskQueueFile _state = new TaskQueueFile();
        static Timer _debounceTimer;
        static volatile bool _dirty;
        static int _flushInProgress;

        static string PathFile => Path.Combine(Store.Root, "task-queue.json");

        static TaskQueueWriter() {
            ReloadFromDisk();
            _debounceTimer = new Timer(_ => FlushDebounced(), null, 250, 250);
        }

        public static event Action Changed;

        public static void ReloadFromDisk() {
            lock (StateLock) {
                _state = LoadAllFromDisk();
                _dirty = false;
            }
        }

        static TaskQueueFile LoadAllFromDisk() {
            try {
                if (!File.Exists(PathFile)) return new TaskQueueFile();
                var serializer = new DataContractJsonSerializer(typeof(TaskQueueFile));
                using (var f = File.OpenRead(PathFile))
                    return (TaskQueueFile)serializer.ReadObject(f) ?? new TaskQueueFile();
            } catch { return new TaskQueueFile(); }
        }

        public static TaskQueueFile Snapshot() {
            lock (StateLock) return CloneState(_state);
        }

        public static System.Collections.Generic.List<TaskQueueItem> Items() {
            lock (StateLock) {
                return (_state.Items ?? new System.Collections.Generic.List<TaskQueueItem>())
                    .Select(CloneItem).ToList();
            }
        }

        static TaskQueueFile CloneState(TaskQueueFile src) {
            return new TaskQueueFile {
                Items = (src.Items ?? new System.Collections.Generic.List<TaskQueueItem>()).Select(CloneItem).ToList(),
                Events = (src.Events ?? new System.Collections.Generic.List<TaskEventLog>()).Select(e => new TaskEventLog {
                    Account = e.Account, Action = e.Action, Result = e.Result, Time = e.Time, Url = e.Url
                }).ToList()
            };
        }

        static TaskQueueItem CloneItem(TaskQueueItem x) {
            return new TaskQueueItem {
                Id = x.Id, Platform = x.Platform, Account = x.Account, ProfileId = x.ProfileId,
                File = x.File, UploadMethod = x.UploadMethod, ScheduledAt = x.ScheduledAt,
                Progress = x.Progress, Stage = x.Stage, Status = x.Status, Result = x.Result,
                Url = x.Url, DiagnosticPath = x.DiagnosticPath, LastConfirmedStage = x.LastConfirmedStage,
                UpdatedAt = x.UpdatedAt
            };
        }

        public static TaskQueueItem Upsert(string id, Action<TaskQueueItem> edit, bool immediate = false) {
            if (string.IsNullOrWhiteSpace(id)) id = TaskQueueStore.NewId();
            Queue.Enqueue(new PendingOp { Kind = PendingKind.Upsert, Id = id, Edit = edit, Immediate = immediate });
            DrainQueue();
            lock (StateLock) {
                var item = _state.Items.FirstOrDefault(x => x.Id == id);
                return CloneItem(item ?? new TaskQueueItem { Id = id });
            }
        }

        public static void LogEvent(string account, string action, string result, string url = "", bool immediate = true) {
            Queue.Enqueue(new PendingOp {
                Kind = PendingKind.LogEvent,
                Immediate = immediate,
                LogAccount = account,
                LogAction = action,
                LogResult = result,
                LogUrl = url ?? ""
            });
            DrainQueue();
        }

        static void DrainQueue() {
            bool flushNow = false;
            while (Queue.TryDequeue(out var op)) {
                lock (StateLock) {
                    if (op.Kind == PendingKind.LogEvent) {
                        _state.Events.Insert(0, new TaskEventLog {
                            Account = op.LogAccount,
                            Action = op.LogAction,
                            Result = op.LogResult,
                            Time = DateTime.Now.ToString("HH:mm dd.MM"),
                            Url = op.LogUrl ?? ""
                        });
                        if (_state.Events.Count > 200)
                            _state.Events = _state.Events.Take(200).ToList();
                    } else {
                        var item = _state.Items.FirstOrDefault(x => x.Id == op.Id);
                        if (item == null) {
                            item = new TaskQueueItem { Id = op.Id };
                            _state.Items.Add(item);
                        }
                        op.Edit(item);
                        item.UpdatedAt = DateTime.Now.ToString("o");
                        if (!op.Immediate && IsFinalStatus(item.Status)) op.Immediate = true;
                    }
                    _dirty = true;
                    if (op.Immediate) flushNow = true;
                }
            }
            if (flushNow) FlushNow();
        }

        static bool IsFinalStatus(string status) {
            if (string.IsNullOrWhiteSpace(status)) return false;
            return status == TaskQueueStatus.Done || status == TaskQueueStatus.Error
                || status == TaskQueueStatus.Stopped || status == TaskQueueStatus.ManualCheck
                || status == TaskQueueStatus.Interrupted;
        }

        static void FlushDebounced() {
            if (!_dirty) return;
            FlushNow();
        }

        public static void FlushNow() {
            if (Interlocked.CompareExchange(ref _flushInProgress, 1, 0) != 0) return;
            TaskQueueFile snapshot;
            try {
                lock (StateLock) {
                    if (!_dirty) return;
                    snapshot = CloneState(_state);
                    _dirty = false;
                }
                WriteAtomic(snapshot);
                try { Changed?.Invoke(); } catch { }
            } finally {
                Interlocked.Exchange(ref _flushInProgress, 0);
            }
        }

        static void WriteAtomic(TaskQueueFile file) {
            Directory.CreateDirectory(Store.Root);
            string temp = PathFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            lock (WriteLock) {
                var serializer = new DataContractJsonSerializer(typeof(TaskQueueFile));
                using (var f = File.Create(temp)) serializer.WriteObject(f, file);
                if (File.Exists(PathFile)) File.Replace(temp, PathFile, null);
                else File.Move(temp, PathFile);
            }
        }

        public static bool RunConcurrentWriteSelfTest(int writers = 40, int opsPerWriter = 25) {
            ReloadFromDisk();
            var errors = new ConcurrentBag<string>();
            Parallel.For(0, writers, w => {
                try {
                    for (int i = 0; i < opsPerWriter; i++) {
                        string id = "stress-" + w + "-" + i;
                        Upsert(id, t => {
                            t.Platform = "YouTube";
                            t.Account = "acc-" + w;
                            t.Status = i == opsPerWriter - 1 ? TaskQueueStatus.Done : TaskQueueStatus.Uploading;
                            t.Progress = i;
                        }, immediate: i == opsPerWriter - 1);
                    }
                } catch (Exception ex) {
                    errors.Add("writer " + w + ": " + ex.Message);
                }
            });
            FlushNow();
            return errors.IsEmpty;
        }
    }
}
