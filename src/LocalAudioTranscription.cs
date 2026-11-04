using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VideoBatch {
    // Shared local speech runtime. Captions remain compatible with ElevenLabs
    // alignments, style presets and existing renderers.
    public static class LocalAudioTranscription {
        public const string Version = "2026-11-04-imported-audio-v1";
        static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        static string Root => Path.Combine(Store.Root, "subtitles");
        public static string CacheFile(string hash) => Path.Combine(Root, "cache", hash + ".json");
        public sealed class Runtime {
            public string python, models, modelFile, version;
        }
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static Runtime ReadRuntime() {
            try {
                var runtime = Json().Deserialize<Runtime>(File.ReadAllText(Path.Combine(Root, "runtime.json")));
                return runtime?.version == Version && File.Exists(runtime.python) && File.Exists(runtime.modelFile) && Directory.Exists(runtime.models) ? runtime : null;
            } catch { return null; }
        }
        static string Scripts() {
            foreach (string root in new[] { AppDomain.CurrentDomain.BaseDirectory }.Concat(AppPaths.CandidateInstallRoots())) {
                string folder = Path.Combine(root, "tools", "subtitles");
                if (File.Exists(Path.Combine(folder, "worker.py")) && File.Exists(Path.Combine(folder, "prepare.ps1"))) return folder;
            }
            throw new Exception("Не найдена папка tools/subtitles. Обновите EXE вместе с этой папкой из комплекта приложения.");
        }
        public static async Task PrepareAsync(IEnumerable<string> files, CancellationToken ct, Action<string> progress = null, bool force = false) {
            // Hashing/probing a large user file must never block the WinForms thread.
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                var missing = await Task.Run(() => (files ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => {
                        ct.ThrowIfCancellationRequested();
                        if (!File.Exists(path)) throw new FileNotFoundException("Не найдена озвучка", path);
                        return new { audio = path, hash = AssemblyFiles.Hash(path) };
                    }).GroupBy(x => x.hash).Select(g => g.First()).Where(x => force || !AssemblySpeechPlan.HasTiming(x.audio)).ToList(), ct).ConfigureAwait(false);
                if (missing.Count == 0) { progress?.Invoke("Субтитры уже сохранены — повторное распознавание не требуется."); return; }
                string scripts = Scripts();
                var runtime = ReadRuntime();
                if (runtime == null) {
                    progress?.Invoke("Первый запуск: готовлю локальное распознавание. Модель скачивается один раз…");
                    await RunProcess("powershell.exe", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(scripts, "prepare.ps1"), "-Root", Root, "-SharedRoot", Path.Combine(Store.Root, "highlights") }, ct, progress).ConfigureAwait(false);
                    runtime = ReadRuntime();
                    if (runtime == null) throw new Exception("Распознавание не подготовлено. Проверьте сообщение установки выше; нужен Python 3.11 x64.");
                }
                string jobs = Path.Combine(Root, "jobs"); Directory.CreateDirectory(jobs);
                string job = Path.Combine(jobs, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(job, Json().Serialize(new { models = runtime.models, items = missing.Select(x => new { x.audio, x.hash, output = CacheFile(x.hash) }).ToArray() }), new UTF8Encoding(false));
                try {
                    await RunProcess(runtime.python, new[] { "-u", Path.Combine(scripts, "worker.py"), "--job", job }, ct, progress).ConfigureAwait(false);
                    foreach (var item in missing) if (!AssemblySpeechPlan.HasTiming(item.audio))
                        throw new Exception("Не удалось сохранить субтитры: " + Path.GetFileName(item.audio));
                } finally { try { File.Delete(job); } catch { } }
            } finally { gate.Release(); }
        }
        static async Task RunProcess(string executable, IEnumerable<string> args, CancellationToken ct, Action<string> progress) {
            ct.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(executable, string.Join(" ", args.Select(Core.Quote))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["PYTHONUTF8"] = "1";
            using (var process = new Process { StartInfo = start }) {
                if (!process.Start()) throw new Exception("Не удалось запустить распознавание речи.");
                var errors = new StringBuilder(); string lastError = null;
                var output = Task.Run(async () => {
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null) {
                        string message = line, stage = "runtime";
                        try {
                            var record = Json().Deserialize<Dictionary<string, object>>(line);
                            if (record.ContainsKey("message")) message = Convert.ToString(record["message"]);
                            if (record.ContainsKey("stage")) stage = Convert.ToString(record["stage"]);
                            if (record.TryGetValue("percent", out object percent) && percent != null) message += " · " + Convert.ToInt32(percent) + "%";
                            if (stage == "error") lastError = message;
                        } catch { }
                        AppDiagnostics.Write("Субтитры", "", "speech", stage, message, stage == "error" ? "error" : "info");
                        progress?.Invoke(message);
                    }
                });
                var error = Task.Run(async () => {
                    string line;
                    while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) {
                        if (errors.Length < 6000) errors.AppendLine(line);
                        AppDiagnostics.Write("Субтитры", "", "speech", "runtime", line);
                    }
                });
                // Setup may own pip/Python children. Stop this task's process tree,
                // never other app, browser or Python sessions.
                using (ct.Register(() => {
                    Task.Run(() => {
                        try {
                            if (process.HasExited) return;
                            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                                using (var killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) killer.WaitForExit(5000);
                            } else process.Kill();
                        } catch { }
                    });
                })) {
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    await Task.WhenAll(output, error).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new Exception(lastError ?? (errors.Length > 0 ? errors.ToString() : "Распознавание завершилось с ошибкой. Проверьте журнал субтитров."));
                }
            }
        }
    }

    internal sealed class AudioSubtitleProgressDialog : Form {
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly Label status;
        readonly Button start, cancel;
        readonly ProgressBar progress;
        readonly string[] files;
        bool running, completed;
        public AudioSubtitleProgressDialog(IEnumerable<string> audio) {
            files = audio.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Text = "Субтитры из аудио"; ClientSize = new Size(620, 280); MinimumSize = Size; MaximizeBox = false; MinimizeBox = false;
            BackColor = Theme.Background; ForeColor = Theme.TextPrimary; Font = Theme.FontBody; StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Файлов озвучки: " + files.Length + ". Распознавание на вашем ПК.\nПервый запуск скачает модель речи; нужны интернет и Python 3.11 x64.\nОзвучка никуда не отправляется. Готовые субтитры используются повторно.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) }, 0, 0);
            status = new Label { Text = "Язык определяется автоматически. Текст распознавания можно проверить в предпросмотре.", Dock = DockStyle.Fill, AutoEllipsis = true }; layout.Controls.Add(status, 0, 1);
            progress = new ProgressBar { Dock = DockStyle.Fill, Visible = false, Style = ProgressBarStyle.Marquee }; layout.Controls.Add(progress, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            cancel = Theme.MakeButton("Закрыть", ghost: true, action: () => { if (running) cancellation.Cancel(); else { DialogResult = completed ? DialogResult.OK : DialogResult.Cancel; Close(); } });
            start = Theme.MakeButton("Распознать", accent: true, action: () => Start()); buttons.Controls.Add(cancel); buttons.Controls.Add(start); layout.Controls.Add(buttons, 0, 3);
            FormClosing += (s, e) => { if (running) { cancellation.Cancel(); e.Cancel = true; status.Text = "Останавливаю распознавание…"; } };
        }
        async void Start() {
            if (running) return; running = true; start.Enabled = false; cancel.Text = "Отменить"; progress.Visible = true;
            var updates = new Progress<string>(message => { if (!IsDisposed) status.Text = message; });
            try {
                await LocalAudioTranscription.PrepareAsync(files, cancellation.Token, message => ((IProgress<string>)updates).Report(message));
                status.Text = "Субтитры готовы. Нажмите «Готово» и выберите оформление."; completed = true;
            } catch (OperationCanceledException) { status.Text = "Распознавание остановлено. Готовые результаты сохранены."; }
            catch (Exception e) { status.Text = e.Message; AppDiagnostics.Write("Субтитры", "", "speech", "error", e.ToString(), "error"); }
            finally { running = false; progress.Visible = false; cancel.Text = completed ? "Готово" : "Закрыть"; start.Enabled = !cancellation.IsCancellationRequested && !completed; }
        }
        protected override void Dispose(bool disposing) { if (disposing) cancellation.Dispose(); base.Dispose(disposing); }
    }
}
