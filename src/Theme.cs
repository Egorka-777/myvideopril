using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoBatch {
    /// <summary>Centralized asphalt dark theme (ChatGPT-like contrast, no blue base).</summary>
    public static class Theme {
        public static readonly Color Background = ColorFromHex("#171717");
        public static readonly Color Sidebar = ColorFromHex("#1F1F1F");
        public static readonly Color TopBar = ColorFromHex("#1F1F1F");
        public static readonly Color Card = ColorFromHex("#242424");
        public static readonly Color Elevated = ColorFromHex("#2B2B2B");
        public static readonly Color Hover = ColorFromHex("#303030");
        public static readonly Color Selected = ColorFromHex("#343434");
        public static readonly Color Border = ColorFromHex("#3A3A3A");
        public static readonly Color TextPrimary = ColorFromHex("#F2F2F2");
        public static readonly Color TextSecondary = ColorFromHex("#A6A6A6");
        public static readonly Color TextMuted = ColorFromHex("#737373");
        public static readonly Color Accent = ColorFromHex("#10A37F");
        public static readonly Color Success = ColorFromHex("#22C55E");
        public static readonly Color Warning = ColorFromHex("#F59E0B");
        public static readonly Color Error = ColorFromHex("#EF4444");
        public static readonly Color Info = ColorFromHex("#60A5FA");

        public static readonly Font FontBody = new Font("Segoe UI", 9.75f);
        public static readonly Font FontSmall = new Font("Segoe UI", 9.25f);
        public static readonly Font FontCardTitle = new Font("Segoe UI", 11.25f, FontStyle.Bold);
        public static readonly Font FontPageTitle = new Font("Segoe UI", 19f, FontStyle.Regular);
        public static readonly Font FontNav = new Font("Segoe UI", 9.75f);

        public const int TopBarHeight = 56;
        public const int SidebarExpanded = 220;
        public const int SidebarCollapsed = 64;
        public const int ContentPadding = 18;
        public const int CardRadius = 9;

        public static Color ColorFromHex(string hex) {
            hex = (hex ?? "").TrimStart('#');
            if (hex.Length == 6) hex = "FF" + hex;
            int argb = int.Parse(hex, System.Globalization.NumberStyles.HexNumber);
            return Color.FromArgb((argb >> 24) & 0xFF, (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
        }

        public static void StyleShellForm(Form form) {
            form.BackColor = Background;
            form.ForeColor = TextPrimary;
            form.Font = FontBody;
            form.MinimumSize = new Size(1100, 700);
        }

        public static Button MakeButton(string text, bool accent = false, bool ghost = false, Action action = null) {
            var b = new Button {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                AutoSize = true,
                MinimumSize = new Size(72, 34),
                Padding = new Padding(12, 6, 12, 6),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0),
                Font = FontBody
            };
            b.FlatAppearance.BorderSize = ghost ? 1 : 0;
            b.FlatAppearance.BorderColor = Border;
            if (accent) {
                b.BackColor = Accent;
                b.ForeColor = Color.White;
                b.FlatAppearance.MouseOverBackColor = ColorFromHex("#0E8F6F");
            } else if (ghost) {
                b.BackColor = Elevated;
                b.ForeColor = TextPrimary;
                b.FlatAppearance.MouseOverBackColor = Hover;
            } else {
                b.BackColor = Elevated;
                b.ForeColor = TextPrimary;
                b.FlatAppearance.MouseOverBackColor = Hover;
            }
            if (action != null) b.Click += (s, e) => action();
            return b;
        }

        public static Panel MakeCard() {
            return new Panel {
                BackColor = Card,
                Padding = new Padding(16),
                Margin = new Padding(0, 0, 12, 12)
            };
        }

        public static void StyleGrid(DataGridView grid) {
            grid.BackgroundColor = Card;
            grid.GridColor = Border;
            grid.BorderStyle = BorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Elevated;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextSecondary;
            grid.ColumnHeadersDefaultCellStyle.Font = FontSmall;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Elevated;
            grid.DefaultCellStyle.BackColor = Card;
            grid.DefaultCellStyle.ForeColor = TextPrimary;
            grid.DefaultCellStyle.SelectionBackColor = Selected;
            grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
            grid.RowHeadersVisible = false;
            grid.AlternatingRowsDefaultCellStyle.BackColor = ColorFromHex("#212121");
        }

        public static TextBox MakeSearchBox() {
            return new TextBox {
                BackColor = Elevated,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = FontBody
            };
        }

        public static ComboBox MakeCombo(string[] items) {
            var c = new ComboBox {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Elevated,
                ForeColor = TextPrimary,
                FlatStyle = FlatStyle.Flat,
                Font = FontBody,
                Width = 130
            };
            if (items != null) c.Items.AddRange(items);
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            return c;
        }

        public static RichTextBox MakeLogBox() {
            return new RichTextBox {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Card,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                DetectUrls = true
            };
        }

        public static FlowLayoutPanel MakeToolbar() {
            return new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = true,
                Padding = new Padding(0, 0, 0, 8),
                BackColor = Background
            };
        }

        public static Button MakeToggle(string text, bool active, Action action) {
            var b = MakeButton(text, accent: active, ghost: !active, action: action);
            b.MinimumSize = new Size(56, 32);
            return b;
        }

        public static void StyleCheckBox(CheckBox box) {
            box.ForeColor = TextPrimary;
            box.BackColor = Color.Transparent;
        }

        public static ContextMenuStrip MakeContextMenu() {
            var m = new ContextMenuStrip {
                BackColor = Elevated,
                ForeColor = TextPrimary,
                RenderMode = ToolStripRenderMode.System
            };
            return m;
        }
    }
}
