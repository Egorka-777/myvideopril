using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class SchedulePreviewDialog : Form {
        public SchedulePreviewDialog(IList<PreparedProfileBatch> batches) {
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
            grid.Columns.Add("date", "Дата");
            grid.Columns.Add("time", "Время");
            grid.Columns.Add("gap", "Δ мин");

            int n = 0;
            DateTime? prev = null;
            foreach (var batch in batches ?? new List<PreparedProfileBatch>()) {
                foreach (var it in batch.Items) {
                    n++;
                    DateTime at;
                    try { at = DateTime.Parse(it.ScheduleDate + " " + it.ScheduleTime); } catch { at = DateTime.Now; }
                    int gap = prev.HasValue ? (int)Math.Round((at - prev.Value).TotalMinutes) : 0;
                    prev = at;
                    grid.Rows.Add(n, Path.GetFileName(it.StagedVideo ?? it.Source?.Video ?? ""), it.UploadTitle,
                        batch.Channel?.Name ?? "", it.ScheduleDate, it.ScheduleTime, gap > 0 ? gap.ToString() : "—");
                }
            }

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(12) };
            var ok = Theme.MakeButton("Запустить", accent: true, action: () => { DialogResult = DialogResult.OK; Close(); });
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
