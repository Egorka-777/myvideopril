using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class SubtitleStyleDialog : Form {
        readonly SubtitleStylePreview preview;
        readonly FlowLayoutPanel cards;
        readonly List<WarmupCaption> cues;
        readonly ComboBox font, plate, highlight, motion;
        readonly NumericUpDown size, position, words, opacity, outline;
        readonly CheckBox enabled, uppercase, bold;
        readonly Button textColor, plateColor, accentColor, outlineColor;
        readonly Label selected;
        readonly Timer timer = new Timer { Interval = 50 };
        readonly DateTime started = DateTime.UtcNow;
        bool loading;
        public WarmupSubtitleStyle Style { get; private set; }
        public bool EditCaptions { get; private set; }

        public SubtitleStyleDialog(WarmupSubtitleStyle current, IEnumerable<WarmupCaption> captions = null, Image background = null, int canvasWidth = 1080, int canvasHeight = 1920, bool canEditCaptions = false) {
            Style = (current ?? new WarmupSubtitleStyle()).Copy(); Style.Normalize();
            cues = (captions ?? Enumerable.Empty<WarmupCaption>()).Where(c => c != null).ToList();
            if (cues.Count == 0) cues.Add(new WarmupCaption { Text = "Твой стиль — твои слова", Start = 0, End = 3,
                Words = new List<SubtitleWord> { new SubtitleWord { Text = "Твой", Start = 0, End = .6 }, new SubtitleWord { Text = "стиль", Start = .7, End = 1.3 }, new SubtitleWord { Text = "—", Start = 1.3, End = 1.4 }, new SubtitleWord { Text = "твои", Start = 1.5, End = 2 }, new SubtitleWord { Text = "слова", Start = 2.1, End = 2.9 } } });
            Text = "Субтитры"; Name = "SubtitleStyleDialog"; BackColor = Theme.Background; ForeColor = Theme.TextPrimary;
            Font = Theme.FontBody; StartPosition = FormStartPosition.CenterParent; Size = new Size(1050, 750); MinimumSize = new Size(850, 590); MaximizeBox = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 2 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); Controls.Add(root);
            preview = new SubtitleStylePreview(canvasWidth, canvasHeight, background) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 16, 8) }; root.Controls.Add(preview, 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill, Name = "SubtitleStyleTabs" };
            var gallery = new TabPage("Стили") { BackColor = Theme.Card, Padding = new Padding(8) };
            var settings = new TabPage("Настройки") { BackColor = Theme.Card, Padding = new Padding(12), AutoScroll = true };
            tabs.TabPages.Add(gallery); tabs.TabPages.Add(settings); root.Controls.Add(tabs, 1, 0);
            cards = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, BackColor = Theme.Card }; gallery.Controls.Add(cards);
            cards.ClientSizeChanged += (s, e) => { foreach (Control card in cards.Controls) card.Width = Math.Max(170, (cards.ClientSize.Width - 36) / 2); };
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 12, 0) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47)); settings.Controls.Add(fields);
            enabled = new CheckBox { Text = "Включены", AutoSize = true }; uppercase = new CheckBox { Text = "Заглавные буквы", AutoSize = true }; bold = new CheckBox { Text = "Жирный шрифт", AutoSize = true };
            font = Theme.MakeCombo(SubtitlePresets.Fonts); plate = Theme.MakeCombo(new[] { "Без плашки", "Скруглённая", "Прямая" });
            highlight = Theme.MakeCombo(new[] { "Без выделения", "Текущее слово", "Караоке" }); motion = Theme.MakeCombo(new[] { "Без анимации", "Лёгкое увеличение" });
            size = Number(28, 88); position = Number(10, 90); words = Number(1, 6); opacity = Number(0, 100); outline = Number(0, 10);
            textColor = ColorButton("TextColor", () => Style.TextColor, c => Style.TextColor = c);
            plateColor = ColorButton("PlateColor", () => Style.PlateColor, c => Style.PlateColor = c);
            accentColor = ColorButton("AccentColor", () => Style.AccentColor, c => Style.AccentColor = c);
            outlineColor = ColorButton("OutlineColor", () => Style.OutlineColor, c => Style.OutlineColor = c);
            Add(fields, "Субтитры", enabled); Add(fields, "Шрифт", font); Add(fields, "Начертание", bold); Add(fields, "Регистр", uppercase);
            Add(fields, "Размер", size); Add(fields, "Положение сверху, %", position); Add(fields, "Слов в группе", words);
            Add(fields, "Цвет текста", textColor); Add(fields, "Выделение слов", highlight); Add(fields, "Цвет выделения", accentColor);
            Add(fields, "Анимация", motion); Add(fields, "Обводка", outline); Add(fields, "Цвет обводки", outlineColor);
            Add(fields, "Плашка", plate); Add(fields, "Цвет плашки", plateColor); Add(fields, "Плотность плашки, %", opacity);
            foreach (var control in new ComboBox[] { font, plate, highlight, motion }) control.SelectedIndexChanged += (s, e) => Changed();
            foreach (var control in new NumericUpDown[] { size, position, words, opacity, outline }) control.ValueChanged += (s, e) => Changed();
            foreach (var control in new CheckBox[] { enabled, uppercase, bold }) control.CheckedChanged += (s, e) => Changed();
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            var apply = Theme.MakeButton("Применить", accent: true, action: () => { DialogResult = DialogResult.OK; Close(); });
            var cancel = Theme.MakeButton("Отмена", ghost: true, action: () => { DialogResult = DialogResult.Cancel; Close(); });
            actions.Controls.Add(apply); actions.Controls.Add(cancel); root.Controls.Add(actions, 1, 1); AcceptButton = apply; CancelButton = cancel;
            var leftActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            leftActions.Controls.Add(Theme.MakeButton("Сохранить мой стиль", ghost: true, action: SaveCustom));
            if (canEditCaptions) leftActions.Controls.Add(Theme.MakeButton("Текст и таймкоды", ghost: true, action: () => { EditCaptions = true; DialogResult = DialogResult.OK; Close(); }));
            selected = new Label { AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 8, 0, 0) }; leftActions.Controls.Add(selected); root.Controls.Add(leftActions, 0, 1);
            LoadFields(); LoadCards();
            timer.Tick += (s, e) => UpdatePreview(); timer.Start();
            FormClosed += (s, e) => timer.Stop();
        }
        static NumericUpDown Number(int min, int max) => new NumericUpDown { Minimum = min, Maximum = max, Width = 150 };
        static void Add(TableLayoutPanel fields, string label, Control control) {
            int row = fields.RowCount++; fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 4, 8) }, 0, row);
            control.Dock = DockStyle.Top; control.Margin = new Padding(0, 4, 0, 4); fields.Controls.Add(control, 1, row);
        }
        Button ColorButton(string name, Func<string> read, Action<string> write) {
            var button = Theme.MakeButton("Выбрать", ghost: true); button.Name = "Subtitle" + name;
            button.Click += (s, e) => { using (var picker = new ColorDialog { Color = ColorTranslator.FromHtml(read()), FullOpen = true }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                write(ColorTranslator.ToHtml(Color.FromArgb(picker.Color.R, picker.Color.G, picker.Color.B))); UpdateColors(); UpdatePreview();
            } }; return button;
        }
        void LoadFields() {
            loading = true;
            try {
                enabled.Checked = Style.Enabled; uppercase.Checked = Style.Uppercase; bold.Checked = Style.Bold; font.SelectedItem = Style.FontFamily;
                plate.SelectedIndex = Style.Plate == "none" ? 0 : Style.Plate == "rounded" ? 1 : 2;
                highlight.SelectedIndex = Style.Highlight == "none" ? 0 : Style.Highlight == "active" ? 1 : 2; motion.SelectedIndex = Style.Motion == "pop" ? 1 : 0;
                size.Value = Style.FontSize; position.Value = Style.Position; words.Value = Style.WordsPerCaption; opacity.Value = Style.Opacity; outline.Value = Style.OutlineWidth;
                UpdateColors();
            } finally { loading = false; }
            UpdatePreview();
        }
        void UpdateColors() {
            foreach (var item in new[] { Tuple.Create(textColor, Style.TextColor), Tuple.Create(plateColor, Style.PlateColor), Tuple.Create(accentColor, Style.AccentColor), Tuple.Create(outlineColor, Style.OutlineColor) }) {
                item.Item1.BackColor = ColorTranslator.FromHtml(item.Item2); item.Item1.ForeColor = item.Item1.BackColor.GetBrightness() > .5 ? Color.Black : Color.White;
            }
        }
        void Changed() {
            if (loading) return;
            Style.Enabled = enabled.Checked; Style.FontFamily = Convert.ToString(font.SelectedItem); Style.Uppercase = uppercase.Checked; Style.Bold = bold.Checked;
            Style.FontSize = (int)size.Value; Style.Position = (int)position.Value; Style.WordsPerCaption = (int)words.Value; Style.Opacity = (int)opacity.Value; Style.OutlineWidth = (int)outline.Value;
            Style.Plate = plate.SelectedIndex == 0 ? "none" : plate.SelectedIndex == 1 ? "rounded" : "solid";
            Style.Highlight = highlight.SelectedIndex == 0 ? "none" : highlight.SelectedIndex == 1 ? "active" : "progressive"; Style.Motion = motion.SelectedIndex == 1 ? "pop" : "none";
            Style.Normalize(); UpdatePreview();
        }
        void LoadCards() {
            foreach (Control old in cards.Controls.Cast<Control>().ToArray()) old.Dispose(); cards.Controls.Clear();
            try {
                foreach (var preset in SubtitlePresets.Load()) {
                    var card = new SubtitlePresetCard(preset) { Width = Math.Max(170, (cards.ClientSize.Width - 36) / 2), Height = 135, Margin = new Padding(4) };
                    card.Click += (s, e) => { Style = preset.Style.Copy(); Style.Enabled = true; selected.Text = preset.Name; LoadFields(); cards.Invalidate(true); };
                    cards.Controls.Add(card);
                }
            } catch (Exception ex) { MessageBox.Show(this, "Не удалось прочитать ваши стили: " + ex.Message, "Субтитры"); }
        }
        void SaveCustom() {
            using (var dialog = new Form { Text = "Мой стиль", Size = new Size(360, 165), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Card }) {
                var name = new TextBox { Left = 16, Top = 18, Width = 310, MaxLength = 40, Text = "Мой стиль" };
                var save = Theme.MakeButton("Сохранить", accent: true); save.Location = new Point(16, 57);
                save.Click += (s, e) => { try { var preset = SubtitlePresets.Save(name.Text, Style); Style = preset.Style.Copy(); selected.Text = preset.Name; LoadCards(); dialog.DialogResult = DialogResult.OK; dialog.Close(); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Субтитры"); } };
                dialog.Controls.Add(name); dialog.Controls.Add(save); dialog.AcceptButton = save; dialog.ShowDialog(this);
            }
        }
        void UpdatePreview() {
            if (preview == null || IsDisposed) return;
            preview.Set(Style, cues, (DateTime.UtcNow - started).TotalSeconds);
        }
        protected override void Dispose(bool disposing) { if (disposing) { timer.Stop(); timer.Dispose(); } base.Dispose(disposing); }
    }

    sealed class SubtitlePresetCard : Control {
        readonly Bitmap image;
        readonly SubtitlePreset preset;
        public SubtitlePresetCard(SubtitlePreset value) {
            preset = value; Name = "SubtitlePreset-" + value.Id; Cursor = Cursors.Hand; DoubleBuffered = true; AccessibleName = value.Name;
            var style = value.Style.Copy(); style.Position = 50; style.FontSize = Math.Min(60, style.FontSize);
            image = WarmupSubtitles.Paint("ТВОЙ СТИЛЬ", style, 540, 230);
        }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(Theme.Background); e.Graphics.DrawImage(image, new Rectangle(4, 4, Width - 8, Height - 34));
            TextRenderer.DrawText(e.Graphics, preset.Name, Theme.FontSmall, new Rectangle(8, Height - 28, Width - 16, 24), Theme.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var pen = new Pen(Focused ? Theme.Accent : Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        protected override void Dispose(bool disposing) { if (disposing) image.Dispose(); base.Dispose(disposing); }
    }

    sealed class SubtitleStylePreview : Control {
        readonly int canvasWidth, canvasHeight;
        readonly Image background;
        Bitmap caption;
        string key;
        public SubtitleStylePreview(int width, int height, Image image) { canvasWidth = width; canvasHeight = height; background = image == null ? null : new Bitmap(image); DoubleBuffered = true; }
        public void Set(WarmupSubtitleStyle style, List<WarmupCaption> source, double elapsed) {
            var cues = SubtitlePresets.ForStyle(source, style); if (cues.Count == 0) return;
            double start = cues[0].Start, duration = Math.Min(8, cues.Last().End - start + .4), time = start + elapsed % Math.Max(.1, duration);
            var cue = cues.FirstOrDefault(c => time >= c.Start && time < c.End);
            var frame = cue == null ? null : SubtitlePresets.FrameAt(cue, style, time);
            string next = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(style) + "|" + frame?.Text + "|" + frame?.ActiveWord + "|" + frame?.TextScale;
            if (next == key) return; key = next; caption?.Dispose();
            caption = !style.Enabled || frame == null ? null : SubtitleTypography.UsesTypography(style) ? SubtitleTypography.Paint(frame, style, canvasWidth, canvasHeight) : WarmupSubtitles.Paint(frame.Text, style, canvasWidth, canvasHeight);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(Theme.Background); float scale = Math.Min((Width - 12f) / canvasWidth, (Height - 12f) / canvasHeight);
            if (scale <= 0) return; var box = new RectangleF((Width - canvasWidth * scale) / 2, (Height - canvasHeight * scale) / 2, canvasWidth * scale, canvasHeight * scale);
            using (var brush = new SolidBrush(Color.FromArgb(20, 27, 40))) e.Graphics.FillRectangle(brush, box);
            if (background != null) e.Graphics.DrawImage(background, box); if (caption != null) e.Graphics.DrawImage(caption, box);
        }
        protected override void Dispose(bool disposing) { if (disposing) { caption?.Dispose(); background?.Dispose(); } base.Dispose(disposing); }
    }
}
