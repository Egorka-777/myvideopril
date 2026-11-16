using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        void OpenDifferenceReport() {
            Safe(() => {
                using (var picker = new OpenFileDialog { Title = "Выберите готовый ролик для просмотра анализа", Filter = "Готовое видео|*.mp4;*.mov;*.mkv;*.webm", InitialDirectory = Directory.Exists(template.Output) ? template.Output : "" }) {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    string output = picker.FileName;
                    if (!File.Exists(AssemblyOutputFiles.Report(output, ".difference.xml"))) RunDifferenceAnalysis(output);
                    else ShowDifferenceReport(output);
                }
            });
        }
        void RunDifferenceAnalysis(string output) {
            using (var ct = new System.Threading.CancellationTokenSource())
            using (var dialog = new Form { Text = "Анализ результата", ClientSize = new Size(600,160), StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Background, ForeColor = Theme.TextPrimary, Font = Theme.FontBody }) {
                var label = new Label { Dock = DockStyle.Fill, Padding = new Padding(18), Text = "Подготовка анализа…" };
                var cancel = Button("Отмена", () => ct.Cancel()); cancel.Dock = DockStyle.Bottom;
                dialog.Controls.Add(label); dialog.Controls.Add(cancel);
                bool completed = false;
                dialog.FormClosing += (s,e) => { if (!completed) { ct.Cancel(); e.Cancel = true; } };
                dialog.Shown += async (s,e) => {
                    try {
                        if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out string hint)) throw new Exception(hint);
                        var updates = new Progress<string>(message => { if (!label.IsDisposed) label.Text = message; });
                        await System.Threading.Tasks.Task.Run(() => AssemblyDifferenceAnalysis.AnalyzeSaved(ffmpeg, probe, output, ct.Token, message => ((IProgress<string>)updates).Report(message)));
                        completed = true; dialog.DialogResult = DialogResult.OK;
                    } catch (OperationCanceledException) { completed = true; dialog.DialogResult = DialogResult.Cancel; }
                    catch (Exception ex) { completed = true; dialog.DialogResult = DialogResult.Cancel; Safe(() => { throw ex; }); }
                    finally { dialog.Close(); }
                };
                if (dialog.ShowDialog(this) == DialogResult.OK) Safe(() => ShowDifferenceReport(output));
            }
        }
        void ShowDifferenceReport(string output) {
            string path = AssemblyOutputFiles.Report(output, ".difference.xml");
            if (!File.Exists(path)) throw new Exception("Для этого ролика анализ ещё не выполнялся. Запустите его кнопкой «Анализ результата…».");
            var report = AssemblyFiles.Load<AssemblyDifferenceReport>(path);
            using (var dialog = new Form { Text = "Анализ отличия · " + Path.GetFileName(output), StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(900, 680), MinimumSize = new Size(640, 460), BackColor = Theme.Background, ForeColor = Theme.TextPrimary }) {
                var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Font = Theme.FontBody, BackColor = Theme.Card, ForeColor = Theme.TextPrimary, Text = AssemblyDifferenceAnalysis.Format(report) };
                var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46 };
                bottom.Controls.Add(Button("Открыть видео", () => Ui.Open(output)));
                bottom.Controls.Add(Button("Закрыть", dialog.Close));
                dialog.Controls.Add(text); dialog.Controls.Add(bottom); dialog.ShowDialog(this);
            }
        }
        void ShowDifferenceBatchSummary(BatchResult result) {
            if (result.Outputs.Count == 0) return;
            var lines = result.Outputs.Select(output => {
                string path = AssemblyOutputFiles.Report(output,".difference.xml");
                if (!File.Exists(path)) return Path.GetFileName(output) + ": анализ отсутствует";
                var report = AssemblyFiles.Load<AssemblyDifferenceReport>(path);
                return Path.GetFileName(output) + ": " + (report.DifferencePercent.HasValue ? report.DifferencePercent.Value.ToString("0.0") + "% отличия" : "полная оценка недоступна");
            });
            using (var dialog = new Form { Text = "Анализ готовых роликов", ClientSize = new Size(740,420), StartPosition = FormStartPosition.CenterParent }) {
                var text = new TextBox { Dock=DockStyle.Fill, Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical, Text=string.Join(Environment.NewLine,lines) + Environment.NewLine + Environment.NewLine + "Локальный индекс по выборочным кадрам и аудио, не оценка платформ. Подробности и ограничения — в полном отчёте." };
                var actions = new FlowLayoutPanel { Dock=DockStyle.Bottom, Height=45 };
                actions.Controls.Add(Button("Отчёт последнего ролика", () => Safe(() => ShowDifferenceReport(result.Outputs.Last()))));
                actions.Controls.Add(Button("Закрыть",dialog.Close)); dialog.Controls.Add(text); dialog.Controls.Add(actions); dialog.ShowDialog(this);
            }
        }
        void AddDifferenceReportButton(FlowLayoutPanel actions) {
            var button = Button("Анализ результата…", OpenDifferenceReport); button.Name = "OutputDifference";
            actions.Controls.Add(button);
        }
    }
}
