using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class TikTokWorkspacePanel : UserControl {
        readonly Preferences settings;
        readonly TikTokBackend backend;
        readonly DataGridView grid;
        readonly RichTextBox log;
        readonly TextBox accountSearch;
        readonly Label marketHint;
        readonly Button marketRu, marketEn;
        string marketView = "RU";

        public TikTokWorkspacePanel(Preferences prefs, TikTokBackend be) {
            settings = prefs;
            backend = be;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            marketView = NormMarket(settings.TikTokMarketView);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var top = Theme.MakeToolbar();
            marketRu = Theme.MakeToggle("RU", marketView == "RU", () => SwitchMarket("RU"));
            marketEn = Theme.MakeToggle("EN", marketView == "EN", () => SwitchMarket("EN"));
            marketHint = new Label { AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(12, 8, 0, 0) };
            accountSearch = Theme.MakeSearchBox();
            accountSearch.Width = 160;
            accountSearch.TextChanged += (s, e) => RefreshGrid();
            top.Controls.Add(marketRu);
            top.Controls.Add(marketEn);
            top.Controls.Add(marketHint);
            top.Controls.Add(new Label { Text = "Поиск", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(12, 8, 6, 0) });
            top.Controls.Add(accountSearch);
            top.Controls.Add(Theme.MakeButton("Добавить видео", accent: true, action: AddVideos));
            top.Controls.Add(Theme.MakeButton("Проверить", ghost: true, action: async () => await RunCheck()));
            root.Controls.Add(top, 0, 0);

            grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            Theme.StyleGrid(grid);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ColorFromHex("#282828");
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "", Width = 28 });
            grid.Columns.Add("account", "Аккаунт");
            grid.Columns.Add("market", "Рынок");
            grid.Columns.Add("profile", "Profile ID");
            grid.Columns.Add("ip", "IP");
            grid.Columns.Add("files", "Файлы");
            grid.Columns.Add("title", "Заголовок");
            grid.Columns.Add("desc", "Описание");
            grid.Columns.Add("status", "Статус");
            root.Controls.Add(grid, 0, 1);

            log = Theme.MakeLogBox();
            root.Controls.Add(log, 0, 2);

            var bottom = Theme.MakeToolbar();
            bottom.Controls.Add(Theme.MakeButton("Загрузить", accent: true, action: async () => await RunUpload()));
            bottom.Controls.Add(Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop()));
            bottom.Controls.Add(Theme.MakeButton("Лог", ghost: true, action: OpenLog));
            root.Controls.Add(bottom, 0, 3);

            backend.LogLine += AppendLog;
            RefreshGrid();
        }

        static string NormMarket(string m) { return (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU"; }

        public void OnNavigated() {
            ApplyNavigationContext();
            RefreshGrid();
        }

        void ApplyNavigationContext() {
            string pid = (NavigationContext.SelectedProfileId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(pid) || !string.Equals(NavigationContext.SelectedPlatform, "TikTok", StringComparison.OrdinalIgnoreCase)) return;
            marketView = NormMarket(NavigationContext.SelectedMarket);
            backend.SetMarketView(marketView);
            backend.SelectProfileById(pid, marketView);
            marketRu.BackColor = marketView == "RU" ? Theme.Accent : Theme.Elevated;
            marketEn.BackColor = marketView == "EN" ? Theme.Accent : Theme.Elevated;
            for (int i = 0; i < grid.Rows.Count; i++) {
                if (grid.Rows[i].Tag is TikTokAccount acc && string.Equals((acc.ProfileId ?? "").Trim(), pid, StringComparison.OrdinalIgnoreCase)) {
                    grid.ClearSelection();
                    grid.Rows[i].Selected = true;
                    break;
                }
            }
        }

        void SwitchMarket(string m) {
            marketView = NormMarket(m);
            settings.TikTokMarketView = marketView;
            try { Store.Save(settings); } catch { }
            marketRu.BackColor = marketView == "RU" ? Theme.Accent : Theme.Elevated;
            marketEn.BackColor = marketView == "EN" ? Theme.Accent : Theme.Elevated;
            RefreshGrid();
        }

        public void RefreshGrid() {
            grid.Rows.Clear();
            string q = (accountSearch.Text ?? "").Trim().ToLowerInvariant();
            int n = 0;
            foreach (var acc in settings.TikTokAccounts ?? new List<TikTokAccount>()) {
                if (acc == null || NormMarket(acc.Market) != marketView) continue;
                if (!string.IsNullOrEmpty(q) && (acc.Name ?? "").ToLowerInvariant().IndexOf(q) < 0) continue;
                int files = acc.Items?.Count(i => !string.IsNullOrWhiteSpace(i.Video)) ?? 0;
                string title = acc.Items?.FirstOrDefault()?.Title ?? acc.Title ?? "";
                string desc = acc.Items?.FirstOrDefault()?.Description ?? acc.Description ?? "";
                int ri = grid.Rows.Add(acc.Enabled, acc.Name, acc.Market, acc.ProfileId, acc.ExpectedIp,
                    files > 0 ? files.ToString() : "—", title, desc, acc.Status ?? "Готов");
                grid.Rows[ri].Tag = acc;
                n++;
            }
            marketHint.Text = (marketView == "EN" ? "English" : "Русские") + " · аккаунтов: " + n;
        }

        TikTokAccount SelectedAccount() {
            if (grid.CurrentRow?.Tag is TikTokAccount a) return a;
            return grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Selected && r.Tag is TikTokAccount)?.Tag as TikTokAccount;
        }

        void AddVideos() {
            var acc = SelectedAccount();
            if (acc == null) { MessageBox.Show(this, "Выберите аккаунт.", "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (var d = new OpenFileDialog { Multiselect = true, Filter = "Видео|*.mp4;*.mov;*.mkv;*.webm|Все|*.*" }) {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try {
                    backend.AssignVideosToProfile(acc.ProfileId, marketView, d.FileNames);
                    RefreshGrid();
                } catch (Exception ex) { MessageBox.Show(this, ex.Message, "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        async Task RunUpload() {
            try { backend.Reload(); var a = SelectedAccount(); if (a != null) backend.SelectProfileById(a.ProfileId, marketView); await backend.RunUploadAsync(); RefreshGrid(); }
            catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
        }

        async Task RunCheck() {
            try { backend.Reload(); await backend.RunCheckProfilesAsync(); RefreshGrid(); }
            catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
        }

        void OpenLog() {
            try {
                string f = backend.Window.CurrentLogFile;
                if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) Ui.Open(f);
            } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
        }

        void AppendLog(string line) {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), line); return; }
            log.AppendText(line + Environment.NewLine);
            log.ScrollToCaret();
        }
    }
}
