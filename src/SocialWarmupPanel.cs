using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    internal sealed class SocialWarmupPanel : UserControl {
        readonly Preferences settings;
        readonly ComboBox network;
        readonly DataGridView grid;
        readonly RichTextBox log;
        readonly Button start, refresh;
        readonly NumericUpDown duration, likePercent;
        readonly FlowLayoutPanel tikTokOptions;
        readonly Label description;
        CancellationTokenSource cancellation;
        internal bool HasWork => cancellation != null;
        internal SocialWarmupPanel(Preferences settings) {
            this.settings = settings; Dock = DockStyle.Fill; BackColor = Theme.Background;
            DashboardSessionState.Bind(this, () => {
                string service = Convert.ToString(network.SelectedItem);
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.Tag is DolphinBrowserProfile profile) {
                        Theme.MarkConnected(row.DefaultCellStyle, DashboardSessionState.IsConnected(profile.Id, service));
                        row.Cells["account"].ToolTipText = DashboardSessionState.Summary(profile.Id, service);
                    }
                grid.Invalidate();
            });
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, WrapContents = false, Padding = new Padding(8) };
            network = Theme.MakeCombo(new[] { "TikTok", "Instagram", "Threads" }); network.Width = 150; network.SelectedIndex = 0;
            network.SelectedIndexChanged += (s, e) => ReloadAccounts(); bar.Controls.Add(network);
            refresh = Theme.MakeButton("Обновить аккаунты", ghost: true, action: ReloadAccounts); bar.Controls.Add(refresh);
            start = Theme.MakeButton("Запустить автопрогрев", accent: true, action: async () => await Run()); bar.Controls.Add(start);
            bar.Controls.Add(Theme.MakeButton("Стоп", ghost: true, action: () => cancellation?.Cancel()));
            tikTokOptions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8), WrapContents = false };
            tikTokOptions.Controls.Add(new Label { Text = "Минут на профиль:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            duration = new NumericUpDown { Minimum = 1, Maximum = 1440, Width = 75, Value = Math.Max(1, Math.Min(1440, settings.TikTokAndroidWarmupMinutes)) };
            tikTokOptions.Controls.Add(duration);
            tikTokOptions.Controls.Add(new Label { Text = "Вероятность лайка, %:", AutoSize = true, Padding = new Padding(12, 5, 0, 0) });
            likePercent = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 65, Value = Math.Max(0, Math.Min(100, settings.TikTokAndroidWarmupLikePercent)) };
            tikTokOptions.Controls.Add(likePercent);
            duration.ValueChanged += (s, e) => SaveTikTokOptions(); likePercent.ValueChanged += (s, e) => SaveTikTokOptions();
            description = new Label { Dock = DockStyle.Top, Height = 62, Padding = new Padding(8), ForeColor = Theme.TextSecondary };
            grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            Theme.StyleGrid(grid);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "selected", HeaderText = "Выбрать", FillWeight = 30 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "account", HeaderText = "Аккаунт · Dolphin ID", ReadOnly = true });
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            log = Theme.MakeLogBox(); log.Dock = DockStyle.Bottom; log.Height = 170;
            Controls.Add(grid); Controls.Add(log); Controls.Add(description); Controls.Add(tikTokOptions); Controls.Add(bar);
            Disposed += (s, e) => cancellation?.Cancel(); ReloadAccounts();
        }
        void SaveTikTokOptions() {
            settings.TikTokAndroidWarmupMinutes = (int)duration.Value;
            settings.TikTokAndroidWarmupLikePercent = (int)likePercent.Value;
            try { Store.Save(settings); } catch (Exception error) { Append("Не удалось сохранить настройки прогрева: " + error.Message); }
        }
        internal void SelectNetwork(string service) { if (!HasWork) network.SelectedItem = service; ReloadAccounts(); }
        internal void ReloadAccounts() {
            if (grid == null || HasWork) return;
            string service = Convert.ToString(network.SelectedItem);
            tikTokOptions.Visible = service == "TikTok";
            description.Text = service == "TikTok"
                ? "Выбранные профили последовательно: привязанный Android → проверка назначенного VPN → сохранённая авторизованная сессия → лента For You. Случайные просмотры 8–28 секунд, прокрутка и лайки с выбранной вероятностью; уже поставленные лайки не снимаются. Вход и ручные проверки не автоматизируются; окно Android остаётся открытым."
                : "Выберите сохранённые аккаунты Dolphin → по 15 минут ленты последовательно. Случайные просмотры, паузы и прокрутка; без поиска, лайков, комментариев и подписок. Используется сессия мобильного окна приложения и прокси профиля, не cookies Dolphin.";
            grid.Rows.Clear();
            try {
                var observations = MetaProfileState.Load();
                var profiles = service == "TikTok"
                    ? (settings.TikTokAccounts ?? new List<TikTokAccount>()).Where(p => p != null).Select(p => new DolphinBrowserProfile { Id = p.ProfileId, Name = p.Name })
                    : MetaProfileState.LocalProfiles(settings, observations);
                string folder = Path.Combine(Store.Root, service == "TikTok" ? "tiktok" : "instagram", "web");
                var bound = service == "TikTok" ? AndroidProfileLauncher.BoundProfileIds() : null;
                foreach (var profile in profiles.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.Id ?? "", @"^\d+$") && (service == "TikTok" ? bound.Contains(p.Id) : Directory.Exists(Path.Combine(folder, p.Id)))).GroupBy(p => p.Id).Select(g => g.First()).OrderBy(p => p.Name)) {
                    int i = grid.Rows.Add(false, profile.Name + " · " + profile.Id); grid.Rows[i].Tag = profile;
                    Theme.MarkConnected(grid.Rows[i].DefaultCellStyle,DashboardSessionState.IsConnected(profile.Id,service));
                    grid.Rows[i].Cells["account"].ToolTipText=DashboardSessionState.Summary(profile.Id,service);
                }
                if (grid.Rows.Count == 0) Append(service == "TikTok" ? "Нет аккаунтов с привязанным Android. Добавьте привязку и войдите в TikTok вручную в Android." : "Нет сохранённых сессий " + service + ". Сначала добавьте аккаунт и войдите в мобильном окне сервиса.");
            } catch (Exception error) { Append("Ошибка чтения аккаунтов: " + error.Message); }
        }
        void Append(string line) { if (!IsDisposed) log.AppendText(line + Environment.NewLine); }
        internal async Task StopBeforeClose() {
            cancellation?.Cancel();
            while (HasWork) await Task.Delay(100);
        }
        async Task Run() {
            if (HasWork) return;
            grid.EndEdit();
            var selected = grid.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToBoolean(r.Cells[0].Value ?? false)).Select(r => (DolphinBrowserProfile)r.Tag).ToList();
            if (selected.Count == 0) { Append("Выберите хотя бы один аккаунт."); return; }
            string service = Convert.ToString(network.SelectedItem);
            cancellation = new CancellationTokenSource(); var ct = cancellation.Token;
            int minutes = (int)duration.Value, probability = (int)likePercent.Value;
            start.Enabled = refresh.Enabled = network.Enabled = grid.Enabled = tikTokOptions.Enabled = false;
            try {
                foreach (var profile in selected) {
                    ct.ThrowIfCancellationRequested();
                    if (service == "TikTok") {
                        Append("TikTok · " + profile.Name + ": Android, " + minutes + " мин, вероятность лайка " + probability + " %.");
                        await AndroidProfileLauncher.Warmup(settings, profile.Id, profile.Name, minutes, probability, ct, Append);
                        continue;
                    }
                    if (InstagramWebWindow.IsProfileOpen(profile.Id)) throw new InvalidOperationException("Профиль уже открыт или проверяется: " + profile.Name);
                    Append(service + " · " + profile.Name + ": получаю назначенный прокси…");
                    var route = await TelegramProxyCatalog.FetchDolphin(WindowsSupport.Unprotect(settings.ProtectedDolphinToken), new[] { new KeyValuePair<string, string>(profile.Id, profile.Name) }, includeCatalog: false);
                    ct.ThrowIfCancellationRequested();
                    var proxy = route.Proxies.SingleOrDefault();
                    if (proxy == null) throw new InvalidOperationException("Прокси профиля не получен; прямое подключение не используется.");
                    using (var window = new InstagramWebWindow(profile.Id, profile.Name, proxy.url)) {
                        window.OpenNetwork(service); window.Show(FindForm());
                        try { await window.RunWarmup(service, ct, Append); } finally { window.Close(); while (!window.IsDisposed) await Task.Delay(100); }
                    }
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { Append("Прогрев остановлен пользователем."); }
            catch (Exception error) { Append("ОШИБКА: " + error.Message); AppDiagnostics.Write(service, "", "", "warmup_error", error.Message, "error"); }
            finally { cancellation.Dispose(); cancellation = null; if (!IsDisposed) start.Enabled = refresh.Enabled = network.Enabled = grid.Enabled = tikTokOptions.Enabled = true; }
        }
    }
}
