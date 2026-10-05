using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class YouTubeLiveDialog : Form {
        readonly YouTubeLiveManager manager;
        readonly IReadOnlyList<YouTubeChannel> channels;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly TextBox folder = new TextBox(), title = new TextBox(), description = new TextBox(), thumbnail = new TextBox(), tags = new TextBox();
        readonly ComboBox privacy = new ComboBox(), quality = new ComboBox();
        readonly CheckBox kids = new CheckBox();
        readonly ListBox accounts = new ListBox();
        readonly Button connect = new Button(), import = new Button(), launch = new Button();
        readonly Label oauth = new Label();
        bool connecting;
        public LiveOptions Options { get; private set; }
        public YouTubeLiveDialog(YouTubeLiveManager manager, IReadOnlyList<YouTubeChannel> channels) {
            this.manager = manager; this.channels = channels;
            Text = "Запустить прямой эфир · " + channels.Count + " канал(ов)";
            StartPosition = FormStartPosition.CenterParent; Size = new Size(900, 760); MinimumSize = new Size(760, 680);
            BackColor = Theme.Background; ForeColor = Theme.TextPrimary; Font = Theme.FontBody;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18), BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 192)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 3, BackColor = Theme.Card, Padding = new Padding(12) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            folder.ReadOnly = true; thumbnail.ReadOnly = true;
            title.MaxLength = 100; description.Multiline = true; description.MaxLength = 4500; description.ScrollBars = ScrollBars.Vertical;
            title.Text = "Прямой эфир";
            AddRow(fields, "Папка с видео", folder, Button("Выбрать папку", PickFolder), 42);
            AddRow(fields, "Название", title, null, 42);
            AddRow(fields, "Описание", description, null, 74);
            var thumbButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            thumbButtons.Controls.Add(Button("Прикрепить", PickThumbnail, 106)); thumbButtons.Controls.Add(Button("×", () => thumbnail.Text = "", 28));
            AddRow(fields, "Превью", thumbnail, thumbButtons, 42);
            AddRow(fields, "Теги", tags, new Label { Text = "Через запятую", AutoSize = true }, 42);
            privacy.DropDownStyle = ComboBoxStyle.DropDownList; privacy.Items.AddRange(new object[] { "Открытый", "По ссылке", "Приватный" }); privacy.SelectedIndex = 0;
            quality.DropDownStyle = ComboBoxStyle.DropDownList; quality.Items.AddRange(new object[] { "720p · 30 fps", "1080p · 30 fps" }); quality.SelectedIndex = 0;
            AddRow(fields, "Доступ", privacy, null, 42); AddRow(fields, "Качество", quality, null, 42);
            kids.Text = "Контент для детей"; kids.AutoSize = true; AddRow(fields, "Аудитория", kids, null, 34);
            var note = new Label { Dock = DockStyle.Fill, Text = "Все видео из папки идут по имени файла, бесконечно по кругу до остановки.\nПапка, название, превью и теги применяются ко всем выбранным каналам.", ForeColor = Theme.TextMuted, AutoSize = true };
            AddRow(fields, "Воспроизведение", note, null, 50);
            root.Controls.Add(fields, 0, 0);
            var auth = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 10, 0, 0) };
            auth.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); auth.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            auth.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); auth.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); auth.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            oauth.Dock = DockStyle.Fill; oauth.AutoEllipsis = true;
            import.Text = "Импорт OAuth JSON"; import.Dock = DockStyle.Fill; import.Click += (s, e) => ImportClient();
            accounts.Dock = DockStyle.Fill; accounts.HorizontalScrollbar = true;
            connect.Text = "Подключить выбранный канал"; connect.Dock = DockStyle.Top; connect.Height = 52; connect.Click += async (s, e) => await Connect();
            auth.Controls.Add(oauth, 0, 0); auth.Controls.Add(import, 1, 0); auth.Controls.Add(accounts, 0, 1); auth.Controls.Add(connect, 1, 1);
            var help = new Label { Text = "Подключение API — один раз для каждого канала. Выберите нужный аккаунт в открывшемся браузере.", AutoSize = true, ForeColor = Theme.TextMuted };
            auth.Controls.Add(help, 0, 2); auth.SetColumnSpan(help, 2); root.Controls.Add(auth, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            var cancel = Button("Отмена", () => Close()); cancel.DialogResult = DialogResult.Cancel;
            launch.Text = "Запустить эфиры"; launch.Width = 180; launch.Height = 32; launch.Click += (s, e) => ValidateLaunch();
            buttons.Controls.Add(cancel); buttons.Controls.Add(launch); root.Controls.Add(buttons, 0, 2); CancelButton = cancel;
            FormClosing += (s, e) => lifetime.Cancel();
            UpdateAccounts();
        }
        static Button Button(string text, Action click, int width = 144) { var b = new Button { Text = text, Width = width, Height = 30 }; b.Click += (s, e) => click(); return b; }
        static void AddRow(TableLayoutPanel panel, string label, Control input, Control action, int height) {
            int row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            input.Dock = DockStyle.Fill; input.Margin = new Padding(4, 6, 6, 6); panel.Controls.Add(input, 1, row);
            if (action != null) { action.Margin = new Padding(4, 6, 0, 6); panel.Controls.Add(action, 2, row); }
        }
        void PickFolder() {
            using (var dialog = new FolderBrowserDialog { Description = "Папка с видео для бесконечного эфира", SelectedPath = folder.Text })
                if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        }
        void PickThumbnail() {
            using (var dialog = new OpenFileDialog { Filter = "Превью JPG/PNG|*.jpg;*.jpeg;*.png" })
                if (dialog.ShowDialog(this) == DialogResult.OK) thumbnail.Text = dialog.FileName;
        }
        void UpdateAccounts() {
            int selected = accounts.SelectedIndex; accounts.Items.Clear();
            foreach (var channel in channels) {
                var a = manager.Store.Account(channel.ChannelId);
                accounts.Items.Add(channel.Name + " → " + (a == null ? "не подключён" : a.Name + " · " + a.RemoteId));
            }
            if (accounts.Items.Count > 0) accounts.SelectedIndex = Math.Max(0, Math.Min(selected, accounts.Items.Count - 1));
            oauth.Text = string.IsNullOrWhiteSpace(manager.Store.Config.ClientId) ? "YouTube API: импортируйте Desktop OAuth JSON" : "YouTube API: OAuth настроен";
            import.Enabled = !manager.HasBusy && !connecting;
        }
        void ImportClient() {
            try {
                if (manager.HasBusy) throw new InvalidOperationException("Сначала завершите все эфиры, включая незавершённые после сбоя.");
                using (var dialog = new OpenFileDialog { Filter = "Google Desktop OAuth|*.json" }) {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var client = LiveJson.Get(LiveJson.Object(File.ReadAllText(dialog.FileName)), "installed");
                    string id = LiveJson.Text(client, "client_id"), secret = LiveJson.Text(client, "client_secret");
                    if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || string.IsNullOrEmpty(secret)) throw new InvalidOperationException("Нужен JSON клиента типа Desktop app (поле installed).");
                    if (manager.Store.Config.ClientId != id && manager.Store.Config.Accounts.Count > 0) {
                        if (MessageBox.Show(this, "При смене OAuth-клиента нужно заново подключить каналы. Продолжить?", "YouTube API", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        manager.Store.Config.Accounts.Clear();
                    }
                    manager.Store.Config.ClientId = id; manager.Store.Config.ProtectedClientSecret = WindowsSupport.Protect(secret); manager.Store.Save(); UpdateAccounts();
                }
            } catch (Exception e) { MessageBox.Show(this, e.Message, "YouTube API", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        async Task Connect() {
            int index = accounts.SelectedIndex; if (index < 0 || connecting) return;
            var channel = channels[index];
            connecting = true; connect.Enabled = launch.Enabled = import.Enabled = false;
            try {
                var account = await YouTubeLiveOAuth.Connect(manager.Store, channel.ChannelId, lifetime.Token);
                using (var api = new YouTubeLiveApi(manager.Store, account)) await api.VerifyChannel(account.RemoteId, channel.ChannelUrl, lifetime.Token);
                if (IsDisposed || lifetime.IsCancellationRequested) return;
                var pending = manager.Store.PendingSnapshot().Where(e => e.LocalId == channel.ChannelId).ToArray();
                if (pending.Any(e => e.RemoteId != account.RemoteId)) throw new InvalidOperationException("Для завершения прошлого эфира подключите прежний канал.");
                if (MessageBox.Show(this, "Строка: " + channel.Name + "\nКанал YouTube: " + account.Name + "\nID: " + account.RemoteId + "\n\nПривязать этот канал?", "Подключение канала", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                manager.Store.SaveAccount(account); UpdateAccounts();
            } catch (OperationCanceledException) { if (!IsDisposed) MessageBox.Show(this, "Подключение отменено или истекло время ожидания.", "YouTube API"); }
            catch (Exception e) { if (!IsDisposed) MessageBox.Show(this, e.Message, "YouTube API", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { connecting = false; if (!IsDisposed) { connect.Enabled = launch.Enabled = true; UpdateAccounts(); } }
        }
        void ValidateLaunch() {
            try {
                Options = new LiveOptions { Folder = folder.Text, Title = title.Text, Description = description.Text, Thumbnail = thumbnail.Text,
                    Tags = tags.Text.Split(','), Privacy = new[] { "public", "unlisted", "private" }[privacy.SelectedIndex], Height = quality.SelectedIndex == 1 ? 1080 : 720, MadeForKids = kids.Checked };
                Options.Validate();
                if (channels.Any(c => manager.Store.Account(c.ChannelId) == null)) throw new InvalidOperationException("Подключите каждый выбранный канал к YouTube API.");
                DialogResult = DialogResult.OK; Close();
            } catch (Exception e) { MessageBox.Show(this, e.Message, "Прямой эфир", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        protected override void Dispose(bool disposing) { if (disposing) lifetime.Cancel(); base.Dispose(disposing); }
    }
}
