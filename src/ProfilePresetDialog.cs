using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    // A successful callback means the network has confirmed ALL requested changes.
    // Once confirmed, persistence retries must never invoke the social action again.
    internal sealed class ProfilePresetApplySession {
        static readonly System.Collections.Generic.HashSet<string> activePresets = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly ProfilePresetStore store;
        readonly string network, accountId;
        readonly Func<ProfilePreset, Task> apply;
        public bool Busy { get; private set; }
        public ProfilePreset PendingReceipt { get; private set; }
        public ProfilePresetApplySession(ProfilePresetStore store, string network, string accountId, Func<ProfilePreset, Task> apply) {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.network = ProfilePresetStore.NormalizeNetwork(network);
            if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentException("Не выбран аккаунт.");
            this.accountId = accountId;
            this.apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }
        public async Task ApplyAsync(ProfilePreset preset) {
            if (Busy) throw new InvalidOperationException("Применение уже выполняется.");
            Busy = true;
            try {
                if (PendingReceipt == null) {
                    ProfilePresetStore.Validate(preset);
                    if (store.GetUsed(network, preset.Id)) throw new InvalidOperationException("Этот пресет уже использован в этой сети.");
                    var snapshot = preset.Copy();
                    string key = store.DataPath + "|" + network + "|" + snapshot.Id;
                    lock (activePresets) if (!activePresets.Add(key)) throw new InvalidOperationException("Этот пресет уже применяется в другом окне этой сети.");
                    try {
                        if (store.GetUsed(network, snapshot.Id)) throw new InvalidOperationException("Этот пресет уже использован в этой сети.");
                        await apply(snapshot.Copy());
                        PendingReceipt = snapshot;
                        store.MarkUsed(network, snapshot.Id, accountId);
                        PendingReceipt = null;
                    } finally { lock (activePresets) activePresets.Remove(key); }
                    return;
                }
                store.MarkUsed(network, PendingReceipt.Id, accountId);
                PendingReceipt = null;
            } finally { Busy = false; }
        }
    }

    public sealed class ProfilePresetDialog : Form {
        readonly ProfilePresetStore store;
        readonly string network;
        readonly ProfilePresetApplySession session;
        readonly ListBox list = new ListBox();
        readonly TextBox name = Theme.MakeSearchBox(), bio = Theme.MakeSearchBox();
        readonly ComboBox avatars = Theme.MakeCombo(null);
        readonly PictureBox preview = new PictureBox();
        readonly Label status = new Label();
        readonly Panel editor = new Panel();
        readonly FlowLayoutPanel libraryActions = new FlowLayoutPanel();
        readonly Button applyButton, saveButton, deleteButton;
        bool refreshing, applying;
        string selectedId;
        string avatarPath = "";

        /// <summary>The callback must throw on failure/partial success; return only after confirmed network success.
        /// This dialog records usage after that return. Callbacks must not call MarkUsed themselves.</summary>
        public static DialogResult ShowDialog(IWin32Window owner, string network, string accountId, Func<ProfilePreset, Task> apply) {
            using (var dialog = new ProfilePresetDialog(network, accountId, apply)) return dialog.ShowDialog(owner);
        }
        public static DialogResult ShowLibrary(IWin32Window owner) {
            using (var dialog = new ProfilePresetDialog(null, null, null)) return dialog.ShowDialog(owner);
        }
        // Storage injection also allows isolated tests and alternate portable storage roots.
        public ProfilePresetDialog(string network, string accountId, Func<ProfilePreset, Task> apply, ProfilePresetStore store = null) {
            this.store = store ?? new ProfilePresetStore();
            this.network = network == null ? null : ProfilePresetStore.NormalizeNetwork(network);
            if (this.network != null) session = new ProfilePresetApplySession(this.store, this.network, accountId, apply);
            Text = session == null ? "Пресеты профилей — библиотека" : "Заполнить профиль — " + network + " — " + accountId;
            ClientSize = new Size(920, 670); MinimumSize = new Size(820, 600);
            StartPosition = FormStartPosition.CenterParent; MaximizeBox = false;
            Theme.StyleModal(this);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(18) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(0, 0, 14, 0) };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            left.Controls.Add(new Label { Text = session == null ? "Пресеты · имя, описание, аватар" : "Пресеты (серые — использованы)", Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.BackColor = Theme.Card; list.ForeColor = Theme.TextPrimary;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 38; list.IntegralHeight = false;
            list.DrawItem += DrawPreset;
            list.SelectedIndexChanged += (s, e) => { if (!refreshing) SelectPreset(); };
            left.Controls.Add(list, 0, 1);
            libraryActions.Dock = DockStyle.Fill; libraryActions.Padding = new Padding(0, 8, 0, 0); libraryActions.WrapContents = false;
            var add = Theme.MakeButton("Новый"); add.Click += (s, e) => NewPreset();
            deleteButton = Theme.MakeButton("Удалить"); deleteButton.Click += (s, e) => Guard(DeletePreset);
            libraryActions.Controls.Add(add); libraryActions.Controls.Add(deleteButton); left.Controls.Add(libraryActions, 0, 2);
            root.Controls.Add(left, 0, 0);
            editor.Dock = DockStyle.Fill;
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(10, 0, 0, 0) };
            foreach (int height in new[] { 28, 38, 28, 112, 28, 46 }) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            fields.Controls.Add(Caption("Имя профиля"), 0, 0); name.Dock = DockStyle.Fill; name.MaxLength = 0; fields.Controls.Add(name, 0, 1);
            fields.Controls.Add(Caption("Описание профиля"), 0, 2); bio.Multiline = true; bio.AcceptsReturn = true; bio.ScrollBars = ScrollBars.Vertical; bio.MaxLength = 0; bio.Dock = DockStyle.Fill; fields.Controls.Add(bio, 0, 3);
            fields.Controls.Add(Caption("Аватар: из библиотеки или свой файл"), 0, 4);
            var avatarRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            avatarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); avatarRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            avatars.Dock = DockStyle.Fill; avatars.DisplayMember = "Label";
            avatars.SelectedIndexChanged += (s, e) => { if (!refreshing && avatars.SelectedItem is AvatarChoice item) Guard(() => SetAvatar(item.Path)); };
            avatarRow.Controls.Add(avatars, 0, 0);
            var browse = Theme.MakeButton("Выбрать файл"); browse.Click += (s, e) => Guard(BrowseAvatar); avatarRow.Controls.Add(browse, 1, 0);
            fields.Controls.Add(avatarRow, 0, 5);
            preview.Dock = DockStyle.Fill; preview.SizeMode = PictureBoxSizeMode.Zoom; preview.BackColor = Theme.Card; preview.Margin = new Padding(3, 8, 3, 8); fields.Controls.Add(preview, 0, 6);
            saveButton = Theme.MakeButton("Сохранить пресет"); saveButton.Click += (s, e) => Guard(SavePreset); fields.Controls.Add(saveButton, 0, 7);
            editor.Controls.Add(fields); root.Controls.Add(editor, 1, 0);
            status.Dock = DockStyle.Fill; status.ForeColor = Theme.TextSecondary; status.Padding = new Padding(0, 8, 0, 0); root.Controls.Add(status, 0, 1); root.SetColumnSpan(status, 2);
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var close = Theme.MakeButton("Закрыть"); close.Click += (s, e) => Close(); bottom.Controls.Add(close);
            applyButton = Theme.MakeButton("Применить", accent: true); applyButton.Click += async (s, e) => await ApplySelectedAsync(); applyButton.Visible = session != null; bottom.Controls.Add(applyButton);
            root.Controls.Add(bottom, 0, 2); root.SetColumnSpan(bottom, 2);
            FormClosing += (s, e) => {
                if (applying || session?.PendingReceipt != null) {
                    e.Cancel = true;
                    if (!applying) ShowError("Профиль изменён, но отметка использования не сохранена. Нажми «Сохранить отметку» перед закрытием.");
                }
            };
            Shown += (s, e) => Guard(() => Reload(null));
            UpdateEnabled();
        }
        static Label Caption(string text) { return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextSecondary }; }
        sealed class AvatarChoice {
            public string Path { get; set; }
            public string Label { get; set; }
        }
        sealed class PresetItem {
            public ProfilePreset Preset;
            public bool Used;
        }
        void DrawPreset(object sender, DrawItemEventArgs e) {
            e.DrawBackground();
            if (e.Index < 0) return;
            var item = (PresetItem)list.Items[e.Index];
            TextRenderer.DrawText(e.Graphics, item.Preset.Name + (item.Used ? " — использован" : ""), list.Font,
                e.Bounds, item.Used ? Theme.TextMuted : Theme.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            e.DrawFocusRectangle();
        }
        void Reload(string selectId) {
            var presets = store.Load();
            var items = presets.Select(p => new PresetItem { Preset = p, Used = network != null && store.GetUsed(network, p.Id) }).ToArray();
            refreshing = true;
            try {
                list.Items.Clear(); list.Items.AddRange(items);
                list.SelectedIndex = Array.FindIndex(items, p => p.Preset.Id == selectId);
                if (list.SelectedIndex < 0 && items.Length > 0) list.SelectedIndex = 0;
                avatars.Items.Clear();
                foreach (string path in store.ListAvatars()) avatars.Items.Add(new AvatarChoice { Path = path, Label = Path.GetFileName(path) });
            } finally { refreshing = false; }
            SelectPreset();
        }
        void SelectPreset() {
            Guard(() => {
                if (list.SelectedItem is PresetItem item) {
                    selectedId = item.Preset.Id; name.Text = item.Preset.Name; bio.Text = item.Preset.Bio;
                    SetAvatar(item.Preset.AvatarPath);
                    status.ForeColor = Theme.TextSecondary;
                    status.Text = session == null ? list.Items.Count + " пресетов · Редактируй имя, описание и аватар, затем нажми «Сохранить пресет». Аккаунты в соцсетях не изменяются." : item.Used ? "Этот пресет уже использован в " + network + ". Можно редактировать или удалить; повторное применение недоступно."
                        : "Проверь имя, описание и аватар. «Применить» также сохранит изменения в библиотеке.";
                } else { selectedId = null; name.Clear(); bio.Clear(); SetAvatar(""); }
                UpdateEnabled();
            });
        }
        void NewPreset() {
            refreshing = true; list.ClearSelected(); refreshing = false;
            selectedId = Guid.NewGuid().ToString("N"); name.Clear(); bio.Clear(); SetAvatar("");
            status.Text = session == null ? "Заполни имя, описание и выбери аватар, затем сохрани пресет." : "Заполни имя, описание и выбери аватар, затем сохрани или примени."; status.ForeColor = Theme.TextSecondary;
            UpdateEnabled(); name.Focus();
        }
        void SetAvatar(string path) {
            Image image = null;
            if (!string.IsNullOrWhiteSpace(path)) using (var source = Image.FromFile(path)) image = new Bitmap(source);
            var old = preview.Image; preview.Image = image; old?.Dispose(); avatarPath = path;
            refreshing = true;
            try { avatars.SelectedIndex = Enumerable.Range(0, avatars.Items.Count).Where(i => string.Equals(((AvatarChoice)avatars.Items[i]).Path, path, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(-1).First(); }
            finally { refreshing = false; }
        }
        void BrowseAvatar() {
            using (var picker = new OpenFileDialog { Title = "Аватар профиля", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp", CheckFileExists = true }) {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                string path = store.ImportAvatar(picker.FileName);
                if (!avatars.Items.Cast<AvatarChoice>().Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                    avatars.Items.Add(new AvatarChoice { Path = path, Label = Path.GetFileName(picker.FileName) });
                SetAvatar(path);
            }
        }
        ProfilePreset EditedPreset() { return new ProfilePreset { Id = selectedId, Name = name.Text.Trim(), Bio = bio.Text.Trim(), AvatarPath = avatarPath }; }
        void SavePreset() {
            var preset = EditedPreset(); store.Save(preset); Reload(preset.Id);
            status.ForeColor = Theme.Success; status.Text = "Пресет сохранён. Использование не изменилось.";
        }
        void DeletePreset() {
            if (!(list.SelectedItem is PresetItem item)) return;
            if (MessageBox.Show(this, "Удалить пресет «" + item.Preset.Name + "»? История использования останется.", "Удалить пресет", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            store.Delete(item.Preset.Id); Reload(null);
        }
        async Task ApplySelectedAsync() {
            if (applying) return;
            applying = true; UpdateEnabled();
            try {
                ProfilePreset preset = session.PendingReceipt;
                if (preset == null) { preset = EditedPreset(); store.Save(preset); preset = store.Load().Single(p => p.Id == preset.Id); }
                status.ForeColor = Theme.TextSecondary; status.Text = "Применение профиля…";
                await session.ApplyAsync(preset);
                applying = false;
                DialogResult = DialogResult.OK; Close();
            } catch (Exception ex) {
                ShowError(session.PendingReceipt != null ? "Профиль изменён, но отметка использования не сохранена. Повтори сохранение отметки (без повторной отправки в сеть). " + ex.Message : ex.Message);
            } finally { applying = false; UpdateEnabled(); }
        }
        void UpdateEnabled() {
            bool editable = !applying && session?.PendingReceipt == null;
            list.Enabled = editable; editor.Enabled = editable; libraryActions.Enabled = editable;
            saveButton.Enabled = selectedId != null; deleteButton.Enabled = list.SelectedItem != null;
            applyButton.Text = session?.PendingReceipt != null ? "Сохранить отметку" : "Применить";
            applyButton.Enabled = session != null && !applying && (session.PendingReceipt != null || (selectedId != null && (!(list.SelectedItem is PresetItem item) || !item.Used)));
        }
        void Guard(Action action) { try { action(); } catch (Exception ex) { ShowError(ex.Message); } }
        void ShowError(string message) { status.Text = message; status.ForeColor = Theme.Error; }
        protected override void Dispose(bool disposing) { if (disposing) { var image = preview.Image; preview.Image = null; image?.Dispose(); } base.Dispose(disposing); }
    }
}
