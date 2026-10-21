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
        TableLayoutPanel shellRoot;
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
        Panel profilesWorkspace;
        Action<int> selectProfilesPage;
        AccountLibraryPanel accountLibraryPanel;
        ProxyPanel proxyPanel;
        YouTubeWorkspacePanel youtubeWorkspace;
        TikTokWorkspacePanel tiktokWorkspace;
        ViewsSearchPanel viewsSearchPanel;
        TasksPanel tasksPanel;
        LogsPanel logsPanel;
        StatisticsPanel statsPanel;
        SettingsPanel settingsPanel;
        AssemblyWindow assemblyStudio;
        HighlightsPanel highlightsPanel;
        TelegramPanel telegramPanel;
        YouTubeWarmupPanel youtubeWarmupPanel;
        WarmupVideoPanel warmupVideoPanel;
        MainWindow processingWindow;
        Control currentContent;
        AppPlatform appPlatform = AppPlatform.Unchosen;
        Label platformBadge;
        Button platformBackBtn;
        PlatformHubPanel platformHub;
        bool hubMode;

        public AppShell(Preferences prefs) {
            settings = prefs ?? new Preferences();
            Text = "VideoBatch";
            ClientSize = new Size(1280, 820);
            StartPosition = FormStartPosition.CenterScreen;
            Theme.StyleShellForm(this);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            shellRoot = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = Theme.Background, Padding = Padding.Empty, Margin = Padding.Empty
            };
            shellRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.SidebarExpanded));
            shellRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(shellRoot);

            Console.WriteLine("UI startup: sidebar");
            sidebar = BuildSidebar();
            shellRoot.Controls.Add(sidebar, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.TopBarHeight));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shellRoot.Controls.Add(right, 1, 0);

            Console.WriteLine("UI startup: top bar");
            topBar = BuildTopBar();
            right.Controls.Add(topBar, 0, 0);

            contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(Theme.ContentPadding) };
            right.Controls.Add(contentHost, 0, 1);

            navigation.Navigated += ShowSection;
            BuildPanels();
            appPlatform = AppPlatformNav.Parse(settings.AppPlatformView);
            if (appPlatform == AppPlatform.Unchosen) appPlatform = AppPlatform.YouTube;
            ApplyPlatform(appPlatform, navigate: false);
            Shown += async (s, e) => {
                ShowPlatformHub();
                await CheckDolphinAsync(false);
            };
            bool closingAfterLive = false, waitingForLive = false, closingAfterHighlights = false, waitingForHighlights = false;
            FormClosing += async (s, e) => {
                if (!closingAfterLive && youtubeWorkspace?.HasLiveWork == true) {
                    e.Cancel = true;
                    if (waitingForLive) return;
                    waitingForLive = true;
                    Enabled = false;
                    try { if (await youtubeWorkspace.FinishLiveBeforeClose()) { closingAfterLive = true; BeginInvoke(new Action(Close)); } }
                    finally { waitingForLive = false; Enabled = true; }
                    return;
                }
                if (!closingAfterHighlights && highlightsPanel?.HasWork == true) {
                    e.Cancel = true; if(waitingForHighlights)return; waitingForHighlights=true;
                    try{await highlightsPanel.StopBeforeClose(); closingAfterHighlights=true; BeginInvoke(new Action(Close));}
                    finally{waitingForHighlights=false;} return;
                }
                if (telegramPanel?.HasWork == true) {
                    e.Cancel=true; if (waitingForHighlights) return; waitingForHighlights=true;
                    try { await telegramPanel.StopBeforeClose(); BeginInvoke(new Action(Close)); } finally { waitingForHighlights=false; } return;
                }
                SaveSettings(); youtubeBackend?.Dispose(); tiktokBackend?.Dispose();
            };
        }

        Panel BuildSidebar() {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Padding = new Padding(8, 12, 8, 12), AutoScroll = true };
            int y = 0;
            y = AddNavGroup(panel, "Обзор", y, NavItem("Главная", NavSection.Home), NavItem("Задачи", NavSection.Tasks), NavItem("Статистика", NavSection.Statistics));
            y = AddNavGroup(panel, "Аккаунты", y, NavItem("Профили", NavSection.Profiles));
            y = AddNavGroup(panel, "Контент", y, NavItem("Обработка видео", NavSection.VideoProcessing), NavItem("Сборка роликов", NavSection.VideoAssembly), NavItem("Нарезчик", NavSection.Highlights), NavItem("YouTube", NavSection.YouTube), NavItem("TikTok", NavSection.TikTok), NavItem("Telegram", NavSection.Telegram));
            y = AddNavGroup(panel, "YouTube", y, NavItem("Автопрогрев", NavSection.Warmup), NavItem("Прогрев роликами", NavSection.WarmupClips), NavItem("Просмотры и поиск", NavSection.ViewsSearch));
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
            var bar=new TableLayoutPanel { Dock=DockStyle.Fill, BackColor=Theme.TopBar, ColumnCount=4, RowCount=1, Padding=new Padding(18,0,18,0), Margin=Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var titleWrap=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1, Margin=Padding.Empty };
            titleWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));titleWrap.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pageTitleLabel=new Label { Text="Главная", Font=Theme.FontPageTitle, ForeColor=Theme.TextPrimary, Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleLeft, AutoEllipsis=true, Margin=Padding.Empty };
            platformBadge=new Label { Text="", AutoSize=true, ForeColor=Theme.TextMuted, Font=Theme.FontSmall, Anchor=AnchorStyles.Right, Margin=new Padding(8,0,16,0) };
            titleWrap.Controls.Add(pageTitleLabel,0,0);titleWrap.Controls.Add(platformBadge,1,0);bar.Controls.Add(titleWrap,0,0);
            globalSearch=Theme.MakeSearchBox();globalSearch.Width=180;globalSearch.Anchor=AnchorStyles.Right;globalSearch.Margin=new Padding(8,0,12,0);globalSearch.AccessibleName="Поиск в разделе";
            globalSearch.TextChanged+=(s,e)=>ApplyGlobalSearch();bar.Controls.Add(globalSearch,1,0);
            dolphinDot=new Panel { Width=7,Height=7,BackColor=Theme.TextMuted,Margin=new Padding(0,13,6,0) };
            dolphinLabel=new Label { Text="Dolphin",ForeColor=Theme.TextSecondary,Width=145,Height=34,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Margin=Padding.Empty };
            tasksLabel=new Label { Text="Задач: 0",ForeColor=Theme.TextSecondary,Width=72,Height=34,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(8,0,8,0) };
            var statusWrap=new FlowLayoutPanel { AutoSize=true,WrapContents=false,Anchor=AnchorStyles.Right,Margin=Padding.Empty,Padding=Padding.Empty };
            statusWrap.Controls.Add(dolphinDot);statusWrap.Controls.Add(dolphinLabel);statusWrap.Controls.Add(tasksLabel);bar.Controls.Add(statusWrap,2,0);
            dolphinRefreshBtn=Theme.MakeIconButton("↻",async()=>await CheckDolphinAsync(true));
            settingsBtn=Theme.MakeIconButton("⚙",()=>navigation.Navigate(NavSection.Settings));
            menuToggleBtn=Theme.MakeIconButton("☰",ToggleSidebar);
            platformBackBtn=Theme.MakeButton("← К разделам",ghost:true,action:ShowPlatformHub);platformBackBtn.Visible=false;
            var actionsWrap=new FlowLayoutPanel { AutoSize=true,WrapContents=false,Anchor=AnchorStyles.Right,Margin=Padding.Empty,Padding=Padding.Empty };
            foreach(var control in new[]{dolphinRefreshBtn,settingsBtn,platformBackBtn,menuToggleBtn}){control.Margin=new Padding(4,0,0,0);control.Height=36;actionsWrap.Controls.Add(control);}
            bar.Controls.Add(actionsWrap,3,0);return bar;
        }

        void BuildPanels() {
            Console.WriteLine("UI startup: YouTubeBackend");
            youtubeBackend = new YouTubeBackend(settings);
            Console.WriteLine("UI startup: TikTokBackend");
            tiktokBackend = new TikTokBackend(settings);
            Console.WriteLine("UI startup: HomePanel");
            homePanel = new HomePanel(settings, navigation);
            Console.WriteLine("UI startup: ProfilesPanel");
            profilesPanel = new ProfilesPanel(settings, navigation, youtubeBackend, tiktokBackend,profile=>{navigation.Navigate(NavSection.AccountLibrary);accountLibraryPanel.SelectProfile(profile.ProfileId);});
            Console.WriteLine("UI startup: AccountLibraryPanel");
            accountLibraryPanel = new AccountLibraryPanel(null, settings, OpenLibrarySource,profilesPanel.ProxyFor);
            Deactivate += (s, e) => accountLibraryPanel.HideSecrets();
            Console.WriteLine("UI startup: ProxyPanel");
            proxyPanel = new ProxyPanel(settings, navigation, youtubeBackend, tiktokBackend);
            Console.WriteLine("UI startup: YouTubeWorkspacePanel");
            youtubeWorkspace = new YouTubeWorkspacePanel(settings, youtubeBackend, navigation);
            Console.WriteLine("UI startup: TikTokWorkspacePanel");
            tiktokWorkspace = new TikTokWorkspacePanel(settings, tiktokBackend);
            Console.WriteLine("UI startup: ViewsSearchPanel");
            viewsSearchPanel = new ViewsSearchPanel(settings, youtubeBackend);
            Console.WriteLine("UI startup: YouTubeWarmupPanel");
            youtubeWarmupPanel = new YouTubeWarmupPanel(settings, youtubeBackend);
            Console.WriteLine("UI startup: WarmupVideoPanel");
            warmupVideoPanel = new WarmupVideoPanel(settings);
            Console.WriteLine("UI startup: PlatformHubPanel");
            platformHub = new PlatformHubPanel();
            platformHub.PlatformChosen += p => { HidePlatformHub(); ApplyPlatform(p, navigate: true); };
            AddPanel(platformHub);
            Console.WriteLine("UI startup: TasksPanel");
            tasksPanel = new TasksPanel(settings);
            Console.WriteLine("UI startup: LogsPanel");
            logsPanel = new LogsPanel(settings);
            Console.WriteLine("UI startup: StatisticsPanel");
            statsPanel = new StatisticsPanel(settings);
            Console.WriteLine("UI startup: SettingsPanel");
            settingsPanel = new SettingsPanel(settings, () => SaveSettings());
            AddPanel(homePanel);
            profilesWorkspace=BuildProfilesWorkspace();AddPanel(profilesWorkspace);
            AddPanel(youtubeWorkspace);
            AddPanel(tiktokWorkspace);
            AddPanel(viewsSearchPanel);
            AddPanel(youtubeWarmupPanel);
            AddPanel(warmupVideoPanel);
            AddPanel(tasksPanel);
            AddPanel(logsPanel);
            AddPanel(statsPanel);
            AddPanel(settingsPanel);
            Console.WriteLine("UI startup: HighlightsPanel");
            highlightsPanel = new HighlightsPanel();
            AddPanel(highlightsPanel);
            Console.WriteLine("UI startup: TelegramPanel");
            telegramPanel = new TelegramPanel(preferences:()=>settings);
            AddPanel(telegramPanel);
            Console.WriteLine("UI startup: AssemblyWindow");
            assemblyStudio = new AssemblyWindow(embedded: true);
            assemblyStudio.Visible = false;
            contentHost.Controls.Add(assemblyStudio);
            if (ToolsLocator.TryResolve(out _, out _, out _)) {
                processingWindow = new MainWindow(settings, embedded: true);
                processingWindow.Visible = false;
                contentHost.Controls.Add(processingWindow);
            }
        }

        Panel BuildProfilesWorkspace(){
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,46));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var tabs=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
            var host=new Panel{Dock=DockStyle.Fill,Margin=Padding.Empty};
            var views=new Control[]{profilesPanel,accountLibraryPanel,proxyPanel};var buttons=new System.Collections.Generic.List<Button>();
            selectProfilesPage=index=>{for(int i=0;i<views.Length;i++){views[i].Visible=i==index;buttons[i].BackColor=i==index?Theme.Selected:Theme.Background;}views[index].BringToFront();};
            foreach(var view in views){view.Dock=DockStyle.Fill;view.Visible=false;host.Controls.Add(view);}
            string[] captions={"Профили","Карточки аккаунтов","Проверка сети"};
            for(int i=0;i<views.Length;i++){int page=i;var button=Theme.MakeButton(captions[i],action:()=>{accountLibraryPanel.RefreshData();profilesPanel.RefreshData();proxyPanel.RefreshData();selectProfilesPage(page);});buttons.Add(button);tabs.Controls.Add(button);}
            root.Controls.Add(tabs,0,0);root.Controls.Add(host,0,1);selectProfilesPage(0);return root;
        }

        void AddPanel(Control panel) {
            panel.Dock = DockStyle.Fill;
            panel.Visible = false;
            contentHost.Controls.Add(panel);
        }

        void OpenLibrarySource(AccountSourceLink link) {
            if (link.Platform == "YouTube") {
                var ch = settings.YouTubeChannels.FirstOrDefault(x => x.ChannelId == link.SourceId); if (ch == null) return;
                navigation.Navigate(NavSection.YouTube); youtubeWorkspace.ShowAccount(ch);
            } else {
                var account = settings.TikTokAccounts.FirstOrDefault(x => x.AccountId == link.SourceId); if (account == null) return;
                navigation.Navigate(NavSection.TikTok); tiktokWorkspace.ShowAccount(account);
            }
        }

        void ShowSection(NavSection section) {
            dolphinLabel.Visible=dolphinDot.Visible=dolphinRefreshBtn.Visible=section!=NavSection.Telegram;
            pageTitleLabel.Text = section==NavSection.AccountLibrary||section==NavSection.Proxy?"Профили":NavigationService.Title(section);
            platformBadge.Visible=platformBadge.Text!=pageTitleLabel.Text;
            globalSearch.Visible=false; // Each workspace already has its own search; do not duplicate it in the shell.
            HighlightNav(section==NavSection.AccountLibrary||section==NavSection.Proxy?NavSection.Profiles:section);
            if (section == NavSection.VideoProcessing) {
                if (processingWindow == null) {
                    if (!ToolsLocator.TryResolve(out _, out _, out var hint))
                        MessageBox.Show(this, hint, "Обработка видео", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ShowContent(processingWindow);
                tasksLabel.Text = "Задач: " + TaskQueueStore.ActiveCount();
                return;
            }
            Control next;
            switch (section) {
                case NavSection.Home: next = homePanel; homePanel.RefreshStats(); break;
                case NavSection.Tasks: next = tasksPanel; tasksPanel.RefreshData(); break;
                case NavSection.Statistics: next = statsPanel; statsPanel.RefreshData(); break;
                case NavSection.Profiles: next=profilesWorkspace;accountLibraryPanel.RefreshData();profilesPanel.RefreshData();selectProfilesPage(0);break;
                case NavSection.AccountLibrary: next=profilesWorkspace;accountLibraryPanel.RefreshData();selectProfilesPage(1);break;
                case NavSection.Proxy: next=profilesWorkspace;proxyPanel.RefreshData();selectProfilesPage(2);break;
                case NavSection.VideoAssembly: next = assemblyStudio; break;
                case NavSection.Highlights: next = highlightsPanel; break;
                case NavSection.Telegram: next = telegramPanel; telegramPanel.RefreshData(); break;
                case NavSection.YouTube: next = youtubeWorkspace; youtubeWorkspace.OnNavigated(); break;
                case NavSection.TikTok: next = tiktokWorkspace; tiktokWorkspace.OnNavigated(); break;
                case NavSection.Warmup: next = youtubeWarmupPanel; youtubeWarmupPanel.ReloadAccounts(); break;
                case NavSection.WarmupClips: next = warmupVideoPanel; break;
                case NavSection.ViewsSearch: next = viewsSearchPanel; viewsSearchPanel.RefreshData(); break;
                case NavSection.Logs: next = logsPanel; logsPanel.RefreshData(); break;
                case NavSection.Settings: next = settingsPanel; break;
                default: next = homePanel; break;
            }
            ShowContent(next);
            tasksLabel.Text = "Задач: " + TaskQueueStore.ActiveCount();
        }

        void ShowContent(Control next) {
            if (currentContent != null && currentContent != next) currentContent.Visible = false;
            currentContent = next;
            if (next != null) { next.Visible = true; next.BringToFront(); }
        }

        void HighlightNav(NavSection section) {
            foreach (Control c in sidebar.Controls) {
                if (!(c is Button b) || !(b.Tag is NavSection s)) continue;
                b.BackColor = s == section ? Theme.Selected : Theme.Sidebar;
                b.ForeColor = s == section ? Theme.TextPrimary : Theme.TextSecondary;
            }
        }

        bool TryRestorePlatformFromSettings() {
            var p = AppPlatformNav.Parse(settings.AppPlatformView);
            if (p == AppPlatform.Unchosen) return false;
            ApplyPlatform(p, navigate: true);
            return true;
        }

        void ShowPlatformHub() {
            hubMode = true;
            platformBackBtn.Visible = false;
            sidebar.Visible = false;
            topBar.Visible = false;
            shellRoot.ColumnStyles[0].Width = 0;
            if (currentContent != null) currentContent.Visible = false;
            platformHub.Visible = true;
            platformHub.BringToFront();
            currentContent = platformHub;
            pageTitleLabel.Text = "VideoBatch";
            platformBadge.Text = "";
        }

        void HidePlatformHub() {
            hubMode = false;
            sidebar.Visible = true;
            topBar.Visible = true;
            shellRoot.ColumnStyles[0].Width = sidebarExpanded ? Theme.SidebarExpanded : Theme.SidebarCollapsed;
            platformBackBtn.Visible = true;
            platformHub.Visible = false;
        }

        void ApplyPlatform(AppPlatform platform, bool navigate) {
            if (platform == AppPlatform.Unchosen) platform = AppPlatform.YouTube;
            appPlatform = platform;
            settings.AppPlatformView = AppPlatformNav.ToStorage(platform);
            SaveSettings();
            platformBadge.Text = AppPlatformNav.Title(platform);
            foreach (Control c in sidebar.Controls) {
                if (c is Button b && b.Tag is NavSection section) {
                    bool visible = AppPlatformNav.IsNavVisible(appPlatform, section);
                    b.Visible = visible;
                    if (!visible && navigation.Current == section && navigate)
                        navigation.Navigate(AppPlatformNav.DefaultSection(appPlatform));
                }
            }
            RefreshSidebarGroupVisibility();
            dolphinLabel.Visible = dolphinDot.Visible = dolphinRefreshBtn.Visible = appPlatform != AppPlatform.Telegram;
            if (navigate) {
                HidePlatformHub();
                navigation.Navigate(AppPlatformNav.DefaultSection(appPlatform));
            } else RefreshSidebarGroupVisibility();
        }

        void RefreshSidebarGroupVisibility() {
            int y=12;
            foreach(Control c in sidebar.Controls) {
                if(c is Label group && group.Font==Theme.FontSmall) {
                    bool show=sidebarExpanded&&IsGroupVisibleForPlatform(group.Text);group.Visible=show;
                    if(show){group.Top=y;group.Height=28;y+=28;}
                } else if(c is Button b && b.Tag is NavSection section) {
                    bool show=AppPlatformNav.IsNavVisible(appPlatform,section);b.Visible=show;
                    if(show){b.Top=y;b.Height=36;y+=40;}
                }
            }
        }

        bool IsGroupVisibleForPlatform(string caption) {
            if (caption == "YouTube") return appPlatform == AppPlatform.YouTube;
            if (caption == "Аккаунты") return appPlatform == AppPlatform.YouTube || appPlatform == AppPlatform.TikTok;
            if (caption == "Контент") return appPlatform != AppPlatform.Unchosen;
            return true;
        }

        void ToggleSidebar() {
            sidebarExpanded = !sidebarExpanded;
            if (!hubMode) shellRoot.ColumnStyles[0].Width = sidebarExpanded ? Theme.SidebarExpanded : Theme.SidebarCollapsed;
            foreach (Control c in sidebar.Controls) {
                if (c is Label l && l.Font == Theme.FontSmall) l.Visible = sidebarExpanded && IsGroupVisibleForPlatform(l.Text);
                if (c is Button b && b.Tag is NavSection) {
                    string full = NavigationService.Title((NavSection)b.Tag);
                    b.Text = sidebarExpanded ? full : (full.Length > 0 ? full.Substring(0, 1) : "?");
                    b.Width = sidebarExpanded ? Theme.SidebarExpanded - 24 : Theme.SidebarCollapsed - 16;
                }
            }
            RefreshSidebarGroupVisibility();
        }

        void ApplyGlobalSearch() {
            string q = (globalSearch.Text ?? "").Trim();
            profilesPanel?.ApplySearch(q);
            accountLibraryPanel?.ApplySearch(q);
            tasksPanel?.ApplySearch(q);
        }

        async Task CheckDolphinAsync(bool manual) {
            dolphinRefreshBtn.Enabled = false;
            try {
                var live = Store.Load();
                settings.ProtectedDolphinToken = live.ProtectedDolphinToken;
                settings.DolphinPort = live.DolphinPort;
                string token = WindowsSupport.Unprotect(live.ProtectedDolphinToken);
                if (string.IsNullOrWhiteSpace(token)) { SetDolphinState("fail", "Dolphin · нет токена"); return; }
                using (var handler = new HttpClientHandler { UseProxy = false })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) }) {
                    var body = new StringContent("{\"token\":\"" + JsonEscape(token) + "\"}", System.Text.Encoding.UTF8, "application/json");
                    var resp = await client.PostAsync("http://127.0.0.1:" + live.DolphinPort + "/v1.0/auth/login-with-token", body).ConfigureAwait(true);
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
