using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class AccountImportDialog : Form {
        readonly IList<AccountSheet> sheets;
        readonly ComboBox sheetChoice = Theme.MakeCombo(null), platform = Theme.MakeCombo(new[] { "YouTube", "TikTok" }), language = Theme.MakeCombo(new[] { "RU", "EN" });
        readonly ComboBox headerRow = Theme.MakeCombo(null);
        readonly Dictionary<string, ComboBox> maps = new Dictionary<string, ComboBox>();
        readonly DataGridView preview = new DataGridView();
        readonly Label info = new Label();
        bool loading;
        public List<AccountRecord> Result { get; private set; }
        AccountSheet Sheet => sheets[sheetChoice.SelectedIndex];
        public AccountImportDialog(IList<AccountSheet> workbook) {
            sheets = workbook; Text = "Аккаунты из Excel"; Name = "AccountImportDialog"; ClientSize = new Size(820, 720); MinimumSize = new Size(760, 680);
            StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Background; ForeColor = Theme.TextPrimary; Font = Theme.FontBody; ShowInTaskbar = false;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 5 };
            foreach (int h in new[] { 56, 50, 270 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, h)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); Controls.Add(root);
            root.Controls.Add(new Label { Text = "Выбери вкладку и проверь столбцы", Font = Theme.FontCardTitle, AutoSize = true, ForeColor = Theme.TextPrimary, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            sheetChoice.Width = 260; foreach (var s in sheets) sheetChoice.Items.Add(s); toolbar.Controls.Add(sheetChoice); toolbar.Controls.Add(platform); language.Width = 65; toolbar.Controls.Add(language);
            toolbar.Controls.Add(new Label { Text = "Строка:", ForeColor = Theme.TextSecondary, AutoSize = true, Margin = new Padding(8, 5, 4, 0) }); headerRow.Width = 62; toolbar.Controls.Add(headerRow); root.Controls.Add(toolbar, 0, 1);
            var mapping = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5 };
            mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132)); mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132)); mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < AccountWorkbook.Keys.Length; i++) {
                int col = i / 5 * 2, row = i % 5; var label = new Label { Text = AccountWorkbook.Captions[i], ForeColor = Theme.TextSecondary, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
                var box = Theme.MakeCombo(null); box.Dock = DockStyle.Fill; box.Margin = new Padding(0, 12, 12, 10); maps.Add(AccountWorkbook.Keys[i], box);
                mapping.Controls.Add(label, col, row); mapping.Controls.Add(box, col + 1, row); box.SelectedIndexChanged += (s, e) => UpdatePreview();
            }
            root.Controls.Add(mapping, 0, 2);
            preview.Dock = DockStyle.Fill; preview.ReadOnly = true; preview.AllowUserToAddRows = false; preview.AllowUserToDeleteRows = false; preview.AllowUserToResizeRows = false; preview.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; preview.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            Theme.StyleGrid(preview); preview.RowTemplate.Height = 30; preview.Columns.Add("name", "Название"); preview.Columns.Add("url", "Ссылка"); preview.Columns.Add("country", "Страна"); root.Controls.Add(preview, 0, 3);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            info.Dock = DockStyle.Fill; info.ForeColor = Theme.TextSecondary; info.TextAlign = ContentAlignment.MiddleLeft; bottom.Controls.Add(info, 0, 0);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
            var cancel = Theme.MakeButton("Отмена", ghost: true); cancel.DialogResult = DialogResult.Cancel; buttons.Controls.Add(cancel);
            var import = Theme.MakeButton("Добавить аккаунты", true, action: Commit); import.Name = "ConfirmAccountImport"; buttons.Controls.Add(import); bottom.Controls.Add(buttons, 1, 0); root.Controls.Add(bottom, 0, 4); CancelButton = cancel;
            sheetChoice.SelectedIndexChanged += (s, e) => LoadSheet(); headerRow.SelectedIndexChanged += (s, e) => { if (!loading && headerRow.SelectedIndex >= 0) { Sheet.HeaderRow = headerRow.SelectedIndex; LoadColumns(); } };
            sheetChoice.SelectedIndex = 0;
        }
        void LoadSheet() {
            loading = true; platform.SelectedItem = AccountWorkbook.GuessPlatform(Sheet.Name); language.SelectedItem = AccountWorkbook.GuessLanguage(Sheet.Name);
            headerRow.Items.Clear();
            for (int i = 0; i < Math.Min(25, Sheet.Rows.Count); i++) headerRow.Items.Add(i < Sheet.SourceRows.Count ? Sheet.SourceRows[i].ToString() : (i + 1).ToString());
            headerRow.SelectedIndex = Sheet.HeaderRow; loading = false; LoadColumns();
        }
        void LoadColumns() {
            loading = true;
            foreach (var pair in maps) {
                pair.Value.Items.Clear(); pair.Value.Items.Add("Не использовать");
                for (int i = 0; i < Sheet.Headers.Length; i++) pair.Value.Items.Add((i + 1) + " · " + (Sheet.Headers[i] == "" ? "без названия" : Sheet.Headers[i]));
                pair.Value.SelectedIndex = AccountWorkbook.GuessColumn(Sheet.Headers, pair.Key) + 1;
            }
            loading = false; UpdatePreview();
        }
        Dictionary<string, int> Columns() { return maps.ToDictionary(pair => pair.Key, pair => pair.Value.SelectedIndex - 1); }
        void UpdatePreview() {
            if (loading || sheetChoice.SelectedIndex < 0) return;
            preview.Rows.Clear(); var columns = Columns();
            for (int i = Sheet.HeaderRow + 1; i < Math.Min(Sheet.Rows.Count, Sheet.HeaderRow + 6); i++) {
                var row = Sheet.Rows[i];
                string Value(string key) {
                    int c = columns[key]; if (c < 0 || c >= row.Length) return "";
                    if (key == "Url" && i < Sheet.LinkRows.Count && c < Sheet.LinkRows[i].Length && Sheet.LinkRows[i][c] != "") return Sheet.LinkRows[i][c];
                    return row[c];
                }
                preview.Rows.Add(Value("Name"), Value("Url"), Value("Country"));
            }
            info.Text = "Строк: " + Math.Max(0, Sheet.Rows.Count - Sheet.HeaderRow - 1) + " · вход и прокси в предпросмотре скрыты";
        }
        void Commit() {
            try {
                Result = AccountWorkbook.ConvertRows(Sheet, Columns(), platform.Text, language.Text, out int skipped);
                if (skipped > 0 && MessageBox.Show(this, "Строк без названия: " + skipped + ". Они будут пропущены. Добавить остальные " + Result.Count + "?", "Импорт", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { Result = null; return; }
                DialogResult = DialogResult.OK;
            } catch (Exception e) { MessageBox.Show(this, e.Message, "Проверь импорт", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
    }
}
