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
        string dolphinState = "unknown"; // unknown | ok | fail

        MainWindow videoPanel;
        UploadWindow youtubePanel;
        TikTokUploadWindow tiktokPanel;
        HomePanel homePanel;
        ProfilesPanel profilesPanel;
        ProxyPanel proxyPanel;
        TasksPanel tasksPanel;
        LogsPanel logsPanel;
        PlaceholderPanel statsPanel;
        PlaceholderPanel warmupPanel;
        PlaceholderPanel viewsPanel;
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
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Background,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.SidebarExpanded));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            sidebar = BuildSidebar();
            root.Controls.Add(sidebar, 0, 0);

            var right = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Theme.Background
            };
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
            FormClosing += (s, e) => {
                SaveSettings();
                DisposeEmbeddedForms();
            };
        }

        Panel BuildSidebar() {
            var panel = new Panel {
                Dock = DockStyle.Fill,
                BackColor = Theme.Sidebar,
                Padding = new Padding(8, 12, 8, 12),
                AutoScroll = true
            };
            int y = 0;
            y = AddNavGroup(panel, "Обзор", y,
                NavItem("Главная", NavSection.Home),
                NavItem("Задачи", NavSection.Tasks),
                NavItem("Статистика", NavSection.Statistics));
            y = AddNavGroup(panel, "Аккаунты", y,
                NavItem("Профили", NavSection.Profiles),
                NavItem("Прокси", NavSection.Proxy));
            y = AddNavGroup(panel, "Контент", y,
                NavItem("Обработка видео", NavSection.VideoProcessing),
                NavItem("YouTube", NavSection.YouTube),
                NavItem("TikTok", NavSection.TikTok));
            y = AddNavGroup(panel, "Автоматизация", y,
                NavItem("Прогрев", NavSection.Warmup),
                NavItem("Просмотры и поиск", NavSection.ViewsSearch));
            y = AddNavGroup(panel, "Система", y,
                NavItem("Логи", NavSection.Logs),
                NavItem("Настройки", NavSection.Settings));
            return panel;
        }

        int AddNavGroup(Panel parent, string caption, int top, params Button[] items) {
            var label = new Label {
                Text = caption,
                ForeColor = Theme.TextMuted,
                Font = Theme.FontSmall,
                AutoSize = true,
                Location = new Point(12, top),
                Visible = sidebarExpanded
            };
            parent.Controls.Add(label);
            top += sidebarExpanded ? 22 : 4;
            foreach (var btn in items) {
                btn.Location = new Point(4, top);
                btn.Width = sidebarExpanded ? Theme.SidebarExpanded - 24 : Theme.SidebarCollapsed - 16;
                parent.Controls.Add(btn);
                top += 36;
            }
            top += 8;
            return top;
        }

        Button NavItem(string text, NavSection section) {
            var btn = new Button {
                Text = sidebarExpanded ? text : text.Length > 0 ? text.Substring(0, 1) : "?",
                FlatStyle = FlatStyle.Flat,
                TextAlign = sidebarExpanded ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter,
                Padding = new Padding(sidebarExpanded ? 12 : 0, 0, 0, 0),
                Height = 32,
                Font = Theme.FontNav,
                ForeColor = Theme.TextSecondary,
                BackColor = Theme.Sidebar,
                Cursor = Cursors.Hand,
                Tag = section
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Theme.Hover;
            btn.Click += (s, e) => navigation.Navigate(section);
            return btn;
        }

        Panel BuildTopBar() {
            var bar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.TopBar, Padding = new Padding(16, 10, 16, 10) };
            pageTitleLabel = new Label {
                Text = "Главная",
                Font = Theme.FontPageTitle,
                ForeColor = Theme.TextPrimary,
                AutoSize = true,
                Location = new Point(0, 6)
            };
            globalSearch = Theme.MakeSearchBox();
            globalSearch.Width = 220;
            globalSearch.TextChanged += (s, e) => ApplyGlobalSearch();
            dolphinDot = new Panel { Width = 10, Height = 10, BackColor = Theme.TextMuted };
            dolphinLabel = new Label { Text = "Dolphin", ForeColor = Theme.TextSecondary, AutoSize = true };
            tasksLabel = new Label { Text = "Задач: 0", ForeColor = Theme.TextSecondary, AutoSize = true };
            dolphinRefreshBtn = Theme.MakeButton("↻", ghost: true);
            dolphinRefreshBtn.Click += async (s, e) => await CheckDolphinAsync(true);
            settingsBtn = Theme.MakeButton("⚙", ghost: true);
            settingsBtn.Click += (s, e) => navigation.Navigate(NavSection.Settings);
            menuToggleBtn = Theme.MakeButton("☰", ghost: true);
            menuToggleBtn.Click += (s, e) => ToggleSidebar();

            var flow = new FlowLayoutPanel {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                BackColor = Theme.TopBar
            };
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
            homePanel = new HomePanel(settings, navigation);
            profilesPanel = new ProfilesPanel(settings, navigation);
            proxyPanel = new ProxyPanel(settings, navigation);
            tasksPanel = new TasksPanel(settings);
            logsPanel = new LogsPanel(settings);
            statsPanel = new PlaceholderPanel("Статистика", "Раздел в разработке.");
            warmupPanel = new PlaceholderPanel("Прогрев", "Раздел в разработке.");
            viewsPanel = new PlaceholderPanel("Просмотры и поиск", "Раздел в разработке.");
            settingsPanel = new SettingsPanel(settings, () => SaveSettings());

            videoPanel = new MainWindow();
            youtubePanel = new UploadWindow(settings);
            tiktokPanel = new TikTokUploadWindow(settings);
            EmbedForm(videoPanel);
            EmbedForm(youtubePanel);
            EmbedForm(tiktokPanel);
        }

        void EmbedForm(Form form) {
            form.TopLevel = false;
            form.FormBorderStyle = FormBorderStyle.None;
            form.Dock = DockStyle.Fill;
            form.Visible = false;
            contentHost.Controls.Add(form);
        }

        void ShowSection(NavSection section) {
            pageTitleLabel.Text = NavigationService.Title(section);
            HighlightNav(section);
            Control next = null;
            if (!NavigationService.IsImplemented(section)) {
                next = section == NavSection.Statistics ? statsPanel
                    : section == NavSection.Warmup ? warmupPanel
                    : viewsPanel;
            } else {
                switch (section) {
                    case NavSection.Home: next = homePanel; break;
                    case NavSection.Tasks: next = tasksPanel; tasksPanel.RefreshData(); break;
                    case NavSection.Profiles: next = profilesPanel; profilesPanel.RefreshData(); break;
                    case NavSection.Proxy: next = proxyPanel; proxyPanel.RefreshData(); break;
                    case NavSection.VideoProcessing: next = videoPanel; break;
                    case NavSection.YouTube:
                        next = youtubePanel;
                        youtubePanel.ApplyNavigationContext();
                        break;
                    case NavSection.TikTok:
                        next = tiktokPanel;
                        tiktokPanel.ApplyNavigationContext();
                        break;
                    case NavSection.Logs: next = logsPanel; logsPanel.RefreshData(); break;
                    case NavSection.Settings: next = settingsPanel; break;
                    default: next = homePanel; break;
                }
            }
            if (currentContent != null) currentContent.Visible = false;
            currentContent = next;
            if (next != null) {
                if (next is Form f) f.Show();
                else next.Visible = true;
                next.BringToFront();
            }
            tasksLabel.Text = "Задач: " + TaskQueueStore.ActiveCount();
        }

        void HighlightNav(NavSection section) {
            foreach (Control c in sidebar.Controls) {
                if (!(c is Button b) || !(b.Tag is NavSection s)) continue;
                bool active = s == section;
                b.BackColor = active ? Theme.Selected : Theme.Sidebar;
                b.ForeColor = active ? Theme.TextPrimary : Theme.TextSecondary;
            }
        }

        void ToggleSidebar() {
            sidebarExpanded = !sidebarExpanded;
            var root = (TableLayoutPanel)Controls[0];
            root.ColumnStyles[0].Width = sidebarExpanded ? Theme.SidebarExpanded : Theme.SidebarCollapsed;
            foreach (Control c in sidebar.Controls) {
                if (c is Label l && l.Font == Theme.FontSmall) l.Visible = sidebarExpanded;
                if (c is Button b && b.Tag is NavSection) {
                    string full = NavigationService.Title((NavSection)b.Tag);
                    b.Text = sidebarExpanded ? full : (full.Length > 0 ? full.Substring(0, 1) : "?");
                    b.Width = sidebarExpanded ? Theme.SidebarExpanded - 24 : Theme.SidebarCollapsed - 16;
                    b.TextAlign = sidebarExpanded ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter;
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
                if (string.IsNullOrWhiteSpace(token)) {
                    SetDolphinState("fail", "Dolphin · нет токена");
                    return;
                }
                using (var handler = new HttpClientHandler { UseProxy = false })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) }) {
                    var body = new StringContent("{\"token\":\"" + JsonEscape(token) + "\"}", System.Text.Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync("http://127.0.0.1:" + settings.DolphinPort + "/v1.0/auth/login-with-token", body).ConfigureAwait(true);
                    if (resp.IsSuccessStatusCode) SetDolphinState("ok", "Dolphin · доступен");
                    else SetDolphinState("fail", "Dolphin · HTTP " + (int)resp.StatusCode);
                }
            } catch {
                SetDolphinState("fail", "Dolphin · недоступен");
            } finally {
                dolphinRefreshBtn.Enabled = true;
                if (manual) MessageBox.Show(this, dolphinLabel.Text, "Dolphin API", MessageBoxButtons.OK,
                    dolphinState == "ok" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
        }

        void SetDolphinState(string state, string label) {
            dolphinState = state;
            dolphinLabel.Text = label;
            dolphinDot.BackColor = state == "ok" ? Theme.Success : state == "fail" ? Theme.Error : Theme.TextMuted;
        }

        static string JsonEscape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

        void SaveSettings() {
            try { Store.Save(settings); } catch { }
        }

        void DisposeEmbeddedForms() {
            videoPanel?.Dispose();
            youtubePanel?.Dispose();
            tiktokPanel?.Dispose();
        }

        public async Task EnsureDolphinBeforeTaskAsync() {
            await CheckDolphinAsync(false);
            if (dolphinState != "ok") throw new Exception(dolphinLabel.Text + ". Проверьте Dolphin Anty и API-токен.");
        }
    }
}
