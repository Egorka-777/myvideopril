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
        readonly ListBox backgroundList;
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
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveFields(); };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12), BackColor = Theme.Background };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            Controls.Add(root);
            var card = Theme.MakeCard(); card.Dock = DockStyle.Fill; card.Margin = new Padding(0, 0, 12, 0); card.Padding = new Padding(16);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Card };
            fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Card, Margin = Padding.Empty };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(fields); card.Controls.Add(scroll); root.Controls.Add(card, 0, 0);

            AddField(Label("Прогрев роликами", Theme.FontCardTitle, Theme.TextPrimary));
            AddField(Label("Как в «Сборке»: каждая связка — свой фон и свой анекдот. Текст озвучивается в ElevenLabs, длина ролика = длина речи (до 10 с).", Theme.FontBody, Theme.TextSecondary));
            var markets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 8) };
            ru = Theme.MakeHeroToggle("RU", market == "RU", () => SwitchMarket("RU")); en = Theme.MakeHeroToggle("EN", market == "EN", () => SwitchMarket("EN"));
            markets.Controls.Add(ru); markets.Controls.Add(en); AddField(markets);

            AddField(Label("Фоны · фото или видео", Theme.FontBody, Theme.TextPrimary, 8));
            backgroundList = new ListBox { Name = "warmup-background-list", Height = 96, Dock = DockStyle.Top, IntegralHeight = false, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.None };
            backgroundList.SelectedIndexChanged += (s, e) => { if (!loading && backgroundList.SelectedIndex >= 0) _ = UpdatePreviewAsync(backgroundFiles[backgroundList.SelectedIndex]); };
            AddField(backgroundList);
            var bgActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
            addBackgrounds = Theme.MakeButton("Добавить файлы", ghost: true, action: PickBackgrounds); addBackgrounds.Name = "warmup-add-backgrounds";
            removeBackground = Theme.MakeButton("Убрать", ghost: true, action: RemoveSelectedBackground); removeBackground.Name = "warmup-remove-background";
            bgActions.Controls.Add(addBackgrounds); bgActions.Controls.Add(removeBackground); AddField(bgActions);
            bundleSummary = Label("Связок: 0 · заполните одинаковое число фонов и анекдотов", Theme.FontBody, Theme.TextSecondary, 4);
            AddField(bundleSummary);

            AddField(Label("Анекдоты (по одному на связку)", Theme.FontBody, Theme.TextPrimary, 8));
            var jokesScroll = new Panel { Dock = DockStyle.Top, Height = 228, AutoScroll = true, BackColor = Theme.Card, Margin = new Padding(0, 0, 0, 4) };
            jokesPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
            jokesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < BundleSlotCount; i++) {
                jokesPanel.RowCount++; jokesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 44, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.Controls.Add(Label("Связка " + (i + 1), Theme.FontBody, Theme.TextMuted), 0, 0);
                var box = Theme.MakeSearchBox(); box.Name = "warmup-joke-" + i; box.Multiline = true; box.Height = 40; box.Dock = DockStyle.Fill; box.ScrollBars = ScrollBars.Vertical;
                box.TextChanged += Changed; jokeBoxes[i] = box; row.Controls.Add(box, 1, 0);
                jokesPanel.Controls.Add(row, 0, i);
            }
            jokesScroll.Controls.Add(jokesPanel);
            AddField(jokesScroll);

            AddField(Label("Папка с роликами", Theme.FontBody, Theme.TextPrimary, 8));
            output = PathBox("warmup-output");
            chooseOutput = Theme.MakeButton("Изменить", ghost: true, action: PickOutput); chooseOutput.Name = "warmup-pick-output";
            AddField(PickerRow(output, chooseOutput));

            var captionHead = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 38, Dock = DockStyle.Top, AutoSize = false, Margin = new Padding(0, 8, 0, 0) };
            captionHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); captionHead.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            captionsEnabled = new CheckBox { Name = "warmup-subtitles-enabled", Text = "Субтитры", AutoSize = true, ForeColor = Theme.TextPrimary, Margin = new Padding(0, 8, 0, 0) };
            editCaptions = Theme.MakeButton("Настроить", ghost: true, action: () => { captionEditor.Visible = !captionEditor.Visible; }); editCaptions.AutoSize = false; editCaptions.Dock = DockStyle.Fill; editCaptions.Margin = Padding.Empty;
            captionHead.Controls.Add(captionsEnabled, 0, 0); captionHead.Controls.Add(editCaptions, 1, 0); AddField(captionHead);
            captionEditor = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, Margin = new Padding(0, 6, 0, 0) };
            captionEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            captionFont = Theme.MakeCombo(new[] { "Segoe UI", "Arial", "Tahoma" });
            captionPlate = Theme.MakeCombo(new[] { "Скруглённая", "Прямая", "Без плашки" });
            captionSize = StyleNumber("warmup-subtitle-size", 28, 88); captionPosition = StyleNumber("warmup-subtitle-position", 10, 90);
            captionWords = StyleNumber("warmup-subtitle-words", 1, 6); captionOpacity = StyleNumber("warmup-subtitle-opacity", 0, 100);
            captionEditor.Controls.Add(SettingRow("Шрифт", captionFont)); captionEditor.Controls.Add(SettingRow("Размер текста", captionSize));
            captionEditor.Controls.Add(SettingRow("Положение сверху, %", captionPosition)); captionEditor.Controls.Add(SettingRow("Слов за раз", captionWords));
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
            preview = new WarmupPreviewControl { Dock = DockStyle.Fill, MinimumSize = new Size(100, 140) }; right.Controls.Add(preview, 0, 1);
            right.Controls.Add(Label("Ход создания", Theme.FontBody, Theme.TextSecondary, 6), 0, 2);
            log = Theme.MakeLogBox(); log.Dock = DockStyle.Fill; log.Margin = new Padding(0, 6, 0, 0); right.Controls.Add(log, 0, 3); root.Controls.Add(right, 1, 0);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 12, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
            create = Theme.MakeButton("Создать ролики", accent: true, action: async () => await RunGenerate()); create.Name = "warmup-create";
            stop = Theme.MakeButton("Стоп", ghost: true, action: () => { run?.Cancel(); stop.Enabled = false; status.Text = "Останавливаю…"; }); stop.Enabled = false;
            results = Theme.MakeButton("Результаты", action: OpenResults); results.Name = "warmup-results";
            actions.Controls.Add(create); actions.Controls.Add(stop); actions.Controls.Add(results); bottom.Controls.Add(actions, 0, 0);
            status = Label("До 10 секунд · 1080 × 1920", Theme.FontBody, Theme.TextSecondary); status.Margin = new Padding(8, 8, 0, 0); bottom.Controls.Add(status, 1, 0);
            root.Controls.Add(bottom, 0, 1); root.SetColumnSpan(bottom, 2);
            captionsEnabled.CheckedChanged += CaptionChanged; captionFont.SelectedIndexChanged += CaptionChanged; captionPlate.SelectedIndexChanged += CaptionChanged;
            captionSize.ValueChanged += CaptionChanged; captionPosition.ValueChanged += CaptionChanged; captionWords.ValueChanged += CaptionChanged; captionOpacity.ValueChanged += CaptionChanged;
            LoadMarketFields();
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
        void UpdateCaptionPreview() {
            captionEditor.Enabled = captionsEnabled.Checked; editCaptions.Enabled = captionsEnabled.Checked;
            captionPlateColor.Enabled = captionOpacity.Enabled = subtitleStyle.Plate != "none";
            Color text = ColorTranslator.FromHtml(subtitleStyle.TextColor), plate = ColorTranslator.FromHtml(subtitleStyle.PlateColor);
            captionTextColor.BackColor = text; captionTextColor.ForeColor = text.GetBrightness() > .5 ? Color.Black : Color.White;
            captionPlateColor.BackColor = plate; captionPlateColor.ForeColor = plate.GetBrightness() > .5 ? Color.Black : Color.White;
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
        void Changed(object sender, EventArgs e) { if (loading) return; UpdateBundleSummary(); saveTimer.Stop(); saveTimer.Start(); }
        void SwitchMarket(string value) { if (run != null || value == market) return; SaveFields(); market = NormMarket(value); LoadMarketFields(); SaveFields(); }
        List<string> ReadJokes() => jokeBoxes.Select(b => b.Text.Trim()).ToList();
        void UpdateBundleSummary() {
            int jokes = ReadJokes().Count(j => j.Length > 0);
            int bgs = backgroundFiles.Count;
            bundleSummary.Text = jokes == bgs && jokes > 0
                ? "Готово к созданию: " + jokes + " связок"
                : "Связок: тексты " + jokes + ", фоны " + bgs + " · числа должны совпадать";
            bundleSummary.ForeColor = jokes == bgs && jokes > 0 ? Theme.Accent : Theme.TextSecondary;
        }
        void RefreshBackgroundList() {
            backgroundList.Items.Clear();
            foreach (string path in backgroundFiles) backgroundList.Items.Add(Path.GetFileName(path));
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
                string oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch", "Warmup-" + market);
                output.Text = string.IsNullOrWhiteSpace(savedOutput) || string.Equals(savedOutput, oldDefault, StringComparison.OrdinalIgnoreCase) ? WarmupVideoEngine.DefaultOutputFolder : savedOutput;
                subtitleStyle = ((market == "EN" ? settings.WarmupSubtitlesEn : settings.WarmupSubtitlesRu) ?? new WarmupSubtitleStyle()).Copy(); subtitleStyle.Normalize();
                captionsEnabled.Checked = subtitleStyle.Enabled; captionFont.SelectedItem = subtitleStyle.FontFamily; captionPlate.SelectedIndex = subtitleStyle.Plate == "none" ? 2 : subtitleStyle.Plate == "solid" ? 1 : 0;
                captionSize.Value = subtitleStyle.FontSize; captionPosition.Value = subtitleStyle.Position; captionWords.Value = subtitleStyle.WordsPerCaption; captionOpacity.Value = subtitleStyle.Opacity;
                UpdateCaptionPreview();
                if (backgroundFiles.Count > 0) _ = UpdatePreviewAsync(backgroundFiles[0]);
                else preview.SetBackgroundImage(null);
            } finally { loading = false; UpdateBundleSummary(); }
        }
        void SaveFields() {
            saveTimer.Stop(); if (loading) return;
            var jokes = ReadJokes();
            if (market == "EN") {
                settings.WarmupVideoJokesEn = jokes; settings.WarmupVideoBackgroundFilesEn = new List<string>(backgroundFiles);
                settings.WarmupVideoBackgroundFileEn = backgroundFiles.FirstOrDefault() ?? ""; settings.WarmupVideoOutputEn = output.Text;
            } else {
                settings.WarmupVideoJokesRu = jokes; settings.WarmupVideoBackgroundFilesRu = new List<string>(backgroundFiles);
                settings.WarmupVideoBackgroundFileRu = backgroundFiles.FirstOrDefault() ?? ""; settings.WarmupVideoOutputRu = output.Text;
            }
            if (market == "EN") settings.WarmupSubtitlesEn = subtitleStyle.Copy(); else settings.WarmupSubtitlesRu = subtitleStyle.Copy();
            settings.WarmupVideoMarketView = market; Store.Save(settings);
        }
        void PickBackgrounds() {
            using (var dialog = new OpenFileDialog { Title = "Фоны для связок", Filter = "Фото и видео|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v", CheckFileExists = true, Multiselect = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (string file in dialog.FileNames) if (!backgroundFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) backgroundFiles.Add(file);
                RefreshBackgroundList(); SaveFields();
                if (backgroundFiles.Count > 0) _ = UpdatePreviewAsync(backgroundFiles[backgroundFiles.Count - 1]);
            }
        }
        void RemoveSelectedBackground() {
            if (backgroundList.SelectedIndex < 0) return;
            backgroundFiles.RemoveAt(backgroundList.SelectedIndex);
            RefreshBackgroundList(); SaveFields();
            if (backgroundFiles.Count > 0) _ = UpdatePreviewAsync(backgroundFiles[0]); else preview.SetBackgroundImage(null);
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
            var jokes = ReadJokes().Where(j => j.Length > 0).ToList();
            string key = WindowsSupport.Unprotect(settings.ProtectedElevenLabsKey);
            if (string.IsNullOrWhiteSpace(key)) key = WindowsSupport.Unprotect(settings.ProtectedKey);
            var job = new WarmupVideoJob {
                Market = market, OutputFolder = output.Text, ElevenLabsKey = key, Subtitles = subtitleStyle.Copy(),
                Jokes = jokes, BackgroundFiles = new List<string>(backgroundFiles)
            };
            run = new CancellationTokenSource(); SetBusy(true); log.Clear(); status.Text = "Создаю…";
            try {
                var files = await WarmupVideoEngine.GenerateAsync(job, Append, run.Token);
                Append("Готово: " + files.Count + " роликов. Папка: " + job.OutputFolder); status.Text = "Готово: " + files.Count;
            } catch (OperationCanceledException) { Append("Остановлено. Готовые ролики остались в папке результатов."); status.Text = "Остановлено"; }
            catch (Exception ex) { Append("Ошибка: " + ex.Message); status.Text = "Не завершено · см. журнал"; }
            finally { run.Dispose(); run = null; if (!IsDisposed) SetBusy(false); }
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
