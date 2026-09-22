using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class YouTubeWorkspacePanel : UserControl {
        readonly Preferences settings;
        readonly YouTubeBackend backend;
        readonly NavigationService navigation;
        readonly DataGridView grid;
        readonly RichTextBox log;
        readonly ComboBox statusFilter;
        readonly TextBox accountSearch;
        readonly Label marketHint, scheduleHint;
        readonly Button marketRu, marketEn, kindShorts, kindLong;
        string marketView = "RU";
        string kindView = "shorts";

        public YouTubeWorkspacePanel(Preferences prefs, YouTubeBackend be, NavigationService nav) {
            settings = prefs;
            backend = be;
            navigation = nav;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            marketView = NormMarket(settings.YouTubeMarketView);
            kindView = GuessDefaultKind();

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var top = Theme.MakeToolbar();
            marketRu = Theme.MakeToggle("RU", marketView == "RU", () => SwitchMarket("RU"));
            marketEn = Theme.MakeToggle("EN", marketView == "EN", () => SwitchMarket("EN"));
            kindShorts = Theme.MakeToggle("Shorts", kindView == "shorts", () => SwitchKindFilter("shorts"));
            kindLong = Theme.MakeToggle("Long", kindView == "long", () => SwitchKindFilter("long"));
            marketHint = new Label { AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(12, 8, 0, 0) };
            accountSearch = Theme.MakeSearchBox();
            accountSearch.Width = 160;
            accountSearch.TextChanged += (s, e) => RefreshGrid();
            statusFilter = Theme.MakeCombo(new[] { "Все статусы", "Готов", "Ошибка", "Отложено", "Загрузка" });
            statusFilter.SelectedIndexChanged += (s, e) => RefreshGrid();
            top.Controls.Add(marketRu);
            top.Controls.Add(marketEn);
            top.Controls.Add(new Label { Text = "Тип", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(12, 8, 6, 0) });
            top.Controls.Add(kindShorts);
            top.Controls.Add(kindLong);
            top.Controls.Add(marketHint);
            top.Controls.Add(new Label { Text = "Поиск", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(12, 8, 6, 0) });
            top.Controls.Add(accountSearch);
            top.Controls.Add(statusFilter);
            top.Controls.Add(Theme.MakeButton("+ Канал", ghost: true, action: AddChannel));
            top.Controls.Add(Theme.MakeButton("Добавить видео", accent: true, action: AddVideos));
            root.Controls.Add(top, 0, 0);

            var scheduleBar = Theme.MakeToolbar();
            scheduleHint = new Label { AutoSize = true, ForeColor = Theme.TextSecondary, Text = ScheduleSummary() };
            scheduleBar.Controls.Add(scheduleHint);
            scheduleBar.Controls.Add(Theme.MakeButton("Предпросмотр расписания", ghost: true, action: PreviewSchedule));
            root.Controls.Add(scheduleBar, 0, 1);

            grid = new DataGridView {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            Theme.StyleGrid(grid);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ColorFromHex("#282828");
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "", Width = 28 });
            grid.Columns.Add("account", "Аккаунт");
            grid.Columns.Add("country", "Страна");
            grid.Columns.Add("profile", "Profile ID");
            grid.Columns["account"].ReadOnly = true;
            grid.Columns["profile"].ReadOnly = true;
            grid.Columns.Add("files", "Файлы");
            grid.Columns.Add("title", "Заголовок");
            var thumbCol = new DataGridViewButtonColumn { Name = "thumb", HeaderText = "Превью", Text = "…", UseColumnTextForButtonValue = true, Width = 64 };
            grid.Columns.Add(thumbCol);
            grid.Columns.Add("publish", "Публикация");
            grid.Columns.Add("status", "Статус");
            grid.Columns.Add("url", "Ссылка");
            grid.CellContentClick += OnGridClick;
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => { if (e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "on") SyncEnabledFromGrid(); };
            root.Controls.Add(grid, 0, 2);

            log = Theme.MakeLogBox();
            root.Controls.Add(log, 0, 3);

            var bottom = Theme.MakeToolbar();
            bottom.Controls.Add(Theme.MakeButton("Быстрая загрузка", accent: true, action: async () => await RunUpload(true)));
            bottom.Controls.Add(Theme.MakeButton("Через Studio", ghost: true, action: async () => await RunUpload(false)));
            bottom.Controls.Add(Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop()));
            bottom.Controls.Add(Theme.MakeButton("Удалить", ghost: true, action: RemoveSelectedVideos));
            bottom.Controls.Add(Theme.MakeButton("Лог", ghost: true, action: OpenLog));
            bottom.Controls.Add(Theme.MakeButton("Проверить IP", ghost: true, action: async () => await RunCheck()));
            root.Controls.Add(bottom, 0, 4);

            backend.LogLine += AppendLog;
            RefreshGrid();
        }

        static string NormMarket(string m) { return (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU"; }
        static bool IsShorts(string k) { return (k ?? "").Trim().ToLowerInvariant() != "long"; }

        string GuessDefaultKind() {
            int shorts = (settings.YouTubeChannels ?? new List<YouTubeChannel>()).Count(c => c != null && NormMarket(c.Market) == marketView && IsShorts(c.Kind));
            int lng = (settings.YouTubeChannels ?? new List<YouTubeChannel>()).Count(c => c != null && NormMarket(c.Market) == marketView && !IsShorts(c.Kind));
            return lng > shorts ? "long" : "shorts";
        }

        public void OnNavigated() {
            ApplyNavigationContext();
            RefreshGrid();
        }

        void ApplyNavigationContext() {
            string pid = (NavigationContext.SelectedProfileId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(pid)) return;
            if (string.Equals(NavigationContext.SelectedPlatform, "YouTube", StringComparison.OrdinalIgnoreCase))
                marketView = NormMarket(NavigationContext.SelectedMarket);
            backend.SetMarketView(marketView);
            backend.SelectProfileById(pid, marketView);
            for (int i = 0; i < grid.Rows.Count; i++) {
                if (grid.Rows[i].Tag is YouTubeChannel ch && string.Equals((ch.ProfileId ?? "").Trim(), pid, StringComparison.OrdinalIgnoreCase)) {
                    grid.ClearSelection();
                    grid.Rows[i].Selected = true;
                    break;
                }
            }
        }

        void SwitchMarket(string m) {
            marketView = NormMarket(m);
            settings.YouTubeMarketView = marketView;
            kindView = GuessDefaultKind();
            kindShorts.BackColor = kindView == "shorts" ? Theme.Accent : Theme.Elevated;
            kindLong.BackColor = kindView == "long" ? Theme.Accent : Theme.Elevated;
            try { Store.Save(settings); } catch { }
            marketRu.BackColor = marketView == "RU" ? Theme.Accent : Theme.Elevated;
            marketEn.BackColor = marketView == "EN" ? Theme.Accent : Theme.Elevated;
            RefreshGrid();
        }

        void SwitchKindFilter(string kind) {
            kindView = kind == "long" ? "long" : "shorts";
            kindShorts.BackColor = kindView == "shorts" ? Theme.Accent : Theme.Elevated;
            kindLong.BackColor = kindView == "long" ? Theme.Accent : Theme.Elevated;
            RefreshGrid();
        }

        string ScheduleSummary() {
            return "HTTP · Shorts: " + (HttpWorkerSettings.ResolvePublishMode(settings, "shorts") == "scheduled"
                ? settings.YouTubeScheduleMinMinutes + "–" + settings.YouTubeScheduleMaxMinutes + " мин" : HttpWorkerSettings.ResolvePublishMode(settings, "shorts"))
                + " · Long: " + (HttpWorkerSettings.ResolvePublishMode(settings, "long") == "immediate" ? "сразу" : HttpWorkerSettings.ResolvePublishMode(settings, "long"))
                + " · время ПК " + DateTime.Now.ToString("zzz");
        }

        void AddChannel() {
            if (!NewAccountDialog.TryShow(FindForm(), "YouTube · " + marketView + " · " + kindView, out var name, out var profileId)) return;
            if (settings.YouTubeChannels == null) settings.YouTubeChannels = new List<YouTubeChannel>();
            if (settings.YouTubeChannels.Any(ch => ch != null && string.Equals((ch.ProfileId ?? "").Trim(), profileId, StringComparison.OrdinalIgnoreCase))) {
                MessageBox.Show(this, "Этот Profile ID уже добавлен в YouTube. Найдите канал через поиск.", "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var channel = new YouTubeChannel { Enabled = true, Name = name, ProfileId = profileId, Market = marketView, Kind = kindView, Status = "Готов" };
            settings.YouTubeChannels.Add(channel);
            try { Store.Save(settings); backend.Reload(); accountSearch.Text = ""; statusFilter.SelectedIndex = 0; RefreshGrid();
                foreach (DataGridViewRow row in grid.Rows) if (ReferenceEquals(row.Tag, channel)) { grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells["account"]; break; }
            } catch (Exception ex) { settings.YouTubeChannels.Remove(channel); MessageBox.Show(this, ex.Message, "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        public void RefreshGrid() {
            grid.Rows.Clear();
            string q = (accountSearch.Text ?? "").Trim().ToLowerInvariant();
            string st = statusFilter.SelectedItem?.ToString() ?? "Все статусы";
            int n = 0;
            foreach (var ch in settings.YouTubeChannels ?? new List<YouTubeChannel>()) {
                if (ch == null || NormMarket(ch.Market) != marketView) continue;
                if (kindView == "shorts" && !IsShorts(ch.Kind)) continue;
                if (kindView == "long" && IsShorts(ch.Kind)) continue;
                if (!string.IsNullOrEmpty(q) && (ch.Name ?? "").ToLowerInvariant().IndexOf(q) < 0 && (ch.ProfileId ?? "").ToLowerInvariant().IndexOf(q) < 0) continue;
                if (!st.StartsWith("Все") && (ch.Status ?? "").IndexOf(st, StringComparison.OrdinalIgnoreCase) < 0) continue;
                SyncPrimary(ch);
                int files = ch.Items?.Count(i => !string.IsNullOrWhiteSpace(i.Video)) ?? (string.IsNullOrWhiteSpace(ch.Video) ? 0 : 1);
                string title = files > 0 ? Clean(ch.Items[0].Title) : Clean(ch.Title);
                string url = ch.Items?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.PublishedUrl))?.PublishedUrl ?? "";
                string thumb = ch.Items?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Thumbnail))?.Thumbnail ?? ch.Thumbnail ?? "";
                int ri = grid.Rows.Add(ch.Enabled, ch.Name, "—", ch.ProfileId,
                    files > 1 ? files + " файлов" : Path.GetFileName(ch.Video ?? ""), title,
                    string.IsNullOrWhiteSpace(thumb) ? "…" : Path.GetFileName(thumb), "—", ch.Status ?? "Готов", url);
                grid.Rows[ri].Tag = ch;
                grid.Rows[ri].Cells["thumb"].ToolTipText = string.IsNullOrWhiteSpace(thumb) ? "Нажмите, чтобы выбрать превью (Studio)" : thumb;
                if (!string.IsNullOrWhiteSpace(ch.ExpectedIp))
                    _ = ResolveCountryAsync(ch.ExpectedIp, ch);
                n++;
            }
            marketHint.Text = (marketView == "EN" ? "English" : "Русские") + " · " + (kindView == "long" ? "Long" : "Shorts") + " · каналов: " + n;
            scheduleHint.Text = ScheduleSummary();
        }

        async Task ResolveCountryAsync(string ip, YouTubeChannel channel) {
            string code = await GeoIpCache.LookupAsync(ip).ConfigureAwait(false);
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() => {
                if (IsDisposed) return;
                foreach (DataGridViewRow row in grid.Rows) if (ReferenceEquals(row.Tag, channel)) {
                    row.Cells["country"].Value = code;
                    row.Cells["country"].ToolTipText = ip;
                    break;
                }
            }));
        }

        void OnGridClick(object sender, DataGridViewCellEventArgs e) {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "thumb") return;
            var ch = grid.Rows[e.RowIndex].Tag as YouTubeChannel;
            if (ch == null) return;
            using (var d = new OpenFileDialog { Filter = "Изображение|*.jpg;*.jpeg;*.png;*.webp|Все|*.*" }) {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                SyncPrimary(ch);
                ch.Thumbnail = d.FileName;
                if (ch.Items != null && ch.Items.Count > 0) ch.Items[0].Thumbnail = d.FileName;
                try { Store.Save(settings); } catch { }
                grid.Rows[e.RowIndex].Cells["thumb"].Value = Path.GetFileName(d.FileName);
                grid.Rows[e.RowIndex].Cells["thumb"].ToolTipText = d.FileName;
                AppendLog("Превью: " + Path.GetFileName(d.FileName) + " (HTTP проверяет результат отдельно после загрузки)");
            }
        }

        void SyncEnabledFromGrid() {
            foreach (DataGridViewRow row in grid.Rows) {
                if (row.Tag is YouTubeChannel ch)
                    ch.Enabled = Convert.ToBoolean(row.Cells["on"].Value ?? false);
            }
            try { Store.Save(settings); } catch { }
        }

        void SyncWorkspaceToBackend() {
            SyncEnabledFromGrid();
            backend.SetMarketView(marketView);
            backend.Reload();
        }

        static void SyncPrimary(YouTubeChannel ch) {
            if (ch.Items == null) ch.Items = new List<YouTubeItem>();
            if (ch.Items.Count == 0 && (!string.IsNullOrWhiteSpace(ch.Video) || !string.IsNullOrWhiteSpace(ch.Title)))
                ch.Items.Add(new YouTubeItem { Video = ch.Video ?? "", Title = ch.Title ?? "", Thumbnail = ch.Thumbnail ?? "" });
            if (ch.Items.Count > 0) { ch.Video = ch.Items[0].Video; ch.Title = ch.Items[0].Title; ch.Thumbnail = ch.Items[0].Thumbnail ?? ch.Thumbnail; }
        }

        static string Clean(string t) { return (t ?? "").Trim(); }

        void AddVideos() {
            var ch = SelectedChannel();
            if (ch == null) { MessageBox.Show(this, "Выберите канал в таблице.", "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (var d = new OpenFileDialog { Multiselect = true, Filter = "Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все|*.*" }) {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try {
                    SyncWorkspaceToBackend();
                    backend.AssignVideosToProfile(ch.ProfileId, marketView, d.FileNames);
                    RefreshGrid();
                    AppendLog("Добавлено видео: " + d.FileNames.Length + " → " + ch.Name);
                } catch (Exception ex) { MessageBox.Show(this, ex.Message, "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        YouTubeChannel SelectedChannel() {
            if (grid.CurrentRow?.Tag is YouTubeChannel ch) return ch;
            return grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Selected && r.Tag is YouTubeChannel)?.Tag as YouTubeChannel;
        }

        async Task RunUpload(bool http) {
            try {
                SyncWorkspaceToBackend();
                if (http) await backend.RunHttpUploadAsync();
                else await backend.RunStudioUploadAsync();
                RefreshGrid();
            } catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
        }

        async Task RunCheck() {
            try { SyncWorkspaceToBackend(); await backend.RunCheckProfilesAsync(); RefreshGrid(); }
            catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
        }

        void PreviewSchedule() {
            try {
                SyncWorkspaceToBackend();
                if (!backend.TryShowSchedulePreview())
                    AppendLog("Предпросмотр отменён.");
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "Расписание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void RemoveSelectedVideos() {
            var ch = SelectedChannel();
            if (ch == null) return;
            ch.Items = new List<YouTubeItem>();
            ch.Video = ""; ch.Title = ""; ch.Thumbnail = ""; ch.Status = ChannelStatus.Ready;
            try { Store.Save(settings); } catch { }
            SyncWorkspaceToBackend();
            RefreshGrid();
        }

        void OpenLog() {
            try {
                string f = backend.Window.CurrentLogFile;
                if (!string.IsNullOrWhiteSpace(f) && File.Exists(f)) Ui.Open(f);
                else MessageBox.Show(this, "Лог ещё не создан.", "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
        }

        void AppendLog(string line) {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), line); return; }
            log.AppendText(line + Environment.NewLine);
            log.ScrollToCaret();
        }
    }

    internal static class NewAccountDialog {
        public static bool TryShow(IWin32Window owner, string platform, out string name, out string profileId) {
            name = "";
            profileId = "";
            using (var dialog = new Form { Text = "Добавить · " + platform, Width = 440, Height = 230,
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Background, ForeColor = Theme.TextPrimary }) {
                var title = new Label { Text = "Имя аккаунта", Left = 20, Top = 18, Width = 380 };
                var nameBox = new TextBox { Left = 20, Top = 42, Width = 380 };
                var idLabel = new Label { Text = "Profile ID в Dolphin", Left = 20, Top = 78, Width = 380 };
                var idBox = new TextBox { Left = 20, Top = 102, Width = 380 };
                var ok = new Button { Text = "Добавить", Left = 228, Top = 143, Width = 82, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Отмена", Left = 318, Top = 143, Width = 82, DialogResult = DialogResult.Cancel };
                dialog.Controls.AddRange(new Control[] { title, nameBox, idLabel, idBox, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ok.Click += (s, e) => {
                    if (!string.IsNullOrWhiteSpace(nameBox.Text) && !string.IsNullOrWhiteSpace(idBox.Text)) return;
                    dialog.DialogResult = DialogResult.None;
                    MessageBox.Show(dialog, "Укажите имя и Profile ID из Dolphin.", "VideoBatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                name = nameBox.Text.Trim();
                profileId = idBox.Text.Trim();
                return true;
            }
        }
    }
}
