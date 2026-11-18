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
        TextBox software;
        ComboBox colorPreset;
        TabControl tabs;
        readonly Random random = new Random();
        readonly bool preserveTimeline;
        ShortDialog shortExport;
        ProfilesDialog longExport;
        NumericUpDown musicVolume;
        public ShortSettings ExportRanges;
        public Profile[] ExportProfiles;
        public bool ExportAutomatic;
        public double MusicVolumeDb;

        public ProcessingDialog(ProcessingSettings source, bool metadataOnly = false, bool assemblyOutput = false, bool preserveTimeline = false, Preferences exportSettings = null) : base(metadataOnly ? "Метаданные роликов" : "Параметры и эффекты", 690) {
            this.preserveTimeline = preserveTimeline;
            ClientSize = new Size(850, 690); MinimumSize = new Size(800, 580);
            Settings = Store.Clone(source ?? new ProcessingSettings()); Settings.Validate();
            var layout = Ui.Table(); layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Body.Controls.Add(layout);
            var hint = Ui.Label(metadataOnly ? "Метки записываются внутрь ролика. Производитель и модель выбираются заново для каждого результата." : "Для каждого результата выбираются новые значения между «от» и «до». Выключенные параметры сохраняют прежнюю обработку. Панель общая для коротких и длинных видео.", true);
            hint.MaximumSize = new Size(770, 0); layout.Controls.Add(hint, 0, 0);
            var buttons = Ui.Flow(); buttons.WrapContents = true; buttons.Margin = new Padding(0, 0, 0, 12);
            if (!metadataOnly) buttons.Controls.Add(Ui.Button("Случайные настройки", () => Safe(() => { Read(); Settings.RandomizeEnabledRanges(random); LoadValues(); })));
            buttons.Controls.Add(Ui.Button("Сохранить пресет", SavePreset)); buttons.Controls.Add(Ui.Button("Загрузить пресет", LoadPreset));
            buttons.Controls.Add(Ui.Button("Выключить всё", () => { foreach (var row in rows) row.Enabled.Checked = false; mirror.Checked = device.Checked = captureDate.Checked = false; if (software!=null) software.Text=""; }));
            layout.Controls.Add(buttons, 0, 1);
            tabs = new TabControl { Dock = DockStyle.Fill }; layout.Controls.Add(tabs, 0, 2);
            var filters = Page(tabs, "Фильтры"); var ft = RangeTable(filters);
            Add(ft, "Обрезка с каждой стороны, %", s => s.Crop, 0, 15);
            if (!assemblyOutput) {
                Add(ft, "Подрезка по времени — суммарно, %", s => s.Trim, 0, 15);
                Add(ft, "Поворот, °", s => s.Rotation, -180, 180);
            }
            if (!preserveTimeline) Add(ft, "Скорость · 100 = исходная, %", s => s.Speed, 50, 200);
            else {
                Settings.Speed.Enabled = Settings.Trim.Enabled = Settings.Tempo.Enabled = false;
                Hint(filters, "Таймкоды перевода зафиксированы: скорость видео, подрезка и независимый темп аудио недоступны.");
            }
            Add(ft, "Яркость · 0 = исходная, %", s => s.Brightness, -50, 50);
            Add(ft, "Контраст · 100 = исходный, %", s => s.Contrast, 50, 150);
            Add(ft, "Резкость · 0 = исходная, %", s => s.Sharpness, -50, 50);
            Add(ft, "Зернистость / шум, %", s => s.Noise, 0, 100);
            Add(ft, "Гамма · 100 = исходная, %", s=>s.Gamma,50,150);
            Add(ft, "Красный канал · сдвиг, %", s=>s.RedShift,-10,10);
            Add(ft, "Зелёный канал · сдвиг, %", s=>s.GreenShift,-10,10);
            Add(ft, "Синий канал · сдвиг, %", s=>s.BlueShift,-10,10);
            if (assemblyOutput) Hint(filters, "Обработка готового ролика. Подрезка начала/конца и повороты исключены. Значения эффектов выбираются заново для каждого результата.");
            else Hint(filters, "Подрезка случайно распределяется между началом и концом. На горизонтальных роликах поворот не больше ±0,2° (едва заметный наклон, без больших чёрных углов). Включённые обрезка, скорость, яркость и контраст заменяют соответствующие значения прежних настроек.");
            var audio = Page(tabs, "Аудио");
            if(exportSettings!=null) {
                var musicFields=Ui.Fields();musicFields.Dock=DockStyle.None;musicFields.Width=730;
                musicVolume=Ui.Number(exportSettings.Shorts?exportSettings.Ranges.Volume:exportSettings.BackgroundDb,exportSettings.Shorts?0:-60,exportSettings.Shorts?150:-.1,2);
                Ui.Row(musicFields,exportSettings.Shorts?"Громкость музыки, %":"Громкость фоновой музыки, дБ",musicVolume);audio.Controls.Add(musicFields);
                Hint(audio,exportSettings.Shorts?"Музыка заменяет исходный звук. Громкость ниже применяется к итоговому звуку после уровня музыки.":"Уровень фоновой музыки задаётся отдельно от громкости итогового звука.");
            }
            var at = RangeTable(audio);
            Add(at, "Громкость · 100 = текущая, %", s => s.Volume, 0, 200);
            Add(at, "Высота тона · 100 = исходная, %", s => s.Pitch, 50, 200);
            if (!assemblyOutput) Add(at, "Темп аудио · 100 = исходный, %", s => s.Tempo, 50, 200);
            Add(at, "Всплески громкости — глубина, %", s => s.AudioModulation, 0, 50);
            Detail(at, "Частота всплесков, Гц", "modulation", Settings.ModulationFrequency, .1, 10);
            Add(at,"Короткое эхо (aecho), %",s=>s.Reverb,0,10);
            Add(at,"EQ · низкие (120 Гц), дБ",s=>s.EqLow,-12,12);
            Add(at,"EQ · средние (1 кГц), дБ",s=>s.EqMid,-12,12);
            Add(at,"EQ · высокие (8 кГц), дБ",s=>s.EqHigh,-12,12);
            Add(at,"Lowpass · верхняя граница, Гц",s=>s.Lowpass,8000,22000);
            Add(at,"Highpass · нижняя граница, Гц",s=>s.Highpass,10,200);
            Add(at,"Flanger · примесь эффекта, %",s=>s.Flanger,0,10);
            Hint(audio, assemblyOutput
                ? "Тон меняется без изменения скорости речи. Независимый темп аудио в сборке отключён, чтобы сохранять синхронность с видео. Эффекты этой вкладки применяются к итоговому звуку, включая музыку и озвучку. Громкость умножается на текущую; усиление ограничивается от перегрузки."
                : "Тон меняется с компенсацией темпа. Темп меняется без изменения тона. Эти эффекты применяются к итоговому звуку, включая музыку и озвучку. После изменения темпа звук обрезается или дополняется тишиной до длины видео. Громкость умножается на текущую; усиление ограничивается от перегрузки.");
            var effects = Page(tabs, "Видеоэффекты"); var et = RangeTable(effects);
            Add(et, "Затемнение в начале (Fade In), с", s => s.FadeIn, 0, 10);
            Add(et, "Затемнение в конце (Fade Out), с", s => s.FadeOut, 0, 10);
            mirror = new CheckBox { Text = "Отзеркаливание по горизонтали", AutoSize = true, Margin = new Padding(0, 10, 0, 10) };
            effects.Controls.Add(mirror);
            var extra = RangeTable(effects);
            Add(extra, "Полупрозрачная сетка — непрозрачность, %", s => s.GridOpacity, 0, 50);
            Detail(extra, "Размер ячейки сетки, px", "grid", Settings.GridCell, 8, 512);
            Add(extra, "Пыль / точки (кино), %", s => s.FilmDust, 0, 30);
            Add(extra, "Масштаб в рамке · 100 = без изменений, %", s => s.FrameScale, 94, 106);
            Add(extra, "Случайный плавный зум / кроп, %", s => s.Zoom, 100, 130);
            Detail(extra, "Период зума, с", "zoom", Settings.ZoomPeriod, .5, 30);
            Add(extra,"GEQ · колебания яркости, уровни",s=>s.Geq,0,5);
            Add(extra,"Tblend · примесь прошлого кадра, %",s=>s.TemporalBlend,0,20);
            Add(extra,"Кадр на размытом фоне, %",s=>s.BlurFrame,92,99);
            Add(extra,"LUT · сила цветового пресета, %",s=>s.ColorLut,0,100);
            colorPreset=Theme.MakeCombo(new[]{"warm","cool","film","vintage"});effects.Controls.Add(colorPreset);
            Hint(effects,"LUT: выбирается один пресет. Новые эффекты выключены по умолчанию; начальные диапазоны слабые. GEQ и tblend могут замедлять экспорт; tblend смешивает соседние кадры. Рамка с размытием сохраняет весь кадр, уменьшая его до указанного процента.");
            Hint(effects, "Пыль — редкие мерцающие точки (не равномерный шум). Масштаб в рамке: меньше 100% — чёрные поля, больше 100% — лёгкое увеличение с обрезкой. 100% зума = без плавного зума. Затемнения ограничены половиной длины ролика.");
            var metadata = Page(tabs, "Метаданные");
            device = new CheckBox { Text = "Случайные производитель и модель", AutoSize = true };
            metadata.Controls.Add(device);
            var brandPanel = new FlowLayoutPanel { AutoSize = true, Width = 730, WrapContents = true };
            foreach (string brand in ProcessingDevices.BrandNames) {
                var check = new CheckBox { Text = brand, AutoSize = true, Margin = new Padding(0, 8, 20, 8) };
                brands.Add(brand, check); brandPanel.Controls.Add(check);
            }
            metadata.Controls.Add(brandPanel);
            metadata.Controls.Add(Ui.Button("Все бренды", () => {foreach (var check in brands.Values) check.Checked=true;}));
            Hint(metadata, "Выбор только из отмеченных брендов. Модель выбирается из списка своего бренда. Всего 148 моделей. Это пользовательские шаблоны метаданных, которые не подтверждают устройство съёмки.");
            var softwareRow=Ui.Fields(); softwareRow.Width=730; softwareRow.Dock=DockStyle.None;
            software=new TextBox { Text=Settings.MetadataSoftware ?? "", Width=360, MaxLength=120 };
            Ui.Row(softwareRow,"Программа / версия (необязательно)",software); metadata.Controls.Add(softwareRow);
            Hint(metadata,"Software — твой текст, например название программы. Оставь пустым, если версия неизвестна. Метки не подтверждают отправку через Telegram или съёмку телефоном.");
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
            if (metadataOnly) { tabs.TabPages.Remove(filters.Parent as TabPage); tabs.TabPages.Remove(audio.Parent as TabPage); tabs.TabPages.Remove(effects.Parent as TabPage); }
            if(exportSettings!=null&&!metadataOnly) {
                var export=Page(tabs,"Экспорт");Control editor;
                if(exportSettings.Shorts){shortExport=new ShortDialog(exportSettings.Ranges,true);editor=shortExport.DetachBody();}
                else {longExport=new ProfilesDialog(exportSettings.Profiles,exportSettings.VideoCount,exportSettings.AutoProfiles);editor=longExport.DetachBody();}
                // Embed the established editors, preserving their values and auto mode.
                editor.Dock=DockStyle.Fill;var exportPage=(TabPage)export.Parent;exportPage.Controls.Remove(export);export.Dispose();exportPage.Controls.Add(editor);
            }
            LoadValues(); StandardButtons(Read);
            ApplyAsphalt(this);
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.ItemSize = new Size(140, 36);
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.DrawItem += DrawTab;
            if (metadataOnly) {
                tabs.Appearance=TabAppearance.FlatButtons; tabs.SizeMode=TabSizeMode.Fixed;
                tabs.ItemSize=new Size(1,1); // One page: the window title already names this section.
            }
        }
        void DrawTab(object sender, DrawItemEventArgs e) {
            bool active = e.Index == tabs.SelectedIndex;
            using (var brush = new SolidBrush(active ? Theme.Card : Theme.Background)) e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, Theme.FontBody, e.Bounds,
                active ? Theme.TextPrimary : Theme.TextSecondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (active) using (var pen = new Pen(Theme.Accent, 3)) e.Graphics.DrawLine(pen, e.Bounds.Left + 10, e.Bounds.Bottom - 2, e.Bounds.Right - 10, e.Bounds.Bottom - 2);
        }
        void ApplyAsphalt(Control control) {
            control.BackColor=control is TextBox || control is NumericUpDown ? Theme.Elevated : Theme.Background;
            control.ForeColor=control is Label ? Theme.TextSecondary : Theme.TextPrimary;
            if (control is TabPage page) page.UseVisualStyleBackColor=false;
            if(control is TabControl nested && !ReferenceEquals(nested,tabs)) {
                nested.DrawMode=TabDrawMode.OwnerDrawFixed;nested.ItemSize=new Size(140,32);nested.SizeMode=TabSizeMode.Fixed;
                nested.DrawItem+=(sender,e)=>{using(var brush=new SolidBrush(e.Index==nested.SelectedIndex?Theme.Card:Theme.Background))e.Graphics.FillRectangle(brush,e.Bounds);TextRenderer.DrawText(e.Graphics,nested.TabPages[e.Index].Text,Theme.FontBody,e.Bounds,Theme.TextPrimary,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);};
            }
            if (control is Button button) {
                button.UseVisualStyleBackColor=false; button.FlatStyle=FlatStyle.Flat;
                button.BackColor=ReferenceEquals(button,AcceptButton) ? Theme.Accent : Theme.Elevated;
                button.ForeColor=Theme.TextPrimary; button.FlatAppearance.BorderColor=Theme.Border;
                button.FlatAppearance.MouseOverBackColor=Theme.Hover;
            }
            foreach (Control child in control.Controls) ApplyAsphalt(child);
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
            Settings.ColorPreset=Convert.ToString(colorPreset.SelectedItem);
            Settings.Mirror = mirror.Checked; Settings.DeviceMetadata = device.Checked; Settings.CaptureDateEnabled = captureDate.Checked;
            Settings.MetadataSoftware=software.Text.Trim(); Settings.CaptureDaysBack = (int)days.Value; Settings.RandomizeCaptureTime = randomTime.Checked;
            Settings.Brands = brands.Where(p => p.Value.Checked).Select(p => p.Key).ToArray();
            if (preserveTimeline) Settings.Speed.Enabled = Settings.Trim.Enabled = Settings.Tempo.Enabled = false;
            Settings.Validate();
            if(shortExport!=null){shortExport.Commit();ExportRanges=shortExport.Settings;ExportRanges.Volume=(double)musicVolume.Value;}
            if(longExport!=null){longExport.Commit();ExportProfiles=longExport.Profiles;ExportAutomatic=longExport.Automatic;MusicVolumeDb=(double)musicVolume.Value;}
        }
        void LoadValues() {
            foreach (var row in rows) { var p = row.Get(Settings); row.Min.Value = (decimal)p.Values.Min; row.Max.Value = (decimal)p.Values.Max; row.Enabled.Checked = p.Enabled; row.Min.Enabled = row.Max.Enabled = p.Enabled; }
            Put("grid", Settings.GridCell); Put("zoom", Settings.ZoomPeriod); Put("modulation", Settings.ModulationFrequency);
            colorPreset.SelectedItem=Settings.ColorPreset;
            mirror.Checked = Settings.Mirror; device.Checked = Settings.DeviceMetadata; captureDate.Checked = Settings.CaptureDateEnabled;
            software.Text=Settings.MetadataSoftware ?? ""; randomTime.Checked = Settings.RandomizeCaptureTime; days.Value = Settings.CaptureDaysBack;
            foreach (var item in brands) { item.Value.Checked = Settings.Brands.Contains(item.Key); item.Value.Enabled = device.Checked; }
            days.Enabled = randomTime.Enabled = captureDate.Checked; preview.Text = "Нажми «Прокрутить пример» для просмотра записи.";
        }
        protected override void Dispose(bool disposing){if(disposing){shortExport?.Dispose();longExport?.Dispose();}base.Dispose(disposing);}
        public void SelectAudioTab(){foreach(TabPage page in tabs.TabPages)if(page.Text=="Аудио"){tabs.SelectedTab=page;break;}}
        public void SelectMetadataTab() { foreach(TabPage page in tabs.TabPages)if(page.Text=="Метаданные"){tabs.SelectedTab=page;break;} }
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
