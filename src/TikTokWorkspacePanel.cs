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

        readonly ComboBox statusFilter;

        readonly Panel accountBadge;

        readonly HashSet<string> preparationKeys = new HashSet<string>();
        string marketView = "RU";

        string statusFilterValue = "Все статусы";



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



            marketRu = Theme.MakeHeroToggle("RU", marketView == "RU", () => SwitchMarket("RU"));

            marketEn = Theme.MakeHeroToggle("EN", marketView == "EN", () => SwitchMarket("EN"));

            statusFilter = Theme.MakeCombo(new[] { "Все статусы", "Готов", "Ошибка", "Нужна сверка", "Импорт из YouTube" });

            statusFilter.SelectedIndexChanged += (s, e) => {

                statusFilterValue = statusFilter.SelectedItem?.ToString() ?? "Все статусы";

                RefreshGrid(skipSync: true);

            };

            accountBadge = Theme.MakeCountBadge("0 аккаунтов");



            marketHint = new Label { AutoSize = true, ForeColor = Theme.TextPrimary, Font = Theme.FontPageTitle, Text = "TikTok" };

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

                ("Сверить загрузку", () => ReconcileCheckedOrSelected()),
                ("Снять все галочки", ClearAllChecks),
                ("Цветокоррекция подготовки…", () => UploadPreparation.ToggleEffects(FindForm(), settings)),
                ("Открыть лог", OpenLog)));

            headerBar.Controls.Add(marketHint, 0, 0);

            headerBar.Controls.Add(headerActions, 1, 0);



            var contextCard = Theme.MakeFilterCard();

            contextCard.Padding = new Padding(16, 14, 16, 14);

            contextCard.Dock = DockStyle.Fill;

            var contextRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Card, Margin = Padding.Empty };

            contextRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            contextRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var switches = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 2, 0, 0) };

            switches.Controls.Add(Theme.MakeHeroSegmentGroup("Рынок", marketRu, marketEn));

            var meta = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, BackColor = Theme.Card, Margin = new Padding(0, 2, 0, 0) };

            meta.Controls.Add(Theme.MakeChip("Shorts · Dolphin"));

            meta.Controls.Add(accountBadge);

            contextRow.Controls.Add(switches, 0, 0);

            contextRow.Controls.Add(meta, 1, 0);

            accountSearch = Theme.MakeSearchBox();

            accountSearch.Width = 220;

            accountSearch.Margin = new Padding(0, 2, 0, 0);

            accountSearch.TextChanged += (s, e) => RefreshGrid(skipSync: true);

            var filterRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Theme.Card, Dock = DockStyle.Top, Margin = new Padding(0, 10, 0, 0) };

            filterRow.Controls.Add(new Label { Text = "Поиск", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 7, 8, 0), Font = Theme.FontSmall });

            filterRow.Controls.Add(accountSearch);

            filterRow.Controls.Add(new Label { Text = "Статус", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(16, 7, 8, 0), Font = Theme.FontSmall });

            statusFilter.Width = 160;

            statusFilter.Margin = new Padding(0, 2, 0, 0);

            filterRow.Controls.Add(statusFilter);

            contextCard.Controls.Add(contextRow);

            contextCard.Controls.Add(filterRow);



            topStack.Controls.Add(headerBar, 0, 0);

            topStack.Controls.Add(contextCard, 0, 1);

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

            var onCol = new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "✓", Width = 40, MinimumWidth = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, FillWeight = 20 };

            onCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            grid.Columns.Add(onCol);

            grid.Columns.Add("account", "Аккаунт");

            grid.Columns["account"].FillWeight = 160;

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
            grid.CellPainting += (sender, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["status"].Index && grid.Rows[e.RowIndex].Tag is TikTokAccount a) UploadPreparation.PaintProgress(e, UploadPreparation.TikTokKey(a)); };
            Disposed += (sender, e) => { foreach (var key in preparationKeys.ToArray()) UploadPreparation.Cancel(key); };

            grid.CellValueChanged += (s, e) => { if (e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "on") SyncEnabledFromGrid(); };

            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.MouseDown += (s, e) => {
                if (e.Button != MouseButtons.Right) return;
                var hit = grid.HitTest(e.X, e.Y);
                if (hit.RowIndex < 0 || !(grid.Rows[hit.RowIndex].Tag is TikTokAccount account)) return;
                grid.ClearSelection(); grid.Rows[hit.RowIndex].Selected = true;
                var row = grid.Rows[hit.RowIndex];
                var menu = Theme.MakeContextMenu();
                UploadPreparation.AddSettingsMenu(menu, settings);
                if (UploadPreparation.IsBusy(UploadPreparation.TikTokKey(account))) menu.Items.Add("Отменить подготовку видео", null, (sender, args) => UploadPreparation.Cancel(UploadPreparation.TikTokKey(account)));
                string listMarket = NormMarket(account.Market);
                menu.Items.Add("Снять галочку", null, (sender, args) => { account.Enabled = false; row.Cells["on"].Value = false; SyncEnabledFromGrid(); RefreshGrid(skipSync: true); });
                menu.Items.Add("Сверить загрузку", null, (sender, args) => ReconcileAccount(account));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Убрать из списка " + listMarket, null, (sender, args) => RemoveAccount(account, listMarket));
                menu.Show(grid.PointToScreen(e.Location));
            };

            body.Controls.Add(grid);

            root.Controls.Add(body, 0, 1);



            log = Theme.MakeLogBox();

            root.Controls.Add(Theme.MakeLogSection(log), 0, 2);



            var stopBtn = Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop());

            stopBtn.ForeColor = Theme.Warning;

            modeHint = new Label { AutoSize = true, ForeColor = Theme.TextMuted, Text = "Загрузка через браузер Dolphin", Margin = new Padding(8, 10, 8, 0) };

            var bottom = Theme.MakeBottomBar(

                (Theme.MakeButton("Опубликовать через Dolphin", accent: true, action: async () => await RunUpload("studio")), true),


                (Theme.MakeButton("Обновить статусы",ghost:true,action:async()=>{try{SyncWorkspaceToBackend();await backend.RecheckContentAsync(GetCheckedAccounts());RefreshGrid(skipSync:true);}catch(Exception e){AppendLog(e.Message);}}),false),
                (stopBtn, false));

            bottom.Controls.Add(modeHint);

            root.Controls.Add(bottom, 0, 3);



            backend.LogLine += AppendLog;

            backend.SetMarketView(marketView);

            SyncProfilesFromYouTube(silent: true);

            StyleHeroToggle(marketRu, marketView == "RU");

            StyleHeroToggle(marketEn, marketView == "EN");

            RefreshGrid(skipSync: true);

        }



        static string NormMarket(string m) { return (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU"; }

        static void StyleHeroToggle(Button b, bool active) => Theme.ApplyHeroToggleStyle(b, active);

        static bool AccountNeedsReview(TikTokAccount acc) => acc?.Items != null && acc.Items.Any(it => it != null && it.HttpNeedsReview);

        static string DisplayStatus(TikTokAccount acc) {

            if (AccountNeedsReview(acc)) return "Нужна сверка";

            return (acc?.Status ?? "Готов").Trim();

        }

        static bool StatusMatchesFilter(string displayStatus, string filter) {

            if (string.IsNullOrWhiteSpace(filter) || filter == "Все статусы") return true;

            displayStatus = (displayStatus ?? "").Trim();

            if (filter == "Нужна сверка") return string.Equals(displayStatus, "Нужна сверка", StringComparison.OrdinalIgnoreCase);

            if (filter == "Импорт из YouTube") return displayStatus.IndexOf("импорт", StringComparison.OrdinalIgnoreCase) >= 0 || displayStatus.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0;

            return displayStatus.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        }

        void ReconcileAccount(TikTokAccount account) {

            if (account == null) return;

            try {

                SyncWorkspaceToBackend();

                backend.ReconcileUploads(new List<TikTokAccount> { account });

                RefreshGrid(skipSync: true);

            } catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); MessageBox.Show(this, ex.Message, "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

        }

        void ClearAllChecks() {

            foreach (DataGridViewRow row in grid.Rows) {

                if (row.Tag is TikTokAccount acc) { acc.Enabled = false; row.Cells["on"].Value = false; }

            }

            SyncEnabledFromGrid();

            RefreshGrid(skipSync: true);

        }

        void RemoveAccount(TikTokAccount account, string listMarket) {
            if (account == null || !settings.TikTokAccounts.Contains(account)) return;
            if (backend.Window.IsBusy) { MessageBox.Show(this, "Сначала завершите операцию или нажмите Стоп и дождитесь её завершения.", "TikTok"); return; }
            if (MessageBox.Show(this, "Убрать «" + account.Name + "» из списка TikTok · " + listMarket + "?\nКарточка с данными, другие списки, профиль браузера, публикации и файлы сохранятся. Автоимпорт не вернёт строку в этот список.",
                "Убрать из списка", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { AccountRemoval.RemoveFromList(settings, listMarket, null, new[] { account }); }
            catch (Exception ex) { MessageBox.Show(this, "Аккаунт не удалён: " + ex.Message, "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            backend.Reload(); RefreshGrid(skipSync: true);
            AppendLog("Убран из списка TikTok · " + listMarket + ": «" + account.Name + "».");
        }



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
        public void ShowAccount(TikTokAccount account) {
            if (account == null || !settings.TikTokAccounts.Contains(account)) return;
            NavigationContext.SelectTikTok(account); marketView = NormMarket(account.Market);
            settings.TikTokMarketView = marketView; accountSearch.Text = ""; statusFilterValue = "Все статусы"; statusFilter.SelectedIndex = 0; backend.SetMarketView(marketView);
            StyleHeroToggle(marketRu, marketView == "RU");
            StyleHeroToggle(marketEn, marketView == "EN");
            RefreshGrid(skipSync: true);
            foreach (DataGridViewRow row in grid.Rows) if (ReferenceEquals(row.Tag, account)) {
                grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells["account"]; break;
            }
        }



        void ApplyNavigationContext() {

            string pid = (NavigationContext.SelectedProfileId ?? "").Trim();

            if (string.IsNullOrWhiteSpace(pid) || !string.Equals(NavigationContext.SelectedPlatform, "TikTok", StringComparison.OrdinalIgnoreCase)) return;

            marketView = NormMarket(NavigationContext.SelectedMarket);

            backend.SetMarketView(marketView);

            backend.SelectProfileById(pid, marketView);

            StyleHeroToggle(marketRu, marketView == "RU");

            StyleHeroToggle(marketEn, marketView == "EN");

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

            StyleHeroToggle(marketRu, marketView == "RU");

            StyleHeroToggle(marketEn, marketView == "EN");

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

                string status = DisplayStatus(acc);

                if (!StatusMatchesFilter(status, statusFilterValue)) continue;

                int files = acc.Items?.Count(i => !string.IsNullOrWhiteSpace(i.Video)) ?? 0;

                string caption = acc.Items?.FirstOrDefault()?.Caption ?? acc.Caption ?? "";

                if (string.IsNullOrWhiteSpace(acc.ProfileId) && status.IndexOf("готов", StringComparison.OrdinalIgnoreCase) >= 0)

                    status = "Нужен Profile ID";

                int ri = grid.Rows.Add(acc.Enabled, acc.Name, acc.ProfileId, acc.ExpectedIp,

                    files > 0 ? files + " файлов" : "—", caption, status);

                grid.Rows[ri].Tag = acc;

                grid.Rows[ri].Cells["caption"].ToolTipText = string.Join(Environment.NewLine + Environment.NewLine,

                    (acc.Items ?? new List<TikTokItem>()).Select((it, i) => (i + 1) + ". " + (it?.Caption ?? "")));

                n++;

            }

            marketHint.Text = "TikTok  ·  " + (marketView == "EN" ? "English" : "Русские") + "  ·  " + n + " аккаунтов";

            if (accountBadge != null) Theme.SetBadgeText(accountBadge, n + " акк.");

        }

        void ReconcileCheckedOrSelected() {

            var accounts = GetCheckedAccounts();

            if (accounts.Count == 0) {

                var sel = SelectedAccount();

                if (sel != null) accounts = new List<TikTokAccount> { sel };

            }

            if (accounts.Count == 0) {

                MessageBox.Show(this, "Отметьте аккаунт галочкой или выберите строку для сверки.", "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return;

            }

            try {

                SyncWorkspaceToBackend();

                backend.ReconcileUploads(accounts);

                RefreshGrid(skipSync: true);

            } catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); MessageBox.Show(this, ex.Message, "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

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



        async void AddVideos() {

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

            string preparationKey = UploadPreparation.TikTokKey(acc);
            if (UploadPreparation.IsBusy(preparationKey)) { AppendLog("Для «" + acc.Name + "» уже идёт подготовка видео."); return; }
            using (var d = new OpenFileDialog { Multiselect = true, Filter = "Видео|*.mp4;*.mov;*.mkv;*.webm|Все|*.*" }) {

                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;

                if (acc.Items?.Any(i => i != null && !string.IsNullOrWhiteSpace(i.Video)) == true && MessageBox.Show(FindForm(), "Заменить видео у «" + acc.Name + "» после подготовки новой пачки?", "TikTok", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string taskId = "prepare-" + Guid.NewGuid().ToString("N"); int logged = -1;
                try {
                    SyncWorkspaceToBackend(); preparationKeys.Add(preparationKey);
                    AppendLog(acc.Name + ": подготовка видео…");
                    var progress = new Progress<UploadPreparationProgress>(state => {
                        if (IsDisposed || !UploadPreparation.IsBusy(preparationKey)) return;
                        grid.Invalidate();
                        if (state.Percent / 5 == logged && state.Stage != "queued") return;
                        logged = state.Percent / 5; AppendLog(acc.Name + ": " + state.Caption);
                        AppDiagnostics.Write("TikTok", acc.ProfileId, taskId, "video_prepare", state.Caption + " · " + state.File);
                    });
                    await backend.AssignPreparedVideosToProfileAsync(acc, d.FileNames, progress);

                    foreach (DataGridViewRow row in grid.Rows)

                        if (ReferenceEquals(row.Tag, acc) || (row.Tag is TikTokAccount a && string.Equals((a.ProfileId ?? "").Trim(), acc.ProfileId, StringComparison.OrdinalIgnoreCase)))

                            row.Cells["on"].Value = true;

                    SyncEnabledFromGrid();

                    RefreshGrid(skipSync: true);

                    AppendLog("Добавлено видео: " + d.FileNames.Length + " → " + acc.Name);

                    AppDiagnostics.Write("TikTok", acc.ProfileId, taskId, "video_prepare_done", "Готово · 100%");
                } catch (OperationCanceledException) { if (!IsDisposed) AppendLog(acc.Name + ": подготовка отменена. Исходники сохранены."); }
                catch (Exception ex) { AppDiagnostics.Write("TikTok", acc.ProfileId, taskId, "video_prepare_error", ex.ToString(), "error"); if (!IsDisposed) { AppendLog(acc.Name + ": ошибка подготовки · " + ex.Message); MessageBox.Show(this, ex.Message, "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
                finally { preparationKeys.Remove(preparationKey); if (!IsDisposed) RefreshGrid(skipSync: true); }
            }
        }

        async Task RunUpload(string transport) {

            transport = "studio";



            if (modeHint != null) modeHint.Text = "Загрузка через браузер Dolphin";

            var selectedAccounts = GetCheckedAccounts();
            var checkedAccounts = selectedAccounts.Where(a => !UploadPreparation.IsBusy(UploadPreparation.TikTokKey(a))).ToList();
            if (selectedAccounts.Count != checkedAccounts.Count) AppendLog("Готовящиеся аккаунты пропущены: дождитесь подготовки и запустите их отдельно.");
            if (checkedAccounts.Count == 0) {

                MessageBox.Show(this,

                    "Отметьте галочкой ✓ аккаунты для загрузки.\n\n" +

                    "Вкладка RU/EN — как на YouTube: в списке только аккаунты выбранного рынка.\n\n" +

                    "Ещё → «Из YouTube» — подтянуть Profile ID из YouTube (те же профили Dolphin).",

                    "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return;

            }

            var blocked = checkedAccounts.Where(AccountNeedsReview).ToList();

            var ready = checkedAccounts.Where(a => !AccountNeedsReview(a)).ToList();

            if (blocked.Count > 0) {

                AppendLog("Нужна сверка (пропуск): " + string.Join(", ", blocked.Select(a => a.Name)));

                if (ready.Count == 0) {

                    MessageBox.Show(this,

                        string.Join("\n", blocked.Select(a => "• " + a.Name)) +

                        "\n\nПредыдущая попытка не подтверждена. ПКМ → «Сверить загрузку» → «Нет», если ролика в Studio нет, или снимите галочку с лишних аккаунтов.",

                        "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    return;

                }

                MessageBox.Show(this,

                    "Пропускаем аккаунты со сверкой:\n" + string.Join("\n", blocked.Select(a => "• " + a.Name)) +

                    "\n\nЗагрузка пойдёт только для:\n" + string.Join("\n", ready.Select(a => "• " + a.Name)),

                    "TikTok", MessageBoxButtons.OK, MessageBoxIcon.Information);

            }

            try {

                SyncProfilesFromYouTube(silent: true);

                SyncWorkspaceToBackend();

                await backend.RunUploadStudioAsync(ready);

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


