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
        readonly Label marketHint;
        readonly Panel scheduleHint, channelBadge, timezoneChip;
        readonly Button marketRu, marketEn, kindShorts, kindLong;
        ToolStripMenuItem statusMenuRoot;
        readonly Font accountNameFont = new Font(Theme.FontBody.FontFamily, 10.5f, FontStyle.Bold);
        readonly Font accountSubFont = Theme.FontSmall;
        string marketView = "RU";
        string kindView = "shorts";
        string searchQuery = "";
        string statusFilterValue = "Все статусы";

        public YouTubeWorkspacePanel(Preferences prefs, YouTubeBackend be, NavigationService nav) {
            settings = prefs;
            backend = be;
            navigation = nav;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            marketView = NormMarket(settings.YouTubeMarketView);
            kindView = NormKindView(settings.YouTubeKindView);
            if (string.IsNullOrWhiteSpace(settings.YouTubeKindView)) kindView = GuessDefaultKind();

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            marketRu = Theme.MakeHeroToggle("RU", marketView == "RU", () => SwitchMarket("RU"));
            marketEn = Theme.MakeHeroToggle("EN", marketView == "EN", () => SwitchMarket("EN"));
            kindShorts = Theme.MakeHeroToggle("Shorts", kindView == "shorts", () => SwitchKindFilter("shorts"));
            kindLong = Theme.MakeHeroToggle("Long", kindView == "long", () => SwitchKindFilter("long"));

            accountSearch = Theme.MakeSearchBox();
            statusFilter = Theme.MakeCombo(new[] { "Все статусы", "Готов", "Ошибка", "Отложено", "Загрузка" });

            marketHint = new Label { AutoSize = true, ForeColor = Theme.TextPrimary, Font = Theme.FontPageTitle, Text = "YouTube" };
            var topStack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background };
            topStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            topStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var headerBar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Theme.Background, Margin = new Padding(0, 0, 0, 6) };
            headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var headerActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Background };
            headerActions.Controls.Add(Theme.MakeButton("Добавить видео", accent: true, action: AddVideos));
            headerActions.Controls.Add(BuildMoreMenuButton());
            headerBar.Controls.Add(marketHint, 0, 0);
            headerBar.Controls.Add(headerActions, 1, 0);

            var contextCard = Theme.MakeFilterCard();
            contextCard.Padding = new Padding(16, 14, 16, 14);
            contextCard.Dock = DockStyle.Fill;
            var contextRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Card, Margin = Padding.Empty };
            contextRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            contextRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            contextRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var switches = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 2, 0, 0) };
            switches.Controls.Add(Theme.MakeHeroSegmentGroup("Рынок", marketRu, marketEn));
            switches.Controls.Add(Theme.MakeHeroSegmentGroup("Формат", kindShorts, kindLong));

            var meta = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, BackColor = Theme.Card, Margin = new Padding(0, 2, 0, 0) };
            scheduleHint = Theme.MakeChip(ScheduleSummary());
            timezoneChip = Theme.MakeAccentChip("ПК " + DateTime.Now.ToString("zzz"));
            channelBadge = Theme.MakeCountBadge("0 каналов");
            meta.Controls.Add(scheduleHint);
            meta.Controls.Add(timezoneChip);
            meta.Controls.Add(channelBadge);

            contextRow.Controls.Add(switches, 0, 0);
            contextRow.Controls.Add(meta, 1, 0);
            contextCard.Controls.Add(contextRow);

            topStack.Controls.Add(headerBar, 0, 0);
            topStack.Controls.Add(contextCard, 0, 1);
            root.Controls.Add(topStack, 0, 0);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(0, 4, 0, 0) };
            grid = new DataGridView {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Margin = new Padding(0, 8, 0, 0)
            };
            Theme.StyleGrid(grid);
            grid.RowTemplate.Height = 54;
            grid.DefaultCellStyle.SelectionBackColor = Theme.ColorFromHex("#106B54");
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ColorFromHex("#282828");
            var onCol = new DataGridViewCheckBoxColumn {
                Name = "on", HeaderText = "✓", Width = 40, MinimumWidth = 40,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Resizable = DataGridViewTriState.False,
                ThreeState = false, FlatStyle = FlatStyle.Flat
            };
            onCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(onCol);
            var thumbCol = new DataGridViewImageColumn {
                Name = "thumb", HeaderText = "", Width = 44, MinimumWidth = 44,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ImageLayout = DataGridViewImageCellLayout.Zoom
            };
            grid.Columns.Add(thumbCol);
            var thumbPickCol = new DataGridViewButtonColumn {
                Name = "thumbPick", HeaderText = "Превью", Text = "Задать", UseColumnTextForButtonValue = true,
                Width = 58, MinimumWidth = 52, AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            };
            thumbPickCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(thumbPickCol);
            var thumbClearCol = new DataGridViewButtonColumn {
                Name = "thumbClear", HeaderText = "", Text = "×", UseColumnTextForButtonValue = true,
                Width = 32, MinimumWidth = 28, AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            };
            thumbClearCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            thumbClearCol.DefaultCellStyle.ForeColor = Theme.Warning;
            grid.Columns.Add(thumbClearCol);
            grid.Columns.Add("account", "Аккаунт");
            grid.Columns["account"].FillWeight = 260;
            grid.Columns["account"].MinimumWidth = 200;
            grid.Columns["account"].ReadOnly = true;
            grid.Columns["account"].DefaultCellStyle.Font = new Font(Theme.FontBody.FontFamily, 10.5f, FontStyle.Bold);
            grid.Columns["account"].DefaultCellStyle.Padding = new Padding(4, 6, 8, 6);
            grid.Columns.Add("country", "Стр.");
            grid.Columns["country"].FillWeight = 36;
            grid.Columns["country"].MinimumWidth = 42;
            grid.Columns.Add("files", "Видео");
            grid.Columns["files"].FillWeight = 55;
            grid.Columns.Add("title", "Заголовок");
            grid.Columns["title"].FillWeight = 120;
            grid.Columns["title"].DefaultCellStyle.ForeColor = Theme.TextSecondary;
            grid.Columns.Add("status", "Статус");
            grid.Columns["status"].FillWeight = 90;
            grid.Columns["status"].DefaultCellStyle.ForeColor = Theme.TextSecondary;
            grid.Columns["status"].DefaultCellStyle.Font = Theme.FontSmall;
            grid.Columns.Add("url", "Ссылка");
            grid.Columns["url"].FillWeight = 70;
            grid.Columns["url"].DefaultCellStyle.ForeColor = Theme.Info;
            grid.CellClick += OnGridClick;
            grid.CellContentClick += OnGridContentClick;
            grid.CellFormatting += OnGridCellFormatting;
            grid.CellPainting += OnAccountCellPaint;
            grid.RowPostPaint += OnRowPostPaint;
            grid.SelectionChanged += (s, e) => RememberSelectedChannel();
            grid.MouseDown += GridMouseDown;
            grid.CellDoubleClick += OnGridCellDoubleClick;
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => { if (e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "on") SyncEnabledFromGrid(); };
            body.Controls.Add(grid);
            root.Controls.Add(body, 0, 1);

            log = Theme.MakeLogBox();
            root.Controls.Add(Theme.MakeLogSection(log), 0, 2);

            var stopBtn = Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop());
            stopBtn.ForeColor = Theme.Warning;
            var bottom = Theme.MakeBottomBar(
                (Theme.MakeButton("Быстрая загрузка", accent: true, action: async () => await RunUpload(true)), true),
                (Theme.MakeButton("Через Studio", ghost: true, action: async () => await RunUpload(false)), false),
                (Theme.MakeButton("Сетка просмотров", ghost: true, action: async () => await RunMesh()), false),
                (stopBtn, false));
            root.Controls.Add(bottom, 0, 3);

            backend.LogLine += AppendLog;
            backend.SetMarketView(marketView);
            backend.SetKindView(kindView);
            RefreshGrid();
        }

        Button BuildMoreMenuButton() {
            var menu = Theme.MakeContextMenu();
            menu.Items.Add("Поиск канала…", null, (s, e) => PromptSearch());
            statusMenuRoot = new ToolStripMenuItem("Фильтр статуса");
            RebuildStatusSubmenu();
            menu.Items.Add(statusMenuRoot);
            if (!string.IsNullOrWhiteSpace(searchQuery))
                menu.Items.Add("Сбросить поиск", null, (s, e) => { searchQuery = ""; RefreshGrid(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Отметить только с ошибкой", null, (s, e) => CheckOnlyFailed());
            menu.Items.Add("Снять галочки с загруженных", null, (s, e) => UncheckUploaded());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("+ Канал", null, (s, e) => AddChannel());
            menu.Items.Add("Предпросмотр расписания", null, (s, e) => PreviewSchedule());
            menu.Items.Add("Проверить IP (выделенный канал)", null, (s, e) => { _ = RunCheck(); });
            menu.Items.Add("Сетка просмотров (отмеченные ✓)", null, async (s, e) => await RunMesh());
            menu.Items.Add("Открыть лог", null, (s, e) => OpenLog());
            menu.Items.Add("Удалить видео с канала", null, (s, e) => RemoveSelectedVideos());
            var b = Theme.MakeButton("Ещё", ghost: true);
            b.Click += (s, e) => { RebuildStatusSubmenu(); menu.Show(b, new Point(0, b.Height)); };
            return b;
        }

        void RebuildStatusSubmenu() {
            if (statusMenuRoot == null) return;
            statusMenuRoot.DropDownItems.Clear();
            statusMenuRoot.Text = "Фильтр · " + statusFilterValue;
            foreach (var st in new[] { "Все статусы", "Готов", "Ошибка", "Отложено", "Загрузка" }) {
                string capture = st;
                statusMenuRoot.DropDownItems.Add(st + (statusFilterValue == st ? "  ✓" : ""), null, (s, e) => {
                    statusFilterValue = capture;
                    statusFilter.SelectedItem = capture;
                    RefreshGrid();
                });
            }
        }

        void PromptSearch() {
            using (var dialog = new Form {
                Text = "Поиск канала",
                Width = 420, Height = 150,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
                BackColor = Theme.Background, ForeColor = Theme.TextPrimary
            }) {
                var hint = new Label { Text = "Имя или Profile ID", Left = 20, Top = 18, Width = 360, ForeColor = Theme.TextMuted };
                var box = new TextBox { Left = 20, Top = 42, Width = 360, Text = searchQuery };
                var ok = new Button { Text = "Найти", Left = 228, Top = 72, Width = 82, DialogResult = DialogResult.OK };
                var clear = new Button { Text = "Сброс", Left = 138, Top = 72, Width = 82 };
                var cancel = new Button { Text = "Отмена", Left = 318, Top = 72, Width = 82, DialogResult = DialogResult.Cancel };
                clear.Click += (s, e) => { box.Text = ""; searchQuery = ""; RefreshGrid(); dialog.Close(); };
                dialog.Controls.AddRange(new Control[] { hint, box, ok, clear, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK) {
                    searchQuery = box.Text.Trim();
                    RefreshGrid();
                }
            }
        }

        static void StyleHeroToggle(Button b, bool active) => Theme.ApplyHeroToggleStyle(b, active);

        static Image LoadThumbImage(string path, string accountName) {
            try {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return new Bitmap(Image.FromFile(path), new Size(40, 40));
            } catch { }
            return MakeAvatar(accountName);
        }

        static Image MakeAvatar(string name) {
            var bmp = new Bitmap(40, 40);
            using (var g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Theme.Elevated);
                using (var brush = new SolidBrush(Theme.Accent))
                    g.FillEllipse(brush, 2, 2, 36, 36);
                string letter = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();
                TextRenderer.DrawText(g, letter, new Font(Theme.FontBody.FontFamily, 14f, FontStyle.Bold),
                    new Rectangle(0, 0, 40, 40), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            return bmp;
        }

        void OnRowPostPaint(object sender, DataGridViewRowPostPaintEventArgs e) {
            if (e.RowIndex < 0 || !grid.Rows[e.RowIndex].Selected) return;
            using (var pen = new Pen(Theme.Accent, 4))
                e.Graphics.DrawLine(pen, e.RowBounds.X + 1, e.RowBounds.Top + 5, e.RowBounds.X + 1, e.RowBounds.Bottom - 5);
        }

        void OnAccountCellPaint(object sender, DataGridViewCellPaintingEventArgs e) {
            if (e.RowIndex < 0 || e.ColumnIndex != grid.Columns["account"].Index) return;
            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus);
            var ch = grid.Rows[e.RowIndex].Tag as YouTubeChannel;
            if (ch == null) return;
            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            Color fg = selected ? Color.White : Theme.TextPrimary;
            Color fgSub = selected ? Color.FromArgb(210, 255, 255, 255) : Theme.TextMuted;
            var rect = e.CellBounds;
            rect.Inflate(-6, -4);
            TextRenderer.DrawText(e.Graphics, ch.Name ?? "—", accountNameFont,
                new Rectangle(rect.X, rect.Y + 2, rect.Width, 22), fg, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, "Profile " + (ch.ProfileId ?? "—"), accountSubFont,
                new Rectangle(rect.X, rect.Y + 24, rect.Width, 18), fgSub, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            e.Handled = true;
        }

        static string NormMarket(string m) { return (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU"; }
        static string NormKindView(string k) { return (k ?? "").Trim().ToLowerInvariant() == "long" ? "long" : "shorts"; }
        static bool IsShorts(string k) { return (k ?? "").Trim().ToLowerInvariant() != "long"; }
        static bool IsLongKind(string k) {
            k = (k ?? "").Trim().ToLowerInvariant();
            return k != "shorts" && k != "short";
        }
        static bool IsShortsKind(string k) {
            k = (k ?? "").Trim().ToLowerInvariant();
            return k == "shorts" || k == "short";
        }
        static bool ChannelMatchesKindView(YouTubeChannel ch, string view) {
            if (ch == null) return false;
            return view == "long" ? IsLongKind(ch.Kind) : IsShortsKind(ch.Kind);
        }

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
            string cid = marketView == "EN" ? settings.LastSelectedYouTubeChannelIdEn : settings.LastSelectedYouTubeChannelIdRu;
            for (int i = 0; i < grid.Rows.Count; i++) {
                if (grid.Rows[i].Tag is YouTubeChannel ch) {
                    if (!string.IsNullOrWhiteSpace(cid) && string.Equals((ch.ChannelId ?? "").Trim(), cid.Trim(), StringComparison.OrdinalIgnoreCase)) {
                        grid.ClearSelection(); grid.Rows[i].Selected = true; break;
                    }
                }
            }
            if (grid.SelectedRows.Count == 0) {
                for (int i = 0; i < grid.Rows.Count; i++) {
                    if (grid.Rows[i].Tag is YouTubeChannel ch && string.Equals((ch.ProfileId ?? "").Trim(), pid, StringComparison.OrdinalIgnoreCase)) {
                        grid.ClearSelection(); grid.Rows[i].Selected = true; break;
                    }
                }
            }
        }

        void SwitchMarket(string m) {
            marketView = NormMarket(m);
            settings.YouTubeMarketView = marketView;
            kindView = NormKindView(settings.YouTubeKindView);
            if (string.IsNullOrWhiteSpace(settings.YouTubeKindView)) kindView = GuessDefaultKind();
            StyleHeroToggle(kindShorts, kindView == "shorts");
            StyleHeroToggle(kindLong, kindView == "long");
            try { Store.Save(settings); } catch { }
            StyleHeroToggle(marketRu, marketView == "RU");
            StyleHeroToggle(marketEn, marketView == "EN");
            backend.SetMarketView(marketView);
            backend.SetKindView(kindView);
            RefreshGrid();
        }

        void SwitchKindFilter(string kind) {
            kindView = NormKindView(kind);
            settings.YouTubeKindView = kindView;
            StyleHeroToggle(kindShorts, kindView == "shorts");
            StyleHeroToggle(kindLong, kindView == "long");
            try { Store.Save(settings); } catch { }
            backend.SetKindView(kindView);
            RefreshGrid();
        }

        string ScheduleSummary() {
            string mode = (settings.YouTubeScheduleMode ?? "network").Trim().ToLowerInvariant();
            string publish = HttpWorkerSettings.ResolvePublishMode(settings, kindView);
            if (mode == "network") {
                int n = (settings.YouTubeChannels ?? new System.Collections.Generic.List<YouTubeChannel>())
                    .Count(c => c != null && NormMarket(c.Market) == marketView);
                int videos = (settings.YouTubeChannels ?? new System.Collections.Generic.List<YouTubeChannel>())
                    .Where(c => c != null && NormMarket(c.Market) == marketView)
                    .Sum(c => c.Items?.Count(i => !string.IsNullOrWhiteSpace(i?.Video)) ?? (string.IsNullOrWhiteSpace(c.Video) ? 0 : 1));
                if (videos < 1) videos = Math.Max(1, n * 10);
                var bounds = ScheduleGenerator.ResolveNetworkGapBounds(Math.Max(1, Math.Min(10, n)), videos, settings);
                string fmt = kindView == "long" ? "Long" : "Shorts";
                return "Сеть · " + fmt + " · " + settings.YouTubeScheduleNetworkPeriodMinutes + " мин · ~" + bounds.minMinutes + "–" + bounds.maxMinutes + " мин · "
                    + (publish == "scheduled" ? "отложено" : publish == "immediate" ? "сразу" : publish) + " · ПК";
            }
            return "HTTP · " + (kindView == "long" ? "Long" : "Shorts") + ": "
                + (publish == "scheduled" ? settings.YouTubeScheduleMinMinutes + "–" + settings.YouTubeScheduleMaxMinutes + " мин" : publish)
                + " · ПК";
        }

        static bool ChannelRowEquals(YouTubeChannel ch, string name, string profileId, string market, string kind) {
            return ch != null
                && string.Equals((ch.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)
                && string.Equals((ch.ProfileId ?? "").Trim(), profileId, StringComparison.OrdinalIgnoreCase)
                && NormMarket(ch.Market) == NormMarket(market)
                && (kind == "long" ? !IsShorts(ch.Kind) : IsShorts(ch.Kind));
        }

        void EnsureChannelIds() {
            foreach (var ch in settings.YouTubeChannels ?? new List<YouTubeChannel>())
                if (ch != null && string.IsNullOrWhiteSpace(ch.ChannelId))
                    ch.ChannelId = Guid.NewGuid().ToString("N");
        }

        void AddChannel() {
            if (!NewAccountDialog.TryShow(FindForm(), "YouTube · " + marketView + " · " + kindView, out var name, out var profileId)) return;
            if (settings.YouTubeChannels == null) settings.YouTubeChannels = new List<YouTubeChannel>();
            if (settings.YouTubeChannels.Any(ch => ChannelRowEquals(ch, name, profileId, marketView, kindView))) {
                MessageBox.Show(this, "Такой канал уже есть в списке (имя + Profile ID + формат).", "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var channel = new YouTubeChannel { ChannelId = Guid.NewGuid().ToString("N"), Enabled = true, Name = name, ProfileId = profileId, Market = marketView, Kind = kindView, Status = "Готов" };
            settings.YouTubeChannels.Add(channel);
            try { Store.Save(settings); backend.Reload(); searchQuery = ""; statusFilterValue = "Все статусы"; statusFilter.SelectedIndex = 0; RefreshGrid();
                foreach (DataGridViewRow row in grid.Rows) if (ReferenceEquals(row.Tag, channel)) { grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells["account"]; break; }
            } catch (Exception ex) { settings.YouTubeChannels.Remove(channel); MessageBox.Show(this, ex.Message, "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        public void RefreshGrid() {
            EnsureChannelIds();
            string keepChannelId = SelectedChannel()?.ChannelId;
            if (string.IsNullOrWhiteSpace(keepChannelId))
                keepChannelId = marketView == "EN" ? settings.LastSelectedYouTubeChannelIdEn : settings.LastSelectedYouTubeChannelIdRu;
            grid.Rows.Clear();
            string q = (searchQuery ?? "").Trim().ToLowerInvariant();
            string st = statusFilterValue ?? "Все статусы";
            int n = 0;
            foreach (var ch in settings.YouTubeChannels ?? new List<YouTubeChannel>()) {
                if (ch == null || NormMarket(ch.Market) != marketView) continue;
                if (!string.IsNullOrEmpty(q) && (ch.Name ?? "").ToLowerInvariant().IndexOf(q) < 0 && (ch.ProfileId ?? "").ToLowerInvariant().IndexOf(q) < 0) continue;
                if (!st.StartsWith("Все") && (ch.Status ?? "").IndexOf(st, StringComparison.OrdinalIgnoreCase) < 0) continue;
                SyncPrimary(ch);
                int files = ch.Items?.Count(i => !string.IsNullOrWhiteSpace(i.Video)) ?? (string.IsNullOrWhiteSpace(ch.Video) ? 0 : 1);
                string title = files > 0 ? Clean(ch.Items[0].Title) : Clean(ch.Title);
                string url = ch.Items?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.PublishedUrl))?.PublishedUrl ?? "";
                string thumb = ch.Items?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Thumbnail))?.Thumbnail ?? ch.Thumbnail ?? "";
                string filesLabel = files > 1 ? files + " видео" : (files == 1 ? "1 видео" : "—");
                int ri = grid.Rows.Add(ch.Enabled, LoadThumbImage(thumb, ch.Name), "Задать", "×", ch.Name, "—",
                    filesLabel, title, ch.Status ?? "Готов", url);
                grid.Rows[ri].Tag = ch;
                grid.Rows[ri].Height = 54;
                grid.Rows[ri].Cells["account"].ToolTipText = (ch.Name ?? "") + "\nProfile " + (ch.ProfileId ?? "");
                grid.Rows[ri].Cells["thumb"].ToolTipText = string.IsNullOrWhiteSpace(thumb) ? "Клик по иконке — выбрать превью" : thumb;
                grid.Rows[ri].Cells["thumbPick"].ToolTipText = "Задать превью для этого канала";
                grid.Rows[ri].Cells["thumbClear"].ToolTipText = string.IsNullOrWhiteSpace(thumb) ? "Превью не задано" : "Удалить превью";
                if (!string.IsNullOrWhiteSpace(ch.ExpectedIp))
                    _ = ResolveCountryAsync(ch.ExpectedIp, ch);
                n++;
            }
            Theme.SetBadgeText(channelBadge, n + " " + (n == 1 ? "канал" : n < 5 ? "канала" : "каналов"));
            Theme.SetBadgeText(timezoneChip, "ПК " + DateTime.Now.ToString("zzz"));
            Theme.SetBadgeText(scheduleHint, ScheduleSummary());
            RestoreGridSelection(keepChannelId);
        }

        void RestoreGridSelection(string channelId) {
            if (string.IsNullOrWhiteSpace(channelId)) return;
            for (int i = 0; i < grid.Rows.Count; i++) {
                if (grid.Rows[i].Tag is YouTubeChannel ch && string.Equals((ch.ChannelId ?? "").Trim(), channelId.Trim(), StringComparison.OrdinalIgnoreCase)) {
                    grid.ClearSelection();
                    grid.Rows[i].Selected = true;
                    try { grid.CurrentCell = grid.Rows[i].Cells["account"]; } catch { }
                    break;
                }
            }
        }

        void GridMouseDown(object sender, MouseEventArgs e) {
            if (e.Button != MouseButtons.Right) return;
            var hit = grid.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0) return;
            var row = grid.Rows[hit.RowIndex];
            if (!(row.Tag is YouTubeChannel ch)) return;
            if (!row.Selected) {
                grid.ClearSelection();
                row.Selected = true;
            }
            ShowRowContextMenu(ch, row.Index, grid.PointToScreen(e.Location));
        }

        void OnGridCellDoubleClick(object sender, DataGridViewCellEventArgs e) {
            if (e.RowIndex < 0 || e.ColumnIndex != grid.Columns["account"].Index) return;
            if (grid.Rows[e.RowIndex].Tag is YouTubeChannel ch) RenameChannel(ch, e.RowIndex);
        }

        void ShowRowContextMenu(YouTubeChannel ch, int rowIndex, Point screen) {
            var menu = Theme.MakeContextMenu();
            menu.Items.Add("Изменить название", null, (s, e) => RenameChannel(ch, rowIndex));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Проверить IP", null, (s, e) => { _ = RunCheckChannels(new List<YouTubeChannel> { ch }); });
            menu.Items.Add("Добавить видео", null, (s, e) => AddVideosForChannel(ch));
            menu.Items.Add("Задать превью", null, (s, e) => PickThumbnail(rowIndex));
            menu.Items.Add("Удалить превью", null, (s, e) => ClearThumbnail(rowIndex));
            menu.Items.Add(new ToolStripSeparator());
            bool on = Convert.ToBoolean(grid.Rows[rowIndex].Cells["on"].Value ?? false);
            menu.Items.Add(on ? "Исключить из загрузки" : "Включить в загрузку", null, (s, e) => ToggleUploadFlag(rowIndex));
            menu.Show(screen);
        }

        void ToggleUploadFlag(int rowIndex) {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return;
            bool on = Convert.ToBoolean(grid.Rows[rowIndex].Cells["on"].Value ?? false);
            grid.Rows[rowIndex].Cells["on"].Value = !on;
            if (grid.Rows[rowIndex].Tag is YouTubeChannel ch) ch.Enabled = !on;
            try { Store.Save(settings); } catch { }
        }

        static void SyncChannelUrlFromName(YouTubeChannel ch) {
            if (ch == null) return;
            var m = System.Text.RegularExpressions.Regex.Match(ch.Name ?? "", @"@([A-Za-z0-9._-]+)");
            if (m.Success) ch.ChannelUrl = "https://www.youtube.com/@" + m.Groups[1].Value;
        }

        void RenameChannel(YouTubeChannel ch, int rowIndex) {
            if (ch == null || rowIndex < 0 || rowIndex >= grid.Rows.Count) return;
            string current = ch.Name ?? "";
            if (!ChannelRenameDialog.TryShow(FindForm(), current, out string next)) return;
            next = (next ?? "").Trim();
            if (string.IsNullOrWhiteSpace(next) || string.Equals(next, current, StringComparison.OrdinalIgnoreCase)) return;
            string kindKey = IsShorts(ch.Kind) ? "shorts" : "long";
            bool duplicate = (settings.YouTubeChannels ?? new List<YouTubeChannel>()).Any(c =>
                c != null && !ReferenceEquals(c, ch) &&
                ChannelRowEquals(c, next, ch.ProfileId ?? "", ch.Market ?? marketView, kindKey));
            if (duplicate) {
                MessageBox.Show(this,
                    "Канал с таким именем и Profile ID уже есть в списке.",
                    "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ch.Name = next;
            SyncChannelUrlFromName(ch);
            try {
                Store.Save(settings);
                backend.Reload();
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            grid.Rows[rowIndex].Cells["account"].Value = next;
            grid.Rows[rowIndex].Cells["account"].ToolTipText = next + "\nProfile " + (ch.ProfileId ?? "");
            grid.InvalidateRow(rowIndex);
            AppendLog("Название: «" + current + "» → «" + next + "»" +
                (string.IsNullOrWhiteSpace(ch.ChannelUrl) ? "" : " · " + ch.ChannelUrl));
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
            if (e.RowIndex < 0 || e.ColumnIndex != grid.Columns["thumb"].Index) return;
            PickThumbnail(e.RowIndex);
        }

        void OnGridContentClick(object sender, DataGridViewCellEventArgs e) {
            if (e.RowIndex < 0) return;
            string col = grid.Columns[e.ColumnIndex].Name;
            if (col == "thumbPick") PickThumbnail(e.RowIndex);
            else if (col == "thumbClear") ClearThumbnail(e.RowIndex);
        }

        void OnGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e) {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "thumbClear") return;
            var ch = grid.Rows[e.RowIndex].Tag as YouTubeChannel;
            bool has = ch != null && HasThumbnail(ch);
            e.CellStyle.ForeColor = has ? Theme.Warning : Theme.TextMuted;
        }

        static bool HasThumbnail(YouTubeChannel ch) {
            if (!string.IsNullOrWhiteSpace(ch.Thumbnail)) return true;
            return ch.Items?.Any(i => !string.IsNullOrWhiteSpace(i?.Thumbnail)) == true;
        }

        void PickThumbnail(int rowIndex) {
            var ch = grid.Rows[rowIndex].Tag as YouTubeChannel;
            if (ch == null) return;
            using (var d = new OpenFileDialog { Filter = "Изображение|*.jpg;*.jpeg;*.png;*.webp|Все|*.*" }) {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                ApplyThumbnail(ch, rowIndex, d.FileName);
            }
        }

        void ClearThumbnail(int rowIndex) {
            var ch = grid.Rows[rowIndex].Tag as YouTubeChannel;
            if (ch == null) return;
            if (!HasThumbnail(ch)) return;
            if (MessageBox.Show(this, "Удалить превью у «" + ch.Name + "»?", "YouTube", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            ch.Thumbnail = "";
            if (ch.Items != null)
                foreach (var it in ch.Items)
                    if (it != null) it.Thumbnail = "";
            try { Store.Save(settings); } catch { }
            var old = grid.Rows[rowIndex].Cells["thumb"].Value as Image;
            grid.Rows[rowIndex].Cells["thumb"].Value = LoadThumbImage("", ch.Name);
            if (old != null && old != grid.Rows[rowIndex].Cells["thumb"].Value) old.Dispose();
            grid.Rows[rowIndex].Cells["thumb"].ToolTipText = "Превью не задано";
            grid.Rows[rowIndex].Cells["thumbClear"].ToolTipText = "Превью не задано";
            AppendLog("Превью удалено → " + ch.Name);
        }

        void ApplyThumbnail(YouTubeChannel ch, int rowIndex, string path) {
            SyncPrimary(ch);
            ch.Thumbnail = path;
            if (ch.Items != null && ch.Items.Count > 0) ch.Items[0].Thumbnail = path;
            try { Store.Save(settings); } catch { }
            var old = grid.Rows[rowIndex].Cells["thumb"].Value as Image;
            grid.Rows[rowIndex].Cells["thumb"].Value = LoadThumbImage(path, ch.Name);
            if (old != null && old != grid.Rows[rowIndex].Cells["thumb"].Value) old.Dispose();
            grid.Rows[rowIndex].Cells["thumb"].ToolTipText = path;
            grid.Rows[rowIndex].Cells["thumbClear"].ToolTipText = "Удалить превью";
            AppendLog("Превью: " + Path.GetFileName(path) + " → " + ch.Name);
        }

        void SyncEnabledFromGrid() {
            foreach (DataGridViewRow row in grid.Rows) {
                if (row.Tag is YouTubeChannel ch)
                    ch.Enabled = Convert.ToBoolean(row.Cells["on"].Value ?? false);
            }
            try { Store.Save(settings); } catch { }
        }

        void SyncWorkspaceToBackend() {
            EnsureChannelIds();
            SyncEnabledFromGrid();
            backend.SetMarketView(marketView);
            backend.SetKindView(kindView);
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
            if (ch == null) { MessageBox.Show(this, "Выберите канал кликом по строке.", "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            AddVideosForChannel(ch);
        }

        void AddVideosForChannel(YouTubeChannel ch) {
            if (ch == null) return;
            RestoreGridSelection(ch.ChannelId);
            using (var d = new OpenFileDialog { Multiselect = true, Filter = "Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все|*.*" }) {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try {
                    SyncWorkspaceToBackend();
                    backend.SelectChannel(ch);
                    backend.AssignVideosToChannel(ch, d.FileNames);
                    RefreshGrid();
                    AppendLog("Добавлено видео: " + d.FileNames.Length + " → " + ch.Name);
                } catch (Exception ex) { MessageBox.Show(this, ex.Message, "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        YouTubeChannel SelectedChannel() {
            if (grid.CurrentRow?.Tag is YouTubeChannel ch) return ch;
            return grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Selected && r.Tag is YouTubeChannel)?.Tag as YouTubeChannel;
        }

        List<YouTubeChannel> SelectedChannels() {
            return grid.SelectedRows.Cast<DataGridViewRow>()
                .Where(r => r.Tag is YouTubeChannel)
                .Select(r => (YouTubeChannel)r.Tag)
                .ToList();
        }

        List<YouTubeChannel> GetCheckedChannels() {
            grid.EndEdit();
            return grid.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Tag is YouTubeChannel && Convert.ToBoolean(r.Cells["on"].Value ?? false))
                .Select(r => (YouTubeChannel)r.Tag)
                .ToList();
        }

        static bool IsUploadDone(YouTubeChannel ch) {
            if (ch == null) return false;
            string st = ch.Status ?? "";
            if (st.IndexOf("отложено", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (st.IndexOf("опубликовано", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (st.IndexOf("HTTP", StringComparison.OrdinalIgnoreCase) >= 0 && st.IndexOf("✓", StringComparison.Ordinal) >= 0) return true;
            return ch.Items?.Any(i => !string.IsNullOrWhiteSpace(i?.PublishedUrl)) == true;
        }

        static bool IsUploadError(YouTubeChannel ch) {
            if (ch == null) return false;
            string st = ch.Status ?? "";
            if (st.StartsWith(ChannelStatus.Error, StringComparison.OrdinalIgnoreCase)) return true;
            if (st.IndexOf("ошиб", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (st.IndexOf("Dolphin не", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (st.IndexOf("initConnection", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        void CheckOnlyFailed() {
            foreach (DataGridViewRow row in grid.Rows) {
                if (!(row.Tag is YouTubeChannel ch)) continue;
                bool on = IsUploadError(ch) && !IsUploadDone(ch);
                row.Cells["on"].Value = on;
                ch.Enabled = on;
            }
            try { Store.Save(settings); } catch { }
            AppendLog("Отмечены только каналы с ошибкой.");
        }

        void UncheckUploaded() {
            int n = 0;
            foreach (DataGridViewRow row in grid.Rows) {
                if (!(row.Tag is YouTubeChannel ch) || !IsUploadDone(ch)) continue;
                row.Cells["on"].Value = false;
                ch.Enabled = false;
                n++;
            }
            try { Store.Save(settings); } catch { }
            AppendLog(n > 0 ? "Сняты галочки с " + n + " загруженных каналов." : "Нет загруженных каналов для снятия галочек.");
        }

        void RememberSelectedChannel() {
            var ch = SelectedChannel();
            if (ch == null || string.IsNullOrWhiteSpace(ch.ChannelId)) return;
            if (marketView == "EN") settings.LastSelectedYouTubeChannelIdEn = ch.ChannelId.Trim();
            else settings.LastSelectedYouTubeChannelIdRu = ch.ChannelId.Trim();
        }

        async Task RunMesh() {
            var checkedChannels = GetCheckedChannels();
            if (checkedChannels.Count == 0) {
                MessageBox.Show(this,
                    "Отметьте галочкой ✓ каналы, которые участвуют в сетке просмотров.",
                    "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try {
                SyncWorkspaceToBackend();
                AppendLog("Сетка: " + checkedChannels.Count + " канал(ов) отмечено.");
                await backend.RunMeshWatchAsync(checkedChannels);
                RefreshGrid();
            } catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
        }

        async Task RunUpload(bool http) {
            var checkedChannels = GetCheckedChannels();
            if (checkedChannels.Count == 0) {
                MessageBox.Show(this,
                    "Отметьте галочкой ✓ каналы, которые нужно загрузить.\n\n" +
                    "Подсказка: Ещё → «Отметить только с ошибкой» — для повторной загрузки упавших.",
                    "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try {
                SyncWorkspaceToBackend();
                if (http) await backend.RunHttpUploadAsync(checkedChannels);
                else await backend.RunStudioUploadAsync(checkedChannels);
                RefreshGrid();
                AppendLog("Загрузка завершена · было отмечено: " + checkedChannels.Count);
            } catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
        }

        async Task RunCheck() {
            await RunCheckChannels(SelectedChannels());
        }

        async Task RunCheckChannels(IReadOnlyList<YouTubeChannel> channels) {
            if (channels == null || channels.Count == 0) {
                MessageBox.Show(this, "Выберите канал кликом по строке (зелёная подсветка). Галочка ✓ — только для пакетной загрузки.", "YouTube", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string keepId = channels[0].ChannelId;
            try {
                EnsureChannelIds();
                SyncEnabledFromGrid();
                backend.SetMarketView(marketView);
                try { Store.Save(settings); } catch { }
                backend.Reload();
                await backend.RunCheckProfilesAsync(channels);
                RefreshGrid();
                RestoreGridSelection(keepId);
            } catch (Exception ex) { AppendLog("ОШИБКА: " + ex.Message); }
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

    internal static class ChannelRenameDialog {
        public static bool TryShow(IWin32Window owner, string currentName, out string newName) {
            newName = "";
            using (var dialog = new Form {
                Text = "Изменить название канала",
                Width = 480, Height = 248,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
                BackColor = Theme.Background, ForeColor = Theme.TextPrimary, Font = Theme.FontBody
            }) {
                var title = new Label {
                    Text = "Название канала",
                    Left = 20, Top = 16, Width = 420,
                    ForeColor = Theme.TextPrimary, Font = Theme.FontBody
                };
                var nameBox = new TextBox { Left = 20, Top = 40, Width = 420, Text = currentName ?? "" };
                var hint = new Label {
                    Text = "Подсказка: добавьте @handle YouTube в конце, например:\nТорговец из Binodex @BaronFilm",
                    Left = 20, Top = 72, Width = 420, Height = 44,
                    ForeColor = Theme.TextMuted, Font = Theme.FontSmall
                };
                var ok = new Button { Text = "Сохранить", Left = 268, Top = 148, Width = 82, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Отмена", Left = 358, Top = 148, Width = 82, DialogResult = DialogResult.Cancel };
                dialog.Controls.AddRange(new Control[] { title, nameBox, hint, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                ok.Click += (s, e) => {
                    if (!string.IsNullOrWhiteSpace(nameBox.Text)) return;
                    dialog.DialogResult = DialogResult.None;
                    MessageBox.Show(dialog, "Укажите название канала.", "VideoBatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                newName = nameBox.Text.Trim();
                return true;
            }
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
