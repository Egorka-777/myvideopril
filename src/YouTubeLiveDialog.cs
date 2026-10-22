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
        readonly Label progressLabel=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Theme.TextPrimary};
        bool connecting, launching;
        LivePlaylist prepared;
        readonly ProgressBar preparationProgress = new ProgressBar { Dock = DockStyle.Bottom, Height = 20, Visible = false };
        public LiveOptions Options { get; private set; }
        public LivePlaylist TakePreparedPlaylist() { var value = prepared; prepared = null; return value; }
        public YouTubeLiveDialog(YouTubeLiveManager manager, IReadOnlyList<YouTubeChannel> channels) {
            this.manager = manager; this.channels = channels;
            Text = "Эфир · " + (channels.Count==1 ? channels[0].Name : channels.Count+" каналов");
            StartPosition = FormStartPosition.CenterParent; Size = new Size(900, 760); MinimumSize = new Size(760, 680);
            BackColor = Theme.Background; ForeColor = Theme.TextPrimary; Font = Theme.FontBody;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18), BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 192)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(root); Controls.Add(preparationProgress);
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 3, BackColor = Theme.Card, Padding = new Padding(12) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            folder.ReadOnly = true; thumbnail.ReadOnly = true;
            title.MaxLength = 100; description.Multiline = true; description.MaxLength = 5000; description.ScrollBars = ScrollBars.Vertical;
            title.Text = "Прямой эфир";
            AddRow(fields, "Видеофайл", folder, Button("Выбрать видео", PickFile), 42);
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
            var note = new Label { Dock = DockStyle.Fill, Text = "Выбранное видео повторяется по кругу до остановки эфира или закрытия приложения.\nРазрешение и FPS сохраняются. Стык конца и начала зависит от самого видео.", ForeColor = Theme.TextMuted, AutoSize = true };
            AddRow(fields, "Воспроизведение", note, null, 50);
            root.Controls.Add(fields, 0, 0);
            var auth = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 10, 0, 0) };
            auth.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); auth.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            auth.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); auth.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); auth.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            oauth.Dock = DockStyle.Fill; oauth.AutoEllipsis = true;

            accounts.Dock = DockStyle.Fill; accounts.HorizontalScrollbar = true;
            connect.Text = "Проверить сессию Dolphin"; connect.Dock = DockStyle.Top; connect.Height = 52; connect.Click += async (s, e) => await Connect();
            auth.Controls.Add(oauth, 0, 0);  auth.Controls.Add(accounts, 0, 1); auth.Controls.Add(connect, 1, 1);
            progressLabel.Text="Проверка сессии и запуск будут видны здесь и в логе.";
            var logButton=Button("Открыть лог",()=>{try{Ui.Open(LiveDiagnostics.LogPath);}catch(Exception e){manager.Trace("Не удалось открыть лог: "+e.Message);}},210);logButton.Dock=DockStyle.Fill;logButton.Margin=Padding.Empty;
            auth.Controls.Add(progressLabel,0,2);auth.Controls.Add(logButton,1,2);
            manager.LogLine+=ShowProgress;
            root.Controls.Add(auth, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            var cancel = Button("Отмена", () => Close()); cancel.DialogResult = DialogResult.Cancel;
            launch.Text = "Запустить эфир"; launch.Width = 180; launch.Height = 32; launch.Click += async (s, e) => await ValidateLaunch();
            buttons.Controls.Add(cancel); buttons.Controls.Add(launch); root.Controls.Add(buttons, 0, 2); CancelButton = cancel;
            FormClosing += (s, e) => lifetime.Cancel();
            UpdateAccounts();manager.Trace("Открыто окно эфира · "+Text);
        }
        void ShowProgress(string line) {
            if(IsDisposed)return;
            if(InvokeRequired){if(IsHandleCreated)BeginInvoke(new Action<string>(ShowProgress),line);return;}
            progressLabel.Text=line;
        }
        static Button Button(string text, Action click, int width = 144) { var b = new Button { Text = text, Width = width, Height = 30 }; b.Click += (s, e) => click(); return b; }
        static void AddRow(TableLayoutPanel panel, string label, Control input, Control action, int height) {
            int row = panel.RowCount++; panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            input.Dock = DockStyle.Fill; input.Margin = new Padding(4, 6, 6, 6); panel.Controls.Add(input, 1, row);
            if (action != null) { action.Margin = new Padding(4, 6, 0, 6); panel.Controls.Add(action, 2, row); }
        }
        void PickFile() {
            using (var dialog = new OpenFileDialog { Title = "Видео для бесконечного эфира", Filter = "Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi", CheckFileExists = true, Multiselect = false })
                if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.FileName;
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
            manager.Trace(channel.Name+" · проверяю сессию Dolphin "+channel.ProfileId);
            connecting = true; connect.Enabled = launch.Enabled = false;
            try {
                if (string.IsNullOrWhiteSpace(channel.ProfileId)) throw new InvalidOperationException("Укажите Profile ID этого канала в Dolphin.");
                var pendingBefore = manager.Store.PendingSnapshot().Where(e => e.LocalId == channel.ChannelId).ToArray();
                if (pendingBefore.Any(e => e.Transport != "studio" || e.ProfileId != channel.ProfileId || e.LocalPort != (manager.Preferences?.DolphinPort ?? 3001)))
                    throw new InvalidOperationException("Сначала завершите прежний эфир его прежним способом подключения. OAuth-журнал нельзя переносить на сессию.");
                var account = new LiveAccount { LocalId = channel.ChannelId, ProfileId = channel.ProfileId, Transport = "studio", LocalPort = manager.Preferences?.DolphinPort ?? 3001 };
                using (var api = new YouTubeLiveSessionApi(account, manager.Preferences, progress: text=>manager.Trace(channel.Name+" · "+text))) {
                    await api.VerifyChannel(manager.Store.Account(channel.ChannelId)?.RemoteId ?? "", Store.ResolveYouTubeChannelUrl(channel.ChannelUrl,channel.Name,null), lifetime.Token);
                    account.RemoteId = api.ChannelId; account.Name = string.IsNullOrWhiteSpace(api.ChannelName) ? channel.Name : api.ChannelName;
                }
                if (IsDisposed || lifetime.IsCancellationRequested) return;
                var pending = manager.Store.PendingSnapshot().Where(e => e.LocalId == channel.ChannelId).ToArray();
                if (pending.Any(e => e.RemoteId != account.RemoteId)) throw new InvalidOperationException("Для завершения прошлого эфира подключите прежний канал.");
                if (!automatic && MessageBox.Show(this, "Строка: " + channel.Name + "\nКанал YouTube: " + account.Name + "\nID: " + account.RemoteId + "\n\nПривязать этот канал?", "Подключение канала", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                manager.Store.SaveAccount(account); UpdateAccounts();
            } catch (OperationCanceledException) { if(automatic) throw; if (!IsDisposed) MessageBox.Show(this, "Подключение отменено или истекло время ожидания.", "Сессия YouTube"); }
            catch (Exception e) {manager.Trace(channel.Name+" · ошибка проверки: "+e.Message);if(automatic) throw; if (!IsDisposed) MessageBox.Show(this, e.Message, "Сессия YouTube", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { connecting = false; if (!IsDisposed) { connect.Enabled = launch.Enabled = !launching; UpdateAccounts(); } }
        }
        async Task ValidateLaunch() {
            if (launching) return;
            launching = true; launch.Enabled = connect.Enabled = false;
            if (launching) return;
            launching = true; launch.Enabled = connect.Enabled = false;
            manager.Trace("Нажат запуск: проверяю видеофайл, превью и параметры");
            try {
                Options = new LiveOptions { VideoFile = folder.Text, Title = title.Text, Description = description.Text, Thumbnail = thumbnail.Text,
                    Tags = tags.Text.Split(','), Privacy = new[] { "public", "unlisted", "private" }[privacy.SelectedIndex], MadeForKids = kids.Checked };
                Options.Validate();
                string ffmpeg, ffprobe, hint;
                if (!ToolsLocator.TryResolve(out ffmpeg, out ffprobe, out hint)) throw new InvalidOperationException(hint);
                Text = "Подготовка видео · " + channels[0].Name;
                preparationProgress.Visible = true; preparationProgress.Value = 0;
                progressLabel.Text = "Подготовка видео. Dolphin откроется после завершения.";
                var progress = new Progress<double>(value => {
                    if (IsDisposed || lifetime.IsCancellationRequested) return;
                    int percent = Math.Max(0, Math.Min(100, (int)value));
                    preparationProgress.Value = percent; progressLabel.Text = "Подготовка видео · " + percent + "% · Dolphin пока не открывается";
                });
                prepared = await YouTubeLiveMedia.Prepare(Options, ffmpeg, ffprobe, manager.Trace, lifetime.Token, progress);
                lifetime.Token.ThrowIfCancellationRequested();
                manager.Trace("Подготовка видео завершена · 100%. Открываю профиль и проверяю канал.");
                Text = "Проверка канала · " + channels[0].Name;
                preparationProgress.Visible = false;
                for(int i=0;i<channels.Count;i++) { accounts.SelectedIndex=i; await Connect(true); }
                if(IsDisposed || lifetime.IsCancellationRequested)return;
                if (channels.Any(c => manager.Store.Account(c.ChannelId)?.Transport != "studio" || manager.Store.Account(c.ChannelId)?.ProfileId != c.ProfileId))
                    throw new InvalidOperationException("Проверьте сессию Dolphin каждого выбранного канала.");
                DialogResult = DialogResult.OK; Close();
            } catch (Exception e) {
                if (prepared != null) { prepared.Dispose(); prepared = null; }
                manager.Trace(lifetime.IsCancellationRequested ? "Подготовка/запуск отменены." : "Запуск остановлен: " + e.Message);
                if(!IsDisposed && !lifetime.IsCancellationRequested)MessageBox.Show(this, e.Message, "Прямой эфир", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            } finally {
                launching = false;
                if (!IsDisposed) { launch.Enabled = connect.Enabled = true; preparationProgress.Visible = false; }
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) {manager.LogLine-=ShowProgress;lifetime.Cancel();if(prepared!=null){prepared.Dispose();prepared=null;}} base.Dispose(disposing); }
    }
}
