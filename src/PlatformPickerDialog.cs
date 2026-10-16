using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoBatch {
    internal sealed class PlatformPickerDialog : Form {
        public AppPlatform Selected { get; private set; } = AppPlatform.Unchosen;

        public PlatformPickerDialog() {
            Text = "VideoBatch";
            ClientSize = new Size(720, 420);
            MinimumSize = new Size(640, 380);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Theme.StyleShellForm(this);

            var root = new TableLayoutPanel {
                Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(28, 24, 28, 24),
                BackColor = Theme.Background
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var title = new Label {
                Text = "Выберите раздел",
                Font = Theme.FontPageTitle,
                ForeColor = Theme.TextPrimary,
                AutoSize = true,
                Dock = DockStyle.Top
            };
            var sub = new Label {
                Text = "Интерфейс один, но каждая соцсеть — отдельно: без смешивания Telegram, YouTube и TikTok.",
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                MaximumSize = new Size(640, 0),
                Margin = new Padding(0, 8, 0, 0)
            };
            var head = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
            head.Controls.Add(title);
            head.Controls.Add(sub);
            root.Controls.Add(head, 0, 0);

            var grid = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Background,
                Margin = new Padding(0, 20, 0, 12)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.Controls.Add(MakeCard("YouTube", "Загрузка, прогрев Shorts, просмотры", AppPlatform.YouTube), 0, 0);
            grid.Controls.Add(MakeCard("TikTok", "Аккаунты и загрузка", AppPlatform.TikTok), 1, 0);
            grid.Controls.Add(MakeCard("Telegram", "Аккаунты, прокси, приглашения", AppPlatform.Telegram), 0, 1);
            grid.Controls.Add(MakeCard("Видео и монтаж", "Обработка, студия, нарезчик", AppPlatform.Studio), 1, 1);
            root.Controls.Add(grid, 0, 1);

            root.Controls.Add(new Label {
                Text = "Сменить раздел можно позже кнопкой «Раздел» в верхней панели.",
                ForeColor = Theme.TextMuted,
                AutoSize = true
            }, 0, 2);
        }

        Panel MakeCard(string caption, string hint, AppPlatform platform) {
            var card = new Panel {
                Dock = DockStyle.Fill,
                BackColor = Theme.Card,
                Margin = new Padding(8),
                Cursor = Cursors.Hand,
                Padding = new Padding(16)
            };
            var t = new Label { Text = caption, Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Location = new Point(16, 16) };
            var h = new Label {
                Text = hint, ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(280, 0),
                Location = new Point(16, 44)
            };
            card.Controls.Add(t);
            card.Controls.Add(h);
            void pick() { Selected = platform; DialogResult = DialogResult.OK; Close(); }
            card.Click += (s, e) => pick();
            t.Click += (s, e) => pick();
            h.Click += (s, e) => pick();
            card.MouseEnter += (s, e) => card.BackColor = Theme.Elevated;
            card.MouseLeave += (s, e) => card.BackColor = Theme.Card;
            return card;
        }
    }
}
