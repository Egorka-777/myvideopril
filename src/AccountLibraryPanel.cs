using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class AccountLibraryPanel : UserControl {
        readonly AccountLibraryStore store;
        readonly Preferences settings;
        readonly Action<AccountSourceLink> openSource;
        readonly ListBox list = new ListBox();
        readonly TextBox search = Theme.MakeSearchBox();
        readonly ComboBox platform = Theme.MakeCombo(new[] { "Все площадки", "YouTube", "TikTok" });
        readonly ComboBox language = Theme.MakeCombo(new[] { "Все языки", "RU", "EN" });
        readonly ComboBox status = Theme.MakeCombo(new[] { "Все статусы" }.Concat(AccountLibraryStore.Statuses).ToArray());
        readonly FlowLayoutPanel detail = new FlowLayoutPanel();
        readonly Label count = new Label();
        readonly Button add, import;
        readonly Timer clipboardTimer = new Timer { Interval = 45000 };
        readonly Timer revealTimer = new Timer { Interval = 60000 };
        readonly List<Action<bool>> secretFields = new List<Action<bool>>();
        List<AccountRecord> accounts = new List<AccountRecord>();
        string copiedSecret;
        bool rebuilding, revealed;
        string loadError = "";
        int unresolved;
        public AccountRecord SelectedAccount => list.SelectedItem as AccountRecord;
        public AccountLibraryPanel(AccountLibraryStore accountStore = null, Preferences prefs = null, Action<AccountSourceLink> open = null) {
            store = accountStore ?? new AccountLibraryStore();
            settings = prefs; openSource = open;
            Name = "AccountLibrary"; BackColor = Theme.Background; ForeColor = Theme.TextPrimary; Font = Theme.FontBody;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);
            var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            count.Text = "Все каналы под рукой"; count.ForeColor = Theme.TextSecondary; count.Dock = DockStyle.Fill; count.TextAlign = ContentAlignment.MiddleLeft;
            head.Controls.Add(count, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
            add = Theme.MakeButton("+ Аккаунт", true, action: () => Edit(null)); add.Name = "AddAccount";
            import = Theme.MakeButton("Из Excel", ghost: true, action: ImportExcel); import.Name = "ImportAccounts";
            actions.Controls.Add(add); actions.Controls.Add(import);
            actions.Controls.Add(Theme.MakeIconButton("↻", RefreshData));
            head.Controls.Add(actions, 1, 0); root.Controls.Add(head, 0, 0);
            var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
            search.Name = "AccountSearch"; search.AccessibleName = "Поиск по названию, стране и заметкам";
            search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 6, 12, 8);
            // Cue banner avoids keeping explanatory text in the actual query.
            search.HandleCreated += (s, e) => SendMessage(search.Handle, 0x1501, new IntPtr(1), "Найти аккаунт…");
            filters.Controls.Add(search, 0, 0);
            var boxes = new[] { platform, language, status };
            for (int i = 0; i < boxes.Length; i++) { boxes[i].Dock = DockStyle.Fill; boxes[i].Margin = new Padding(0, 6, 8, 8); filters.Controls.Add(boxes[i], i + 1, 0); boxes[i].SelectedIndexChanged += (s, e) => Filter(); }
            search.TextChanged += (s, e) => Filter(); root.Controls.Add(filters, 0, 1);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 305)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            list.Name = "AccountList"; list.Dock = DockStyle.Fill; list.BackColor = Theme.Sidebar; list.ForeColor = Theme.TextPrimary;
            list.BorderStyle = BorderStyle.None; list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 78; list.IntegralHeight = false;
            list.Margin = new Padding(0, 0, 16, 0); list.DrawItem += DrawAccount;
            list.SelectedIndexChanged += (s, e) => { if (!rebuilding) ShowCard(); };
            body.Controls.Add(list, 0, 0);
            detail.Name = "AccountCard"; detail.Dock = DockStyle.Fill; detail.AutoScroll = true; detail.FlowDirection = FlowDirection.TopDown; detail.WrapContents = false;
            detail.Margin = Padding.Empty; detail.SizeChanged += (s, e) => FitCards();
            body.Controls.Add(detail, 1, 0); root.Controls.Add(body, 0, 2);
            clipboardTimer.Tick += (s, e) => ClearCopiedSecret(); revealTimer.Tick += (s, e) => HideSecrets();
            VisibleChanged += (s, e) => { if (!Visible) HideSecrets(); };
            RefreshData();
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, string lParam);
        public void ApplySearch(string query) { search.Text = query ?? ""; }
        public void RefreshData() {
            try {
                unresolved = settings == null ? 0 : AccountLibrarySync.Sync(settings, store).Unresolved;
                accounts = store.Load(); loadError = ""; add.Enabled = import.Enabled = true;
            }
            catch (Exception e) { accounts.Clear(); loadError = e.Message; add.Enabled = import.Enabled = false; }
            Filter();
        }
        void Filter() {
            string id = SelectedAccount?.Id;
            rebuilding = true;
            list.BeginUpdate(); list.Items.Clear();
            var visible = accounts.Where(a => (platform.SelectedIndex <= 0 || a.Platform == platform.Text)
                && (language.SelectedIndex <= 0 || AccountLibrarySync.Markets(a).Contains(language.Text))
                && (status.SelectedIndex <= 0 || a.Status == status.Text) && AccountLibraryStore.Matches(a, search.Text))
                .OrderBy(a => a.Status == "Архив").ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var a in visible) list.Items.Add(a);
            int selected = visible.FindIndex(a => a.Id == id); list.SelectedIndex = selected >= 0 ? selected : visible.Count > 0 ? 0 : -1;
            list.EndUpdate(); rebuilding = false;
            count.Text = loadError != "" ? "Не удалось открыть аккаунты" : "Аккаунтов: " + visible.Count + (visible.Count != accounts.Count ? " из " + accounts.Count : "") + " · выбери карточку слева";
            if (unresolved > 0) count.Text += " · не связаны: " + unresolved;
            ShowCard();
        }
        void DrawAccount(object sender, DrawItemEventArgs e) {
            if (e.Index < 0) return;
            var a = (AccountRecord)list.Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(selected ? Theme.Selected : Theme.Sidebar)) e.Graphics.FillRectangle(fill, e.Bounds);
            if (selected) using (var accent = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + 12, 3, e.Bounds.Height - 24);
            var name = new Rectangle(e.Bounds.X + 16, e.Bounds.Y + 10, e.Bounds.Width - 30, 26);
            TextRenderer.DrawText(e.Graphics, a.Name, Theme.FontCardTitle, name, Theme.TextPrimary, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(e.Graphics, a.Platform + " · " + string.Join("/", AccountLibrarySync.Markets(a)) + (a.Country == "" ? "" : " · " + a.Country), Theme.FontSmall,
                new Rectangle(name.X, name.Y + 27, name.Width, 18), Theme.TextSecondary, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Color color = a.Status == "Рабочий" ? Theme.Success : a.Status == "Заблокирован" || a.Status == "Не работает" ? Theme.Error : Theme.TextMuted;
            TextRenderer.DrawText(e.Graphics, "● " + a.Status + (AccountLibrarySync.Missing(a).Length > 0 ? " · есть пустые поля" : ""), Theme.FontSmall, new Rectangle(name.X, name.Y + 46, name.Width, 18), color, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        void ShowCard() {
            HideSecrets(); secretFields.Clear(); detail.SuspendLayout();
            foreach (Control child in detail.Controls.Cast<Control>().ToArray()) child.Dispose(); detail.Controls.Clear();
            var a = SelectedAccount;
            if (a == null) {
                var empty = Card(loadError != "" ? "Аккаунты сохранены, но файл не открывается" : accounts.Count == 0 ? "Добавь первый аккаунт" : "Ничего не найдено");
                AddText(empty, loadError != "" ? loadError : accounts.Count == 0 ? "Название, вход, прокси и покупка — всё будет в одной карточке. Начни с «+ Аккаунт» или перенеси свою вкладку Excel." : "Попробуй другое название или убери фильтры.");
            } else {
                var hero = Card(a.Name);
                AddText(hero, a.Platform + " · " + string.Join("/", AccountLibrarySync.Markets(a)) + (a.Country == "" ? "" : " · " + a.Country));
                var missing = AccountLibrarySync.Missing(a);
                if (missing.Length > 0) { AddText(hero, "Не заполнено: " + string.Join(", ", missing) + "."); hero.Controls[hero.Controls.Count - 1].ForeColor = Theme.Warning; }
                var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
                var quick = Theme.MakeCombo(AccountLibraryStore.Statuses); quick.Width = 150; quick.SelectedItem = a.Status; quick.Name = "AccountStatus";
                quick.SelectedIndexChanged += (s, e) => Safe(() => { store.SetStatus(a.Id, quick.Text); RefreshData(); });
                tools.Controls.Add(quick); tools.Controls.Add(Theme.MakeButton(missing.Length > 0 ? "Дополнить" : "Изменить", ghost: true, action: () => Edit(a)));
                var active = (a.Sources ?? new List<AccountSourceLink>()).Where(s => AccountLibrarySync.Sources(settings).Any(x => x.Key == s.Key)).ToList();
                if (a.Sources?.Count > 0) {
                    if (openSource != null) foreach (var link in active) { var captured = link; tools.Controls.Add(Theme.MakeButton(link.Platform + " · " + link.Market + (link.Kind == "" ? "" : " · " + link.Kind), ghost: true, action: () => openSource(captured))); }
                } else tools.Controls.Add(Theme.MakeButton("Удалить", ghost: true, action: () => Delete(a)));
                hero.Controls.Add(tools);
                if (a.Sources?.Count > 0) AddText(hero, AccountLibrarySync.DescribeSources(a, settings));
                var channel = Card("Канал"); ValueRow(channel, "Ссылка", a.Url, false, true);
                var access = Card("Вход и прокси");
                var reveal = Theme.MakeButton("Показать данные", ghost: true); reveal.Name = "RevealAccountSecrets";
                reveal.Click += (s, e) => {
                    revealed = !revealed; foreach (var set in secretFields) set(revealed);
                    reveal.Text = revealed ? "Скрыть данные" : "Показать данные";
                    if (revealed) { revealTimer.Stop(); revealTimer.Start(); } else revealTimer.Stop();
                };
                secretFields.Add(show => { if (!reveal.IsDisposed) reveal.Text = show ? "Скрыть данные" : "Показать данные"; });
                access.Controls.Add(reveal);
                ValueRow(access, "Логин / почта", a.Login, true); ValueRow(access, "Пароль", a.Password, true);
                ValueRow(access, "Дополнительные данные", a.AccessNotes, true); ValueRow(access, "Прокси", a.Proxy, true);
                var source = Card("Покупка и заметки"); ValueRow(source, "Где покупал", a.PurchaseUrl, false, true);
                AddText(source, string.IsNullOrWhiteSpace(a.Notes) ? "Заметок пока нет. Добавь их через «Изменить»." : a.Notes);
            }
            FitCards(); detail.ResumeLayout(true);
        }
        FlowLayoutPanel Card(string title) {
            var card = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                BackColor = Theme.Card, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 12) };
            card.Controls.Add(new Label { Text = title, Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary, AutoSize = true, Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(Math.Max(180, detail.ClientSize.Width - 70), 0) });
            detail.Controls.Add(card); return card;
        }
        void AddText(FlowLayoutPanel card, string text) { card.Controls.Add(new Label { Text = text, ForeColor = Theme.TextSecondary, AutoSize = true, Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(Math.Max(180, detail.ClientSize.Width - 70), 0) }); }
        void ValueRow(FlowLayoutPanel card, string caption, string value, bool secret, bool link = false) {
            card.Controls.Add(new Label { Text = caption, ForeColor = Theme.TextMuted, AutoSize = true, Margin = new Padding(0, 8, 0, 4) });
            bool missing = string.IsNullOrWhiteSpace(value);
            var row = new TableLayoutPanel { Height = 38, ColumnCount = link ? 3 : 2, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); if (link) row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            var box = new TextBox { Text = missing ? "Не добавлено" : secret ? "••••••••" : value, ReadOnly = true, BorderStyle = BorderStyle.None,
                BackColor = Theme.Card, ForeColor = missing ? Theme.TextMuted : Theme.TextPrimary, Dock = DockStyle.Fill, Margin = new Padding(0, 7, 8, 0), Font = Theme.FontBody };
            row.Controls.Add(box, 0, 0);
            if (secret && !missing) secretFields.Add(show => { if (!box.IsDisposed) { box.Multiline = show && value.Contains("\n"); box.Text = show ? value : "••••••••"; row.Height = show && value.Contains("\n") ? 80 : 38; } });
            if (link) { var open = Theme.MakeButton("Открыть", ghost: true, action: () => Safe(() => OpenLink(value))); open.Enabled = !missing; open.Dock = DockStyle.Fill; open.AutoSize = false; open.MinimumSize = Size.Empty; open.Padding = Padding.Empty; row.Controls.Add(open, 1, 0); }
            var copy = Theme.MakeIconButton("⧉", () => Safe(() => Copy(value, secret))); copy.Enabled = !missing; copy.AccessibleName = "Скопировать: " + caption; row.Controls.Add(copy, link ? 2 : 1, 0);
            card.Controls.Add(row);
        }
        void FitCards() {
            int width = Math.Max(210, detail.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            foreach (FlowLayoutPanel card in detail.Controls.OfType<FlowLayoutPanel>()) {
                card.MinimumSize = new Size(width, 0); card.MaximumSize = new Size(width, 0);
                foreach (Control c in card.Controls) {
                    if (c is Label) c.MaximumSize = new Size(width - 36, 0);
                    if (c is TableLayoutPanel) c.Width = width - 36;
                }
            }
        }
        void Edit(AccountRecord a) {
            using (var wizard = new AccountWizard(a)) if (wizard.ShowDialog(this) == DialogResult.OK) Safe(() => {
                store.Upsert(wizard.Result); RefreshData(); SelectId(wizard.Result.Id);
            });
        }
        void Delete(AccountRecord a) {
            if (MessageBox.Show(this, "Удалить карточку «" + a.Name + "»? Сам канал и профиль загрузки останутся.", "Удаление карточки", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Safe(() => { store.Remove(a.Id); RefreshData(); });
        }
        void ImportExcel() {
            using (var picker = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", Title = "Таблица с аккаунтами" }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                Safe(() => {
                    var sheets = AccountWorkbook.Read(picker.FileName);
                    using (var dialog = new AccountImportDialog(sheets)) if (dialog.ShowDialog(this) == DialogResult.OK) {
                        int added = store.Import(dialog.Result); RefreshData();
                        MessageBox.Show(this, "Добавлено: " + added + ". Уже существующих пропущено: " + (dialog.Result.Count - added) + ".\nДругую вкладку можно добавить через «Из Excel».", "Импорт завершён", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                });
            }
        }
        public void SelectId(string id) { for (int i = 0; i < list.Items.Count; i++) if (((AccountRecord)list.Items[i]).Id == id) { list.SelectedIndex = i; return; } }
        public void HideSecrets() { revealed = false; revealTimer.Stop(); foreach (var set in secretFields) set(false); }
        void Copy(string value, bool secret) {
            if (string.IsNullOrEmpty(value)) return;
            Clipboard.SetText(value); copiedSecret = secret ? value : null; clipboardTimer.Stop(); if (secret) clipboardTimer.Start();
        }
        void ClearCopiedSecret() {
            clipboardTimer.Stop();
            try { if (copiedSecret != null && Clipboard.ContainsText() && Clipboard.GetText() == copiedSecret) Clipboard.Clear(); } catch { }
            copiedSecret = null;
        }
        static void OpenLink(string value) { AccountLibraryStore.ValidateUrl(value); if (!string.IsNullOrWhiteSpace(value)) Process.Start(new ProcessStartInfo(value.Trim()) { UseShellExecute = true }); }
        void Safe(Action action) { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "Аккаунты", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
        protected override void Dispose(bool disposing) {
            if (disposing) { ClearCopiedSecret(); clipboardTimer.Dispose(); revealTimer.Dispose(); secretFields.Clear(); }
            base.Dispose(disposing);
        }
    }

    public sealed class AccountWizard : Form {
        readonly AccountRecord draft;
        readonly Dictionary<string, TextBox> fields = new Dictionary<string, TextBox>();
        readonly List<FlowLayoutPanel> pages = new List<FlowLayoutPanel>();
        readonly ComboBox platform = Theme.MakeCombo(new[] { "YouTube", "TikTok" });
        readonly ComboBox language = Theme.MakeCombo(new[] { "RU", "EN" });
        readonly ComboBox status = Theme.MakeCombo(AccountLibraryStore.Statuses);
        readonly Label heading = new Label();
        readonly Button back, next;
        int step;
        public AccountRecord Result { get; private set; }
        public AccountWizard(AccountRecord record = null) {
            draft = record?.Copy() ?? new AccountRecord(); Text = record == null ? "Новый аккаунт" : "Изменить аккаунт";
            Name = "AccountWizard"; ClientSize = new Size(600, 650); MinimumSize = new Size(550, 590); StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Background; ForeColor = Theme.TextPrimary; Font = Theme.FontBody; ShowInTaskbar = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); Controls.Add(root);
            heading.Dock = DockStyle.Fill; heading.Font = Theme.FontPageTitle; root.Controls.Add(heading, 0, 0);
            var host = new Panel { Dock = DockStyle.Fill }; root.Controls.Add(host, 0, 1);
            for (int i = 0; i < 3; i++) { var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Visible = false }; pages.Add(page); host.Controls.Add(page); }
            Field(pages[0], "Name", "Как называется канал?", draft.Name); Field(pages[0], "Url", "Ссылка на аккаунт", draft.Url);
            var choices = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 12) };
            platform.SelectedItem = draft.Platform; language.SelectedItem = draft.Language; status.SelectedItem = draft.Status;
            choices.Controls.Add(platform); choices.Controls.Add(language); status.Width = 160; choices.Controls.Add(status); pages[0].Controls.Add(choices);
            Field(pages[0], "Country", "Страна", draft.Country);
            Field(pages[1], "Login", "Логин / почта", draft.Login); Field(pages[1], "Password", "Пароль", draft.Password, secret: true);
            Field(pages[1], "AccessNotes", "Дополнительные данные для входа", draft.AccessNotes, multiline: true, secret: true);
            Field(pages[1], "Proxy", "Прокси — вставь строку целиком", draft.Proxy, secret: true);
            var show = new CheckBox { Text = "Показать данные при редактировании", AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 8, 0, 0) };
            show.Name = "ShowAccountEditSecrets";
            show.CheckedChanged += (s, e) => {
                foreach (string key in new[] { "Password", "AccessNotes", "Proxy" }) fields[key].UseSystemPasswordChar = !show.Checked;
                fields["AccessNotes"].Multiline = show.Checked; fields["AccessNotes"].Height = show.Checked ? 85 : 30;
            }; pages[1].Controls.Add(show);
            Field(pages[2], "PurchaseUrl", "Ссылка на покупку / продавца", draft.PurchaseUrl); Field(pages[2], "Notes", "Заметки — что важно помнить", draft.Notes, multiline: true);
            pages[2].Controls.Add(new Label { Text = "Необязательные поля можно оставить пустыми. Данные сохраняются локально для твоего пользователя Windows.", MaximumSize = new Size(500, 0), AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(0, 12, 0, 0) });
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 12, 0, 0) };
            next = Theme.MakeButton("Дальше →", true, action: Advance); next.Name = "AccountNext";
            back = Theme.MakeButton("← Назад", ghost: true, action: () => { step--; ShowStep(); });
            var cancel = Theme.MakeButton("Отмена", ghost: true); cancel.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(next); bottom.Controls.Add(back); bottom.Controls.Add(cancel); root.Controls.Add(bottom, 0, 2);
            CancelButton = cancel; AcceptButton = next;
            foreach (var page in pages) page.SizeChanged += (s, e) => { foreach (Control c in page.Controls) if (c is TextBox) c.Width = Math.Max(200, page.ClientSize.Width - 24); };
            ShowStep();
        }
        void Field(FlowLayoutPanel page, string key, string caption, string value, bool multiline = false, bool secret = false) {
            page.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 8, 0, 5) });
            // Password masking is only reliable in a single-line WinForms TextBox.
            var box = new TextBox { Name = "Account" + key, Text = value ?? "", Width = 500, BackColor = Theme.Elevated, ForeColor = Theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle, Font = Theme.FontBody, Multiline = multiline && !secret, Height = multiline && !secret ? 135 : 30,
                ScrollBars = multiline && !secret ? ScrollBars.Vertical : ScrollBars.None, UseSystemPasswordChar = secret, Margin = new Padding(0, 0, 0, 10) };
            fields[key] = box; page.Controls.Add(box);
        }
        void ShowStep() {
            heading.Text = new[] { "1 / 3  ·  Канал", "2 / 3  ·  Вход и прокси", "3 / 3  ·  Покупка и заметки" }[step];
            for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == step;
            pages[step].BringToFront(); back.Enabled = step > 0; next.Text = step == 2 ? "Сохранить" : "Дальше →";
        }
        void Advance() {
            try {
                if (step == 0) { if (string.IsNullOrWhiteSpace(fields["Name"].Text)) throw new ArgumentException("Укажи название аккаунта."); AccountLibraryStore.ValidateUrl(fields["Url"].Text); }
                if (step < 2) { step++; ShowStep(); return; }
                draft.Name = fields["Name"].Text.Trim(); draft.Url = fields["Url"].Text.Trim(); draft.Country = fields["Country"].Text.Trim();
                draft.Platform = platform.Text; draft.Language = language.Text; draft.Status = status.Text;
                draft.Login = fields["Login"].Text; draft.Password = fields["Password"].Text; draft.AccessNotes = fields["AccessNotes"].Text; draft.Proxy = fields["Proxy"].Text;
                draft.PurchaseUrl = fields["PurchaseUrl"].Text.Trim(); draft.Notes = fields["Notes"].Text;
                AccountLibraryStore.Validate(draft); Result = draft.Copy(); DialogResult = DialogResult.OK;
            } catch (Exception e) { MessageBox.Show(this, e.Message, "Аккаунт", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
    }
}
