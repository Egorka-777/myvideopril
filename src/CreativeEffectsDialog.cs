using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class CreativeEffectsDialog : Form {
        readonly string source, ffmpeg, probe;
        readonly CreativeEffectsSettings working;
        readonly ComboBox color, style;
        readonly TrackBar colorStrength, styleStrength;
        readonly Label colorValue, styleValue, status;
        readonly PictureBox before, after;
        readonly NumericUpDown timestamp;
        readonly Button apply;
        readonly System.Windows.Forms.Timer debounce;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        CancellationTokenSource rendering;
        Media media;
        int version;
        bool closing, ready;

        public static CreativeEffectsSettings Edit(IWin32Window owner, string source, CreativeEffectsSettings settings, string ffmpeg, string probe) {
            using (var dialog = new CreativeEffectsDialog(source, settings, ffmpeg, probe))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.working.Clone() : null;
        }

        public CreativeEffectsDialog(string source, CreativeEffectsSettings settings, string ffmpeg, string probe) {
            this.source = source; this.ffmpeg = ffmpeg; this.probe = probe;
            working = settings == null ? new CreativeEffectsSettings() : settings.Clone();
            working.Validate();
            Text = "Эффекты · цвет и стилизация";
            Font = Theme.FontBody; BackColor = Theme.Background; ForeColor = Theme.TextPrimary;
            AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false; MinimizeBox = false;
            var screen = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(Math.Min(1080, screen.Width - 60), Math.Min(760, screen.Height - 80));
            MinimumSize = new Size(Math.Min(720, screen.Width - 30), Math.Min(540, screen.Height - 40));
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            root.Controls.Add(new Label { Text = "Цвет и стиль сочетаются независимо. Эффекты применяются к исходнику до кадрирования и наложений.\nСтили — приближения FFmpeg, не точные копии CapCut / Photoshop. Предпросмотр — один кадр, не видео.", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary }, 0, 0);
            var settingsGrid = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 3 };
            settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); settingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            settingsGrid.Controls.Add(new Label { Text = "Цветовой фильтр", AutoSize = true }, 0, 0);
            settingsGrid.Controls.Add(new Label { Text = "Стилизация (приближение)", AutoSize = true }, 1, 0);
            color = Choice(new[] { "Без фильтра", "Тёплый", "Холодный", "Шоколад", "Сепия", "Монохром" });
            style = Choice(new[] { "Без стилизации", "Акварель · сглаживание и пастель", "Живопись · сглаживание и постеризация", "Пластик · сглаживание и контуры", "Мягкость · гауссово размытие" });
            color.SelectedIndex = Array.IndexOf(CreativeEffects.ColorPresets, working.ColorPreset);
            style.SelectedIndex = Array.IndexOf(CreativeEffects.StylePresets, working.StylePreset);
            settingsGrid.Controls.Add(color, 0, 1); settingsGrid.Controls.Add(style, 1, 1);
            colorStrength = Slider(working.ColorStrength); styleStrength = Slider(working.StyleStrength);
            colorValue = new Label { AutoSize = true }; styleValue = new Label { AutoSize = true };
            settingsGrid.Controls.Add(StrengthPanel(colorStrength, colorValue), 0, 2);
            settingsGrid.Controls.Add(StrengthPanel(styleStrength, styleValue), 1, 2);
            root.Controls.Add(settingsGrid, 0, 1);
            var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            previews.RowStyles.Add(new RowStyle(SizeType.AutoSize)); previews.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previews.Controls.Add(new Label { Text = "До · исходник", AutoSize = true }, 0, 0);
            previews.Controls.Add(new Label { Text = "После · цвет + стиль", AutoSize = true }, 1, 0);
            before = Preview(); after = Preview(); previews.Controls.Add(before, 0, 1); previews.Controls.Add(after, 1, 1);
            root.Controls.Add(previews, 0, 2);
            var timeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            timeRow.Controls.Add(new Label { Text = "Кадр на отметке, с:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            timestamp = new NumericUpDown { DecimalPlaces = 3, Increment = .1m, Width = 120, Enabled = false, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary };
            timeRow.Controls.Add(timestamp); root.Controls.Add(timeRow, 0, 3);
            var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { Text = "Чтение длительности исходника…", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary };
            footer.Controls.Add(status, 0, 0);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            apply = Theme.MakeButton("Применить", accent: true, action: () => { working.Validate(); DialogResult = DialogResult.OK; Close(); });
            apply.Enabled = false;
            var cancel = Theme.MakeButton("Отмена", ghost: true, action: () => { DialogResult = DialogResult.Cancel; Close(); });
            buttons.Controls.Add(apply); buttons.Controls.Add(cancel); footer.Controls.Add(buttons, 1, 0); root.Controls.Add(footer, 0, 4);
            AcceptButton = apply; CancelButton = cancel;
            debounce = new System.Windows.Forms.Timer { Interval = 300 };
            debounce.Tick += async (s, e) => { debounce.Stop(); await RenderAsync(); };
            color.SelectedIndexChanged += Changed; style.SelectedIndexChanged += Changed;
            colorStrength.ValueChanged += Changed; styleStrength.ValueChanged += Changed; timestamp.ValueChanged += Changed;
            ReadSettings();
            Shown += async (s, e) => await InitializeAsync();
        }
        static ComboBox Choice(string[] items) {
            var box = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary };
            box.Items.AddRange(items); return box;
        }
        static TrackBar Slider(double strength) { return new TrackBar { Minimum = 0, Maximum = 100, Value = (int)Math.Round(strength * 100), TickFrequency = 10, Dock = DockStyle.Top, Height = 38 }; }
        static Control StrengthPanel(TrackBar slider, Label value) {
            var panel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1 };
            panel.Controls.Add(value, 0, 0); panel.Controls.Add(slider, 0, 1); return panel;
        }
        static PictureBox Preview() { return new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(3, 3, 6, 6) }; }
        void ReadSettings() {
            working.ColorPreset = CreativeEffects.ColorPresets[color.SelectedIndex]; working.StylePreset = CreativeEffects.StylePresets[style.SelectedIndex];
            working.ColorStrength = colorStrength.Value / 100.0; working.StyleStrength = styleStrength.Value / 100.0;
            colorValue.Text = "Интенсивность цвета: " + colorStrength.Value + "%"; styleValue.Text = "Интенсивность стиля: " + styleStrength.Value + "%";
        }
        void Changed(object sender, EventArgs e) {
            ReadSettings();
            if (!ready || closing) return;
            ++version; rendering?.Cancel(); debounce.Stop(); apply.Enabled = false;
            SetImage(before, null); SetImage(after, null);
            status.ForeColor = Theme.TextSecondary; status.Text = "Обновление кадра…"; debounce.Start();
        }
        async Task InitializeAsync() {
            try {
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) throw new FileNotFoundException("Выберите исходное видео.", source);
                media = Core.VideoInfo(await Core.Probe(probe, source, lifetime.Token));
                if (closing) return;
                double fps = FrameRate(media.Fps);
                // Leave a full frame before EOF; the real ffprobe duration bounds the numeric selector.
                timestamp.Maximum = (decimal)Math.Max(0, media.VideoDuration - 1 / fps);
                ready = true; timestamp.Enabled = true;
                await RenderAsync();
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closing) Failure(ex); }
        }
        static double FrameRate(string rate) {
            string[] parts = (rate ?? "").Split('/');
            double numerator = Core.Parse(parts[0]), denominator = parts.Length > 1 ? Core.Parse(parts[1]) : 1;
            return numerator > 0 && denominator > 0 ? numerator / denominator : 25;
        }
        async Task RenderAsync() {
            if (!ready || closing) return;
            int request = ++version;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            rendering?.Cancel(); rendering = cancel;
            var ct = cancel.Token;
            string folder = Path.Combine(Path.GetTempPath(), "creative-effects-" + Guid.NewGuid().ToString("N"));
            Bitmap original = null, processed = null;
            try {
                apply.Enabled = false;
                Directory.CreateDirectory(folder);
                double factor = Math.Min(1, 720.0 / Math.Max(media.Width, media.Height));
                string scale = "scale=" + Math.Max(2, (int)(media.Width * factor) / 2 * 2) + ":" + Math.Max(2, (int)(media.Height * factor) / 2 * 2) + ",setsar=1";
                string filter = CreativeEffects.VideoFilter(working.Clone());
                double time = (double)timestamp.Value;
                string a = Path.Combine(folder, "before.png"), b = Path.Combine(folder, "after.png");
                await ExtractAsync(a, time, scale, ct);
                original = ReadBitmap(a);
                if (filter.Length == 0) processed = new Bitmap(original);
                else {
                    // Spatial effects run at source resolution, exactly as in the final render, before preview scaling.
                    await ExtractAsync(b, time, filter + "," + scale, ct);
                    processed = ReadBitmap(b);
                }
                ct.ThrowIfCancellationRequested();
                if (closing || request != version) return;
                SetImage(before, original); original = null; SetImage(after, processed); processed = null;
                status.ForeColor = Theme.TextSecondary;
                status.Text = "Кадр " + Core.N(time) + " / " + Core.N(media.VideoDuration) + " с · исходник " + media.Width + "×" + media.Height;
                apply.Enabled = true;
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closing && request == version) Failure(ex); }
            finally {
                original?.Dispose(); processed?.Dispose();
                if (ReferenceEquals(rendering, cancel)) rendering = null;
                cancel.Dispose();
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (Exception ex) {
                    System.Diagnostics.Trace.TraceError("Creative effects preview cleanup: " + ex);
                    if (!closing && request == version) { status.Text = "Не удалены временные кадры: " + ex.Message; status.ForeColor = Theme.Warning; }
                }
            }
        }
        Task<string> ExtractAsync(string output, double time, string filter, CancellationToken ct) {
            return Core.Tool(ffmpeg, new[] { "-v", "error", "-nostdin", "-y", "-ss", Core.N(time), "-i", source, "-map", "0:" + media.VideoIndex, "-an", "-sn", "-frames:v", "1", "-vf", filter, "-threads", "1", output }, ct, null, 60);
        }
        static Bitmap ReadBitmap(string path) { using (var image = Image.FromFile(path)) return new Bitmap(image); }
        static void SetImage(PictureBox box, Bitmap image) { var old = box.Image; box.Image = image; old?.Dispose(); }
        void Failure(Exception ex) { apply.Enabled = false; status.Text = "Предпросмотр не готов: " + ex.Message; status.ForeColor = Theme.Warning; }
        protected override void OnFormClosing(FormClosingEventArgs e) {
            base.OnFormClosing(e);
            if (!e.Cancel) Stop();
        }
        void Stop() { if (closing) return; closing = true; ++version; debounce.Stop(); lifetime.Cancel(); rendering?.Cancel(); }
        protected override void Dispose(bool disposing) {
            if (disposing) { Stop(); debounce.Dispose(); lifetime.Dispose(); SetImage(before, null); SetImage(after, null); }
            base.Dispose(disposing);
        }
    }
}
