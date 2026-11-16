using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        void OpenInsertions() {
            Safe(() => {
                using (var dialog = new Form { Text = "Рекламная пауза", Name = "AssemblyInsertions", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(1080, 560), MinimumSize = new Size(850, 450), BackColor = Theme.Card, ForeColor = Theme.TextPrimary }) {
                    var note = new Label { Dock = DockStyle.Top, Height = 85, Padding = new Padding(10), Text = "Основной ролик и весь его звук останавливаются; после рекламы продолжаются с того же места.\nПустое время — случайное место по всему ролику. Секунда — точное время; «Диапазон» + от / до — случайно в диапазоне.\nВремя — по исходному ролику. Папка: один случайный файл на паузу и ролик. Связка: аудио в первой колонке + визуал. Сдвиг +2: анимация позже; −2: раньше. Визуал повторяется на длину аудио." };
                    var grid = new DataGridView { Name = "InsertionRows", Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = true, BackgroundColor = Theme.Card, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Color.Black, BackColor = Color.White }, RowHeadersWidth = 25 };
                    grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Файл / папка", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
                    grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Random", HeaderText = "Диапазон", Width = 75 });
                    grid.Columns.Add("Start", "Секунда / от"); grid.Columns.Add("End", "До, с"); grid.Columns.Add("Duration", "Фото, с");
                    grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Visual", HeaderText = "Визуал / папка", Width = 220 });
                    grid.Columns.Add("Offset", "Сдвиг визуала, с");
                    foreach (var item in template.Insertions ?? new List<AssemblyInsertion>()) { int index = grid.Rows.Add(item.Source, item.RandomTime, item.RandomWholeVideo ? "" : item.Start.ToString(CultureInfo.CurrentCulture), item.RandomWholeVideo ? "" : item.End.ToString(CultureInfo.CurrentCulture), item.ImageDuration.ToString(CultureInfo.CurrentCulture), item.VisualSource, item.VisualOffset.ToString(CultureInfo.CurrentCulture)); grid.Rows[index].Tag = item.Copy(); }
                    var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(6), WrapContents = true };
                    Action<string> add = path => grid.Rows.Add(path, false, "", "", "5", "", "0");
                    Func<DataGridViewRow, AssemblyInsertion> read = row => ReadInsertion(
                        row.Tag as AssemblyInsertion, template, row.Cells["Source"].Value, row.Cells["Visual"].Value,
                        row.Cells["Start"].Value, row.Cells["End"].Value, Convert.ToBoolean(row.Cells["Random"].Value),
                        row.Cells["Duration"].Value, row.Cells["Offset"].Value);
                    var shift = new NumericUpDown { Minimum = -120, Maximum = 120, DecimalPlaces = 1, Increment = .5m, Visible = false }; buttons.Controls.Add(shift);
                    var shiftSlider = new AssemblySlider("Сдвиг визуала · раньше / позже", shift, "с") { Width = 420, Name = "InsertionOffsetSlider" };
                    bool loadingShift = false;
                    shift.ValueChanged += (s,e) => { if (!loadingShift && grid.CurrentRow != null) grid.CurrentRow.Cells["Offset"].Value = shift.Value.ToString(CultureInfo.CurrentCulture); };
                    grid.SelectionChanged += (s,e) => { if (grid.CurrentRow != null) { double value = InsertionNumber(grid.CurrentRow.Cells["Offset"].Value, 0); loadingShift = true; try { shift.Value = Math.Max(shift.Minimum, Math.Min(shift.Maximum, (decimal)value)); } finally { loadingShift = false; } } };
                    buttons.Controls.Add(Button("Добавить файл", () => { using (var picker = new OpenFileDialog { Filter = "Видео, аудио, фото|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp", Multiselect = true }) if (picker.ShowDialog(dialog) == DialogResult.OK) foreach (string path in picker.FileNames) add(path); }));
                    buttons.Controls.Add(Button("Добавить папку", () => { string path = Ui.Folder(dialog, template.Materials); if (path != null) add(path); }));
                    buttons.Controls.Add(Button("Визуал к аудио…", () => { if (grid.CurrentRow == null) return; using (var picker = new OpenFileDialog { Filter = "Анимация / изображение|*.mp4;*.mov;*.webm;*.mkv;*.avi;*.gif;*.png;*.jpg;*.jpeg;*.webp;*.bmp" }) if (picker.ShowDialog(dialog) == DialogResult.OK) grid.CurrentRow.Cells["Visual"].Value = picker.FileName; }));
                    var preview = Button("Просмотреть связку", () => { });
                    preview.Click += async (s,e) => { preview.Enabled = false; try { grid.EndEdit(); if (grid.CurrentRow != null) { var row = grid.CurrentRow; var item = read(row); await PreviewInsertion(dialog, item); row.Tag = item; } } catch (Exception ex) { Ui.Error(dialog, ex); } finally { preview.Enabled = true; } }; buttons.Controls.Add(preview);
                    buttons.Controls.Add(Button("Удалить", () => { foreach (DataGridViewRow row in grid.SelectedRows) grid.Rows.Remove(row); }));
                    buttons.Controls.Add(Button("Сохранить", () => Safe(() => {
                        grid.EndEdit(); var items = new List<AssemblyInsertion>();
                        foreach (DataGridViewRow row in grid.Rows) {
                            items.Add(read(row));
                        }
                        template.Insertions = items; Changed(false); Save(); dialog.DialogResult = DialogResult.OK; dialog.Close();
                    })));
                    buttons.Controls.Add(shiftSlider);
                    dialog.Controls.Add(grid); dialog.Controls.Add(note); dialog.Controls.Add(buttons);
                    dialog.ShowDialog(this);
                }
            });
        }
        async System.Threading.Tasks.Task PreviewInsertion(Form owner, AssemblyInsertion insertion) {
            if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-ad-preview-" + Guid.NewGuid().ToString("N")); System.IO.Directory.CreateDirectory(folder);
            using (var lifetime = new System.Threading.CancellationTokenSource()) {
                FormClosedEventHandler closed = (s,e) => lifetime.Cancel(); owner.FormClosed += closed;
                try {
                    string main = System.IO.Path.Combine(folder,"main.mp4"), output = System.IO.Path.Combine(folder,"preview.mp4");
                    Bitmap background = canvas.SubtitleBackground();
                    if (background == null) throw new Exception("Выберите сцену для предпросмотра рекламной паузы.");
                    using (background) {
                        if (!string.IsNullOrWhiteSpace(insertion.VisualSource)) {
                            var visuals = AssemblyFiles.Pool(template.Resolve(insertion.VisualSource), AssemblyFiles.PictureExtensions.Concat(AssemblyFiles.VideoExtensions).Distinct().ToArray());
                            if (visuals.Count == 0) throw new Exception("Не найден визуал связки.");
                            string visual = visuals[0];
                            var size = AssemblyOverlayMedia.Size(visual);
                            var previewTemplate = new AssemblyTemplate { Width = background.Width, Height = background.Height, SplitLayout = "none" };
                            var layer = new AssemblyLayer { Source = visual, PreserveAspectRatio = insertion.VisualPreserveAspectRatio, UniformImageScale = true, PlaybackMode = "continuous", Opacity = 1 };
                            if (insertion.VisualPlacementConfigured) { layer.X=insertion.VisualX; layer.Y=insertion.VisualY; layer.Width=insertion.VisualWidth; layer.Height=insertion.VisualHeight; }
                            else {
                                double scale = Math.Min((double)background.Width/size.Width,(double)background.Height/size.Height);
                                layer.Width=size.Width*scale/background.Width; layer.Height=size.Height*scale/background.Height; layer.X=(1-layer.Width)/2; layer.Y=(1-layer.Height)/2;
                            }
                            using (var editor = new Form { Text = "Размещение визуала связки", ClientSize = new Size(800,700), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Background, ForeColor = Theme.TextPrimary })
                            using (var placementCanvas = new AssemblyCanvas { Dock = DockStyle.Fill }) {
                                var scene = new AssemblyScenePlan { Scene = new AssemblyScene { Duration = 60 } }; scene.Scene.Layers.Add(layer); scene.Layers.Add(new AssemblyLayerPlan { Layer = layer, Asset = new AssemblyAsset { Path = visual } });
                                placementCanvas.SetScene(previewTemplate,scene,layer); placementCanvas.SetBackground(new Bitmap(background));
                                placementCanvas.PlacementChanged += () => placementCanvas.SetScene(previewTemplate,scene,layer);
                                placementCanvas.PreviewError += message => editor.Text = "Размещение визуала · " + message;
                                var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 85, WrapContents = true };
                                var keep = new CheckBox { Text = "Сохранять пропорции", Checked = layer.PreserveAspectRatio, AutoSize = true, ForeColor = Theme.TextPrimary }; keep.CheckedChanged += (s,e) => { layer.PreserveAspectRatio=keep.Checked; placementCanvas.SetScene(previewTemplate,scene,layer); };
                                actions.Controls.Add(keep);
                                actions.Controls.Add(new Label { Text = "Тяните визуал мышью; нижний правый угол — размер. Выход за кадр обрезается.", AutoSize = true, ForeColor = Theme.TextSecondary });
                                actions.Controls.Add(Button("Сохранить размещение", () => { editor.DialogResult=DialogResult.OK; editor.Close(); }, true));
                                editor.Controls.Add(placementCanvas); editor.Controls.Add(actions);
                                if (editor.ShowDialog(owner) != DialogResult.OK) return;
                                insertion.VisualPlacementConfigured=true; insertion.VisualPreserveAspectRatio=layer.PreserveAspectRatio;
                                insertion.VisualX=layer.X; insertion.VisualY=layer.Y; insertion.VisualWidth=layer.Width; insertion.VisualHeight=layer.Height;
                            }
                            // Preview exactly the selected visual, not another random file from its pool.
                            insertion = insertion.Copy(); insertion.VisualSource=visual;
                        }
                        background.Save(System.IO.Path.Combine(folder,"background.png"));
                    }
                    await Core.Tool(ffmpeg, new[] { "-v", "error", "-y", "-loop", "1", "-i", System.IO.Path.Combine(folder,"background.png"), "-t", ".1", "-vf", "scale=540:960:force_original_aspect_ratio=decrease:force_divisible_by=2", "-r", "30", "-c:v", "libx264", "-pix_fmt", "yuv420p", main }, lifetime.Token, null, 60);
                    var t = new AssemblyTemplate { Materials = template.Materials, Fps = 30 }; var copy = insertion.Copy(); copy.Start = 0; copy.RandomTime = false; copy.RandomWholeVideo = false; t.Insertions.Add(copy);
                    await AssemblyInsertions.Apply(t, ffmpeg, probe, main, output, lifetime.Token, new Random());
                    using (var player = new Form { Text = "Предпросмотр связки · анимация + аудио", ClientSize = new Size(760,620), StartPosition = FormStartPosition.CenterParent, BackColor = Color.Black })
                    using (var browser = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill }) {
                        player.Controls.Add(browser);
                        player.Shown += async (s,e) => { try {
                            await browser.EnsureCoreWebView2Async();
                            browser.CoreWebView2.SetVirtualHostNameToFolderMapping("ad-preview.local", folder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                            browser.NavigateToString("<html><body style='margin:0;background:#111;color:white'><video controls autoplay style='width:100%;height:100vh' src='https://ad-preview.local/preview.mp4'></video></body></html>");
                        } catch (Exception ex) { Ui.Error(player,ex); } };
                        player.ShowDialog(owner);
                    }
                } finally { owner.FormClosed -= closed; try { System.IO.Directory.Delete(folder,true); } catch (System.IO.IOException) { } }
            }
        }
        internal static AssemblyInsertion ReadInsertion(AssemblyInsertion previous, AssemblyTemplate template, object source, object visual,
            object start, object end, bool random, object duration, object offset) {
            var item = previous?.Copy() ?? new AssemblyInsertion();
            item.Source = Convert.ToString(source).Trim(); item.VisualSource = Convert.ToString(visual).Trim();
            item.RandomWholeVideo = string.IsNullOrWhiteSpace(Convert.ToString(start)) && (!random || string.IsNullOrWhiteSpace(Convert.ToString(end)));
            item.RandomTime = random;
            if (!item.RandomWholeVideo) {
                item.Start = InsertionNumber(start);
                if (random) item.End = InsertionNumber(end);
                if (item.Start < 0 || random && item.End < item.Start) throw new Exception("Укажите неотрицательную секунду / диапазон рекламной паузы.");
            }
            bool photo = AssemblyInsertions.IsImage(item.Source) || AssemblyFiles.Pool(template.Resolve(item.Source), AssemblyFiles.PictureExtensions).Any(AssemblyInsertions.IsImage);
            if (photo) {
                item.ImageDuration = InsertionNumber(duration, 5);
                if (item.ImageDuration <= 0) throw new Exception("Длительность фото должна быть положительной.");
            } else if (double.TryParse(Convert.ToString(duration).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && !double.IsNaN(seconds) && !double.IsInfinity(seconds)) item.ImageDuration = seconds;
            item.VisualOffset = InsertionNumber(offset, 0);
            if (item.Source.Length == 0) throw new Exception("Укажите материал рекламной паузы.");
            return item;
        }

        static double InsertionNumber(object value, double? blank = null) {
            if (blank.HasValue && string.IsNullOrWhiteSpace(Convert.ToString(value))) return blank.Value;
            double number;
            if (!double.TryParse(Convert.ToString(value).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number))
                throw new Exception("Время рекламной паузы должно быть числом в секундах.");
            return number;
        }
    }
}
