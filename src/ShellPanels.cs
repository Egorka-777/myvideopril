using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class HomePanel : UserControl {
        readonly Preferences settings;
        readonly NavigationService navigation;
        readonly Label profilesCount, youtubeCount, tiktokCount, queueCount, scheduledCount, errorsCount;
        readonly ListView eventsList;

        public HomePanel(Preferences prefs, NavigationService nav) {
            settings = prefs;
            navigation = nav;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            AutoScroll = true;

            var cards = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 12) };
            profilesCount = StatCard(cards, "Профилей (уник.)");
            youtubeCount = StatCard(cards, "YouTube");
            tiktokCount = StatCard(cards, "TikTok");
            queueCount = StatCard(cards, "В очереди");
            scheduledCount = StatCard(cards, "Запланировано");
            errorsCount = StatCard(cards, "Ошибок");
            Controls.Add(cards);

            var actions = Theme.MakeToolbar();
            actions.Controls.Add(Theme.MakeButton("Профили", accent: true, action: () => navigation.Navigate(NavSection.Profiles)));
            actions.Controls.Add(Theme.MakeButton("YouTube", action: () => navigation.Navigate(NavSection.YouTube)));
            actions.Controls.Add(Theme.MakeButton("TikTok", action: () => navigation.Navigate(NavSection.TikTok)));
            actions.Controls.Add(Theme.MakeButton("Задачи", action: () => navigation.Navigate(NavSection.Tasks)));
            actions.Controls.Add(Theme.MakeButton("Логи", action: () => navigation.Navigate(NavSection.Logs)));
            Controls.Add(actions);

            eventsList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BackColor = Theme.Card, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.None };
            eventsList.Columns.Add("Аккаунт", 140);
            eventsList.Columns.Add("Действие", 160);
            eventsList.Columns.Add("Результат", 220);
            eventsList.Columns.Add("Время", 100);
            eventsList.Columns.Add("Ссылка", 200);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            host.Controls.Add(new Label { Text = "Последние операции", Dock = DockStyle.Top, Height = 28, Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary });
            host.Controls.Add(eventsList);
            Controls.Add(host);
            TaskQueueStore.Changed += () => { if (!IsDisposed) BeginInvoke(new Action(RefreshStats)); };
            RefreshStats();
        }

        static Label StatCard(FlowLayoutPanel parent, string caption) {
            var card = Theme.MakeCard();
            card.Width = 170;
            card.Height = 84;
            card.Controls.Add(new Label { Text = caption, ForeColor = Theme.TextSecondary, AutoSize = true, Location = new Point(12, 10) });
            var value = new Label { Text = "0", Font = new Font("Segoe UI", 20f), ForeColor = Theme.TextPrimary, AutoSize = true, Location = new Point(12, 32) };
            card.Controls.Add(value);
            parent.Controls.Add(card);
            return value;
        }

        public void RefreshStats() {
            var counts = ProfileCatalog.Counts(settings);
            profilesCount.Text = counts.uniqueProfileIds.ToString();
            youtubeCount.Text = counts.youtube.ToString();
            tiktokCount.Text = counts.tiktok.ToString();
            var tasks = TaskQueueStore.Load();
            queueCount.Text = tasks.Count(t => t.Status == TaskQueueStatus.Waiting || t.Status == TaskQueueStatus.Preparing).ToString();
            scheduledCount.Text = (settings.YouTubeChannels ?? new List<YouTubeChannel>()).SelectMany(c => c.Items ?? new List<YouTubeItem>()).Count(i => !string.IsNullOrWhiteSpace(i.PublishedUrl)).ToString();
            errorsCount.Text = tasks.Count(t => t.Status == TaskQueueStatus.Error || t.Status == TaskQueueStatus.ManualCheck).ToString();
            eventsList.Items.Clear();
            foreach (var ev in TaskQueueStore.RecentEvents(40)) {
                var item = new ListViewItem(ev.Account ?? "");
                item.SubItems.Add(ev.Action ?? "");
                item.SubItems.Add(ev.Result ?? "");
                item.SubItems.Add(ev.Time ?? "");
                item.SubItems.Add(ev.Url ?? "");
                eventsList.Items.Add(item);
            }
        }
    }

    public sealed class ProfilesPanel : UserControl {
        readonly Preferences settings;
        readonly NavigationService navigation;
        readonly YouTubeBackend youtubeBackend;
        readonly TikTokBackend tiktokBackend;
        readonly DataGridView grid;
        readonly TextBox search;
        readonly ComboBox platformFilter, marketFilter, kindFilter, statusFilter;
        List<UnifiedProfile> rows = new List<UnifiedProfile>();

        public ProfilesPanel(Preferences prefs, NavigationService nav, YouTubeBackend yt, TikTokBackend tk) {
            settings = prefs;
            navigation = nav;
            youtubeBackend = yt;
            tiktokBackend = tk;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            var filters = Theme.MakeToolbar();
            search = Theme.MakeSearchBox();
            search.Width = 180;
            search.TextChanged += (s, e) => RefreshData();
            platformFilter = Theme.MakeCombo(new[] { "Все платформы", "YouTube", "TikTok", "YouTube+TikTok" });
            marketFilter = Theme.MakeCombo(new[] { "Все рынки", "RU", "EN", "Mixed" });
            kindFilter = Theme.MakeCombo(new[] { "Все типы", "Shorts", "Long", "TikTok", "Mixed" });
            statusFilter = Theme.MakeCombo(new[] { "Все статусы", "Готов", "Ошибка", "Отложено" });
            platformFilter.SelectedIndexChanged += (s, e) => RefreshData();
            marketFilter.SelectedIndexChanged += (s, e) => RefreshData();
            kindFilter.SelectedIndexChanged += (s, e) => RefreshData();
            statusFilter.SelectedIndexChanged += (s, e) => RefreshData();
            filters.Controls.Add(search);
            filters.Controls.Add(platformFilter);
            filters.Controls.Add(marketFilter);
            filters.Controls.Add(kindFilter);
            filters.Controls.Add(statusFilter);
            filters.Controls.Add(Theme.MakeButton("Обновить", ghost: true, action: RefreshData));
            Controls.Add(filters);

            grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            Theme.StyleGrid(grid);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ColorFromHex("#282828");
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 28, ReadOnly = false });
            grid.Columns.Add("name", "Профиль");
            grid.Columns.Add("platforms", "Платформы");
            grid.Columns.Add("channels", "Каналы");
            grid.Columns.Add("market", "Рынок");
            grid.Columns.Add("kind", "Тип");
            grid.Columns.Add("country", "Страна");
            grid.Columns.Add("profile", "Profile ID");
            grid.Columns.Add("ip", "IP");
            grid.Columns.Add("videos", "Видео");
            grid.Columns.Add("status", "Состояние");
            grid.Columns.Add("last", "Последняя операция");
            var act = new DataGridViewButtonColumn { Name = "actions", HeaderText = "", Text = "⋯", UseColumnTextForButtonValue = true, Width = 40 };
            grid.Columns.Add(act);
            grid.CellContentClick += OnAction;
            Controls.Add(grid);

            var bottom = Theme.MakeToolbar();
            bottom.Controls.Add(Theme.MakeButton("YouTube", accent: true, action: () => OpenPlatform(NavSection.YouTube)));
            bottom.Controls.Add(Theme.MakeButton("TikTok", action: () => OpenPlatform(NavSection.TikTok)));
            bottom.Controls.Add(Theme.MakeButton("+ Канал YouTube", ghost: true, action: () => OpenPlatform(NavSection.YouTube)));
            bottom.Controls.Add(Theme.MakeButton("+ Аккаунт TikTok", ghost: true, action: () => OpenPlatform(NavSection.TikTok)));
            bottom.Controls.Add(Theme.MakeButton("Проверить IP", ghost: true, action: async () => await CheckSelectedIp()));
            Controls.Add(bottom);
            RefreshData();
        }

        public void ApplySearch(string q) { if (q != null && search.Text != q) search.Text = q; RefreshData(); }

        public void RefreshData() {
            grid.Rows.Clear();
            rows = ProfileCatalog.Build(settings);
            foreach (var p in rows) ProfileCatalog.FinalizeLabels(p);
            string q = (search.Text ?? "").Trim().ToLowerInvariant();
            foreach (var p in rows) {
                if (!FilterMatch(p, q)) continue;
                int ri = grid.Rows.Add(false, p.DisplayName, p.PlatformsLabel, p.ChannelNames, p.Market, p.ContentKind,
                    p.CountryCode, p.ProfileId, p.ExpectedIp, p.VideoCount, p.Status, p.LastOperation, "⋯");
                grid.Rows[ri].Tag = p;
                if (p.ExpectedIp != "—" && p.CountryCode == "—")
                    _ = ResolveCountryAsync(p, ri);
            }
        }

        async Task ResolveCountryAsync(UnifiedProfile p, int rowIndex) {
            string code = await GeoIpCache.LookupAsync(p.ExpectedIp).ConfigureAwait(false);
            if (IsDisposed || rowIndex >= grid.Rows.Count) return;
            p.CountryCode = code;
            if (grid.Rows[rowIndex].Tag == p) grid.Rows[rowIndex].Cells["country"].Value = code;
        }

        bool FilterMatch(UnifiedProfile p, string q) {
            if (!string.IsNullOrEmpty(q)) {
                string blob = (p.DisplayName + " " + p.ProfileId + " " + p.ChannelNames).ToLowerInvariant();
                if (!blob.Contains(q)) return false;
            }
            string pf = platformFilter.SelectedItem?.ToString() ?? "";
            if (pf == "YouTube" && p.YouTubeAccounts.Count == 0) return false;
            if (pf == "TikTok" && p.TikTokAccounts.Count == 0) return false;
            if (pf == "YouTube+TikTok" && (p.YouTubeAccounts.Count == 0 || p.TikTokAccounts.Count == 0)) return false;
            string mk = marketFilter.SelectedItem?.ToString() ?? "";
            if (!mk.StartsWith("Все") && p.Market.IndexOf(mk, StringComparison.OrdinalIgnoreCase) < 0) return false;
            string kf = kindFilter.SelectedItem?.ToString() ?? "";
            if (!kf.StartsWith("Все") && p.ContentKind.IndexOf(kf, StringComparison.OrdinalIgnoreCase) < 0) return false;
            string st = statusFilter.SelectedItem?.ToString() ?? "";
            if (!st.StartsWith("Все") && (p.Status ?? "").IndexOf(st, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        UnifiedProfile CurrentProfile() {
            if (grid.CurrentRow?.Tag is UnifiedProfile p) return p;
            return grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Selected && r.Tag is UnifiedProfile)?.Tag as UnifiedProfile;
        }

        void SelectContext(UnifiedProfile p, string platform) {
            if (p == null) return;
            if (platform == "TikTok" && p.PrimaryTikTok != null) NavigationContext.SelectTikTok(p.PrimaryTikTok);
            else if (p.PrimaryYouTube != null) NavigationContext.SelectYouTube(p.PrimaryYouTube);
            else if (p.PrimaryTikTok != null) NavigationContext.SelectTikTok(p.PrimaryTikTok);
        }

        void OpenPlatform(NavSection section) {
            var p = CurrentProfile();
            if (p == null && rows.Count > 0) p = rows[0];
            if (p == null) { MessageBox.Show(this, "Нет профилей в settings.xml.", "Профили", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            SelectContext(p, section == NavSection.TikTok ? "TikTok" : "YouTube");
            navigation.Navigate(section);
        }

        async Task CheckSelectedIp() {
            var p = CurrentProfile();
            if (p == null || string.IsNullOrWhiteSpace(p.ProfileId)) return;
            try {
                if (p.PrimaryYouTube != null) {
                    youtubeBackend.SetMarketView(p.PrimaryYouTube.Market);
                    youtubeBackend.SelectProfileById(p.ProfileId, p.PrimaryYouTube.Market);
                    await youtubeBackend.RunCheckProfilesAsync();
                } else if (p.PrimaryTikTok != null) {
                    tiktokBackend.SetMarketView(p.PrimaryTikTok.Market);
                    tiktokBackend.SelectProfileById(p.ProfileId, p.PrimaryTikTok.Market);
                    await tiktokBackend.RunCheckProfilesAsync();
                }
                RefreshData();
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Проверка IP", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void OnAction(object sender, DataGridViewCellEventArgs e) {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "actions") return;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
            var p = (UnifiedProfile)grid.Rows[e.RowIndex].Tag;
            var menu = Theme.MakeContextMenu();
            menu.Items.Add("YouTube", null, (s, ev) => { SelectContext(p, "YouTube"); navigation.Navigate(NavSection.YouTube); });
            menu.Items.Add("TikTok", null, (s, ev) => { SelectContext(p, "TikTok"); navigation.Navigate(NavSection.TikTok); });
            menu.Items.Add("Добавить видео", null, (s, ev) => { SelectContext(p, "YouTube"); navigation.Navigate(NavSection.YouTube); });
            menu.Items.Add("Проверить IP", null, async (s, ev) => { grid.Rows[e.RowIndex].Selected = true; await CheckSelectedIp(); });
            menu.Items.Add("Dolphin Profile ID", null, (s, ev) => CopyProfileId(p));
            menu.Items.Add("Изменить имя", null, (s, ev) => RenameProfile(p));
            menu.Items.Add("Прокси", null, (s, ev) => navigation.Navigate(NavSection.Proxy));
            menu.Items.Add("Журнал", null, (s, ev) => navigation.Navigate(NavSection.Logs));
            menu.Show(Cursor.Position);
        }

        void CopyProfileId(UnifiedProfile p) {
            if (p == null || string.IsNullOrWhiteSpace(p.ProfileId)) {
                MessageBox.Show(this, "Profile ID не задан.", "Профили", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { Clipboard.SetText(p.ProfileId); MessageBox.Show(this, "Profile ID скопирован: " + p.ProfileId, "Dolphin", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Dolphin", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void RenameProfile(UnifiedProfile p) {
            if (p == null) return;
            string current = p.DisplayName ?? "";
            using (var dlg = new Form {
                Text = "Изменить имя",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ClientSize = new Size(420, 120),
                BackColor = Theme.Background,
                ForeColor = Theme.TextPrimary,
                Font = Theme.FontBody
            }) {
                var box = Theme.MakeSearchBox();
                box.Text = current;
                box.Width = 380;
                box.Location = new Point(16, 16);
                dlg.Controls.Add(box);
                var ok = Theme.MakeButton("Сохранить", accent: true, action: () => { dlg.DialogResult = DialogResult.OK; dlg.Close(); });
                ok.Location = new Point(16, 56);
                dlg.Controls.Add(ok);
                dlg.AcceptButton = ok;
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                string next = (box.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(next) || string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) return;
                foreach (var ch in p.YouTubeAccounts) if (ch != null) ch.Name = next;
                foreach (var acc in p.TikTokAccounts) if (acc != null) acc.Name = next;
                try { Store.Save(settings); youtubeBackend.Reload(); tiktokBackend.Reload(); RefreshData(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Профили", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
    }

    public sealed class ProxyPanel : UserControl {
        readonly Preferences settings;
        readonly NavigationService navigation;
        readonly YouTubeBackend youtubeBackend;
        readonly TikTokBackend tiktokBackend;
        readonly DataGridView grid;

        public ProxyPanel(Preferences prefs, NavigationService nav, YouTubeBackend yt, TikTokBackend tk) {
            settings = prefs;
            navigation = nav;
            youtubeBackend = yt;
            tiktokBackend = tk;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            var top = Theme.MakeToolbar();
            top.Controls.Add(Theme.MakeButton("Проверить выбранные", accent: true, action: async () => await CheckSelected()));
            top.Controls.Add(Theme.MakeButton("Проверить все", ghost: true, action: async () => await CheckAll()));
            top.Controls.Add(Theme.MakeButton("Обновить", ghost: true, action: RefreshData));
            Controls.Add(top);

            grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            Theme.StyleGrid(grid);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ColorFromHex("#282828");
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 28, ReadOnly = false });
            grid.Columns.Add("profile", "Профиль");
            grid.Columns.Add("accounts", "Аккаунты");
            grid.Columns.Add("pid", "Profile ID");
            grid.Columns.Add("country", "Страна");
            grid.Columns.Add("expected", "Ожидаемый IP");
            grid.Columns.Add("actual", "Фактический IP");
            grid.Columns.Add("result", "Результат");
            grid.Columns.Add("checked", "Проверка");
            Controls.Add(grid);
            RefreshData();
        }

        public void RefreshData() {
            grid.Rows.Clear();
            foreach (var p in ProfileCatalog.Build(settings)) {
                ProfileCatalog.FinalizeLabels(p);
                if (string.IsNullOrWhiteSpace(p.ProfileId)) continue;
                int ri = grid.Rows.Add(false, p.DisplayName, p.ChannelNames, p.ProfileId, p.CountryCode,
                    p.ExpectedIp, p.VerifiedIp, p.ProxyCheckResult, p.LastProxyCheck.HasValue ? p.LastProxyCheck.Value.ToString("dd.MM HH:mm") : "—");
                grid.Rows[ri].Tag = p;
                if (p.ExpectedIp != "—" && p.CountryCode == "—") _ = GeoCountry(p, ri);
            }
        }

        async Task GeoCountry(UnifiedProfile p, int ri) {
            string c = await GeoIpCache.LookupAsync(p.ExpectedIp).ConfigureAwait(false);
            if (!IsDisposed && ri < grid.Rows.Count && grid.Rows[ri].Tag == p) grid.Rows[ri].Cells["country"].Value = c;
        }

        List<UnifiedProfile> SelectedRows() {
            var list = new List<UnifiedProfile>();
            foreach (DataGridViewRow r in grid.Rows)
                if (r.Tag is UnifiedProfile p && Convert.ToBoolean(r.Cells["sel"].Value)) list.Add(p);
            if (list.Count == 0 && grid.CurrentRow?.Tag is UnifiedProfile one) list.Add(one);
            return list;
        }

        async Task CheckSelected() {
            foreach (var p in SelectedRows()) await CheckOne(p);
            RefreshData();
        }

        async Task CheckAll() {
            foreach (var p in ProfileCatalog.Build(settings))
                if (!string.IsNullOrWhiteSpace(p.ProfileId)) await CheckOne(p);
            RefreshData();
        }

        async Task CheckOne(UnifiedProfile p) {
            try {
                if (p.PrimaryYouTube != null) {
                    youtubeBackend.SetMarketView(p.PrimaryYouTube.Market);
                    youtubeBackend.SelectProfileById(p.ProfileId, p.PrimaryYouTube.Market);
                    await youtubeBackend.RunCheckProfilesAsync();
                } else if (p.PrimaryTikTok != null) {
                    tiktokBackend.SetMarketView(p.PrimaryTikTok.Market);
                    tiktokBackend.SelectProfileById(p.ProfileId, p.PrimaryTikTok.Market);
                    await tiktokBackend.RunCheckProfilesAsync();
                }
                p.ProxyCheckResult = "Проверен";
                p.LastProxyCheck = DateTime.Now;
            } catch (Exception ex) {
                p.ProxyCheckResult = ex.Message.Length > 80 ? ex.Message.Substring(0, 80) : ex.Message;
            }
        }
    }

    public sealed class StatisticsPanel : UserControl {
        readonly Preferences settings;
        readonly Label body;

        public StatisticsPanel(Preferences prefs) {
            settings = prefs;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            var card = Theme.MakeCard();
            card.Dock = DockStyle.Fill;
            card.Controls.Add(new Label { Text = "Статистика", Font = Theme.FontPageTitle, ForeColor = Theme.TextPrimary, Dock = DockStyle.Top, Height = 32 });
            body = new Label { ForeColor = Theme.TextSecondary, AutoSize = false, Dock = DockStyle.Fill, Font = Theme.FontBody };
            card.Controls.Add(body);
            Controls.Add(card);
        }

        public void RefreshData() {
            var c = ProfileCatalog.Counts(settings);
            int queued = TaskQueueStore.Load().Count;
            int mesh = 0;
            try { if (System.IO.File.Exists(Store.MeshCatalogPath)) mesh = System.IO.File.ReadAllText(Store.MeshCatalogPath).Split(new[] { "\"videoId\"" }, StringSplitOptions.None).Length - 1; } catch { }
            body.Text = "Доступные данные из settings.xml и локальной очереди:\r\n\r\n"
                + "Уникальных Profile ID: " + c.uniqueProfileIds + "\r\n"
                + "YouTube-аккаунтов: " + c.youtube + "\r\n"
                + "TikTok-аккаунтов: " + c.tiktok + "\r\n"
                + "Задач в очереди: " + queued + "\r\n"
                + "Записей в каталоге сетки: " + mesh + "\r\n\r\n"
                + "Подробная аналитика просмотров/CTR в приложении пока не подключена.";
        }
    }

    public sealed class SettingsPanel : UserControl {
        public SettingsPanel(Preferences settings, Action save) {
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            AutoScroll = true;
            var card = Theme.MakeCard();
            card.Dock = DockStyle.Top;
            card.AutoSize = true;
            card.Controls.Add(new Label { Text = "Настройки", Font = Theme.FontPageTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Dock = DockStyle.Top });

            var keep = new CheckBox { Text = "Оставлять профиль Dolphin открытым после загрузки", AutoSize = true, Checked = settings.KeepDolphinProfileOpenAfterUpload, ForeColor = Theme.TextPrimary, Margin = new Padding(0, 16, 0, 8) };
            Theme.StyleCheckBox(keep);
            keep.CheckedChanged += (s, e) => { settings.KeepDolphinProfileOpenAfterUpload = keep.Checked; save(); };
            card.Controls.Add(keep);

            var port = Theme.MakeSearchBox();
            port.Text = settings.DolphinPort.ToString();
            port.Width = 80;
            port.Leave += (s, e) => { if (int.TryParse(port.Text, out var v) && v > 0 && v < 65536) { settings.DolphinPort = v; save(); } };
            card.Controls.Add(Labeled("Dolphin порт", port));

            var customWorkers = Theme.MakeSearchBox();
            customWorkers.Text = (settings.HttpWorkerCustomCount <= 0 ? HttpWorkerSettings.DefaultWorkers : settings.HttpWorkerCustomCount).ToString();
            customWorkers.Width = 80;
            customWorkers.Enabled = (settings.HttpWorkerPreset ?? "normal") == "custom";

            var workerPreset = Theme.MakeCombo(new[] { "safe", "normal", "fast", "custom", "auto" });
            workerPreset.SelectedItem = string.IsNullOrWhiteSpace(settings.HttpWorkerPreset) ? "normal" : settings.HttpWorkerPreset;
            workerPreset.SelectedIndexChanged += (s, e) => {
                settings.HttpWorkerPreset = workerPreset.SelectedItem?.ToString() ?? "normal";
                customWorkers.Enabled = settings.HttpWorkerPreset == "custom";
                save();
            };
            card.Controls.Add(Labeled("HTTP workers (safe/normal/fast/custom/auto)", workerPreset));
            customWorkers.Leave += (s, e) => {
                if (int.TryParse(customWorkers.Text, out var v)) {
                    settings.HttpWorkerCustomCount = Math.Max(1, Math.Min(HttpWorkerSettings.MaxCustomWorkers, v));
                    settings.MaxParallelUploads = settings.HttpWorkerCustomCount;
                    save();
                    customWorkers.Text = settings.HttpWorkerCustomCount.ToString();
                }
            };
            card.Controls.Add(Labeled("Пользовательский pool (1–10)", customWorkers));

            var publishMode = Theme.MakeCombo(new[] { "scheduled", "immediate", "private" });
            publishMode.SelectedItem = string.IsNullOrWhiteSpace(settings.YouTubeHttpPublishMode) ? "scheduled" : settings.YouTubeHttpPublishMode;
            publishMode.SelectedIndexChanged += (s, e) => { settings.YouTubeHttpPublishMode = publishMode.SelectedItem?.ToString() ?? "scheduled"; save(); };
            card.Controls.Add(Labeled("YouTube HTTP · Shorts", publishMode));

            var longMode = Theme.MakeCombo(new[] { "immediate", "scheduled", "private" });
            longMode.SelectedItem = HttpWorkerSettings.ResolvePublishMode(settings, "long");
            longMode.SelectedIndexChanged += (s, e) => { settings.YouTubeHttpLongPublishMode = longMode.SelectedItem?.ToString() ?? "scheduled"; save(); };
            card.Controls.Add(Labeled("YouTube HTTP · Long", longMode));

            var shortsMin = Theme.MakeSearchBox();
            shortsMin.Text = settings.YouTubeScheduleMinMinutes.ToString(); shortsMin.Width = 80;
            var shortsMax = Theme.MakeSearchBox();
            shortsMax.Text = settings.YouTubeScheduleMaxMinutes.ToString(); shortsMax.Width = 80;
            shortsMin.Leave += (s, e) => {
                if (int.TryParse(shortsMin.Text, out var v) && v >= 1 && v <= settings.YouTubeScheduleMaxMinutes) { settings.YouTubeScheduleMinMinutes = v; save(); }
                shortsMin.Text = settings.YouTubeScheduleMinMinutes.ToString();
            };
            shortsMax.Leave += (s, e) => {
                if (int.TryParse(shortsMax.Text, out var v) && v >= settings.YouTubeScheduleMinMinutes && v <= 1440) { settings.YouTubeScheduleMaxMinutes = v; save(); }
                shortsMax.Text = settings.YouTubeScheduleMaxMinutes.ToString();
            };
            card.Controls.Add(Labeled("Shorts · минимум, мин", shortsMin));
            card.Controls.Add(Labeled("Shorts · максимум, мин", shortsMax));
            card.Controls.Add(new Label { Text = "Отложенные Shorts используют время и часовой пояс этого ПК. Ранее созданные отложенные ролики не меняются.",
                ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(760, 0) });

            var lead = Theme.MakeSearchBox();
            lead.Text = settings.YouTubeScheduleLeadMinutes.ToString();
            lead.Width = 80;
            lead.Leave += (s, e) => {
                if (int.TryParse(lead.Text, out var v)) {
                    settings.YouTubeScheduleLeadMinutes = Math.Max(ScheduleGenerator.PreflightMinLeadMinutes, v);
                    save();
                    lead.Text = settings.YouTubeScheduleLeadMinutes.ToString();
                }
            };
            card.Controls.Add(Labeled("Запас до 1-й публикации, мин (≥20)", lead));

            var schedMode = Theme.MakeCombo(new[] { "network", "random", "period" });
            schedMode.SelectedItem = string.IsNullOrWhiteSpace(settings.YouTubeScheduleMode) ? "network" : settings.YouTubeScheduleMode;
            schedMode.SelectedIndexChanged += (s, e) => { settings.YouTubeScheduleMode = schedMode.SelectedItem?.ToString() ?? "network"; save(); };
            card.Controls.Add(Labeled("Режим расписания", schedMode));

            var netPeriod = Theme.MakeSearchBox();
            netPeriod.Text = settings.YouTubeScheduleNetworkPeriodMinutes.ToString(); netPeriod.Width = 80;
            netPeriod.Leave += (s, e) => {
                if (int.TryParse(netPeriod.Text, out var v) && v >= 60 && v <= 24 * 60) { settings.YouTubeScheduleNetworkPeriodMinutes = v; save(); }
                netPeriod.Text = settings.YouTubeScheduleNetworkPeriodMinutes.ToString();
            };
            card.Controls.Add(Labeled("Сеть · круг активности, мин", netPeriod));

            var netMin = Theme.MakeSearchBox();
            netMin.Text = settings.YouTubeScheduleNetworkMinMinutes.ToString(); netMin.Width = 80;
            var netMax = Theme.MakeSearchBox();
            netMax.Text = settings.YouTubeScheduleNetworkMaxMinutes.ToString(); netMax.Width = 80;
            netMin.Leave += (s, e) => {
                if (int.TryParse(netMin.Text, out var v) && v >= 1 && v <= settings.YouTubeScheduleNetworkMaxMinutes) { settings.YouTubeScheduleNetworkMinMinutes = v; save(); }
                netMin.Text = settings.YouTubeScheduleNetworkMinMinutes.ToString();
            };
            netMax.Leave += (s, e) => {
                if (int.TryParse(netMax.Text, out var v) && v >= settings.YouTubeScheduleNetworkMinMinutes && v <= 60) { settings.YouTubeScheduleNetworkMaxMinutes = v; save(); }
                netMax.Text = settings.YouTubeScheduleNetworkMaxMinutes.ToString();
            };
            card.Controls.Add(Labeled("Сеть · мин. интервал, мин", netMin));
            card.Controls.Add(Labeled("Сеть · макс. интервал, мин", netMax));
            card.Controls.Add(new Label { Text = "Режим network: все аккаунты в одной очереди, перемешаны, ~8 ч на ПК. Новый канал автоматически включается в пересчёт.",
                ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(760, 0) });

            card.Controls.Add(new Label {
                Text = "Каталог настроек: " + Store.Config + "\nStaging: " + settings.UploadStagingFolder + "\nДиагностика: " + System.IO.Path.Combine(Store.Root, "diagnostics"),
                ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(0, 12, 0, 0)
            });
            Controls.Add(card);
        }

        static Control Labeled(string caption, Control c) {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
            row.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Theme.TextSecondary, Width = 180, Margin = new Padding(0, 6, 8, 0) });
            row.Controls.Add(c);
            return row;
        }
    }
}
