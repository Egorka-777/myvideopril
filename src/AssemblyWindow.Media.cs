using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        void ImageScopeMenu() {
            ShowChoices("Где показывать изображение?", packChips, new[] {
                Tuple.Create("Эта сцена", (Action)(() => SetImageScope(false)), !globalEditing),
                Tuple.Create("Общее изображение · весь ролик", (Action)(() => SetImageScope(true)), globalEditing),
                Tuple.Create(globalEditing ? "Перенести слой в сцену" : "Перенести слой в общее изображение", (Action)MoveImageScope, false)
            });
        }
        void SetImageScope(bool whole) { globalEditing = whole; layerIndex = EditableLayers.Count > 0 ? 0 : -1; RefreshChips(); LoadLayer(); RefreshCanvas(); }
        void MoveImageScope() {
            var layer = SelectedLayer;
            if (layer == null || SelectedScene == null) return;
            var target = globalEditing ? SelectedScene.Layers : template.GlobalLayers;
            if (target.Count >= 8) throw new Exception("В выбранной области уже 8 слоёв.");
            EditableLayers.Remove(layer); target.Add(layer); globalEditing = !globalEditing; layerIndex = target.Count - 1;
            layer.Start = 0; layer.End = 0;
            RefreshChips(); LoadLayer(); Changed(false); Save();
        }
        Form MediaDialog(string title, int height) {
            return new Form { Text = title, ClientSize = new Size(620, height), MinimumSize = new Size(620, height), StartPosition = FormStartPosition.CenterParent, Font = Theme.FontBody, BackColor = Theme.Background, ForeColor = Theme.TextPrimary };
        }
        FlowLayoutPanel MediaBody(Form form) {
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(18) };
            form.Controls.Add(body); return body;
        }
        void OpenVideoEditor() {
            LeaveSubtitleWorkspaceIfOpen();
            using (var form = MediaDialog("Видео · источник и два экрана", 580)) {
                var body = MediaBody(form);
                body.Controls.Add(new Label { Text = "Откуда брать основное видео", AutoSize = true, Font = Theme.FontCardTitle });
                var scope = new ComboBox { Name = "VideoScope", DropDownStyle = ComboBoxStyle.DropDownList, Width = 560 };
                scope.Items.AddRange(new object[] { "Отрывок для каждой сцены", "Одно непрерывное видео на весь ролик", "Отдельная папка у выбранной сцены" });
                scope.SelectedIndex = template.ContinuousVideo ? 1 : SelectedScene?.VideoMode == "own" ? 2 : 0;
                body.Controls.Add(scope);
                var primaryCount = new Label { AutoSize = false, Width = 560, Height = 22, ForeColor = Theme.TextSecondary };
                var secondaryCount = new Label { AutoSize = false, Width = 560, Height = 22, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 0, 0, 8) };
                void RefreshCounts() {
                    int primary = Pool(template.Videos, AssemblyFiles.VideoExtensions).Count;
                    if (scope.SelectedIndex == 2 && SelectedScene != null) primary = Pool(SelectedScene.Videos, AssemblyFiles.VideoExtensions).Count;
                    int secondary = Pool(template.SecondaryVideos, AssemblyFiles.VideoExtensions).Count;
                    primaryCount.Text = "Основная половина · " + primary + " файлов";
                    secondaryCount.Text = template.SplitLayout == "none"
                        ? "Вторая половина · выберите деление ниже"
                        : "Вторая половина · " + secondary + " файлов";
                    secondaryCount.ForeColor = template.SplitLayout != "none" && secondary == 0 ? Theme.Error : Theme.TextSecondary;
                }
                RefreshCounts();
                body.Controls.Add(primaryCount);
                var files = Button("📁  Основное видео · открыть папку", () => {
                    if (scope.SelectedIndex == 2 && SelectedScene != null) OpenPool(3); else OpenPool(0);
                    RefreshCounts();
                }); files.Width = 560; body.Controls.Add(files);
                body.Controls.Add(new Label { Text = "Два экрана", AutoSize = true, Margin = new Padding(0, 14, 0, 6), Font = Theme.FontCardTitle });
                var picker = new AssemblySplitLayoutPicker { Width = 560, SplitLayout = template.SplitLayout };
                body.Controls.Add(picker);
                body.Controls.Add(secondaryCount);
                var secondary = Button("📁  Второе видео · добавить файлы", () => {
                    var paths = PickFiles(0); if (paths == null) return;
                    string source = template.SecondaryVideos;
                    AssemblyWorkspace.Import(template, ref source, "Второе-видео-" + Guid.NewGuid().ToString("N").Substring(0, 6), paths, AssemblyFiles.VideoExtensions, false);
                    template.SecondaryVideos = source; Changed(true); Save(); RefreshCounts();
                }); secondary.Width = 560; body.Controls.Add(secondary);
                var softness = Value(0, 50, 0); softness.Value = (decimal)(template.SplitSoftness * 100); softness.Visible = false;
                body.Controls.Add(new AssemblySlider("Смягчение стыка", softness, "%") { Width = 560, Margin = new Padding(0, 8, 0, 0) });
                body.Controls.Add(new Label {
                    Text = "Долю 70/30 и масштаб каждой половины настраивайте в превью: тяните линию и крутите колёсико над нужной частью.",
                    Width = 560, Height = 40, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 4, 0, 0)
                });
                var removeSecond = Button("Убрать деление", () => { template.SecondaryVideos = ""; template.SplitLayout = "none"; picker.SplitLayout = "none"; Changed(true); Save(); RefreshCounts(); });
                removeSecond.Width = 560; body.Controls.Add(removeSecond);
                scope.SelectedIndexChanged += (s, e) => {
                    template.ContinuousVideo = scope.SelectedIndex == 1;
                    if (scope.SelectedIndex == 2) { if (SelectedScene != null && Pool(SelectedScene.Videos, AssemblyFiles.VideoExtensions).Count > 0) SelectedScene.VideoMode = "own"; }
                    else if (SelectedScene != null) SelectedScene.VideoMode = "shared";
                    Changed(true); Save(); RefreshCounts();
                };
                picker.LayoutSelected += layout => {
                    template.SplitLayout = layout;
                    Changed(true); Save(); RefreshCounts();
                };
                softness.ValueChanged += (s, e) => { template.SplitSoftness = (double)softness.Value / 100; Changed(true); };
                body.Controls.Add(Button("Готово", () => { Save(); form.Close(); RefreshInfo(); RequestBackground(); }, true));
                form.ShowDialog(this);
            }
        }
        void ImportSpeechToSceneAudio(string[] paths) {
            if (SelectedScene == null) throw new Exception("Выберите сцену слева.");
            paths = PathUtil.NormalizeAll(paths);
            if (paths == null || paths.Length == 0) return;
            string source = SelectedScene.Music;
            AssemblyWorkspace.Import(template, ref source, Path.Combine(SelectedScene.Folder, "Аудио"), paths, AssemblyFiles.MusicExtensions, false);
            SelectedScene.Music = source;
            Changed(false); Save();
        }

        Panel audioWorkspace;
        void ShowSceneWorkspace() {
            LeaveSubtitleWorkspaceIfOpen();
            if (audioWorkspace != null) audioWorkspace.Visible = false;
            if (sceneEditor != null) { sceneEditor.Visible = true; sceneEditor.BringToFront(); }
        }
        FlowLayoutPanel SettingsWorkspace(string title, string hint) {
            LeaveSubtitleWorkspaceIfOpen();
            sceneEditor.Visible = false;
            if (audioWorkspace != null) audioWorkspace.Dispose();
            audioWorkspace = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(12) };
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            audioWorkspace.Controls.Add(body); contentHost.Controls.Add(audioWorkspace); audioWorkspace.BringToFront();
            body.Controls.Add(new Label { Text = title, Font = Theme.FontCardTitle, AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
            body.Controls.Add(new Label { Text = hint, Height = 56, Width = 360, ForeColor = Theme.TextSecondary });
            void ResizeSettings() { foreach (Control c in body.Controls) c.Width = Math.Max(240, body.ClientSize.Width - 22); }
            body.SizeChanged += (s, e) => ResizeSettings();
            BeginInvoke((Action)ResizeSettings);
            return body;
        }
        void OpenVoiceWorkspace() {
            LeaveSubtitleWorkspaceIfOpen();
            using (var form = MediaDialog("Озвучки · " + AssemblyProfile.ShortLabel(studioProfile), 360)) {
                var body = MediaBody(form);
                body.Controls.Add(new Label { Text = "Один файл на ролик. Субтитры синхронизируются с выбранной озвучкой.", Width = 560, Height = 40, ForeColor = Theme.TextSecondary });
                body.Controls.Add(new Label { Text = "Готовых вариантов: " + AssemblySpeechPlan.AvailableLines(template).Count, Height = 30, ForeColor = Theme.TextSecondary });
                body.Controls.Add(Button("Тексты и генерация ElevenLabs", OpenVoiceEditor, true));
                body.Controls.Add(Button("Использовать варианты ElevenLabs", () => { template.Voiceover = ""; Changed(false); Save(); RequestTimingHints(); RefreshInfo(); form.Close(); }));
                body.Controls.Add(Button("Выбрать отдельный файл озвучки", () => {
                    var paths = PickFiles(1); if (paths == null || paths.Length == 0) return;
                    template.Voiceover = paths[0]; Changed(false); Save(); RequestTimingHints(); RefreshInfo(); form.Close();
                }));
                body.Controls.Add(new Label { Text = string.IsNullOrWhiteSpace(template.Voiceover) ? "По одному варианту на каждый ролик." : "Выбран: " + Path.GetFileName(template.ResolveAudio(template.Voiceover)), Height = 52, ForeColor = Theme.TextSecondary });
                body.Controls.Add(Button("Готово", () => form.Close(), true));
                form.ShowDialog(this);
            }
        }
        void OpenAudioEditor() {
            LeaveSubtitleWorkspaceIfOpen();
            using (var form = MediaDialog("Музыка · " + AssemblyProfile.ShortLabel(studioProfile), 460)) {
                var body = MediaBody(form);
                body.Controls.Add(new Label { Text = "Музыка отдельно от озвучки. Голос всегда 100%.", Width = 560, Height = 36, ForeColor = Theme.TextSecondary });
                var rollMusic = Button("Музыка на весь ролик · " + Pool(template.Music, AssemblyFiles.MusicExtensions).Count + " файлов", () => OpenPool(1)); rollMusic.Width = 560; body.Controls.Add(rollMusic);
                loading = true; volume.Value = (decimal)(template.MusicVolume * 100); loading = false;
                body.Controls.Add(new AssemblySlider("Громкость музыки на весь ролик", volume, "%") { Width = 560 });
                body.Controls.Add(new Label { Text = SelectedScene == null ? "Выбери сцену для локального аудио." : "Аудио только для «" + SceneLabel(SelectedScene, sceneIndex) + "»", Width = 560, Height = 30, Margin = new Padding(0, 14, 0, 0) });
                var sceneMusic = Button("Добавить аудио выбранной сцены", () => {
                    if (SelectedScene == null) return;
                    var paths = PickFiles(1); if (paths == null) return;
                    string source = SelectedScene.Music;
                    AssemblyWorkspace.Import(template, ref source, Path.Combine(SelectedScene.Folder, "Аудио"), paths, AssemblyFiles.MusicExtensions, false);
                    SelectedScene.Music = source; Changed(false); Save();
                }); sceneMusic.Width = 560; body.Controls.Add(sceneMusic);
                var localVolume = Value(0, 200, 0); localVolume.Value = (decimal)((SelectedScene?.MusicVolume ?? 1) * 100); localVolume.Visible = false; body.Controls.Add(localVolume);
                body.Controls.Add(new AssemblySlider("Громкость аудио сцены", localVolume, "%") { Width = 560 });
                localVolume.ValueChanged += (s, e) => { if (SelectedScene != null) { SelectedScene.MusicVolume = (double)localVolume.Value / 100; Changed(false); } };
                body.Controls.Add(Button("Убрать аудио сцены", () => { if (SelectedScene != null) { SelectedScene.Music = ""; Changed(false); Save(); } }));
                body.Controls.Add(Button("Готово", () => { Save(); RefreshInfo(); form.Close(); }, true));
                form.ShowDialog(this);
            }
        }
    }
}
