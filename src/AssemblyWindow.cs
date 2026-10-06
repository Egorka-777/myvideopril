using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class AssemblyWindow : Form {
        AssemblyTemplate template;
        readonly string templatePath, historyFolder;
        TextBox materials, musicPath, output, videoSource, layerSource, sceneName, layerName, fontName;
        ListBox sceneList, layerList;
        ListView sources;
        NumericUpDown duration, count, transitionTime, volume, start, end, x, y, width, height, opacity, fontSize, radius, padding;
        ComboBox transition, resolution;
        CheckBox enabled, unique, fixedAsset, randomStart;
        AssemblyCanvas canvas;
        Panel left, header, sceneEditor, exportEditor;
        FlowLayoutPanel footer, timeline, layerChips;
        Label status, total, sceneCaption, musicCaption, outputCaption;
        ProgressBar progress;
        Button stop, sceneTab, exportTab, changing, permanent, fresh, visibleLayer;
        AssemblyAssetCard videoCard, imageCard;
        readonly List<Button> sceneCards = new List<Button>();
        readonly bool throwErrors;
        bool loading, closeAfter;
        CancellationTokenSource cancellation;
        AssemblyPlan previewPlan;
        readonly string previewFolder;

        public AssemblyWindow(bool throwErrors = false) {
            this.throwErrors = throwErrors;
            templatePath = Path.Combine(Store.Root, "assembly-template.xml"); historyFolder = Path.Combine(Store.Root, "assembly-history");
            previewFolder = Path.Combine(Path.GetTempPath(), "videobatch-preview-" + Guid.NewGuid().ToString("N"));
            string loadError = null;
            try { template = File.Exists(templatePath) ? AssemblyFiles.Load<AssemblyTemplate>(templatePath) : AssemblyTemplate.Defaults(); template.Validate(); }
            catch (Exception e) { template = AssemblyTemplate.Defaults(); loadError = "Не удалось прочитать шаблон: " + e.Message; }
            if (string.IsNullOrWhiteSpace(template.Materials)) template.Materials = Path.Combine(AppPaths.ExeDirectory, "Материалы-сборки");
            if (string.IsNullOrWhiteSpace(template.Output)) template.Output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch", "Assembled");
            Text = "Сборка роликов — конструктор"; ClientSize = new Size(1220, 780); MinimumSize = new Size(1080, 690);
            StartPosition = FormStartPosition.CenterParent; Font = Theme.FontBody; BackColor = Theme.Background; ForeColor = Theme.TextPrimary;
            var area = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(Math.Min(ClientSize.Width, area.Width - 40), Math.Min(ClientSize.Height, area.Height - 70));
            InitBindings(); BuildStudio();
            FormClosing += (s, e) => { if (cancellation != null) { e.Cancel = true; closeAfter = true; cancellation.Cancel(); } else Safe(Save); };
            LoadTop(); RefreshScenes(0); RefreshSources();
            status.Text = loadError ?? "Начни с первой сцены: добавь видео и картинку. Оригиналы остаются у тебя.";
        }
        void InitBindings() {
            var hidden = new Panel { Visible = false }; Controls.Add(hidden);
            materials = new TextBox(); musicPath = new TextBox(); output = new TextBox(); videoSource = new TextBox(); layerSource = new TextBox(); sceneName = new TextBox(); layerName = new TextBox(); fontName = new TextBox();
            duration = Value(.5m, 60); count = Value(1, 1000, 0); transitionTime = Value(0, 2, 2); volume = Value(0, 200, 0);
            start = Value(0, 60); end = Value(0, 60); x = Value(0, 100); y = Value(0, 100); width = Value(2, 100); height = Value(2, 100); opacity = Value(0, 100); fontSize = Value(12, 240, 0); radius = Value(0, 200, 0); padding = Value(0, 150, 0);
            enabled = new CheckBox(); unique = new CheckBox(); fixedAsset = new CheckBox(); randomStart = new CheckBox();
            sceneList = new ListBox(); layerList = new ListBox(); sources = new ListView();
            transition = new ComboBox(); transition.Items.AddRange(AssemblyTemplate.Transitions);
            resolution = new ComboBox(); resolution.Items.AddRange(new object[] { "Вертикально · Full HD", "Вертикально · HD", "Горизонтально · Full HD", "Горизонтально · HD" });
            hidden.Controls.AddRange(new Control[] { materials, musicPath, output, videoSource, layerSource, sceneName, layerName, fontName, duration, count, transitionTime, volume, start, end, x, y, width, height, opacity, fontSize, radius, padding, enabled, unique, fixedAsset, randomStart, sceneList, layerList, sources, transition, resolution });
            sceneList.SelectedIndexChanged += (s, e) => { if (!loading) LoadScene(); };
            layerList.SelectedIndexChanged += (s, e) => { if (!loading) { LoadLayer(); RefreshCanvas(); } };
            foreach (var box in new[] { sceneName, videoSource, layerName, layerSource, fontName }) box.TextChanged += (s, e) => EditorChanged();
            foreach (var n in new[] { duration, x, y, width, height, opacity, start, end, fontSize, radius, padding }) n.ValueChanged += (s, e) => EditorChanged();
            foreach (var c in new[] { enabled, unique, fixedAsset }) c.CheckedChanged += (s, e) => EditorChanged();
            foreach (var n in new[] { count, transitionTime, volume }) n.ValueChanged += (s, e) => { if (!loading) { ReadTop(); RefreshCanvas(); } };
            foreach (var c in new[] { transition, resolution }) c.SelectedIndexChanged += (s, e) => { if (!loading) { ReadTop(); previewPlan = null; canvas?.SetBackground(null); RefreshCanvas(); } };
            randomStart.CheckedChanged += (s, e) => { if (!loading) ReadTop(); };
        }
        static NumericUpDown Value(decimal min, decimal max, int decimals = 1) { return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .1m }; }
        static Label Caption(string text, int height = 25) { return new Label { Text = text, Height = height, AutoSize = false, ForeColor = Theme.TextSecondary, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleLeft }; }
        static FlowLayoutPanel Row(int height = 44) { return new FlowLayoutPanel { Dock = DockStyle.Top, Height = height, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty }; }
        static Button Button(string text, Action action = null, bool primary = false) { return Theme.MakeButton(text, accent: primary, action: action); }
        static FlowLayoutPanel Stack() { return new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 0, 14, 12) }; }
        static void FitStack(FlowLayoutPanel stack) { stack.SizeChanged += (s, e) => { foreach (Control c in stack.Controls) c.Width = Math.Max(270, stack.ClientSize.Width - 24); }; }
        void BuildStudio() {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 12, 18, 12), ColumnCount = 1, RowCount = 4, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); Controls.Add(root); root.BringToFront();
            header = new Panel { Dock = DockStyle.Fill }; root.Controls.Add(header, 0, 0);
            var title = new Label { Text = "Конструктор роликов", Font = Theme.FontPageTitle, ForeColor = Theme.TextPrimary, Location = new Point(0, 0), AutoSize = true }; header.Controls.Add(title);
            header.Controls.Add(new Label { Text = "Собери сцены как конструктор. Картинки двигай прямо на экране.", Location = new Point(2, 36), AutoSize = true, ForeColor = Theme.TextSecondary });
            var menu = Theme.MakeMenuButton("Шаблон", ("Сохранить", () => Safe(Save)), ("Загрузить", LoadTemplate), ("Сохранить копию…", ExportTemplate), ("Папка материалов…", ChooseMaterials));
            menu.Anchor = AnchorStyles.Top | AnchorStyles.Right; header.Controls.Add(menu); header.SizeChanged += (s, e) => menu.Location = new Point(header.Width - menu.Width, 6);
            var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 406)); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.Controls.Add(workspace, 0, 1);
            left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 16, 0) }; workspace.Controls.Add(left, 0, 0);
            var inspector = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 }; inspector.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); inspector.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.Controls.Add(inspector);
            var nav = Row(); sceneTab = Button("① Сцена", () => SwitchInspector(false), true); exportTab = Button("② Музыка и выпуск", () => SwitchInspector(true)); nav.Controls.AddRange(new Control[] { sceneTab, exportTab }); inspector.Controls.Add(nav, 0, 0);
            var host = new Panel { Dock = DockStyle.Fill }; inspector.Controls.Add(host, 0, 1);
            sceneEditor = new Panel { Dock = DockStyle.Fill }; exportEditor = new Panel { Dock = DockStyle.Fill, Visible = false }; host.Controls.AddRange(new Control[] { sceneEditor, exportEditor }); BuildSceneEditor(); BuildExportEditor();
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Card, Padding = new Padding(14) };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); workspace.Controls.Add(right, 1, 0);
            var previewRow = Row(); var bg = Button("▶ Обновить фон", async () => await ShowSample()); previewRow.Controls.Add(bg); previewRow.Controls.Add(new Label { Text = "Тяни картинку или угол рамки", AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(4, 10, 0, 0) }); right.Controls.Add(previewRow, 0, 0);
            canvas = new AssemblyCanvas { Dock = DockStyle.Fill }; right.Controls.Add(canvas, 0, 1);
            canvas.LayerSelected += i => layerList.SelectedIndex = i;
            canvas.PlacementChanged += () => { previewPlan = null; LoadLayer(); RefreshCanvas(); };
            total = Caption("", 40); right.Controls.Add(total, 0, 2);
            var strip = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(0, 12, 0, 0) };
            strip.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); strip.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); strip.Controls.Add(Caption("ПОРЯДОК СЦЕН · нажми карточку или плюс между ними", 22), 0, 0);
            timeline = new FlowLayoutPanel { Name = "SceneTimeline", Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 4, 0, 0), BackColor = Theme.Background }; strip.Controls.Add(timeline, 0, 1); root.Controls.Add(strip, 0, 2);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 }; bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 6)); bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            status = Caption("", 25); status.AutoEllipsis = true; bottom.Controls.Add(status, 0, 0);
            progress = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 }; bottom.Controls.Add(progress, 0, 1);
            footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
            footer.Controls.Add(Button("▶ Проверить пример", async () => await Run(true)));
            footer.Controls.Add(Button("✦ Собрать ролики", async () => await Run(false), true));
            footer.Controls.Add(Button("Открыть результаты", () => Safe(() => { ReadTop(); Directory.CreateDirectory(template.Output); Ui.Open(template.Output); })));
            stop = Button("■ Стоп", () => cancellation?.Cancel()); stop.Visible = false; footer.Controls.Add(stop); bottom.Controls.Add(footer, 0, 2); root.Controls.Add(bottom, 0, 3);
        }
        void SwitchInspector(bool export) {
            sceneEditor.Visible = !export; exportEditor.Visible = export; if (export) exportEditor.BringToFront(); else sceneEditor.BringToFront();
            sceneTab.BackColor = export ? Theme.Elevated : Theme.Accent; exportTab.BackColor = export ? Theme.Accent : Theme.Elevated;
        }
        void BuildSceneEditor() {
            var f = Stack(); sceneEditor.Controls.Add(f); FitStack(f);
            var sceneHead = new TableLayoutPanel { Width = 358, Height = 42, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            sceneHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sceneHead.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40)); sceneHead.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40)); sceneHead.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            sceneCaption = Caption("Сцена", 34); sceneCaption.Font = Theme.FontCardTitle; sceneCaption.ForeColor = Theme.TextPrimary; sceneCaption.AutoEllipsis = true; sceneCaption.Dock = DockStyle.Fill;
            sceneHead.Controls.Add(sceneCaption, 0, 0); sceneHead.Controls.Add(Theme.MakeIconButton("←", () => MoveScene(-1)), 1, 0); sceneHead.Controls.Add(Theme.MakeIconButton("→", () => MoveScene(1)), 2, 0); sceneHead.Controls.Add(Theme.MakeIconButton("⋯", ShowSceneMenu), 3, 0); f.Controls.Add(sceneHead);
            f.Controls.Add(new AssemblySlider("Длительность", duration, "с") { Width = 358, Name = "SceneDurationSlider" });
            videoCard = new AssemblyAssetCard("Фоновое видео", "+ Видео", () => ImportSceneFiles(true)); videoCard.Width = 358;
            videoCard.FolderClick += () => SourceMenu(true, videoCard); videoCard.FilesDropped += files => ImportSceneFiles(true, files); f.Controls.Add(videoCard);
            var layerHead = Row(38); layerHead.Width = 358; layerHead.Controls.Add(new Label { Text = "КАРТИНКИ ПОВЕРХ ВИДЕО", AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 10, 6, 0) });
            layerHead.Controls.Add(Button("+", AddImage)); f.Controls.Add(layerHead);
            layerChips = new FlowLayoutPanel { Width = 358, Height = 44, WrapContents = true, AutoSize = true, MinimumSize = new Size(0, 40), MaximumSize = new Size(0, 120) }; f.Controls.Add(layerChips);
            imageCard = new AssemblyAssetCard("Выбранная картинка", "+ Картинки", () => ImportSceneFiles(false)); imageCard.Width = 358;
            imageCard.FolderClick += () => SourceMenu(false, imageCard); imageCard.FilesDropped += files => ImportSceneFiles(false, files); f.Controls.Add(imageCard);
            var modes = Row(44); modes.Width = 358;
            changing = Button("Менять", () => SetImageMode(0)); permanent = Button("Одна", () => SetImageMode(1)); fresh = Button("Без повторов", () => SetImageMode(2)); modes.Controls.AddRange(new Control[] { changing, permanent, fresh }); f.Controls.Add(modes);
            f.Controls.Add(new AssemblySlider("Размер картинки", width, "%") { Width = 358 });
            f.Controls.Add(new AssemblySlider("Видимость картинки", opacity, "%") { Width = 358 });
            var placement = Row(44); placement.Width = 358;
            placement.Controls.Add(Button("Сверху", () => PlaceImage(.08))); placement.Controls.Add(Button("По центру", () => PlaceImage(-1))); placement.Controls.Add(Button("Снизу", () => PlaceImage(-2))); f.Controls.Add(placement);
            var extra = Row(44); extra.Width = 358;
            visibleLayer = Button("◉ Показать", () => enabled.Checked = !enabled.Checked); extra.Controls.Add(visibleLayer); extra.Controls.Add(Button("⋯ Картинка", ShowImageMenu)); f.Controls.Add(extra);
        }
        void BuildExportEditor() {
            var f = Stack(); exportEditor.Controls.Add(f); FitStack(f);
            var music = new AssemblyAssetCard("Музыка на весь ролик", "+ Музыка", ChooseMusic) { Width = 358 }; music.FolderClick += () => { musicPath.Text = ""; ReadTop(); RefreshSmart(); }; musicCaption = music.Detail; f.Controls.Add(music);
            f.Controls.Add(new AssemblySlider("Громкость", volume, "%") { Width = 358 });
            var presets = Row(44); presets.Width = 358; foreach (int n in new[] { 1, 5, 10, 20 }) { int v = n; presets.Controls.Add(Button(v + " шт.", () => count.Value = v)); } f.Controls.Add(presets);
            f.Controls.Add(new AssemblySlider("Сколько роликов", count, "шт.") { Width = 358 });
            f.Controls.Add(Caption("Как будут меняться сцены"));
            var effects = Theme.MakeCombo(new[] { "Без перехода", "Плавно", "Через чёрный", "Через белый", "Сдвиг влево", "Сдвиг вправо", "Мягкий сдвиг", "Размытие", "Приближение" }); effects.Width = 358; f.Controls.Add(effects);
            effects.SelectedIndexChanged += (s, e) => { if (!loading) transition.SelectedIndex = effects.SelectedIndex; }; transition.SelectedIndexChanged += (s, e) => effects.SelectedIndex = transition.SelectedIndex;
            f.Controls.Add(new AssemblySlider("Мягкость перехода", transitionTime, "с") { Width = 358 });
            f.Controls.Add(Caption("Формат ролика")); var formats = Theme.MakeCombo(resolution.Items.Cast<string>().ToArray()); formats.Width = 358; f.Controls.Add(formats);
            formats.SelectedIndexChanged += (s, e) => { if (!loading) resolution.SelectedIndex = formats.SelectedIndex; }; resolution.SelectedIndexChanged += (s, e) => formats.SelectedIndex = resolution.SelectedIndex;
            var random = Button("Случайный участок видео", () => randomStart.Checked = !randomStart.Checked); random.Width = 358; f.Controls.Add(random); randomStart.CheckedChanged += (s, e) => random.BackColor = randomStart.Checked ? Theme.Accent : Theme.Elevated;
            var destination = new AssemblyAssetCard("Куда сохранять ролики", "Выбрать папку", ChooseOutput) { Width = 358 }; destination.FolderClick += () => Safe(() => { ReadTop(); Directory.CreateDirectory(template.Output); Ui.Open(template.Output); }); outputCaption = destination.Detail; f.Controls.Add(destination);
            f.Controls.Add(Caption("Видео можно использовать снова. Картинки\n«Без повторов» расходуются один раз.", 48));
        }
        void BuildTimeline() {
            foreach (Control c in timeline.Controls.Cast<Control>().ToArray()) c.Dispose(); timeline.Controls.Clear(); sceneCards.Clear();
            for (int i = 0; i <= template.Scenes.Count; i++) {
                int position = i; var add = Button("+", () => InsertScene(position)); add.Name = "InsertScene" + i; add.AutoSize = false; add.Size = new Size(36, 72); add.MinimumSize = new Size(36, 72); add.Padding = Padding.Empty; add.Margin = new Padding(0, 0, 6, 0); timeline.Controls.Add(add);
                if (i == template.Scenes.Count) break;
                int index = i; var card = Button("", () => { sceneList.SelectedIndex = index; SwitchInspector(false); }); card.Name = "SceneCard" + i; card.AutoSize = false; card.Size = new Size(180, 72); card.MinimumSize = card.Size; card.TextAlign = ContentAlignment.MiddleLeft; card.Padding = new Padding(12, 4, 8, 4); card.Margin = new Padding(0, 0, 6, 0); sceneCards.Add(card); timeline.Controls.Add(card);
            }
            RefreshSmart();
        }
        void RefreshLayerChips() {
            if (layerChips == null) return; foreach (Control c in layerChips.Controls.Cast<Control>().ToArray()) c.Dispose(); layerChips.Controls.Clear();
            if (SelectedScene == null) return;
            for (int i = 0; i < SelectedScene.Layers.Count; i++) { int index = i; var b = Button(SelectedScene.Layers[i].Name, () => layerList.SelectedIndex = index); b.Tag = i; layerChips.Controls.Add(b); }
        }
        void RefreshSmart() {
            if (videoCard == null || SelectedScene == null) return;
            sceneCaption.Text = SelectedScene.Name;
            for (int i = 0; i < sceneCards.Count && i < template.Scenes.Count; i++) { sceneCards[i].Text = (i + 1) + "  " + template.Scenes[i].Name + "\n" + template.Scenes[i].Duration.ToString("0.##") + " сек  ·  " + template.Scenes[i].Layers.Count + " карт."; sceneCards[i].BackColor = i == sceneList.SelectedIndex ? Theme.Accent : Theme.Card; }
            foreach (Button b in layerChips.Controls.OfType<Button>()) { int i = (int)b.Tag; b.Text = SelectedScene.Layers[i].Name; b.BackColor = i == layerList.SelectedIndex ? Theme.Accent : Theme.Elevated; }
            var videos = PoolSafe(SceneVideoSource(), AssemblyFiles.VideoExtensions); videoCard.SetInfo("Фоновое видео", videos.Count == 0 ? "Добавь короткие видео для этой сцены" : videos.Count + " видео · варианты для сборки", null);
            var l = SelectedLayer; imageCard.Enabled = l != null;
            foreach (var b in new[] { changing, permanent, fresh, visibleLayer }) b.Enabled = l != null;
            if (l != null) { var files = PoolSafe(l.Source, AssemblyFiles.ImageExtensions); imageCard.SetInfo(l.Name, files.Count == 0 ? "Добавь готовую картинку с текстом" : files.Count + " карт. · " + (l.Fixed ? "всегда одна" : l.Unique ? "без повторов" : "выбирается случайно"), files.FirstOrDefault()); }
            else imageCard.SetInfo("Пока без картинок", "Нажми + рядом с заголовком выше", null);
            changing.BackColor = l != null && !l.Fixed && !l.Unique ? Theme.Accent : Theme.Elevated; permanent.BackColor = l?.Fixed == true ? Theme.Accent : Theme.Elevated; fresh.BackColor = l?.Unique == true ? Theme.Accent : Theme.Elevated;
            visibleLayer.Text = l?.Enabled == true ? "◉ Видна" : "○ Скрыта";
            if (musicCaption != null) musicCaption.Text = string.IsNullOrWhiteSpace(musicPath.Text) ? "Можно собрать без музыки" : Path.GetFileName(musicPath.Text);
            if (outputCaption != null) outputCaption.Text = string.IsNullOrWhiteSpace(output.Text) ? "Выбери папку" : Path.GetFileName(output.Text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        List<string> PoolSafe(string path, string[] extensions) { try { return AssemblyFiles.Pool(template.Resolve(path), extensions); } catch { return new List<string>(); } }
        string SceneVideoSource() { return string.IsNullOrWhiteSpace(SelectedScene.Videos) ? template.Videos : SelectedScene.Videos; }
        void InsertScene(int index) { if (index == 0) { sceneList.SelectedIndex = 0; AddScene(false); } else { sceneList.SelectedIndex = index - 1; AddScene(true); } }
        void SetImageMode(int mode) { if (SelectedLayer == null) return; loading = true; fixedAsset.Checked = mode == 1; unique.Checked = mode == 2; loading = false; EditorChanged(); }
        void PlaceImage(double position) { if (SelectedLayer == null) return; x.Value = (decimal)((1 - SelectedLayer.Width) * 50); y.Value = (decimal)((position == -1 ? (1 - SelectedLayer.Height) / 2 : position == -2 ? Math.Max(0, .92 - SelectedLayer.Height) : Math.Min(position, 1 - SelectedLayer.Height)) * 100); }
        void AddImage() { Safe(() => { if (SelectedScene.Layers.Count >= 8) throw new Exception("В сцене можно разместить до 8 картинок."); string folder = Path.Combine("Сцены", "Картинка-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(template.Resolve(folder)); SelectedScene.Layers.Add(new AssemblyLayer { Name = "Новая картинка", Source = folder }); previewPlan = null; RefreshLayers(SelectedScene.Layers.Count - 1); Save(); }); }
        void ShowSceneMenu() { var m = Theme.MakeContextMenu(); m.Items.Add("Переименовать…", null, (s, e) => Rename(sceneName, "Название сцены")); m.Items.Add("Добавить перед", null, (s, e) => AddScene(false)); m.Items.Add("Добавить после", null, (s, e) => AddScene(true)); m.Items.Add("Удалить сцену", null, (s, e) => Safe(() => { if (template.Scenes.Count <= 2) throw new Exception("Оставь минимум две сцены."); int i = sceneList.SelectedIndex; template.Scenes.RemoveAt(i); previewPlan = null; RefreshScenes(Math.Min(i, template.Scenes.Count - 1)); })); m.Show(Cursor.Position); }
        void ShowImageMenu() { if (SelectedLayer == null) return; var m = Theme.MakeContextMenu(); m.Items.Add("Переименовать…", null, (s, e) => Rename(layerName, "Название картинки")); m.Items.Add("Точная настройка…", null, (s, e) => AdvancedImage()); m.Items.Add("Удалить картинку", null, (s, e) => Safe(() => { int i = layerList.SelectedIndex; SelectedScene.Layers.RemoveAt(i); previewPlan = null; RefreshLayers(Math.Min(i, SelectedScene.Layers.Count - 1)); })); m.Show(Cursor.Position); }
        void Rename(TextBox target, string title) { using (var form = new Form { Text = title, ClientSize = new Size(360, 114), BackColor = Theme.Background, ForeColor = Theme.TextPrimary, Font = Theme.FontBody, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false }) { var input = Theme.MakeSearchBox(); input.Text = target.Text; input.SetBounds(16, 16, 328, 28); form.Controls.Add(input); var ok = Button("Готово", () => { target.Text = input.Text; form.Close(); }, true); ok.SetBounds(226, 62, 118, 36); form.Controls.Add(ok); form.AcceptButton = ok; form.ShowDialog(this); } }
        void SourceMenu(bool video, Control owner) {
            if (!video && SelectedLayer == null) return; var m = Theme.MakeContextMenu();
            m.Items.Add("Открыть папку материалов", null, (s, e) => Safe(() => { string path = template.Resolve(video ? SceneVideoSource() : SelectedLayer.Source); if (File.Exists(path)) path = Path.GetDirectoryName(path); Directory.CreateDirectory(path); Ui.Open(path); }));
            m.Items.Add("Использовать другую папку…", null, (s, e) => { var p = Ui.Folder(this, template.Materials); if (p != null) { if (video) videoSource.Text = p; else layerSource.Text = p; } });
            if (!video) m.Items.Add("Использовать одну картинку…", null, (s, e) => { using (var d = new OpenFileDialog { Filter = "Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.txt" }) if (d.ShowDialog(this) == DialogResult.OK) { layerSource.Text = d.FileName; SetImageMode(1); } });
            m.Show(owner, new Point(0, owner.Height));
        }
        void ImportSceneFiles(bool video, string[] files = null) {
            Safe(() => {
                if (!video && SelectedLayer == null) return;
                if (files == null) using (var d = new OpenFileDialog { Multiselect = true, Filter = video ? "Видео|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v" : "Картинки|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.txt" }) { if (d.ShowDialog(this) != DialogResult.OK) return; files = d.FileNames; }
                ReadTop(); string source = video ? SceneVideoSource() : SelectedLayer.Source; string folder = template.Resolve(source); var extensions = video ? AssemblyFiles.VideoExtensions : AssemblyFiles.ImageExtensions;
                if (File.Exists(folder) || extensions.Contains(Path.GetExtension(folder).ToLowerInvariant())) { source = Path.Combine("Сцены", "Материалы-" + Guid.NewGuid().ToString("N").Substring(0, 6)); if (video) videoSource.Text = source; else layerSource.Text = source; folder = template.Resolve(source); }
                Directory.CreateDirectory(folder); int added = 0;
                foreach (string file in files) { if (!File.Exists(file) || !extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue; string dest = Path.Combine(folder, Path.GetFileName(file)); if (Path.GetFullPath(file).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue; if (File.Exists(dest)) dest = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + Path.GetExtension(file)); File.Copy(file, dest); added++; }
                previewPlan = null; RefreshSources(); RefreshCanvas(); Save(); status.Text = "Добавлено: " + added + ". Картинку можно двигать в просмотре справа.";
            });
        }
        void ChooseMusic() { using (var d = new OpenFileDialog { Filter = "Музыка|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac" }) if (d.ShowDialog(this) == DialogResult.OK) { musicPath.Text = d.FileName; ReadTop(); RefreshSmart(); } }
        void ChooseOutput() { var p = Ui.Folder(this, output.Text); if (p != null) { output.Text = p; ReadTop(); RefreshSmart(); } }
        void ChooseMaterials() { var p = Ui.Folder(this, materials.Text); if (p != null) { materials.Text = p; ReadTop(); previewPlan = null; canvas.SetBackground(null); RefreshSources(); RefreshCanvas(); } }
        void ExportTemplate() { Safe(() => { Save(); using (var d = new SaveFileDialog { Filter = "Шаблон|*.xml", DefaultExt = "xml", FileName = "VideoBatch-template.xml" }) if (d.ShowDialog(this) == DialogResult.OK) AssemblyFiles.Save(d.FileName, template); }); }
        void AdvancedImage() {
            using (var form = new Form { Text = "Точная настройка картинки", ClientSize = new Size(410, 630), BackColor = Theme.Background, ForeColor = Theme.TextPrimary, Font = Theme.FontBody, StartPosition = FormStartPosition.CenterParent }) {
                var f = Stack(); f.Padding = new Padding(16); form.Controls.Add(f);
                foreach (var entry in new[] { Tuple.Create("По горизонтали", x, "%"), Tuple.Create("По вертикали", y, "%"), Tuple.Create("Ширина", width, "%"), Tuple.Create("Высота", height, "%"), Tuple.Create("Появится через", start, "с"), Tuple.Create("Исчезнет через · 0 = до конца", end, "с"), Tuple.Create("Для TXT: размер текста", fontSize, ""), Tuple.Create("Для TXT: круглые углы", radius, ""), Tuple.Create("Для TXT: отступ внутри", padding, "") }) f.Controls.Add(new AssemblySlider(entry.Item1, entry.Item2, entry.Item3) { Width = 350 });
                f.Controls.Add(Button("Шрифт TXT…", () => Rename(fontName, "Шрифт TXT"))); f.Controls.Add(Button("Готово", () => form.Close(), true)); form.ShowDialog(this);
            }
        }
        AssemblyScene SelectedScene { get { return sceneList.SelectedIndex >= 0 ? template.Scenes[sceneList.SelectedIndex] : null; } }
        AssemblyLayer SelectedLayer { get { return SelectedScene != null && layerList.SelectedIndex >= 0 ? SelectedScene.Layers[layerList.SelectedIndex] : null; } }
        void LoadTop() {
            loading = true; materials.Text = template.Materials; output.Text = template.Output; musicPath.Text = template.Music;
            count.Value = template.Count; transition.SelectedItem = template.Transition; transitionTime.Value = (decimal)template.TransitionDuration; volume.Value = (decimal)(template.MusicVolume * 100); randomStart.Checked = template.RandomVideoStart;
            resolution.SelectedIndex = template.Width == 720 ? 1 : template.Width == 1920 ? 2 : template.Width == 1280 ? 3 : 0; loading = false;
        }
        void ReadTop() {
            template.Materials = materials.Text.Trim(); template.Output = output.Text.Trim(); template.Music = musicPath.Text.Trim(); template.Count = (int)count.Value;
            template.Transition = (string)transition.SelectedItem ?? "fade"; template.TransitionDuration = (double)transitionTime.Value; template.MusicVolume = (double)volume.Value / 100; template.RandomVideoStart = randomStart.Checked;
            int[,] sizes = { { 1080, 1920 }, { 720, 1280 }, { 1920, 1080 }, { 1280, 720 } }; int index = Math.Max(0, resolution.SelectedIndex); template.Width = sizes[index, 0]; template.Height = sizes[index, 1];
        }
        void RefreshScenes(int selected) { loading = true; sceneList.Items.Clear(); foreach (var s in template.Scenes) sceneList.Items.Add(s); sceneList.SelectedIndex = selected; loading = false; BuildTimeline(); LoadScene(); }
        void LoadScene() {
            if (SelectedScene == null) return; canvas?.SetBackground(null); loading = true; sceneName.Text = SelectedScene.Name; duration.Value = (decimal)SelectedScene.Duration; videoSource.Text = SelectedScene.Videos; loading = false; RefreshLayers(0); RefreshSources();
        }
        void RefreshLayers(int selected) { loading = true; layerList.Items.Clear(); if (SelectedScene != null) foreach (var l in SelectedScene.Layers) layerList.Items.Add(l.Name); layerList.SelectedIndex = layerList.Items.Count == 0 ? -1 : Math.Max(0, selected); loading = false; RefreshLayerChips(); LoadLayer(); RefreshCanvas(); RefreshSources(); }
        void LoadLayer() {
            loading = true; var l = SelectedLayer;
            foreach (Control c in new Control[] { layerName, layerSource, fontName, enabled, unique, fixedAsset, x, y, width, height, opacity, start, end, fontSize, radius, padding }) c.Enabled = l != null;
            if (l != null) {
                layerName.Text = l.Name; layerSource.Text = l.Source; enabled.Checked = l.Enabled; unique.Checked = l.Unique; fixedAsset.Checked = l.Fixed;
                x.Value = (decimal)(l.X * 100); y.Value = (decimal)(l.Y * 100); width.Value = (decimal)(l.Width * 100); height.Value = (decimal)(l.Height * 100); opacity.Value = (decimal)(l.Opacity * 100);
                start.Value = (decimal)l.Start; end.Value = (decimal)l.End; fontName.Text = l.Font; fontSize.Value = (decimal)l.FontSize; radius.Value = (decimal)l.Radius; padding.Value = (decimal)l.Padding;
            }
            loading = false;
        }
        void EditorChanged() {
            if (loading || SelectedScene == null) return;
            bool durationChanged = Math.Abs(SelectedScene.Duration - (double)duration.Value) > .00001;
            SelectedScene.Name = sceneName.Text; SelectedScene.Duration = (double)duration.Value; SelectedScene.Videos = videoSource.Text.Trim();
            // Keep timing valid when a user shortens a scene with the slider.
            if (durationChanged) {
                loading = true;
                foreach (var layer in SelectedScene.Layers) { layer.Start = Math.Min(layer.Start, Math.Max(0, SelectedScene.Duration - .1)); if (layer.End > SelectedScene.Duration || layer.End != 0 && layer.End <= layer.Start) layer.End = 0; }
                if (SelectedLayer != null) { start.Value = (decimal)SelectedLayer.Start; end.Value = (decimal)SelectedLayer.End; }
                if (template.Transition != "cut") { decimal limit = (decimal)Math.Max(0, template.Scenes.Min(s => s.Duration) / 2 - .01); if (transitionTime.Value > limit) { transitionTime.Value = limit; template.TransitionDuration = (double)limit; } }
                loading = false;
            }
            var l = SelectedLayer;
            if (l != null) {
                l.Name = layerName.Text; l.Source = layerSource.Text.Trim(); l.Enabled = enabled.Checked;
                if (fixedAsset.Checked && unique.Checked) { loading = true; if (l.Fixed) fixedAsset.Checked = false; else unique.Checked = false; loading = false; }
                double requestedWidth = (double)width.Value / 100;
                if (Math.Abs(requestedWidth - l.Width) > .00001) { loading = true; height.Value = (decimal)Math.Max(2, Math.Min(100, l.Height * requestedWidth / l.Width * 100)); loading = false; }
                l.Unique = unique.Checked; l.Fixed = fixedAsset.Checked; l.X = (double)x.Value / 100; l.Y = (double)y.Value / 100;
                l.Width = Math.Min((double)width.Value / 100, 1 - l.X); l.Height = Math.Min((double)height.Value / 100, 1 - l.Y);
                if (l.Width < .02) { l.X = .98; l.Width = .02; } if (l.Height < .02) { l.Y = .98; l.Height = .02; }
                l.Opacity = (double)opacity.Value / 100; l.Start = (double)start.Value; l.End = (double)end.Value; l.Font = fontName.Text; l.FontSize = (double)fontSize.Value; l.Radius = (double)radius.Value; l.Padding = (double)padding.Value;
                loading = true; x.Value = (decimal)(l.X * 100); y.Value = (decimal)(l.Y * 100); width.Value = (decimal)(l.Width * 100); height.Value = (decimal)(l.Height * 100); layerList.Items[layerList.SelectedIndex] = l.Name; loading = false;
            }
            loading = true; sceneList.Items[sceneList.SelectedIndex] = SelectedScene; loading = false; previewPlan = null; RefreshCanvas(); RefreshSources();
        }
        void RefreshCanvas() {
            if (canvas == null || SelectedScene == null) return;
            var sp = new AssemblyScenePlan { Scene = SelectedScene }; int i = 0;
            foreach (var l in SelectedScene.Layers.Where(l => l.Enabled)) {
                try {
                    var pp = previewPlan != null && sceneList.SelectedIndex < previewPlan.Scenes.Count ? previewPlan.Scenes[sceneList.SelectedIndex] : null;
                    if (pp != null && i < pp.Layers.Count) sp.Layers.Add(new AssemblyLayerPlan { Layer = l, Asset = pp.Layers[i].Asset });
                    else { var files = AssemblyFiles.Pool(template.Resolve(l.Source), AssemblyFiles.ImageExtensions); if (files.Count > 0) sp.Layers.Add(new AssemblyLayerPlan { Layer = l, Asset = new AssemblyAsset { Path = files[0] } }); }
                } catch { } i++;
            }
            canvas.SetScene(template, sp, SelectedLayer); total.Text = template.Scenes.Count + " сцены · " + template.Duration.ToString("0.##") + " сек · к выпуску " + template.Count + " роликов"; RefreshSmart();
        }
        sealed class SourceTarget { public string Name, Path; public string[] Extensions; }
        void RefreshSources() {
            if (sources == null) return; sources.Items.Clear();
            var all = new List<SourceTarget> { new SourceTarget { Name = "Общие видео", Path = template.Videos, Extensions = AssemblyFiles.VideoExtensions } };
            all.AddRange(template.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.Videos)).Select(s => new SourceTarget { Name = "Видео: " + s.Name, Path = s.Videos, Extensions = AssemblyFiles.VideoExtensions }));
            all.AddRange(template.Scenes.SelectMany(s => s.Layers).Where(l => l.Enabled).Select(l => new SourceTarget { Name = l.Name, Path = l.Source, Extensions = AssemblyFiles.ImageExtensions }));
            foreach (var target in all.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())) {
                int size = 0; try { size = AssemblyFiles.Pool(template.Resolve(target.Path), target.Extensions).Count; } catch { }
                var item = new ListViewItem(new[] { target.Name, target.Path, size.ToString() }) { Tag = target }; sources.Items.Add(item);
            }
            RefreshSmart();
        }
        void CreateFolders() { Safe(() => { ReadTop(); if (string.IsNullOrWhiteSpace(template.Materials)) throw new Exception("Выбери общую папку материалов."); Directory.CreateDirectory(template.Materials); RefreshSources(); foreach (ListViewItem item in sources.Items) { var target = (SourceTarget)item.Tag; string path = template.Resolve(target.Path); if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path) && (Directory.Exists(path) || !target.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))) Directory.CreateDirectory(path); } RefreshSources(); Ui.Open(template.Materials); }); }
        void AddFiles() { Safe(() => { if (sources.SelectedItems.Count == 0) throw new Exception("Выбери строку материала в списке."); using (var d = new OpenFileDialog { Multiselect = true, Filter = "Все файлы|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) CopyFiles(d.FileNames); }); }
        void CopyFiles(string[] files) {
            ReadTop(); if (sources.SelectedItems.Count == 0) throw new Exception("Выбери строку материала."); var target = (SourceTarget)sources.SelectedItems[0].Tag;
            string folder = template.Resolve(target.Path); if (File.Exists(folder) || (!Directory.Exists(folder) && target.Extensions.Contains(Path.GetExtension(folder).ToLowerInvariant()))) throw new Exception("Этот источник — отдельный файл. Его можно заменить на вкладке 2."); Directory.CreateDirectory(folder);
            foreach (string file in files) {
                if (!File.Exists(file) || !target.Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                string dest = Path.Combine(folder, Path.GetFileName(file)); if (Path.GetFullPath(file).Equals(dest, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(dest)) dest = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + Path.GetExtension(file));
                File.Copy(file, dest);
            }
            previewPlan = null; RefreshSources(); RefreshCanvas(); status.Text = "Материалы добавлены. Вкладка 2 — расположение изображений.";
        }
        void OpenSource() { Safe(() => { ReadTop(); if (sources.SelectedItems.Count == 0) throw new Exception("Выбери строку материала."); string path = template.Resolve(((SourceTarget)sources.SelectedItems[0].Tag).Path); if (File.Exists(path)) path = Path.GetDirectoryName(path); Directory.CreateDirectory(path); Ui.Open(path); }); }
        void MoveScene(int delta) { Safe(() => { int i = sceneList.SelectedIndex, next = i + delta; if (next < 0 || next >= template.Scenes.Count) return; var item = template.Scenes[i]; template.Scenes.RemoveAt(i); template.Scenes.Insert(next, item); previewPlan = null; RefreshScenes(next); }); }
        void AddScene(bool after) {
            Safe(() => {
                ReadTop(); if (template.Scenes.Count >= 12) throw new Exception("В одном шаблоне поддерживается до 12 сцен.");
                if (string.IsNullOrWhiteSpace(template.Materials)) throw new Exception("Выбери папку материалов в меню «Шаблон».");
                int index = Math.Max(0, sceneList.SelectedIndex) + (after ? 1 : 0);
                string relative = Path.Combine("Сцены", "Сцена-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                string video = Path.Combine(relative, "Видео"), image = Path.Combine(relative, "Изображения");
                Directory.CreateDirectory(template.Resolve(video)); Directory.CreateDirectory(template.Resolve(image));
                var scene = new AssemblyScene { Name = "Новая сцена", Videos = video, Duration = 4, Layers = new List<AssemblyLayer> { new AssemblyLayer { Name = "Изображение новой сцены", Source = image } } };
                template.Scenes.Insert(Math.Min(index, template.Scenes.Count), scene); previewPlan = null; RefreshScenes(Math.Min(index, template.Scenes.Count - 1));
                Save(); status.Text = "Сцена добавлена. Созданы папки «Видео» и «Изображения»: " + template.Resolve(relative);
            });
        }
        void Save() { ReadTop(); template.Validate(); AssemblyFiles.Save(templatePath, template); status.Text = "Шаблон сохранён. Эти папки, время и расположение восстановятся при следующем открытии."; }
        void LoadTemplate() { Safe(() => { using (var d = new OpenFileDialog { Filter = "Шаблон сборки|*.xml" }) if (d.ShowDialog(this) == DialogResult.OK) { var t = AssemblyFiles.Load<AssemblyTemplate>(d.FileName); t.Validate(); template = t; previewPlan = null; LoadTop(); RefreshScenes(0); RefreshSources(); } }); }
        void Busy(bool busy) {
            left.Enabled = !busy; canvas.Enabled = !busy; header.Enabled = !busy; timeline.Enabled = !busy; foreach (Control c in footer.Controls) c.Enabled = !busy; stop.Visible = busy; stop.Enabled = busy; if (busy) progress.Value = 0;
        }
        async Task<AssemblyPlan> Sample(AssemblyTemplate t, string probe, CancellationToken ct) {
            var videos = await AssemblyEngine.ReadVideos(t, probe, ct); string path = Path.Combine(historyFolder, t.Id + ".xml");
            var h = File.Exists(path) ? AssemblyFiles.Load<AssemblyHistory>(path) : new AssemblyHistory(); var batch = AssemblyPlanner.Create(t, videos, h, new Random(), 1);
            if (batch.Plans.Count == 0) throw new Exception(batch.Limit); return batch.Plans[0];
        }
        async Task ShowSample() {
            if (cancellation != null) return;
            try {
                ReadTop(); var files = PoolSafe(SceneVideoSource(), AssemblyFiles.VideoExtensions);
                if (files.Count == 0) throw new Exception("Добавь видео в выбранную сцену кнопкой «+ Видео».");
                if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
                cancellation = new CancellationTokenSource(); Busy(true); var t = Store.Clone(template);
                var selected = previewPlan != null && sceneList.SelectedIndex < previewPlan.Scenes.Count ? previewPlan.Scenes[sceneList.SelectedIndex] : null;
                string video = selected?.Video.Path ?? files[0]; double offset = selected?.VideoStart ?? 0;
                Directory.CreateDirectory(previewFolder); string path = Path.Combine(previewFolder, Guid.NewGuid().ToString("N") + ".jpg");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-ss", Core.N(offset), "-i", video, "-frames:v", "1", "-vf", "scale=" + t.Width + ":" + t.Height + ":force_original_aspect_ratio=increase,crop=" + t.Width + ":" + t.Height, path }, cancellation.Token, null, 60);
                using (var image = Image.FromFile(path)) canvas.SetBackground(new Bitmap(image)); File.Delete(path); RefreshCanvas(); status.Text = "Фон готов. Двигай картинку мышью. Музыку и переходы можно проверить в примере.";
            } catch (OperationCanceledException) { status.Text = "Предпросмотр остановлен."; } catch (Exception e) { status.Text = e.Message; Ui.Error(this, e); } finally { Finish(); }
        }
        async Task Run(bool sample) {
            if (cancellation != null) return;
            try {
                Save(); if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
                var t = Store.Clone(template); cancellation = new CancellationTokenSource(); Busy(true);
                var updates = new Progress<Update>(u => { status.Text = u.Text; progress.Value = (int)Math.Max(0, Math.Min(1000, u.Percent * 10)); });
                if (sample) {
                    var plan = await Sample(t, probe, cancellation.Token); previewPlan = plan; Directory.CreateDirectory(previewFolder);
                    string path = Path.Combine(previewFolder, "preview-" + Guid.NewGuid().ToString("N") + ".mp4"), work = Path.Combine(previewFolder, "render-" + Guid.NewGuid().ToString("N"));
                    try { await Task.Run(() => AssemblyEngine.Render(t, plan, ffmpeg, probe, path, work, AssemblyRaster.Save, cancellation.Token, s => ((IProgress<Update>)updates).Report(new Update(s, 0)))); }
                    finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
                    RefreshCanvas(); status.Text = "Пример готов: " + t.Duration.ToString("0.##") + " с. Заголовки не израсходованы."; progress.Value = 1000; Ui.Open(path);
                } else {
                    var result = await Task.Run(() => AssemblyEngine.Run(t, ffmpeg, probe, historyFolder, AssemblyRaster.Save, updates, cancellation.Token)); previewPlan = null; RefreshSources();
                    if (result.Errors.Count > 0) MessageBox.Show(this, "Готовых роликов: " + result.Outputs.Count + "\n\n" + string.Join("\n\n", result.Errors), "Сборка роликов", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            } catch (OperationCanceledException) { status.Text = "Сборка остановлена."; } catch (Exception e) { status.Text = e.Message; Ui.Error(this, e); } finally { Finish(); }
        }
        void Finish() { cancellation?.Dispose(); cancellation = null; Busy(false); if (closeAfter) Close(); }
        void Safe(Action action) { try { action(); } catch (Exception e) { if (throwErrors) throw; if (status != null) status.Text = e.Message; Ui.Error(this, e); } }
        protected override void Dispose(bool disposing) { if (disposing && Directory.Exists(previewFolder)) try { Directory.Delete(previewFolder, true); } catch { } base.Dispose(disposing); }
    }
    public sealed class AssemblyCanvas : Control {
        AssemblyTemplate template;
        AssemblyScenePlan scene;
        AssemblyLayer selected;
        Image background;
        readonly List<Bitmap> images = new List<Bitmap>();
        Point origin;
        double oldX, oldY, oldWidth, oldHeight;
        bool dragging, resizing;
        public event Action<int> LayerSelected;
        public event Action PlacementChanged;
        public AssemblyCanvas() { DoubleBuffered = true; BackColor = Theme.Background; }
        public void SetBackground(Image value) { background?.Dispose(); background = value; Invalidate(); }
        public void SetScene(AssemblyTemplate t, AssemblyScenePlan s, AssemblyLayer chosen) {
            template = t; scene = s; selected = chosen;
            foreach (var image in images) image?.Dispose(); images.Clear();
            foreach (var l in scene.Layers) try { images.Add(AssemblyRaster.Create(t, l)); } catch { images.Add(null); }
            Invalidate();
        }
        RectangleF Frame {
            get { if (template == null) return RectangleF.Empty; float scale = Math.Min((Width - 16f) / template.Width, (Height - 16f) / template.Height); return new RectangleF((Width - template.Width * scale) / 2, (Height - template.Height * scale) / 2, template.Width * scale, template.Height * scale); }
        }
        RectangleF Box(AssemblyLayer l) { var f = Frame; return new RectangleF(f.X + (float)l.X * f.Width, f.Y + (float)l.Y * f.Height, (float)l.Width * f.Width, (float)l.Height * f.Height); }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); if (scene == null || Frame.Width <= 0) return; var f = Frame; e.Graphics.FillRectangle(Brushes.Black, f);
            if (background != null) e.Graphics.DrawImage(background, f);
            else { using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) e.Graphics.DrawString("ВИДЕО СЦЕНЫ\n\nДобавь видео слева\nи нажми «Обновить фон»", Font, Brushes.Gray, f, format); }
            for (int i = 0; i < images.Count; i++) if (images[i] != null) e.Graphics.DrawImage(images[i], f);
            foreach (var l in scene.Scene.Layers.Where(l => l.Enabled)) {
                var box = Box(l); using (var pen = new Pen(l == selected ? Theme.Accent : Theme.TextMuted, l == selected ? 2 : 1)) e.Graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                using (var brush = new SolidBrush(Theme.Accent)) { e.Graphics.DrawString(l.Name, Font, brush, box.X + 4, box.Y + 3); if (l == selected) e.Graphics.FillEllipse(brush, box.Right - 6, box.Bottom - 6, 12, 12); }
            }
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e); if (scene == null || e.Button != MouseButtons.Left) return;
            var hit = scene.Scene.Layers.LastOrDefault(l => l.Enabled && Box(l).Contains(e.Location)); if (hit == null) return;
            selected = hit; LayerSelected?.Invoke(scene.Scene.Layers.IndexOf(hit)); var box = Box(hit); resizing = e.X >= box.Right - 14 && e.Y >= box.Bottom - 14;
            dragging = true; Capture = true; origin = e.Location; oldX = hit.X; oldY = hit.Y; oldWidth = hit.Width; oldHeight = hit.Height;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e); if (!dragging || selected == null) return; var f = Frame; double dx = (e.X - origin.X) / f.Width, dy = (e.Y - origin.Y) / f.Height;
            if (resizing) { selected.Width = Math.Max(.02, Math.Min(1 - selected.X, oldWidth + dx)); selected.Height = Math.Max(.02, Math.Min(1 - selected.Y, oldHeight + dy)); }
            else { selected.X = Math.Max(0, Math.Min(1 - selected.Width, oldX + dx)); selected.Y = Math.Max(0, Math.Min(1 - selected.Height, oldY + dy)); }
            PlacementChanged?.Invoke(); Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
        protected override void Dispose(bool disposing) { if (disposing) { background?.Dispose(); foreach (var image in images) image?.Dispose(); } base.Dispose(disposing); }
    }
    // A keyboard-accessible slider with a visible value, no technical input box.
    public sealed class AssemblySlider : Control {
        readonly NumericUpDown value;
        readonly string caption, unit;
        readonly EventHandler valueChanged, enabledChanged;
        bool dragging;
        public AssemblySlider(string caption, NumericUpDown value, string unit) {
            this.caption = caption; this.value = value; this.unit = unit;
            Name = "Slider"; Height = 66; Margin = new Padding(0, 0, 0, 8); Cursor = Cursors.Hand; TabStop = true; DoubleBuffered = true; BackColor = Theme.Card; ForeColor = Theme.TextPrimary;
            AccessibleName = caption; AccessibleRole = AccessibleRole.Slider;
            valueChanged = (s, e) => { AccessibleDescription = value.Value.ToString("0.##") + " " + unit; Invalidate(); };
            enabledChanged = (s, e) => Enabled = value.Enabled;
            value.ValueChanged += valueChanged; value.EnabledChanged += enabledChanged; Enabled = value.Enabled;
            SetStyle(ControlStyles.Selectable, true);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var text = new SolidBrush(Enabled ? Theme.TextPrimary : Theme.TextMuted)) {
                e.Graphics.DrawString(caption, Font, text, 12, 9);
                var label = value.Value.ToString("0.##") + (unit == "" ? "" : " " + unit);
                using (var format = new StringFormat { Alignment = StringAlignment.Far }) e.Graphics.DrawString(label, Font, text, new RectangleF(Width - 98, 9, 84, 24), format);
            }
            float left = 18, right = Width - 18, top = 46;
            float ratio = (float)((value.Value - value.Minimum) / (value.Maximum - value.Minimum));
            using (var track = new Pen(Theme.Border, 5)) { track.StartCap = track.EndCap = LineCap.Round; e.Graphics.DrawLine(track, left, top, right, top); }
            using (var track = new Pen(Enabled ? Theme.Accent : Theme.TextMuted, 5)) { track.StartCap = track.EndCap = LineCap.Round; e.Graphics.DrawLine(track, left, top, left + (right - left) * ratio, top); }
            using (var brush = new SolidBrush(Enabled ? Theme.TextPrimary : Theme.TextMuted)) e.Graphics.FillEllipse(brush, left + (right - left) * ratio - 7, top - 7, 14, 14);
            if (Focused) using (var pen = new Pen(Theme.Accent)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
        void At(int x) { decimal ratio = (decimal)Math.Max(0, Math.Min(1, (x - 18.0) / Math.Max(1, Width - 36))); value.Value = Math.Round(value.Minimum + ratio * (value.Maximum - value.Minimum), value.DecimalPlaces); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!Enabled || e.Button != MouseButtons.Left) return; Focus(); dragging = true; Capture = true; At(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) At(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e) {
            base.OnKeyDown(e); decimal step = value.Increment;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) { value.Value = Math.Max(value.Minimum, Math.Min(value.Maximum, value.Value + (e.KeyCode == Keys.Left ? -step : step))); e.Handled = true; }
            if (e.KeyCode == Keys.Home) { value.Value = value.Minimum; e.Handled = true; } if (e.KeyCode == Keys.End) { value.Value = value.Maximum; e.Handled = true; }
        }
        protected override void Dispose(bool disposing) { if (disposing) { value.ValueChanged -= valueChanged; value.EnabledChanged -= enabledChanged; } base.Dispose(disposing); }
    }
    public sealed class AssemblyAssetCard : Panel {
        readonly PictureBox thumbnail;
        readonly Label title;
        readonly Button add, folder;
        string currentImage;
        public readonly Label Detail;
        public event Action FolderClick;
        public event Action<string[]> FilesDropped;
        public AssemblyAssetCard(string caption, string action, Action click) {
            Height = 110; BackColor = Theme.Card; Margin = new Padding(0, 0, 0, 10); Padding = new Padding(10); AllowDrop = true;
            thumbnail = new PictureBox { BackColor = Theme.Background, SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(10, 10), Size = new Size(70, 90) }; Controls.Add(thumbnail);
            title = new Label { Text = caption, Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary, AutoEllipsis = true, Location = new Point(94, 12), Height = 26 }; Controls.Add(title);
            Detail = new Label { ForeColor = Theme.TextSecondary, AutoEllipsis = true, Location = new Point(94, 38), Height = 27 }; Controls.Add(Detail);
            add = Theme.MakeButton(action, action: click); add.AutoSize = false; add.SetBounds(94, 66, 170, 34); Controls.Add(add);
            folder = Theme.MakeIconButton("⋯", () => FolderClick?.Invoke()); folder.Location = new Point(276, 66); Controls.Add(folder);
            SizeChanged += (s, e) => { title.Width = Detail.Width = Math.Max(80, Width - 106); folder.Left = Width - 48; add.Width = Math.Max(100, Math.Min(170, folder.Left - 102)); };
            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => FilesDropped?.Invoke((string[])e.Data.GetData(DataFormats.FileDrop));
        }
        public void SetInfo(string caption, string detail, string path) {
            title.Text = caption; Detail.Text = detail; AccessibleName = caption + ". " + detail;
            if (path == currentImage) return; currentImage = path; var old = thumbnail.Image; thumbnail.Image = null; old?.Dispose();
            if (string.IsNullOrWhiteSpace(path) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) return;
            try { using (var source = Image.FromFile(path)) { var b = new Bitmap(140, 216); using (var g = Graphics.FromImage(b)) { g.Clear(Theme.Background); double r = Math.Min(140.0 / source.Width, 216.0 / source.Height); int w = Math.Max(1, (int)(source.Width * r)), h = Math.Max(1, (int)(source.Height * r)); g.DrawImage(source, (140 - w) / 2, (216 - h) / 2, w, h); } thumbnail.Image = b; } } catch { }
        }
        protected override void Dispose(bool disposing) { if (disposing) { thumbnail.Image?.Dispose(); thumbnail.Image = null; } base.Dispose(disposing); }
    }
}
