using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    /// <summary>Поиск канала на YouTube. Сетка просмотров — в разделе YouTube (галочки ✓, кнопка «Сетка просмотров»).</summary>
    public sealed class ViewsSearchPanel : UserControl {
        readonly Preferences settings;
        readonly YouTubeBackend backend;
        readonly RichTextBox log;
        readonly TextBox searchKeys, searchTitle, searchUrl;
        readonly ComboBox searchFilter, marketBox;

        public ViewsSearchPanel(Preferences prefs, YouTubeBackend be) {
            settings = prefs;
            backend = be;
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(new Label {
                Text = "Поиск канала на YouTube. Сетка просмотров — в разделе YouTube: формат Long → отметьте каналы ✓ → «Сетка просмотров».",
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                MaximumSize = new Size(900, 0),
                Dock = DockStyle.Top
            }, 0, 0);

            var card = Theme.MakeCard();
            card.Dock = DockStyle.Fill;
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 2, BackColor = Theme.Card, Padding = new Padding(8) };
            form.Controls.Add(new Label { Text = "Рынок", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 0);
            marketBox = Theme.MakeCombo(new[] { "RU", "EN" });
            marketBox.SelectedItem = NormMarket(settings.YouTubeMarketView);
            marketBox.SelectedIndexChanged += (s, e) => LoadSearchFields();
            form.Controls.Add(marketBox, 1, 0);
            form.Controls.Add(new Label { Text = "Ключи", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 1);
            searchKeys = Theme.MakeSearchBox();
            searchKeys.Multiline = true;
            searchKeys.Height = 80;
            form.Controls.Add(searchKeys, 1, 1);
            form.Controls.Add(new Label { Text = "Заголовок", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 2);
            searchTitle = Theme.MakeSearchBox();
            form.Controls.Add(searchTitle, 1, 2);
            form.Controls.Add(new Label { Text = "Ссылка", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 3);
            searchUrl = Theme.MakeSearchBox();
            form.Controls.Add(searchUrl, 1, 3);
            form.Controls.Add(new Label { Text = "Фильтр", ForeColor = Theme.TextMuted, AutoSize = true }, 0, 4);
            searchFilter = Theme.MakeCombo(new[] { "today", "week", "month", "year", "none" });
            form.Controls.Add(searchFilter, 1, 4);
            card.Controls.Add(new Label { Text = "Поиск YouTube", ForeColor = Theme.TextPrimary, Font = Theme.FontCardTitle, Dock = DockStyle.Top, Height = 28 });
            card.Controls.Add(form);
            root.Controls.Add(card, 0, 1);

            log = Theme.MakeLogBox();
            root.Controls.Add(log, 0, 2);

            var bottom = Theme.MakeToolbar();
            bottom.Controls.Add(Theme.MakeButton("Поиск YouTube", accent: true, action: async () => await RunSearch()));
            bottom.Controls.Add(Theme.MakeButton("Стоп", ghost: true, action: () => backend.Stop()));
            root.Controls.Add(bottom, 0, 3);

            backend.LogLine += line => { if (InvokeRequired) BeginInvoke(new Action<string>(l => log.AppendText(l + "\n")), line); else log.AppendText(line + "\n"); };
            LoadSearchFields();
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

        async Task RunSearch() {
            try {
                SaveSearchFields();
                string m = NormMarket(marketBox.SelectedItem?.ToString());
                backend.SetMarketView(m);
                backend.Reload();
                await backend.RunSearchAsync();
            } catch (Exception ex) { log.AppendText("ОШИБКА: " + ex.Message + Environment.NewLine); }
        }

        public void RefreshData() { LoadSearchFields(); }
    }
}
