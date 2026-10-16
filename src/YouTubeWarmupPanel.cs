using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class YouTubeWarmupPanel : UserControl {
        readonly Preferences settings;
        readonly YouTubeBackend backend;
        readonly NumericUpDown minMinutes, maxMinutes, minWatchSec, maxWatchSec, likePercent;
        readonly Panel numHost;
        readonly DataGridView grid;
        readonly RichTextBox log;
        readonly Button marketRu, marketEn, likesToggle;
        string marketView = "RU";
        bool likesOn = true;

        public YouTubeWarmupPanel(Preferences prefs, YouTubeBackend be) {
            settings = prefs;
            backend = be;
            marketView = NormMarket(settings.YouTubeMarketView);
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            numHost = new Panel { Visible = false, Size = new Size(0, 0) };
            Controls.Add(numHost);
            minMinutes = HiddenNum(settings.YouTubeWarmupMinMinutes, 1, 120);
            maxMinutes = HiddenNum(settings.YouTubeWarmupMaxMinutes, 1, 120);
            minWatchSec = HiddenNum(settings.YouTubeWarmupWatchMinSec, 1, 40);
            maxWatchSec = HiddenNum(settings.YouTubeWarmupWatchMaxSec, 1, 40);
            likePercent = HiddenNum(settings.YouTubeWarmupLikePercent, 0, 100);
            likesOn = settings.YouTubeWarmupLikesEnabled;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            root.Controls.Add(split, 0, 0);

            var settingsCard = Theme.MakeCard();
            settingsCard.Dock = DockStyle.Fill;
            settingsCard.Padding = new Padding(16);
            var settingsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = Theme.Card };
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var settingsHead = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, BackColor = Theme.Card };
            settingsHead.Controls.Add(new Label { Text = "Настройки сессии", Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Margin = new Padding(0, 4, 12, 0) });
            settingsHead.Controls.Add(Theme.MakeButton("Сохранить", ghost: true, action: () => { SavePrefs(); statusSaved(); }));
            settingsLayout.Controls.Add(settingsHead, 0, 0);

            var sliders = new FlowLayoutPanel {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = Theme.Card
            };
            sliders.SizeChanged += (s, e) => {
                int w = Math.Max(220, sliders.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
                foreach (Control child in sliders.Controls) child.Width = w;
            };
            sliders.Controls.Add(new AssemblyRangeSlider("Длительность сессии", minMinutes, maxMinutes, "мин") { Width = 340 });
            sliders.Controls.Add(new AssemblyRangeSlider("Просмотр одного ролика (диапазон)", minWatchSec, maxWatchSec, "сек") { Width = 340 });
            sliders.Controls.Add(new Label {
                Text = "Внутри сессии ~80% роликов листаются за 1–10 сек., ~20% — дольше (до 40 сек., иногда до конца). Реклама пролистывается без остановки.",
                ForeColor = Theme.TextMuted, AutoSize = false, Width = 340, MaximumSize = new Size(340, 0), Margin = new Padding(0, 0, 0, 10)
            });

            var likeWrap = new Panel { Width = 340, Height = 132, BackColor = Theme.Card, Margin = new Padding(0, 4, 0, 8) };
            likeWrap.Controls.Add(new Label { Text = "Лайки", ForeColor = Theme.TextPrimary, Location = new Point(12, 8), AutoSize = true });
            likesToggle = Theme.MakeHeroToggle(likesOn ? "Вкл" : "Выкл", likesOn, ToggleLikes);
            likesToggle.Location = new Point(12, 34);
            likeWrap.Controls.Add(likesToggle);
            likeWrap.Controls.Add(new AssemblySlider("Вероятность лайка", likePercent, "%") { Location = new Point(0, 72), Width = 340, Height = 58 });
            sliders.Controls.Add(likeWrap);
            settingsLayout.Controls.Add(sliders, 0, 1);
            settingsCard.Controls.Add(settingsLayout);
            UpdateLikeSlider();
            split.Controls.Add(settingsCard, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Background, Padding = new Padding(8, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

            var accountsCard = Theme.MakeCard();
            accountsCard.Dock = DockStyle.Fill;
            var accLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Card, Padding = new Padding(8) };
            accLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            accLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            accLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var accTitle = new Label { Text = "Аккаунты для прогрева", Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Margin = new Padding(4, 0, 0, 6) };
            accLayout.Controls.Add(accTitle, 0, 0);

            marketRu = Theme.MakeHeroToggle("RU", marketView == "RU", () => SwitchMarket("RU"));
            marketEn = Theme.MakeHeroToggle("EN", marketView == "EN", () => SwitchMarket("EN"));
            marketRu.AutoSize = false; marketRu.Size = new Size(72, Theme.HeroToggleHeight);
            marketEn.AutoSize = false; marketEn.Size = new Size(72, Theme.HeroToggleHeight);
            var marketRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 8) };
            marketRow.Controls.Add(Theme.MakeHeroSegmentGroup("", marketRu, marketEn));
            marketRow.Controls.Add(Theme.MakeButton("Все", ghost: true, action: () => SetAllChecks(true)));
            marketRow.Controls.Add(Theme.MakeButton("Снять", ghost: true, action: () => SetAllChecks(false)));
            accLayout.Controls.Add(marketRow, 0, 1);

            grid = new DataGridView {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ScrollBars = ScrollBars.Vertical
            };
            Theme.StyleGrid(grid);
            grid.RowTemplate.Height = 40;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.ColorFromHex("#212121");
            var onCol = new DataGridViewCheckBoxColumn {
                Name = "on", HeaderText = "✓", Width = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FlatStyle = FlatStyle.Flat, ThreeState = false
            };
            onCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(onCol);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "channel", HeaderText = "Канал", ReadOnly = true, FillWeight = 200 });
            grid.Columns["channel"].DefaultCellStyle.Font = new Font(Theme.FontBody.FontFamily, 10f, FontStyle.Bold);
            grid.CellValueChanged += (s, e) => { if (e.ColumnIndex == 0) PersistChecks(); };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            accLayout.Controls.Add(grid, 0, 2);
            accountsCard.Controls.Add(accLayout);

            var logCard = Theme.MakeCard();
            logCard.Dock = DockStyle.Fill;
            log = Theme.MakeLogBox();
            logCard.Controls.Add(log);
            right.Controls.Add(accountsCard, 0, 0);
            right.Controls.Add(logCard, 0, 1);
            split.Controls.Add(right, 1, 0);

            var bottom = Theme.MakeToolbar();
            bottom.Controls.Add(Theme.MakeButton("Запустить автопрогрев", accent: true, action: async () => await RunWarmup()));
            bottom.Controls.Add(Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop()));
            root.Controls.Add(bottom, 0, 1);

            backend.LogLine += line => AppendLog(line);
            ReloadAccounts();
        }

        void statusSaved() {
            AppendLog("Настройки автопрогрева сохранены.");
        }

        static string NormMarket(string m) => (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU";

        void SwitchMarket(string market) {
            SavePrefs();
            marketView = NormMarket(market);
            settings.YouTubeMarketView = marketView;
            Theme.ApplyHeroToggleStyle(marketRu, marketView == "RU");
            Theme.ApplyHeroToggleStyle(marketEn, marketView == "EN");
            ReloadAccounts();
            try { Store.Save(settings); } catch { }
        }

        NumericUpDown HiddenNum(int value, int min, int max) {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Visible = false };
            numHost.Controls.Add(n);
            return n;
        }

        void ToggleLikes() {
            likesOn = !likesOn;
            Theme.ApplyHeroToggleStyle(likesToggle, likesOn);
            likesToggle.Text = likesOn ? "Вкл" : "Выкл";
            UpdateLikeSlider();
        }

        void UpdateLikeSlider() {
            likePercent.Enabled = likesOn;
            foreach (Control c in Controls) Walk(c);
            void Walk(Control parent) {
                foreach (Control c in parent.Controls) {
                    if (c is AssemblySlider s && s.AccessibleName == "Вероятность лайка") s.Enabled = likesOn;
                    if (c.HasChildren) Walk(c);
                }
            }
        }

        public void ReloadAccounts() {
            grid.Rows.Clear();
            foreach (var ch in settings.YouTubeChannels.Where(c => c != null && !string.IsNullOrWhiteSpace(c.ProfileId) && NormMarket(c.Market) == marketView)
                .OrderBy(c => c.Name)) {
                int i = grid.Rows.Add(ch.Enabled, ch.Name ?? "Канал");
                grid.Rows[i].Tag = ch;
                grid.Rows[i].Cells["channel"].ToolTipText = (ch.Name ?? "") + "\nProfile " + (ch.ProfileId ?? "");
            }
        }

        void SetAllChecks(bool on) {
            foreach (DataGridViewRow row in grid.Rows) {
                row.Cells[0].Value = on;
                if (row.Tag is YouTubeChannel ch) ch.Enabled = on;
            }
            PersistChecks();
        }

        void PersistChecks() {
            foreach (DataGridViewRow row in grid.Rows) {
                if (row.Tag is YouTubeChannel ch)
                    ch.Enabled = Convert.ToBoolean(row.Cells[0].Value ?? false);
            }
            try { Store.Save(settings); } catch { }
            backend.Reload();
        }

        List<YouTubeChannel> SelectedChannels() {
            grid.EndEdit();
            return grid.Rows.Cast<DataGridViewRow>()
                .Where(r => Convert.ToBoolean(r.Cells[0].Value ?? false) && r.Tag is YouTubeChannel)
                .Select(r => (YouTubeChannel)r.Tag)
                .GroupBy(c => (c.ProfileId ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        void AppendLog(string line) {
            if (InvokeRequired) BeginInvoke(new Action<string>(AppendLog), line);
            else log.AppendText(line + Environment.NewLine);
        }

        void SavePrefs() {
            settings.YouTubeWarmupMinMinutes = (int)minMinutes.Value;
            settings.YouTubeWarmupMaxMinutes = (int)maxMinutes.Value;
            if (settings.YouTubeWarmupMaxMinutes < settings.YouTubeWarmupMinMinutes)
                maxMinutes.Value = settings.YouTubeWarmupMaxMinutes = settings.YouTubeWarmupMinMinutes;
            settings.YouTubeWarmupWatchMinSec = (int)minWatchSec.Value;
            settings.YouTubeWarmupWatchMaxSec = (int)maxWatchSec.Value;
            if (settings.YouTubeWarmupWatchMaxSec < settings.YouTubeWarmupWatchMinSec)
                maxWatchSec.Value = settings.YouTubeWarmupWatchMaxSec = settings.YouTubeWarmupWatchMinSec;
            settings.YouTubeWarmupLikesEnabled = likesOn;
            settings.YouTubeWarmupLikePercent = (int)likePercent.Value;
            PersistChecks();
            Store.Save(settings);
        }

        async Task RunWarmup() {
            SavePrefs();
            var list = SelectedChannels();
            if (list.Count == 0) {
                MessageBox.Show(this, "Отметьте галочкой хотя бы один аккаунт в списке " + marketView + ".", "Автопрогрев", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try {
                await backend.RunShortsWarmupAsync(list);
            } catch (Exception ex) {
                AppendLog("ОШИБКА: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Автопрогрев", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
