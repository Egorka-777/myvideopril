using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class SubtitleStyleDialog {
        CheckBox randomEnabled;
        readonly List<Action> loadRandom = new List<Action>();
        WarmupSubtitleStyle randomPreview;
        void AddRandomFields(TableLayoutPanel fields) {
            randomEnabled = new CheckBox { Text = "Хаотичные субтитры", AutoSize = true, Name = "SubtitleRandomEnabled" };
            Add(fields, "Случайный стиль на ролик", randomEnabled);
            randomEnabled.CheckedChanged += (s, e) => { if (loading) return; Style.RandomStyle.Enabled = randomEnabled.Checked; randomPreview = null; UpdatePreview(); };
            Add(fields, "Один стиль на всё видео", new Label { Text = "Диапазоны от / до. Обычные настройки сохраняются.", AutoSize = true });
            RandomRange(fields, "Размер", "Size", 28, 150, o => o.SizeMin, o => o.SizeMax, (o, a, b) => { o.SizeMin = a; o.SizeMax = b; });
            RandomRange(fields, "Положение сверху, %", "Position", 10, 90, o => o.PositionMin, o => o.PositionMax, (o, a, b) => { o.PositionMin = a; o.PositionMax = b; });
            RandomRange(fields, "Слов в группе", "Words", 1, 6, o => o.WordsMin, o => o.WordsMax, (o, a, b) => { o.WordsMin = a; o.WordsMax = b; });
            RandomRange(fields, "Обводка", "Outline", 0, 10, o => o.OutlineMin, o => o.OutlineMax, (o, a, b) => { o.OutlineMin = a; o.OutlineMax = b; });
            RandomRange(fields, "Плотность плашки, %", "Opacity", 0, 100, o => o.OpacityMin, o => o.OpacityMax, (o, a, b) => { o.OpacityMin = a; o.OpacityMax = b; });
            RandomChoices(fields, "Допустимые шрифты", "Fonts", SubtitlePresets.Fonts, SubtitlePresets.Fonts, o => o.Fonts, (o, v) => o.Fonts = v);
            RandomChoices(fields, "Цвета текста и акцента", "Colors", new[] { "#FFFFFF", "#FFE84A", "#32F56B", "#65D9FF", "#FF8C42", "#FF78BE" }, new[] { "Белый", "Жёлтый", "Зелёный", "Голубой", "Оранжевый", "Розовый" }, o => o.Colors, (o, v) => o.Colors = v);
            RandomChoices(fields, "Допустимые анимации", "Motions", new[] { "none", "pop", "fade", "rise", "word-accent" }, new[] { "Без анимации", "Лёгкое увеличение", "Плавное появление", "Мягкий подъём", "Акцент на слове" }, o => o.Motions, (o, v) => o.Motions = v);
            RandomChoices(fields, "Допустимые плашки", "Plates", new[] { "none", "rounded", "solid" }, new[] { "Без плашки", "Скруглённая", "Прямая" }, o => o.Plates, (o, v) => o.Plates = v);
            RandomChoices(fields, "Выделение слов", "Highlights", new[] { "none", "active", "progressive" }, new[] { "Без выделения", "Текущее слово", "Караоке" }, o => o.Highlights, (o, v) => o.Highlights = v);
            Add(fields, "Пример комбинации", Theme.MakeButton("Другой вариант", ghost: true, action: () => { if (!ValidateRandomChoices()) return; randomPreview = null; UpdatePreview(); }));
        }
        void RandomRange(TableLayoutPanel fields, string label, string name, int lower, int upper, Func<SubtitleRandomSettings, int> min, Func<SubtitleRandomSettings, int> max, Action<SubtitleRandomSettings, int, int> write) {
            var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var a = Number(lower, upper); var b = Number(lower, upper); a.Width = b.Width = 64;
            a.Name = "SubtitleRandom" + name + "Min"; b.Name = "SubtitleRandom" + name + "Max";
            panel.Controls.Add(a); panel.Controls.Add(new Label { Text = "—", AutoSize = true }); panel.Controls.Add(b); Add(fields, label + " (от / до)", panel);
            loadRandom.Add(() => { a.Value = Math.Max(lower, Math.Min(upper, min(Style.RandomStyle))); b.Value = Math.Max(lower, Math.Min(upper, max(Style.RandomStyle))); });
            EventHandler changed = (s, e) => { if (loading) return; write(Style.RandomStyle, (int)a.Value, (int)b.Value); randomPreview = null; UpdatePreview(); };
            a.ValueChanged += changed; b.ValueChanged += changed;
        }
        void RandomChoices(TableLayoutPanel fields, string label, string name, string[] values, string[] labels, Func<SubtitleRandomSettings, string[]> read, Action<SubtitleRandomSettings, string[]> write) {
            var list = new CheckedListBox { Name = "SubtitleRandom" + name, CheckOnClick = true, Height = Math.Min(120, values.Length * 21 + 6), BackColor = Theme.Card, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.FixedSingle };
            list.Items.AddRange(labels); Add(fields, label, list);
            loadRandom.Add(() => { for (int i = 0; i < values.Length; i++) list.SetItemChecked(i, (read(Style.RandomStyle) ?? new string[0]).Contains(values[i])); });
            list.ItemCheck += (s, e) => {
                if (loading) return;
                write(Style.RandomStyle, values.Where((v, i) => i == e.Index ? e.NewValue == CheckState.Checked : list.GetItemChecked(i)).ToArray());
                randomPreview = null; UpdatePreview();
            };
        }
        void LoadRandomFields() {
            randomEnabled.Checked = Style.RandomStyle.Enabled;
            foreach (var load in loadRandom) load(); randomPreview = null;
        }
        bool HasRandomChoices() {
            var o = Style.RandomStyle;
            return new[] { o.Fonts, o.Colors, o.Motions, o.Plates, o.Highlights }.All(v => v != null && v.Length > 0);
        }
        bool ValidateRandomChoices() {
            if (!Style.RandomStyle.Enabled || HasRandomChoices()) return true;
            MessageBox.Show(this, "Выберите хотя бы один вариант в каждом списке случайного стиля.", "Субтитры"); return false;
        }
    }
}
