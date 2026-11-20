using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        void ImageScopeMenu() {
            ShowChoices("Где показывать объект?", packChips, new[] {
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
            var area = Screen.FromControl(this).WorkingArea;
            return new Form { Text = title, ClientSize = new Size(720, Math.Min(height, area.Height - 100)), MinimumSize = new Size(580, 460), StartPosition = FormStartPosition.CenterParent, Font = Theme.FontBody, BackColor = Theme.Background, ForeColor = Theme.TextPrimary };
        }
        FlowLayoutPanel MediaBody(Form form) {
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24, 20, 24, 16) };
            form.Controls.Add(body);
            bool resizing = false;
            void ResizeBody() {
                if (resizing) return;
                resizing = true;
                try { SizeMediaFlow(body, Math.Max(240, body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth)); }
                finally { resizing = false; }
            }
            body.ClientSizeChanged += (s, e) => ResizeBody();
            body.Layout += (s, e) => ResizeBody();
            form.Shown += (s, e) => ResizeBody();
            return body;
        }
        static void SizeMediaFlow(FlowLayoutPanel flow, int width) {
            foreach (Control control in flow.Controls) {
                if (control is NumericUpDown) continue;
                control.Width = Math.Max(100, width - control.Margin.Horizontal);
                if (control is FlowLayoutPanel constrained) constrained.MaximumSize = new Size(control.Width, 0);
                if (control is Label label) {
                    label.AutoSize = false;
                    label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 8;
                } else if (control is Button button) {
                    button.AutoSize = false;
                    button.Height = Math.Max(40, TextRenderer.MeasureText(button.Text, button.Font, new Size(Math.Max(80, button.Width - 32), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 18);
                } else if (control is CheckBox check) {
                    check.AutoSize = false;
                    check.Height = Math.Max(30, TextRenderer.MeasureText(check.Text, check.Font, new Size(Math.Max(80, check.Width - 28), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + 10);
                } else if (control is FlowLayoutPanel nested && nested.FlowDirection == FlowDirection.TopDown) {
                    SizeMediaFlow(nested, control.Width - nested.Padding.Horizontal);
                } else if (control is Panel card && card.Controls.Count == 1 && card.Controls[0] is FlowLayoutPanel content) {
                    content.MaximumSize = new Size(card.Width - card.Padding.Horizontal, 0);
                    content.Width = card.Width - card.Padding.Horizontal;
                    SizeMediaFlow(content, content.Width);
                    content.PerformLayout();
                    card.Height = content.PreferredSize.Height + card.Padding.Vertical;
                }
            }
        }
        FlowLayoutPanel MediaSection(FlowLayoutPanel body, string title, string hint) {
            var card = Theme.MakeCard(); card.Width = 620;
            card.Margin = new Padding(0, 0, 0, 16);
            var content = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Location = new Point(card.Padding.Left, card.Padding.Top) };
            card.Controls.Add(content); body.Controls.Add(card);
            content.Controls.Add(new Label { Text = title, Font = Theme.FontCardTitle, Width = 560, Margin = new Padding(0, 0, 0, 4) });
            if (!string.IsNullOrEmpty(hint)) content.Controls.Add(new Label { Text = hint, ForeColor = Theme.TextSecondary, Width = 560, Margin = new Padding(0, 0, 0, 12) });
            return content;
        }
        static void MediaFooter(Form form, Button done) {
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(24, 16, 24, 16), BackColor = Theme.Card };
            done.AutoSize = false; done.Width = 144; done.Dock = DockStyle.Right; done.Margin = Padding.Empty;
            footer.Controls.Add(done); form.Controls.Add(footer); footer.SendToBack();
        }
        void OpenVideoEditor() {
            LeaveSubtitleWorkspaceIfOpen();
            using (var form = MediaDialog("Фоновое видео · источник и два экрана", 640)) {
                var page = MediaBody(form);
                var body = MediaSection(page, "Фоновое видео", "Основа ролика под объектами сцен. Выберите источник и настройте размещение.");
                var scope = new ComboBox { Name = "VideoScope", DropDownStyle = ComboBoxStyle.DropDownList, Width = 560 };
                scope.Items.AddRange(new object[] { "Отрывок для каждой сцены", "Одно непрерывное видео на весь ролик", "Отдельная папка у выбранной сцены" });
                scope.SelectedIndex = template.ContinuousVideo ? 1 : SelectedScene?.VideoMode == "own" ? 2 : 0;
                body.Controls.Add(scope);
                var primaryCount = new Label { AutoSize = false, Width = 560, Height = 22, ForeColor = Theme.TextSecondary };
                var secondaryCount = new Label { AutoSize = false, Width = 560, Height = 22, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 0, 0, 8) };
                var firstList = new ListBox { Width = 560, Height = 65, BackColor = Theme.Background, ForeColor = Theme.TextPrimary, HorizontalScrollbar = true };
                var secondList = new ListBox { Width = 560, Height = 65, BackColor = Theme.Background, ForeColor = Theme.TextPrimary, HorizontalScrollbar = true };
                var sharedPool = new CheckBox { Text = "Общая пачка: верх и низ выбираются из одного набора", AutoSize = true, ForeColor = Theme.TextPrimary, Checked = template.SplitSharedPool, Margin = new Padding(0, 8, 0, 4) };
                var fixHalves = new CheckBox { Text = "Менять половины местами между роликами", AutoSize = true, ForeColor = Theme.TextPrimary, Checked = !template.SplitFixHalves, Margin = new Padding(0, 0, 0, 8) };
                void RefreshCounts() {
                    int primary = Pool(template.Videos, AssemblyFiles.VideoExtensions).Count;
                    if (scope.SelectedIndex == 2 && SelectedScene != null) primary = Pool(SelectedScene.Videos, AssemblyFiles.VideoExtensions).Count;
                    int secondary = Pool(template.SecondaryVideos, AssemblyFiles.VideoExtensions).Count;
                    firstList.Items.Clear(); secondList.Items.Clear();
                    firstList.Items.AddRange(Pool(scope.SelectedIndex == 2 && SelectedScene != null ? SelectedScene.Videos : template.Videos, AssemblyFiles.VideoExtensions).Select(Path.GetFileName).Cast<object>().ToArray());
                    secondList.Items.AddRange(Pool(template.SecondaryVideos, AssemblyFiles.VideoExtensions).Select(Path.GetFileName).Cast<object>().ToArray());
                    int shared = template.SplitVideoPaths().Count;
                    bool splitOn = template.SplitLayout != "none";
                    if (template.SplitSharedPool && splitOn) {
                        primaryCount.Text = "Общая пачка · " + shared + " файлов";
                        secondaryCount.Text = shared < 2 ? "Нужно минимум 2 видео для смены половин в связках" : "В связках видео могут оказаться сверху или снизу";
                    } else {
                        primaryCount.Text = "Пачка 1 · верх / слева · " + primary + " файлов";
                        secondaryCount.Text = !splitOn ? "Пачка 2 · выберите деление ниже" : "Пачка 2 · низ / справа · " + secondary + " файлов";
                    }
                    secondaryCount.ForeColor = splitOn && (template.SplitSharedPool ? shared < 2 : secondary == 0) ? Theme.Error : Theme.TextSecondary;
                }
                RefreshCounts();
                body.Controls.Add(primaryCount);
                var files = Button("📁  Фоновое видео · открыть папку", () => {
                    if (scope.SelectedIndex == 2 && SelectedScene != null) OpenPool(3); else OpenPool(0);
                    RefreshCounts();
                }); files.Width = 560; body.Controls.Add(files);
                AddVoicePitchControls(body);
                AddNativeAudioControls(body, true);
                body.Controls.Add(firstList);
                AddVideoPlacementControls(body);
                body = MediaSection(page, "Два экрана", "Расположение, источники и звук каждой половины.");
                var picker = new AssemblySplitLayoutPicker { Width = 560, SplitLayout = template.SplitLayout };
                body.Controls.Add(picker);
                body.Controls.Add(sharedPool);
                body.Controls.Add(fixHalves);
                body.Controls.Add(secondaryCount);
                var secondary = Button("📁  Второе видео · добавить файлы", () => {
                    var paths = PickFiles(0); if (paths == null) return;
                    string source = template.SecondaryVideos;
                    AssemblyWorkspace.Import(template, ref source, "Второе-видео-" + Guid.NewGuid().ToString("N").Substring(0, 6), paths, AssemblyFiles.VideoExtensions, false);
                    template.SecondaryVideos = source; Changed(true); Save(); RefreshCounts();
                }); secondary.Width = 560; body.Controls.Add(secondary);
                body.Controls.Add(secondList);
                body.Controls.Add(new Label { Text = "Звук двух экранов · только одно видео", Width = 560, Height = 24, Font = Theme.FontCardTitle });
                var splitAudio = new ComboBox { Name = "SplitAudioMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 560 };
                splitAudio.Items.AddRange(new object[] { "Только верх / лево", "Только низ / право", "По очереди: верх, низ — для новых связок" });
                splitAudio.SelectedIndex = template.EffectiveSplitAudioMode == "bottom" ? 1 : template.EffectiveSplitAudioMode == "alternate" ? 2 : 0;
                body.Controls.Add(splitAudio);
                var splitVolume = Value(0, 200, 0); splitVolume.Value = (decimal)(template.SplitAudioVolume * 100); splitVolume.Visible = false;
                body.Controls.Add(new AssemblySlider("Громкость выбранного видео", splitVolume, "%") { Width = 560 });
                splitAudio.SelectedIndexChanged += (s, e) => { template.SplitAudioMode = new[] { "top", "bottom", "alternate" }[splitAudio.SelectedIndex]; Changed(false); Save(); };
                splitVolume.ValueChanged += (s, e) => { template.SplitAudioVolume = (double)splitVolume.Value / 100; Changed(false); Save(); };
                body.Controls.Add(new Label { Text = "Звук зависит от положения видео, а не номера пачки. Смена звука не создаёт новую связку и не разрешает повтор пары.", Width = 560, Height = 42, ForeColor = Theme.TextSecondary });
                body.Controls.Add(Button("Пачка 2 · заменить файлы", () => {
                    var paths = PickFiles(0); if (paths == null) return;
                    string source = template.SecondaryVideos;
                    AssemblyWorkspace.Import(template, ref source, "Второе-видео-" + Guid.NewGuid().ToString("N").Substring(0, 6), paths, AssemblyFiles.VideoExtensions, true);
                    template.SecondaryVideos = source; Changed(true); Save(); RefreshCounts();
                }));
                body.Controls.Add(Button("Пачка 2 · очистить", () => { template.SecondaryVideos = ""; Changed(true); Save(); RefreshCounts(); }));
                body.Controls.Add(new Label { Text = "Одинаковый файл не попадёт в обе половины. Использованные связки не повторяются. Источник звука выбирается отдельно: верх, низ или по очереди.", Width = 560, Height = 42, ForeColor = Theme.TextSecondary });
                files.Text = "Фоновое видео · добавить / заменить файлы";
                body.Controls.Add(Button("Очистить всё фоновое видео",()=>{
                    AssemblyWorkspace.ClearBackgroundVideos(template);
                    picker.SplitLayout="none";Changed(true);Save();RefreshCounts();
                    status.Text="Все источники фонового видео отключены. Объекты сцен не изменены.";
                }));
                secondary.Text = "Пачка 2 · добавить файлы";
                sharedPool.Text = "Дополнительно: объединить обе пачки в общий набор";
                sharedPool.CheckedChanged += (s, e) => { template.SplitSharedPool = sharedPool.Checked; Changed(true); Save(); RefreshCounts(); };
                fixHalves.CheckedChanged += (s, e) => { template.SplitFixHalves = !fixHalves.Checked; Changed(true); Save(); RefreshCounts(); };
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
                MediaFooter(form, Button("Готово", () => { Save(); form.Close(); RefreshInfo(); RequestBackground(); }, true));
                form.ShowDialog(this);
            }
        }
        void ImportSpeechToSceneAudio(string[] paths) {
            if (SelectedScene == null) throw new Exception("Выберите сцену слева.");
            paths = PathUtil.NormalizeAll(paths);
            if (paths == null || paths.Length == 0) return;
            string source = SelectedScene.Voiceover;
            int count = AssemblyWorkspace.Import(template, ref source, Path.Combine(SelectedScene.Folder, "Озвучка"), paths, AssemblyFiles.MusicExtensions, false);
            if (count > 0) {
                SelectedScene.Voiceover = source;
                SelectedScene.VoiceMode = "own";
            }
            AssemblySpeechPlan.NormalizeSceneVoiceModes(template);
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
            audioWorkspace = Theme.MakeCard(); audioWorkspace.Dock = DockStyle.Fill; audioWorkspace.Padding = new Padding(24);
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            audioWorkspace.Controls.Add(body); contentHost.Controls.Add(audioWorkspace); audioWorkspace.BringToFront();
            body.Controls.Add(new Label { Text = title, Font = Theme.FontCardTitle, AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
            body.Controls.Add(new Label { Text = hint, Height = 56, Width = 360, ForeColor = Theme.TextSecondary });
            void ResizeSettings() { SizeMediaFlow(body, Math.Max(200, body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8)); }
            body.SizeChanged += (s, e) => ResizeSettings();
            BeginInvoke((Action)ResizeSettings);
            return body;
        }
        void OpenVoiceWorkspace() {
            LeaveSubtitleWorkspaceIfOpen();
            // Capture the scene so that actions never change a different scene by accident.
            var scene = SelectedScene;
            using (var form = MediaDialog("Озвучка · " + AssemblyProfile.ShortLabel(studioProfile, template), 620)) {
                var page = MediaBody(form);
                var body = MediaSection(page, "Озвучка ролика", "Источник голоса, общая пачка и персональные настройки сцены.");
                Action refresh = () => { Changed(false); Save(); RequestTimingHints(); RefreshInfo(); };
                body.Controls.Add(new Label { Text = "Один вариант озвучки выбирается из пачки для каждого ролика. Длина видео = выбранное аудио. Музыка настраивается отдельно.", Width = 560, Height = 54, ForeColor = Theme.TextSecondary });
                AddVoicePitchControls(body);
                body = MediaSection(page, "Общая пачка", "Один случайный вариант для каждого нового ролика.");
                var sharedCount = AssemblySpeechPlan.SharedVoiceOptions(template).Count;
                bool hasSharedFiles = AssemblyFiles.Pool(template.Resolve(template.Voiceover), AssemblyFiles.MusicExtensions).Count > 0;
                body.Controls.Add(new Label { Text = "Доступно вариантов: " + sharedCount + (hasSharedFiles ? " · загруженные файлы" : " · ElevenLabs"), Width = 560, Height = 32, ForeColor = Theme.TextSecondary });
                body.Controls.Add(new Label { Text = "Новая пачка заменяет активную. Добавление сохраняет прежние варианты. Старые файлы при замене не удаляются.", Width = 560, Height = 42, ForeColor = Theme.TextSecondary });
                foreach (bool replace in new[] { true, false }) {
                    var import = Button(replace ? "Заменить общую пачку" : "Добавить файлы в общую пачку", () => Safe(() => {
                        var paths = PickFiles(1); if (paths == null || paths.Length == 0) return;
                        string source = template.Voiceover;
                        int count = AssemblyWorkspace.Import(template, ref source, Path.Combine("Озвучка", "Общая"), paths, AssemblyFiles.MusicExtensions, replace);
                        if (count > 0) { template.Voiceover = source; refresh(); form.Close(); BeginInvoke((Action)OpenVoiceWorkspace); }
                    }), replace);
                    import.Name = replace ? "SharedVoiceReplace" : "SharedVoiceAdd";
                    body.Controls.Add(import);
                }
                body.Controls.Add(Button("Использовать варианты ElevenLabs", () => {
                    template.Voiceover = ""; refresh(); form.Close(); BeginInvoke((Action)OpenVoiceWorkspace);
                }));
                body.Controls.Add(Button("Тексты и генерация ElevenLabs →", () => { form.Close(); BeginInvoke((Action)OpenVoiceEditor); }));
                if (scene != null) {
                    body = MediaSection(page, SceneLabel(scene,sceneIndex) + " · источник голоса", "Выберите источник. Собственные файлы сцены сохраняются при переключении.");
                    string mode = scene.VoiceMode == "own" ? "Своя пачка" : scene.VoiceMode == "shared" ? "Общая пачка" : scene.VoiceMode == "none" || template.HasSceneVoiceovers ? "Без озвучки" : "Общая озвучка на весь ролик";
                    int ownCount = AssemblySpeechPlan.SceneOwnAudioCount(template, scene);
                    int activeCount = template.HasSceneVoiceovers ? AssemblySpeechPlan.SceneVoiceOptions(template, scene).Count : sharedCount;
                    body.Controls.Add(new Label { Text = "Активно: " + mode + " · вариантов: " + activeCount + "\nСохранено в своей пачке сцены: " + ownCount, Width = 560, Height = 48, ForeColor = Theme.TextSecondary });
                    var sources = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 12) };
                    body.Controls.Add(sources);
                    var sharedSource = Button("Общая пачка", () => Safe(() => {
                        if (AssemblySpeechPlan.SharedVoiceOptions(template).Count == 0) throw new Exception("Добавьте общие файлы или сначала создайте озвучки в ElevenLabs.");
                        scene.VoiceMode = "shared"; refresh(); form.Close(); BeginInvoke((Action)OpenVoiceWorkspace);
                    }));
                    Theme.ApplyHeroToggleStyle(sharedSource, scene.VoiceMode == "shared"); sources.Controls.Add(sharedSource);
                    if (ownCount > 0) {
                        var ownSource = Button("Своя пачка", () => { scene.VoiceMode = "own"; refresh(); form.Close(); BeginInvoke((Action)OpenVoiceWorkspace); });
                        Theme.ApplyHeroToggleStyle(ownSource, scene.VoiceMode == "own"); sources.Controls.Add(ownSource);
                    }
                    var silentSource = Button("Без озвучки", () => { scene.VoiceMode = "none"; refresh(); form.Close(); BeginInvoke((Action)OpenVoiceWorkspace); });
                    Theme.ApplyHeroToggleStyle(silentSource, scene.VoiceMode == "none" || (template.HasSceneVoiceovers && scene.VoiceMode != "own" && scene.VoiceMode != "shared")); sources.Controls.Add(silentSource);
                    foreach (bool replace in new[] { true, false }) {
                        var import = Button(replace ? "Заменить свою пачку сцены" : "Добавить файлы в свою пачку", () => Safe(() => {
                            var paths = PickFiles(1); if (paths == null || paths.Length == 0) return;
                            string source = scene.Voiceover;
                            int count = AssemblyWorkspace.Import(template, ref source, Path.Combine(scene.Folder, "Озвучка"), paths, AssemblyFiles.MusicExtensions, replace);
                            if (count > 0) { scene.Voiceover = source; scene.VoiceMode = "own"; refresh(); form.Close(); BeginInvoke((Action)OpenVoiceWorkspace); }
                        }), replace);
                        import.Name = replace ? "SceneVoiceReplace" : "SceneVoiceAdd";
                        body.Controls.Add(import);
                    }
                }
                body = MediaSection(page, "Совместимость", "Для проектов с единой дорожкой озвучки на весь ролик.");
                body.Controls.Add(Button("Вернуть озвучку на весь ролик", () => {
                    foreach (var item in template.Scenes) item.VoiceMode = "";
                    refresh(); form.Close();
                }));
                MediaFooter(form, Button("Готово", () => { Save(); form.Close(); }, true));
                form.ShowDialog(this);
            }
        }
        void AddVoicePitchControls(FlowLayoutPanel body) {
            var pitch = new NumericUpDown { Name = "VoicePitchValue", Minimum = -12, Maximum = 12, DecimalPlaces = 1, Increment = .5m,
                Value = (decimal)template.VoicePitchSemitones, Visible = false };
            body.Controls.Add(pitch);
            body.Controls.Add(new AssemblySlider("Тон голоса · ниже / выше", pitch, "пт") { Name = "VoicePitchSlider", Width = 560 });
            body.Controls.Add(new Label { Text = "−12…+12 полутонов · 0 — обычный голос. Без изменения скорости. Озвучка, а без неё — звук видео. Музыка и фон не меняются.", Width = 560, Height = 46, ForeColor = Theme.TextSecondary });
            pitch.ValueChanged += (s, e) => { template.VoicePitchSemitones = (double)pitch.Value; Changed(false); Save(); };
        }
        void AddNativeAudioControls(FlowLayoutPanel body, bool primary) {
            var row = new FlowLayoutPanel { Width = 560, Height = 32, WrapContents = false };
            var on = new CheckBox { Text = "Родной звук · пачка " + (primary ? "1" : "2"), AutoSize = true,
                Checked = primary ? template.PrimaryNativeAudio : template.SecondaryNativeAudio, ForeColor = Theme.TextPrimary };
            var level = new NumericUpDown { Minimum = 0, Maximum = 200, Width = 65, Value = (decimal)((primary ? template.PrimaryNativeVolume : template.SecondaryNativeVolume) * 100) };
            row.Controls.Add(on);
            if (!primary) { row.Controls.Add(level); row.Controls.Add(new Label { Text = "%", AutoSize = true }); }
            else { on.Text="Звук основных видео · без деления экрана"; level.Dispose(); }
            body.Controls.Add(row);
            on.CheckedChanged += (s, e) => { if (primary) template.PrimaryNativeAudio = on.Checked; else template.SecondaryNativeAudio = on.Checked; Changed(false); Save(); };
            if (!primary) level.ValueChanged += (s, e) => { template.SecondaryNativeVolume = (double)level.Value / 100; Changed(false); Save(); };
        }
        void AddSourcePitchControls(FlowLayoutPanel body) {
            body.Controls.Add(new Label {Text="Обработка голоса · без изменения скорости и длительности",AutoSize=true,Font=Theme.FontCardTitle});
            var sources=new ComboBox {Name="AudioProcessingSource",DropDownStyle=ComboBoxStyle.DropDownList,Width=560};
            var layers=new List<AssemblyLayer> {null};sources.Items.Add("Фоновое видео");
            for(int i=0;i<template.Scenes.Count;i++)for(int j=0;j<template.Scenes[i].Layers.Count;j++) {
                var layer=template.Scenes[i].Layers[j];
                if(layer.IsText || !Pool(layer.Source,AssemblyFiles.PictureExtensions).Any(AssemblyOverlayMedia.IsAnimated))continue;
                layers.Add(layer);sources.Items.Add(SceneLabel(template.Scenes[i],i)+" · Объект "+(j+1));
            }
            body.Controls.Add(sources);
            var sound=new CheckBox {Text="Включить звук выбранной пачки",AutoSize=true,ForeColor=Theme.TextPrimary};body.Controls.Add(sound);
            var on=new CheckBox {Text="Изменять высоту голоса · случайно в диапазоне",AutoSize=true,ForeColor=Theme.TextPrimary};body.Controls.Add(on);
            var row=new FlowLayoutPanel {AutoSize=true,Width=560};
            var min=new NumericUpDown {Name="AudioPitchMin",Minimum=-12,Maximum=12,DecimalPlaces=1,Increment=.5m,Width=100};
            var max=new NumericUpDown {Name="AudioPitchMax",Minimum=-12,Maximum=12,DecimalPlaces=1,Increment=.5m,Width=100};
            row.Controls.Add(new Label {Text="От",AutoSize=true});row.Controls.Add(min);row.Controls.Add(new Label {Text="до",AutoSize=true});row.Controls.Add(max);row.Controls.Add(new Label {Text="полутонов (− ниже, + выше)",AutoSize=true});body.Controls.Add(row);
            bool binding=false;
            Func<ProcessingParameter> selected=()=>layers[sources.SelectedIndex]?.AudioPitch??template.BackgroundAudioPitch;
            Action load=()=>{binding=true;var layer=layers[sources.SelectedIndex];var p=selected();sound.Visible=layer!=null;sound.Checked=layer?.AudioEnabled??true;on.Checked=p.Enabled;min.Value=(decimal)p.Values.Min;max.Value=(decimal)p.Values.Max;binding=false;};
            sources.SelectedIndexChanged+=(s,e)=>{load();template.AudioProcessingSourceId=layers[sources.SelectedIndex]?.ObjectId??"background";Save();};
            Action save=()=>{if(binding)return;var p=selected();p.Enabled=on.Checked;p.Values=new Range((double)min.Value,(double)max.Value);if(layers[sources.SelectedIndex]!=null)layers[sources.SelectedIndex].AudioEnabled=sound.Checked;Changed(false);Save();};
            on.CheckedChanged+=(s,e)=>{if(!binding && on.Checked && layers[sources.SelectedIndex]!=null)sound.Checked=true;save();};sound.CheckedChanged+=(s,e)=>save();
            min.ValueChanged+=(s,e)=>{if(binding)return;if(min.Value>max.Value)max.Value=min.Value;save();};
            max.ValueChanged+=(s,e)=>{if(binding)return;if(max.Value<min.Value)min.Value=max.Value;save();};
            int initial=template.AudioProcessingSourceId=="background"?0:layers.FindIndex(l=>l!=null && l.ObjectId==template.AudioProcessingSourceId && SelectedScene!=null && SelectedScene.Layers.Contains(l));
            if(initial<0 && SelectedLayer!=null)initial=layers.IndexOf(SelectedLayer);
            sources.SelectedIndex=Math.Max(0,initial);
        }
        void OpenAudioEditor() {
            LeaveSubtitleWorkspaceIfOpen();
            using (var form = MediaDialog("Настройки звука · " + AssemblyProfile.ShortLabel(studioProfile, template), 740)) {
                var page = MediaBody(form);
                var body = MediaSection(page, "Фоновая музыка", "Непрерывная музыкальная дорожка на весь ролик.");
                var rollMusic = Button("Пачка фоновой музыки · " + Pool(template.Music, AssemblyFiles.MusicExtensions).Count + " файлов", () => OpenPool(1)); rollMusic.Width = 560; body.Controls.Add(rollMusic);
                var mainScene = template.Scenes.FirstOrDefault(s => !s.IsPreview);
                var bedVolume = Value(0, 200, 0); bedVolume.Value = (decimal)((mainScene == null ? template.MusicVolume : template.SceneBedMusicVolume(mainScene)) * 100); bedVolume.Visible = false; body.Controls.Add(bedVolume);
                body.Controls.Add(new AssemblySlider("Громкость музыки", bedVolume, "%") { Width = 560, Name = "SceneBedMusicSlider" });
                var previewMusic = new CheckBox { Text = "Музыка в превью", Width = 560, Height = 30, Checked = template.Scenes.Any(s => s.IsPreview && template.SceneBedMusicVolume(s) > 0), ForeColor = Theme.TextPrimary };
                body.Controls.Add(previewMusic);
                Action applyBed = () => {
                    template.MusicVolume = (double)bedVolume.Value / 100;
                    foreach (var s in template.Scenes) s.BedMusicVolume = s.IsPreview ? (previewMusic.Checked ? template.MusicVolume : 0) : -1;
                    Changed(false); Save();
                };
                bedVolume.ValueChanged += (s, e) => applyBed();
                previewMusic.CheckedChanged += (s, e) => applyBed();
                AddAudioLevelingControls(body, form);
                body = MediaSection(page, SelectedScene == null ? "Звук видео" : SceneLabel(SelectedScene, sceneIndex) + " · звук видео", SelectedScene == null ? "Выберите сцену слева." : "Уровень и обработка исходного звука выбранной сцены.");
                if (SelectedScene != null) {
                    double initialNative = template.SceneNativeAudioVolume(SelectedScene);
                    var nativeOn = new CheckBox { Text = "Включить звук видео", Width = 560, Height = 30, Checked = initialNative > 0, ForeColor = Theme.TextPrimary };
                    var nativeLevel = Value(0, 200, 0); nativeLevel.Value = (decimal)(initialNative * 100); nativeLevel.Visible = false;
                    body.Controls.Add(nativeOn); body.Controls.Add(nativeLevel);
                    body.Controls.Add(new AssemblySlider("Громкость", nativeLevel, "%") { Width = 560, Name = "SceneNativeAudioSlider" });
                    nativeOn.CheckedChanged += (s, e) => {
                        if (!nativeOn.Checked) SelectedScene.NativeAudioVolume = 0;
                        else if (SelectedScene.NativeAudioVolume <= 0) SelectedScene.NativeAudioVolume = nativeLevel.Value > 0 ? (double)nativeLevel.Value / 100 : 1;
                        nativeLevel.Value = (decimal)(SelectedScene.NativeAudioVolume * 100);
                        Changed(false); Save();
                    };
                    nativeLevel.ValueChanged += (s, e) => {
                        SelectedScene.NativeAudioVolume = (double)nativeLevel.Value / 100;
                        nativeOn.Checked = SelectedScene.NativeAudioVolume > 0;
                        Changed(false); Save();
                    };
                    AddSourcePitchControls(body);
                    body = MediaSection(page, SceneLabel(SelectedScene, sceneIndex) + " · дополнительное аудио", "Отдельные аудиофайлы для выбранной сцены.");
                    var localVolume = Value(0, 200, 0); localVolume.Value = (decimal)(SelectedScene.MusicVolume * 100); localVolume.Visible = false;
                    var localSlider = new AssemblySlider("Громкость", localVolume, "%") { Width = 560 };
                    Button removeMusic = null;
                    Action showLocal = () => { bool has = !string.IsNullOrWhiteSpace(SelectedScene.Music); localSlider.Visible = has; removeMusic.Visible = has; };
                    var sceneMusic = Button("Добавить аудио сцены", () => {
                        var paths = PickFiles(1); if (paths == null) return;
                        string source = SelectedScene.Music;
                        AssemblyWorkspace.Import(template, ref source, Path.Combine(SelectedScene.Folder, "Аудио"), paths, AssemblyFiles.MusicExtensions, false);
                        SelectedScene.Music = source; Changed(false); Save(); showLocal();
                    }); sceneMusic.Width = 560;
                    removeMusic = Button("Убрать аудио сцены", () => { SelectedScene.Music = ""; Changed(false); Save(); showLocal(); });
                    localVolume.ValueChanged += (s, e) => { SelectedScene.MusicVolume = (double)localVolume.Value / 100; Changed(false); };
                    body.Controls.Add(sceneMusic); body.Controls.Add(localVolume); body.Controls.Add(localSlider); body.Controls.Add(removeMusic);
                    showLocal();
                }
                MediaFooter(form, Button("Готово", () => { Save(); RefreshInfo(); form.Close(); }, true));
                form.ShowDialog(this);
            }
        }
    }
}
