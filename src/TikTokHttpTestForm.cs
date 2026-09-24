using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch.TikTokHttpTest {
    public sealed class TikTokHttpTestForm : Form {
        readonly ComboBox account = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 690 };
        readonly TextBox video = new TextBox { Width = 600, ReadOnly = true };
        readonly TextBox caption = new TextBox { Width = 690, Multiline = true, Height = 72 };
        readonly RadioButton modeHttp = new RadioButton { Text = "HTTP", AutoSize = true, Checked = true };
        readonly RadioButton modeStudio = new RadioButton { Text = "Studio", AutoSize = true };
        readonly DateTimePicker schedule = new DateTimePicker { Width = 260, Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy HH:mm", ShowUpDown = true };
        readonly CheckBox useSchedule = new CheckBox { Text = "Запланировать", AutoSize = true };
        readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9) };
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
        readonly Button run = new Button { Text = "Запустить тест", AutoSize = true, Height = 38 };
        readonly Button cancel = new Button { Text = "Стоп", AutoSize = true, Height = 38, Enabled = false };
        SettingsSnapshot snapshot;
        Process worker;
        CancellationTokenSource cancellation;

        public TikTokHttpTestForm() {
            Text = "VideoBatch · TikTok HTTP/Studio · изолированный тест";
            ClientSize = new Size(820, 720);
            MinimumSize = new Size(760, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            Padding = new Padding(16);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10 };
            for (int i = 0; i < 9; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            root.Controls.Add(new Label { Text = "TikTok HTTP test — один аккаунт, одно видео", AutoSize = true, Font = new Font("Segoe UI", 16, FontStyle.Bold) }, 0, 0);
            root.Controls.Add(new Label { Text = "Рабочий settings.xml не изменяется. Подробная диагностика без секретов.", AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.DarkGreen }, 0, 1);
            root.Controls.Add(Row("Аккаунт", account), 0, 2);
            var pick = new Button { Text = "Выбрать видео…", AutoSize = true };
            pick.Click += (s, e) => PickVideo();
            var videoRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            videoRow.Controls.Add(video);
            videoRow.Controls.Add(pick);
            root.Controls.Add(Row("Видео", videoRow), 0, 3);
            root.Controls.Add(Row("Подпись", caption), 0, 4);
            var modeRow = new FlowLayoutPanel { AutoSize = true };
            modeRow.Controls.Add(modeHttp);
            modeRow.Controls.Add(modeStudio);
            root.Controls.Add(Row("Режим", modeRow), 0, 5);
            var schedRow = new FlowLayoutPanel { AutoSize = true };
            schedRow.Controls.Add(useSchedule);
            schedRow.Controls.Add(schedule);
            root.Controls.Add(Row("Публикация", schedRow), 0, 6);
            var actions = new FlowLayoutPanel { AutoSize = true };
            actions.Controls.Add(run);
            actions.Controls.Add(cancel);
            root.Controls.Add(actions, 0, 7);
            root.Controls.Add(progress, 0, 8);
            root.Controls.Add(log, 0, 9);

            schedule.Value = DateTime.Now.AddHours(2);
            schedule.MinDate = DateTime.Now.AddMinutes(20);
            useSchedule.CheckedChanged += (s, e) => schedule.Enabled = useSchedule.Checked;
            schedule.Enabled = false;
            run.Click += async (s, e) => await RunTest();
            cancel.Click += (s, e) => CancelWorker();
            FormClosing += (s, e) => CancelWorker();
            Shown += (s, e) => LoadSnapshot();
        }

        static Control Row(string caption, Control control) {
            var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 5, 0, 5) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            row.Controls.Add(control, 1, 0);
            return row;
        }

        void LoadSnapshot() {
            try {
                snapshot = SettingsSnapshot.CreateReadOnlyCopy();
                var accounts = snapshot.Preferences.TikTokAccounts
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ProfileId))
                    .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                account.Items.Clear();
                account.Items.AddRange(accounts.Cast<object>().ToArray());
                if (account.Items.Count > 0) account.SelectedIndex = 0;
                Append("Снимок настроек создан. TikTok-аккаунтов: " + accounts.Length + ".");
                if (accounts.Length == 0) throw new Exception("В settings.xml нет TikTok-аккаунтов с Profile ID.");
            } catch (Exception error) {
                run.Enabled = false;
                Append("ОШИБКА: " + error.Message);
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void PickVideo() {
            using (var dialog = new OpenFileDialog { Filter = "Видео|*.mp4;*.mov;*.mkv;*.webm|Все|*.*" }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                video.Text = dialog.FileName;
                if (string.IsNullOrWhiteSpace(caption.Text)) caption.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }

        async Task RunTest() {
            if (worker != null) return;
            try {
                if (snapshot == null) throw new Exception("Снимок не загружен.");
                var acc = account.SelectedItem as ImportedTikTokAccount;
                if (acc == null) throw new Exception("Выберите один TikTok-аккаунт.");
                if (string.IsNullOrWhiteSpace(acc.ExpectedIp)) throw new Exception("Нет сохранённого IP. Проверьте профиль в основном VideoBatch.");
                if (!File.Exists(video.Text)) throw new Exception("Выберите видео.");
                string cap = (caption.Text ?? "").Trim();
                if (cap.Length < 1 || cap.Length > 2200) throw new Exception("Подпись 1–2200 символов.");
                string token = SettingsSnapshot.Unprotect(snapshot.Preferences.ProtectedDolphinToken);
                if (string.IsNullOrWhiteSpace(token)) throw new Exception("Нет Dolphin API-токена.");
                snapshot.AssertSourceUnchanged();

                bool http = modeHttp.Checked;
                string publishMode = useSchedule.Checked ? "scheduled" : "immediate";
                long unix = useSchedule.Checked ? new DateTimeOffset(schedule.Value).ToUnixTimeSeconds() : 0;
                if (useSchedule.Checked && schedule.Value <= DateTime.Now.AddMinutes(15))
                    throw new Exception("Планирование минимум через 15 минут.");

                if (MessageBox.Show(this, "Реальная загрузка 1 ролика (" + (http ? "HTTP" : "Studio") + "). Продолжить?",
                    "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                Directory.CreateDirectory(TikTokHttpTestPaths.Jobs);
                string jobPath = Path.Combine(TikTokHttpTestPaths.Jobs, "tiktok-test-" + Guid.NewGuid().ToString("N") + ".json");
                cancellation = new CancellationTokenSource();
                SetBusy(true);
                progress.Value = 0;

                if (http) {
                    var dto = new HttpTikTokTestJob {
                        profileId = acc.ProfileId.Trim(),
                        expectedIp = acc.ExpectedIp.Trim(),
                        localPort = snapshot.Preferences.DolphinPort,
                        market = acc.Market ?? "RU",
                        publishMode = publishMode,
                        items = new[] {
                            new HttpTikTokTestItem {
                                localJobId = "test-1", video = Path.GetFullPath(video.Text), caption = cap,
                                publishMode = publishMode, scheduledUnixSeconds = unix
                            }
                        }
                    };
                    using (var output = File.Create(jobPath)) new DataContractJsonSerializer(typeof(HttpTikTokTestJob)).WriteObject(output, dto);
                    await RunWorker(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "uploader", "worker-tiktok-http.js"), jobPath, token, cancellation.Token);
                } else {
                    File.WriteAllText(jobPath, SerializeStudioJob(
                        acc.ProfileId.Trim(), acc.ExpectedIp.Trim(), snapshot.Preferences.DolphinPort,
                        Path.GetFullPath(video.Text), cap), Encoding.UTF8);
                    await RunWorker(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "uploader", "worker-tiktok.js"), jobPath, token, cancellation.Token);
                }
                try { File.Delete(jobPath); } catch { }
                snapshot.AssertSourceUnchanged();
            } catch (OperationCanceledException) { Append("Остановлено."); }
            catch (Exception error) { Append("ОШИБКА: " + error.Message); MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { SetBusy(false); cancellation?.Dispose(); cancellation = null; worker = null; }
        }

        static string SerializeStudioJob(string profileId, string expectedIp, int localPort, string videoPath, string cap) {
            var sb = new StringBuilder();
            sb.Append("{\"profileId\":\"").Append(Escape(profileId)).Append("\"");
            sb.Append(",\"expectedIp\":\"").Append(Escape(expectedIp)).Append("\"");
            sb.Append(",\"localPort\":").Append(localPort);
            sb.Append(",\"skipQueueDelay\":true");
            sb.Append(",\"items\":[{\"video\":\"").Append(Escape(videoPath)).Append("\"");
            sb.Append(",\"caption\":\"").Append(Escape(cap)).Append("\"}]}");
            return sb.ToString();
        }

        static string Escape(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

        async Task RunWorker(string script, string jobPath, string token, CancellationToken ct) {
            string root = Path.GetDirectoryName(script);
            string node = Path.Combine(root, "node.exe");
            if (!File.Exists(node) || !File.Exists(script)) throw new Exception("Нет node.exe или worker рядом с EXE.");
            var info = new ProcessStartInfo(node, Quote(script) + " " + Quote(jobPath)) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = root
            };
            info.EnvironmentVariables["VIDEOBATCH_DOLPHIN_TOKEN"] = token;
            worker = new Process { StartInfo = info, EnableRaisingEvents = true };
            if (!worker.Start()) throw new Exception("Не удалось запустить worker.");
            var stderr = worker.StandardError.ReadToEndAsync();
            var serializer = new DataContractJsonSerializer(typeof(WorkerMessage));
            string reported = "";
            using (ct.Register(() => { try { if (worker != null && !worker.HasExited) worker.Kill(); } catch { } })) {
                string line;
                bool success = false;
                while ((line = await worker.StandardOutput.ReadLineAsync()) != null) {
                    WorkerMessage msg = null;
                    try { using (var m = new MemoryStream(Encoding.UTF8.GetBytes(line))) msg = (WorkerMessage)serializer.ReadObject(m); } catch { }
                    if (msg == null) { Append(line); continue; }
                    Append((msg.stage ?? "log") + ": " + (msg.text ?? msg.error ?? ""));
                    progress.Value = (int)Math.Max(0, Math.Min(100, msg.percent));
                    if (msg.stage == "done" && msg.success) success = true;
                    if (msg.stage == "error" || msg.stage == "manual_check") reported = msg.error ?? msg.text ?? reported;
                }
                await Task.Run(() => worker.WaitForExit());
                string err = await stderr;
                if (!string.IsNullOrWhiteSpace(reported)) throw new Exception(reported);
                if (worker.ExitCode != 0) throw new Exception(string.IsNullOrWhiteSpace(err) ? "Worker exit " + worker.ExitCode : err.Trim());
                if (!success) throw new Exception("Worker завершился без подтверждения success.");
            }
        }

        static string Quote(string v) { return "\"" + (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        void Append(string text) {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Append), text); return; }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            try { Directory.CreateDirectory(TikTokHttpTestPaths.TestRoot); File.AppendAllText(TikTokHttpTestPaths.Log, text + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        void SetBusy(bool busy) {
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetBusy), busy); return; }
            run.Enabled = !busy; cancel.Enabled = busy; account.Enabled = !busy; video.Enabled = !busy; caption.Enabled = !busy;
            modeHttp.Enabled = !busy; modeStudio.Enabled = !busy; schedule.Enabled = !busy && useSchedule.Checked; useSchedule.Enabled = !busy;
        }

        void CancelWorker() {
            cancellation?.Cancel();
            try { if (worker != null && !worker.HasExited) worker.Kill(); } catch { }
        }
    }
}
