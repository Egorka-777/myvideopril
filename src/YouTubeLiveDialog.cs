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
        readonly ComboBox privacy = new ComboBox();
        readonly CheckBox kids = new CheckBox();
        readonly ListBox accounts = new ListBox();
        readonly Button connect = new Button(), launch = new Button();
        readonly Label oauth = new Label();
        bool connecting;
        public LiveOptions Options { get; private set; }
        public YouTubeLiveDialog(YouTubeLiveManager manager, IReadOnlyList<YouTubeChannel> channels) {
            this.manager = manager; this.channels = channels;
            Text = "Эфир · " + (channels.Count==1 ? channels[0].Name : channels.Count+" каналов");
            StartPosition = FormStartPosition.CenterParent; Size = new Size(900, 760); MinimumSize = new Size(760, 680);
            BackColor = Theme.Background; ForeColor = Theme.TextPrimary; Font = Theme.FontBody;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18), BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 192)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 3, BackColor = Theme.Card, Padding = new Padding(12) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            folder.ReadOnly = true; thumbnail.ReadOnly = true;
            title.MaxLength = 100; description.Multiline = true; description.MaxLength = 5000; description.ScrollBars = ScrollBars.Vertical;
            title.Text = "Прямой эфир";
            AddRow(fields, "Папка с видео", folder, Button("Выбрать папку", PickFolder), 42);
            AddRow(fields, "Название", title, null, 42);
            AddRow(fields, "Описание", description, null, 74);
            var thumbButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            thumbButtons.Controls.Add(Button("Прикрепить", PickThumbnail, 106)); thumbButtons.Controls.Add(Button("×", () => thumbnail.Text = "", 28));
            AddRow(fields, "Превью", thumbnail, thumbButtons, 42);
            var tagCount=new Label { AutoSize=true,Text="0 / 500" }; tags.TextChanged+=(s,e)=>{ int n=LiveOptions.TagLength(tags.Text.Split(','));tagCount.Text=n+" / 500";tagCount.ForeColor=n>500?Theme.Warning:Theme.TextSecondary; };
            AddRow(fields, "Теги", tags, tagCount, 42);
            privacy.DropDownStyle = ComboBoxStyle.DropDownList; privacy.Items.AddRange(new object[] { "Открытый", "По ссылке", "Приватный" }); privacy.SelectedIndex = 0;
            AddRow(fields, "Доступ", privacy, null, 42);
            kids.Text = "Контент для детей"; kids.AutoSize = true; AddRow(fields, "Аудитория", kids, null, 34);
            var note = new Label { Dock = DockStyle.Fill, Text = "Все видео из папки идут по имени файла, бесконечно по кругу до остановки.\nРазрешение и FPS берутся из видео. Для одного эфира они должны совпадать у всех файлов.", ForeColor = Theme.TextMuted, AutoSize = true };
            AddRow(fields, "Воспроизведение", note, null, 50);
            root.Controls.Add(fields, 0, 0);
            var auth = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 10, 0, 0) };
            auth.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); auth.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            auth.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); auth.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); auth.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            oauth.Dock = DockStyle.Fill; oauth.AutoEllipsis = true;

            accounts.Dock = DockStyle.Fill; accounts.HorizontalScrollbar = true;
            connect.Text = "Проверить сессию Dolphin"; connect.Dock = DockStyle.Top; connect.Height = 52; connect.Click += async (s, e) => await Connect();
            auth.Controls.Add(oauth, 0, 0);  auth.Controls.Add(accounts, 0, 1); auth.Controls.Add(connect, 1, 1);
            var help = new Label { Text = "Используется вход в YouTube внутри Dolphin. Google OAuth для новых эфиров не нужен.", AutoSize = true, ForeColor = Theme.TextMuted };
            auth.Controls.Add(help, 0, 2); auth.SetColumnSpan(help, 2); root.Controls.Add(auth, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            var cancel = Button("Отмена", () => Close()); cancel.DialogResult = DialogResult.Cancel;
            launch.Text = "Запустить эфир"; launch.Width = 180; launch.Height = 32; launch.Click += async (s, e) => await ValidateLaunch();
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
                accounts.Items.Add(channel.Name + " → " + (a == null ? "сессия проверится при запуске" : a.Transport == "studio" ? a.Name + " · " + a.RemoteId + " · Dolphin " + a.ProfileId : "старое OAuth-подключение: проверьте сессию Dolphin"));
            }
            if (accounts.Items.Count > 0) accounts.SelectedIndex = Math.Max(0, Math.Min(selected, accounts.Items.Count - 1));
            oauth.Text = "YouTube Studio · HTTPS · сохранённая сессия Dolphin";
        }
        async Task Connect(bool automatic = false) {
            int index = accounts.SelectedIndex; if (index < 0 || connecting) return;
            var channel = channels[index];
            connecting = true; connect.Enabled = launch.Enabled = false;
            try {
                if (string.IsNullOrWhiteSpace(channel.ProfileId)) throw new InvalidOperationException("Укажите Profile ID этого канала в Dolphin.");
                var pendingBefore = manager.Store.PendingSnapshot().Where(e => e.LocalId == channel.ChannelId).ToArray();
                if (pendingBefore.Any(e => e.Transport != "studio" || e.ProfileId != channel.ProfileId || e.LocalPort != (manager.Preferences?.DolphinPort ?? 3001)))
                    throw new InvalidOperationException("Сначала завершите прежний эфир его прежним способом подключения. OAuth-журнал нельзя переносить на сессию.");
                var account = new LiveAccount { LocalId = channel.ChannelId, ProfileId = channel.ProfileId, Transport = "studio", LocalPort = manager.Preferences?.DolphinPort ?? 3001 };
                using (var api = new YouTubeLiveSessionApi(account, manager.Preferences)) {
                    await api.VerifyChannel(manager.Store.Account(channel.ChannelId)?.RemoteId ?? "", Store.ResolveYouTubeChannelUrl(channel.ChannelUrl,channel.Name,null), lifetime.Token);
                    account.RemoteId = api.ChannelId; account.Name = string.IsNullOrWhiteSpace(api.ChannelName) ? channel.Name : api.ChannelName;
                }
                if (IsDisposed || lifetime.IsCancellationRequested) return;
                var pending = manager.Store.PendingSnapshot().Where(e => e.LocalId == channel.ChannelId).ToArray();
                if (pending.Any(e => e.RemoteId != account.RemoteId)) throw new InvalidOperationException("Для завершения прошлого эфира подключите прежний канал.");
                if (!automatic && MessageBox.Show(this, "Строка: " + channel.Name + "\nКанал YouTube: " + account.Name + "\nID: " + account.RemoteId + "\n\nПривязать этот канал?", "Подключение канала", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                manager.Store.SaveAccount(account); UpdateAccounts();
            } catch (OperationCanceledException) { if(automatic) throw; if (!IsDisposed) MessageBox.Show(this, "Подключение отменено или истекло время ожидания.", "Сессия YouTube"); }
            catch (Exception e) { if(automatic) throw; if (!IsDisposed) MessageBox.Show(this, e.Message, "Сессия YouTube", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { connecting = false; if (!IsDisposed) { connect.Enabled = launch.Enabled = true; UpdateAccounts(); } }
        }
        async Task ValidateLaunch() {
            try {
                Options = new LiveOptions { Folder = folder.Text, Title = title.Text, Description = description.Text, Thumbnail = thumbnail.Text,
                    Tags = tags.Text.Split(','), Privacy = new[] { "public", "unlisted", "private" }[privacy.SelectedIndex], MadeForKids = kids.Checked };
                Options.Validate();
                for(int i=0;i<channels.Count;i++) { var a=manager.Store.Account(channels[i].ChannelId); if(a==null || a.Transport!="studio" || a.ProfileId!=channels[i].ProfileId || a.LocalPort!=(manager.Preferences?.DolphinPort ?? 3001)) {accounts.SelectedIndex=i; await Connect(true);} }
                if(IsDisposed || lifetime.IsCancellationRequested)return;
                if (channels.Any(c => manager.Store.Account(c.ChannelId)?.Transport != "studio" || manager.Store.Account(c.ChannelId)?.ProfileId != c.ProfileId))
                    throw new InvalidOperationException("Проверьте сессию Dolphin каждого выбранного канала.");
                DialogResult = DialogResult.OK; Close();
            } catch (Exception e) { if(!IsDisposed && !lifetime.IsCancellationRequested)MessageBox.Show(this, e.Message, "Прямой эфир", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        protected override void Dispose(bool disposing) { if (disposing) lifetime.Cancel(); base.Dispose(disposing); }
    }
}
