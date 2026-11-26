using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    internal sealed class VideoTranslationWindow : Form {
        readonly Preferences prefs;
        readonly TabControl studioTabs;
        AssemblyWindow montage;
        string montageSource;
        readonly List<string> batchSources = new List<string>();
        readonly TextBox source, folder, model, instructions;
        readonly ComboBox style, profanity, gender, voices, targetLanguage;
        readonly NumericUpDown duckLevel, duckAttack, duckRelease;
        readonly CheckBox subtitles, maskVideoText;
        readonly Label maskImages;
        readonly DataGridView lines;
        readonly Label status;
        readonly ProgressBar progress;
        readonly FlowLayoutPanel settings, actions;
        readonly Button stop;
        VideoTranslationOptions options;
        VideoTranslationProject project;
        CancellationTokenSource cancellation;
        bool busy, loading;

        public bool HasWork => busy || montage?.HasTranslationWork == true;
        public void SaveState() { ReadOptions(); SaveProject(); montage?.SaveTranslationWorkspace(); }
        public VideoTranslationWindow(Preferences preferences, bool embedded = false) {
            prefs = preferences;
            options = Store.Clone(prefs.TranslationOptions ?? new VideoTranslationOptions());
            Text = "Автоперевод видео"; Name = "VideoTranslationWindow";
            if (embedded) { TopLevel = false; FormBorderStyle = FormBorderStyle.None; Dock = DockStyle.Fill; }
            Font = Theme.FontBody; ForeColor = Theme.TextPrimary; BackColor = Theme.Background;
            StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(850, 650); ClientSize = new Size(1100, 820);
            var area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 40));
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            if (embedded) MinimumSize = Size.Empty;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 386)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            studioTabs = new TabControl { Name = "TranslationStudioTabs", Dock = DockStyle.Fill };
            var montagePage = new TabPage("Монтажная студия") { BackColor = Theme.Background };
            var translationPage = new TabPage("Перевод · язык и текст") { BackColor = Theme.Background };
            studioTabs.TabPages.Add(montagePage); studioTabs.TabPages.Add(translationPage); Controls.Add(studioTabs); translationPage.Controls.Add(root);
            settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, BackColor = Theme.Card, Padding = new Padding(8) };
            root.Controls.Add(settings, 0, 0);
            source = Box("TranslationSource"); source.ReadOnly = true;
            folder = Box("TranslationFolder"); folder.Text = Path.Combine(VideoBatchOutputPaths.Processing, "Переводы");
            var files = Row(); files.Controls.Add(Caption("Оригинал · авто", 110)); source.Width = 490; files.Controls.Add(source); files.Controls.Add(Btn("Добавить видео…", PickSource)); settings.Controls.Add(files);
            var destination = Row(); destination.Controls.Add(Caption("Папка проекта", 110)); folder.Width = 490; destination.Controls.Add(folder); destination.Controls.Add(Btn("Выбрать…", () => { using (var d = new FolderBrowserDialog { SelectedPath = folder.Text }) if (d.ShowDialog(this) == DialogResult.OK) folder.Text = d.SelectedPath; })); settings.Controls.Add(destination);
            targetLanguage = Combo("TranslationTargetLanguage", VideoTranslationEngine.LanguageNames, VideoTranslationEngine.LanguageName(options.TargetLanguage)); targetLanguage.Width = 265;
            var languageRow = Row(); languageRow.Controls.Add(Caption("Перевести на", 110)); languageRow.Controls.Add(targetLanguage);
            languageRow.Controls.Add(new Label { AutoSize = true, Text = "Язык оригинала определяется автоматически", Margin = new Padding(8) }); settings.Controls.Add(languageRow);
            style = Combo("TranslationStyle", new[] { "Гоблинский", "Колхозный", "Матёрый спец", "Дерзкий блогер", "Интеллигентный сарказм" }, options.Style);
            profanity = Combo("TranslationProfanity", new[] { "Без мата", "Умеренно", "Без цензуры" }, options.Profanity);
            var mode = Row(); mode.Controls.Add(Caption("Стиль", 110)); style.Width = 265; mode.Controls.Add(style); mode.Controls.Add(Caption("Лексика", 85)); profanity.Width = 180; mode.Controls.Add(profanity); settings.Controls.Add(mode);
            model = Box("TranslationGeminiModel"); model.Width = 265; model.Text = GeminiTranslationClient.ModelId(options.GeminiModel);
            var api = Row(); api.Controls.Add(Caption("OpenRouter", 110)); api.Controls.Add(model);
            api.Controls.Add(new Label { AutoSize = true, Text = "Ключи OpenRouter и ElevenLabs — из настроек приложения", Margin = new Padding(8) }); settings.Controls.Add(api);
            instructions = Box("TranslationInstructions"); instructions.Width = 580; instructions.Text = options.Instructions;
            var custom = Row(); custom.Controls.Add(Caption("Пожелания", 110)); custom.Controls.Add(instructions); settings.Controls.Add(custom);
            gender = Combo("TranslationGender", new[] { "Мужской", "Женский" }, "Мужской"); gender.Width = 130;
            voices = new ComboBox { Name = "TranslationVoice", DropDownStyle = ComboBoxStyle.DropDownList, Width = 290 };
            if (!string.IsNullOrWhiteSpace(options.VoiceId)) { voices.Items.Add(new ElevenLabsVoice { voice_id = options.VoiceId, name = "Сохранённый · " + options.VoiceId }); voices.SelectedIndex = 0; }
            gender.SelectedIndexChanged += (s, e) => voices.Items.Clear();
            var voiceRow = Row(); voiceRow.Controls.Add(Caption("ElevenLabs", 110)); voiceRow.Controls.Add(gender); voiceRow.Controls.Add(voices);
            var load = Btn("Стандартные голоса", null); load.Click += async (s, e) => await Run(LoadVoices); voiceRow.Controls.Add(load); settings.Controls.Add(voiceRow);
            options.NormalizeDuck();
            duckLevel = new NumericUpDown { Name = "TranslationDuckLevel", Minimum = 0, Maximum = 100, DecimalPlaces = 0, Width = 55, Value = (decimal)options.OriginalDuckLevelPercent };
            duckAttack = new NumericUpDown { Name = "TranslationDuckAttack", Minimum = 0.1m, Maximum = 5, DecimalPlaces = 1, Increment = 0.1m, Width = 55, Value = (decimal)options.OriginalDuckAttackSeconds };
            duckRelease = new NumericUpDown { Name = "TranslationDuckRelease", Minimum = 0.1m, Maximum = 5, DecimalPlaces = 1, Increment = 0.1m, Width = 55, Value = (decimal)options.OriginalDuckReleaseSeconds };
            var duckRow = Row(); duckRow.Controls.Add(Caption("Оригинал под речью", 110)); duckRow.Controls.Add(duckLevel);
            duckRow.Controls.Add(new Label { AutoSize = true, Text = "%", Margin = new Padding(2, 4, 8, 0) });
            duckRow.Controls.Add(Caption("Спад", 38)); duckRow.Controls.Add(duckAttack);
            duckRow.Controls.Add(new Label { AutoSize = true, Text = "с", Margin = new Padding(2, 4, 8, 0) });
            duckRow.Controls.Add(Caption("Восст.", 50)); duckRow.Controls.Add(duckRelease);
            duckRow.Controls.Add(new Label { AutoSize = true, Text = "с · 0 = тишина, 100 = без приглушения", Margin = new Padding(6, 4, 0, 0) });
            settings.Controls.Add(duckRow);
            subtitles = new CheckBox { Name = "TranslationBurnSubtitles", AutoSize = true, Checked = options.BurnSubtitles, Text = "Наложить субтитры на видео", Margin = new Padding(8) };
            var tools = Row(); tools.Controls.Add(subtitles); tools.Controls.Add(Btn("Стиль субтитров…", () => { _ = Run(EditSubtitles); })); tools.Controls.Add(Btn("Обработка видео…", EditProcessing)); settings.Controls.Add(tools);
            maskVideoText = new CheckBox { Name = "TranslationMaskVideoText", AutoSize = true, Checked = options.MaskVideoText, Text = "Находить и закрывать текст в кадре", Margin = new Padding(8) };
            maskImages = new Label { Name = "TranslationMaskImages", AutoSize = true, Margin = new Padding(8) };
            var maskRow = Row(); maskRow.Controls.Add(maskVideoText); maskRow.Controls.Add(Btn("Картинки-плашки…", PickMaskImages)); maskRow.Controls.Add(maskImages); settings.Controls.Add(maskRow);
            var scanText = Btn("Сканировать текст в кадре", null); scanText.Name = "TranslationScanVideoText"; scanText.Click += async (s, e) => await Run(ScanVideoText);
            var scanRow = Row(); scanRow.Controls.Add(scanText); scanRow.Controls.Add(new Label { AutoSize = true, Margin = new Padding(8), Text = "Локально · плашка за 1 с до текста и до его исчезновения · картинки по очереди" }); settings.Controls.Add(scanRow); UpdateMaskImages();
            settings.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(940, 0), ForeColor = Theme.TextSecondary, Text = "В паузах оригинал 100%. Приглушение — только в шаге 3. SRT отдельно; API платное." });
            lines = new DataGridView { Name = "TranslationSegments", Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, BackgroundColor = Theme.Card, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Color.Black, WrapMode = DataGridViewTriState.True } };
            lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Таймкод", Width = 145, ReadOnly = true });
            lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "Original", HeaderText = "Оригинал", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            lines.Columns.Add(new DataGridViewTextBoxColumn { Name = "Translation", HeaderText = "Перевод (можно редактировать)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            root.Controls.Add(lines, 0, 1);
            actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true, Padding = new Padding(0, 4, 0, 0) };
            var automatic = Btn("Автоперевод", null); automatic.Click += async (s, e) => await Run(AutoTranslate); actions.Controls.Add(automatic);
            var transcribe = Btn("1. Распознать оригинал", null); transcribe.Click += async (s, e) => await Run(Transcribe); actions.Controls.Add(transcribe);
            var translate = Btn("2. Перевести через Gemini", null); translate.Click += async (s, e) => await Run(Translate); actions.Controls.Add(translate);
            var render = Btn("3. Озвучить и создать видео", null); render.Click += async (s, e) => await Run(Render); actions.Controls.Add(render);
            actions.Controls.Add(Btn("Сохранить", () => { SaveState(); status.Text = "Настройки и текст перевода сохранены."; }));
            actions.Controls.Add(Btn("Открыть проект…", OpenProject)); actions.Controls.Add(Btn("Папка результата", () => Ui.Open(project == null ? folder.Text : project.Directory)));
            actions.Controls.Add(Btn("Смотреть результат", () => { if (project == null || !File.Exists(project.Output)) throw new Exception("Сначала создайте видео."); Ui.Open(project.Output); }));
            root.Controls.Add(actions, 0, 2);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 }; bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, Text = "Добавьте видео или откройте сохранённый проект. Перевод перед озвучкой можно исправить в таблице." };
            progress = new ProgressBar { Dock = DockStyle.Fill, Height = 12 };
            stop = Btn("Остановить", () => cancellation?.Cancel()); stop.Enabled = false; bottom.Controls.Add(status, 0, 0); bottom.Controls.Add(progress, 0, 1); bottom.Controls.Add(stop, 1, 0);
            root.Controls.Add(bottom, 0, 3);
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; status.Text = "Остановите текущую операцию перед закрытием."; return; } try { ReadOptions(); SaveProject(); } catch (Exception ex) { e.Cancel = true; ShowError(ex); } };
            studioTabs.SelectedIndex = 1;
            studioTabs.SelectedIndexChanged += async (s, e) => { if (studioTabs.SelectedIndex == 0 && !busy) await Run(EnsureMontage); };
            if (!string.IsNullOrWhiteSpace(prefs.TranslationLastProject)) {
                try { project = VideoTranslationEngine.Load(prefs.TranslationLastProject); RestoreProjectOptions(); BindProject(); }
                catch (Exception ex) { status.Text = "Не удалось открыть прошлый проект: " + ex.Message; }
            }
        }
        static TextBox Box(string name) => new TextBox { Name = name };
        static Label Caption(string text, int width) => new Label { Text = text, Width = width, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
        static FlowLayoutPanel Row() => new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        static ComboBox Combo(string name, string[] items, string value) { var c = new ComboBox { Name = name, DropDownStyle = ComboBoxStyle.DropDownList }; c.Items.AddRange(items); c.SelectedIndex = Math.Max(0, Array.IndexOf(items, value)); return c; }
        Button Btn(string text, Action action) {
            var b = new Button { Text = text, AutoSize = true, Height = 32, Padding = new Padding(6, 2, 6, 2), BackColor = Theme.Card, ForeColor = Theme.TextPrimary, FlatStyle = FlatStyle.Flat };
            if (action != null) b.Click += (s, e) => { try { action(); } catch (Exception ex) { ShowError(ex); } }; return b;
        }
        void ShowError(Exception ex) { status.Text = "Ошибка: " + ex.Message; AppDiagnostics.Write("Перевод", "", "translation", "error", ex.ToString(), "error"); }
        string ElevenKey() {
            string value = WindowsSupport.Unprotect(prefs.AssemblyElevenLabsKey);
            return string.IsNullOrWhiteSpace(value) ? WindowsSupport.Unprotect(prefs.ProtectedElevenLabsKey) : value;
        }
        void ReadOptions() {
            if (loading) return;
            options.TargetLanguage = VideoTranslationEngine.LanguageCodes[targetLanguage.SelectedIndex];
            options.Style = Convert.ToString(style.SelectedItem); options.Profanity = Convert.ToString(profanity.SelectedItem);
            options.GeminiModel = model.Text.Trim(); options.Instructions = instructions.Text.Trim(); options.BurnSubtitles = subtitles.Checked;
            options.MaskVideoText = maskVideoText.Checked;
            options.VoiceId = (voices.SelectedItem as ElevenLabsVoice)?.voice_id ?? "";
            options.OriginalDuckLevelPercent = (double)duckLevel.Value;
            options.OriginalDuckAttackSeconds = (double)duckAttack.Value;
            options.OriginalDuckReleaseSeconds = (double)duckRelease.Value;
            options.NormalizeDuck();
            duckLevel.Value = (decimal)options.OriginalDuckLevelPercent;
            duckAttack.Value = (decimal)options.OriginalDuckAttackSeconds;
            duckRelease.Value = (decimal)options.OriginalDuckReleaseSeconds;
            prefs.TranslationOptions = Store.Clone(options); Store.Save(prefs);
        }
        void ReadLines() {
            lines.EndEdit();
            if (project == null) return;
            foreach (DataGridViewRow row in lines.Rows) ((VideoTranslationSegment)row.Tag).Text = Convert.ToString(row.Cells["Translation"].Value);
        }
        void SaveProject(bool readLines = true) {
            if (readLines) ReadLines(); if (project == null) return;
            project.Options = Store.Clone(options); project.BatchSources = batchSources.ToList();
            VideoTranslationEngine.Save(project); prefs.TranslationLastProject = project.Directory; Store.Save(prefs);
        }
        void RestoreProjectOptions() {
            batchSources.Clear(); batchSources.AddRange(project.BatchSources ?? new List<string>());
            options = Store.Clone(project.Options ?? new VideoTranslationOptions());
            targetLanguage.SelectedIndex = Array.IndexOf(VideoTranslationEngine.LanguageCodes, options.TargetLanguage);
            style.SelectedItem = options.Style; profanity.SelectedItem = options.Profanity;
            instructions.Text = options.Instructions; model.Text = GeminiTranslationClient.ModelId(options.GeminiModel); subtitles.Checked = options.BurnSubtitles;
            maskVideoText.Checked = options.MaskVideoText; UpdateMaskImages();
            options.NormalizeDuck();
            duckLevel.Value = (decimal)options.OriginalDuckLevelPercent;
            duckAttack.Value = (decimal)options.OriginalDuckAttackSeconds;
            duckRelease.Value = (decimal)options.OriginalDuckReleaseSeconds;
            voices.Items.Clear();
            if (!string.IsNullOrWhiteSpace(options.VoiceId)) { voices.Items.Add(new ElevenLabsVoice { voice_id = options.VoiceId, name = "Сохранённый · " + options.VoiceId }); voices.SelectedIndex = 0; }
        }
        void BindProject() {
            loading = true;
            try {
                source.Text = project.Source; folder.Text = project.Directory; lines.Rows.Clear();
                foreach (var segment in project.Segments) {
                    int i = lines.Rows.Add(TimeSpan.FromSeconds(segment.Start).ToString(@"hh\:mm\:ss\.fff") + "–" + TimeSpan.FromSeconds(segment.End).ToString(@"hh\:mm\:ss\.fff"), segment.Original, segment.Text);
                    lines.Rows[i].Tag = segment;
                }
            } finally { loading = false; }
        }
        async void PickSource() {
            using (var d = new OpenFileDialog { Filter = "Видео|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v|Все файлы|*.*", Multiselect = true }) {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                ReadOptions(); SaveProject(); project = null; lines.Rows.Clear(); source.Text = d.FileName;
                string translationDay = VideoBatchOutputPaths.DailySessionFolder(Path.Combine(VideoBatchOutputPaths.Processing, "Переводы"));
                folder.Text = Path.Combine(translationDay, VideoTranslationEngine.ProjectDirectoryName(d.FileName, DateTime.Now));
                batchSources.Clear(); batchSources.AddRange(d.FileNames);
                prefs.TranslationLastProject = ""; Store.Save(prefs);
                await Run(EnsureMontage); studioTabs.SelectedIndex = 0;
                status.Text = "Видео добавлено: " + batchSources.Count + ". Настройте монтаж; перевод — на соседней вкладке.";
            }
        }
        void OpenProject() {
            using (var d = new OpenFileDialog { Filter = "Проект перевода|*.json" }) {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                SaveState(); var loaded = VideoTranslationEngine.Load(d.FileName); batchSources.Clear(); project = loaded; RestoreProjectOptions(); BindProject(); prefs.TranslationLastProject = project.Directory; Store.Save(prefs);
            }
        }
        async Task Run(Func<CancellationToken, Action<string>, Task> action) {
            if (busy) return;
            try {
                ReadOptions(); ReadLines(); busy = true; if (montage != null) montage.Enabled = false; settings.Enabled = actions.Enabled = lines.Enabled = false; stop.Enabled = true;
                progress.Style = ProgressBarStyle.Marquee; cancellation = new CancellationTokenSource();
                var updates = new Progress<string>(text => { if (!IsDisposed) status.Text = text; });
                await action(cancellation.Token, text => ((IProgress<string>)updates).Report(text));
            } catch (OperationCanceledException) { status.Text = "Остановлено. Сохранённые этапы проекта остаются доступны."; }
            catch (Exception ex) { ShowError(ex); }
            finally {
                try { SaveProject(false); if (project != null) BindProject(); } catch (Exception ex) { ShowError(ex); }
                cancellation?.Dispose(); cancellation = null; busy = false; if (montage != null) montage.Enabled = true; settings.Enabled = actions.Enabled = lines.Enabled = true; stop.Enabled = false; progress.Style = ProgressBarStyle.Blocks;
            }
        }
        async Task EnsureMontage(CancellationToken ct, Action<string> report) {
            string video = project?.Source ?? source.Text;
            if (string.IsNullOrWhiteSpace(video)) { studioTabs.SelectedIndex = 1; return; }
            string path = Path.Combine(project?.Directory ?? folder.Text, "montage.xml");
            if (montage == null || !string.Equals(montageSource, video, StringComparison.OrdinalIgnoreCase)) {
                if (montage != null) { montage.SaveTranslationWorkspace(); studioTabs.TabPages[0].Controls.Remove(montage); montage.Dispose(); }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                montage = new AssemblyWindow(embedded: true, preferences: prefs, workspacePath: path, translationMode: true);
                montageSource = video;
                montage.TranslationSourceRequested += PickSource;
                montage.TranslationSettingsRequested += () => studioTabs.SelectedIndex = 1;
                montage.TranslationSubtitlesRequested += async () => await Run(EditSubtitles);
                montage.TranslationRenderRequested += async () => await Run(Render);
                studioTabs.TabPages[0].Controls.Add(montage); montage.Show();
                report("Загружаю исходное видео в монтажную студию…");
                await montage.LoadTranslationSourceAsync(video, ct);
            }
        }
        async Task LoadVoices(CancellationToken ct, Action<string> report) {
            report("Загрузка голосов ElevenLabs…");
            using (var client = new ElevenLabsClient(ElevenKey(), WindowsSupport.Unprotect(prefs.ProtectedWarmupHttpProxy))) {
                var available = await client.ListVoicesAsync(gender.SelectedIndex == 1 ? "female" : "male", ct, defaultOnly: true);
                string selected = options.VoiceId; voices.Items.Clear(); foreach (var voice in available) voices.Items.Add(voice);
                if (voices.Items.Count > 0) voices.SelectedIndex = Math.Max(0, available.FindIndex(v => v.voice_id == selected));
                status.Text = "Голосов: " + available.Count + ". Выберите голос перед озвучкой.";
            }
        }
        void UpdateMaskImages() { maskImages.Text = "Картинок: " + (options.TextMaskImages?.Count ?? 0); }
        void PickMaskImages() {
            using (var dialog = new OpenFileDialog { Filter = "Картинки-плашки|*.png;*.jpg;*.jpeg;*.bmp|Все файлы|*.*", Multiselect = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (string path in dialog.FileNames) using (var image = Image.FromFile(path)) { }
                options.TextMaskImages = dialog.FileNames.ToList(); maskVideoText.Checked = true; UpdateMaskImages(); ReadOptions();
            }
        }
        async Task ScanVideoText(CancellationToken ct, Action<string> report) {
            string input = project?.Source ?? source.Text;
            if (string.IsNullOrWhiteSpace(input)) throw new Exception("Добавьте исходное видео.");
            if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
            var media = Core.VideoInfo(await Core.Probe(probe, input, ct));
            Directory.CreateDirectory(folder.Text);
            var scanProject = new VideoTranslationProject { Source = input, Directory = folder.Text, Duration = media.VideoDuration, SourceHash = await Task.Run(() => AssemblyFiles.Hash(input), ct) };
            var regions = await VideoTextMask.ScanAsync(scanProject, probe, ct, report);
            status.Text = "Сканирование завершено. Областей текста: " + regions.Count + ". Для наложения включите галочку и выберите картинки-плашки.";
        }
        async Task Transcribe(CancellationToken ct, Action<string> report) {
            if (string.IsNullOrWhiteSpace(source.Text)) throw new Exception("Добавьте исходное видео.");
            project = await VideoTranslationEngine.TranscribeAsync(source.Text, folder.Text, ct, report); BindProject(); status.Text = "Оригинал распознан. Выберите стиль и запустите перевод.";
        }
        async Task AutoTranslate(CancellationToken ct, Action<string> report) {
            VideoTextMask.ValidateImages(options);
            await Transcribe(ct, report);
            await Translate(ct, report);
            await Render(ct, report);
        }
        async Task Translate(CancellationToken ct, Action<string> report) {
            if (project == null) throw new Exception("Сначала распознайте оригинал или откройте проект.");
            await VideoTranslationEngine.TranslateAsync(project, options, WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey), ct, report, WindowsSupport.Unprotect(prefs.ProtectedWarmupHttpProxy));
            await EnsureMontage(ct, report); options.SubtitleStyle = montage.TranslationTemplateSnapshot().SubtitleStyle.Copy(); montage?.SetTranslationCaptions(project, options.SubtitleStyle);
            status.Text = "Перевод готов. Проверьте текст в таблице перед озвучкой.";
        }
        async Task Render(CancellationToken ct, Action<string> report) {
            if (project == null) throw new Exception("Сначала распознайте и переведите видео.");
            if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
            await EnsureMontage(ct, report);
            var layout = montage.TranslationTemplateSnapshot();
            options.SubtitleStyle = layout.SubtitleStyle?.Copy() ?? options.SubtitleStyle;
            options.SubtitleStyle.Enabled = subtitles.Checked; options.BurnSubtitles = subtitles.Checked;
            var renderOptions = Store.Clone(options); renderOptions.Processing = new ProcessingSettings();
            var sources = batchSources.Count > 1 ? batchSources.ToList() : new List<string> { project.Source };
            string firstDirectory = project.Directory; int completed = 0;
            foreach (string input in sources) {
                ct.ThrowIfCancellationRequested(); var item = project;
                if (!string.Equals(project.Source, input, StringComparison.OrdinalIgnoreCase)) {
                    string directory = Path.Combine(firstDirectory, "batch-" + (completed + 1) + "-" + Path.GetFileNameWithoutExtension(input));
                    item = File.Exists(Path.Combine(directory, VideoTranslationEngine.ProjectFileName))
                        ? VideoTranslationEngine.Load(directory)
                        : await VideoTranslationEngine.TranscribeAsync(input, directory, ct, report);
                    if (item.Segments.Any(s => string.IsNullOrWhiteSpace(s.Text)))
                        await VideoTranslationEngine.TranslateAsync(item, options, WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey), ct, report, WindowsSupport.Unprotect(prefs.ProtectedWarmupHttpProxy));
                }
                await VideoTranslationEngine.RenderAsync(item, renderOptions, ElevenKey(), WindowsSupport.Unprotect(prefs.ProtectedWarmupHttpProxy), ffmpeg, probe, ct, report, geminiKey: WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey));
                item.Output = await AssemblyWindow.RenderTranslationStudioAsync(layout, item.Output, Path.Combine(item.Directory, "translated-studio.mp4"), Path.Combine(item.Directory, "montage-render"), ffmpeg, probe, ct, report);
                item.Options = Store.Clone(options); VideoTranslationEngine.Save(item); completed++;
                report("Готово " + completed + " из " + sources.Count + ": " + item.Output);
            }
            status.Text = "Монтаж и перевод готовы: " + completed + " видео. Папка: " + firstDirectory;
        }
        async Task EditSubtitles(CancellationToken ct, Action<string> report) {
            ReadLines();
            await EnsureMontage(ct, report);
            if (montage != null) options.SubtitleStyle = montage.TranslationTemplateSnapshot().SubtitleStyle.Copy();
            string video = project?.Source ?? source.Text;
            if (string.IsNullOrWhiteSpace(video)) throw new Exception("Добавьте видео, чтобы настроить субтитры на его кадре.");
            if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
            report("Загружаю кадр видео для предпросмотра субтитров…");
            var media = Core.VideoInfo(await Core.Probe(probe, video, ct));
            string frame = Path.Combine(Path.GetTempPath(), "vb-subtitle-preview-" + Guid.NewGuid().ToString("N") + ".png");
            try {
                var captions = project?.Segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)).Select(s => new WarmupCaption { Start = s.Start, End = s.End, Text = s.Text }).ToList();
                double time = project?.Segments.FirstOrDefault()?.Start ?? Math.Min(1, media.VideoDuration / 2);
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-ss", Core.N(time), "-i", video, "-map", "0:" + media.VideoIndex, "-frames:v", "1", "-vf", "scale=" + media.Width + ":" + media.Height + ",setsar=1", "-threads", "1", frame }, ct, null, 60);
                using (var background = Image.FromFile(frame))
                using (var d = new SubtitleStyleDialog(options.SubtitleStyle, captions, background, media.Width, media.Height)) {
                    if (d.ShowDialog(this) == DialogResult.OK) { options.SubtitleStyle = d.Style; subtitles.Checked = d.Style.Enabled; ReadOptions(); montage?.SetTranslationCaptions(project, d.Style); }
                }
                status.Text = "Предпросмотр субтитров: " + Path.GetFileName(video);
            } finally { if (File.Exists(frame)) File.Delete(frame); }
        }
        async void EditProcessing() {
            if (!string.IsNullOrWhiteSpace(project?.Source ?? source.Text)) { await Run(EnsureMontage); montage?.EditTranslationProcessing(); return; }
            using (var d = new ProcessingDialog(Store.Clone(options.Processing ?? new ProcessingSettings()), false, true, true)) {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                options.Processing = d.Settings; ReadOptions();
                status.Text = "Обработка сохранена. Изменения скорости и длительности отключаются для сохранения таймкодов перевода.";
            }
        }
    }
}
