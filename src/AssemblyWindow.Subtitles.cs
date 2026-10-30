using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        void LeaveSubtitleWorkspaceIfOpen() { if (subtitleWorkspaceActive) LeaveSubtitleWorkspace(); }

        void OpenSubtitleEditor() {
            Safe(() => {
                if (subtitleWorkspaceActive) LeaveSubtitleWorkspace();
                using (var background = canvas.SubtitleBackground())
                using (var dialog = new SubtitleStyleDialog(template.SubtitleStyle ?? new WarmupSubtitleStyle { Enabled = false }, PreviewCaptionCues(), background, template.Width, template.Height, template.Captions?.Count > 0)) {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    template.SubtitleStyle = dialog.Style.Copy();
                    loading = true; subtitlePosition.Value = template.SubtitleStyle.Position; loading = false;
                    Changed(false); Save(); ApplySubtitlePreview(); RequestTimingHints(); RefreshInfo();
                    if (dialog.EditCaptions) { EnterSubtitleWorkspace(); subtitleCaptions.Visible = true; }
                }
            });
        }

        void BuildSubtitleWorkspace() {
            subtitlePanel = new Panel { Name = "AssemblySubtitleEditor", Dock = DockStyle.Fill, Visible = false, BackColor = Theme.Card, Padding = new Padding(12, 8, 12, 8) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            subtitlePanel.Controls.Add(root);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Card };
            var stack = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 360 };
            scroll.Controls.Add(stack);
            scroll.ClientSizeChanged += (s, e) => stack.Width = Math.Max(280, scroll.ClientSize.Width - 20);
            root.Controls.Add(scroll, 0, 0);

            stack.Controls.Add(new Label { Text = "Субтитры · " + AssemblyProfile.ShortLabel(studioProfile), Font = Theme.FontCardTitle, AutoSize = true, ForeColor = Theme.TextPrimary, Margin = new Padding(0, 0, 0, 8) });
            stack.Controls.Add(new Label { Text = "Слева — крупное превью ролика. Тяните плашку субтитров или двигайте положение.", Width = 360, Height = 36, ForeColor = Theme.TextSecondary });
            subtitleEnabled = new CheckBox { AutoSize = true, Text = "Показывать субтитры в ролике", Margin = new Padding(0, 6, 0, 4) };
            stack.Controls.Add(subtitleEnabled);
            subtitleUppercase = new CheckBox { AutoSize = true, Text = "Все буквы заглавные", Margin = new Padding(0, 0, 0, 8) };
            stack.Controls.Add(subtitleUppercase);

            subtitleFont = Theme.MakeCombo(SubtitlePresets.Fonts); SubtitleField(stack, "Шрифт", subtitleFont);
            subtitleSize = SubtitleSlider(stack, "Размер текста", 28, 88, 0, 56, "");
            subtitlePanelPosition = SubtitleSlider(stack, "Положение сверху", 10, 90, 0, 72, "%");
            subtitleWords = SubtitleSlider(stack, "Слов в группе", 1, 6, 0, 3, "");
            subtitlePlate = Theme.MakeCombo(new[] { "Скруглённая", "Прямая", "Без плашки" }); SubtitleField(stack, "Плашка", subtitlePlate);
            subtitleOpacity = SubtitleSlider(stack, "Плотность плашки", 0, 100, 0, 90, "%");

            var moves = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 8) };
            moves.Controls.Add(Button("↑", () => NudgeSubtitle(-3))); moves.Controls.Add(Button("По центру", () => SetSubtitlePosition(50))); moves.Controls.Add(Button("↓", () => NudgeSubtitle(3)));
            stack.Controls.Add(new Label { Text = "Положение на экране", Height = 22, ForeColor = Theme.TextSecondary }); stack.Controls.Add(moves);

            var colors = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
            colors.Controls.Add(SubtitleButton("Цвет текста", () => PickSubtitleColor(false)));
            colors.Controls.Add(SubtitleButton("Цвет плашки", () => PickSubtitleColor(true)));
            stack.Controls.Add(colors);

            stack.Controls.Add(new Label { Text = "Точные таймкоды берутся из озвучки ElevenLabs автоматически. Для старых файлов создайте новую озвучку.", Width = 360, Height = 52, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 8, 0, 4) });
            subtitleCaptions = new DataGridView {
                Visible = false, Width = 360, Height = 220, AutoGenerateColumns = false, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
                BackgroundColor = Theme.Card, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Color.Black, BackColor = Color.White, WrapMode = DataGridViewTriState.True },
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            subtitleCaptions.Columns.Add(new DataGridViewTextBoxColumn { Name = "Start", HeaderText = "Начало, с", Width = 72 });
            subtitleCaptions.Columns.Add(new DataGridViewTextBoxColumn { Name = "End", HeaderText = "Конец, с", Width = 72 });
            subtitleCaptions.Columns.Add(new DataGridViewTextBoxColumn { Name = "Text", HeaderText = "Текст", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            stack.Controls.Add(subtitleCaptions);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            subtitlePanelMessage = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, Text = "Изменения сохраняются автоматически." };
            var done = Button("← К сценам", LeaveSubtitleWorkspace); done.AutoSize = false; done.Dock = DockStyle.Fill;
            bottom.Controls.Add(subtitlePanelMessage, 0, 0); bottom.Controls.Add(done, 1, 0);
            root.Controls.Add(bottom, 0, 1);

            foreach (var n in new NumericUpDown[] { subtitleSize, subtitlePanelPosition, subtitleWords, subtitleOpacity })
                n.ValueChanged += (s, e) => PushSubtitleStyleToPreview();
            subtitleFont.SelectedIndexChanged += (s, e) => PushSubtitleStyleToPreview();
            subtitlePlate.SelectedIndexChanged += (s, e) => PushSubtitleStyleToPreview();
            subtitleEnabled.CheckedChanged += (s, e) => PushSubtitleStyleToPreview();
            subtitleUppercase.CheckedChanged += (s, e) => PushSubtitleStyleToPreview();
            subtitleCaptions.SelectionChanged += (s, e) => ApplySubtitlePreview();
            subtitleCaptions.CellValueChanged += (s, e) => { PushSubtitleStyleToPreview(); SaveSubtitlesFromPanel(false); };

            subtitlePanelPosition.ValueChanged += (s, e) => {
                if (subtitlePanelLoading) return;
                SetSubtitlePosition((int)subtitlePanelPosition.Value);
            };

            contentHost.Controls.Add(subtitlePanel);
        }

        static void SubtitleField(FlowLayoutPanel panel, string caption, Control control) {
            panel.Controls.Add(new Label { Text = caption, AutoSize = false, Height = 22, ForeColor = Theme.TextSecondary });
            control.Width = 360; control.Margin = new Padding(0, 0, 0, 6); panel.Controls.Add(control);
        }

        NumericUpDown SubtitleSlider(FlowLayoutPanel panel, string caption, decimal min, decimal max, int decimals, double value, string unit) {
            var number = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .1m, Visible = false };
            number.Value = Math.Max(min, Math.Min(max, (decimal)value));
            Controls.Add(number);
            panel.Controls.Add(new AssemblySlider(caption, number, unit) { Width = 360, Margin = new Padding(0, 0, 0, 6) });
            return number;
        }

        Button SubtitleButton(string text, Action action) {
            var b = Button(text, action); b.AutoSize = false; b.Size = new Size(170, 36); b.Margin = new Padding(0, 0, 8, 0); return b;
        }

        void EnterSubtitleWorkspace() {
            subtitleWorkspaceActive = true;
            subtitleEditing = true;
            layerIndex = -1;
            sceneEditor.Visible = false;
            if(audioWorkspace != null)audioWorkspace.Visible=false;
            subtitlePanel.Visible = true;
            subtitlePanel.BringToFront();
            LoadSubtitlePanelFromTemplate();
            canvas.SetSubtitleEditing(true);
            RefreshCanvas(); RequestBackground(); RefreshInfo();
            status.Text = "Субтитры · настройки слева, крупное превью справа. Тяните плашку на экране.";
        }

        void LeaveSubtitleWorkspace() {
            Safe(() => {
                try { SaveSubtitlesFromPanel(true); }
                catch (Exception ex) { subtitlePanelMessage.Text = ex.Message; return; }
                subtitleWorkspaceActive = false;
                subtitleEditing = false;
                subtitlePanel.Visible = false;
                sceneEditor.Visible = true;
                canvas.SetSubtitleEditing(false);
                RefreshCanvas(); RefreshInfo(); RequestTimingHints();
                status.Text = "Субтитры сохранены · " + AssemblyProfile.ShortLabel(studioProfile) + ".";
            });
        }

        void LoadSubtitlePanelFromTemplate() {
            subtitlePanelLoading = true;
            try {
                var style = (template.SubtitleStyle ?? new WarmupSubtitleStyle { Enabled = false }).Copy(); style.Normalize();
                subtitleEnabled.Checked = style.Enabled;
                subtitleUppercase.Checked = style.Uppercase;
                subtitleFont.SelectedItem = style.FontFamily;
                if (subtitleFont.SelectedIndex < 0) subtitleFont.SelectedIndex = 0;
                subtitlePlate.SelectedIndex = style.Plate == "none" ? 2 : style.Plate == "solid" ? 1 : 0;
                subtitleSize.Value = style.FontSize;
                subtitlePanelPosition.Value = style.Position;
                subtitleWords.Value = style.WordsPerCaption;
                subtitleOpacity.Value = style.Opacity;
                subtitleCaptions.Rows.Clear();
                foreach (var c in template.Captions ?? new List<AssemblyCaption>())
                    subtitleCaptions.Rows.Add(c.Start.ToString("0.###", CultureInfo.CurrentCulture), c.End.ToString("0.###", CultureInfo.CurrentCulture), c.Text);
            } finally { subtitlePanelLoading = false; }
            PushSubtitleStyleToPreview();
        }

        void PushSubtitleStyleToPreview() {
            if (subtitlePanelLoading || !subtitleWorkspaceActive) return;
            if (template.SubtitleStyle == null) template.SubtitleStyle = new WarmupSubtitleStyle();
            var style = template.SubtitleStyle;
            style.Enabled = subtitleEnabled.Checked;
            style.Uppercase = subtitleUppercase != null && subtitleUppercase.Checked;
            style.FontFamily = Convert.ToString(subtitleFont.SelectedItem);
            style.FontSize = (int)subtitleSize.Value;
            style.Position = (int)subtitlePanelPosition.Value;
            style.WordsPerCaption = (int)subtitleWords.Value;
            style.Opacity = (int)subtitleOpacity.Value;
            style.Plate = subtitlePlate.SelectedIndex == 2 ? "none" : subtitlePlate.SelectedIndex == 1 ? "solid" : "rounded";
            style.Normalize();
            loading = true; subtitlePosition.Value = style.Position; loading = false;
            ApplySubtitlePreview();
            UpdateSubtitleTimingMessage();
            saveTimer.Stop(); saveTimer.Start();
        }

        void UpdateSubtitleTimingMessage() {
            if (!subtitleWorkspaceActive || subtitlePanelMessage == null) return;
            var lines = AssemblySpeechPlan.AvailableLines(template);
            bool missing = lines.Any(l => !System.IO.File.Exists(template.Resolve(l.AudioPath) + ".timing.json"));
            subtitlePanelMessage.Text = template.SubtitleStyle?.Enabled != true ? "Субтитры отключены." :
                missing ? "Нет таймкодов: создайте озвучку заново. В превью — только образец оформления." :
                PreviewCaptionCues().Count > 0 ? "Точные таймкоды озвучки загружены." : "Нужна озвучка с таймкодами ElevenLabs.";
            subtitlePanelMessage.ForeColor = missing ? Theme.Error : Theme.TextSecondary;
        }

        void PickSubtitleColor(bool plate) {
            var style = template.SubtitleStyle ?? new WarmupSubtitleStyle();
            using (var picker = new ColorDialog { Color = ColorTranslator.FromHtml(plate ? style.PlateColor : style.TextColor), FullOpen = true }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                string color = "#" + picker.Color.R.ToString("X2") + picker.Color.G.ToString("X2") + picker.Color.B.ToString("X2");
                if (template.SubtitleStyle == null) template.SubtitleStyle = new WarmupSubtitleStyle();
                if (plate) template.SubtitleStyle.PlateColor = color; else template.SubtitleStyle.TextColor = color;
                PushSubtitleStyleToPreview();
            }
        }

        void SaveSubtitlesFromPanel(bool strict) {
            if (subtitleCaptions == null || !subtitleWorkspaceActive) return;
            subtitleCaptions.EndEdit();
            var style = template.SubtitleStyle ?? new WarmupSubtitleStyle();
            if (subtitleEnabled != null) {
                style.Enabled = subtitleEnabled.Checked;
                style.Uppercase = subtitleUppercase != null && subtitleUppercase.Checked;
                style.FontFamily = Convert.ToString(subtitleFont.SelectedItem);
                style.FontSize = (int)subtitleSize.Value;
                style.Position = (int)subtitlePanelPosition.Value;
                style.WordsPerCaption = (int)subtitleWords.Value;
                style.Opacity = (int)subtitleOpacity.Value;
                style.Plate = subtitlePlate.SelectedIndex == 2 ? "none" : subtitlePlate.SelectedIndex == 1 ? "solid" : "rounded";
                style.Normalize();
            }
            var saved = ReadSubtitleCaptions(strict);
            template.SubtitleStyle = style;
            template.Captions = saved;
            if (saved.Count > 0) template.SubtitleStyle.Enabled = true;
            Changed(false); Save();
        }

        List<AssemblyCaption> ReadSubtitleCaptions(bool strict) {
            var result = new List<AssemblyCaption>();
            foreach (DataGridViewRow row in subtitleCaptions.Rows) {
                if (row.IsNewRow) continue;
                if (!SubtitleSeconds(Convert.ToString(row.Cells["Start"].Value), out double start) || !SubtitleSeconds(Convert.ToString(row.Cells["End"].Value), out double end)) {
                    if (strict) throw new Exception("Субтитр " + (row.Index + 1) + ": проверьте секунды (конец позже начала).");
                    continue;
                }
                if (start < 0 || end <= start || end > 86400) {
                    if (strict) throw new Exception("Субтитр " + (row.Index + 1) + ": проверьте секунды (конец позже начала).");
                    continue;
                }
                result.Add(new AssemblyCaption { Start = start, End = end, Text = Convert.ToString(row.Cells["Text"].Value) ?? "" });
            }
            return result;
        }

        static bool SubtitleSeconds(string text, out double value) {
            return double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
