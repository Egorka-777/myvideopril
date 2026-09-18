using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class ViewsSearchPanel : UserControl {
        readonly Preferences settings;
        readonly YouTubeBackend backend;
        readonly RichTextBox log;
        readonly TextBox searchKeys, searchTitle, searchUrl;
        readonly ComboBox searchFilter, marketBox;
        readonly CheckedListBox viewers;

        public ViewsSearchPanel(Preferences prefs, YouTubeBackend be) {
            settings = prefs;
            backend = be;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var note = new Label {
                Text = "Сетка просмотров и поиск YouTube — существующие обработчики Dolphin/worker.js.",
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                MaximumSize = new Size(900, 0),
                Dock = DockStyle.Top
            };
            root.Controls.Add(note, 0, 0);

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            root.Controls.Add(split, 0, 1);

            var left = Theme.MakeCard();
            left.Dock = DockStyle.Fill;
            var leftLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Card };
            leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftLayout.Controls.Add(new Label { Text = "Аккаунты-зрители", ForeColor = Theme.TextPrimary, Font = Theme.FontCardTitle, AutoSize = true, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
            marketBox = Theme.MakeCombo(new[] { "RU", "EN" });
            marketBox.SelectedItem = NormMarket(settings.YouTubeMarketView);
            marketBox.SelectedIndexChanged += (s, e) => LoadViewers();
            leftLayout.Controls.Add(marketBox, 0, 1);
            viewers = new CheckedListBox { Dock = DockStyle.Fill, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.None, CheckOnClick = true };
            leftLayout.Controls.Add(viewers, 0, 2);
            left.Controls.Add(leftLayout);
            split.Controls.Add(left, 0, 0);

            var right = Theme.MakeCard();
            right.Dock = DockStyle.Fill;
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 2 };
            form.Controls.Add(new Label { Text = "Ключи", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 0);
            searchKeys = Theme.MakeSearchBox();
            searchKeys.Multiline = true;
            searchKeys.Height = 60;
            form.Controls.Add(searchKeys, 1, 0);
            form.Controls.Add(new Label { Text = "Заголовок", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 1);
            searchTitle = Theme.MakeSearchBox();
            form.Controls.Add(searchTitle, 1, 1);
            form.Controls.Add(new Label { Text = "Ссылка", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 2);
            searchUrl = Theme.MakeSearchBox();
            form.Controls.Add(searchUrl, 1, 2);
            form.Controls.Add(new Label { Text = "Фильтр", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 3);
            searchFilter = Theme.MakeCombo(new[] { "today", "week", "month", "year", "none" });
            form.Controls.Add(searchFilter, 1, 3);
            right.Controls.Add(new Label { Text = "Поиск YouTube", ForeColor = Theme.TextPrimary, Font = Theme.FontCardTitle, Dock = DockStyle.Top, Height = 28 });
            right.Controls.Add(form);
            split.Controls.Add(right, 1, 0);

            log = Theme.MakeLogBox();
            root.Controls.Add(log, 0, 2);

            var bottom = Theme.MakeToolbar();
            bottom.Controls.Add(Theme.MakeButton("Запустить сетку", accent: true, action: async () => await RunMesh()));
            bottom.Controls.Add(Theme.MakeButton("Поиск YouTube", ghost: true, action: async () => await RunSearch()));
            bottom.Controls.Add(Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop()));
            root.Controls.Add(bottom, 0, 3);

            backend.LogLine += line => { if (InvokeRequired) BeginInvoke(new Action<string>(l => log.AppendText(l + "\n")), line); else log.AppendText(line + "\n"); };
            LoadSearchFields();
            LoadViewers();
        }

        static string NormMarket(string m) { return (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU"; }

        void LoadSearchFields() {
            string m = NormMarket(marketBox?.SelectedItem?.ToString() ?? settings.YouTubeMarketView);
            searchKeys.Text = m == "EN" ? settings.YouTubeSearchKeysEn : settings.YouTubeSearchKeysRu;
            searchTitle.Text = m == "EN" ? settings.YouTubeSearchFullTitleEn : settings.YouTubeSearchFullTitleRu;
            searchUrl.Text = m == "EN" ? settings.YouTubeSearchUrlEn : settings.YouTubeSearchUrlRu;
            string f = m == "EN" ? settings.YouTubeSearchFilterEn : settings.YouTubeSearchFilterRu;
            if (!string.IsNullOrWhiteSpace(f)) searchFilter.SelectedItem = f;
        }

        void SaveSearchFields() {
            string m = NormMarket(marketBox.SelectedItem?.ToString());
            if (m == "EN") {
                settings.YouTubeSearchKeysEn = searchKeys.Text;
                settings.YouTubeSearchFullTitleEn = searchTitle.Text;
                settings.YouTubeSearchUrlEn = searchUrl.Text;
                settings.YouTubeSearchFilterEn = searchFilter.SelectedItem?.ToString() ?? "today";
            } else {
                settings.YouTubeSearchKeysRu = searchKeys.Text;
                settings.YouTubeSearchFullTitleRu = searchTitle.Text;
                settings.YouTubeSearchUrlRu = searchUrl.Text;
                settings.YouTubeSearchFilterRu = searchFilter.SelectedItem?.ToString() ?? "today";
            }
            try { Store.Save(settings); } catch { }
        }

        void LoadViewers() {
            viewers.Items.Clear();
            string m = NormMarket(marketBox.SelectedItem?.ToString());
            LoadSearchFields();
            foreach (var ch in settings.YouTubeChannels ?? new System.Collections.Generic.List<YouTubeChannel>())
                if (ch != null && NormMarket(ch.Market) == m)
                    viewers.Items.Add(ch, ch.Enabled);
        }

        async Task RunMesh() {
            try {
                SaveSearchFields();
                string m = NormMarket(marketBox.SelectedItem?.ToString());
                backend.SetMarketView(m);
                backend.Reload();
                for (int i = 0; i < viewers.Items.Count; i++) {
                    if (viewers.GetItemChecked(i) && viewers.Items[i] is YouTubeChannel ch)
                        ch.Enabled = true;
                }
                await backend.RunMeshWatchAsync();
            } catch (Exception ex) { log.AppendText("ОШИБКА: " + ex.Message + Environment.NewLine); }
        }

        async Task RunSearch() {
            try {
                SaveSearchFields();
                string m = NormMarket(marketBox.SelectedItem?.ToString());
                backend.SetMarketView(m);
                backend.Reload();
                await backend.RunSearchAsync();
            } catch (Exception ex) { log.AppendText("ОШИБКА: " + ex.Message + Environment.NewLine); }
        }

        public void RefreshData() { LoadViewers(); }
    }
}
