using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class WarmupVideoPanel : UserControl {
        readonly Preferences settings;
        readonly NumericUpDown count;
        readonly TextBox prompt, background, output;
        readonly RichTextBox log;
        readonly WarmupPreviewControl preview;
        readonly Button ru, en, chooseBackground, chooseOutput, create, stop, results;
        readonly Label status, previewHint;
        readonly TableLayoutPanel fields, captionEditor;
        readonly CheckBox captionsEnabled;
        readonly ComboBox captionFont, captionPlate;
        readonly NumericUpDown captionSize, captionPosition, captionWords, captionOpacity;
        readonly Button captionTextColor, captionPlateColor, editCaptions;
        WarmupSubtitleStyle subtitleStyle;
        readonly System.Windows.Forms.Timer saveTimer;
        string market, backgroundPath = "";
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
            fields = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Theme.Card, Margin = Padding.Empty };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(fields); card.Controls.Add(scroll); root.Controls.Add(card, 0, 0);
            scroll.ClientSizeChanged += (s, e) => { fields.Width = Math.Max(180, scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2); };
            AddField(Label("Короткий анекдот", Theme.FontCardTitle, Theme.TextPrimary));
            AddField(Label("Выберите фон и тему — остальное соберётся автоматически.", Theme.FontBody, Theme.TextSecondary));
            var markets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 12) };
            ru = Theme.MakeHeroToggle("RU", market == "RU", () => SwitchMarket("RU")); en = Theme.MakeHeroToggle("EN", market == "EN", () => SwitchMarket("EN"));
            markets.Controls.Add(ru); markets.Controls.Add(en); AddField(markets);
            AddField(Label("Фон", Theme.FontBody, Theme.TextPrimary));
            background = PathBox("warmup-background");
            chooseBackground = Theme.MakeButton("Выбрать", ghost: true, action: PickBackground); chooseBackground.Name = "warmup-pick-background";
            AddField(PickerRow(background, chooseBackground));
            AddField(Label("Фото или видео · вертикальный кадр 9:16", Theme.FontBody, Theme.TextMuted));
            AddField(Label("О чём анекдот", Theme.FontBody, Theme.TextPrimary, 14));
            prompt = Theme.MakeSearchBox(); prompt.Name = "warmup-prompt"; prompt.Multiline = true; prompt.Height = 90; prompt.Dock = DockStyle.Fill; prompt.Margin = new Padding(0, 4, 0, 0); prompt.ScrollBars = ScrollBars.Vertical;
            AddField(prompt);
            var quantity = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 14, 0, 0) };
            quantity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); quantity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            quantity.Controls.Add(Label("Количество роликов", Theme.FontBody, Theme.TextPrimary), 0, 0);
            count = new NumericUpDown { Name = "warmup-count", Minimum = 1, Maximum = 100, Value = 1, Dock = DockStyle.Fill, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary, Font = Theme.FontBody, Margin = new Padding(0, 0, 0, 2) };
            quantity.Controls.Add(count, 1, 0); AddField(quantity);
            AddField(Label("Длина — по озвучке, до 10 секунд. Без пауз в начале и конце.", Theme.FontBody, Theme.TextSecondary, 8));
            AddField(Label("Папка с роликами", Theme.FontBody, Theme.TextPrimary, 14));
            output = PathBox("warmup-output");
            chooseOutput = Theme.MakeButton("Изменить", ghost: true, action: PickOutput); chooseOutput.Name = "warmup-pick-output";
            AddField(PickerRow(output, chooseOutput));
            AddField(Label("Используются ключи OpenRouter и ElevenLabs из настроек.", Theme.FontBody, Theme.TextMuted, 12));

            var captionHead = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
            captionHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); captionHead.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            captionsEnabled = new CheckBox { Name = "warmup-subtitles-enabled", Text = "Субтитры", AutoSize = true, ForeColor = Theme.TextPrimary, Margin = new Padding(0, 8, 0, 0) };
            editCaptions = Theme.MakeButton("Настроить", ghost: true, action: () => { captionEditor.Visible = !captionEditor.Visible; }); editCaptions.Dock = DockStyle.Fill; editCaptions.Margin = Padding.Empty;
            captionHead.Controls.Add(captionsEnabled, 0, 0); captionHead.Controls.Add(editCaptions, 1, 0); AddField(captionHead);
            captionEditor = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, Visible = false, Margin = new Padding(0, 6, 0, 0) };
            captionEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            captionFont = Theme.MakeCombo(new[] { "Segoe UI", "Arial", "Tahoma" });
            captionPlate = Theme.MakeCombo(new[] { "Скруглённая", "Прямая", "Без плашки" });
            captionSize = StyleNumber("warmup-subtitle-size", 28, 88); captionPosition = StyleNumber("warmup-subtitle-position", 10, 90);
            captionWords = StyleNumber("warmup-subtitle-words", 1, 6); captionOpacity = StyleNumber("warmup-subtitle-opacity", 0, 100);
            captionEditor.Controls.Add(SettingRow("Шрифт", captionFont)); captionEditor.Controls.Add(SettingRow("Размер текста", captionSize));
            captionEditor.Controls.Add(SettingRow("Положение сверху, %", captionPosition)); captionEditor.Controls.Add(SettingRow("Слов за раз", captionWords));
            captionEditor.Controls.Add(SettingRow("Плашка", captionPlate)); captionEditor.Controls.Add(SettingRow("Плотность плашки, %", captionOpacity));
            var colors = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 6, 0, 0) };
            colors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); colors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            captionTextColor = Theme.MakeButton("Цвет текста", action: () => PickCaptionColor(false)); captionTextColor.Dock = DockStyle.Fill;
            captionPlateColor = Theme.MakeButton("Цвет плашки", action: () => PickCaptionColor(true)); captionPlateColor.Dock = DockStyle.Fill; captionPlateColor.Margin = Padding.Empty;
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
            prompt.TextChanged += Changed; count.ValueChanged += Changed;
            captionsEnabled.CheckedChanged += CaptionChanged; captionFont.SelectedIndexChanged += CaptionChanged; captionPlate.SelectedIndexChanged += CaptionChanged;
            captionSize.ValueChanged += CaptionChanged; captionPosition.ValueChanged += CaptionChanged; captionWords.ValueChanged += CaptionChanged; captionOpacity.ValueChanged += CaptionChanged;
            LoadMarketFields();
        }
        static Label Label(string text, Font font, Color color, int top = 0) => new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, top, 0, 4) };
        static TextBox PathBox(string name) { var box = Theme.MakeSearchBox(); box.Name = name; box.ReadOnly = true; box.Dock = DockStyle.Fill; box.Margin = new Padding(0, 7, 8, 0); return box; }
        static Control PickerRow(Control box, Button button) {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            button.Dock = DockStyle.Fill; button.Margin = Padding.Empty; row.Controls.Add(box, 0, 0); row.Controls.Add(button, 1, 0); return row;
        }
        static NumericUpDown StyleNumber(string name, int min, int max) => new NumericUpDown { Name = name, Minimum = min, Maximum = max, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary, Font = Theme.FontBody };
        static Control SettingRow(string title, Control field) {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 0) };
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
        void Changed(object sender, EventArgs e) { if (loading) return; saveTimer.Stop(); saveTimer.Start(); }
        void SwitchMarket(string value) { if (run != null || value == market) return; SaveFields(); market = NormMarket(value); LoadMarketFields(); SaveFields(); }
        void LoadMarketFields() {
            loading = true;
            try {
                Theme.ApplyHeroToggleStyle(ru, market == "RU"); Theme.ApplyHeroToggleStyle(en, market == "EN");
                prompt.Text = market == "EN" ? settings.WarmupVideoPromptEn : settings.WarmupVideoPromptRu;
                count.Value = Math.Max(1, Math.Min(100, market == "EN" ? settings.WarmupVideoCountEn : settings.WarmupVideoCountRu));
                backgroundPath = market == "EN" ? settings.WarmupVideoBackgroundFileEn : settings.WarmupVideoBackgroundFileRu;
                // A legacy folder is not silently converted into a random file.
                if (string.IsNullOrWhiteSpace(backgroundPath)) {
                    string legacy = market == "EN" ? settings.WarmupVideoBackgroundsEn : settings.WarmupVideoBackgroundsRu;
                    if (File.Exists(legacy)) backgroundPath = legacy;
                }
                background.Text = string.IsNullOrWhiteSpace(backgroundPath) ? "Выберите фото или видео" : backgroundPath;
                string savedOutput = market == "EN" ? settings.WarmupVideoOutputEn : settings.WarmupVideoOutputRu;
                string oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch", "Warmup-" + market);
                output.Text = string.IsNullOrWhiteSpace(savedOutput) || string.Equals(savedOutput, oldDefault, StringComparison.OrdinalIgnoreCase) ? WarmupVideoEngine.DefaultOutputFolder : savedOutput;
                subtitleStyle = ((market == "EN" ? settings.WarmupSubtitlesEn : settings.WarmupSubtitlesRu) ?? new WarmupSubtitleStyle()).Copy(); subtitleStyle.Normalize();
                captionsEnabled.Checked = subtitleStyle.Enabled; captionFont.SelectedItem = subtitleStyle.FontFamily; captionPlate.SelectedIndex = subtitleStyle.Plate == "none" ? 2 : subtitleStyle.Plate == "solid" ? 1 : 0;
                captionSize.Value = subtitleStyle.FontSize; captionPosition.Value = subtitleStyle.Position; captionWords.Value = subtitleStyle.WordsPerCaption; captionOpacity.Value = subtitleStyle.Opacity;
                UpdateCaptionPreview();
                _ = UpdatePreviewAsync(backgroundPath);
            } finally { loading = false; }
        }
        void SaveFields() {
            saveTimer.Stop(); if (loading) return;
            if (market == "EN") { settings.WarmupVideoPromptEn = prompt.Text.Trim(); settings.WarmupVideoCountEn = (int)count.Value; settings.WarmupVideoBackgroundFileEn = backgroundPath; settings.WarmupVideoOutputEn = output.Text; }
            else { settings.WarmupVideoPromptRu = prompt.Text.Trim(); settings.WarmupVideoCountRu = (int)count.Value; settings.WarmupVideoBackgroundFileRu = backgroundPath; settings.WarmupVideoOutputRu = output.Text; }
            if (market == "EN") settings.WarmupSubtitlesEn = subtitleStyle.Copy(); else settings.WarmupSubtitlesRu = subtitleStyle.Copy();
            settings.WarmupVideoMarketView = market; Store.Save(settings);
        }
        void PickBackground() {
            using (var dialog = new OpenFileDialog { Title = "Фон: фото или видео", Filter = "Фото и видео|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v", CheckFileExists = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                backgroundPath = dialog.FileName; background.Text = backgroundPath; SaveFields(); _ = UpdatePreviewAsync(backgroundPath);
            }
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
        void SetBusy(bool busy) { create.Enabled = !busy; stop.Enabled = busy; ru.Enabled = en.Enabled = chooseBackground.Enabled = chooseOutput.Enabled = prompt.Enabled = count.Enabled = captionsEnabled.Enabled = !busy; captionEditor.Enabled = !busy && captionsEnabled.Checked; editCaptions.Enabled = !busy && captionsEnabled.Checked; }
        async Task RunGenerate() {
            if (run != null) return;
            SaveFields(); string key = WindowsSupport.Unprotect(settings.ProtectedElevenLabsKey);
            if (string.IsNullOrWhiteSpace(key)) key = WindowsSupport.Unprotect(settings.ProtectedKey);
            var job = new WarmupVideoJob { Market = market, Prompt = prompt.Text.Trim(), Count = (int)count.Value, BackgroundFile = backgroundPath, OutputFolder = output.Text,
                OpenRouterKey = WindowsSupport.Unprotect(settings.ProtectedOpenRouterKey), ElevenLabsKey = key, OpenRouterModel = settings.OpenRouterModel, Subtitles = subtitleStyle.Copy() };
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
