using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VideoBatch {
    public sealed class HttpAccountMetrics {
        public string Account = "", ProfileId = "";
        public string Outcome = "success";
        public DateTime QueuedAt, StartedAt, FinishedAt;
        public TimeSpan QueueWait, DolphinStart, YoutubeConfirm, FileTransfer, VideoCreated, Metadata, Thumbnail, Total;

        DateTime? _dolphinAt, _youtubeAt, _uploadAt, _videoCreatedAt, _metadataAt, _thumbnailAt;

        public void MarkStarted() {
            StartedAt = DateTime.Now;
            if (QueuedAt != default) QueueWait = StartedAt - QueuedAt;
        }

        public void OnStage(string stage) {
            if (string.IsNullOrWhiteSpace(stage)) return;
            var now = DateTime.Now;
            switch (stage.ToLowerInvariant()) {
                case "dolphin":
                    _dolphinAt = now;
                    if (StartedAt != default) DolphinStart = now - StartedAt;
                    break;
                case "youtube":
                    if (_dolphinAt.HasValue) YoutubeConfirm = now - _dolphinAt.Value;
                    else if (StartedAt != default) YoutubeConfirm = now - StartedAt;
                    _youtubeAt = now;
                    break;
                case "upload":
                    if (_youtubeAt.HasValue && !_uploadAt.HasValue) FileTransfer = now - _youtubeAt.Value;
                    _uploadAt = now;
                    break;
                case "video_created":
                    if (_uploadAt.HasValue) VideoCreated = now - _uploadAt.Value;
                    _videoCreatedAt = now;
                    break;
                case "schedule":
                case "metadata":
                    if (_videoCreatedAt.HasValue) Metadata = now - _videoCreatedAt.Value;
                    _metadataAt = now;
                    break;
                case "thumbnail":
                case "thumbnail_warning":
                    if (_metadataAt.HasValue) Thumbnail = now - _metadataAt.Value;
                    _thumbnailAt = now;
                    break;
            }
        }

        public void Finish(string outcome) {
            Outcome = outcome ?? "success";
            FinishedAt = DateTime.Now;
            if (StartedAt != default) Total = FinishedAt - StartedAt;
        }
    }

    public sealed class HttpUploadBatchMetrics {
        readonly object _lock = new object();
        readonly List<HttpAccountMetrics> _accounts = new List<HttpAccountMetrics>();
        public DateTime StartedAt = DateTime.Now;
        public DateTime FinishedAt;
        public int Processed, Success, Warning, Error;

        public IReadOnlyList<HttpAccountMetrics> Accounts {
            get { lock (_lock) return _accounts.ToList(); }
        }

        public HttpAccountMetrics BeginAccount(string account, string profileId) {
            var m = new HttpAccountMetrics { Account = account ?? "", ProfileId = profileId ?? "", QueuedAt = DateTime.Now };
            lock (_lock) _accounts.Add(m);
            return m;
        }

        public void Complete() {
            lock (_lock) {
                FinishedAt = DateTime.Now;
                Processed = _accounts.Count;
                Success = _accounts.Count(a => a != null && a.Outcome == "success");
                Warning = _accounts.Count(a => a != null && (a.Outcome == "warning" || a.Outcome == "manual_check"));
                Error = _accounts.Count(a => a != null && a.Outcome == "error");
            }
        }

        public TimeSpan AverageAccountTime() {
            var done = Accounts.Where(a => a != null && a.Total > TimeSpan.Zero).ToList();
            if (done.Count == 0) return TimeSpan.Zero;
            return TimeSpan.FromTicks((long)done.Average(a => a.Total.Ticks));
        }

        public TimeSpan TotalBatchTime() {
            if (FinishedAt == default) return DateTime.Now - StartedAt;
            return FinishedAt - StartedAt;
        }

        public string FormatSummary() {
            var avg = AverageAccountTime();
            var sb = new StringBuilder();
            sb.Append("HTTP метрики: обработано ").Append(Processed)
                .Append(", успешно ").Append(Success)
                .Append(", с предупреждением ").Append(Warning)
                .Append(", ошибка ").Append(Error)
                .Append(", среднее время аккаунта ").Append(FormatSpan(avg))
                .Append(", общее время пачки ").Append(FormatSpan(TotalBatchTime()));
            return sb.ToString();
        }

        public void WriteAccountLines(Action<string> log) {
            if (log == null) return;
            foreach (var a in Accounts) {
                log("  · " + a.Account + " [" + a.ProfileId + "]: " + a.Outcome
                    + " · очередь " + FormatSpan(a.QueueWait)
                    + ", Dolphin " + FormatSpan(a.DolphinStart)
                    + ", YouTube " + FormatSpan(a.YoutubeConfirm)
                    + ", файл " + FormatSpan(a.FileTransfer)
                    + ", create " + FormatSpan(a.VideoCreated)
                    + ", meta " + FormatSpan(a.Metadata)
                    + ", превью " + FormatSpan(a.Thumbnail)
                    + ", всего " + FormatSpan(a.Total));
            }
            log(FormatSummary());
        }

        static string FormatSpan(TimeSpan t) {
            if (t <= TimeSpan.Zero) return "—";
            if (t.TotalMinutes >= 1) return Math.Round(t.TotalMinutes, 1) + " мин";
            return Math.Round(t.TotalSeconds, 0) + " сек";
        }
    }
}
