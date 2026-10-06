using System;
using System.Collections.Generic;
using System.Drawing;
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
        Panel left;
        FlowLayoutPanel footer;
        Label status, total;
        ProgressBar progress;
        Button stop;
        bool loading, closeAfter;
        CancellationTokenSource cancellation;
        AssemblyPlan previewPlan;
        readonly string previewFolder;

        public AssemblyWindow() {
            templatePath = Path.Combine(Store.Root, "assembly-template.xml"); historyFolder = Path.Combine(Store.Root, "assembly-history");
            previewFolder = Path.Combine(Path.GetTempPath(), "videobatch-preview-" + Guid.NewGuid().ToString("N"));
            string loadError = null;
            try { template = File.Exists(templatePath) ? AssemblyFiles.Load<AssemblyTemplate>(templatePath) : AssemblyTemplate.Defaults(); template.Validate(); }
            catch (Exception e) { template = AssemblyTemplate.Defaults(); loadError = "Не удалось прочитать сохранённый шаблон: " + e.Message; }
            if (string.IsNullOrWhiteSpace(template.Materials)) template.Materials = Path.Combine(AppPaths.ExeDirectory, "Материалы-сборки");
            if (string.IsNullOrWhiteSpace(template.Output)) template.Output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VideoBatch", "Assembled");
            Text = "Сборка роликов — шаблон и материалы"; ClientSize = new Size(1250, 870); MinimumSize = new Size(1040, 730);
            StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 9); BackColor = Color.FromArgb(240, 243, 248);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112)); Controls.Add(root);
            left = new Panel { Dock = DockStyle.Fill }; root.Controls.Add(left, 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill }; left.Controls.Add(tabs);
            BuildMaterials(tabs); BuildEditor(tabs); BuildOptions(tabs);
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(12, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); root.Controls.Add(right, 1, 0);
            right.Controls.Add(new Label { Text = "Выбери сцену и изображение. Перетаскивай рамку; нижний правый угол меняет её размер.", Dock = DockStyle.Fill }, 0, 0);
            canvas = new AssemblyCanvas { Dock = DockStyle.Fill }; right.Controls.Add(canvas, 0, 1);
            canvas.LayerSelected += i => { layerList.SelectedIndex = i; };
            canvas.PlacementChanged += () => { LoadLayer(); RefreshCanvas(); };
            total = new Label { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) }; right.Controls.Add(total, 0, 2);
            status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = loadError ?? "1. Выбери папку материалов. 2. Заполни папки. 3. Настрой расположение и собери пример." }; root.Controls.Add(status, 0, 1); root.SetColumnSpan(status, 2);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 }; bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 8)); bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            progress = new ProgressBar { Dock = DockStyle.Fill, Maximum = 1000 }; bottom.Controls.Add(progress, 0, 0);
            footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(0, 10, 0, 0) }; bottom.Controls.Add(footer, 0, 1);
            footer.Controls.Add(Ui.Button("Сохранить шаблон", () => Safe(Save)));
            footer.Controls.Add(Ui.Button("Загрузить шаблон", LoadTemplate));
            footer.Controls.Add(Ui.Button("Экспорт шаблона", () => Safe(() => { Save(); using (var d = new SaveFileDialog { Filter = "Шаблон сборки|*.xml", DefaultExt = "xml", FileName = "VideoBatch-template.xml" }) if (d.ShowDialog(this) == DialogResult.OK) AssemblyFiles.Save(d.FileName, template); })));
            footer.Controls.Add(Ui.Button("Показать кадр", async () => await ShowSample()));
            footer.Controls.Add(Ui.Button("Собрать пример", async () => await Run(true)));
            footer.Controls.Add(Ui.Button("Собрать ролики", async () => await Run(false), true));
            footer.Controls.Add(Ui.Button("Результаты", () => Safe(() => { ReadTop(); Directory.CreateDirectory(template.Output); Ui.Open(template.Output); })));
            stop = Ui.Button("Стоп", () => cancellation?.Cancel()); stop.Visible = false; footer.Controls.Add(stop);
            root.Controls.Add(bottom, 0, 2); root.SetColumnSpan(bottom, 2);
            FormClosing += (s, e) => { if (cancellation != null) { e.Cancel = true; closeAfter = true; cancellation.Cancel(); } else { Safe(Save); } };
            LoadTop(); RefreshScenes(0); RefreshSources();
        }
        static TabPage Page(TabControl tabs, string name) { var p = new TabPage(name) { Padding = new Padding(10) }; tabs.TabPages.Add(p); return p; }
        static FlowLayoutPanel Flow(Control parent) { var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false }; parent.Controls.Add(f); return f; }
        static void Hint(FlowLayoutPanel parent, string text) { parent.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(0, 8, 0, 12) }); }
        static TextBox TextField(FlowLayoutPanel panel, string label, Action browse = null) {
            panel.Controls.Add(new Label { Text = label, AutoSize = true }); var row = new FlowLayoutPanel { Width = 650, Height = 36, WrapContents = false };
            var box = new TextBox { Width = browse == null ? 610 : 530, Margin = new Padding(0, 4, 8, 0) }; row.Controls.Add(box);
            if (browse != null) row.Controls.Add(Ui.Button("Выбрать", browse)); panel.Controls.Add(row); return box;
        }
        static NumericUpDown Number(FlowLayoutPanel panel, string label, decimal min, decimal max, int decimals = 1) {
            var row = new FlowLayoutPanel { Width = 650, Height = 34, WrapContents = false };
            row.Controls.Add(new Label { Text = label, Width = 325, AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft });
            var n = new NumericUpDown { Width = 105, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .1m };
            row.Controls.Add(n); panel.Controls.Add(row); return n;
        }
        void BuildMaterials(TabControl tabs) {
            var f = Flow(Page(tabs, "1. Материалы"));
            Hint(f, "Заголовки — готовые изображения. PNG сохраняет прозрачный фон. Скриншот бота фиксирован. По умолчанию папка материалов находится рядом с приложением; можно выбрать другую.");
            materials = TextField(f, "Общая папка материалов", () => { var path = Ui.Folder(this, materials.Text); if (path != null) { materials.Text = path; ReadTop(); previewPlan = null; RefreshSources(); RefreshCanvas(); } });
            var buttons = new FlowLayoutPanel { Width = 660, Height = 42 }; buttons.Controls.Add(Ui.Button("Создать папки", CreateFolders)); buttons.Controls.Add(Ui.Button("Обновить список", () => Safe(() => { ReadTop(); RefreshSources(); }))); f.Controls.Add(buttons);
            sources = new ListView { Width = 650, Height = 295, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
            sources.Columns.Add("Материал", 200); sources.Columns.Add("Папка / файл", 300); sources.Columns.Add("Файлов", 85); f.Controls.Add(sources);
            var actions = new FlowLayoutPanel { Width = 660, Height = 44 };
            actions.Controls.Add(Ui.Button("Добавить файлы", AddFiles)); actions.Controls.Add(Ui.Button("Открыть папку", OpenSource)); f.Controls.Add(actions);
            Hint(f, "Выбери строку и добавь файлы или перетащи их прямо на строку. Оригиналы сохраняются, в папку материалов копируются файлы. Источник каждой сцены можно изменить на вкладке 2.");
            sources.AllowDrop = true; sources.DragEnter += (s, e) => e.Effect = cancellation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            sources.DragDrop += (s, e) => Safe(() => { var pt = sources.PointToClient(new Point(e.X, e.Y)); var hit = sources.GetItemAt(pt.X, pt.Y); if (hit != null) { hit.Selected = true; CopyFiles((string[])e.Data.GetData(DataFormats.FileDrop)); } });
            musicPath = TextField(f, "Музыка — один файл на весь ролик (можно оставить пустым)", () => { using (var d = new OpenFileDialog { Filter = "Аудио|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac|Все файлы|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) musicPath.Text = d.FileName; });
            output = TextField(f, "Папка готовых роликов", () => { var path = Ui.Folder(this, output.Text); if (path != null) output.Text = path; });
        }
        void BuildEditor(TabControl tabs) {
            var p = Page(tabs, "2. Шаблон и расположение");
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 }; table.RowStyles.Add(new RowStyle(SizeType.Absolute, 94)); table.RowStyles.Add(new RowStyle(SizeType.Absolute, 80)); table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); p.Controls.Add(table);
            var scenes = new FlowLayoutPanel { Dock = DockStyle.Fill }; sceneList = new ListBox { Width = 280, Height = 80 }; sceneList.SelectedIndexChanged += (s, e) => { if (!loading) LoadScene(); }; scenes.Controls.Add(sceneList);
            var sceneButtons = new FlowLayoutPanel { Width = 340, Height = 86, WrapContents = true };
            sceneButtons.Controls.Add(Ui.Button("+ Перед", () => AddScene(false)));
            sceneButtons.Controls.Add(Ui.Button("+ После", () => AddScene(true)));
            sceneButtons.Controls.Add(Ui.Button("− Сцена", () => Safe(() => { if (template.Scenes.Count <= 2) throw new Exception("Оставь минимум две сцены."); int i = sceneList.SelectedIndex; template.Scenes.RemoveAt(i); previewPlan = null; RefreshScenes(Math.Min(i, template.Scenes.Count - 1)); })));
            sceneButtons.Controls.Add(Ui.Button("↑", () => MoveScene(-1))); sceneButtons.Controls.Add(Ui.Button("↓", () => MoveScene(1))); scenes.Controls.Add(sceneButtons); table.Controls.Add(scenes, 0, 0);
            var layers = new FlowLayoutPanel { Dock = DockStyle.Fill }; layerList = new ListBox { Width = 280, Height = 65 }; layerList.SelectedIndexChanged += (s, e) => { if (!loading) { LoadLayer(); RefreshCanvas(); } }; layers.Controls.Add(layerList);
            layers.Controls.Add(Ui.Button("+ Изображение", () => Safe(() => { SelectedScene.Layers.Add(new AssemblyLayer()); previewPlan = null; RefreshLayers(SelectedScene.Layers.Count - 1); })));
            layers.Controls.Add(Ui.Button("− Изображение", () => Safe(() => { if (SelectedLayer == null) return; int i = layerList.SelectedIndex; SelectedScene.Layers.RemoveAt(i); previewPlan = null; RefreshLayers(Math.Min(i, SelectedScene.Layers.Count - 1)); })));
            table.Controls.Add(layers, 0, 1);
            var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false }; table.Controls.Add(f, 0, 2);
            sceneName = TextField(f, "Название сцены"); duration = Number(f, "Длительность сцены, секунды", .5m, 60m);
            videoSource = TextField(f, "Видео этой сцены — папка; пусто = общая «Видео»", () => { var path = Ui.Folder(this, template.Materials); if (path != null) videoSource.Text = path; });
            layerName = TextField(f, "Название изображения"); layerSource = TextField(f, "Источник изображения — папка или конкретный файл", () => { var path = Ui.Folder(this, template.Materials); if (path != null) layerSource.Text = path; });
            f.Controls.Add(Ui.Button("Выбрать один файл изображения", () => { using (var d = new OpenFileDialog { Filter = "Изображения / текст|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.txt" }) if (d.ShowDialog(this) == DialogResult.OK) layerSource.Text = d.FileName; }));
            enabled = new CheckBox { Text = "Показывать это изображение", AutoSize = true }; unique = new CheckBox { Text = "Не повторять в следующих роликах (история сохраняется)", AutoSize = true }; fixedAsset = new CheckBox { Text = "Постоянное изображение: первый файл по имени", AutoSize = true };
            f.Controls.Add(enabled); f.Controls.Add(unique); f.Controls.Add(fixedAsset);
            x = Number(f, "Слева, % кадра", 0, 100); y = Number(f, "Сверху, % кадра", 0, 100); width = Number(f, "Ширина области, % кадра", 2, 100); height = Number(f, "Высота области, % кадра", 2, 100); opacity = Number(f, "Непрозрачность, %", 0, 100);
            start = Number(f, "Появление: секунда внутри сцены", 0, 60); end = Number(f, "Исчезновение: 0 = до конца сцены", 0, 60);
            Hint(f, "Изображение вписывается в рамку без растягивания и обрезки. Порядок в списке: нижние изображения накладываются поверх верхних.");
            fontName = TextField(f, "Для TXT: шрифт (для готовых картинок не используется)"); fontSize = Number(f, "Для TXT: максимальный размер шрифта, px", 12, 240, 0); radius = Number(f, "Для TXT: скругление чёрной плашки, px", 0, 200, 0); padding = Number(f, "Для TXT: внутренний отступ, px", 0, 150, 0);
            foreach (var box in new[] { sceneName, videoSource, layerName, layerSource, fontName }) box.TextChanged += (s, e) => EditorChanged();
            foreach (var n in new[] { duration, x, y, width, height, opacity, start, end, fontSize, radius, padding }) n.ValueChanged += (s, e) => EditorChanged();
            foreach (var c in new[] { enabled, unique, fixedAsset }) c.CheckedChanged += (s, e) => EditorChanged();
        }
        void BuildOptions(TabControl tabs) {
            var f = Flow(Page(tabs, "3. Сборка"));
            count = Number(f, "Сколько роликов собрать", 1, 1000, 0);
            f.Controls.Add(new Label { Text = "Формат кадра", AutoSize = true }); resolution = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            resolution.Items.AddRange(new object[] { "1080 × 1920 · вертикальный", "720 × 1280 · вертикальный", "1920 × 1080 · горизонтальный", "1280 × 720 · горизонтальный" }); f.Controls.Add(resolution);
            f.Controls.Add(new Label { Text = "Переход между сценами", AutoSize = true }); transition = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList }; transition.Items.AddRange(AssemblyTemplate.Transitions); f.Controls.Add(transition);
            transitionTime = Number(f, "Длительность перехода, секунды", 0, 2, 2); volume = Number(f, "Громкость музыки, %", 0, 200, 0);
            randomStart = new CheckBox { Text = "Выбирать случайный участок внутри длинного видео", AutoSize = true }; f.Controls.Add(randomStart);
            Hint(f, "Внутри одного ролика видео не повторяются. Между роликами видео используются снова, но полностью одинаковые сборки исключаются. Заголовки с отметкой «Не повторять» расходуются один раз, даже при следующем запуске программы.");
            Hint(f, "Переходы входят в итоговую длительность. Для них нужны дополнительные кадры исходника: например, для сцены 5 с с переходом 0,35 с нужен отрывок не короче 5,35 с. Последней сцене дополнительные кадры не нужны.");
            foreach (var n in new[] { count, transitionTime, volume }) n.ValueChanged += (s, e) => { if (!loading) { ReadTop(); RefreshCanvas(); } };
            transition.SelectedIndexChanged += (s, e) => { if (!loading) { ReadTop(); previewPlan = null; RefreshCanvas(); } };
            resolution.SelectedIndexChanged += (s, e) => { if (!loading) { ReadTop(); RefreshCanvas(); } };
            randomStart.CheckedChanged += (s, e) => { if (!loading) ReadTop(); };
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
        void RefreshScenes(int selected) { loading = true; sceneList.Items.Clear(); foreach (var s in template.Scenes) sceneList.Items.Add(s); sceneList.SelectedIndex = selected; loading = false; LoadScene(); }
        void LoadScene() {
            if (SelectedScene == null) return; canvas?.SetBackground(null); loading = true; sceneName.Text = SelectedScene.Name; duration.Value = (decimal)SelectedScene.Duration; videoSource.Text = SelectedScene.Videos; loading = false; RefreshLayers(0); RefreshSources();
        }
        void RefreshLayers(int selected) { loading = true; layerList.Items.Clear(); if (SelectedScene != null) foreach (var l in SelectedScene.Layers) layerList.Items.Add(l.Name); layerList.SelectedIndex = layerList.Items.Count == 0 ? -1 : Math.Max(0, selected); loading = false; LoadLayer(); RefreshCanvas(); RefreshSources(); }
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
            SelectedScene.Name = sceneName.Text; SelectedScene.Duration = (double)duration.Value; SelectedScene.Videos = videoSource.Text.Trim();
            var l = SelectedLayer;
            if (l != null) {
                l.Name = layerName.Text; l.Source = layerSource.Text.Trim(); l.Enabled = enabled.Checked;
                if (fixedAsset.Checked && unique.Checked) { loading = true; if (l.Fixed) fixedAsset.Checked = false; else unique.Checked = false; loading = false; }
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
            canvas.SetScene(template, sp, SelectedLayer); total.Text = "Итого: " + template.Duration.ToString("0.##") + " с · " + template.Width + " × " + template.Height + "\nПрозрачность и пропорции картинок сохраняются.";
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
                if (string.IsNullOrWhiteSpace(template.Materials)) throw new Exception("Выбери папку материалов на вкладке 1.");
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
            left.Enabled = !busy; canvas.Enabled = !busy; foreach (Control c in footer.Controls) c.Enabled = !busy; stop.Visible = busy; stop.Enabled = busy; if (busy) progress.Value = 0;
        }
        async Task<AssemblyPlan> Sample(AssemblyTemplate t, string probe, CancellationToken ct) {
            var videos = await AssemblyEngine.ReadVideos(t, probe, ct); string path = Path.Combine(historyFolder, t.Id + ".xml");
            var h = File.Exists(path) ? AssemblyFiles.Load<AssemblyHistory>(path) : new AssemblyHistory(); var batch = AssemblyPlanner.Create(t, videos, h, new Random(), 1);
            if (batch.Plans.Count == 0) throw new Exception(batch.Limit); return batch.Plans[0];
        }
        async Task ShowSample() {
            if (cancellation != null) return;
            try {
                Save(); if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
                cancellation = new CancellationTokenSource(); Busy(true); var t = Store.Clone(template);
                if (previewPlan == null) previewPlan = await Sample(t, probe, cancellation.Token);
                var scene = previewPlan.Scenes[sceneList.SelectedIndex]; Directory.CreateDirectory(previewFolder); string path = Path.Combine(previewFolder, Guid.NewGuid().ToString("N") + ".jpg");
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-ss", Core.N(scene.VideoStart), "-i", scene.Video.Path, "-frames:v", "1", "-vf", "scale=" + t.Width + ":" + t.Height + ":force_original_aspect_ratio=increase,crop=" + t.Width + ":" + t.Height, path }, cancellation.Token, null, 60);
                using (var image = Image.FromFile(path)) canvas.SetBackground(new Bitmap(image)); File.Delete(path); RefreshCanvas(); status.Text = "Кадр: " + Path.GetFileName(scene.Video.Path) + ". Переходы и музыку проверяй кнопкой «Собрать пример».";
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
        void Safe(Action action) { try { action(); } catch (Exception e) { if (status != null) status.Text = e.Message; Ui.Error(this, e); } }
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
        public AssemblyCanvas() { DoubleBuffered = true; BackColor = Color.FromArgb(25, 29, 36); }
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
            for (int i = 0; i < images.Count; i++) if (images[i] != null) e.Graphics.DrawImage(images[i], f);
            foreach (var l in scene.Scene.Layers.Where(l => l.Enabled)) {
                var box = Box(l); using (var pen = new Pen(l == selected ? Color.LimeGreen : Color.FromArgb(120, Color.White), l == selected ? 2 : 1)) e.Graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                e.Graphics.DrawString(l.Name, Font, Brushes.LimeGreen, box.X + 4, box.Y + 3);
                if (l == selected) e.Graphics.FillRectangle(Brushes.LimeGreen, box.Right - 9, box.Bottom - 9, 9, 9);
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
}
