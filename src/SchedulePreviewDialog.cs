using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class SchedulePreviewDialog : Form {
        public SchedulePreviewDialog(IList<PreparedProfileBatch> batches, Preferences prefs=null, bool http=false, bool previewOnly=false) {
            Text = "Предпросмотр расписания";
            ClientSize = new Size(920, 520);
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.FontBody;
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            MinimumSize = new Size(760, 400);

            var grid = new DataGridView {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            Theme.StyleGrid(grid);
            grid.Columns.Add("num", "#");
            grid.Columns.Add("file", "Файл");
            grid.Columns.Add("title", "Заголовок");
            grid.Columns.Add("account", "Аккаунт");
            grid.Columns.Add("mode", "Режим");
            grid.Columns.Add("date", "Дата ПК");
            grid.Columns.Add("time", "Время ПК");
            grid.Columns.Add("gap", "Δ мин");

            int n = 0;
            DateTime? prev = null;
            foreach (var batch in batches ?? new List<PreparedProfileBatch>()) {
                foreach (var it in batch.Items) {
                    n++;
                    string mode=http?HttpWorkerSettings.ResolvePublishMode(prefs,it.Kind??batch.Channel?.Kind):"scheduled";
                    DateTime at=DateTime.MinValue;
                    int gap=0;
                    if(mode=="scheduled"){
                        if(!ScheduleGenerator.TryParseSlot(it.ScheduleDate,it.ScheduleTime,out at))
                            throw new Exception("Нет времени публикации для «"+it.UploadTitle+"».");
                        gap=prev.HasValue?(int)Math.Round((at-prev.Value).TotalMinutes):0;
                        prev=at;
                    }
                    grid.Rows.Add(n, Path.GetFileName(it.StagedVideo ?? it.Source?.Video ?? ""), it.UploadTitle,
                        batch.Channel?.Name ?? "", mode=="immediate"?"Сразу":mode=="private"?"Приватное":"Отложено",
                        mode=="scheduled"?it.ScheduleDate:"—",mode=="scheduled"?it.ScheduleTime:"—",gap>0?gap.ToString():"—");
                }
            }

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12) };
            TimeSpan offset=TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            string sign=offset<TimeSpan.Zero?"-":"+";
            var note=new Label { AutoSize=true,ForeColor=Theme.TextSecondary,Margin=new Padding(8,8,14,4),
                Text="Время этого ПК: "+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" (UTC"+sign+offset.Duration().ToString(@"hh\:mm")+"). Страна канала не меняет время слота." };
            bottom.Controls.Add(note);
            var ok = Theme.MakeButton(previewOnly?"Закрыть":"Запустить", accent: true, action: () => { DialogResult = DialogResult.OK; Close(); });
            var cancel = Theme.MakeButton("Отмена", ghost: true, action: () => { DialogResult = DialogResult.Cancel; Close(); });
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            Controls.Add(grid);
            Controls.Add(bottom);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
