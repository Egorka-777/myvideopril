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
        bool likesOn = true;
        Button likesToggle;

        public YouTubeWarmupPanel(Preferences prefs, YouTubeBackend be) {
            settings = prefs;
            backend = be;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            numHost = new Panel { Visible = false, Size = new Size(0, 0) };
            Controls.Add(numHost);
            minMinutes = HiddenNum(settings.YouTubeWarmupMinMinutes, 1, 120);
            maxMinutes = HiddenNum(settings.YouTubeWarmupMaxMinutes, 1, 120);
            minWatchSec = HiddenNum(settings.YouTubeWarmupWatchMinSec, 3, 300);
            maxWatchSec = HiddenNum(settings.YouTubeWarmupWatchMaxSec, 3, 300);
            likePercent = HiddenNum(settings.YouTubeWarmupLikePercent, 0, 100);
            likesOn = settings.YouTubeWarmupLikesEnabled;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            root.Controls.Add(split, 0, 0);

            var settingsCard = Theme.MakeCard();
            settingsCard.Dock = DockStyle.Fill;
            settingsCard.Padding = new Padding(16);
            var sliders = new FlowLayoutPanel {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, BackColor = Theme.Card
            };
            var settingsLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            settingsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            settingsLayout.Controls.Add(new Label{Text="Настройки сессии",Font=Theme.FontCardTitle,ForeColor=Theme.TextPrimary,Dock=DockStyle.Fill},0,0);
            settingsLayout.Controls.Add(sliders,0,1);settingsCard.Controls.Add(settingsLayout);
            sliders.SizeChanged+=(s,e)=>{foreach(Control child in sliders.Controls)child.Width=Math.Max(220,sliders.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-8);};

            sliders.Controls.Add(new AssemblySlider("Мин. время сессии", minMinutes, "мин") { Width = 360 });
            sliders.Controls.Add(new AssemblySlider("Макс. время сессии", maxMinutes, "мин") { Width = 360 });
            sliders.Controls.Add(new AssemblySlider("Мин. просмотр ролика", minWatchSec, "сек") { Width = 360 });
            sliders.Controls.Add(new AssemblySlider("Макс. просмотр ролика", maxWatchSec, "сек") { Width = 360 });

            var likeWrap = new Panel { Width = 360, Height = 150, BackColor = Theme.Card, Margin = new Padding(0, 4, 0, 8) };
            likeWrap.Controls.Add(new Label { Text = "Лайки", ForeColor = Theme.TextPrimary, Location = new Point(12, 8), AutoSize = true });
            likesToggle = Theme.MakeHeroToggle(likesOn ? "Вкл" : "Выкл", likesOn, ToggleLikes);
            likesToggle.Location = new Point(12, 34);
            likeWrap.Controls.Add(likesToggle);
            likeWrap.Controls.Add(new AssemblySlider("Вероятность лайка", likePercent, "%") { Location = new Point(0, 78), Width = 360, Height = 66 });
            sliders.Controls.Add(likeWrap);
            UpdateLikeSlider();

            split.Controls.Add(settingsCard, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Background, Padding = new Padding(12, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

            var accountsCard = Theme.MakeCard();
            accountsCard.Dock = DockStyle.Fill;
            var accHeader = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, AutoSize = false, BackColor = Theme.Card, Padding = new Padding(8, 8, 8, 0) };
            accHeader.Controls.Add(new Label { Text = "Аккаунты для прогрева", Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Margin = new Padding(4, 4, 16, 0) });
            accHeader.Controls.Add(Theme.MakeButton("Все", ghost: true, action: () => SetAllChecks(true)));
            accHeader.Controls.Add(Theme.MakeButton("Снять", ghost: true, action: () => SetAllChecks(false)));
            accHeader.Controls.Add(Theme.MakeButton("Обновить", ghost: true, action: ReloadAccounts));
            var accLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Card, Padding = new Padding(4) };
            accLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            accLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            accLayout.Controls.Add(accHeader, 0, 0);

            grid = new DataGridView {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            Theme.StyleGrid(grid);
            grid.RowTemplate.Height = 44;
            var onCol = new DataGridViewCheckBoxColumn {
                Name = "on", HeaderText = "✓", Width = 44, AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FlatStyle = FlatStyle.Flat, ThreeState = false
            };
            onCol.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(onCol);
            grid.Columns.Add("name", "Канал");
            grid.Columns["name"].ReadOnly = true;
            grid.Columns.Add("profile", "Профиль Dolphin");
            grid.Columns["profile"].ReadOnly = true;
            grid.CellValueChanged += (s, e) => { if (e.ColumnIndex == 0) PersistChecks(); };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            accLayout.Controls.Add(grid, 0, 1);
            accountsCard.Controls.Add(accLayout);

            right.Controls.Add(accountsCard, 0, 0);
            log = Theme.MakeLogBox();
            right.Controls.Add(log, 0, 1);
            split.Controls.Add(right, 1, 0);

            var bottom = Theme.MakeToolbar();
            bottom.Controls.Add(Theme.MakeButton("Запустить автопрогрев", accent: true, action: async () => await RunWarmup()));
            bottom.Controls.Add(Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop()));
            root.Controls.Add(bottom, 0, 1);

            backend.LogLine += line => AppendLog(line);
            ReloadAccounts();
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
            foreach (Control c in Controls) FindSliders(c);
            void FindSliders(Control parent) {
                foreach (Control c in parent.Controls) {
                    if (c is AssemblySlider s && s.AccessibleName == "Вероятность лайка") s.Enabled = likesOn;
                    if (c.HasChildren) FindSliders(c);
                }
            }
        }

        public void ReloadAccounts() {
            grid.Rows.Clear();
            foreach (var ch in settings.YouTubeChannels.Where(c => c != null && !string.IsNullOrWhiteSpace(c.ProfileId)).OrderBy(c => c.Name)) {
                int i = grid.Rows.Add(ch.Enabled, ch.Name ?? "Канал", ch.ProfileId ?? "");
                grid.Rows[i].Tag = ch;
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
                MessageBox.Show(this, "Отметьте галочкой хотя бы один аккаунт.", "Автопрогрев", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
