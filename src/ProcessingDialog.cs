using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class ProcessingDialog : Dialog {
        public ProcessingSettings Settings;
        sealed class RowControls { public CheckBox Enabled; public NumericUpDown Min, Max; public Func<ProcessingSettings, ProcessingParameter> Get; }
        readonly List<RowControls> rows = new List<RowControls>();
        readonly Dictionary<string, NumericUpDown[]> details = new Dictionary<string, NumericUpDown[]>();
        readonly Dictionary<string, CheckBox> brands = new Dictionary<string, CheckBox>();
        CheckBox mirror, device, captureDate, randomTime;
        NumericUpDown days;
        Label preview;
        readonly Random random = new Random();

        public ProcessingDialog(ProcessingSettings source) : base("Фильтры и эффекты", 690) {
            ClientSize = new Size(850, 690); MinimumSize = new Size(800, 580);
            Settings = Store.Clone(source ?? new ProcessingSettings()); Settings.Validate();
            var layout = Ui.Table(); layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Body.Controls.Add(layout);
            var hint = Ui.Label("Для каждого результата выбираются новые значения между «от» и «до». Выключенные параметры сохраняют прежнюю обработку. Панель общая для коротких и длинных видео.", true);
            hint.MaximumSize = new Size(770, 0); layout.Controls.Add(hint, 0, 0);
            var buttons = Ui.Flow(); buttons.WrapContents = true; buttons.Margin = new Padding(0, 0, 0, 12);
            buttons.Controls.Add(Ui.Button("Случайные настройки", () => Safe(() => { Read(); Settings.RandomizeEnabledRanges(random); LoadValues(); })));
            buttons.Controls.Add(Ui.Button("Сохранить пресет", SavePreset)); buttons.Controls.Add(Ui.Button("Загрузить пресет", LoadPreset));
            buttons.Controls.Add(Ui.Button("Выключить всё", () => { foreach (var row in rows) row.Enabled.Checked = false; mirror.Checked = device.Checked = captureDate.Checked = false; }));
            layout.Controls.Add(buttons, 0, 1);
            var tabs = new TabControl { Dock = DockStyle.Fill }; layout.Controls.Add(tabs, 0, 2);
            var filters = Page(tabs, "Фильтры"); var ft = RangeTable(filters);
            Add(ft, "Обрезка с каждой стороны, %", s => s.Crop, 0, 15);
            Add(ft, "Подрезка по времени — суммарно, %", s => s.Trim, 0, 15);
            Add(ft, "Поворот, °", s => s.Rotation, -180, 180);
            Add(ft, "Скорость · 100 = исходная, %", s => s.Speed, 50, 200);
            Add(ft, "Яркость · 0 = исходная, %", s => s.Brightness, -50, 50);
            Add(ft, "Контраст · 100 = исходный, %", s => s.Contrast, 50, 150);
            Add(ft, "Резкость · 0 = исходная, %", s => s.Sharpness, -50, 50);
            Add(ft, "Зернистость / шум, %", s => s.Noise, 0, 100);
            Hint(filters, "Подрезка случайно распределяется между началом и концом. Поворот сохраняет размер кадра, свободные края — чёрные. Включённые обрезка, скорость, яркость и контраст заменяют соответствующие значения прежних настроек.");
            var audio = Page(tabs, "Аудио"); var at = RangeTable(audio);
            Add(at, "Громкость · 100 = текущая, %", s => s.Volume, 0, 200);
            Add(at, "Высота тона · 100 = исходная, %", s => s.Pitch, 50, 200);
            Add(at, "Темп аудио · 100 = исходный, %", s => s.Tempo, 50, 200);
            Add(at, "Всплески громкости — глубина, %", s => s.AudioModulation, 0, 50);
            Detail(at, "Частота всплесков, Гц", "modulation", Settings.ModulationFrequency, .1, 10);
            Hint(audio, "Тон меняется с компенсацией темпа. Темп меняется без изменения тона. Эти эффекты применяются к итоговому звуку, включая музыку и озвучку. После изменения темпа звук обрезается или дополняется тишиной до длины видео. Громкость умножается на текущую; усиление ограничивается от перегрузки.");
            var effects = Page(tabs, "Видеоэффекты"); var et = RangeTable(effects);
            Add(et, "Затемнение в начале (Fade In), с", s => s.FadeIn, 0, 10);
            Add(et, "Затемнение в конце (Fade Out), с", s => s.FadeOut, 0, 10);
            mirror = new CheckBox { Text = "Отзеркаливание по горизонтали", AutoSize = true, Margin = new Padding(0, 10, 0, 10) };
            effects.Controls.Add(mirror);
            var extra = RangeTable(effects);
            Add(extra, "Полупрозрачная сетка — непрозрачность, %", s => s.GridOpacity, 0, 50);
            Detail(extra, "Размер ячейки сетки, px", "grid", Settings.GridCell, 8, 512);
            Add(extra, "Случайный плавный зум / кроп, %", s => s.Zoom, 100, 130);
            Detail(extra, "Период зума, с", "zoom", Settings.ZoomPeriod, .5, 30);
            Hint(effects, "100% зума = без приближения. Пик, период и центр выбираются заново для каждого результата. Затемнения ограничиваются половиной длины ролика, чтобы не перекрывать друг друга.");
            var metadata = Page(tabs, "Метаданные");
            device = new CheckBox { Text = "Записывать случайные Brand / Model / Software", AutoSize = true };
            metadata.Controls.Add(device);
            var brandPanel = new FlowLayoutPanel { AutoSize = true, Width = 730, WrapContents = true };
            foreach (string brand in ProcessingDevices.BrandNames) {
                var check = new CheckBox { Text = brand, AutoSize = true, Margin = new Padding(0, 8, 20, 8) };
                brands.Add(brand, check); brandPanel.Controls.Add(check);
            }
            metadata.Controls.Add(brandPanel);
            Hint(metadata, "Выбор только из отмеченных брендов. Model и Software берутся из одной записи выбранного бренда. Это пользовательские шаблоны метаданных, которые не подтверждают устройство съёмки.");
            captureDate = new CheckBox { Text = "Случайный сдвиг даты съёмки назад", AutoSize = true, Margin = new Padding(0, 15, 0, 8) };
            metadata.Controls.Add(captureDate);
            var dateRow = Ui.Fields(); dateRow.Width = 730; dateRow.Dock = DockStyle.None;
            days = Ui.Number(Settings.CaptureDaysBack, 0, 3650, 0); Ui.Row(dateRow, "Глубина, дней назад", days); metadata.Controls.Add(dateRow);
            randomTime = new CheckBox { Text = "Также рандомизировать время суток", AutoSize = true }; metadata.Controls.Add(randomTime);
            Hint(metadata, "Дата выбирается между текущим моментом и указанным числом дней назад. Будущие даты исключены. Выбранная дата записывается в метаданные и даты результирующего файла.");
            preview = Ui.Label(""); preview.MaximumSize = new Size(730, 0); preview.Font = new Font("Consolas", 10);
            metadata.Controls.Add(preview); metadata.Controls.Add(Ui.Button("Прокрутить пример", () => Safe(Preview)));
            Hint(metadata, "Пример — отдельный случайный выбор. При обработке каждого результата значения выбираются заново.");
            device.CheckedChanged += (a, b) => { foreach (var check in brands.Values) check.Enabled = device.Checked; };
            captureDate.CheckedChanged += (a, b) => { days.Enabled = randomTime.Enabled = captureDate.Checked; };
            LoadValues(); StandardButtons(Read);
        }
        static FlowLayoutPanel Page(TabControl tabs, string title) {
            var page = new TabPage(title) { Padding = new Padding(12) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            page.Controls.Add(flow); tabs.TabPages.Add(page); return flow;
        }
        static void Hint(FlowLayoutPanel panel, string text) { var l = Ui.Label(text, true); l.MaximumSize = new Size(730, 0); l.Margin = new Padding(0, 12, 0, 8); panel.Controls.Add(l); }
        static TableLayoutPanel RangeTable(FlowLayoutPanel panel) {
            var table = Ui.Table(3); table.Dock = DockStyle.None; table.AutoSize = true; table.Width = 730;
            table.ColumnStyles[0].Width = 64; table.ColumnStyles[1].Width = table.ColumnStyles[2].Width = 18;
            table.Controls.Add(Ui.Label("Параметр / включить"), 0, 0); table.Controls.Add(Ui.Label("От"), 1, 0); table.Controls.Add(Ui.Label("До"), 2, 0); table.RowCount = 1;
            panel.Controls.Add(table); return table;
        }
        void Add(TableLayoutPanel table, string text, Func<ProcessingSettings, ProcessingParameter> get, double min, double max) {
            int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var p = get(Settings); var controls = new RowControls { Get = get, Enabled = new CheckBox { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 10, 9) }, Min = Ui.Number(p.Values.Min, min, max), Max = Ui.Number(p.Values.Max, min, max) };
            controls.Min.Margin = controls.Max.Margin = new Padding(0, 6, 10, 6);
            controls.Enabled.CheckedChanged += (a, b) => controls.Min.Enabled = controls.Max.Enabled = controls.Enabled.Checked;
            table.Controls.Add(controls.Enabled, 0, row); table.Controls.Add(controls.Min, 1, row); table.Controls.Add(controls.Max, 2, row); rows.Add(controls);
        }
        void Detail(TableLayoutPanel table, string text, string key, Range range, double min, double max) {
            int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var a = Ui.Number(range.Min, min, max); var b = Ui.Number(range.Max, min, max);
            a.Margin = b.Margin = new Padding(0, 6, 10, 6); var label = Ui.Label(text); label.Anchor = AnchorStyles.Left;
            table.Controls.Add(label, 0, row); table.Controls.Add(a, 1, row); table.Controls.Add(b, 2, row); details[key] = new[] { a, b };
        }
        void Safe(Action action) { try { action(); } catch (Exception e) { Ui.Error(this, e); } }
        Range DetailValue(string key) { return new Range((double)details[key][0].Value, (double)details[key][1].Value); }
        void Read() {
            foreach (var row in rows) { var p = row.Get(Settings); p.Enabled = row.Enabled.Checked; p.Values = new Range((double)row.Min.Value, (double)row.Max.Value); }
            Settings.GridCell = DetailValue("grid"); Settings.ZoomPeriod = DetailValue("zoom"); Settings.ModulationFrequency = DetailValue("modulation");
            Settings.Mirror = mirror.Checked; Settings.DeviceMetadata = device.Checked; Settings.CaptureDateEnabled = captureDate.Checked;
            Settings.CaptureDaysBack = (int)days.Value; Settings.RandomizeCaptureTime = randomTime.Checked;
            Settings.Brands = brands.Where(p => p.Value.Checked).Select(p => p.Key).ToArray(); Settings.Validate();
        }
        void LoadValues() {
            foreach (var row in rows) { var p = row.Get(Settings); row.Min.Value = (decimal)p.Values.Min; row.Max.Value = (decimal)p.Values.Max; row.Enabled.Checked = p.Enabled; row.Min.Enabled = row.Max.Enabled = p.Enabled; }
            Put("grid", Settings.GridCell); Put("zoom", Settings.ZoomPeriod); Put("modulation", Settings.ModulationFrequency);
            mirror.Checked = Settings.Mirror; device.Checked = Settings.DeviceMetadata; captureDate.Checked = Settings.CaptureDateEnabled;
            randomTime.Checked = Settings.RandomizeCaptureTime; days.Value = Settings.CaptureDaysBack;
            foreach (var item in brands) { item.Value.Checked = Settings.Brands.Contains(item.Key); item.Value.Enabled = device.Checked; }
            days.Enabled = randomTime.Enabled = captureDate.Checked; preview.Text = "Нажми «Прокрутить пример» для просмотра записи.";
        }
        void Put(string key, Range range) { details[key][0].Value = (decimal)range.Min; details[key][1].Value = (decimal)range.Max; }
        void Preview() {
            Read(); var p = new Profile(); ProcessingEngine.Apply(p, Settings, new Media { Duration = 60 }, random, DateTime.Now);
            var sample = p.Processing;
            preview.Text = "Brand: " + (sample == null || sample.Brand == "" ? "без замены" : sample.Brand) + "\nModel: " + (sample == null || sample.Model == "" ? "без замены" : sample.Model) + "\nSoftware: " + (sample == null || sample.Software == "" ? "без замены" : sample.Software) + "\nDate: " + (sample != null && sample.HasCaptureDate ? sample.CaptureDate.ToString("yyyy-MM-dd HH:mm:ss") : "прежний режим дат");
        }
        void SavePreset() { Safe(() => { Read(); using (var dialog = new SaveFileDialog { Filter = "Пресет обработки|*.xml", DefaultExt = "xml", FileName = "VideoBatch-effects.xml" }) if (dialog.ShowDialog(this) == DialogResult.OK) ProcessingPreset.Save(dialog.FileName, Settings); }); }
        void LoadPreset() { Safe(() => { using (var dialog = new OpenFileDialog { Filter = "Пресет обработки|*.xml" }) if (dialog.ShowDialog(this) == DialogResult.OK) { var loaded = ProcessingPreset.Load(dialog.FileName); Settings = loaded; LoadValues(); } }); }
    }
}
