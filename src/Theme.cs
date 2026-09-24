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
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 8, 0);
            grid.DefaultCellStyle.BackColor = Card;
            grid.DefaultCellStyle.ForeColor = TextPrimary;
            grid.DefaultCellStyle.SelectionBackColor = Selected;
            grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
            grid.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);
            grid.RowHeadersVisible = false;
            grid.RowTemplate.Height = 52;
            grid.ColumnHeadersHeight = 34;
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
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

        public const int HeroToggleWidth = 76;
        public const int HeroToggleHeight = 36;

        public static Button MakeHeroToggle(string text, bool active, Action action) {
            var b = new Button {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                AutoSize = false,
                Size = new Size(HeroToggleWidth, HeroToggleHeight),
                Cursor = Cursors.Hand,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                TextAlign = ContentAlignment.MiddleCenter
            };
            b.FlatAppearance.BorderSize = 0;
            ApplyHeroToggleStyle(b, active);
            if (action != null) b.Click += (s, e) => action();
            return b;
        }

        public static void ApplyHeroToggleStyle(Button b, bool active) {
            b.BackColor = active ? Accent : ColorFromHex("#343434");
            b.ForeColor = active ? Color.White : TextPrimary;
            b.Font = new Font(FontBody.FontFamily, 10.5f, active ? FontStyle.Bold : FontStyle.Regular);
        }

        /// <summary>Large context switcher — RU/EN, Shorts/Long at top of workspace.</summary>
        public static Control MakeHeroSegmentGroup(string caption, params Button[] options) {
            int count = options?.Length ?? 0;
            if (count == 0) count = 1;
            const int gap = 2;
            const int pad = 3;
            int shellW = pad * 2 + count * HeroToggleWidth + Math.Max(0, count - 1) * gap;
            int shellH = pad * 2 + HeroToggleHeight;

            var row = new TableLayoutPanel {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = string.IsNullOrWhiteSpace(caption) ? 1 : 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 18, 0)
            };
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, shellH));
            int shellCol = 0;
            if (!string.IsNullOrWhiteSpace(caption)) {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.Controls.Add(new Label {
                    Text = caption,
                    ForeColor = TextSecondary,
                    AutoSize = true,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(0, 0, 10, 0),
                    Font = new Font(FontBody.FontFamily, 10f, FontStyle.Bold)
                }, 0, 0);
                shellCol = 1;
            } else {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            }

            var shell = new Panel {
                Size = new Size(shellW, shellH),
                BackColor = Elevated,
                Margin = Padding.Empty
            };
            if (options != null)
                for (int i = 0; i < options.Length; i++) {
                    var btn = options[i];
                    btn.AutoSize = false;
                    btn.Size = new Size(HeroToggleWidth, HeroToggleHeight);
                    btn.Location = new Point(pad + i * (HeroToggleWidth + gap), pad);
                    btn.FlatAppearance.BorderSize = 0;
                    btn.Margin = Padding.Empty;
                    shell.Controls.Add(btn);
                }
            row.Controls.Add(shell, shellCol, 0);
            return row;
        }

        public static Panel MakeBadge(string text, bool accent = false) {
            var wrap = new Panel {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = accent ? ColorFromHex("#1A3D32") : Elevated,
                Padding = new Padding(12, 6, 12, 6),
                Margin = new Padding(0, 0, 8, 0)
            };
            wrap.Controls.Add(new Label {
                Text = text,
                AutoSize = true,
                ForeColor = accent ? Accent : TextPrimary,
                Font = new Font(FontBody.FontFamily, 10f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Margin = Padding.Empty
            });
            return wrap;
        }

        public static Panel MakeCountBadge(string text) => MakeBadge(text);

        public static Panel MakeAccentChip(string text) => MakeBadge(text, accent: true);

        public static void SetBadgeText(Control badge, string text) {
            if (badge is Panel panel && panel.Controls.Count > 0 && panel.Controls[0] is Label label)
                label.Text = text ?? "";
        }

        public static Button MakeIconButton(string text, Action action = null) {
            var b = new Button {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                AutoSize = false,
                Size = new Size(36, 36),
                MinimumSize = new Size(36, 36),
                MaximumSize = new Size(36, 36),
                Margin = new Padding(6, 0, 0, 0),
                Padding = Padding.Empty,
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                BackColor = Elevated,
                ForeColor = TextPrimary,
                Font = new Font(FontBody.FontFamily, 11f)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Hover;
            if (action != null) b.Click += (s, e) => action();
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
                RenderMode = ToolStripRenderMode.Professional,
                ShowImageMargin = false,
                Font = FontBody
            };
            m.Renderer = new DarkMenuRenderer();
            return m;
        }

        sealed class DarkMenuRenderer : ToolStripProfessionalRenderer {
            public DarkMenuRenderer() : base(new DarkMenuColors()) { }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
                e.TextColor = e.Item.Selected ? Color.White : TextPrimary;
                base.OnRenderItemText(e);
            }
        }

        sealed class DarkMenuColors : ProfessionalColorTable {
            public override Color MenuItemSelected => ColorFromHex("#0E8F6F");
            public override Color MenuItemSelectedGradientBegin => ColorFromHex("#0E8F6F");
            public override Color MenuItemSelectedGradientEnd => ColorFromHex("#0E8F6F");
            public override Color MenuItemBorder => Border;
            public override Color ToolStripDropDownBackground => Elevated;
            public override Color ImageMarginGradientBegin => Elevated;
            public override Color ImageMarginGradientMiddle => Elevated;
            public override Color ImageMarginGradientEnd => Elevated;
            public override Color MenuBorder => Border;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
        }

        /// <summary>Card-style filter strip (market, type, search).</summary>
        public static Panel MakeFilterCard() {
            return new Panel {
                Dock = DockStyle.Top,
                AutoSize = true,
                BackColor = Card,
                Padding = new Padding(14, 12, 14, 12),
                Margin = new Padding(0, 0, 0, 10)
            };
        }

        /// <summary>Segmented toggle group (RU/EN, Shorts/Long).</summary>
        public static Panel MakeSegmentGroup(string caption, params Button[] options) {
            var wrap = new FlowLayoutPanel {
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0, 0, 20, 0),
                BackColor = Color.Transparent
            };
            if (!string.IsNullOrWhiteSpace(caption))
                wrap.Controls.Add(new Label {
                    Text = caption,
                    ForeColor = TextMuted,
                    AutoSize = true,
                    Margin = new Padding(0, 7, 8, 0),
                    Font = FontSmall
                });
            var shell = new Panel {
                BackColor = Elevated,
                AutoSize = true,
                Padding = new Padding(3),
                Margin = new Padding(0)
            };
            var inner = new FlowLayoutPanel {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Elevated,
                Margin = Padding.Empty
            };
            foreach (var btn in options) {
                btn.FlatAppearance.BorderSize = 0;
                btn.Margin = new Padding(1, 0, 1, 0);
                btn.MinimumSize = new Size(58, 32);
                btn.Padding = new Padding(8, 4, 8, 4);
                inner.Controls.Add(btn);
            }
            shell.Controls.Add(inner);
            wrap.Controls.Add(shell);
            return wrap;
        }

        public static Panel MakeChip(string text) {
            var wrap = MakeBadge(text);
            if (wrap.Controls.Count > 0 && wrap.Controls[0] is Label label) {
                label.ForeColor = TextSecondary;
                label.Font = FontSmall;
            }
            wrap.Margin = new Padding(0, 0, 8, 0);
            return wrap;
        }

        public static Panel MakeWorkspaceHeader(string subtitle, params Control[] rightActions) {
            var bar = new TableLayoutPanel {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Background,
                Margin = new Padding(0, 0, 0, 8)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label {
                Text = subtitle,
                AutoSize = true,
                ForeColor = TextPrimary,
                Font = FontCardTitle,
                Margin = new Padding(0, 4, 0, 0),
                Anchor = AnchorStyles.Left
            };
            var actions = new FlowLayoutPanel {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = Background,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0)
            };
            if (rightActions != null)
                foreach (var c in rightActions)
                    if (c != null) actions.Controls.Add(c);
            bar.Controls.Add(title, 0, 0);
            bar.Controls.Add(actions, 1, 0);
            return bar;
        }

        public static Button MakeMenuButton(string text, params (string label, Action action)[] items) {
            var menu = MakeContextMenu();
            if (items != null)
                foreach (var item in items)
                    if (item.action != null)
                        menu.Items.Add(item.label, null, (s, e) => item.action());
            var b = MakeButton(text + "  ▾", ghost: true);
            b.Click += (s, e) => menu.Show(b, new Point(0, b.Height));
            return b;
        }

        public static Panel MakeBottomBar(params (Control control, bool primary)[] items) {
            var bar = new Panel {
                Dock = DockStyle.Top,
                AutoSize = true,
                BackColor = TopBar,
                Padding = new Padding(12, 10, 12, 10),
                Margin = new Padding(0, 10, 0, 0)
            };
            var flow = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                BackColor = TopBar
            };
            if (items != null)
                foreach (var item in items) {
                    if (item.control == null) continue;
                    if (item.primary) {
                        item.control.MinimumSize = new Size(140, 38);
                        item.control.Margin = new Padding(0, 0, 10, 0);
                        item.control.Font = new Font(FontBody.FontFamily, FontBody.Size, FontStyle.Bold);
                    } else {
                        item.control.Margin = new Padding(0, 0, 8, 0);
                    }
                    flow.Controls.Add(item.control);
                }
            bar.Controls.Add(flow);
            return bar;
        }

        public static Panel MakeLogSection(RichTextBox log) {
            var wrap = new Panel {
                Dock = DockStyle.Fill,
                BackColor = Card,
                Padding = new Padding(12, 8, 12, 8),
                Margin = new Padding(0, 8, 0, 0)
            };
            var cap = new Label {
                Text = "Журнал",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = TextMuted,
                Font = FontSmall
            };
            log.Dock = DockStyle.Fill;
            log.BorderStyle = BorderStyle.None;
            wrap.Controls.Add(log);
            wrap.Controls.Add(cap);
            return wrap;
        }
    }
}
