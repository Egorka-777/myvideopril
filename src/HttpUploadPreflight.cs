using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoBatch {
    /// <summary>Validate HTTP upload batch before any Dolphin profile starts.</summary>
    public static class HttpUploadPreflight {
        public static List<string> Validate(IList<PreparedProfileBatch> batches, Preferences settings) {
            var problems = new List<string>();
            if (batches == null || batches.Count == 0) {
                problems.Add("Нет подготовленных пакетов для HTTP-загрузки.");
                return problems;
            }
            try { HttpUploadRunner.CheckFiles(); }
            catch (Exception ex) { problems.Add("HTTP-worker: " + ex.Message); }

            if (!File.Exists(DolphinRunner.Node))
                problems.Add("Не найден node.exe: " + DolphinRunner.Node);

            var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in batches) {
                string account = batch?.Channel?.Name ?? "(без имени)";
                string pid = (batch?.ProfileId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(pid))
                    problems.Add(account + ": не указан Profile ID Dolphin.");
                else if (!profileIds.Add(pid))
                    problems.Add(account + ": дубликат Profile ID «" + pid + "» в одной HTTP-пачке.");

                if (batch?.Items == null || batch.Items.Count == 0) {
                    problems.Add(account + " [" + pid + "]: пустой список видео.");
                    continue;
                }
                foreach (var it in batch.Items) {
                    string ctx = account + " [" + pid + "] [" + it.Index + "/" + it.Total + "]";
                    if (string.IsNullOrWhiteSpace(it.StagedVideo) || !File.Exists(it.StagedVideo))
                        problems.Add(ctx + ": файл не найден — " + (it.StagedVideo ?? ""));
                    string title = (it.UploadTitle ?? "").Trim();
                    if (title.Length < 1)
                        problems.Add(ctx + ": пустой заголовок.");
                    else if (title.Length > 100)
                        problems.Add(ctx + ": заголовок длиннее 100 символов (" + title.Length + ").");
                    if (title.Contains("<") || title.Contains(">"))
                        problems.Add(ctx + ": заголовок содержит < или >.");
                    if (!string.IsNullOrWhiteSpace(it.Source?.Thumbnail) && !File.Exists(it.Source.Thumbnail))
                        problems.Add(ctx + ": файл превью не найден — " + it.Source.Thumbnail);

                    if (HttpWorkerSettings.ResolvePublishMode(settings, it.Kind ?? batch.Channel?.Kind) != "scheduled") continue;

                    if (!ScheduleGenerator.TryParseSlot(it.ScheduleDate, it.ScheduleTime, out var at)) {
                        problems.Add(ctx + ": некорректное расписание «" + it.ScheduleDate + " " + it.ScheduleTime + "».");
                        continue;
                    }
                    if (at <= DateTime.Now.AddMinutes(ScheduleGenerator.PreflightMinLeadMinutes))
                        problems.Add(ctx + ": расписание слишком рано (" + it.ScheduleDate + " " + it.ScheduleTime
                            + ", нужно минимум через " + ScheduleGenerator.PreflightMinLeadMinutes + " мин.).");
                }
            }
            return problems;
        }
    }
}
