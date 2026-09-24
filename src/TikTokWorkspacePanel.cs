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

        readonly Label marketHint, modeHint;

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

            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Controls.Add(root);



            marketRu = Theme.MakeToggle("RU", marketView == "RU", () => SwitchMarket("RU"));

            marketEn = Theme.MakeToggle("EN", marketView == "EN", () => SwitchMarket("EN"));



            marketHint = new Label { AutoSize = true, ForeColor = Theme.TextPrimary, Font = Theme.FontCardTitle, Text = "TikTok" };

            var topStack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background };

            topStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            topStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));



            var headerBar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Theme.Background, Margin = new Padding(0, 0, 0, 8) };

            headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var headerActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Background };

            headerActions.Controls.Add(Theme.MakeButton("Добавить видео", accent: true, action: AddVideos));

            headerActions.Controls.Add(Theme.MakeMenuButton("Ещё",

                ("+ Аккаунт", AddAccount),

                ("Из YouTube", SyncProfilesFromYouTubeMenu),

                ("Проверить IP", () => { _ = RunCheck(); }),

                ("Открыть лог", OpenLog)));

            headerBar.Controls.Add(marketHint, 0, 0);

            headerBar.Controls.Add(headerActions, 1, 0);



            var filterCard = Theme.MakeFilterCard();

            filterCard.Dock = DockStyle.Fill;

            var filterRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Theme.Card, Dock = DockStyle.Top };

            filterRow.Controls.Add(Theme.MakeSegmentGroup("Рынок", marketRu, marketEn));



            accountSearch = Theme.MakeSearchBox();

            accountSearch.Width = 200;

            accountSearch.Margin = new Padding(0, 2, 0, 0);

            accountSearch.TextChanged += (s, e) => RefreshGrid(skipSync: true);

            var searchWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(4, 0, 0, 0), BackColor = Theme.Card };

            searchWrap.Controls.Add(new Label { Text = "Поиск", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 7, 8, 0), Font = Theme.FontSmall });

            searchWrap.Controls.Add(accountSearch);

            filterRow.Controls.Add(searchWrap);

            filterCard.Controls.Add(filterRow);



            topStack.Controls.Add(headerBar, 0, 0);

            topStack.Controls.Add(filterCard, 0, 1);

            root.Controls.Add(topStack, 0, 0);



            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(0, 4, 0, 0) };

            grid = new DataGridView {

                Dock = DockStyle.Fill,

                AllowUserToAddRows = false,

                SelectionMode = DataGridViewSelectionMode.FullRowSelect,

                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill

            };

            Theme.StyleGrid(grid);

            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ColorFromHex("#282828");

            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "", Width = 32, FillWeight = 20 });

            grid.Columns.Add("account", "Аккаунт");

            grid.Columns["account"].FillWeight = 140;

            grid.Columns.Add("market", "Рынок");

            grid.Columns["market"].FillWeight = 50;

            grid.Columns.Add("profile", "Profile ID");

            grid.Columns["profile"].FillWeight = 80;

            grid.Columns["account"].ReadOnly = true;

            grid.Columns["profile"].ReadOnly = true;

            grid.Columns.Add("ip", "IP");

            grid.Columns["ip"].FillWeight = 70;

            grid.Columns.Add("files", "Файлы");

            grid.Columns["files"].FillWeight = 60;

            grid.Columns.Add("caption", "Подпись TikTok");

            grid.Columns["caption"].FillWeight = 180;

            grid.Columns.Add("status", "Статус");

            grid.Columns["status"].FillWeight = 100;

            grid.CellValueChanged += (s, e) => { if (e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "on") SyncEnabledFromGrid(); };

            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

            body.Controls.Add(grid);

            root.Controls.Add(body, 0, 1);



            log = Theme.MakeLogBox();

            root.Controls.Add(Theme.MakeLogSection(log), 0, 2);



            var stopBtn = Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop());

            stopBtn.ForeColor = Theme.Warning;

            modeHint = new Label { AutoSize = true, ForeColor = Theme.TextMuted, Text = "Режим: HTTP (beta) · Studio резерв", Margin = new Padding(8, 10, 8, 0) };

            var bottom = Theme.MakeBottomBar(

                (Theme.MakeButton("Быстрая загрузка (HTTP beta)", accent: true, action: async () => await RunUpload("http")), true),

                (Theme.MakeButton("Через Studio", accent: false, action: async () => await RunUpload("studio")), false),

                (stopBtn, false));

            bottom.Controls.Add(modeHint);

            root.Controls.Add(bottom, 0, 3);



            backend.LogLine += AppendLog;

            backend.SetMarketView(marketView);

            SyncProfilesFromYouTube(silent: true);

            RefreshGrid(skipSync: true);

        }



        static string NormMarket(string m) { return (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU"; }



        void AddAccount() {

            if (!NewAccountDialog.TryShow(FindForm(), "TikTok · " + marketView, out var name, out var profileId)) return;

            if (settings.TikTokAccounts == null) settings.TikTokAccounts = new List<TikTokAccount>();

            if (settings.TikTokAccounts.Any(acc => acc != null && string.Equals((acc.ProfileId ?? "").Trim(), profileId, StringComparison.OrdinalIgnoreCase))) {

                MessageBox.Show(this, "Этот Profile ID уже добавлен в TikTok.", "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return;

            }

            var account = new TikTokAccount { Enabled = true, Name = name, ProfileId = profileId, Market = marketView, Status = "Готов" };

            settings.TikTokAccounts.Add(account);

            try { Store.Save(settings); backend.Reload(); accountSearch.Text = ""; RefreshGrid();

                foreach (DataGridViewRow row in grid.Rows) if (ReferenceEquals(row.Tag, account)) { grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells["account"]; break; }

            } catch (Exception ex) { settings.TikTokAccounts.Remove(account); MessageBox.Show(this, ex.Message, "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Error); }

        }



        public void OnNavigated() {

            ApplyNavigationContext();

            SyncProfilesFromYouTube(silent: true);

            RefreshGrid(skipSync: true);

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

            backend.SetMarketView(marketView);

            try { Store.Save(settings); } catch { }

            marketRu.BackColor = marketView == "RU" ? Theme.Accent : Theme.Elevated;

            marketEn.BackColor = marketView == "EN" ? Theme.Accent : Theme.Elevated;

            SyncProfilesFromYouTube(silent: true);

            RefreshGrid(skipSync: true);

        }



        void SyncProfilesFromYouTubeMenu() {

            var r = SyncProfilesFromYouTube(silent: false);

            RefreshGrid(skipSync: true);

            if (r.Linked + r.Imported == 0)

                MessageBox.Show(this, "На рынке " + (marketView == "EN" ? "English" : "Русские") + " нет новых профилей из YouTube (или у YouTube-каналов нет Profile ID).", "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information);

        }



        TikTokSyncResult SyncProfilesFromYouTube(bool silent) {

            var r = TikTokProfileSync.SyncFromYouTube(settings, marketView);

            if (r.Linked + r.Imported + r.RemovedEmpty > 0) {

                try { Store.Save(settings); backend.Reload(); } catch { }

                if (!silent)

                    AppendLog("YouTube→TikTok: привязано " + r.Linked + ", новых " + r.Imported + (r.RemovedEmpty > 0 ? ", убрано пустых " + r.RemovedEmpty : "") + ".");

            }

            return r;

        }



        public void RefreshGrid(bool skipSync = false) {

            if (!skipSync) SyncProfilesFromYouTube(silent: true);

            grid.Rows.Clear();

            string q = (accountSearch.Text ?? "").Trim().ToLowerInvariant();

            int n = 0;

            foreach (var acc in settings.TikTokAccounts ?? new List<TikTokAccount>()) {

                if (acc == null || NormMarket(acc.Market) != marketView) continue;

                if (!string.IsNullOrEmpty(q) && (acc.Name ?? "").ToLowerInvariant().IndexOf(q) < 0 && (acc.ProfileId ?? "").ToLowerInvariant().IndexOf(q) < 0) continue;

                int files = acc.Items?.Count(i => !string.IsNullOrWhiteSpace(i.Video)) ?? 0;

                string caption = acc.Items?.FirstOrDefault()?.Caption ?? acc.Caption ?? "";

                string status = acc.Status ?? "Готов";

                if (string.IsNullOrWhiteSpace(acc.ProfileId) && status.IndexOf("готов", StringComparison.OrdinalIgnoreCase) >= 0)

                    status = "Нужен Profile ID";

                int ri = grid.Rows.Add(acc.Enabled, acc.Name, acc.Market, acc.ProfileId, acc.ExpectedIp,

                    files > 0 ? files + " файлов" : "—", caption, status);

                grid.Rows[ri].Tag = acc;

                grid.Rows[ri].Cells["caption"].ToolTipText = string.Join(Environment.NewLine + Environment.NewLine,

                    (acc.Items ?? new List<TikTokItem>()).Select((it, i) => (i + 1) + ". " + (it?.Caption ?? "")));

                n++;

            }

            marketHint.Text = "TikTok  ·  " + (marketView == "EN" ? "English" : "Русские") + "  ·  " + n + " аккаунтов";

        }



        void SyncEnabledFromGrid() {

            grid.EndEdit();

            foreach (DataGridViewRow row in grid.Rows) {

                if (row.Tag is TikTokAccount acc)

                    acc.Enabled = Convert.ToBoolean(row.Cells["on"].Value ?? false);

            }

            try { Store.Save(settings); } catch { }

        }



        void SyncWorkspaceToBackend() {

            SyncEnabledFromGrid();

            backend.SetMarketView(marketView);

            backend.Reload();

        }



        TikTokAccount SelectedAccount() {

            if (grid.CurrentRow?.Tag is TikTokAccount a) return a;

            return grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Selected && r.Tag is TikTokAccount)?.Tag as TikTokAccount;

        }



        List<TikTokAccount> GetCheckedAccounts() {

            grid.EndEdit();

            return grid.Rows.Cast<DataGridViewRow>()

                .Where(r => r.Tag is TikTokAccount && Convert.ToBoolean(r.Cells["on"].Value ?? false))

                .Select(r => (TikTokAccount)r.Tag)

                .ToList();

        }



        void AddVideos() {

            var acc = SelectedAccount();

            if (acc == null) { MessageBox.Show(this, "Выберите аккаунт в таблице.", "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            SyncProfilesFromYouTube(silent: true);

            RefreshGrid(skipSync: true);

            acc = settings.TikTokAccounts?.FirstOrDefault(a => ReferenceEquals(a, acc))

                ?? settings.TikTokAccounts?.FirstOrDefault(a => a != null && string.Equals(a.Name, acc.Name, StringComparison.OrdinalIgnoreCase) && NormMarket(a.Market) == marketView)

                ?? acc;

            if (string.IsNullOrWhiteSpace(acc.ProfileId)) {

                MessageBox.Show(this, "У «" + acc.Name + "» нет Profile ID.\n\nЕщё → «Из YouTube» подтянет те же профили Dolphin, что и на YouTube.", "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return;

            }

            using (var d = new OpenFileDialog { Multiselect = true, Filter = "Видео|*.mp4;*.mov;*.mkv;*.webm|Все|*.*" }) {

                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;

                try {

                    SyncWorkspaceToBackend();

                    backend.AssignVideosToProfile(acc.ProfileId, marketView, d.FileNames);

                    foreach (DataGridViewRow row in grid.Rows)

                        if (ReferenceEquals(row.Tag, acc) || (row.Tag is TikTokAccount a && string.Equals((a.ProfileId ?? "").Trim(), acc.ProfileId, StringComparison.OrdinalIgnoreCase)))

                            row.Cells["on"].Value = true;

                    SyncEnabledFromGrid();

                    RefreshGrid(skipSync: true);

                    AppendLog("Добавлено видео: " + d.FileNames.Length + " → " + acc.Name);

                } catch (Exception ex) { MessageBox.Show(this, ex.Message, "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

            }

        }



        async Task RunUpload(string transport) {

            transport = (transport ?? "http").Trim().ToLowerInvariant();

            if (transport != "studio") transport = "http";

            if (modeHint != null) modeHint.Text = transport == "studio" ? "Режим: Studio" : "Режим: HTTP (beta)";

            var checkedAccounts = GetCheckedAccounts();

            if (checkedAccounts.Count == 0) {

                MessageBox.Show(this,

                    "Отметьте галочкой ✓ аккаунты для загрузки.\n\n" +

                    "Ещё → «Из YouTube» — подтянуть Profile ID из YouTube (те же профили Dolphin).",

                    "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return;

            }

            try {

                SyncProfilesFromYouTube(silent: true);

                SyncWorkspaceToBackend();

                if (transport == "studio")

                    await backend.RunUploadStudioAsync(checkedAccounts);

                else

                    await backend.RunUploadHttpAsync(checkedAccounts);

                RefreshGrid(skipSync: true);

            } catch (Exception ex) {

                AppendLog("ОШИБКА: " + ex.Message);

                if (modeHint != null && transport == "http") {

                    string reason = ex.Message.Length > 80 ? ex.Message.Substring(0, 80) + "…" : ex.Message;

                    modeHint.Text = "HTTP недоступен — " + reason;

                }

            }

        }



        async Task RunCheck() {

            var checkedAccounts = GetCheckedAccounts();

            if (checkedAccounts.Count == 0) {

                var sel = SelectedAccount();

                if (sel != null) checkedAccounts = new List<TikTokAccount> { sel };

            }

            if (checkedAccounts.Count == 0) {

                MessageBox.Show(this, "Отметьте галочкой аккаунты или выберите строку для проверки IP.", "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return;

            }

            try {

                SyncProfilesFromYouTube(silent: true);

                SyncWorkspaceToBackend();

                foreach (var acc in checkedAccounts) {

                    if (!string.IsNullOrWhiteSpace(acc.ProfileId))

                        backend.SelectProfileById(acc.ProfileId, marketView);

                }

                await backend.RunCheckProfilesAsync();

                RefreshGrid(skipSync: true);

            } catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }

        }



        void OpenLog() {

            try {

                string f = backend.Window.CurrentLogFile;

                if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) Ui.Open(f);

                else MessageBox.Show(this, "Лог ещё не создан.", "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information);

            } catch (Exception ex) { MessageBox.Show(this, ex.Message); }

        }



        void AppendLog(string line) {

            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), line); return; }

            log.AppendText(line + Environment.NewLine);

            log.ScrollToCaret();

        }

    }

}


