using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class PlatformHubPanel : UserControl {
        public event Action<AppPlatform> PlatformChosen;

        public PlatformHubPanel() {
            Dock = DockStyle.Fill;
            BackColor = Theme.Background;
            var root = new TableLayoutPanel {
                Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1,
                BackColor = Theme.Background, Padding = new Padding(48, 40, 48, 40)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var title = new Label { Text = "VideoBatch", Font = new Font(Theme.FontPageTitle.FontFamily, 28f), ForeColor = Theme.TextPrimary, AutoSize = true };
            var sub = new Label {
                Text = "Выберите, с чем работаете. Интерфейс один — лишние разделы скрыты.",
                ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(720, 0), Margin = new Padding(0, 10, 0, 0)
            };
            var head = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            head.Controls.Add(title);
            head.Controls.Add(sub);
            root.Controls.Add(head, 0, 0);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Background, Margin = new Padding(0, 28, 0, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.Controls.Add(MakeCard("YouTube", "Загрузка и автопрогрев", AppPlatform.YouTube), 0, 0);
            grid.Controls.Add(MakeCard("TikTok", "Аккаунты и публикации", AppPlatform.TikTok), 1, 0);
            grid.Controls.Add(MakeCard("Telegram", "Аккаунты и приглашения", AppPlatform.Telegram), 0, 1);
            grid.Controls.Add(MakeCard("Видео и монтаж", "Обработка, студия, нарезчик", AppPlatform.Studio), 1, 1);
            root.Controls.Add(grid, 0, 1);
        }

        Panel MakeCard(string caption, string hint, AppPlatform platform) {
            var card = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Margin = new Padding(10), Cursor = Cursors.Hand, Padding = new Padding(20) };
            var t = new Label { Text = caption, Font = new Font(Theme.FontCardTitle.FontFamily, 14f, FontStyle.Bold), ForeColor = Theme.TextPrimary, AutoSize = true, Location = new Point(20, 20) };
            var h = new Label { Text = hint, ForeColor = Theme.TextSecondary, AutoSize = true, MaximumSize = new Size(320, 0), Location = new Point(20, 52) };
            card.Controls.Add(t);
            card.Controls.Add(h);
            void pick() => PlatformChosen?.Invoke(platform);
            foreach (Control c in new Control[] { card, t, h }) {
                c.Click += (s, e) => pick();
                if (c is Panel p) {
                    p.MouseEnter += (s, e) => p.BackColor = Theme.Elevated;
                    p.MouseLeave += (s, e) => p.BackColor = Theme.Card;
                }
            }
            return card;
        }
    }
}
