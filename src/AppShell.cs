using System;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class AppShell : Form {
        readonly Preferences settings;
        readonly NavigationService navigation = new NavigationService();
        Panel sidebar;
        Panel topBar;
        Panel contentHost;
        Label pageTitleLabel;
        TextBox globalSearch;
        Panel dolphinDot;
        Label dolphinLabel;
        Label tasksLabel;
        Button menuToggleBtn;
        Button settingsBtn;
        Button dolphinRefreshBtn;
        bool sidebarExpanded = true;
        string dolphinState = "unknown";

        YouTubeBackend youtubeBackend;
        TikTokBackend tiktokBackend;
        HomePanel homePanel;
        ProfilesPanel profilesPanel;
        ProxyPanel proxyPanel;
        YouTubeWorkspacePanel youtubeWorkspace;
        TikTokWorkspacePanel tiktokWorkspace;
        ViewsSearchPanel viewsSearchPanel;
        TasksPanel tasksPanel;
        LogsPanel logsPanel;
        StatisticsPanel statsPanel;
        SettingsPanel settingsPanel;
        Control currentContent;

        public AppShell(Preferences prefs) {
            settings = prefs ?? new Preferences();
            Text = "VideoBatch";
            ClientSize = new Size(1280, 820);
            StartPosition = FormStartPosition.CenterScreen;
            Theme.StyleShellForm(this);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = Theme.Background, Padding = Padding.Empty, Margin = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.SidebarExpanded));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            sidebar = BuildSidebar();
            root.Controls.Add(sidebar, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.TopBarHeight));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(right, 1, 0);

            topBar = BuildTopBar();
            right.Controls.Add(topBar, 0, 0);

            contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(Theme.ContentPadding) };
            right.Controls.Add(contentHost, 0, 1);

            navigation.Navigated += ShowSection;
            BuildPanels();
            ShowSection(NavSection.Home);
            Shown += async (s, e) => await CheckDolphinAsync(false);
            FormClosing += (s, e) => { SaveSettings(); youtubeBackend?.Dispose(); tiktokBackend?.Dispose(); };
        }

        Panel BuildSidebar() {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Padding = new Padding(8, 12, 8, 12), AutoScroll = true };
            int y = 0;
            y = AddNavGroup(panel, "Обзор", y, NavItem("Главная", NavSection.Home), NavItem("Задачи", NavSection.Tasks), NavItem("Статистика", NavSection.Statistics));
            y = AddNavGroup(panel, "Аккаунты", y, NavItem("Профили", NavSection.Profiles), NavItem("Прокси", NavSection.Proxy));
            y = AddNavGroup(panel, "Контент", y, NavItem("Обработка видео", NavSection.VideoProcessing), NavItem("YouTube", NavSection.YouTube), NavItem("TikTok", NavSection.TikTok));
            y = AddNavGroup(panel, "Автоматизация", y, NavItem("Просмотры и поиск", NavSection.ViewsSearch));
            y = AddNavGroup(panel, "Система", y, NavItem("Логи", NavSection.Logs), NavItem("Настройки", NavSection.Settings));
            return panel;
        }

        int AddNavGroup(Panel parent, string caption, int top, params Button[] items) {
            var label = new Label { Text = caption, ForeColor = Theme.TextMuted, Font = Theme.FontSmall, AutoSize = true, Location = new Point(12, top), Visible = sidebarExpanded };
            parent.Controls.Add(label);
            top += sidebarExpanded ? 22 : 4;
            foreach (var btn in items) { btn.Location = new Point(4, top); btn.Width = sidebarExpanded ? Theme.SidebarExpanded - 24 : Theme.SidebarCollapsed - 16; parent.Controls.Add(btn); top += 36; }
            return top + 8;
        }

        Button NavItem(string text, NavSection section) {
            var btn = new Button {
                Text = sidebarExpanded ? text : (text.Length > 0 ? text.Substring(0, 1) : "?"),
                FlatStyle = FlatStyle.Flat, Height = 32, Font = Theme.FontNav,
                ForeColor = Theme.TextSecondary, BackColor = Theme.Sidebar, Cursor = Cursors.Hand, Tag = section,
                TextAlign = sidebarExpanded ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter,
                Padding = new Padding(sidebarExpanded ? 12 : 0, 0, 0, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Theme.Hover;
            btn.Click += (s, e) => navigation.Navigate(section);
            return btn;
        }

        Panel BuildTopBar() {
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.TopBar, Padding = new Padding(16, 10, 16, 10) };
            pageTitleLabel = new Label { Text = "Главная", Font = Theme.FontPageTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Location = new Point(0, 6) };
            globalSearch = Theme.MakeSearchBox();
            globalSearch.Width = 220;
            globalSearch.TextChanged += (s, e) => ApplyGlobalSearch();
            dolphinDot = new Panel { Width = 10, Height = 10, BackColor = Theme.TextMuted };
            dolphinLabel = new Label { Text = "Dolphin", ForeColor = Theme.TextSecondary, AutoSize = true };
            tasksLabel = new Label { Text = "Задач: 0", ForeColor = Theme.TextSecondary, AutoSize = true };
            dolphinRefreshBtn = Theme.MakeButton("↻", ghost: true, action: async () => await CheckDolphinAsync(true));
            settingsBtn = Theme.MakeButton("⚙", ghost: true, action: () => navigation.Navigate(NavSection.Settings));
            menuToggleBtn = Theme.MakeButton("☰", ghost: true, action: ToggleSidebar);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = Theme.TopBar };
            flow.Controls.Add(menuToggleBtn);
            flow.Controls.Add(settingsBtn);
            flow.Controls.Add(dolphinRefreshBtn);
            flow.Controls.Add(tasksLabel);
            var dolphinWrap = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.TopBar };
            dolphinWrap.Controls.Add(dolphinLabel);
            dolphinWrap.Controls.Add(dolphinDot);
            flow.Controls.Add(dolphinWrap);
            flow.Controls.Add(globalSearch);
            bar.Controls.Add(flow);
            bar.Controls.Add(pageTitleLabel);
            return bar;
        }

        void BuildPanels() {
            youtubeBackend = new YouTubeBackend(settings);
            tiktokBackend = new TikTokBackend(settings);
            homePanel = new HomePanel(settings, navigation);
            profilesPanel = new ProfilesPanel(settings, navigation, youtubeBackend, tiktokBackend);
            proxyPanel = new ProxyPanel(settings, navigation, youtubeBackend, tiktokBackend);
            youtubeWorkspace = new YouTubeWorkspacePanel(settings, youtubeBackend, navigation);
            tiktokWorkspace = new TikTokWorkspacePanel(settings, tiktokBackend);
            viewsSearchPanel = new ViewsSearchPanel(settings, youtubeBackend);
            tasksPanel = new TasksPanel(settings);
            logsPanel = new LogsPanel(settings);
            statsPanel = new StatisticsPanel(settings);
            settingsPanel = new SettingsPanel(settings, () => SaveSettings());
            AddPanel(homePanel);
            AddPanel(profilesPanel);
            AddPanel(proxyPanel);
            AddPanel(youtubeWorkspace);
            AddPanel(tiktokWorkspace);
            AddPanel(viewsSearchPanel);
            AddPanel(tasksPanel);
            AddPanel(logsPanel);
            AddPanel(statsPanel);
            AddPanel(settingsPanel);
        }

        void AddPanel(Control panel) {
            panel.Dock = DockStyle.Fill;
            panel.Visible = false;
            contentHost.Controls.Add(panel);
        }

        void ShowSection(NavSection section) {
            pageTitleLabel.Text = NavigationService.Title(section);
            HighlightNav(section);
            if (section == NavSection.VideoProcessing) {
                if (!ToolsLocator.TryResolve(out _, out _, out var hint)) {
                    MessageBox.Show(this, hint, "Обработка видео", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                using (var w = new MainWindow()) w.ShowDialog(this);
                return;
            }
            Control next;
            switch (section) {
                case NavSection.Home: next = homePanel; homePanel.RefreshStats(); break;
                case NavSection.Tasks: next = tasksPanel; tasksPanel.RefreshData(); break;
                case NavSection.Statistics: next = statsPanel; statsPanel.RefreshData(); break;
                case NavSection.Profiles: next = profilesPanel; profilesPanel.RefreshData(); break;
                case NavSection.Proxy: next = proxyPanel; proxyPanel.RefreshData(); break;
                case NavSection.YouTube: next = youtubeWorkspace; youtubeWorkspace.OnNavigated(); break;
                case NavSection.TikTok: next = tiktokWorkspace; tiktokWorkspace.OnNavigated(); break;
                case NavSection.ViewsSearch: next = viewsSearchPanel; viewsSearchPanel.RefreshData(); break;
                case NavSection.Logs: next = logsPanel; logsPanel.RefreshData(); break;
                case NavSection.Settings: next = settingsPanel; break;
                default: next = homePanel; break;
            }
            if (currentContent != null) currentContent.Visible = false;
            currentContent = next;
            if (next != null) { next.Visible = true; next.BringToFront(); }
            tasksLabel.Text = "Задач: " + TaskQueueStore.ActiveCount();
        }

        void HighlightNav(NavSection section) {
            foreach (Control c in sidebar.Controls) {
                if (!(c is Button b) || !(b.Tag is NavSection s)) continue;
                b.BackColor = s == section ? Theme.Selected : Theme.Sidebar;
                b.ForeColor = s == section ? Theme.TextPrimary : Theme.TextSecondary;
            }
        }

        void ToggleSidebar() {
            sidebarExpanded = !sidebarExpanded;
            ((TableLayoutPanel)Controls[0]).ColumnStyles[0].Width = sidebarExpanded ? Theme.SidebarExpanded : Theme.SidebarCollapsed;
            foreach (Control c in sidebar.Controls) {
                if (c is Label l && l.Font == Theme.FontSmall) l.Visible = sidebarExpanded;
                if (c is Button b && b.Tag is NavSection) {
                    string full = NavigationService.Title((NavSection)b.Tag);
                    b.Text = sidebarExpanded ? full : (full.Length > 0 ? full.Substring(0, 1) : "?");
                    b.Width = sidebarExpanded ? Theme.SidebarExpanded - 24 : Theme.SidebarCollapsed - 16;
                }
            }
        }

        void ApplyGlobalSearch() {
            string q = (globalSearch.Text ?? "").Trim();
            profilesPanel?.ApplySearch(q);
            tasksPanel?.ApplySearch(q);
        }

        async Task CheckDolphinAsync(bool manual) {
            dolphinRefreshBtn.Enabled = false;
            try {
                string token = WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
                if (string.IsNullOrWhiteSpace(token)) { SetDolphinState("fail", "Dolphin · нет токена"); return; }
                using (var handler = new HttpClientHandler { UseProxy = false })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) }) {
                    var body = new StringContent("{\"token\":\"" + JsonEscape(token) + "\"}", System.Text.Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync("http://127.0.0.1:" + settings.DolphinPort + "/v1.0/auth/login-with-token", body).ConfigureAwait(true);
                    SetDolphinState(resp.IsSuccessStatusCode ? "ok" : "fail", resp.IsSuccessStatusCode ? "Dolphin · доступен" : "Dolphin · HTTP " + (int)resp.StatusCode);
                }
            } catch { SetDolphinState("fail", "Dolphin · недоступен"); }
            finally {
                dolphinRefreshBtn.Enabled = true;
                if (manual) MessageBox.Show(this, dolphinLabel.Text, "Dolphin API", MessageBoxButtons.OK, dolphinState == "ok" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }

        void SetDolphinState(string state, string label) { dolphinState = state; dolphinLabel.Text = label; dolphinDot.BackColor = state == "ok" ? Theme.Success : state == "fail" ? Theme.Error : Theme.TextMuted; }
        static string JsonEscape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }
        void SaveSettings() { try { Store.Save(settings); } catch { } }
    }
}
