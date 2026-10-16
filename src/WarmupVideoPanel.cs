using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class WarmupVideoPanel : UserControl {
        readonly Preferences settings;
        readonly NumericUpDown countNum, maxSecNum;
        readonly Panel numHost;
        readonly TextBox promptBox, bgFolder, outFolder;
        readonly RichTextBox log;
        readonly WarmupPreviewControl preview;
        readonly Button overlayNone, overlayShorts, overlayTikTok;
        readonly Button btnRu, btnEn;
        string market = "RU";
        CancellationTokenSource run;

        public WarmupVideoPanel(Preferences prefs) {
            settings = prefs;
            market = NormMarket(settings.WarmupVideoMarketView);
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;

            numHost = new Panel { Visible = false, Size = new Size(0, 0) };
            Controls.Add(numHost);
            countNum = HiddenNum(10, 1, 100);
            maxSecNum = HiddenNum(8, 3, 60);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Background };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Theme.Background };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Background, Padding = new Padding(0, 0, 0, 8) };
            btnRu = Theme.MakeHeroToggle("RU", market == "RU", () => SwitchMarket("RU"));
            btnEn = Theme.MakeHeroToggle("EN", market == "EN", () => SwitchMarket("EN"));
            head.Controls.Add(Theme.MakeHeroSegmentGroup("Рынок", btnRu, btnEn));
            left.Controls.Add(head, 0, 0);

            var card = Theme.MakeCard();
            card.Dock = DockStyle.Fill;
            var form = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Card, Padding = new Padding(12) };
            form.Controls.Add(new Label { Text = "Прогревочные ролики", Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
            form.Controls.Add(new Label {
                Text = "OpenRouter пишет тексты → ElevenLabs озвучивает (случайный голос) → FFmpeg собирает короткий MP4.",
                ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 10)
            });
            promptBox = Theme.MakeSearchBox();
            promptBox.Multiline = true;
            promptBox.Height = 88;
            promptBox.Width = 400;
            form.Controls.Add(Labeled("Задача для нейросети", promptBox));
            form.Controls.Add(new AssemblySlider("Количество роликов", countNum, "шт") { Width = 400, Margin = new Padding(0, 4, 0, 0) });
            form.Controls.Add(new AssemblySlider("Макс. длина ролика", maxSecNum, "сек") { Width = 400, Margin = new Padding(0, 4, 0, 0) });
            bgFolder = Theme.MakeSearchBox(); bgFolder.Width = 280;
            form.Controls.Add(FolderRow("Фон (фото/видео)", bgFolder, () => PickFolder(bgFolder)));
            outFolder = Theme.MakeSearchBox(); outFolder.Width = 280;
            form.Controls.Add(FolderRow("Куда сохранять", outFolder, () => PickFolder(outFolder)));
            card.Controls.Add(form);
            left.Controls.Add(card, 0, 1);
            root.Controls.Add(left, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Background, Padding = new Padding(12, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            var overlayRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Background };
            overlayRow.Controls.Add(new Label { Text = "Безопасная зона", ForeColor = Theme.TextSecondary, AutoSize = true, Margin = new Padding(0, 8, 12, 0) });
            overlayNone = Theme.MakeHeroToggle("Без UI", true, () => SetOverlay("none"));
            overlayShorts = Theme.MakeHeroToggle("Shorts", false, () => SetOverlay("shorts"));
            overlayTikTok = Theme.MakeHeroToggle("TikTok", false, () => SetOverlay("tiktok"));
            overlayRow.Controls.Add(Theme.MakeHeroSegmentGroup("", overlayNone, overlayShorts, overlayTikTok));
            right.Controls.Add(overlayRow, 0, 0);
            preview = new WarmupPreviewControl { Dock = DockStyle.Fill };
            right.Controls.Add(preview, 0, 1);
            log = Theme.MakeLogBox();
            right.Controls.Add(log, 0, 2);
            root.Controls.Add(right, 1, 0);

            var bottom = Theme.MakeToolbar();
            bottom.Controls.Add(Theme.MakeButton("Сгенерировать", accent: true, action: async () => await RunGenerate()));
            bottom.Controls.Add(Theme.MakeButton("Стоп", ghost: true, action: () => run?.Cancel()));
            bottom.Controls.Add(Theme.MakeButton("Сохранить", action: SaveFields));
            root.SetColumnSpan(bottom, 2);
            root.Controls.Add(bottom, 0, 1);

            LoadMarketFields();
        }

        static Control Labeled(string caption, Control field) {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Width = 420, Margin = new Padding(0, 0, 0, 8) };
            p.Controls.Add(new Label { Text = caption, ForeColor = Theme.TextMuted, AutoSize = true });
            p.Controls.Add(field);
            return p;
        }

        Control FolderRow(string caption, TextBox box, Action pick) {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Width = 420, Margin = new Padding(0, 0, 0, 8) };
            row.Controls.Add(new Label { Text = caption, ForeColor = Theme.TextMuted, AutoSize = true, Width = 420 });
            box.Margin = new Padding(0, 4, 8, 0);
            row.Controls.Add(box);
            row.Controls.Add(Theme.MakeButton("…", ghost: true, action: pick));
            return row;
        }

        NumericUpDown HiddenNum(int value, int min, int max) {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Visible = false };
            numHost.Controls.Add(n);
            return n;
        }

        static string NormMarket(string m) => (m ?? "").Trim().ToUpperInvariant() == "EN" ? "EN" : "RU";

        void SwitchMarket(string m) {
            SaveFields();
            market = NormMarket(m);
            settings.WarmupVideoMarketView = market;
            Theme.ApplyHeroToggleStyle(btnRu, market == "RU");
            Theme.ApplyHeroToggleStyle(btnEn, market == "EN");
            LoadMarketFields();
        }

        void LoadMarketFields() {
            promptBox.Text = market == "EN" ? settings.WarmupVideoPromptEn : settings.WarmupVideoPromptRu;
            countNum.Value = Math.Max(countNum.Minimum, Math.Min(countNum.Maximum, market == "EN" ? settings.WarmupVideoCountEn : settings.WarmupVideoCountRu));
            maxSecNum.Value = Math.Max(maxSecNum.Minimum, Math.Min(maxSecNum.Maximum, market == "EN" ? settings.WarmupVideoMaxSecEn : settings.WarmupVideoMaxSecRu));
            bgFolder.Text = market == "EN" ? settings.WarmupVideoBackgroundsEn : settings.WarmupVideoBackgroundsRu;
            outFolder.Text = market == "EN" ? settings.WarmupVideoOutputEn : settings.WarmupVideoOutputRu;
            if (string.IsNullOrWhiteSpace(outFolder.Text))
                outFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch", "Warmup-" + market);
            SetOverlay(settings.WarmupVideoPreviewOverlay);
        }

        void SaveFields() {
            if (market == "EN") {
                settings.WarmupVideoPromptEn = promptBox.Text.Trim();
                settings.WarmupVideoCountEn = (int)countNum.Value;
                settings.WarmupVideoMaxSecEn = (int)maxSecNum.Value;
                settings.WarmupVideoBackgroundsEn = bgFolder.Text.Trim();
                settings.WarmupVideoOutputEn = outFolder.Text.Trim();
            } else {
                settings.WarmupVideoPromptRu = promptBox.Text.Trim();
                settings.WarmupVideoCountRu = (int)countNum.Value;
                settings.WarmupVideoMaxSecRu = (int)maxSecNum.Value;
                settings.WarmupVideoBackgroundsRu = bgFolder.Text.Trim();
                settings.WarmupVideoOutputRu = outFolder.Text.Trim();
            }
            settings.WarmupVideoMarketView = market;
            Store.Save(settings);
        }

        void SetOverlay(string mode) {
            mode = (mode ?? "none").Trim().ToLowerInvariant();
            settings.WarmupVideoPreviewOverlay = mode;
            Theme.ApplyHeroToggleStyle(overlayNone, mode == "none");
            Theme.ApplyHeroToggleStyle(overlayShorts, mode == "shorts");
            Theme.ApplyHeroToggleStyle(overlayTikTok, mode == "tiktok");
            preview.SetOverlay(mode);
            Store.Save(settings);
        }

        void PickFolder(TextBox box) {
            var dir = Ui.PickFolder(FindForm(), box.Text, "Выберите папку");
            if (dir != null) box.Text = dir;
        }

        async Task RunGenerate() {
            if (run != null) return;
            SaveFields();
            string orKey = WindowsSupport.Unprotect(settings.ProtectedOpenRouterKey);
            string elKey = WindowsSupport.Unprotect(settings.ProtectedElevenLabsKey);
            if (string.IsNullOrWhiteSpace(elKey)) elKey = WindowsSupport.Unprotect(settings.ProtectedKey);
            var job = new WarmupVideoJob {
                Market = market,
                Prompt = promptBox.Text.Trim(),
                Count = (int)countNum.Value,
                MaxSeconds = (int)maxSecNum.Value,
                BackgroundsFolder = bgFolder.Text.Trim(),
                OutputFolder = outFolder.Text.Trim(),
                OpenRouterKey = orKey,
                ElevenLabsKey = elKey,
                OpenRouterModel = settings.OpenRouterModel ?? "openai/gpt-4o-mini"
            };
            run = new CancellationTokenSource();
            try {
                Append("Старт…");
                var files = await WarmupVideoEngine.GenerateAsync(job, Append, run.Token).ConfigureAwait(true);
                Append("Готово: " + files.Count + " файлов в " + job.OutputFolder);
                MessageBox.Show(this, "Создано роликов: " + files.Count, "Прогревочные ролики", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (OperationCanceledException) { Append("Остановлено."); }
            catch (Exception ex) { Append("ОШИБКА: " + ex.Message); MessageBox.Show(this, ex.Message, "Прогревочные ролики", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { run.Dispose(); run = null; }
        }

        void Append(string line) {
            if (InvokeRequired) BeginInvoke(new Action<string>(Append), line);
            else log.AppendText(line + Environment.NewLine);
        }
    }
}
