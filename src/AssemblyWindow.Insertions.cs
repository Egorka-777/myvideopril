using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        void OpenInsertions() {
            Safe(() => {
                using (var dialog = new Form { Text = "Рекламная пауза", Name = "AssemblyInsertions", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(820, 410), MinimumSize = new Size(700, 350), BackColor = Theme.Card, ForeColor = Theme.TextPrimary }) {
                    var note = new Label { Dock = DockStyle.Top, Height = 65, Padding = new Padding(10), Text = "Основной ролик и весь его звук останавливаются; после рекламы продолжаются с того же места.\nВремя — по исходному ролику. Папка: один случайный файл на паузу и ролик. Фото — без звука; аудио — на стоп-кадре." };
                    var grid = new DataGridView { Name = "InsertionRows", Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = true, BackgroundColor = Theme.Card, DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Color.Black, BackColor = Color.White }, RowHeadersWidth = 25 };
                    grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "Файл / папка", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
                    grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Random", HeaderText = "Случайно", Width = 70 });
                    grid.Columns.Add("Start", "Секунда / от"); grid.Columns.Add("End", "До, с"); grid.Columns.Add("Duration", "Фото, с");
                    foreach (var item in template.Insertions ?? new List<AssemblyInsertion>()) grid.Rows.Add(item.Source, item.RandomTime, item.Start.ToString(CultureInfo.CurrentCulture), item.End.ToString(CultureInfo.CurrentCulture), item.ImageDuration.ToString(CultureInfo.CurrentCulture));
                    var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(6), WrapContents = false };
                    Action<string> add = path => grid.Rows.Add(path, false, "0", "0", "5");
                    buttons.Controls.Add(Button("Добавить файл", () => { using (var picker = new OpenFileDialog { Filter = "Видео, аудио, фото|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp", Multiselect = true }) if (picker.ShowDialog(dialog) == DialogResult.OK) foreach (string path in picker.FileNames) add(path); }));
                    buttons.Controls.Add(Button("Добавить папку", () => { string path = Ui.Folder(dialog, template.Materials); if (path != null) add(path); }));
                    buttons.Controls.Add(Button("Удалить", () => { foreach (DataGridViewRow row in grid.SelectedRows) grid.Rows.Remove(row); }));
                    buttons.Controls.Add(Button("Сохранить", () => Safe(() => {
                        grid.EndEdit(); var items = new List<AssemblyInsertion>();
                        foreach (DataGridViewRow row in grid.Rows) {
                            double start = InsertionNumber(row.Cells["Start"].Value), end = InsertionNumber(row.Cells["End"].Value), duration = InsertionNumber(row.Cells["Duration"].Value);
                            bool random = Convert.ToBoolean(row.Cells["Random"].Value);
                            string source = Convert.ToString(row.Cells["Source"].Value).Trim();
                            if (source.Length == 0 || start < 0 || random && end < start || duration <= 0) throw new Exception("Укажите материал, неотрицательную секунду / диапазон и положительную длительность фото.");
                            items.Add(new AssemblyInsertion { Source = source, RandomTime = random, Start = start, End = end, ImageDuration = duration });
                        }
                        template.Insertions = items; Changed(false); Save(); dialog.DialogResult = DialogResult.OK; dialog.Close();
                    })));
                    dialog.Controls.Add(grid); dialog.Controls.Add(note); dialog.Controls.Add(buttons);
                    dialog.ShowDialog(this);
                }
            });
        }
        static double InsertionNumber(object value) {
            double number;
            if (!double.TryParse(Convert.ToString(value).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number))
                throw new Exception("Время рекламной паузы должно быть числом в секундах.");
            return number;
        }
    }
}
