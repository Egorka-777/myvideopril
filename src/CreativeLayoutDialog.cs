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
    /// <summary>Edits a private preset; only the Save button commits it to the project.</summary>
    public sealed class CreativeLayoutDialog : Form {
        readonly CreativeProject project;
        readonly CreativePreset preset;
        readonly string ffmpeg, probe;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly AssemblyCanvas canvas;
        readonly AssemblyTemplate template;
        readonly AssemblyLayer selection;
        readonly ComboBox target, commentIndex;
        readonly NumericUpDown[] coordinates = new NumericUpDown[4];
        readonly Label status;
        readonly TextBox coverPath, bannerPath;
        readonly Control coordinateGrid, framingControls, blurControls, coverControls, bannerControls, transitionControls;
        readonly CheckBox blurEnabled;
        readonly NumericUpDown sourceZoom, sourcePanX, sourcePanY;
        readonly TrackBar zoomSlider;
        Size sourceSize;
        bool SourceSelected { get { return target != null && target.SelectedIndex == 5; } }
        Bitmap firstFrame, lastFrame, coverImage, bannerImage;
        readonly Bitmap[] cardImages = new Bitmap[3];
        bool syncing, closing;
        int imageVersion;

        public CreativeLayoutDialog(CreativeProject project, string ffmpeg, string probe, int initialTarget = 5, bool addBlur = false) {
            if (project == null) throw new ArgumentNullException(nameof(project));
            this.project = project; this.ffmpeg = ffmpeg; this.probe = probe;
            preset = project.Preset == null ? new CreativePreset() : Store.Clone(project.Preset);
            preset.EnsureCommentCards();
            if (addBlur) preset.IntroBlur = true;
            Text = "Кадрирование и оформление креатива";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = Theme.FontBody; BackColor = Theme.Background; ForeColor = Theme.TextPrimary;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false;
            var screen = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(Math.Min(1080, screen.Width - 60), Math.Min(760, screen.Height - 80));
            MinimumSize = new Size(Math.Min(720, screen.Width - 30), Math.Min(480, screen.Height - 40));

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            root.Controls.Add(new Label {
                Text = "Тяните рамку для перемещения, нижний правый угол — для размера. Изображения вписаны без растяжения.\nЭто схема размещения, не готовый ролик. Размытие и анимацию проверяйте точным предпросмотром в основном окне.",
                Dock = DockStyle.Fill, AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 0, 0, 10)
            }, 0, 0);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            root.Controls.Add(body, 0, 1);
            canvas = new AssemblyCanvas { Dock = DockStyle.Fill, Font = Theme.FontBody, Margin = new Padding(0, 0, 12, 0), AccessibleName = "Размещение элементов креатива" };
            body.Controls.Add(canvas, 0, 0);
            template = new AssemblyTemplate { Width = preset.Width, Height = preset.Height, Fps = preset.Fps, SplitLayout = "none" };
            // An empty text layer supplies the existing editor's rectangle, drag and resize behavior.
            // Static images are composited separately so their aspect fit does not alter the editable envelope.
            selection = new AssemblyLayer { Kind = "text", Text = "", Name = "Область", PreserveAspectRatio = false, AppearanceTransition = "cut", ExitTransition = "cut" };
            canvas.PlacementChanged += () => {
                if (syncing || SourceSelected) return;
                var rect = CurrentRect;
                // All creative regions are normalized to the visible output frame.
                selection.Width = Limit(selection.Width, .02, 1);
                selection.Height = Limit(selection.Height, .02, 1);
                selection.X = Limit(selection.X, 0, 1 - selection.Width);
                selection.Y = Limit(selection.Y, 0, 1 - selection.Height);
                rect.X = selection.X; rect.Y = selection.Y; rect.Width = selection.Width; rect.Height = selection.Height;
                ReadCoordinates(); DrawComposition();
            };
            canvas.BaseVideoPlacementChanged += SourcePlacementChanged;
            canvas.MouseWheel += (s, e) => {
                if (!SourceSelected || firstFrame == null) return;
                preset.SourceZoom = Math.Max(.1, preset.SourceZoom + (e.Delta > 0 ? .1 : -.1));
                ReadFraming(); DrawComposition();
            };
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            body.Controls.Add(scroll, 1, 0);
            var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 8, 8) };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(settings);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            target = Choice(new[] { "Маска размытия", "Закрывающее изображение", "Видео в концовке", "Карточка комментария", "Баннер", "Кадрирование" });
            target.Visible = false;
            Add(settings, target);
            tools.Controls.Add(Theme.MakeButton("Кадрирование", ghost: true, action: () => target.SelectedIndex = 5));
            var addLayer = Theme.MakeButton("Добавить слой…", ghost: true);
            addLayer.Click += (s, e) => {
                var menu = Theme.MakeContextMenu();
                menu.Items.Add("Маска размытия", null, (a, b) => { blurEnabled.Checked = true; target.SelectedIndex = 0; });
                menu.Items.Add("Закрывающее изображение", null, (a, b) => target.SelectedIndex = 1);
                menu.Items.Add("Баннер", null, (a, b) => target.SelectedIndex = 4);
                menu.Show(addLayer, new Point(0, addLayer.Height));
            };
            tools.Controls.Add(addLayer);
            tools.Controls.Add(Theme.MakeButton("Концовка", ghost: true, action: () => target.SelectedIndex = 2));
            tools.Controls.Add(Theme.MakeButton("Комментарии", ghost: true, action: () => target.SelectedIndex = 3));
            Add(settings, tools);
            Add(settings, new Label { Text = "Слои · изменить существующий", AutoSize = true, ForeColor = Theme.TextSecondary });
            var layers = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            layers.Controls.Add(Theme.MakeButton("Маска размытия", ghost: true, action: () => target.SelectedIndex = 0));
            layers.Controls.Add(Theme.MakeButton("Плашка", ghost: true, action: () => target.SelectedIndex = 1));
            layers.Controls.Add(Theme.MakeButton("Баннер", ghost: true, action: () => target.SelectedIndex = 4));
            Add(settings, layers);
            commentIndex = Choice(new[] { "Комментарий 1 · сверху", "Комментарий 2 · посередине", "Комментарий 3 · снизу" });
            commentIndex.SelectedIndex = 0;
            Add(settings, commentIndex);
            commentIndex.SelectedIndexChanged += (s,e) => SelectTarget();
            target.SelectedIndexChanged += (s, e) => SelectTarget();
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            string[] names = { "X", "Y", "Ширина", "Высота" };
            for (int i = 0; i < coordinates.Length; i++) {
                int index = i;
                var field = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                field.Controls.Add(new Label { Text = names[i] + " (0–1)", AutoSize = true });
                coordinates[i] = Number(0, 1, 3, .01m, 0);
                coordinates[i].Width = 104;
                coordinates[i].ValueChanged += (s, e) => ChangeCoordinate(index);
                field.Controls.Add(coordinates[i]); grid.Controls.Add(field, i % 2, i / 2);
            }
            coordinateGrid = grid;
            Add(settings, grid);
            var framing = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1 };
            framing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Add(framing, Heading("Кадрирование исходника"));
            Add(framing, new Label { Text = "Тяните видео — сдвиг; угол рамки или колесо — масштаб. Точный сдвиг задаётся полями X / Y.", AutoSize = true, Dock = DockStyle.Top });
            Add(framing, new Label { Text = "Масштаб × (0,1 — 10)", AutoSize = true });
            sourceZoom = Number(.1m, 10, 2, .05m, preset.SourceZoom);
            Add(framing, sourceZoom);
            zoomSlider = new TrackBar { Minimum = 10, Maximum = Math.Max(1000, (int)Math.Round(preset.SourceZoom * 100)), TickFrequency = 100, SmallChange = 5, LargeChange = 25, Value = Math.Max(10, (int)Math.Round(preset.SourceZoom * 100)), Dock = DockStyle.Top, Height = 38 };
            Add(framing, zoomSlider);
            Add(framing, new Label { Text = "Сдвиг X (−1…1)", AutoSize = true });
            sourcePanX = Number(-1, 1, 3, .01m, preset.SourcePanX); Add(framing, sourcePanX);
            Add(framing, new Label { Text = "Сдвиг Y (−1…1)", AutoSize = true });
            sourcePanY = Number(-1, 1, 3, .01m, preset.SourcePanY); Add(framing, sourcePanY);
            sourceZoom.ValueChanged += (s, e) => ChangeFraming();
            sourcePanX.ValueChanged += (s, e) => ChangeFraming();
            sourcePanY.ValueChanged += (s, e) => ChangeFraming();
            zoomSlider.ValueChanged += (s, e) => { if (!syncing) sourceZoom.Value = zoomSlider.Value / 100m; };
            Add(framing, Theme.MakeButton("Вписать и центрировать", ghost: true, action: () => {
                preset.SourceZoom = 1; preset.SourcePanX = preset.SourcePanY = 0; ReadFraming(); DrawComposition();
            }));
            Add(framing, new Label { Text = "Кадрирование общее для вступления и оригинала. В концовке уменьшается весь кадр. Исходный файл не меняется.", AutoSize = true, Dock = DockStyle.Top, ForeColor = Theme.TextSecondary });
            framingControls = framing; Add(settings, framing);
            var blurPanel = Section(settings); blurControls = blurPanel;
            Add(blurPanel, Heading("Слой · маска размытия"));
            blurEnabled = new CheckBox { Text = "Эффект размытия · только вступление", AutoSize = true, Checked = preset.IntroBlur };
            blurEnabled.CheckedChanged += (s, e) => { preset.IntroBlur = blurEnabled.Checked; DrawComposition(); };
            Add(blurPanel, blurEnabled);
            AddNumber(blurPanel, "Сила размытия", 1, 100, 1, .5m, preset.BlurStrength, n => preset.BlurStrength = (double)n);
            Add(blurPanel, new Label { Text = "Переместите рамку на субтитры. Штриховка — границы маски; точный эффект виден в предпросмотре ролика.", AutoSize = true, Dock = DockStyle.Top, ForeColor = Theme.TextSecondary });
            var coverPanel = Section(settings); coverControls = coverPanel;
            coverPath = AddImagePicker(coverPanel, "Слой · закрывающее изображение", preset.IntroCover, true);
            var bannerPanel = Section(settings); bannerControls = bannerPanel;
            bannerPath = AddImagePicker(bannerPanel, "Слой · баннер", preset.Banner, false);
            Add(framing, Heading("Переход к оригиналу"));
            AddNumber(framing, "Mix, с", 0, 30, 2, .05m, preset.MixDuration, n => preset.MixDuration = (double)n);
            var transitions = Section(settings); transitionControls = transitions;
            Add(transitions, Heading("Анимация концовки и комментариев"));
            AddNumber(transitions, "Уменьшение / перемещение видео, с", 0, 30, 2, .05m, preset.MotionDuration, n => preset.MotionDuration = (double)n);
            Add(transitions, new Label { Text = "Появление карточки", AutoSize = true });
            var appearance = Choice(new[] { "Плавно (fade)", "Сразу (cut)" });
            appearance.SelectedIndex = preset.CardAppearance == "cut" ? 1 : 0;
            appearance.SelectedIndexChanged += (s, e) => preset.CardAppearance = appearance.SelectedIndex == 1 ? "cut" : "fade";
            Add(transitions, appearance);
            AddNumber(transitions, "Появление карточки, с", 0, 30, 2, .05m, preset.CardAppearanceDuration, n => preset.CardAppearanceDuration = (double)n);
            AddNumber(transitions, "Исчезновение карточки, с", 0, 30, 2, .05m, preset.CardExitDuration, n => preset.CardExitDuration = (double)n);
            Add(transitions, new Label { Text = "Комментарии появляются сверху вниз и остаются стопкой. Выберите номер, чтобы настроить его рамку. Пропорции сохраняются.", AutoSize = true, Dock = DockStyle.Top, ForeColor = Theme.TextSecondary });
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 8, 0, 0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { Text = "Загрузка исходных кадров…", Dock = DockStyle.Fill, AutoSize = true, ForeColor = Theme.TextSecondary };
            footer.Controls.Add(status, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var save = Theme.MakeButton("Применить", accent: true, action: Commit);
            var cancel = Theme.MakeButton("Отмена", ghost: true, action: () => { DialogResult = DialogResult.Cancel; Close(); });
            actions.Controls.Add(save); actions.Controls.Add(cancel); footer.Controls.Add(actions, 1, 0);
            root.Controls.Add(footer, 0, 2); AcceptButton = save; CancelButton = cancel;
            target.SelectedIndex = initialTarget;
            Shown += async (s, e) => { await Task.WhenAll(LoadFramesAsync(), LoadImagesAsync()); };
        }

        CreativeRect CurrentRect {
            get {
                switch (target.SelectedIndex) {
                    case 1: return preset.IntroCoverBounds;
                    case 2: return preset.FinalVideo;
                    case 3: return preset.CommentCards[commentIndex.SelectedIndex];
                    case 4: return preset.BannerBounds;
                    default: return preset.IntroMask;
                }
            }
        }
        static TableLayoutPanel Section(TableLayoutPanel parent) {
            var section = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Add(parent, section); return section;
        }
        static double Limit(double value, double min, double max) { return Math.Max(min, Math.Min(max, value)); }
        static void Add(TableLayoutPanel panel, Control control) { control.Margin = new Padding(0, 3, 0, 5); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.Controls.Add(control, 0, panel.RowCount++); }
        static Label Heading(string text) { return new Label { Text = text, AutoSize = true, Font = Theme.FontCardTitle, Dock = DockStyle.Top }; }
        static ComboBox Choice(string[] choices) {
            var box = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary, IntegralHeight = false, DropDownHeight = 180 };
            box.Items.AddRange(choices); return box;
        }
        static NumericUpDown Number(decimal min, decimal max, int digits, decimal step, double value) {
            return new NumericUpDown { Minimum = min, Maximum = Math.Max(max, (decimal)value), DecimalPlaces = digits, Increment = step, Value = Math.Max(min, (decimal)value), BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary, Width = 120 };
        }
        static void AddNumber(TableLayoutPanel panel, string text, decimal min, decimal max, int digits, decimal step, double value, Action<decimal> changed) {
            Add(panel, new Label { Text = text, AutoSize = true });
            var input = Number(min, max, digits, step, value);
            input.ValueChanged += (s, e) => changed(input.Value); Add(panel, input);
        }
        TextBox AddImagePicker(TableLayoutPanel panel, string title, string path, bool cover) {
            Add(panel, new Label { Text = title, AutoSize = true });
            var text = new TextBox { Text = path ?? "", ReadOnly = true, Dock = DockStyle.Top, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary };
            Add(panel, text);
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            row.Controls.Add(Theme.MakeButton("Выбрать…", ghost: true, action: () => {
                using (var dialog = new OpenFileDialog { Title = title, Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|Все файлы|*.*" }) {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    SetImagePathAsync(text, dialog.FileName, cover);
                }
            }));
            row.Controls.Add(Theme.MakeButton("Убрать", ghost: true, action: () => SetImagePathAsync(text, "", cover)));
            Add(panel, row); return text;
        }
        async void SetImagePathAsync(TextBox text, string path, bool cover) {
            text.Text = path;
            if (cover) preset.IntroCover = path; else preset.Banner = path;
            target.SelectedIndex = cover ? 1 : 4;
            await LoadImagesAsync();
        }
        void SelectTarget() {
            if (target.SelectedIndex < 0) return;
            commentIndex.Visible = target.SelectedIndex == 3;
            coordinateGrid.Visible = !SourceSelected;
            framingControls.Visible = SourceSelected;
            blurControls.Visible = target.SelectedIndex == 0;
            coverControls.Visible = target.SelectedIndex == 1;
            bannerControls.Visible = target.SelectedIndex == 4;
            transitionControls.Visible = target.SelectedIndex == 2 || target.SelectedIndex == 3;
            template.BaseVideoX = template.BaseVideoY = 0;
            template.BaseVideoWidth = template.BaseVideoHeight = 1;
            if (SourceSelected) {
                canvas.SetScene(template, new AssemblyScenePlan { Scene = new AssemblyScene { Duration = 3600 } }, null);
                canvas.SelectBaseVideo();
                ReadFraming(); DrawComposition(); return;
            }
            var rect = CurrentRect;
            selection.X = rect.X; selection.Y = rect.Y; selection.Width = rect.Width; selection.Height = rect.Height;
            var scene = new AssemblyScene { Duration = 3600 };
            scene.Layers.Add(selection);
            canvas.SetScene(template, new AssemblyScenePlan { Scene = scene, Layers = new List<AssemblyLayerPlan> { new AssemblyLayerPlan { Layer = selection, Asset = new AssemblyAsset { Path = "" } } } }, selection);
            ReadCoordinates(); DrawComposition();
        }
        void ReadFraming() {
            syncing = true;
            try {
                sourceZoom.Maximum = Math.Max(sourceZoom.Maximum, (decimal)preset.SourceZoom);
                sourceZoom.Value = (decimal)preset.SourceZoom;
                sourcePanX.Value = (decimal)preset.SourcePanX; sourcePanY.Value = (decimal)preset.SourcePanY;
                zoomSlider.Maximum = Math.Max(zoomSlider.Maximum, (int)Math.Round(preset.SourceZoom * 100));
                zoomSlider.Value = Math.Max(10, (int)Math.Round(preset.SourceZoom * 100));
            } finally { syncing = false; }
        }
        void ChangeFraming() {
            if (syncing) return;
            preset.SourceZoom = (double)sourceZoom.Value;
            preset.SourcePanX = (double)sourcePanX.Value; preset.SourcePanY = (double)sourcePanY.Value;
            ReadFraming(); DrawComposition();
        }
        void SourcePlacementChanged() {
            if (syncing || !SourceSelected || sourceSize.IsEmpty) return;
            double fit = Math.Min((double)preset.Width / sourceSize.Width, (double)preset.Height / sourceSize.Height);
            preset.SourceZoom = Math.Max(.1, template.BaseVideoWidth * preset.Width / (sourceSize.Width * fit));
            // Recompute rounded dimensions before inverting centre-based pan.
            var bounds = CreativeGeometry.SourceBounds(preset, sourceSize);
            preset.SourcePanX = Limit(2 * (template.BaseVideoX * preset.Width - (preset.Width - bounds.Width) / 2.0) / preset.Width, -1, 1);
            preset.SourcePanY = Limit(2 * (template.BaseVideoY * preset.Height - (preset.Height - bounds.Height) / 2.0) / preset.Height, -1, 1);
            ReadFraming(); DrawComposition();
        }
        void ReadCoordinates() {
            syncing = true;
            try {
                var rect = CurrentRect;
                double[] values = { rect.X, rect.Y, rect.Width, rect.Height };
                for (int i = 0; i < values.Length; i++) coordinates[i].Value = (decimal)Limit(values[i], 0, 1);
            } finally { syncing = false; }
        }
        void ChangeCoordinate(int index) {
            if (syncing || target.SelectedIndex < 0) return;
            var rect = CurrentRect;
            double value = (double)coordinates[index].Value;
            if (index == 0) rect.X = Limit(value, 0, 1 - rect.Width);
            else if (index == 1) rect.Y = Limit(value, 0, 1 - rect.Height);
            else if (index == 2) rect.Width = Limit(value, .02, 1 - rect.X);
            else rect.Height = Limit(value, .02, 1 - rect.Y);
            selection.X = rect.X; selection.Y = rect.Y; selection.Width = rect.Width; selection.Height = rect.Height;
            ReadCoordinates(); DrawComposition();
        }
        static RectangleF RegionBounds(CreativeRect rect, Size size) { return new RectangleF((float)(rect.X * size.Width), (float)(rect.Y * size.Height), (float)(rect.Width * size.Width), (float)(rect.Height * size.Height)); }
        static void DrawFit(Graphics graphics, Image image, RectangleF bounds) {
            if (image != null && bounds.Width > 0 && bounds.Height > 0) graphics.DrawImage(image, AssemblyRaster.FitImageBounds(bounds, image.Size));
        }
        void DrawFramedSource(Graphics graphics, Image image, RectangleF destination) {
            if (image == null || sourceSize.IsEmpty) return;
            var bounds = CreativeGeometry.SourceBounds(preset, sourceSize);
            var state = graphics.Save();
            try {
                graphics.SetClip(destination, CombineMode.Intersect);
                graphics.DrawImage(image, new RectangleF(destination.X + bounds.X * destination.Width / preset.Width,
                    destination.Y + bounds.Y * destination.Height / preset.Height,
                    bounds.Width * destination.Width / preset.Width, bounds.Height * destination.Height / preset.Height));
            } finally { graphics.Restore(state); }
        }
        void DrawComposition() {
            if (closing || target.SelectedIndex < 0) return;
            if (SourceSelected) {
                if (firstFrame != null && !sourceSize.IsEmpty) {
                    var bounds = CreativeGeometry.SourceBounds(preset, sourceSize);
                    template.BaseVideoX = bounds.X / preset.Width; template.BaseVideoY = bounds.Y / preset.Height;
                    template.BaseVideoWidth = bounds.Width / preset.Width; template.BaseVideoHeight = bounds.Height / preset.Height;
                    template.BaseVideoPreserveAspectRatio = true;
                    canvas.SetBackground(new Bitmap(firstFrame));
                }
                canvas.Invalidate(); return;
            }
            template.BaseVideoX = template.BaseVideoY = 0;
            template.BaseVideoWidth = template.BaseVideoHeight = 1;
            double scale = Math.Min(1, 720.0 / Math.Max(preset.Width, preset.Height));
            var size = new Size(Math.Max(1, (int)(preset.Width * scale)), Math.Max(1, (int)(preset.Height * scale)));
            var composite = new Bitmap(size.Width, size.Height);
            try {
                using (var graphics = Graphics.FromImage(composite)) {
                    graphics.Clear(Color.Black); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    bool intro = target.SelectedIndex < 2;
                    var output = new RectangleF(PointF.Empty, size);
                    var finalBox = AssemblyRaster.FitImageBounds(RegionBounds(preset.FinalVideo, size), new Size(preset.Width, preset.Height));
                    DrawFramedSource(graphics, intro ? firstFrame : lastFrame, intro ? output : finalBox);
                    if (intro) {
                        if (preset.IntroBlur) {
                            var mask = RegionBounds(preset.IntroMask, size);
                            using (var hatch = new HatchBrush(HatchStyle.ForwardDiagonal, Color.FromArgb(180, Theme.Warning), Color.FromArgb(35, Theme.Warning))) graphics.FillRectangle(hatch, mask);
                        }
                        DrawFit(graphics, coverImage, RegionBounds(preset.IntroCoverBounds, size));
                    } else for(int i=0;i<3;i++)DrawFit(graphics, cardImages[i], RegionBounds(preset.CommentBounds(i), size));
                    DrawFit(graphics, bannerImage, RegionBounds(preset.BannerBounds, size));
                }
                canvas.SetBackground(composite); composite = null;
            } finally { composite?.Dispose(); }
        }

        async Task LoadFramesAsync() {
            var ct = lifetime.Token;
            string folder = Path.Combine(Path.GetTempPath(), "creative-layout-" + Guid.NewGuid().ToString("N"));
            Bitmap first = null, last = null;
            try {
                if (string.IsNullOrWhiteSpace(project.Source)) throw new Exception("Выберите исходное видео в основном окне.");
                var media = Core.VideoInfo(await Core.Probe(probe, project.Source, ct));
                preset.ApplySourceSize(media);
                template.Width = preset.Width; template.Height = preset.Height;
                SelectTarget();
                Directory.CreateDirectory(folder);
                double factor = Math.Min(1, 960.0 / Math.Max(media.Width, media.Height));
                int w = Math.Max(2, (int)(media.Width * factor) / 2 * 2), h = Math.Max(2, (int)(media.Height * factor) / 2 * 2);
                string start = Path.Combine(folder, "first.png"), end = Path.Combine(folder, "last.png");
                await ExtractFrameAsync(start, 0, media.VideoIndex, w, h, ct);
                // Decode the final second and retain its last frame, rather than assuming a constant frame rate.
                double endStart = Math.Max(0, media.VideoDuration - 1);
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-ss", Core.N(endStart), "-i", project.Source, "-map", "0:" + media.VideoIndex, "-an", "-sn", "-vf", "scale=" + w + ":" + h + ",setsar=1", "-fps_mode", "passthrough", "-update", "1", "-threads", "1", end }, ct, null, 60);
                first = await Task.Run(() => ReadBitmap(start), ct);
                last = await Task.Run(() => ReadBitmap(end), ct);
                if (closing || ct.IsCancellationRequested) return;
                sourceSize = new Size(media.Width, media.Height);
                firstFrame = first; first = null; lastFrame = last; last = null;
                if (status.ForeColor != Theme.Warning) status.Text = "Первый / последний кадр исходника · " + media.Width + "×" + media.Height;
                DrawComposition();
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closing) { status.Text = "Кадры не загружены: " + ex.Message; status.ForeColor = Theme.Warning; } }
            finally {
                first?.Dispose(); last?.Dispose();
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (Exception ex) { if (!closing) { status.Text = "Не удалось удалить временные кадры: " + ex.Message; status.ForeColor = Theme.Warning; } }
            }
        }
        Task<string> ExtractFrameAsync(string path, double time, int stream, int w, int h, CancellationToken ct) {
            return Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-ss", Core.N(time), "-i", project.Source, "-map", "0:" + stream, "-frames:v", "1", "-vf", "scale=" + w + ":" + h + ",setsar=1", "-threads", "1", path }, ct, null, 60);
        }
        static Bitmap ReadBitmap(string path) {
            using (var image = AssemblyOverlayMedia.LoadFrame(path, 0, false)) {
                double scale = Math.Min(1, 1200.0 / Math.Max(image.Width, image.Height));
                var result = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
                try {
                    using (var graphics = Graphics.FromImage(result)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(image, new Rectangle(Point.Empty, result.Size)); }
                    return result;
                } catch { result.Dispose(); throw; }
            }
        }
        async Task<Bitmap> LoadAssetAsync(string path, CancellationToken ct) {
            // Animated assets use the supplied tool and cancellable API, not the canvas decoder.
            if (!AssemblyOverlayMedia.IsAnimated(path)) return await Task.Run(() => ReadBitmap(path), ct);
            string temp = Path.Combine(Path.GetTempPath(), "creative-layout-asset-" + Guid.NewGuid().ToString("N") + ".png");
            try {
                await Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-i", path, "-frames:v", "1", "-vf", "scale=1200:1200:force_original_aspect_ratio=decrease", "-threads", "1", temp }, ct, null, 60);
                return await Task.Run(() => ReadBitmap(temp), ct);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        async Task LoadImagesAsync() {
            int version = ++imageVersion;
            var ct = lifetime.Token;
            string[] paths = new[] { preset.IntroCover, preset.Banner }.Concat(Enumerable.Range(0,3).Select(i => project.Comments!=null && i<project.Comments.Count ? project.Comments[i].ImagePath : "")).ToArray();
            var images = new Bitmap[5];
            try {
                for (int i = 0; i < paths.Length; i++) {
                    ct.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(paths[i])) images[i] = await LoadAssetAsync(paths[i], ct);
                }
                if (closing || ct.IsCancellationRequested || version != imageVersion) return;
                coverImage?.Dispose(); bannerImage?.Dispose();
                coverImage = images[0]; bannerImage = images[1];
                for(int i=0;i<3;i++){cardImages[i]?.Dispose();cardImages[i]=images[i+2];}
                Array.Clear(images, 0, images.Length);
                DrawComposition();
            } catch (OperationCanceledException) { }
            catch (Exception ex) {
                if (!closing && version == imageVersion) {
                    // Do not leave an old asset on screen under the newly selected path.
                    coverImage?.Dispose(); bannerImage?.Dispose();
                    coverImage = bannerImage = null;
                    for(int i=0;i<3;i++){cardImages[i]?.Dispose();cardImages[i]=null;}
                    DrawComposition(); status.Text = "Изображение не загружено: " + ex.Message; status.ForeColor = Theme.Warning;
                }
            } finally { foreach (var image in images) image?.Dispose(); }
        }
        void Commit() {
            // Preserves unrelated settings (speech, language, insertions) from the private deep copy.
            project.Preset = preset;
            DialogResult = DialogResult.OK; Close();
        }
        protected override void OnFormClosing(FormClosingEventArgs e) {
            base.OnFormClosing(e);
            if (!e.Cancel) { closing = true; lifetime.Cancel(); }
        }
        protected override void Dispose(bool disposing) {
            if (disposing && !closing) { closing = true; lifetime.Cancel(); }
            if (disposing) {
                firstFrame?.Dispose(); firstFrame = null; lastFrame?.Dispose(); lastFrame = null;
                coverImage?.Dispose(); coverImage = null; bannerImage?.Dispose(); bannerImage = null;
                for(int i=0;i<3;i++){cardImages[i]?.Dispose();cardImages[i]=null;}
                lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
