using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public class PlaceholderPanel : UserControl {
        public PlaceholderPanel(string title, string message) {
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            var card = Theme.MakeCard();
            card.Dock = DockStyle.Top;
            card.Height = 120;
            card.Controls.Add(new Label {
                Text = title,
                Font = Theme.FontPageTitle,
                ForeColor = Theme.TextPrimary,
                AutoSize = true,
                Location = new Point(16, 16)
            });
            card.Controls.Add(new Label {
                Text = message,
                Font = Theme.FontBody,
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                Location = new Point(16, 56)
            });
            Controls.Add(card);
        }
    }

    public sealed class HomePanel : UserControl {
        readonly Preferences settings;
        readonly NavigationService navigation;
        readonly Label profilesCount, queueCount, scheduledCount, errorsCount;
        readonly ListView eventsList;

        public HomePanel(Preferences prefs, NavigationService nav) {
            settings = prefs;
            navigation = nav;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            AutoScroll = true;

            var cards = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 12) };
            profilesCount = StatCard(cards, "Профилей");
            queueCount = StatCard(cards, "В очереди");
            scheduledCount = StatCard(cards, "Запланировано");
            errorsCount = StatCard(cards, "Ошибок");
            Controls.Add(cards);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 16) };
            actions.Controls.Add(ActionBtn("Добавить видео", () => navigation.Navigate(NavSection.YouTube)));
            actions.Controls.Add(ActionBtn("YouTube", () => navigation.Navigate(NavSection.YouTube)));
            actions.Controls.Add(ActionBtn("TikTok", () => navigation.Navigate(NavSection.TikTok)));
            actions.Controls.Add(ActionBtn("Очередь", () => navigation.Navigate(NavSection.Tasks)));
            actions.Controls.Add(ActionBtn("Проверить аккаунты", () => navigation.Navigate(NavSection.Profiles)));
            actions.Controls.Add(ActionBtn("Логи", () => navigation.Navigate(NavSection.Logs)));
            Controls.Add(actions);

            eventsList = new ListView {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = Theme.Card,
                ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.None
            };
            eventsList.Columns.Add("Аккаунт", 140);
            eventsList.Columns.Add("Действие", 160);
            eventsList.Columns.Add("Результат", 200);
            eventsList.Columns.Add("Время", 120);
            eventsList.Columns.Add("Ссылка", 220);
            Theme.StyleGrid(new DataGridView()); // no-op helper reuse avoided
            var eventsHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            eventsHost.Controls.Add(new Label { Text = "Последние события", Dock = DockStyle.Top, Height = 28, Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary });
            eventsHost.Controls.Add(eventsList);
            Controls.Add(eventsHost);

            RefreshStats();
            TaskQueueStore.Changed += () => { if (!IsDisposed) BeginInvoke(new Action(RefreshStats)); };
        }

        static Label StatCard(FlowLayoutPanel parent, string caption) {
            var card = Theme.MakeCard();
            card.Width = 200;
            card.Height = 88;
            card.Controls.Add(new Label { Text = caption, ForeColor = Theme.TextSecondary, AutoSize = true, Location = new Point(12, 10) });
            var value = new Label { Text = "—", Font = new Font("Segoe UI", 22f), ForeColor = Theme.TextPrimary, AutoSize = true, Location = new Point(12, 34), Name = "value" };
            card.Controls.Add(value);
            parent.Controls.Add(card);
            return value;
        }

        Button ActionBtn(string text, Action action) {
            var b = Theme.MakeButton(text, accent: text.Contains("YouTube") || text.Contains("видео"));
            b.Click += (s, e) => action();
            return b;
        }

        void RefreshStats() {
            int profiles = (settings.YouTubeChannels?.Count ?? 0) + (settings.TikTokAccounts?.Count ?? 0);
            profilesCount.Text = profiles.ToString();
            var tasks = TaskQueueStore.Load();
            queueCount.Text = tasks.Count(t => t.Status == TaskQueueStatus.Waiting || t.Status == TaskQueueStatus.Preparing).ToString();
            scheduledCount.Text = (settings.YouTubeChannels ?? new List<YouTubeChannel>())
                .SelectMany(c => c.Items ?? new List<YouTubeItem>())
                .Count(i => !string.IsNullOrWhiteSpace(i.PublishedUrl) || !string.IsNullOrWhiteSpace(i.PublishedVideoId)).ToString();
            errorsCount.Text = tasks.Count(t => t.Status == TaskQueueStatus.Error || t.Status == TaskQueueStatus.ManualCheck).ToString();
            eventsList.Items.Clear();
            foreach (var ev in TaskQueueStore.RecentEvents(30)) {
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
        readonly DataGridView grid;
        readonly TextBox search;
        readonly ComboBox platformFilter, marketFilter, kindFilter, statusFilter;

        public ProfilesPanel(Preferences prefs, NavigationService nav) {
            settings = prefs;
            navigation = nav;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
            search = Theme.MakeSearchBox();
            search.Width = 180;
            search.TextChanged += (s, e) => RefreshData();
            platformFilter = FilterCombo(new[] { "Все платформы", "YouTube", "TikTok" });
            marketFilter = FilterCombo(new[] { "Все рынки", "RU", "EN" });
            kindFilter = FilterCombo(new[] { "Все типы", "Shorts", "Long", "TikTok" });
            statusFilter = FilterCombo(new[] { "Все статусы", "Готов", "Ошибка", "Отложено" });
            filters.Controls.Add(search);
            filters.Controls.Add(platformFilter);
            filters.Controls.Add(marketFilter);
            filters.Controls.Add(kindFilter);
            filters.Controls.Add(statusFilter);
            filters.Controls.Add(Theme.MakeButton("Обновить", ghost: true, action: RefreshData));
            Controls.Add(filters);

            grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 28 });
            grid.Columns.Add("status", "Статус");
            grid.Columns.Add("name", "Аккаунт");
            grid.Columns.Add("platform", "Платформа");
            grid.Columns.Add("market", "Рынок");
            grid.Columns.Add("kind", "Тип");
            grid.Columns.Add("profile", "Profile ID");
            grid.Columns.Add("ip", "Ожидаемый IP");
            grid.Columns.Add("videos", "Видео");
            grid.Columns.Add("last", "Последняя операция");
            grid.Columns.Add("result", "Результат");
            var actionsCol = new DataGridViewButtonColumn { Name = "actions", HeaderText = "Действия", Text = "⋯", UseColumnTextForButtonValue = true, Width = 70 };
            grid.Columns.Add(actionsCol);
            grid.CellContentClick += GridAction;
            Controls.Add(grid);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            bottom.Controls.Add(Theme.MakeButton("Выбрать", ghost: true, action: SelectCurrent));
            bottom.Controls.Add(Theme.MakeButton("YouTube", accent: true, action: () => OpenUpload(NavSection.YouTube)));
            bottom.Controls.Add(Theme.MakeButton("TikTok", action: () => OpenUpload(NavSection.TikTok)));
            bottom.Controls.Add(Theme.MakeButton("Добавить видео", accent: true, action: () => { SelectCurrent(); navigation.Navigate(NavSection.YouTube); }));
            Controls.Add(bottom);
            RefreshData();
        }

        ComboBox FilterCombo(string[] items) {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            c.Items.AddRange(items);
            c.SelectedIndex = 0;
            c.SelectedIndexChanged += (s, e) => RefreshData();
            return c;
        }

        public void ApplySearch(string q) {
            if (q != null && search.Text != q) search.Text = q;
            RefreshData();
        }

        public void RefreshData() {
            grid.Rows.Clear();
            string q = (search.Text ?? "").Trim().ToLowerInvariant();
            string platform = platformFilter.SelectedItem?.ToString() ?? "";
            string market = marketFilter.SelectedItem?.ToString() ?? "";
            string kind = kindFilter.SelectedItem?.ToString() ?? "";
            string status = statusFilter.SelectedItem?.ToString() ?? "";

            foreach (var ch in settings.YouTubeChannels ?? new List<YouTubeChannel>()) {
                if (ch == null) continue;
                if (platform.StartsWith("TikTok")) continue;
                if (!MatchProfile(ch.Name, ch.ProfileId, ch.Market, ch.Kind, ch.Status, "YouTube", q, platform, market, kind, status)) continue;
                int vids = ch.Items?.Count(i => !string.IsNullOrWhiteSpace(i.Video)) ?? (string.IsNullOrWhiteSpace(ch.Video) ? 0 : 1);
                int ri = grid.Rows.Add(false, ch.Status, ch.Name, "YouTube", ch.Market, KindLabel(ch.Kind), ch.ProfileId, ch.ExpectedIp, vids, ch.Status, "", "⋯");
                grid.Rows[ri].Tag = ch;
            }
            foreach (var acc in settings.TikTokAccounts ?? new List<TikTokAccount>()) {
                if (acc == null) continue;
                if (platform.StartsWith("YouTube")) continue;
                if (!MatchProfile(acc.Name, acc.ProfileId, acc.Market, "tiktok", acc.Status, "TikTok", q, platform, market, kind, status)) continue;
                int vids = acc.Items?.Count ?? 0;
                int ri2 = grid.Rows.Add(false, acc.Status, acc.Name, "TikTok", acc.Market, "TikTok", acc.ProfileId, acc.ExpectedIp, vids, acc.Status, "", "⋯");
                grid.Rows[ri2].Tag = acc;
            }
        }

        static string KindLabel(string kind) {
            return (kind ?? "").ToLowerInvariant() == "shorts" ? "Shorts" : "Long";
        }

        static bool MatchProfile(string name, string pid, string mkt, string kind, string st, string platformName, string q, string platform, string market, string kindF, string status) {
            if (!string.IsNullOrEmpty(q)) {
                string blob = ((name ?? "") + " " + (pid ?? "")).ToLowerInvariant();
                if (!blob.Contains(q)) return false;
            }
            if (!platform.StartsWith("Все") && !platform.StartsWith(platformName)) return false;
            if (!market.StartsWith("Все") && !string.Equals(mkt, market, StringComparison.OrdinalIgnoreCase)) return false;
            if (!kindF.StartsWith("Все")) {
                if (kindF == "TikTok" && platformName != "TikTok") return false;
                if (kindF == "Shorts" && (kind ?? "").ToLowerInvariant() != "shorts") return false;
                if (kindF == "Long" && platformName == "YouTube" && (kind ?? "").ToLowerInvariant() == "shorts") return false;
            }
            if (!status.StartsWith("Все") && (st ?? "").IndexOf(status, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        void SelectCurrent() {
            var row = grid.CurrentRow;
            if (row?.Tag is YouTubeChannel yc) {
                NavigationContext.SelectYouTube(yc);
                if (yc.Market == "EN") settings.LastSelectedYouTubeProfileIdEn = yc.ProfileId;
                else settings.LastSelectedYouTubeProfileIdRu = yc.ProfileId;
            } else if (row?.Tag is TikTokAccount tk) {
                NavigationContext.SelectTikTok(tk);
            }
        }

        void OpenUpload(NavSection section) {
            SelectCurrent();
            navigation.Navigate(section);
        }

        void GridAction(object sender, DataGridViewCellEventArgs e) {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "actions") return;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
            SelectCurrent();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Выбрать", null, (s, ev) => SelectCurrent());
            menu.Items.Add("Открыть загрузку", null, (s, ev) => {
                if (grid.Rows[e.RowIndex].Tag is YouTubeChannel) OpenUpload(NavSection.YouTube);
                else OpenUpload(NavSection.TikTok);
            });
            menu.Items.Add("Прокси", null, (s, ev) => navigation.Navigate(NavSection.Proxy));
            menu.Items.Add("Логи", null, (s, ev) => navigation.Navigate(NavSection.Logs));
            menu.Show(Cursor.Position);
        }
    }

    public sealed class ProxyPanel : UserControl {
        readonly Preferences settings;
        readonly NavigationService navigation;
        readonly DataGridView grid;

        public ProxyPanel(Preferences prefs, NavigationService nav) {
            settings = prefs;
            navigation = nav;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            top.Controls.Add(Theme.MakeButton("Проверить выбранные", accent: true, action: () => MessageBox.Show(this, "Используйте «Проверить профили» в разделе YouTube → Дополнительно.", "Прокси", MessageBoxButtons.OK, MessageBoxIcon.Information)));
            top.Controls.Add(Theme.MakeButton("Обновить", ghost: true, action: RefreshData));
            top.Controls.Add(Theme.MakeButton("YouTube", ghost: true, action: () => navigation.Navigate(NavSection.YouTube)));
            Controls.Add(top);

            grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            Theme.StyleGrid(grid);
            grid.Columns.Add("account", "Аккаунт");
            grid.Columns.Add("profile", "Profile ID");
            grid.Columns.Add("expected", "Ожидаемый IP");
            grid.Columns.Add("actual", "Проверенный IP");
            grid.Columns.Add("state", "Состояние");
            grid.Columns.Add("checked", "Последняя проверка");
            Controls.Add(grid);
            RefreshData();
        }

        public void RefreshData() {
            grid.Rows.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in settings.YouTubeChannels ?? new List<YouTubeChannel>()) {
                if (ch == null || string.IsNullOrWhiteSpace(ch.ProfileId)) continue;
                if (!seen.Add(ch.ProfileId.Trim())) continue;
                string state = string.IsNullOrWhiteSpace(ch.ExpectedIp) ? "Не проверялся" : "Привязан";
                grid.Rows.Add(ch.Name, ch.ProfileId, ch.ExpectedIp, ch.ExpectedIp, state, "—");
            }
            foreach (var acc in settings.TikTokAccounts ?? new List<TikTokAccount>()) {
                if (acc == null || string.IsNullOrWhiteSpace(acc.ProfileId)) continue;
                if (!seen.Add(acc.ProfileId.Trim())) continue;
                string state = string.IsNullOrWhiteSpace(acc.ExpectedIp) ? "Не проверялся" : "Привязан";
                grid.Rows.Add(acc.Name, acc.ProfileId, acc.ExpectedIp, acc.ExpectedIp, state, "—");
            }
        }
    }

    public sealed class SettingsPanel : UserControl {
        public SettingsPanel(Preferences settings, Action save) {
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            var card = Theme.MakeCard();
            card.Dock = DockStyle.Top;
            card.AutoSize = true;
            card.Controls.Add(new Label { Text = "Настройки", Font = Theme.FontPageTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Dock = DockStyle.Top });
            var keep = new CheckBox { Text = "Оставлять профиль Dolphin открытым после загрузки", AutoSize = true, Checked = settings.KeepDolphinProfileOpenAfterUpload, ForeColor = Theme.TextPrimary, Margin = new Padding(0, 16, 0, 8) };
            keep.CheckedChanged += (s, e) => { settings.KeepDolphinProfileOpenAfterUpload = keep.Checked; save(); };
            card.Controls.Add(keep);
            var note = new Label {
                Text = "Папка настроек: " + Store.Config + "\nDolphin порт: " + settings.DolphinPort,
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                Margin = new Padding(0, 8, 0, 0)
            };
            card.Controls.Add(note);
            Controls.Add(card);
        }
    }
}
