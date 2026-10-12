using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class AssemblyTitleDialog : Form {
        public List<string> Titles;
        readonly TextBox input;
        readonly ListBox preview;
        readonly Label counter;

        public AssemblyTitleDialog(IList<string> current) {
            Text = "Заголовки для сборки";
            ClientSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterParent;
            Font = Theme.FontBody;
            BackColor = Theme.Background;
            ForeColor = Theme.TextPrimary;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(16) };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            body.Controls.Add(new Label {
                Text = "Каждый ролик получит случайный заголовок из списка. Вставьте фразы через запятую или с новой строки. Сохраняются с заглавной буквы у каждого слова.",
                AutoSize = true, MaximumSize = new Size(580, 0), ForeColor = Theme.TextSecondary, Margin = new Padding(0, 0, 0, 8)
            });
            input = new TextBox {
                Multiline = true, AcceptsReturn = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                Width = 580, Height = 220, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10)
            };
            if (current != null && current.Count > 0) input.Text = string.Join(", ", current);
            input.TextChanged += (s, e) => RefreshPreview();
            body.Controls.Add(input);

            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 8) };
            actions.Controls.Add(Theme.MakeButton("Загрузить ключи", ghost: true, action: LoadDefaults));
            body.Controls.Add(actions);

            counter = new Label { AutoSize = true, ForeColor = Theme.Accent, Margin = new Padding(0, 0, 0, 6) };
            body.Controls.Add(counter);
            preview = new ListBox { Width = 580, Height = 160, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };
            body.Controls.Add(preview);
            root.Controls.Add(body, 0, 0);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            bottom.Controls.Add(Theme.MakeButton("Сохранить", accent: true, action: Save));
            bottom.Controls.Add(Theme.MakeButton("Отмена", ghost: true, action: () => { DialogResult = DialogResult.Cancel; Close(); }));
            root.Controls.Add(bottom, 0, 1);
            RefreshPreview();
        }

        void LoadDefaults() {
            input.Text = string.Join(", ", AssemblyOutputTags.DefaultTitlePhrases.Select(AssemblyOutputTags.ToTitle));
        }

        void RefreshPreview() {
            try {
                var list = AssemblyOutputTags.ParseTitleInput(input.Text);
                preview.BeginUpdate(); preview.Items.Clear();
                foreach (var t in list.Take(80)) preview.Items.Add(t.Length > 96 ? t.Substring(0, 96) + "…" : t);
                if (list.Count > 80) preview.Items.Add("… ещё " + (list.Count - 80));
                preview.EndUpdate();
                counter.Text = list.Count == 0 ? "Список пуст — заголовки не меняются" : ("Заголовков: " + list.Count);
                counter.ForeColor = list.Count == 0 ? Theme.Warning : Theme.Accent;
            } catch (Exception e) {
                counter.Text = e.Message;
                counter.ForeColor = Theme.Warning;
            }
        }

        void Save() {
            Titles = AssemblyOutputTags.ParseTitleInput(input.Text);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
