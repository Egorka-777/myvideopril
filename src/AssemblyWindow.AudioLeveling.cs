using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        void AddAudioLevelingControls(FlowLayoutPanel body, Form form) {
            body.Controls.Add(new Label { Text = "ЗВУК ОСНОВНЫХ ВИДЕО", Width = 560, Height = 26, Font = Theme.FontCardTitle });
            body.Controls.Add(new Label { Text = "Проверить → выровнять по самому громкому видео. Громкость звука затем задаётся отдельно для каждой сцены. Замеряются первые 20 секунд, у коротких видео — весь файл. Исходники не изменяются.", Width = 560, Height = 58, ForeColor = Theme.TextSecondary });
            var state = new Label { Width = 560, Height = 40, ForeColor = Theme.TextSecondary,
                Text = template.NativeAudioLeveled ? "Выравнивание включено для проверенных видео." : "Выравнивание ещё не применено." };
            var results = new TextBox { Name="AudioLevelResults", Width=560, Height=120, Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical, BackColor=Theme.Card, ForeColor=Theme.TextPrimary };
            List<AssemblyAudioLevel> measured = null;
            bool running = false, closeRequested = false;
            var cancel = new CancellationTokenSource();
            var actions = new FlowLayoutPanel { Width=560, Height=40, WrapContents=false };
            Button check = null, align = null;
            align = Button("Выровнять", () => Safe(() => {
                if (measured == null) return;
                AssemblyAudioLeveling.Align(template,measured);
                Changed(false); Save(); state.Text="Выравнивание применено. Громкость настраивается в блоке выбранной сцены.";
            })); align.Enabled=false; align.Name="AudioLevelAlign";
            check = Button("Проверить", async () => {
                try {
                    if (!ToolsLocator.TryResolve(out string ffmpeg,out string probe,out string hint)) throw new Exception(hint);
                    running=true; check.Enabled=false; align.Enabled=false; measured=null;
                    var snapshot=Store.Clone(template);
                    var progress=new Progress<string>(message => { if (!state.IsDisposed) state.Text=message; });
                    measured=await AssemblyAudioLeveling.Measure(snapshot,ffmpeg,probe,cancel.Token,message => ((IProgress<string>)progress).Report(message));
                    results.Text=string.Join(Environment.NewLine,measured.Select(x => Path.GetFileName(x.Path)+": "+(!string.IsNullOrWhiteSpace(x.Error) ? x.Error : !x.HasAudio || x.IsSilent ? "нет измеряемого звука" : x.Loudness.ToString("0.0")+" · измеренный уровень")));
                    bool errors=measured.Any(x => !string.IsNullOrWhiteSpace(x.Error));
                    align.Enabled=!errors && measured.Any(x => x.HasAudio && !x.IsSilent);
                    state.Text=errors ? "Есть ошибки проверки. Выравнивание не применено." : measured.Any(x => x.HasAudio && !x.IsSilent) ? "Проверено: "+measured.Count+". Самый громкий уровень: "+measured.Where(x=>x.HasAudio && !x.IsSilent).Max(x=>x.Loudness).ToString("0.0")+". Нажмите «Выровнять»." : "В проверенных видео нет измеряемого звука.";
                } catch (OperationCanceledException) { state.Text="Проверка остановлена."; }
                catch (Exception e) { state.Text="Ошибка проверки: "+e.Message; AppDiagnostics.Write("Сборка",studioProfile,"","audio-level",e.ToString(),"error"); }
                finally { running=false; check.Enabled=true; if(closeRequested) form.Close(); }
            }); check.Name="AudioLevelCheck";
            actions.Controls.Add(check); actions.Controls.Add(align);
            body.Controls.Add(actions); body.Controls.Add(state); body.Controls.Add(results);
            var enabled = new CheckBox { Text="Применять выравнивание проверенных видео", Width=560, Height=30, Checked=template.NativeAudioLeveled, ForeColor=Theme.TextPrimary };
            enabled.CheckedChanged += (s,e) => { template.NativeAudioLeveled=enabled.Checked; Changed(false); Save(); };
            enabled.Enabled=template.NativeAudioLevels != null && template.NativeAudioLevels.Any(x=>x.HasAudio && !x.IsSilent && string.IsNullOrWhiteSpace(x.Error));
            align.Click += (s,e) => { enabled.Enabled=template.NativeAudioLevels != null && template.NativeAudioLevels.Any(x=>x.HasAudio && !x.IsSilent && string.IsNullOrWhiteSpace(x.Error)); enabled.Checked=template.NativeAudioLeveled; };
            body.Controls.Add(enabled);
            form.FormClosing += (s,e) => { if (running) { closeRequested=true; cancel.Cancel(); e.Cancel=true; state.Text="Останавливаю проверку…"; } };
            form.FormClosed += (s,e) => cancel.Dispose();
        }
    }
}
