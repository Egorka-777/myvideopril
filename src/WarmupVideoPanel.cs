using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class WarmupVideoPanel : UserControl {
        public const int BundleSlotCount = 10;

        readonly Preferences settings;
        readonly TextBox[] jokeBoxes = new TextBox[BundleSlotCount];
        readonly Label backgroundCaption;
        readonly ComboBox backgroundPreviewPick;
        readonly TextBox output;
        readonly RichTextBox log;
        readonly WarmupPreviewControl preview;
        readonly Button ru, en, addBackgrounds, removeBackground, chooseOutput, create, stop, results;
        readonly Label status, previewHint, bundleSummary;
        readonly TableLayoutPanel fields, captionEditor, jokesPanel;
        readonly CheckBox captionsEnabled;
        readonly ComboBox captionFont, captionPlate;
        readonly NumericUpDown captionSize, captionPosition, captionWords, captionOpacity;
        readonly Button captionTextColor, captionPlateColor, editCaptions;
        WarmupSubtitleStyle subtitleStyle;
        readonly System.Windows.Forms.Timer saveTimer;
        string market;
        readonly List<string> backgroundFiles = new List<string>();
        bool loading;
        CancellationTokenSource run, previewRun;

        public WarmupVideoPanel(Preferences prefs) {
            settings = prefs; market = NormMarket(prefs.WarmupVideoMarketView);
            Dock = DockStyle.Fill; BackColor = Theme.Background; Font = Theme.FontBody;
            saveTimer = new System.Windows.Forms.Timer { Interval = 400 };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveFields(); UpdateBundleSummary(); };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12), BackColor = Theme.Background };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            Controls.Add(root);
            var card = Theme.MakeCard(); card.Dock = DockStyle.Fill; card.Margin = new Padding(0, 0, 12, 0); card.Padding = new Padding(16);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Card };
            fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Card, Margin = Padding.Empty };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.ClientSizeChanged += (s, e) => fields.MinimumSize = new Size(Math.Max(0, scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth), 0);
            scroll.Controls.Add(fields); card.Controls.Add(scroll); root.Controls.Add(card, 0, 0);

            AddField(Label("Прогрев роликами", Theme.FontCardTitle, Theme.TextPrimary));
            AddField(Label("Связка — анекдот + фон. Фоны распределяются между текстами; использованные пары не повторяются. Длина ролика = длина речи.", Theme.FontBody, Theme.TextSecondary));
            var markets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 8) };
            ru = Theme.MakeHeroToggle("RU", market == "RU", () => SwitchMarket("RU")); en = Theme.MakeHeroToggle("EN", market == "EN", () => SwitchMarket("EN"));
            markets.Controls.Add(ru); markets.Controls.Add(en); AddField(markets);

            backgroundCaption = Label("Фоны: нет · нажмите «Добавить»", Theme.FontBody, Theme.TextSecondary, 4);
            backgroundCaption.Name = "warmup-background-caption"; backgroundCaption.MaximumSize = new Size(640, 0);
            AddField(backgroundCaption);
            var bgActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 4) };
            addBackgrounds = Theme.MakeButton("Добавить файлы", ghost: true, action: PickBackgrounds); addBackgrounds.Name = "warmup-add-backgrounds";
            removeBackground = Theme.MakeButton("Убрать последний", ghost: true, action: RemoveLastBackground); removeBackground.Name = "warmup-remove-background";
            bgActions.Controls.Add(addBackgrounds); bgActions.Controls.Add(removeBackground); AddField(bgActions);
            backgroundPreviewPick = Theme.MakeCombo(Array.Empty<string>()); backgroundPreviewPick.Name = "warmup-background-preview"; backgroundPreviewPick.Width = 420; backgroundPreviewPick.Visible = false;
            backgroundPreviewPick.SelectedIndexChanged += (s, e) => { if (!loading && backgroundPreviewPick.SelectedIndex >= 0) _ = UpdatePreviewAsync(backgroundFiles[backgroundPreviewPick.SelectedIndex]); };
            AddField(backgroundPreviewPick);
            bundleSummary = Label("Добавьте анекдоты и хотя бы один фон", Theme.FontBody, Theme.TextSecondary, 4);
            bundleSummary.MaximumSize = new Size(300, 0);
            AddField(bundleSummary);

            AddField(Label("Анекдоты · одна строка на связку", Theme.FontBody, Theme.TextPrimary, 8));
            jokesPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0, 0, 0, 4) };
            jokesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < BundleSlotCount; i++) {
                jokesPanel.RowCount++; jokesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 34, ColumnCount = 2, Margin = new Padding(0, 0, 0, 3) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.Controls.Add(Label("#" + (i + 1), Theme.FontBody, Theme.TextMuted), 0, 0);
                var box = Theme.MakeSearchBox(); box.Name = "warmup-joke-" + i; box.Multiline = false; box.Height = 28; box.Dock = DockStyle.Fill; box.ScrollBars = ScrollBars.None;
                box.TextChanged += Changed; jokeBoxes[i] = box; row.Controls.Add(box, 1, 0);
                jokesPanel.Controls.Add(row, 0, i);
            }
            AddField(jokesPanel);

            AddField(Label("Папка с роликами", Theme.FontBody, Theme.TextPrimary, 8));
            output = PathBox("warmup-output");
            chooseOutput = Theme.MakeButton("Изменить", ghost: true, action: PickOutput); chooseOutput.Name = "warmup-pick-output";
            AddField(PickerRow(output, chooseOutput));

            var captionHead = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 38, Dock = DockStyle.Top, AutoSize = false, Margin = new Padding(0, 8, 0, 0) };
            captionHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); captionHead.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            captionsEnabled = new CheckBox { Name = "warmup-subtitles-enabled", Text = "Субтитры", AutoSize = true, ForeColor = Theme.TextPrimary, Margin = new Padding(0, 8, 0, 0) };
            editCaptions = Theme.MakeButton("Стили", ghost: true, action: OpenCaptionStyles); editCaptions.AutoSize = false; editCaptions.Dock = DockStyle.Fill; editCaptions.Margin = Padding.Empty;
            captionHead.Controls.Add(captionsEnabled, 0, 0); captionHead.Controls.Add(editCaptions, 1, 0); AddField(captionHead);
            captionEditor = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Margin = new Padding(0, 6, 0, 0) };
            captionEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            captionFont = Theme.MakeCombo(SubtitlePresets.Fonts);
            captionPlate = Theme.MakeCombo(new[] { "Скруглённая", "Прямая", "Без плашки" });
            captionSize = StyleNumber("warmup-subtitle-size", 28, 88); captionPosition = StyleNumber("warmup-subtitle-position", 10, 90);
            captionWords = StyleNumber("warmup-subtitle-words", 1, 6); captionOpacity = StyleNumber("warmup-subtitle-opacity", 0, 100);
            captionEditor.Controls.Add(SettingRow("Шрифт", captionFont)); captionEditor.Controls.Add(SettingRow("Размер текста", captionSize));
            captionEditor.Controls.Add(SettingRow("Положение сверху, % (или тяните в превью)", captionPosition)); captionEditor.Controls.Add(SettingRow("Слов за раз", captionWords));
            captionEditor.Controls.Add(SettingRow("Плашка", captionPlate)); captionEditor.Controls.Add(SettingRow("Плотность плашки, %", captionOpacity));
            var colors = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 38, RowCount = 1, ColumnCount = 2, Margin = new Padding(0, 6, 0, 0) };
            colors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); colors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            captionTextColor = Theme.MakeButton("Цвет текста", action: () => PickCaptionColor(false)); captionTextColor.AutoSize = false; captionTextColor.Dock = DockStyle.Fill;
            captionPlateColor = Theme.MakeButton("Цвет плашки", action: () => PickCaptionColor(true)); captionPlateColor.AutoSize = false; captionPlateColor.Dock = DockStyle.Fill; captionPlateColor.Margin = Padding.Empty;
            colors.Controls.Add(captionTextColor, 0, 0); colors.Controls.Add(captionPlateColor, 1, 0); captionEditor.Controls.Add(colors);
            AddField(captionEditor);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            previewHint = Label("Предпросмотр фона", Theme.FontBody, Theme.TextSecondary); right.Controls.Add(previewHint, 0, 0);
            preview = new WarmupPreviewControl { Dock = DockStyle.Fill, MinimumSize = new Size(100, 140) };
            preview.SubtitlePositionChanged += pos => {
                if (loading || subtitleStyle == null) return;
                loading = true;
                try { captionPosition.Value = Math.Max(captionPosition.Minimum, Math.Min(captionPosition.Maximum, pos)); subtitleStyle.Position = (int)captionPosition.Value; UpdateCaptionPreview(); }
                finally { loading = false; }
                SaveFields();
            };
            right.Controls.Add(preview, 0, 1);
            right.Controls.Add(Label("Ход создания", Theme.FontBody, Theme.TextSecondary, 6), 0, 2);
            log = Theme.MakeLogBox(); log.Dock = DockStyle.Fill; log.Margin = new Padding(0, 6, 0, 0); right.Controls.Add(log, 0, 3); root.Controls.Add(right, 1, 0);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 12, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
            create = Theme.MakeButton("Создать ролики", accent: true, action: async () => await RunGenerate()); create.Name = "warmup-create";
            stop = Theme.MakeButton("Стоп", ghost: true, action: () => { run?.Cancel(); stop.Enabled = false; status.Text = "Останавливаю…"; }); stop.Enabled = false;
            results = Theme.MakeButton("Результаты", action: OpenResults); results.Name = "warmup-results";
            actions.Controls.Add(create); actions.Controls.Add(stop); actions.Controls.Add(results); bottom.Controls.Add(actions, 0, 0);
            status = Label("1080 × 1920 · длина по озвучке", Theme.FontBody, Theme.TextSecondary); status.Margin = new Padding(8, 8, 0, 0); bottom.Controls.Add(status, 1, 0);
            root.Controls.Add(bottom, 0, 1); root.SetColumnSpan(bottom, 2);
            captionsEnabled.CheckedChanged += CaptionChanged; captionFont.SelectedIndexChanged += CaptionChanged; captionPlate.SelectedIndexChanged += CaptionChanged;
            captionSize.ValueChanged += CaptionChanged; captionPosition.ValueChanged += CaptionChanged; captionWords.ValueChanged += CaptionChanged; captionOpacity.ValueChanged += CaptionChanged;
            LoadMarketFields();
        }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            BeginInvoke(new Action(UpdateBundleSummary));
        }
        static Label Label(string text, Font font, Color color, int top = 0) => new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, top, 0, 4) };
        static TextBox PathBox(string name) { var box = Theme.MakeSearchBox(); box.Name = name; box.ReadOnly = true; box.Dock = DockStyle.Fill; box.Margin = new Padding(0, 7, 8, 0); return box; }
        static Control PickerRow(Control box, Button button) {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 40, RowCount = 1, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            button.AutoSize = false; button.Dock = DockStyle.Fill; button.Margin = Padding.Empty; row.Controls.Add(box, 0, 0); row.Controls.Add(button, 1, 0); return row;
        }
        static NumericUpDown StyleNumber(string name, int min, int max) => new NumericUpDown { Name = name, Minimum = min, Maximum = max, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary, Font = Theme.FontBody };
        static Control SettingRow(string title, Control field) {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 40, RowCount = 1, ColumnCount = 2, Margin = new Padding(0, 4, 0, 0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            row.Controls.Add(Label(title, Theme.FontBody, Theme.TextSecondary), 0, 0); field.Dock = DockStyle.Fill; row.Controls.Add(field, 1, 0); return row;
        }
        void CaptionChanged(object sender, EventArgs e) {
            if (loading || subtitleStyle == null) return;
            subtitleStyle.Enabled = captionsEnabled.Checked; subtitleStyle.FontFamily = captionFont.Text;
            subtitleStyle.Plate = captionPlate.SelectedIndex == 2 ? "none" : captionPlate.SelectedIndex == 1 ? "solid" : "rounded";
            subtitleStyle.FontSize = (int)captionSize.Value; subtitleStyle.Position = (int)captionPosition.Value; subtitleStyle.WordsPerCaption = (int)captionWords.Value; subtitleStyle.Opacity = (int)captionOpacity.Value;
            UpdateCaptionPreview(); Changed(sender, e);
        }
        void OpenCaptionStyles() {
            using (var background = preview.CopyBackground())
            using (var dialog = new SubtitleStyleDialog(subtitleStyle, null, background)) {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                loading = true;
                try {
                    subtitleStyle = dialog.Style.Copy(); captionsEnabled.Checked = subtitleStyle.Enabled;
                    captionFont.SelectedItem = subtitleStyle.FontFamily;
                    captionPlate.SelectedIndex = subtitleStyle.Plate == "none" ? 2 : subtitleStyle.Plate == "solid" ? 1 : 0;
                    captionSize.Value = subtitleStyle.FontSize; captionPosition.Value = subtitleStyle.Position;
                    captionWords.Value = subtitleStyle.WordsPerCaption; captionOpacity.Value = subtitleStyle.Opacity;
                    UpdateCaptionPreview();
                } finally { loading = false; }
                SaveFields();
            }
        }
        void UpdateCaptionPreview() {
            captionEditor.Enabled = captionsEnabled.Checked; editCaptions.Enabled = captionsEnabled.Checked;
            captionPlateColor.Enabled = captionOpacity.Enabled = subtitleStyle.Plate != "none";
            Color text = ColorTranslator.FromHtml(subtitleStyle.TextColor), plate = ColorTranslator.FromHtml(subtitleStyle.PlateColor);
            captionTextColor.BackColor = text; captionTextColor.ForeColor = text.GetBrightness() > .5 ? Color.Black : Color.White;
            captionPlateColor.BackColor = plate; captionPlateColor.ForeColor = plate.GetBrightness() > .5 ? Color.Black : Color.White;
            preview.SubtitleDragEnabled = captionsEnabled.Checked;
            preview.SetSubtitles(subtitleStyle, market == "EN" ? "This is how captions look" : "Так выглядят ваши субтитры");
        }
        void PickCaptionColor(bool plate) {
            using (var dialog = new ColorDialog { FullOpen = true, Color = ColorTranslator.FromHtml(plate ? subtitleStyle.PlateColor : subtitleStyle.TextColor) }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string hex = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2");
                if (plate) subtitleStyle.PlateColor = hex; else subtitleStyle.TextColor = hex; UpdateCaptionPreview(); SaveFields();
            }
        }
        void AddField(Control control) { fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize)); fields.Controls.Add(control, 0, fields.RowCount - 1); }
        static string NormMarket(string value) => value == "EN" ? "EN" : "RU";
        void Changed(object sender, EventArgs e) { if (loading) return; CaptureFields(); saveTimer.Stop(); saveTimer.Start(); }
        void SwitchMarket(string value) { if (run != null || value == market) return; SaveFields(); market = NormMarket(value); LoadMarketFields(); SaveFields(); }
        List<string> ReadJokes() => jokeBoxes.Select(b => b.Text).ToList();
        int summaryVersion;
        async void UpdateBundleSummary() {
            if (bundleSummary == null || !IsHandleCreated) return;
            int version = ++summaryVersion;
            var jokes = ReadJokes(); var backgrounds = backgroundFiles.ToArray(); string selectedMarket = market;
            if (!jokes.Any(j => j.Length > 0) || backgrounds.Length == 0) {
                bundleSummary.Text = "Добавьте анекдоты и хотя бы один фон"; bundleSummary.ForeColor = Theme.TextSecondary; return;
            }
            bundleSummary.Text = "Считаю доступные связки…";
            try {
                var inventory = await Task.Run(() => WarmupBundleHistory.Inventory(selectedMarket, jokes, backgrounds));
                if (IsDisposed || version != summaryVersion) return;
                bundleSummary.Text = "Всего: " + inventory.Total + " · использовано: " + inventory.Used + " · осталось: " + inventory.Remaining + " · в следующей сборке: " + inventory.Next.Count;
                bundleSummary.ForeColor = inventory.Remaining > 0 ? Theme.Accent : Theme.Warning;
            } catch (Exception e) {
                if (!IsDisposed && version == summaryVersion) { bundleSummary.Text = "Не удалось прочитать историю: " + e.Message; bundleSummary.ForeColor = Theme.Warning; }
            }
        }
        void RefreshBackgroundList() {
            if (backgroundFiles.Count == 0) {
                backgroundCaption.Text = "Фоны: нет · нажмите «Добавить»";
                backgroundCaption.ForeColor = Theme.TextSecondary;
                backgroundPreviewPick.Visible = false;
                backgroundPreviewPick.Items.Clear();
            } else {
                int missing = backgroundFiles.Count(p => !File.Exists(p));
                backgroundCaption.Text = "Фоны: " + backgroundFiles.Count + (missing > 0 ? " · не найдено на диске: " + missing : "");
                backgroundCaption.ForeColor = missing > 0 ? Theme.Warning : Theme.TextPrimary;
                backgroundPreviewPick.Visible = backgroundFiles.Count > 1;
                int keep = backgroundPreviewPick.SelectedIndex;
                backgroundPreviewPick.Items.Clear();
                foreach (string path in backgroundFiles) backgroundPreviewPick.Items.Add(Path.GetFileName(path));
                if (keep >= 0 && keep < backgroundFiles.Count) backgroundPreviewPick.SelectedIndex = keep;
                else if (backgroundFiles.Count > 0) backgroundPreviewPick.SelectedIndex = backgroundFiles.Count - 1;
            }
            UpdateBundleSummary();
        }
        void LoadMarketFields() {
            loading = true;
            try {
                Theme.ApplyHeroToggleStyle(ru, market == "RU"); Theme.ApplyHeroToggleStyle(en, market == "EN");
                var jokes = (market == "EN" ? settings.WarmupVideoJokesEn : settings.WarmupVideoJokesRu) ?? new List<string>();
                for (int i = 0; i < BundleSlotCount; i++)
                    jokeBoxes[i].Text = i < jokes.Count ? jokes[i] ?? "" : "";
                backgroundFiles.Clear();
                var stored = (market == "EN" ? settings.WarmupVideoBackgroundFilesEn : settings.WarmupVideoBackgroundFilesRu) ?? new List<string>();
                backgroundFiles.AddRange(stored.Where(p => !string.IsNullOrWhiteSpace(p)));
                if (backgroundFiles.Count == 0) {
                    string single = market == "EN" ? settings.WarmupVideoBackgroundFileEn : settings.WarmupVideoBackgroundFileRu;
                    if (!string.IsNullOrWhiteSpace(single)) backgroundFiles.Add(single);
                }
                RefreshBackgroundList();
                string savedOutput = market == "EN" ? settings.WarmupVideoOutputEn : settings.WarmupVideoOutputRu;
                savedOutput = VideoBatchOutputPaths.RemapWarmupOutput(savedOutput, market);
                output.Text = string.IsNullOrWhiteSpace(savedOutput) ? WarmupVideoEngine.DefaultOutputFolderFor(market) : savedOutput;
                subtitleStyle = ((market == "EN" ? settings.WarmupSubtitlesEn : settings.WarmupSubtitlesRu) ?? new WarmupSubtitleStyle()).Copy(); subtitleStyle.Normalize();
                captionsEnabled.Checked = subtitleStyle.Enabled; captionFont.SelectedItem = subtitleStyle.FontFamily; captionPlate.SelectedIndex = subtitleStyle.Plate == "none" ? 2 : subtitleStyle.Plate == "solid" ? 1 : 0;
                captionSize.Value = subtitleStyle.FontSize; captionPosition.Value = subtitleStyle.Position; captionWords.Value = subtitleStyle.WordsPerCaption; captionOpacity.Value = subtitleStyle.Opacity;
                UpdateCaptionPreview();
                if (backgroundFiles.Count > 0) _ = UpdatePreviewAsync(backgroundFiles[0]);
                else preview.SetBackgroundImage(null);
            } finally { loading = false; UpdateBundleSummary(); }
        }
        void CaptureFields() {
            if (loading) return;
            var jokes = ReadJokes();
            jokes = Store.PadWarmupJokes(jokes);
            if (market == "EN") {
                settings.WarmupVideoJokesEn = jokes; settings.WarmupVideoBackgroundFilesEn = new List<string>(backgroundFiles);
                settings.WarmupVideoBackgroundFileEn = backgroundFiles.FirstOrDefault() ?? ""; settings.WarmupVideoOutputEn = output.Text;
            } else {
                settings.WarmupVideoJokesRu = jokes; settings.WarmupVideoBackgroundFilesRu = new List<string>(backgroundFiles);
                settings.WarmupVideoBackgroundFileRu = backgroundFiles.FirstOrDefault() ?? ""; settings.WarmupVideoOutputRu = output.Text;
            }
            if (market == "EN") settings.WarmupSubtitlesEn = subtitleStyle.Copy(); else settings.WarmupSubtitlesRu = subtitleStyle.Copy();
            settings.WarmupVideoMarketView = market;
        }
        void SaveFields() {
            saveTimer.Stop(); if (loading) return;
            CaptureFields(); Store.Save(settings);
        }
        void PickBackgrounds() {
            using (var dialog = new OpenFileDialog { Title = "Фоны для связок", Filter = "Фото и видео|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v", CheckFileExists = true, Multiselect = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (string file in dialog.FileNames) if (!backgroundFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) backgroundFiles.Add(file);
                RefreshBackgroundList(); SaveFields();
                if (backgroundFiles.Count > 0) _ = UpdatePreviewAsync(backgroundFiles[backgroundFiles.Count - 1]);
            }
        }
        void RemoveLastBackground() {
            if (backgroundFiles.Count == 0) return;
            backgroundFiles.RemoveAt(backgroundFiles.Count - 1);
            RefreshBackgroundList(); SaveFields();
            if (backgroundFiles.Count > 0) _ = UpdatePreviewAsync(backgroundFiles[backgroundFiles.Count - 1]); else preview.SetBackgroundImage(null);
        }
        void PickOutput() { string path = Ui.PickFolder(FindForm(), output.Text, "Папка с готовыми роликами"); if (path == null) return; output.Text = path; SaveFields(); }
        async Task UpdatePreviewAsync(string path) {
            previewRun?.Cancel(); var own = previewRun = new CancellationTokenSource(); string temp = null;
            preview.SetBackgroundImage(null); previewHint.Text = "Предпросмотр фона";
            try {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!File.Exists(path)) throw new Exception("Файл фона не найден. Выберите его заново.");
                if (!ToolsLocator.TryResolve(out var ffmpeg, out _, out _)) {
                    if (WarmupVideoEngine.IsVideo(path) || Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase)) throw new Exception("Для предпросмотра нужен FFmpeg.");
                    using (var img = Image.FromFile(path)) preview.SetBackgroundImage(new Bitmap(img)); return;
                }
                temp = Path.Combine(Path.GetTempPath(), "vb-warmup-preview-" + Guid.NewGuid().ToString("N") + ".png");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-i", path, "-frames:v", "1", "-vf", "scale=270:480:force_original_aspect_ratio=increase,crop=270:480,setsar=1", temp }, own.Token, null, 20);
                own.Token.ThrowIfCancellationRequested();
                using (var img = Image.FromFile(temp)) preview.SetBackgroundImage(new Bitmap(img));
                previewHint.Text = WarmupVideoEngine.IsVideo(path) ? "Видео · первый кадр" : "Фото · кадр 9:16";
            } catch (OperationCanceledException) { }
            catch (Exception) { if (!own.IsCancellationRequested && !IsDisposed) previewHint.Text = "Фон не удалось показать. Проверьте файл и FFmpeg."; }
            finally { if (temp != null) { try { File.Delete(temp); } catch { } } if (ReferenceEquals(previewRun, own)) previewRun = null; own.Dispose(); }
        }
        void SetBusy(bool busy) {
            create.Enabled = !busy; stop.Enabled = busy; ru.Enabled = en.Enabled = addBackgrounds.Enabled = removeBackground.Enabled = chooseOutput.Enabled = !busy;
            foreach (var box in jokeBoxes) box.Enabled = !busy;
            captionsEnabled.Enabled = !busy; captionEditor.Enabled = !busy && captionsEnabled.Checked; editCaptions.Enabled = !busy && captionsEnabled.Checked;
        }
        async Task RunGenerate() {
            if (run != null) return;
            SaveFields();
            var jokes = ReadJokes().Where(j => !string.IsNullOrWhiteSpace(j)).ToList();
            string key = WindowsSupport.Unprotect(settings.ProtectedElevenLabsKey);
            if (string.IsNullOrWhiteSpace(key)) key = WindowsSupport.Unprotect(settings.ProtectedKey);
            var job = new WarmupVideoJob {
                Market = market, OutputFolder = output.Text, ElevenLabsKey = key, HttpProxy = WindowsSupport.Unprotect(settings.ProtectedWarmupHttpProxy),
                Subtitles = subtitleStyle.Copy(),
                Jokes = jokes, BackgroundFiles = new List<string>(backgroundFiles)
            };
            run = new CancellationTokenSource(); SetBusy(true); log.Clear(); status.Text = "Создаю…";
            try {
                var files = await WarmupVideoEngine.GenerateAsync(job, Append, run.Token);
                Append("Готово: " + files.Count + " роликов. Папка: " + job.OutputFolder); status.Text = "Готово: " + files.Count;
            } catch (OperationCanceledException) { Append("Остановлено. Готовые ролики остались в папке результатов."); status.Text = "Остановлено"; }
            catch (Exception ex) { Append("Ошибка: " + ex.Message); status.Text = "Не завершено · см. журнал"; }
            finally { run.Dispose(); run = null; if (!IsDisposed) { SetBusy(false); UpdateBundleSummary(); } }
        }
        void OpenResults() {
            try { SaveFields(); Directory.CreateDirectory(output.Text); Process.Start(new ProcessStartInfo(output.Text) { UseShellExecute = true }); }
            catch (Exception ex) { Append("Не удалось открыть папку: " + ex.Message); }
        }
        void Append(string text) {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Append), text); } catch (InvalidOperationException) { } return; }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); log.ScrollToCaret();
        }
        protected override void Dispose(bool disposing) {
            if (disposing) { if (!IsDisposed) { try { SaveFields(); } catch { } } saveTimer.Stop(); saveTimer.Dispose(); run?.Cancel(); previewRun?.Cancel(); }
            base.Dispose(disposing);
        }
    }
}
