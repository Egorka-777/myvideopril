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
            AssemblyWorkspace.EnsureSplitSharedPool(template);
            using (var form = MediaDialog("Фоновое видео", 620)) {
                var page = MediaBody(form);
                var body = MediaSection(page, "Раскладка", null);
                var picker = new AssemblySplitLayoutPicker { Width = 560, Height = 168, SplitLayout = template.SplitLayout, Margin = new Padding(0, 4, 0, 0) };
                body.Controls.Add(picker);
                body.Controls.Add(new Label {
                    Text = "Нажмите половинку: добавить или заменить. В превью — тянуть и колёсико.",
                    Width = 560, Height = 28, ForeColor = Theme.TextSecondary, Margin = new Padding(0, 6, 0, 8)
                });
                var poolSummary = new Label { AutoSize = false, Width = 560, Height = 22, ForeColor = Theme.TextSecondary };
                var fileList = new ListBox { Width = 560, Height = 72, BackColor = Theme.Background, ForeColor = Theme.TextPrimary, HorizontalScrollbar = true };
                string HalfTitle(int half) {
                    if (template.SplitLayout == "none") return "Основное видео";
                    if (template.SplitLayout == "vertical") return half == 0 ? "Верх" : "Низ";
                    return half == 0 ? "Левая половина" : "Правая половина";
                }
                void ImportHalf(int half, bool replace) {
                    var paths = PickFiles(0, form);
                    if (paths == null) return;
                    template.SplitSharedPool = false;
                    if (half == 0) ImportPool(0, paths, replace);
                    else {
                        string source = template.SecondaryVideos;
                        AssemblyWorkspace.Import(template, ref source, "Второе-видео-" + Guid.NewGuid().ToString("N").Substring(0, 6), paths, AssemblyFiles.VideoExtensions, replace || Pool(template.SecondaryVideos, AssemblyFiles.VideoExtensions).Count == 0);
                        template.SecondaryVideos = source;
                        Changed(true); Save();
                    }
                    RefreshCounts(); RequestCapacity(); RequestBackground();
                }
                void RefreshCounts() {
                    bool splitOn = template.SplitLayout != "none";
                    int primary = Pool(template.Videos, AssemblyFiles.VideoExtensions).Count;
                    int secondary = Pool(template.SecondaryVideos, AssemblyFiles.VideoExtensions).Count;
                    fileList.Items.Clear();
                    if (!splitOn) {
                        poolSummary.Text = "Файлов: " + primary;
                        fileList.Items.AddRange(Pool(template.Videos, AssemblyFiles.VideoExtensions).Select(Path.GetFileName).Cast<object>().ToArray());
                    } else {
                        poolSummary.Text = "Верх: " + primary + " · низ: " + secondary + (secondary == 0 ? " · нажмите нижнюю половинку" : "");
                        fileList.Items.AddRange(Pool(template.Videos, AssemblyFiles.VideoExtensions).Select(f => "▲ " + Path.GetFileName(f)).Cast<object>().ToArray());
                        fileList.Items.AddRange(Pool(template.SecondaryVideos, AssemblyFiles.VideoExtensions).Select(f => "▼ " + Path.GetFileName(f)).Cast<object>().ToArray());
                    }
                    poolSummary.ForeColor = splitOn && secondary == 0 ? Theme.Error : Theme.TextSecondary;
                }
                body.Controls.Add(poolSummary);
                body.Controls.Add(fileList);
                var fileActions = new FlowLayoutPanel { Width = 560, AutoSize = true, WrapContents = false };
                fileActions.Controls.Add(Button("Очистить все видео", () => {
                    AssemblyWorkspace.ClearBackgroundVideos(template);
                    picker.SplitLayout = "none";
                    Changed(true); Save(); RefreshCounts(); RequestCapacity();
                    status.Text = "Фоновое видео отключено.";
                }));
                body.Controls.Add(fileActions);
                var softness = Value(0, 50, 0); softness.Value = (decimal)(template.SplitSoftness * 100); softness.Visible = false;
                body.Controls.Add(new AssemblySlider("Смягчение стыка", softness, "%") { Width = 560, Margin = new Padding(0, 8, 0, 0) });
                RefreshCounts();
                picker.LayoutSelected += layout => {
                    template.SplitLayout = layout;
                    if (layout != "none") template.SplitSharedPool = false;
                    Changed(true); Save(); RefreshCounts(); RequestCapacity(); RequestBackground();
                };
                picker.HalfClicked += half => ShowChoices(HalfTitle(half), picker, new[] {
                    Tuple.Create("+ Добавить файлы", (Action)(() => ImportHalf(half, false)), false),
                    Tuple.Create("Заменить пачку", (Action)(() => ImportHalf(half, true)), false),
                });
                softness.ValueChanged += (s, e) => { template.SplitSoftness = (double)softness.Value / 100; Changed(true); };
                MediaFooter(form, Button("Готово", () => { Save(); form.Close(); RefreshInfo(); RequestBackground(); RequestCapacity(); }, true));
                form.ShowDialog(this);
            }
        }
        void AddSplitScreenAudioControls(FlowLayoutPanel body) {
            if (template.SplitLayout == "none") return;
            body.Controls.Add(new Label { Text = "Звук двух экранов", Width = 560, Height = 24, Font = Theme.FontCardTitle, Margin = new Padding(0, 0, 0, 4) });
            var splitAudio = new ComboBox { Name = "SplitAudioMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 560 };
            splitAudio.Items.AddRange(new object[] { "Только верх", "Только низ", "По очереди (верх и низ в разных роликах)" });
            splitAudio.SelectedIndex = template.EffectiveSplitAudioMode == "bottom" ? 1 : template.EffectiveSplitAudioMode == "alternate" ? 2 : 0;
            body.Controls.Add(splitAudio);
            var splitVolume = Value(0, 200, 0); splitVolume.Value = (decimal)(template.SplitAudioVolume * 100); splitVolume.Visible = false;
            body.Controls.Add(new AssemblySlider("Громкость звука видео", splitVolume, "%") { Width = 560 });
            splitAudio.SelectedIndexChanged += (s, e) => { template.SplitAudioMode = new[] { "top", "bottom", "alternate" }[splitAudio.SelectedIndex]; Changed(false); Save(); };
            splitVolume.ValueChanged += (s, e) => { template.SplitAudioVolume = (double)splitVolume.Value / 100; Changed(false); Save(); };
            body.Controls.Add(new Label { Text = "Источник звука не влияет на число связок.", Width = 560, Height = 28, ForeColor = Theme.TextSecondary });
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
                if (template.SplitLayout != "none") {
                    var splitBody = MediaSection(page, "Два экрана", "Какой половине слышен родной звук видео.");
                    AddSplitScreenAudioControls(splitBody);
                }
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
