using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class TasksPanel : UserControl {
        readonly Preferences settings;
        readonly DataGridView grid;
        readonly TextBox search;

        public TasksPanel(Preferences prefs) {
            settings = prefs;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            search = Theme.MakeSearchBox();
            search.Width = 200;
            search.TextChanged += (s, e) => RefreshData();
            top.Controls.Add(search);
            top.Controls.Add(Theme.MakeButton("Обновить", ghost: true, action: RefreshData));
            top.Controls.Add(Theme.MakeButton("Открыть диагностику", ghost: true, action: OpenDiagnostics));
            Controls.Add(top);

            grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            Theme.StyleGrid(grid);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ColorFromHex("#282828");
            grid.Columns.Add("id", "ID");
            grid.Columns.Add("platform", "Платформа");
            grid.Columns.Add("account", "Аккаунт");
            grid.Columns.Add("file", "Файл");
            grid.Columns.Add("method", "Способ");
            grid.Columns.Add("scheduled", "Публикация");
            grid.Columns.Add("progress", "Прогресс");
            grid.Columns.Add("stage", "Этап");
            grid.Columns.Add("status", "Статус");
            grid.Columns.Add("result", "Результат");
            Controls.Add(grid);
            TaskQueueStore.Changed += () => { if (!IsDisposed) BeginInvoke(new Action(RefreshData)); };
            RefreshData();
        }

        public void ApplySearch(string q) {
            if (q != null && search.Text != q) search.Text = q;
            RefreshData();
        }

        public void RefreshData() {
            grid.Rows.Clear();
            string q = (search.Text ?? "").Trim().ToLowerInvariant();
            foreach (var t in TaskQueueStore.Load().OrderByDescending(x => x.UpdatedAt)) {
                if (!string.IsNullOrEmpty(q)) {
                    string blob = ((t.Account ?? "") + " " + (t.File ?? "") + " " + (t.Id ?? "")).ToLowerInvariant();
                    if (!blob.Contains(q)) continue;
                }
                int ri = grid.Rows.Add(t.Id, t.Platform, t.Account, Path.GetFileName(t.File ?? ""), t.UploadMethod,
                    t.ScheduledAt, t.Progress.ToString("0") + "%", t.Stage, t.Status, t.Result);
                grid.Rows[ri].Tag = t;
            }
        }

        void OpenDiagnostics() {
            string dir = Path.Combine(Store.Root, "diagnostics");
            Directory.CreateDirectory(dir);
            try { Ui.Open(dir); } catch (Exception e) { MessageBox.Show(e.Message); }
        }
    }

    public sealed class LogsPanel : UserControl {
        readonly Preferences settings;
        readonly ListBox logList;
        readonly ComboBox levelFilter, platformFilter;
        readonly TextBox accountFilter, taskFilter;

        public LogsPanel(Preferences prefs) {
            settings = prefs;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            accountFilter = Theme.MakeSearchBox();
            accountFilter.Width = 120;
            taskFilter = Theme.MakeSearchBox();
            taskFilter.Width = 120;
            platformFilter = Theme.MakeCombo(new[] { "Все", "YouTube", "TikTok" });
            levelFilter = Theme.MakeCombo(new[] { "Все", "info", "warning", "error" });
            filters.Controls.Add(new Label { Text = "Аккаунт", AutoSize = true, ForeColor = Theme.TextSecondary });
            filters.Controls.Add(accountFilter);
            filters.Controls.Add(new Label { Text = "Задача", AutoSize = true, ForeColor = Theme.TextSecondary });
            filters.Controls.Add(taskFilter);
            filters.Controls.Add(platformFilter);
            filters.Controls.Add(levelFilter);
            filters.Controls.Add(Theme.MakeButton("Обновить", ghost: true, action: RefreshData));
            filters.Controls.Add(Theme.MakeButton("Папка диагностики", ghost: true, action: () => {
                string dir = Path.Combine(Store.Root, "diagnostics");
                Directory.CreateDirectory(dir);
                Ui.Open(dir);
            }));
            filters.Controls.Add(Theme.MakeButton("Очистить", ghost: true, action: () => { logList.Items.Clear(); }));
            Controls.Add(filters);

            logList = new ListBox { Dock = DockStyle.Fill, BackColor = Theme.Card, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f) };
            Controls.Add(logList);
            RefreshData();
        }

        public void RefreshData() {
            logList.Items.Clear();
            string acc = (accountFilter.Text ?? "").Trim().ToLowerInvariant();
            string task = (taskFilter.Text ?? "").Trim().ToLowerInvariant();
            string platform = platformFilter.SelectedItem?.ToString() ?? "Все";

            foreach (var ev in TaskQueueStore.RecentEvents(100)) {
                if (!string.IsNullOrEmpty(acc) && (ev.Account ?? "").ToLowerInvariant().IndexOf(acc) < 0) continue;
                if (!platform.StartsWith("Все") && (ev.Action ?? "").IndexOf(platform, StringComparison.OrdinalIgnoreCase) < 0) continue;
                logList.Items.Add(string.Format("[{0}] {1} · {2} → {3} {4}", ev.Time, ev.Account, ev.Action, ev.Result, ev.Url));
            }

            string diagDir = Path.Combine(Store.Root, "diagnostics");
            if (Directory.Exists(diagDir)) {
                foreach (var file in Directory.GetFiles(diagDir, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(20)) {
                    try {
                        foreach (var line in File.ReadLines(file).Take(50)) {
                            if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("manual_check", StringComparison.OrdinalIgnoreCase) >= 0)
                                logList.Items.Add(Path.GetFileName(file) + ": " + line.Substring(0, Math.Min(180, line.Length)));
                        }
                    } catch { }
                }
            }
        }
    }
}
