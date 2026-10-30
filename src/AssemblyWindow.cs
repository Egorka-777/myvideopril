using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow : Form {
        AssemblyTemplate template;
        readonly string templatePath, historyFolder, previewFolder;
        readonly bool throwErrors;
        int sceneIndex, layerIndex, previewVersion;
        bool loading, closeAfter;
        int batchMaximum;
        CancellationTokenSource cancellation, previewCancellation, capacityCancellation;
        string capacityKey; int capacityVersion;
        ContextMenuStrip activeSceneMenu;
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 400 };
        NumericUpDown duration, width, count, volume, voiceoverVolume, subtitlePosition;
        bool subtitleEditing;
        AssemblySlider subtitlePositionSlider;
        AssemblyCanvas canvas;
        AssemblyHorizontalStrip sceneStack;
        Panel header, workspace, contentHost, heroPreviewHost, subtitlePanel;
        TableLayoutPanel workspaceColumns, sceneEditor;
        bool subtitleWorkspaceActive;
        CheckBox subtitleEnabled, subtitleUppercase;
        ComboBox subtitleFont, subtitlePlate;
        NumericUpDown subtitleSize, subtitlePanelPosition, subtitleWords, subtitleOpacity;
        DataGridView subtitleCaptions;
        Label subtitlePanelMessage;
        bool subtitlePanelLoading;
        FlowLayoutPanel options;
        FlowLayoutPanel footer, packChips;
        Label status, selectedCaption, capacityLabel;
        Button videoButton, musicButton, globalImagesButton, voiceToolbarButton, subsToolbarButton, formatButton, titlesButton, profileRuBtn, profileEnBtn, profileRuInstBtn, profileEnInstBtn, profileTestBtn, profileRuTikTokBtn, profileEnTikTokBtn, stop, transitionButton, countButton, lessButton, moreButton, assembleButton;
        NumericUpDown splitPrimaryZoom, splitSecondaryZoom;
        double? timingAnchorDuration;
        List<WarmupCaption> previewSpeechCaptions;
        int timingVersion;
        CancellationTokenSource timingCancellation;
        TableLayoutPanel previewColumn;
        string studioProfile = AssemblyProfile.Ru;
        Preferences assemblyPrefs;
        ProgressBar progress;
        readonly List<Button> sceneCards = new List<Button>();
        readonly Dictionary<AssemblyLayer, string> previewImages = new Dictionary<AssemblyLayer, string>();
        public bool Embedded { get; private set; }
        public AssemblyWindow(bool throwErrors = false, bool embedded = false) {
            this.throwErrors = throwErrors;
            Embedded = embedded;
            templatePath = Path.Combine(Store.Root, "assembly-template.xml"); historyFolder = Path.Combine(Store.Root, "assembly-history");
            previewFolder = Path.Combine(Path.GetTempPath(), "videobatch-preview-" + Guid.NewGuid().ToString("N"));
            string note = null;
            try { template = File.Exists(templatePath) ? AssemblyFiles.Load<AssemblyTemplate>(templatePath) : AssemblyTemplate.Defaults(); template.Validate(); }
            catch (Exception e) { if (File.Exists(templatePath)) File.Copy(templatePath, templatePath + ".unreadable-" + Guid.NewGuid().ToString("N").Substring(0,6) + ".xml"); template = AssemblyTemplate.Defaults(); note = "Старый шаблон сохранён отдельно: " + e.Message; }
            if (string.IsNullOrWhiteSpace(template.Materials)) template.Materials = Path.Combine(AppPaths.ExeDirectory, "Материалы-сборки");
            if (string.IsNullOrWhiteSpace(template.Output)) template.Output = VideoBatchOutputPaths.AssemblyFolder(AssemblyProfile.Ru);
            if (template.PackStudioVersion == 0 && File.Exists(templatePath)) {
                File.Copy(templatePath, templatePath + ".before-packs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml", true);
                note = "Пачки и позиции сохранены. Пустые папки больше не мешают сборке.";
            }
            if (template.ObjectStudioVersion==0 && File.Exists(templatePath)) { File.Copy(templatePath,templatePath+".before-objects-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".xml",true); note="Объекты сохранены. Пачки с несколькими картинками теперь меняются; проверь лишние объекты."; }
            AssemblyWorkspace.Prepare(template);
            assemblyPrefs = Store.Load();
            studioProfile = AssemblyProfile.Normalize(string.IsNullOrWhiteSpace(assemblyPrefs.AssemblyStudioProfile) ? assemblyPrefs.AssemblyMarketView : assemblyPrefs.AssemblyStudioProfile);
            if (studioProfile == AssemblyProfile.RuTikTok || studioProfile == AssemblyProfile.EnTikTok) studioProfile = AssemblyProfile.MainProfile(studioProfile);
            AssemblyMarketProfiles.EnsureSeeded(template);
            AssemblyMarketProfiles.EnsureWorkspaceMigrated(template, templatePath);
            AssemblyMarketProfiles.Apply(template, studioProfile); AssemblyWorkspace.Prepare(template);
            Text = "Сборка роликов — моя студия"; Font = Theme.FontBody; BackColor = Theme.Background; ForeColor = Theme.TextPrimary;
            if (embedded) {
                TopLevel = false; FormBorderStyle = FormBorderStyle.None; Dock = DockStyle.Fill; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            } else {
                ClientSize = new Size(1180, 790); MinimumSize = new Size(980, 660);
                StartPosition = FormStartPosition.CenterParent;
                var area = Screen.FromControl(this).WorkingArea;
                ClientSize = new Size(Math.Min(ClientSize.Width, area.Width - 24), Math.Min(ClientSize.Height, area.Height - 60));
            }
            var bindings = new Panel { Visible = false }; Controls.Add(bindings);
            duration = Value(.5m, 60, 1); width = Value(2, 100, 1); count = Value(1, int.MaxValue, 0); volume = Value(0, 200, 0);
            voiceoverVolume = Value(0, 200, 0);
            subtitlePosition = Value(10, 90, 0);
            bindings.Controls.AddRange(new Control[] { duration, width, count, volume, voiceoverVolume, subtitlePosition }); count.Value = template.Count; volume.Value = (decimal)(template.MusicVolume * 100); voiceoverVolume.Value = (decimal)(template.VoiceoverVolume * 100);
            if (template.SubtitleStyle != null) subtitlePosition.Value = template.SubtitleStyle.Position;
            duration.ValueChanged += (s,e) => ChangeDuration(); width.ValueChanged += (s,e) => ChangeSize();
            count.ValueChanged += (s,e) => { if (!loading) { template.Count = (int)count.Value; Changed(false); } };
            volume.ValueChanged += (s,e) => { if (!loading) { template.MusicVolume = (double)volume.Value / 100; Changed(false); } };
            voiceoverVolume.ValueChanged += (s,e) => { if (!loading) { template.VoiceoverVolume = (double)voiceoverVolume.Value / 100; Changed(false); } };
            subtitlePosition.ValueChanged += (s,e) => { if (!loading) { if (template.SubtitleStyle == null) template.SubtitleStyle = new WarmupSubtitleStyle(); template.SubtitleStyle.Position = (int)subtitlePosition.Value; Changed(false); RefreshCanvas(); } };
            BuildStudio(); SelectScene(0); Save();
            if (note != null) status.Text = note;
            saveTimer.Tick += (s,e) => { saveTimer.Stop(); Safe(Save); };
            Shown += (s,e) => { RequestBackground(); RequestCapacity(); RequestTimingHints(); WarmMaterialsSilent(); };
            Activated += (s,e) => { AssemblyWorkspace.Prepare(template); RefreshInfo(); RefreshCanvas(); RequestCapacity(); RequestTimingHints(); };
            VisibleChanged += (s,e) => { if (!Visible && !IsDisposed && !loading) Safe(() => { saveTimer.Stop(); Save(); }); };
            FormClosing += (s,e) => {
                if (Embedded) {
                    if (e.CloseReason == CloseReason.UserClosing && cancellation != null) { e.Cancel = true; closeAfter = true; cancellation.Cancel(); }
                    else if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
                    return;
                }
                if (cancellation != null) { e.Cancel = true; closeAfter = true; cancellation.Cancel(); } else { saveTimer.Stop(); Safe(Save); previewCancellation?.Cancel(); capacityCancellation?.Cancel(); }
            };
        }
        static NumericUpDown Value(decimal min, decimal max, int decimals) { return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .1m }; }
        static Button Button(string text, Action action = null, bool accent = false) { var b = new AssemblyStudioButton(text, action, accent); if (text.Length == 1) { b.AutoSize = false; b.MinimumSize = new Size(36,34); b.Size = new Size(36,34); b.Padding = Padding.Empty; } return b; }
        static Label Label(string text, int height = 24) { return new Label { Text = text, Height = height, Dock = DockStyle.Fill, ForeColor = Theme.TextSecondary, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }; }
        static FlowLayoutPanel Row() { return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty }; }
        bool globalEditing;
        readonly List<AssemblyLayer> previewLayerOrder = new List<AssemblyLayer>();
        List<AssemblyLayer> EditableLayers => globalEditing ? template.GlobalLayers : SelectedScene?.Layers;
        AssemblyScene SelectedScene { get { return sceneIndex >= 0 && sceneIndex < template.Scenes.Count ? template.Scenes[sceneIndex] : null; } }
        AssemblyLayer SelectedLayer { get { return EditableLayers != null && layerIndex >= 0 && layerIndex < EditableLayers.Count ? EditableLayers[layerIndex] : null; } }
        void Later(Action action) { if (IsDisposed || Disposing) return; if (IsHandleCreated) BeginInvoke((Action)(() => {if (!IsDisposed && !Disposing) Safe(action);})); else Safe(action); }
        void DisposeMenuLater(ContextMenuStrip menu) { Later(() => {if (!menu.IsDisposed) menu.Dispose();}); }
        ToolStripItem MenuAction(ContextMenuStrip menu,string caption,Action action) { return menu.Items.Add(caption,null,(sender,args) => {menu.Close(); Later(action);}); }
        void BuildStudio() {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18,12,18,12), RowCount = 3, ColumnCount = 1, BackColor = Theme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104)); Controls.Add(root); root.BringToFront();
            header = new Panel { Dock = DockStyle.Fill }; root.Controls.Add(header,0,0);
            var headerGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); headerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); headerGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var titleStack = new FlowLayoutPanel { AutoSize = true, AutoSizeMode=AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 8, 0) };
            titleStack.Controls.Add(new Label { Text = "Моя студия", Font = Theme.FontPageTitle, AutoSize = true, ForeColor = Theme.TextPrimary, Margin = new Padding(0, 0, 0, 2) });
            titleStack.Controls.Add(new Label { Text = "Настрой сцены один раз — собирай новые ролики из своих материалов.", AutoSize = true, ForeColor = Theme.TextSecondary, MaximumSize = new Size(640, 0) });
            headerGrid.Controls.Add(titleStack, 0, 0);
            var settings = Button("⋯", SettingsMenu); settings.Name = "AssemblySettings"; settings.AutoSize = false; settings.Size = new Size(46, 36); settings.MinimumSize = settings.Size;
            headerGrid.Controls.Add(settings, 1, 0);
            profileRuBtn = ProfileToggle("RU YT", AssemblyProfile.Ru);
            profileEnBtn = ProfileToggle("EN YT", AssemblyProfile.En);
            profileRuInstBtn = ProfileToggle("RU INS", AssemblyProfile.RuInst);
            profileEnInstBtn = ProfileToggle("EN INS", AssemblyProfile.EnInst);
            profileTestBtn = ProfileToggle("ТЕСТ", AssemblyProfile.Test);
            profileRuTikTokBtn = ProfileToggle("RU TikTok", AssemblyProfile.RuTikTok);
            profileEnTikTokBtn = ProfileToggle("EN TikTok", AssemblyProfile.EnTikTok);
            profileRuBtn.Name = "AssemblyProfileRu"; profileEnBtn.Name = "AssemblyProfileEn";
            profileRuInstBtn.Name = "AssemblyProfileRuInst"; profileEnInstBtn.Name = "AssemblyProfileEnInst";
            profileTestBtn.Name = "AssemblyProfileTest";
            profileRuTikTokBtn.Name = "AssemblyProfileRuTikTok"; profileEnTikTokBtn.Name = "AssemblyProfileEnTikTok";
            var marketRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode=AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            marketRow.Controls.Add(Theme.MakeHeroSegmentGroup("", profileRuBtn, profileEnBtn, profileRuInstBtn, profileEnInstBtn, profileTestBtn));
            headerGrid.Controls.Add(marketRow, 0, 1); headerGrid.SetColumnSpan(marketRow, 2);
            header.Controls.Add(headerGrid);
            workspace = new Panel { Dock = DockStyle.Fill }; root.Controls.Add(workspace,0,1);
            workspaceColumns = new TableLayoutPanel { Name = "AssemblyWorkspaceColumns", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            workspaceColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            workspaceColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            workspace.Controls.Add(workspaceColumns);
            heroPreviewHost = new Panel { Name = "AssemblyPreviewColumn", Dock = DockStyle.Fill, BackColor = Theme.Card, Visible = true, Padding = new Padding(0, 0, 10, 0) };
            workspaceColumns.Controls.Add(heroPreviewHost, 0, 0);
            var settingsColumn = new TableLayoutPanel { Name = "AssemblySettingsColumn", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10, 0, 0, 0), Margin = Padding.Empty };
            settingsColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            settingsColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            settingsColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspaceColumns.Controls.Add(settingsColumn, 1, 0);
            var compactBar = new FlowLayoutPanel { Name = "AssemblyCompactBar", Dock = DockStyle.Fill, WrapContents = true, AutoSize = false, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 0) };
            voiceToolbarButton = CompactBarButton("AssemblyVoice", OpenVoiceWorkspace);
            videoButton = CompactBarButton("VideoPool", OpenVideoEditor);
            musicButton = CompactBarButton("MusicPool", OpenAudioEditor);
            subsToolbarButton = CompactBarButton("AssemblySubtitles", OpenSubtitleEditor);
            compactBar.Controls.Add(voiceToolbarButton);
            compactBar.Controls.Add(videoButton);
            compactBar.Controls.Add(musicButton);
            compactBar.Controls.Add(subsToolbarButton);
            settingsColumn.Controls.Add(compactBar, 0, 0);
            var sceneHost = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            sceneHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            sceneHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sceneHost.Controls.Add(Label("СЦЕНЫ · выберите сцену"), 0, 0);
            sceneStack = new AssemblyHorizontalStrip { Name = "SceneTimeline", Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 16) };
            sceneHost.Controls.Add(sceneStack, 0, 1);
            settingsColumn.Controls.Add(sceneHost, 0, 1);
            contentHost = new Panel { Dock = DockStyle.Fill };
            settingsColumn.Controls.Add(contentHost, 0, 2);
            globalImagesButton = HeaderAsset("GlobalImages", () => { globalEditing = true; layerIndex = 0; RefreshChips(); LoadLayer(); RefreshCanvas(); if (SelectedLayer == null) AddObject(); OpenPool(2); });
            globalImagesButton.Visible = false;
            previewColumn = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
            previewColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            previewColumn.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            previewColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heroPreviewHost.Controls.Add(previewColumn);
            sceneEditor = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Card, Padding = new Padding(12), AutoScroll = true };
            sceneEditor.RowStyles.Add(new RowStyle(SizeType.Absolute,40)); sceneEditor.RowStyles.Add(new RowStyle(SizeType.AutoSize)); sceneEditor.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            contentHost.Controls.Add(sceneEditor);
            BuildSubtitleWorkspace();
            var sceneHead = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
            sceneHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); foreach (int w in new[] {44,44,44}) sceneHead.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,w));
            selectedCaption = Label(""); selectedCaption.Font = Theme.FontCardTitle; selectedCaption.ForeColor = Theme.TextPrimary; sceneHead.Controls.Add(selectedCaption,0,0);
            sceneHead.Controls.Add(Button("←", () => MoveScene(-1)),1,0); sceneHead.Controls.Add(Button("→", () => MoveScene(1)),2,0);
            sceneHead.Controls.Add(Button("⋯", SceneMenu),3,0); sceneEditor.Controls.Add(sceneHead,0,0);
            var objects=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=1,ColumnCount=1,Margin=Padding.Empty,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink}; objects.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            packChips=new FlowLayoutPanel { Name="Objects", Dock=DockStyle.Fill, WrapContents=true, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, FlowDirection=FlowDirection.LeftToRight, Margin=Padding.Empty, Padding=new Padding(0,2,0,6) };
            objects.Controls.Add(packChips,0,0); sceneEditor.Controls.Add(objects,0,1);
            canvas = new AssemblyCanvas { Dock = DockStyle.Fill };
            canvas.LayerSelected += i => { if (i < 0 || i >= previewLayerOrder.Count) return; var layer = previewLayerOrder[i]; globalEditing = template.GlobalLayers.Contains(layer); layerIndex = EditableLayers.IndexOf(layer); LoadLayer(); RefreshChips(); };
            canvas.PlacementChanged += () => { if (SelectedLayer != null) SelectedLayer.PlacementConfigured = true; LoadLayer(); Changed(false); };
            canvas.SplitRatioChanged += () => Safe(() => { Changed(true); Save(); RequestBackground(); });
            canvas.SplitZoomChanged += () => Safe(() => { if (splitPrimaryZoom != null && !loading) { splitPrimaryZoom.Value = (decimal)(template.SplitPrimaryZoom * 100); splitSecondaryZoom.Value = (decimal)(template.SplitSecondaryZoom * 100); } Changed(true); Save(); RequestBackground(); });
            canvas.SubtitlePositionChanged += pos => Safe(() => {
                if (template.SubtitleStyle == null) template.SubtitleStyle = new WarmupSubtitleStyle { Enabled = true };
                template.SubtitleStyle.Enabled = true;
                template.SubtitleStyle.Position = pos;
                loading = true; subtitlePosition.Value = pos; loading = false;
                if (subtitlePanelPosition != null && !subtitlePanelLoading) { subtitlePanelLoading = true; subtitlePanelPosition.Value = pos; subtitlePanelLoading = false; }
                Changed(false); Save(); RefreshCanvas();
            });
            var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
            controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); controls.RowStyles.Add(new RowStyle(SizeType.Absolute,70)); controls.RowStyles.Add(new RowStyle(SizeType.Absolute,38)); controls.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            controls.Controls.Add(new AssemblySlider("Длительность",duration,"с") { Dock = DockStyle.Fill, Name = "SceneDurationSlider" },0,0);
            controls.Controls.Add(new AssemblySlider("Размер картинки",width,"%") { Dock = DockStyle.Fill, Name = "ImageSizeSlider" },1,0);
            var positions = Row(); positions.Controls.Add(Button("↑", () => NudgePlacement(-1))); positions.Controls.Add(Button("По центру", () => CenterPlacement())); positions.Controls.Add(Button("↓", () => NudgePlacement(1))); controls.Controls.Add(positions,0,1); controls.SetColumnSpan(positions,2);
            subtitlePositionSlider = new AssemblySlider("Положение субтитров", subtitlePosition, "%") { Dock = DockStyle.Fill, Name = "SubtitlePositionSlider", Visible = false };
            controls.Controls.Add(subtitlePositionSlider,0,2); controls.SetColumnSpan(subtitlePositionSlider,2);
            sceneEditor.Controls.Add(controls, 0, 2);
            var splitZoomRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Visible = false, Name = "SplitZoomRow", Padding = new Padding(0, 2, 0, 0) };
            splitPrimaryZoom = Value(85, 115, 0); splitSecondaryZoom = Value(85, 115, 0);
            splitPrimaryZoom.Visible = splitSecondaryZoom.Visible = false;
            splitZoomRow.Controls.Add(new AssemblySlider("Верх/лево · масштаб", splitPrimaryZoom, "%") { Width = 220, Margin = new Padding(0, 0, 8, 0) });
            splitZoomRow.Controls.Add(new AssemblySlider("Низ/право · масштаб", splitSecondaryZoom, "%") { Width = 220 });
            splitPrimaryZoom.ValueChanged += (s, e) => { if (loading) return; template.SplitPrimaryZoom = (double)splitPrimaryZoom.Value / 100; Changed(true); Save(); RequestBackground(); };
            splitSecondaryZoom.ValueChanged += (s, e) => { if (loading) return; template.SplitSecondaryZoom = (double)splitSecondaryZoom.Value / 100; Changed(true); Save(); RequestBackground(); };
            previewColumn.Controls.Add(Label("ПРЕВЬЮ · картинки, субтитры, граница двух видео"), 0, 0);
            previewColumn.Controls.Add(splitZoomRow, 0, 1);
            previewColumn.Controls.Add(canvas, 0, 2);
            var bottom=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=3,ColumnCount=1,Padding=new Padding(0,6,0,4)};
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,22)); bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,4)); bottom.RowStyles.Add(new RowStyle(SizeType.Absolute,52)); root.Controls.Add(bottom,0,2);
            status=Label(""); status.Name="AssemblyStatus"; var notices=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty}; notices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40)); notices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60)); notices.Controls.Add(status,0,0); capacityLabel=Label("Добавь видео для расчёта связок"); capacityLabel.Name="AssemblyCapacity"; capacityLabel.TextAlign=ContentAlignment.MiddleRight; notices.Controls.Add(capacityLabel,1,0); bottom.Controls.Add(notices,0,0);
            progress=new ProgressBar {Dock=DockStyle.Fill,Maximum=1000}; bottom.Controls.Add(progress,0,1);
            footer=Row(); footer.Padding=new Padding(0,2,0,0); footer.WrapContents=false; options=footer;
            formatButton=Button("",FormatMenu); formatButton.Name="Format"; FooterSize(formatButton,106);
            transitionButton=Button("",TransitionMenu); transitionButton.Name="Transitions"; FooterSize(transitionButton,168);
            titlesButton=Button("",OpenTitles); titlesButton.Name="AssemblyTitles"; FooterSize(titlesButton,128);
            lessButton=Button("−",() => ChangeCount(-1)); lessButton.Name="CountLess"; FooterSize(lessButton,34);
            countButton=Button("",CountMenu); countButton.Name="AssemblyCount"; FooterSize(countButton,112);
            moreButton=Button("+",() => ChangeCount(1)); moreButton.Name="CountMore"; FooterSize(moreButton,34);
            assembleButton=Button("✦ Собрать",async () => await Run(false),true); assembleButton.Name="Assemble"; FooterSize(assembleButton,140);
            var results=Button("Результаты ↗",() => Safe(() => {Directory.CreateDirectory(template.Output); Ui.Open(template.Output);})); results.Name="Results"; FooterSize(results,132);
            stop=Button("■ Стоп",() => cancellation?.Cancel()); stop.Visible=false; FooterSize(stop,82);
            footer.Controls.AddRange(new Control[] {formatButton,transitionButton,titlesButton,lessButton,countButton,moreButton,assembleButton,results,stop}); bottom.Controls.Add(footer,0,2); UpdateCountControls();
            SetDrop(videoButton, files => ImportPool(0,files)); SetDrop(musicButton, files => ImportPool(1,files)); SetDrop(globalImagesButton, files => ImportPool(2,files)); SetDrop(packChips, files => ImportPool(2,files)); BuildScenes();
        }
        Button HeaderAsset(string name, Action action) {
            var b = Button("", action); b.Name = name; b.AutoSize = false; b.Size = new Size(108, 48); b.MinimumSize = b.Size;
            b.TextAlign = ContentAlignment.TopLeft; b.Padding = new Padding(8, 5, 4, 4); b.Margin = new Padding(0, 8, 6, 0);
            return b;
        }
        Button CompactBarButton(string name, Action action) {
            var b = Button("", action); b.Name = name; b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(96, 32); b.MaximumSize = new Size(156, 36);
            b.Margin = new Padding(0, 0, 6, 4); b.TextAlign = ContentAlignment.MiddleLeft; b.Padding = new Padding(8, 4, 6, 4);
            return b;
        }
        Button StudioChip(string text, Action action = null, bool accent = false) {
            var b = Button(text, action, accent); b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.MinimumSize = new Size(88, 32); b.Margin = new Padding(0, 0, 6, 4); b.Padding = new Padding(8, 4, 8, 4);
            return b;
        }
        static void FooterSize(Button b,int w) { b.AutoSize=false; b.Size=new Size(w,46); b.MinimumSize=b.Size; b.Padding=new Padding(8,3,8,3); b.Margin=new Padding(0,0,6,0); }
        void SetDrop(Control c, Action<string[]> action) { c.AllowDrop = true; c.DragEnter += (s,e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; c.DragDrop += (s,e) => Safe(() => action(PathUtil.NormalizeAll((string[])e.Data.GetData(DataFormats.FileDrop)))); }
        void BuildScenes() {
            foreach (Control c in sceneStack.Controls.Cast<Control>().ToArray()) c.Dispose(); sceneStack.Controls.Clear(); sceneCards.Clear();
            for (int i = 0; i <= template.Scenes.Count; i++) {
                int at = i; var add = new AssemblyInsertButton(() => InsertScene(at)); add.Name = "InsertScene" + i; add.Size = new Size(28, 52); add.Margin = new Padding(0, 0, 4, 0); sceneStack.Controls.Add(add);
                if (i == template.Scenes.Count) break;
                int index = i; var card = new AssemblySceneButton(index+1,() => SelectScene(index)); card.Name = "SceneCard" + i; card.Dock = DockStyle.Fill;
                var row = new AssemblySceneRow { Width = 148, Height = 52, BackColor=Theme.Card, Margin = new Padding(0, 0, 4, 0) };
                var context = CreateSceneMenu(index); card.ContextMenuStrip = row.ContextMenuStrip = context; row.Disposed += (sender,args) => DisposeMenuLater(context);
                row.Controls.Add(card); sceneCards.Add(card); sceneStack.Controls.Add(row);
            }
            RefreshInfo();
        }
        void SelectScene(int index) { ShowSceneWorkspace(); globalEditing = false; sceneIndex = template.Scenes.Count==0 ? -1 : Math.Max(0,Math.Min(index,template.Scenes.Count - 1)); layerIndex = SelectedScene==null ? -1 : 0; loading = true; duration.Enabled=SelectedScene!=null; if (SelectedScene!=null) duration.Value = (decimal)SelectedScene.Duration; loading = false; canvas.SetBackground(null); LoadLayer(); RefreshChips(); RefreshCanvas(); RefreshInfo(); RequestBackground(); }
        void LoadLayer() {
            loading=true; width.Enabled=SelectedLayer!=null; if (SelectedLayer!=null) width.Value=(decimal)(SelectedLayer.Width*100); loading=false;
        }
        void SelectObject(int index) { layerIndex=index; LoadLayer(); RefreshChips(); RefreshCanvas(); }
        void OpenSceneImages() {
            if (SelectedScene == null) return;
            globalEditing = false;
            if (EditableLayers == null || EditableLayers.Count == 0) AddObject();
            else if (layerIndex < 0) layerIndex = 0;
            OpenPool(2);
        }
        void RefreshChips() {
            foreach (Control c in packChips.Controls.Cast<Control>().ToArray()) c.Dispose(); packChips.Controls.Clear();
            if (EditableLayers==null) return;
            if (SelectedScene != null && !template.ContinuousVideo && SelectedScene.VideoMode == "own") {
                int vn = Pool(SelectedScene.Videos, AssemblyFiles.VideoExtensions).Count;
                var sceneVideo = StudioChip("Видео сцены · " + vn, () => { SetSceneVideoMode("own"); OpenPool(3); }); sceneVideo.Name = "SceneVideoPool";
                packChips.Controls.Add(sceneVideo);
            }
            var scope = StudioChip(globalEditing ? "Общее изображение ▾" : "Эта сцена ▾", ImageScopeMenu); scope.Name = "ImageScope"; packChips.Controls.Add(scope);
            if (!globalEditing && SelectedScene != null && EditableLayers.Count == 0) {
                var empty = StudioChip("Изображения · добавить", OpenSceneImages); empty.Name = "SceneImages"; packChips.Controls.Add(empty);
            }
            for (int i = 0; i < EditableLayers.Count; i++) {
                int at=i; var l=EditableLayers[i]; int n=Pool(l.Source,AssemblyFiles.PictureExtensions).Count;
                var b=StudioChip("Объект "+(i+1)+" · "+(n==0 ? "+ файлы" : n+" шт."),() => { SelectObject(at); OpenPool(2); }, i==layerIndex); b.Name="Object"+i;
                var menu=Theme.MakeContextMenu(); menu.Opening+=(sender,args) => {layerIndex=at; LoadLayer(); RefreshCanvas();}; MenuAction(menu,"Загрузить изображения",() => ImportPool(2,PickFiles(2))); MenuAction(menu,"Удалить объект",() => RemoveObject(at)); b.ContextMenuStrip=menu; b.Disposed+=(sender,args) => DisposeMenuLater(menu); packChips.Controls.Add(b);
            }
            if (EditableLayers.Count>6) packChips.Controls.Add(StudioChip("⋯",ObjectList));
            var add=StudioChip("+ Объект",AddObject); add.Name="AddObject"; add.Enabled=EditableLayers.Count<8; packChips.Controls.Add(add);
        }
        void ObjectList() { var choices=EditableLayers.Select((l,i) => {int at=i; return Tuple.Create("Объект "+(i+1), (Action)(() => SelectObject(at)), i==layerIndex);}); ShowChoices("Объекты сцены",packChips,choices); }
        void AddObject() {
            Safe(() => {if (EditableLayers==null) return; if (EditableLayers.Count>=8) throw new Exception("До 8 слоёв в выбранной области."); var l=new AssemblyLayer {Name="Слой "+(EditableLayers.Count+1),Source=Path.Combine(globalEditing ? "Весь-ролик" : SelectedScene.Folder,"Объект-"+Guid.NewGuid().ToString("N").Substring(0,6)),Width=.7,Height=.18,X=.15,Y=Math.Min(.78,.12+EditableLayers.Count*.23)};
            Directory.CreateDirectory(template.Resolve(l.Source)); EditableLayers.Add(l); SelectObject(EditableLayers.Count-1); Changed(false); Save(); status.Text="Объект добавлен. Расставь его и загрузи варианты.";});
        }
        void RemoveObject(int index) {
            Safe(() => {EditableLayers.RemoveAt(index); previewImages.Clear(); layerIndex=EditableLayers.Count==0 ? -1 : Math.Min(index,EditableLayers.Count-1); LoadLayer(); RefreshChips(); Changed(false); Save(); status.Text="Объект убран. Его изображения остались в папке материалов.";});
        }
        List<string> Pool(string source, string[] extensions) { return AssemblyFiles.Pool(template.Resolve(source),extensions); }
        void RefreshInfo() {
            if (videoButton == null) return;
            int videos = Pool(template.Videos,AssemblyFiles.VideoExtensions).Count, songs = Pool(template.Music,AssemblyFiles.MusicExtensions).Count;
            if (videoButton != null)
                videoButton.Text = "Видео · " + videos + (template.ContinuousVideo ? " · сквозное" : " · по сценам");
            int speechFiles = AssemblySpeechPlan.SharedVoiceOptions(template).Count;
            int scenesWithVoice = template.Scenes.Count(scene => scene.VoiceMode == "shared" || scene.VoiceMode == "own");
            if (musicButton != null) musicButton.Text = "Музыка · " + songs;
            if (voiceToolbarButton != null) voiceToolbarButton.Text = "Озвучка · " + (template.HasSceneVoiceovers ? scenesWithVoice + " сцен" : speechFiles + " файлов");
            double rollDuration = template.Duration; // Effective length is selected per output at render time.
            for (int i = 0; i < sceneCards.Count; i++) {
                var scene = template.Scenes[i];
                int images = scene.Layers.Where(l => l.Enabled).Sum(l => Pool(l.Source,AssemblyFiles.PictureExtensions).Count);
                string detail = scene.Duration.ToString("0.##") + " с · " + (images == 0 ? "Добавь картинки" : images + " вариантов");
                if (template.HasSceneVoiceovers) {
                    int voices = AssemblySpeechPlan.SceneVoiceOptions(template,scene).Count;
                    detail = voices > 0 ? "Озвучка · " + voices + " вариантов · +1 с" : detail + " · без озвучки";
                }
                ((AssemblySceneButton)sceneCards[i]).SetInfo(SceneLabel(scene,i), detail, i == sceneIndex, false);
            }
            selectedCaption.ForeColor = Theme.TextPrimary;
            selectedCaption.Text = SelectedScene == null ? "Нет сцен · нажми + в ленте сцен" :
                SceneLabel(SelectedScene,sceneIndex) + (template.HasSceneVoiceovers
                    ? " · длительность по выбранной озвучке (+1 с)"
                    : " · " + SelectedScene.Duration.ToString("0.#") + " с · ручная длина " + rollDuration.ToString("0.#") + " с");
            if (globalImagesButton != null) globalImagesButton.Text = "Изображения\nвесь ролик";
            if (voiceToolbarButton != null) voiceToolbarButton.ForeColor = Theme.TextPrimary;
            if (subsToolbarButton != null) {
                subsToolbarButton.Text = "Субтитры";
                subsToolbarButton.BackColor = subtitleWorkspaceActive ? Theme.Accent : Theme.Card;
                subsToolbarButton.ForeColor = subtitleWorkspaceActive ? Color.White : Theme.TextPrimary;
            }
            if (template.SplitLayout != "none" && Pool(template.SecondaryVideos, AssemblyFiles.VideoExtensions).Count == 0)
                videoButton.ForeColor = Theme.Error;
            else if (videoButton != null) videoButton.ForeColor = Theme.TextPrimary;
            var splitZoomRow = previewColumn?.Controls.Find("SplitZoomRow", true).FirstOrDefault() as Control;
            bool splitOn = template.SplitLayout != "none";
            if (splitZoomRow != null) splitZoomRow.Visible = splitOn;
            if (splitOn && splitPrimaryZoom != null && splitSecondaryZoom != null) {
                loading = true;
                splitPrimaryZoom.Value = (decimal)(template.SplitPrimaryZoom * 100);
                splitSecondaryZoom.Value = (decimal)(template.SplitSecondaryZoom * 100);
                loading = false;
            }
            int gcd=Gcd(template.Width,template.Height); formatButton.Text="Формат\n"+template.Width/gcd+":"+template.Height/gcd+" ▾";
            countButton.Text="Роликов\n"+template.Count.ToString("N0"); transitionButton.Text="Переход\n"+TransitionLabel(template.Transition)+" ▾";
            if (titlesButton != null) titlesButton.Text = template.TitleBank.Count == 0 ? "Заголовки\n+ Загрузить ключи" : ("Заголовки\n" + template.TitleBank.Count + " фраз");
            UpdateCountControls();
            RefreshSubtitleControls();

        }
        void OpenTitles() {
            Safe(() => {
                using (var dialog = new AssemblyTitleDialog(template.TitleBank)) {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    template.TitleBank = dialog.Titles ?? new List<string>();
                    Changed(false); Save();
                    status.Text = template.TitleBank.Count == 0 ? "Заголовки отключены. Метаданные устройства и даты всё равно меняются для каждого ролика." : ("Сохранено заголовков: " + template.TitleBank.Count + ". К каждому ролику — свой заголовок и свои метаданные.");
                    RefreshInfo();
                }
            });
        }
        static int Gcd(int a,int b) { while (b != 0) { int next = a % b; a = b; b = next; } return a; }
        static string SceneLabel(AssemblyScene s,int i) { return s.Name.StartsWith("Сцена ") || s.Name.Contains("Hook") || s.Name.Contains("TG-бот") || s.Name.Contains("Торговля") || s.Name == "Новая сцена" ? "Сцена " + (i + 1) : s.Name; }
        void RefreshCanvas() {
            previewLayerOrder.Clear();
            if (SelectedScene==null) {canvas.ClearScene();return;}
            var sp = new AssemblyScenePlan { Scene = SelectedScene };
            foreach (var l in SelectedScene.Layers.Concat(template.GlobalLayers).Where(l => l.Enabled)) {
                var files = Pool(l.Source,AssemblyFiles.PictureExtensions); string chosen;
                if (files.Count == 0) { if (l==SelectedLayer) sp.Layers.Add(new AssemblyLayerPlan {Layer=l,Asset=new AssemblyAsset {Path=""}}); continue; }
                if (!previewImages.TryGetValue(l,out chosen) || !files.Contains(chosen)) chosen = files[0];
                if (l.Fixed && !string.IsNullOrWhiteSpace(l.FixedAssetHash)) chosen = files.FirstOrDefault(f => AssemblyFiles.Hash(f) == l.FixedAssetHash) ?? chosen;
                if (l.Fixed) previewImages[l]=chosen;
                sp.Layers.Add(new AssemblyLayerPlan { Layer = l, Asset = new AssemblyAsset { Path = chosen } });
            }
            previewLayerOrder.AddRange(sp.Layers.Select(l => l.Layer));
            canvas.SetScene(template,sp,SelectedLayer);
            ApplySubtitlePreview();
        }
        double PreviewGlobalTime() {
            if (sceneIndex < 0 || sceneIndex >= template.Scenes.Count) return 0;
            double t = 0;
            for (int i = 0; i < sceneIndex; i++) {
                t += template.Scenes[i].Duration;
                if (template.SceneTimingVersion > 0 && template.Transition != "cut") t += template.TransitionSeconds;
            }
            var scene = template.Scenes[sceneIndex];
            t += Math.Max(.05, Math.Min(scene.Duration * .4, Math.Max(0, scene.Duration - .05)));
            return t;
        }
        List<WarmupCaption> PreviewCaptionCues() {
            if (previewSpeechCaptions != null && previewSpeechCaptions.Count > 0) return previewSpeechCaptions;
            return (template.Captions ?? new List<AssemblyCaption>())
                .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                .Select(c => new WarmupCaption { Text = c.Text, Start = c.Start, End = c.End }).ToList();
        }
        string CaptionPreviewText() {
            if (!SubtitlesPreviewActive() && (template.SubtitleStyle == null || !template.SubtitleStyle.Enabled)) return null;
            double t = PreviewGlobalTime();
            var cues = PreviewCaptionCues();
            var active = cues.FirstOrDefault(c => t >= c.Start && t < c.End && !string.IsNullOrWhiteSpace(c.Text));
            if (active != null) return active.Text;
            if (subtitleCaptions?.CurrentRow != null && !subtitleCaptions.CurrentRow.IsNewRow) {
                string rowText = Convert.ToString(subtitleCaptions.CurrentRow.Cells["Text"].Value);
                if (!string.IsNullOrWhiteSpace(rowText)) return rowText;
            }
            string caption = cues.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Text))?.Text;
            if (!string.IsNullOrWhiteSpace(caption)) return caption;
            if (subtitleWorkspaceActive && template.SubtitleStyle?.Enabled == true) {
                string text = AssemblySpeechPlan.AvailableLines(template).FirstOrDefault()?.Text;
                if (!string.IsNullOrWhiteSpace(text)) return string.Join(" ", text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Take(template.SubtitleStyle.WordsPerCaption));
            }
            return null;
        }
        bool SubtitlesPreviewActive() {
            if (template.Captions != null && template.Captions.Any(c => !string.IsNullOrWhiteSpace(c.Text))) return true;
            if (previewSpeechCaptions != null && previewSpeechCaptions.Count > 0) return true;
            return template.SpeechLines != null && template.SpeechLines.Any(l => !string.IsNullOrWhiteSpace(l.AudioPath));
        }
        void ApplySubtitlePreview() {
            if (canvas == null) return;
            if (SelectedScene == null) { canvas.ClearSubtitles(); return; }
            if (template.SubtitleStyle?.Enabled != true) { canvas.ClearSubtitles(); canvas.SetSubtitleEditing(false); return; }
            var style = (template.SubtitleStyle ?? new WarmupSubtitleStyle()).Copy();
            style.Enabled = true;
            style.Normalize();
            canvas.SetSubtitleEditing(subtitleEditing);
            canvas.SetSubtitles(style, CaptionPreviewText());
        }
        void NudgePlacement(int direction) {
            if (subtitleEditing) { NudgeSubtitle(direction * 3); return; }
            if (direction < 0) Place(.08); else if (direction > 0) Place(-2);
        }
        void CenterPlacement() { if (subtitleEditing) { SetSubtitlePosition(50); return; } Place(-1); }
        void NudgeSubtitle(int delta) { SetSubtitlePosition((template.SubtitleStyle?.Position ?? 72) + delta); }
        void SetSubtitlePosition(int value) {
            if (template.SubtitleStyle == null) template.SubtitleStyle = new WarmupSubtitleStyle { Enabled = true };
            template.SubtitleStyle.Enabled = true;
            template.SubtitleStyle.Position = Math.Max(10, Math.Min(90, value));
            loading = true; subtitlePosition.Value = template.SubtitleStyle.Position; loading = false;
            Changed(false); RefreshCanvas();
        }
        void RefreshSubtitleControls() {
            bool show = SubtitlesPreviewActive() || (template.SubtitleStyle != null && template.SubtitleStyle.Enabled);
            if (subtitlePositionSlider != null) subtitlePositionSlider.Visible = show;
            if (show && template.SubtitleStyle != null) { loading = true; subtitlePosition.Value = template.SubtitleStyle.Position; loading = false; }
        }
        void ChangeDuration() {
            if (loading || SelectedScene==null) return; SelectedScene.Duration = (double)duration.Value;
            foreach (var l in SelectedScene.Layers) { l.Start = Math.Min(l.Start,Math.Max(0,SelectedScene.Duration - .1)); if (l.End > SelectedScene.Duration || l.End != 0 && l.End <= l.Start) l.End = 0; }
            if (template.TransitionDuration >= template.Scenes.Min(s => s.Duration) / 2) template.TransitionDuration = Math.Max(0,template.Scenes.Min(s => s.Duration) / 2 - .01);
            Changed(false);
        }
        void ChangeSize() {
            if (loading || SelectedLayer == null) return; var l = SelectedLayer; double next = (double)width.Value / 100;
            double maximum = Math.Min(1, l.Width / l.Height);
            next = Math.Min(next, maximum);
            l.PlacementConfigured = true; l.Height = l.Height * next / l.Width; l.Width = next; l.X = Math.Min(l.X,1 - l.Width); l.Y = Math.Min(l.Y,1 - l.Height); LoadLayer(); Changed(false);
        }
        void Place(double at) { if (SelectedLayer == null) return; var l = SelectedLayer; l.PlacementConfigured = true; l.X = (1 - l.Width)/2; l.Y = at == -1 ? (1 - l.Height)/2 : at == -2 ? Math.Max(0,.92 - l.Height) : Math.Min(at,1 - l.Height); Changed(false); }
        void Changed(bool background) { RefreshInfo(); RefreshCanvas(); saveTimer.Stop(); saveTimer.Start(); if (background) RequestBackground(); RequestTimingHints(); }
        Button ProfileToggle(string label, string profile) {
            var b = Theme.MakeHeroToggle(label, studioProfile == profile, () => SwitchProfile(profile));
            if (label.IndexOf("Inst", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("TikTok", StringComparison.OrdinalIgnoreCase) >= 0) {
                b.AutoSize = false;
                b.Size = new Size(label.IndexOf("TikTok", StringComparison.OrdinalIgnoreCase) >= 0 ? 98 : 92, Theme.HeroToggleHeight);
                b.MinimumSize = b.Size;
            }
            return b;
        }
        void Save() { AssemblyMarketProfiles.Capture(template, studioProfile); template.Validate(); AssemblyFiles.Save(templatePath, template); RequestCapacity(); }
        void SwitchProfile(string profile) {
            Safe(() => {
                LeaveSubtitleWorkspaceIfOpen();
                if (cancellation != null) return;
                profile = AssemblyProfile.Normalize(profile);
                if (profile == studioProfile) return;
                AssemblyMarketProfiles.Capture(template, studioProfile);
                studioProfile = profile;
                assemblyPrefs.AssemblyStudioProfile = profile;
                assemblyPrefs.AssemblyMarketView = AssemblyProfile.MainProfile(profile);
                Store.Save(assemblyPrefs);
                previewImages.Clear();
                AssemblyMarketProfiles.Apply(template, studioProfile); AssemblyWorkspace.Prepare(template);
                RefreshProfileToggles();
                BindTemplateValues(); BuildScenes(); SelectScene(sceneIndex < 0 ? 0 : sceneIndex); RefreshInfo(); Changed(true); Save();
                status.Text = ProfileStatusLine(studioProfile);
            });
        }
        static string ProfileStatusLine(string profile) {
            return "Сборка · " + AssemblyProfile.ShortLabel(profile) + ": свои материалы, связки, заголовки и папка результатов.";
        }
        void RefreshProfileToggles() {
            if (profileRuBtn == null) return;
            Theme.ApplyHeroToggleStyle(profileRuBtn, studioProfile == AssemblyProfile.Ru);
            Theme.ApplyHeroToggleStyle(profileEnBtn, studioProfile == AssemblyProfile.En);
            Theme.ApplyHeroToggleStyle(profileRuInstBtn, studioProfile == AssemblyProfile.RuInst);
            Theme.ApplyHeroToggleStyle(profileEnInstBtn, studioProfile == AssemblyProfile.EnInst);
            Theme.ApplyHeroToggleStyle(profileTestBtn, studioProfile == AssemblyProfile.Test);
            Theme.ApplyHeroToggleStyle(profileRuTikTokBtn, studioProfile == AssemblyProfile.RuTikTok);
            Theme.ApplyHeroToggleStyle(profileEnTikTokBtn, studioProfile == AssemblyProfile.EnTikTok);
        }
        void InsertScene(int index) { Safe(() => { AssemblyWorkspace.Insert(template,index); BuildScenes(); SelectScene(index); Save(); status.Text = "Сцена добавлена. Папка создана — нажми «+ Изображения» для объекта."; }); }
        void MoveScene(int delta) { Safe(() => { int next = sceneIndex + delta; if (next < 0 || next >= template.Scenes.Count) return; var scene = SelectedScene; template.Scenes.RemoveAt(sceneIndex); template.Scenes.Insert(next,scene); BuildScenes(); SelectScene(next); Save(); }); }
        void RemoveScene() { Safe(() => { if (SelectedScene==null) return; template.Scenes.RemoveAt(sceneIndex); sceneIndex = Math.Min(sceneIndex,template.Scenes.Count - 1); BuildScenes(); SelectScene(sceneIndex); Save(); status.Text = "Сцена убрана. Её файлы сохранены в папке материалов."; }); }
        ContextMenuStrip CreateSceneMenu(int index) {
            var target=template.Scenes[index]; var menu = Theme.MakeContextMenu(); menu.Name="SceneActions";
            menu.Opening += (sender,args) => {int at=template.Scenes.IndexOf(target); if (at>=0) SelectScene(at);else args.Cancel=true;};
            Func<Action,Action> onScene=action => () => {int at=template.Scenes.IndexOf(target);if (at>=0) {SelectScene(at);action();}};
            MenuAction(menu,"Видео этой сцены…",onScene(SceneVideoMenu));
            MenuAction(menu,"Озвучка этой сцены…",onScene(OpenVoiceWorkspace));
            MenuAction(menu,"Изображения сцены…",onScene(OpenSceneImages));
            MenuAction(menu,"Добавить перед",onScene(() => InsertScene(sceneIndex)));
            MenuAction(menu,"Добавить после",onScene(() => InsertScene(sceneIndex+1)));
            menu.Items.Add(new ToolStripSeparator());
            var remove=MenuAction(menu,"Удалить сцену",onScene(RemoveScene)); remove.Name="DeleteScene";
            return menu;
        }
        void SceneMenu() { if (SelectedScene==null) return; var menu=CreateSceneMenu(sceneIndex); activeSceneMenu=menu; menu.Closed += (sender,args) => DisposeMenuLater(menu); menu.Show(Cursor.Position); }
        void SceneVideoMenu() {
            if (SelectedScene==null) return;
            SetSceneVideoMode("own");
            OpenPool(3);
        }
        void SetSceneVideoMode(string mode) { if (SelectedScene==null) return; if (mode=="own" && string.IsNullOrWhiteSpace(SelectedScene.Videos)) {SelectedScene.Videos=Path.Combine(SelectedScene.Folder,"Видео");Directory.CreateDirectory(template.Resolve(SelectedScene.Videos));} SelectedScene.VideoMode=mode; RefreshChips(); Changed(true); Save(); }
        static string ImageModeLabel(AssemblyLayer layer) { return layer.Fixed ? "Постоянный" : layer.Unique ? "Без повторов" : "Меняется"; }
        static string TransitionLabel(string key) {
            switch (key) { case "random": return "Автоматически"; case "cut": return "Нет"; case "fade": return "Плавно"; case "zoomsoft": return "Приближение"; case "zoomout": return "Отдаление"; case "pull": return "Втягивание"; case "swipe": return "Свайп"; default: return "Другой"; }
        }
        void TransitionMenu() {
            var choices=new List<Tuple<string,Action,bool>>();
            foreach (string effect in new[] {"random","cut","fade","zoomsoft","zoomout","pull","swipe"}) {string key=effect; choices.Add(Tuple.Create(TransitionLabel(key),(Action)(() => SetTransition(key)),template.Transition==key));}
            foreach (var speed in new[] {Tuple.Create("Быстро · 0,2 с",.2),Tuple.Create("Обычно · 0,35 с",.35),Tuple.Create("Мягко · 0,5 с",.5)}) {var chosen=speed; choices.Add(Tuple.Create(chosen.Item1,(Action)(() => {template.TransitionDuration=Math.Min(chosen.Item2,template.Scenes.Select(scene => scene.Duration).DefaultIfEmpty(5).Min()/2-.01); Changed(false); Save();}),Math.Abs(template.TransitionDuration-chosen.Item2)<.01));}
            ShowChoices("Переход всего кадра",transitionButton,choices);
        }
        void SetTransition(string key) {template.Transition=key; if (template.TransitionDuration==0) template.TransitionDuration=.35; template.TransitionDuration=Math.Min(template.TransitionDuration,template.Scenes.Select(scene => scene.Duration).DefaultIfEmpty(5).Min()/2-.01); Changed(false); Save();}
        void ChangeCount(int delta) { if (batchMaximum<1) return; count.Value=Math.Max(1,Math.Min((decimal)batchMaximum,count.Value+delta)); }
        void UpdateCountControls() {
            if (countButton==null) return; bool ready=batchMaximum>0 && template.Scenes.Count>0 && cancellation==null;
            lessButton.Enabled=ready && count.Value>1; moreButton.Enabled=ready && count.Value<batchMaximum; countButton.Enabled=ready;
            assembleButton.Enabled=ready;
        }
        void ApplyCapacity(AssemblyCapacity capacity) {
            if (template.Scenes.Count==0) capacityLabel.Text="Добавь сцену для сборки";
            else {
                string journal="Журнал "+AssemblyHistoryScope.ShortLabel(AssemblyHistoryScope.Normalize(template.HistoryScope));
                capacityLabel.Text=journal+" · "+capacity.Caption;
            }
            batchMaximum=capacity.BatchLimit;
            count.Maximum=Math.Max(1,batchMaximum); UpdateCountControls();
        }
        void CountMenu() {
            if (batchMaximum<1) return; var popup=ChoiceFrame("Сколько роликов?",countButton,260,160);
            var input=new TextBox {Text=template.Count.ToString(),BackColor=Theme.Elevated,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.None,Font=Theme.FontCardTitle,TextAlign=HorizontalAlignment.Center,Location=new Point(16,46),Size=new Size(228,30)}; popup.Controls.Add(input);
            popup.Controls.Add(new Label {Text="Доступно до "+batchMaximum.ToString("N0"),ForeColor=Theme.TextSecondary,Location=new Point(16,82),Size=new Size(228,24),TextAlign=ContentAlignment.MiddleCenter});
            Action apply=() => {int n; if (int.TryParse(input.Text,out n) && n>=1 && n<=batchMaximum) {count.Value=n; popup.Close();} else {input.BackColor=Color.FromArgb(75,40,40);input.Focus();}};
            var done=Button("Готово",apply,true); done.SetBounds(16,112,228,34); done.AutoSize=false; popup.Controls.Add(done); input.KeyDown+=(sender,args) => {if (args.KeyCode==Keys.Enter) apply();}; popup.Show(this); input.Focus(); input.SelectAll();
        }
        void FormatMenu() {
            var choices=new List<Tuple<string,Action,bool>>();
            foreach (var f in new[] {Tuple.Create("9:16 · TikTok / Shorts",1080,1920),Tuple.Create("9:16 · 720p",720,1280),Tuple.Create("4:5 · вертикальный",1080,1350),Tuple.Create("3:4 · вертикальный",1080,1440),Tuple.Create("1:1 · квадрат",1080,1080),Tuple.Create("16:9 · широкий",1920,1080)}) {var format=f; choices.Add(Tuple.Create(format.Item1,(Action)(() => SetFormat(format.Item2,format.Item3)),template.Width==format.Item2 && template.Height==format.Item3));}
            choices.Add(Tuple.Create("Ускорение · " + (template.RenderMode == "cpu" ? "процессор" : "авто"), (Action)RenderModeMenu, false));
            ShowChoices("Формат ролика",formatButton,choices);
        }
        void RenderModeMenu() {
            ShowChoices("Сборка роликов", formatButton, new[] {
                Tuple.Create("Автоматически", (Action)(() => SetRenderMode("auto")), template.RenderMode == "auto"),
                Tuple.Create("На процессоре", (Action)(() => SetRenderMode("cpu")), template.RenderMode == "cpu")
            });
        }
        void SetRenderMode(string mode) { template.RenderMode = mode; Save(); status.Text = mode == "auto" ? "Ускорение включено. Видеокарта проверяется перед сборкой." : "Сборка на процессоре. Размер и расположение сохранены."; }
        Form ChoiceFrame(string title,Control anchor,int w,int h) {
            var popup=new Form {Name="StudioChoice",FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,ClientSize=new Size(w,h),BackColor=Theme.Card,ForeColor=Theme.TextPrimary,Font=Theme.FontBody,KeyPreview=true};
            var area=Screen.FromControl(this).WorkingArea; var point=anchor.PointToScreen(new Point(0,anchor.Height+6)); popup.Location=new Point(Math.Max(area.Left,Math.Min(point.X,area.Right-w)),Math.Max(area.Top,Math.Min(point.Y,area.Bottom-h)));
            popup.Controls.Add(new Label {Text=title,Font=Theme.FontCardTitle,ForeColor=Theme.TextPrimary,Location=new Point(14,10),Size=new Size(w-28,28)});
            popup.Paint+=(sender,args) => {using (var pen=new Pen(Theme.Border)) args.Graphics.DrawRectangle(pen,0,0,w-1,h-1);}; popup.Deactivate+=(sender,args) => popup.Close(); popup.KeyDown+=(sender,args) => {if (args.KeyCode==Keys.Escape) popup.Close();}; popup.FormClosed+=(sender,args) => popup.Dispose(); return popup;
        }
        void ShowChoices(string title,Control anchor,IEnumerable<Tuple<string,Action,bool>> choices) {
            var list=choices.ToList(); var popup=ChoiceFrame(title,anchor,300,44+list.Count*42+8); int top=44;
            foreach (var choice in list) {var selected=choice; var b=Button((choice.Item3 ? "✓  " : "")+choice.Item1,() => {popup.Close();Later(selected.Item2);},choice.Item3); b.AutoSize=false; b.TextAlign=ContentAlignment.MiddleLeft; b.SetBounds(10,top,280,36); popup.Controls.Add(b); top+=42;}
            popup.Show(this);
        }
        void SetFormat(int w,int h) { template.Width = w; template.Height = h; Changed(true); Save(); status.Text = "Формат " + w + "×" + h + " сохранён для " + AssemblyProfile.ShortLabel(studioProfile) + "."; }
        string Source(int kind) { return kind == 0 ? template.Videos : kind == 1 ? template.Music : kind == 3 ? SelectedScene.Videos : SelectedLayer.Source; }
        string[] Extensions(int kind) { return kind == 0 || kind == 3 ? AssemblyFiles.VideoExtensions : kind == 1 ? AssemblyFiles.MusicExtensions : AssemblyFiles.PictureExtensions; }
        void SetSource(int kind,string source) { if (kind == 0) template.Videos = source; else if (kind == 1) template.Music = source; else if (kind == 3) { SelectedScene.Videos = source; SelectedScene.VideoMode="own"; } else SelectedLayer.Source = source; }
        string[] PickFiles(int kind) {
            using (var dialog = new OpenFileDialog { Multiselect = true, AutoUpgradeEnabled = true, Filter = "Файлы|" + string.Join(";",Extensions(kind).Select(e => "*" + e)) }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                var paths = PathUtil.NormalizeAll(dialog.FileNames);
                foreach (string p in paths) if (!PathUtil.Exists(p)) throw new Exception("Не удалось прочитать «" + Path.GetFileName(p) + "». Перетащите файлы в окно «Общие видео» или выберите их ещё раз.");
                return paths;
            }
        }
        void ImportPool(int kind,string[] files,bool replace = false) {
            Safe(() => {
                files = PathUtil.NormalizeAll(files);
                if (files == null || files.Length == 0) return;
                if (kind == 2) foreach (string file in files.Where(File.Exists).Where(p => AssemblyFiles.PictureExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))) {
                    try { using (var image = Image.FromFile(file)) { if (image.Width <= 0 || image.Height <= 0) throw new Exception("Пустое изображение."); } }
                    catch (Exception e) { throw new Exception("Не удалось открыть картинку «" + Path.GetFileName(file) + "». Проверь файл или загрузи другую картинку.",e); }
                }
                if (kind==2 && SelectedLayer==null) AddObject();
                string source = Source(kind); bool empty = Pool(source,Extensions(kind)).Count == 0;
                string fallback = kind == 0 ? "Видео" : kind == 1 ? "Музыка" : Path.Combine(kind == 2 && globalEditing ? "Весь-ролик" : SelectedScene.Folder,kind == 3 ? "Видео" : "Изображения");
                int added = AssemblyWorkspace.Import(template,ref source,fallback,files,Extensions(kind),replace); if (kind==3 && added==0) return; SetSource(kind,source);
                if (kind == 2 && added > 0) { SelectedLayer.Enabled = true; var first = Pool(source,Extensions(kind)).FirstOrDefault(); if (first != null && !SelectedLayer.PlacementConfigured) FitImage(SelectedLayer,first); if (SelectedLayer.Fixed && first != null && (replace || empty && string.IsNullOrWhiteSpace(SelectedLayer.FixedAssetHash))) SelectedLayer.FixedAssetHash=AssemblyFiles.Hash(first); previewImages.Remove(SelectedLayer); {var pool=Pool(source,Extensions(kind)); SelectedLayer.Fixed=pool.Select(AssemblyFiles.Hash).Distinct().Count()==1; SelectedLayer.Unique=false; SelectedLayer.FixedAssetHash=SelectedLayer.Fixed ? AssemblyFiles.Hash(pool[0]) : "";} }
                LoadLayer(); RefreshChips(); Changed(kind != 1); Save(); status.Text = "Добавлено вариантов: " + added + ". Расположение объекта сохранено.";
            });
        }
        void FitImage(AssemblyLayer l,string path) { l.PlacementConfigured = true; using (var image = Image.FromFile(path)) { l.Width = .84; l.Height = l.Width * template.Width * image.Height / image.Width / template.Height; if (l.Height > .78) { l.Width *= .78/l.Height; l.Height = .78; } l.Height = Math.Max(.02,l.Height); l.Width = Math.Max(.02,l.Width); l.X = (1-l.Width)/2; l.Y = (1-l.Height)/2; } }
        void OpenPool(int kind) { Safe(() => { if ((kind==2 || kind==3) && SelectedScene==null) return; if (kind==3 && string.IsNullOrWhiteSpace(SelectedScene.Videos)) {SelectedScene.Videos=Path.Combine(SelectedScene.Folder,"Видео");Directory.CreateDirectory(template.Resolve(SelectedScene.Videos));} if (kind==2 && SelectedLayer==null) AddObject(); using (var form = CreatePoolWindow(kind)) form.ShowDialog(this); }); }
        Form CreatePoolWindow(int kind) {
            var form = new Form { Text = kind == 0 ? "Общие видео для сцен" : kind == 1 ? "Музыка на весь ролик" : kind == 3 ? "Видео выбранной сцены" : "Изображения · " + (globalEditing ? "Весь ролик" : SceneLabel(SelectedScene,sceneIndex)), ClientSize = new Size(680,560), MinimumSize = new Size(600,440), BackColor = Theme.Background, ForeColor = Theme.TextPrimary, Font = Theme.FontBody, StartPosition = FormStartPosition.CenterParent };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,46)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); form.Controls.Add(root);
            var packs = Row(); root.Controls.Add(packs,0,0); var actions = Row(); root.Controls.Add(actions,0,1); var modes = Row(); root.Controls.Add(modes,0,2);
            var filesView = new AssemblyScrollStack { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0,0,16,0) }; root.Controls.Add(filesView,0,3);
            var bottom = Row(); bottom.Controls.Add(Button("Готово", () => form.Close(),true)); bottom.Controls.Add(Button("Открыть папку", () => Safe(() => { string path = template.Resolve(Source(kind)); if (File.Exists(path)) path = Path.GetDirectoryName(path); if (string.IsNullOrWhiteSpace(path)) path = template.Resolve(SelectedScene.Folder); Directory.CreateDirectory(path); Ui.Open(path); }))); root.Controls.Add(bottom,0,4);
            Action refresh = null;
            refresh = () => {
                foreach (Control c in packs.Controls.Cast<Control>().ToArray()) c.Dispose(); packs.Controls.Clear();
                foreach (Control c in modes.Controls.Cast<Control>().ToArray()) c.Dispose(); modes.Controls.Clear();
                if (kind == 1) modes.Controls.Add(new AssemblySlider("Громкость",volume,"%") { Width = 560, Height = 66 });
                if (kind==2) {
                    packs.Controls.Add(new Label {Text="Объект "+(layerIndex+1)+" · одно изображение в каждом ролике",ForeColor=Theme.TextPrimary,AutoSize=true,Margin=new Padding(0,8,0,0)});
                    modes.Controls.Add(new Label {Text="Одна картинка — постоянная. Несколько — выбирается одна на ролик.",ForeColor=Theme.TextSecondary,AutoSize=true,Margin=new Padding(0,8,0,0)});
                } else packs.Controls.Add(new Label {Text=kind==0 ? "Фон заполняет кадр. На каждую сцену выбирается один отрывок." : kind==1 ? "Один трек на весь ролик. В следующем ролике — другой вариант." : "Свои видео используются только в этой сцене. Общий фон выбирается кнопкой у сцены.",ForeColor=Theme.TextSecondary,AutoSize=true,Margin=new Padding(0,8,0,0)});
                foreach (Control c in filesView.Controls.Cast<Control>().ToArray()) c.Dispose(); filesView.Controls.Clear();
                var files = Pool(Source(kind),Extensions(kind));
                if (files.Count == 0) filesView.Controls.Add(new Label { Text = "Пока пусто. Нажми «+ Добавить файлы» или перетащи файлы сюда.\nПустая пачка картинок не мешает собрать ролик.", Width = 580, Height = 64, ForeColor = Theme.TextSecondary });
                foreach (string file in files) {
                    var row = new Panel { Width = Math.Max(500,filesView.Width-24), Height = 78, BackColor = Theme.Card, Margin = new Padding(0,0,0,6) };
                    var thumb = new PictureBox { Location = new Point(8,6), Size = new Size(86,64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Background }; row.Controls.Add(thumb);
                    if (kind == 2) try { using (var img = Image.FromFile(file)) thumb.Image = new Bitmap(img); } catch { }
                    row.Disposed += (s,e) => thumb.Image?.Dispose();
                    var pick = Button((kind == 2 && SelectedLayer.Fixed && AssemblyFiles.Hash(file) == SelectedLayer.FixedAssetHash ? "✓  " : "") + Path.GetFileName(file), () => {
                        if (kind == 2) { SelectPicture(file); refresh(); return; }
                        string target = PathUtil.Normalize(file);
                        if (!PathUtil.Exists(target)) { MessageBox.Show(form, "Файл не найден. Если это было исходное видео с Рабочего стола, сначала нажмите «+ Добавить файлы» — копия появится в материалах.", "Сборка роликов", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                        Ui.Open(target);
                    }); pick.SetBounds(104,12,Math.Max(300,row.Width-116),50); pick.AutoSize = false; pick.TextAlign = ContentAlignment.MiddleLeft; row.Controls.Add(pick); filesView.Controls.Add(row);
                }
            };
            actions.Controls.Add(Button("+ Добавить файлы", () => { ImportPool(kind,PickFiles(kind)); refresh(); },true)); actions.Controls.Add(Button("Заменить пачку", () => { ImportPool(kind,PickFiles(kind),true); refresh(); }));
            actions.Controls.Add(Button("Очистить", () => { ClearPool(kind); refresh(); }));
            if (kind == 1) root.RowStyles[2].Height = 72;
            SetDrop(filesView,files => { ImportPool(kind,files); refresh(); }); refresh();
            form.FormClosed += (s,e) => { LoadLayer(); RefreshChips(); Changed(true); Save(); };
            return form;
        }
        void AddPack(string[] files) {AddObject(); ImportPool(2,files);}
        void ClearPool(int kind) { string path = (kind == 0 ? "Видео" : kind == 1 ? "Музыка" : Path.Combine(SelectedScene.Folder,kind == 3 ? "Видео" : "Изображения")) + "-" + Guid.NewGuid().ToString("N").Substring(0,6); Directory.CreateDirectory(template.Resolve(path)); SetSource(kind,path); if (kind==2 && SelectedLayer!=null) SelectedLayer.FixedAssetHash=""; previewImages.Clear(); LoadLayer(); RefreshChips(); Changed(true); Save(); status.Text = "Пачка очищена в студии. Предыдущие файлы сохранены в папке материалов."; }
        void SelectPicture(string file) { previewImages[SelectedLayer]=file; if (SelectedLayer.Fixed) SelectedLayer.FixedAssetHash=AssemblyFiles.Hash(file); Changed(false); Save(); }
        void SetImageMode(int mode) {
            SelectedLayer.ModeExplicit=true; SelectedLayer.Fixed=mode==1; SelectedLayer.Unique=mode==2; SelectedLayer.Enabled=true;
            if (SelectedLayer.Fixed) { string chosen; var files=Pool(SelectedLayer.Source,AssemblyFiles.PictureExtensions); if (!previewImages.TryGetValue(SelectedLayer,out chosen) || !files.Contains(chosen)) chosen=files.FirstOrDefault(); SelectedLayer.FixedAssetHash=chosen==null ? "" : AssemblyFiles.Hash(chosen); }
            LoadLayer(); RefreshChips(); Changed(false); Save();
        }
        void SettingsMenu() {
            var menu = Theme.MakeContextMenu();
            menu.Items.Add("Видео и деление экрана…", null, (s, e) => OpenVideoEditor());
            menu.Items.Add("Изображения на весь ролик…", null, (s, e) => { globalEditing = true; layerIndex = 0; RefreshChips(); LoadLayer(); RefreshCanvas(); if (SelectedLayer == null) AddObject(); OpenPool(2); });
            menu.Items.Add("Заголовки роликов…", null, (s, e) => OpenTitles());
            menu.Items.Add("Папка материалов",null,(s,e) => Ui.Open(template.Materials));
            menu.Items.Add("Куда сохранять ролики…",null,(s,e) => Safe(() => { var p = Ui.Folder(this,template.Output); if (p != null) { template.Output = p; Save(); RefreshInfo(); } }));
            menu.Items.Add("Общий журнал связок (как раньше)…",null,(s,e) => Safe(() => {
                if (MessageBox.Show(this,"Один журнал на RU и EN: связки будут общими для обоих рынков.\nПродолжить?","Сборка роликов",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes) return;
                AssemblyMarketProfiles.Capture(template, studioProfile); template.HistoryScope = AssemblyHistoryScope.Shared; Save(); capacityKey = null; RequestCapacity();
                status.Text = "Включён общий журнал связок. Для раздельных RU/EN снова выберите вкладку RU или EN.";
            }));
            menu.Items.Add("Посмотреть пример ролика",null,async (s,e) => await Run(true));
            menu.Items.Add("Служебные отчёты",null,(s,e) => Safe(() => Ui.Open(AssemblyOutputFiles.Folder(Path.Combine(template.Output,"report.mp4")))));
            menu.Items.Add("Убрать txt/xml рядом с роликами",null,(s,e) => Safe(() => {
                Directory.CreateDirectory(template.Output);
                int n = AssemblyOutputFiles.CleanupOutputFolder(template.Output);
                status.Text = n == 0 ? "В папке результатов только MP4 — лишних файлов не найдено." : ("Убрано служебных файлов: " + n + ". Для загрузки нужны только MP4; рецепты хранятся в .videobatch-reports.");
            }));
            menu.Items.Add("Метаданные роликов…",null,(s,e) => MetadataMenu());
            menu.Items.Add("Переходы…",null,(s,e) => TransitionMenu());
            menu.Items.Add("Сохранить копию студии…",null,(s,e) => Safe(() => { Save(); using (var d = new SaveFileDialog { Filter = "Студия|*.xml", FileName = "Моя-студия.xml" }) if (d.ShowDialog(this) == DialogResult.OK) AssemblyFiles.Save(d.FileName,template); }));
            menu.Items.Add("Открыть сохранённую студию…",null,(s,e) => Safe(() => { using (var d = new OpenFileDialog { Filter = "Студия|*.xml" }) if (d.ShowDialog(this) == DialogResult.OK) { var t = AssemblyFiles.Load<AssemblyTemplate>(d.FileName); t.Validate(); AssemblyWorkspace.Prepare(t); AssemblyMarketProfiles.EnsureSeeded(t); template = t; AssemblyMarketProfiles.Apply(template, studioProfile); AssemblyWorkspace.Prepare(template); BindTemplateValues(); previewImages.Clear(); BuildScenes(); SelectScene(0); Save(); } }));
            menu.Items.Add("Новая студия · 4 сцены",null,(s,e) => Safe(() => { Save(); AssemblyFiles.Save(templatePath + ".saved-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml",template); string folder = Path.Combine(template.Materials,"Студия-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); template = AssemblyTemplate.Defaults(); template.Materials = folder; template.Output = VideoBatchOutputPaths.AssemblyFolder(studioProfile); AssemblyWorkspace.Prepare(template); previewImages.Clear(); BindTemplateValues(); BuildScenes(); SelectScene(0); Save(); })); menu.Show(Cursor.Position);
        }
        void MetadataMenu() {
            var source=template.OutputMetadata ?? AssemblyOutputMetadata.Default();
            using (var dialog=new ProcessingDialog(new ProcessingSettings { DeviceMetadata=source.DeviceMetadata,
                CaptureDateEnabled=source.CaptureDate,RandomizeCaptureTime=source.RandomizeCaptureTime,
                CaptureDaysBack=source.CaptureDaysBack,Brands=source.Brands ?? (string[])ProcessingDevices.BrandNames.Clone(),
                MetadataSoftware=source.Software },true)) {
                dialog.SelectMetadataTab();
                if (dialog.ShowDialog(this)!=DialogResult.OK) return;
                var s=dialog.Settings;
                template.OutputMetadata=new AssemblyOutputMetadata { DeviceMetadata=s.DeviceMetadata,CaptureDate=s.CaptureDateEnabled,
                    RandomizeCaptureTime=s.RandomizeCaptureTime,CaptureDaysBack=s.CaptureDaysBack,Brands=s.Brands,Software=s.MetadataSoftware };
                Save(); status.Text="Метаданные сохранены. Они записываются внутрь каждого готового ролика.";
            }
        }
        void BindTemplateValues() { capacityKey=null; batchMaximum=0; UpdateCountControls(); RefreshProfileToggles(); loading=true; count.Maximum=int.MaxValue; count.Value=template.Count; volume.Value=(decimal)(template.MusicVolume*100); voiceoverVolume.Value=(decimal)(template.VoiceoverVolume*100); if (template.SubtitleStyle!=null) subtitlePosition.Value=template.SubtitleStyle.Position; loading=false; RequestTimingHints(); RefreshSubtitleControls(); }
        async void RequestTimingHints() {
            if (!IsHandleCreated || IsDisposed) return;
            timingCancellation?.Cancel(); int version = ++timingVersion;
            var ct = new CancellationTokenSource(); timingCancellation = ct;
            try {
                if (template.HasSceneVoiceovers) { timingAnchorDuration = null; previewSpeechCaptions = null; RefreshInfo(); RefreshCanvas(); return; }
                if (!ToolsLocator.TryResolve(out _, out string probe, out _)) { timingAnchorDuration = null; previewSpeechCaptions = null; RefreshInfo(); return; }
                double? total = await Task.Run(async () => await AssemblySpeechPlan.ResolveVoiceoverDurationAsync(template, probe, ct.Token).ConfigureAwait(false), ct.Token);
                List<WarmupCaption> speechCaptions = null;
                if (total.HasValue && total.Value > 0)
                    speechCaptions = await Task.Run(async () => await AssemblySpeechPlan.BuildCaptionsFromSpeechAsync(template, probe, ct.Token).ConfigureAwait(false), ct.Token);
                if (version == timingVersion && !IsDisposed) {
                    timingAnchorDuration = total;
                    previewSpeechCaptions = speechCaptions == null ? null : WarmupSubtitles.FitToDuration(speechCaptions, total ?? template.Duration);
                    RefreshInfo(); RefreshCanvas(); UpdateSubtitleTimingMessage();
                }
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (version == timingVersion && !IsDisposed) { timingAnchorDuration = null; previewSpeechCaptions = null; RefreshInfo(); status.Text = "Таймкоды озвучки: " + ex.Message; } }
            finally { if (timingCancellation == ct) timingCancellation = null; ct.Dispose(); }
        }
        static string HistoryLedgerFingerprint(string folder,string scope) {
            scope=AssemblyHistoryScope.Normalize(scope);
            if (!Directory.Exists(folder)) return "";
            var files=new List<string> { AssemblyHistoryStore.PathFor(folder,scope) };
            string backup=files[0]+".bak"; if (File.Exists(backup)) files.Add(backup);
            if (scope==AssemblyHistoryScope.Shared) files.AddRange(Directory.GetFiles(folder,"*.xml").Where(p => !p.StartsWith(Path.Combine(folder,"markets"),StringComparison.OrdinalIgnoreCase)));
            return string.Join("|",files.Where(File.Exists).Select(f => f+":"+File.GetLastWriteTimeUtc(f).Ticks));
        }
        async void RequestBackground() {
            if (canvas == null || !IsHandleCreated || cancellation != null) return;
            previewCancellation?.Cancel(); int version = ++previewVersion; var ct = new CancellationTokenSource(); previewCancellation = ct;
            try {
                if (SelectedScene==null) return; var files = Pool(template.ContinuousVideo ? template.Videos : template.VideoSource(SelectedScene),AssemblyFiles.VideoExtensions); canvas.SetBackground(null); if (files.Count == 0) return;
                if (!ToolsLocator.TryResolve(out string ffmpeg,out string probe,out string hint)) { status.Text = hint; return; }
                int w = template.Width, h = template.Height; Directory.CreateDirectory(previewFolder); string file = Path.Combine(previewFolder,Guid.NewGuid().ToString("N") + ".jpg");
                try {
                    var second = Pool(template.SecondaryVideos, AssemblyFiles.VideoExtensions);
                    if (template.SplitLayout != "none" && second.Count > 0) {
                        bool vertical = AssemblySplit.IsVertical(template.SplitLayout);
                        AssemblySplit.PaneSizes(template, out int primarySpan, out int secondarySpan, out int splitLine, out int feather);
                        int pw = vertical ? w : primarySpan, ph = vertical ? primarySpan : h;
                        int sw2 = vertical ? w : secondarySpan, sh2 = vertical ? secondarySpan : h;
                        string padB = AssemblySplit.PadOffset(vertical, true, primarySpan, w, h);
                        string weight = AssemblySplit.BlendWeight(vertical, splitLine, feather);
                        string graph = "[0:v]" + AssemblySplit.Fill(pw, ph, template.Fps, template.SplitPrimaryZoom) + ",pad=" + w + ":" + h + ":0:0,format=yuv444p[a];[1:v]" + AssemblySplit.Fill(sw2, sh2, template.Fps, template.SplitSecondaryZoom) + ",pad=" + w + ":" + h + ":" + padB + ",format=yuv444p[b];[a][b]blend=all_expr='A*(1-(" + weight + "))+B*(" + weight + ")'[preview]";
                        await Core.Tool(ffmpeg,new[] { "-v","error","-y","-i",files[0],"-i",second[0],"-filter_complex_threads","1","-filter_complex",graph,"-map","[preview]","-frames:v","1","-threads","1",file },ct.Token,null,60);
                    } else await Core.Tool(ffmpeg,new[] { "-v","error","-y","-i",files[0],"-frames:v","1","-vf","scale="+w+":"+h+":force_original_aspect_ratio=increase,crop="+w+":"+h,"-threads","1",file },ct.Token,null,60);
                    if (version == previewVersion && !IsDisposed) using (var img = Image.FromFile(file)) canvas.SetBackground(new Bitmap(img));
                } finally { if (File.Exists(file)) File.Delete(file); }
            } catch (OperationCanceledException) { } catch (Exception e) { if (version == previewVersion && !IsDisposed) status.Text = "Не удалось показать видео: " + e.Message; }
            finally { if (previewCancellation == ct) previewCancellation = null; ct.Dispose(); }
        }
        void WarmMaterialsSilent() {
            if (string.IsNullOrWhiteSpace(template?.Materials)) return;
            if (!ToolsLocator.TryResolve(out string ffmpeg, out string probe, out _)) return;
            string root = template.Materials;
            _ = Task.Run(async () => { try { await VideoSdr.WarmFolder(ffmpeg, probe, root, CancellationToken.None).ConfigureAwait(false); if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RequestCapacity)); } catch { } });
        }
        async void RequestCapacity() {
            if (capacityLabel==null || !IsHandleCreated || IsDisposed || cancellation!=null) return;
            try {
                var t=Store.Clone(template); string scope=AssemblyHistoryScope.Normalize(t.HistoryScope);
                var files=t.Scenes.SelectMany(scene => Pool(t.VideoSource(scene),AssemblyFiles.VideoExtensions).Concat(Pool(scene.Music,AssemblyFiles.MusicExtensions)).Concat(scene.Layers.Where(l => l.Enabled).SelectMany(l => Pool(l.Source,t.PictureExtensions)))).Concat(Pool(t.Music,AssemblyFiles.MusicExtensions)).Concat(Pool(t.SecondaryVideos,AssemblyFiles.VideoExtensions)).Concat(t.GlobalLayers.Where(l => l.Enabled).SelectMany(l => Pool(l.Source,t.PictureExtensions))).Distinct().ToList();
                string key=AssemblyFiles.HashText(t.ContinuousVideo+"|"+t.SecondaryVideos+"|"+t.SplitLayout+"|"+string.Join(";",t.GlobalLayers.Select(l => l.Source+":"+l.Enabled+":"+l.Fixed+":"+l.FixedAssetHash+":"+l.Unique))+"|"+t.Id+"|"+scope+"|"+t.LoopShortVideos+"|"+t.AllowVideoReuse+"|"+t.Transition+"|"+t.TransitionDuration+"|"+string.Join("|",t.Scenes.Select(scene => scene.Duration+":"+t.VideoSource(scene)+":"+string.Join(",",scene.Layers.Select(l => l.Enabled+":"+l.Source+":"+l.Unique+":"+l.Fixed+":"+l.FixedAssetHash))))+"|"+string.Join("|",files.Select(file => {var info=new FileInfo(file); return file+":"+info.Length+":"+info.LastWriteTimeUtc.Ticks;}))+"|"+HistoryLedgerFingerprint(historyFolder,scope));
                if (key==capacityKey) return; capacityKey=key; capacityCancellation?.Cancel(); int version=++capacityVersion;
                var ct=new CancellationTokenSource(); capacityCancellation=ct; capacityLabel.Text="Считаю новые связки…"; batchMaximum=0; UpdateCountControls();
                try {
                    if (!ToolsLocator.TryResolve(out string ffmpeg,out string probe,out string hint)) { capacityLabel.Text="Добавь FFmpeg для расчёта связок"; return; }
                    var capacity=await Task.Run(async () => { var v=await AssemblyEngine.ReadVideos(t,ffmpeg,probe,ct.Token); var h=AssemblyHistoryStore.Load(historyFolder,scope); return new AssemblyInventory(t,v,h).Calculate(); },ct.Token);
                    if (version==capacityVersion && !IsDisposed) ApplyCapacity(capacity);
                } catch (OperationCanceledException) { }
                catch (Exception ex) { if (version==capacityVersion && !IsDisposed) { capacityLabel.Text=files.Any(f => AssemblyFiles.VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) ? "Проверь материалы · расчёт недоступен" : "Добавь видео для расчёта связок"; status.Text=ex.Message; capacityKey=null; } }
                finally { if (capacityCancellation==ct) capacityCancellation=null; ct.Dispose(); }
            } catch (Exception ex) { if (!IsDisposed) { capacityLabel.Text="Расчёт недоступен"; status.Text=ex.Message; } }
        }
        void Busy(bool busy) { header.Enabled = workspace.Enabled = !busy; foreach (Control c in footer.Controls) c.Enabled = !busy; stop.Visible = busy; stop.Enabled = busy; if (busy) progress.Value = 0; else UpdateCountControls(); }
        async Task Run(bool sample) {
            if (cancellation != null) return; saveTimer.Stop(); previewCancellation?.Cancel(); ++previewVersion;
            try {
                AssemblyWorkspace.Prepare(template); Save(); if (!ToolsLocator.TryResolve(out string ffmpeg,out string probe,out string hint)) throw new Exception(hint);
                cancellation = new CancellationTokenSource(); Busy(true);
                var updates = new Progress<Update>(u => { AppDiagnostics.Write("Сборка",studioProfile,"","render",u.Text); status.Text = u.Text; progress.Value = (int)Math.Max(0,Math.Min(1000,u.Percent*10)); });
                if (sample) {
                    var t = Store.Clone(template);
                    var videos = await AssemblyEngine.ReadVideos(t,ffmpeg,probe,cancellation.Token); var history = AssemblyHistoryStore.Load(historyFolder,t.HistoryScope);
                    var batch = AssemblyPlanner.Create(t,videos,history,new Random(),1,null,cancellation.Token); if (batch.Plans.Count == 0) throw new Exception(batch.Limit);
                    Directory.CreateDirectory(previewFolder); string path = Path.Combine(previewFolder,"example-" + Guid.NewGuid().ToString("N") + ".mp4"), work = Path.Combine(previewFolder,"render-" + Guid.NewGuid().ToString("N"));
                    try { await Task.Run(() => AssemblyEngine.Render(t,batch.Plans[0],ffmpeg,probe,path,work,AssemblyRaster.Save,cancellation.Token,s => ((IProgress<Update>)updates).Report(new Update(s,0)))); }
                    finally { if (Directory.Exists(work)) Directory.Delete(work,true); }
                    status.Text = "Пример готов. Файлы без повторов не израсходованы."; progress.Value = 1000; Ui.Open(path);
                } else {
                    var t = Store.Clone(template);
                    var result = await Task.Run(() => AssemblyEngine.Run(t,ffmpeg,probe,historyFolder,AssemblyRaster.Save,updates,cancellation.Token));
                    ShowAssemblyBatchResult(result, "Сборка · " + AssemblyProfile.ShortLabel(studioProfile));
                }
            } catch (OperationCanceledException) { status.Text = "Сборка остановлена."; } catch (Exception e) { AppDiagnostics.Write("Сборка",studioProfile,"","error",e.Message,"error"); status.Text = e.Message; if (throwErrors) throw; Ui.Error(this,e); }
            finally { cancellation?.Dispose(); cancellation = null; Busy(false); capacityKey=null; RequestCapacity(); if (closeAfter) Close(); }
        }
        void ShowAssemblyBatchResult(BatchResult result, string title) {
            foreach (var error in result.Errors) AppDiagnostics.Write("Сборка", studioProfile, "", "error", error, "error");
            if (result.Errors.Count == 0) {
                try {
                    var history = AssemblyHistoryStore.Load(historyFolder, template.HistoryScope);
                    status.Text = "Готово: " + result.Outputs.Count + " ролик(ов). Журнал "
                        + AssemblyHistoryScope.ShortLabel(AssemblyHistoryScope.Normalize(template.HistoryScope))
                        + ": " + history.Combinations.Count.ToString("N0") + " связок сохранено — повторы блокируются.";
                } catch { status.Text = "Готово: " + result.Outputs.Count + " ролик(ов)."; }
                return;
            }
            string details = "Готовых роликов: " + result.Outputs.Count + "\n\n" + string.Join("\n\n", result.Errors);
            MessageBox.Show(this, details, title, MessageBoxButtons.OK, result.Outputs.Count == 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        void Safe(Action action) { try { action(); } catch (Exception e) { AppDiagnostics.Write("Сборка",studioProfile,"","error",e.Message,"error"); if (throwErrors) throw; if (status != null) status.Text = e.Message; Ui.Error(this,e); } }
        protected override void Dispose(bool disposing) { if (disposing) { saveTimer.Dispose(); previewCancellation?.Cancel(); capacityCancellation?.Cancel(); cancellation?.Cancel(); } base.Dispose(disposing); }
    }
    public sealed class AssemblySceneRow : Panel {
        protected override void OnSizeChanged(EventArgs e) {
            base.OnSizeChanged(e); if (Width<16 || Height<16) return;
            using (var path=new GraphicsPath()) { int d=16,w=Width,h=Height; path.AddArc(0,0,d,d,180,90); path.AddArc(w-d,0,d,d,270,90); path.AddArc(w-d,h-d,d,d,0,90); path.AddArc(0,h-d,d,d,90,90); path.CloseFigure(); var old=Region; Region=new Region(path); old?.Dispose(); }
        }
    }
    public sealed class AssemblySceneButton : Button {
        readonly int number;
        string title, detail;
        bool selected;
        public AssemblySceneButton(int number,Action action) {
            this.number=number; FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; BackColor=Theme.Card; ForeColor=Theme.TextPrimary; Font=Theme.FontBody; Cursor=Cursors.Hand; DoubleBuffered=true; Margin=Padding.Empty; Click += (sender,args) => action();
        }
        bool warnDetail;
        public void SetInfo(string title,string detail,bool selected,bool warn=false) { this.title=title; this.detail=detail; this.selected=selected; warnDetail=warn; AccessibleName=title+". "+detail; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(Theme.Card); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using (var brush=new SolidBrush(warnDetail ? Color.FromArgb(255,254,242,242) : Theme.Card)) e.Graphics.FillRectangle(brush,0,0,Width,Height);
            using (var brush=new SolidBrush(selected ? Theme.Accent : warnDetail ? Theme.Error : Theme.Elevated)) e.Graphics.FillEllipse(brush,8,10,24,24);
            TextRenderer.DrawText(e.Graphics,number.ToString(),Font,new Rectangle(8,10,24,24),selected || warnDetail ? Color.White : Theme.TextSecondary,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics,title,Theme.FontCardTitle,new Rectangle(38,7,Width-42,20),warnDetail ? Theme.Error : Theme.TextPrimary,TextFormatFlags.Left|TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics,detail,Font,new Rectangle(38,26,Width-42,16),warnDetail ? Theme.Error : Theme.TextSecondary,TextFormatFlags.Left|TextFormatFlags.EndEllipsis);
            if (warnDetail) using (var pen=new Pen(Theme.Error,2)) e.Graphics.DrawRectangle(pen,1,1,Width-3,Height-3);
            if (selected) using (var pen=new Pen(Theme.Accent,2)) e.Graphics.DrawLine(pen,1,10,1,Height-10);
            if (Focused && !warnDetail) using (var pen=new Pen(Theme.Accent)) e.Graphics.DrawRectangle(pen,1,1,Width-3,Height-3);
        }
    }
    public sealed class AssemblyInsertButton : Button {
        bool hover;
        public AssemblyInsertButton(Action action) { FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; BackColor=Theme.Background; ForeColor=Theme.TextMuted; Font=Theme.FontBody; Cursor=Cursors.Hand; DoubleBuffered=true; AccessibleName="Добавить сцену здесь"; Click += (sender,args) => action(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover=true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover=false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) {
            e.Graphics.Clear(Theme.Background); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            int mid=Height/2, center=Width/2, r=Math.Min(11, Math.Min(Width, Height) / 2 - 2);
            using (var fill=new SolidBrush(hover ? Color.FromArgb(28,Theme.Accent.R,Theme.Accent.G,Theme.Accent.B) : Theme.Card))
                e.Graphics.FillEllipse(fill,center-r,mid-r,r*2,r*2);
            using (var pen=new Pen(hover ? Theme.Accent : Theme.Border, hover ? 2f : 1f))
                e.Graphics.DrawEllipse(pen,center-r,mid-r,r*2,r*2);
            TextRenderer.DrawText(e.Graphics,"+",Font,new Rectangle(center-r,mid-r,r*2,r*2),hover ? Theme.Accent : Theme.TextMuted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding);
        }
    }
    public sealed class AssemblyCanvas : Control {
        AssemblyTemplate template;
        AssemblyScenePlan scene;
        AssemblyLayer selected;
        Image background, subtitleOverlay;
        readonly List<Bitmap> images = new List<Bitmap>();
        readonly Dictionary<AssemblyLayer, Size> sourceSizes = new Dictionary<AssemblyLayer, Size>();
        Point origin;
        double oldX, oldY, oldWidth, oldHeight;
        bool dragging, resizing, draggingSplit, draggingSubtitle, subtitleDragEnabled, subtitleEditing, subtitleSelected;
        WarmupSubtitleStyle subtitleStyle;
        string subtitlePreviewText = "";
        RectangleF subtitlePlateScreen = RectangleF.Empty;
        public event Action<int> LayerSelected;
        public event Action PlacementChanged;
        public event Action<int> SubtitlePositionChanged;
        public event Action SplitRatioChanged;
        public event Action SplitZoomChanged;
        public AssemblyCanvas() { DoubleBuffered = true; BackColor = Theme.Background; }
        public void SetBackground(Image value) { background?.Dispose(); background = value; Invalidate(); }
        public void SetSubtitleEditing(bool value) { subtitleEditing = value; subtitleSelected = value; Invalidate(); }
        public void ClearSubtitles() { subtitleOverlay?.Dispose(); subtitleOverlay = null; subtitleDragEnabled = false; subtitleStyle = null; subtitlePlateScreen = RectangleF.Empty; subtitleSelected = false; Cursor = Cursors.Default; Invalidate(); }
        public void SetSubtitles(WarmupSubtitleStyle style, string text) {
            subtitleOverlay?.Dispose();
            subtitlePreviewText = text ?? "";
            subtitleStyle = style?.Copy();
            subtitleDragEnabled = subtitleStyle != null && subtitleStyle.Enabled && !string.IsNullOrWhiteSpace(subtitlePreviewText);
            if (subtitleDragEnabled && template != null)
                subtitleOverlay = WarmupSubtitles.Paint(subtitlePreviewText, subtitleStyle, template.Width, template.Height);
            else subtitleOverlay = null;
            UpdateSubtitlePlateScreen();
            Cursor = subtitleDragEnabled ? Cursors.SizeNS : Cursors.Default;
            Invalidate();
        }
        void UpdateSubtitlePlateScreen() {
            subtitlePlateScreen = RectangleF.Empty;
            if (!subtitleDragEnabled || template == null || subtitleStyle == null || string.IsNullOrWhiteSpace(subtitlePreviewText)) return;
            var f = Frame; if (f.Width <= 0) return;
            var plate = WarmupSubtitles.PlateRect(subtitlePreviewText, subtitleStyle, template.Width, template.Height);
            subtitlePlateScreen = new RectangleF(f.X + plate.X / template.Width * f.Width, f.Y + plate.Y / template.Height * f.Height, plate.Width / template.Width * f.Width, plate.Height / template.Height * f.Height);
        }
        bool SubtitleHit(Point p) {
            if (!subtitleDragEnabled) return false;
            UpdateSubtitlePlateScreen();
            if (subtitlePlateScreen.Width <= 0) return false;
            var hit = subtitlePlateScreen; hit.Inflate(14, 14);
            return hit.Contains(p);
        }
        public void ClearScene() { scene=null; selected=null; foreach (var image in images) image?.Dispose();images.Clear();SetBackground(null);ClearSubtitles(); }
        public void SetScene(AssemblyTemplate t, AssemblyScenePlan s, AssemblyLayer chosen) {
            template = t; scene = s; selected = chosen;
            foreach (var image in images) image?.Dispose(); images.Clear(); sourceSizes.Clear();
            foreach (var l in scene.Layers) try {
                if (!string.IsNullOrWhiteSpace(l.Asset.Path) && !Path.GetExtension(l.Asset.Path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    using (var source = Image.FromFile(l.Asset.Path)) sourceSizes[l.Layer] = source.Size;
                images.Add(AssemblyRaster.Create(t, l));
            } catch { images.Add(null); }
            Invalidate();
        }
        RectangleF Frame {
            get { if (template == null) return RectangleF.Empty; float scale = Math.Min((Width - 16f) / template.Width, (Height - 16f) / template.Height); return new RectangleF((Width - template.Width * scale) / 2, (Height - template.Height * scale) / 2, template.Width * scale, template.Height * scale); }
        }
        RectangleF Box(AssemblyLayer l) {
            var f = Frame;
            var box = new RectangleF(f.X + (float)l.X * f.Width, f.Y + (float)l.Y * f.Height, (float)l.Width * f.Width, (float)l.Height * f.Height);
            return sourceSizes.TryGetValue(l, out Size size) ? AssemblyRaster.FitImageBounds(box, size) : box;
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); if (scene==null) {TextRenderer.DrawText(e.Graphics,"Нет сцен\nНажми + слева, чтобы добавить первую",Font,ClientRectangle,Theme.TextSecondary,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak);return;} if (Frame.Width <= 0) return; var f = Frame; e.Graphics.FillRectangle(Brushes.Black, f);
            if (background != null) e.Graphics.DrawImage(background, f);
            else { using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) e.Graphics.DrawString("Добавь видео в шапке\n\nПревью появится автоматически", Font, Brushes.Gray, f, format); }
            if (template != null && template.SplitLayout != "none") {
                bool vertical = AssemblySplit.IsVertical(template.SplitLayout);
                AssemblySplit.PaneSizes(template, out int primarySpan, out _, out int splitLine, out _);
                float ratio = splitLine / (float)(vertical ? template.Height : template.Width);
                float line = vertical ? f.Top + f.Height * ratio : f.Left + f.Width * ratio;
                using (var pen = new Pen(Color.FromArgb(230, Theme.Accent), Math.Max(2f, f.Width / template.Width * 3f))) {
                    if (vertical) e.Graphics.DrawLine(pen, f.Left, line, f.Right, line);
                    else e.Graphics.DrawLine(pen, line, f.Top, line, f.Bottom);
                }
                using (var grip = new SolidBrush(Color.FromArgb(220, Theme.Accent)))
                    e.Graphics.FillEllipse(grip, vertical ? f.Left + f.Width / 2f - 8 : line - 8, vertical ? line - 8 : f.Top + f.Height / 2f - 8, 16, 16);
                using (var label = new Font(Font.FontFamily, Math.Max(8f, f.Height * .032f), FontStyle.Bold)) {
                    int pct = (int)Math.Round((vertical ? ratio : ratio) * 100);
                    string primary = (vertical ? "Верх" : "Слева") + " · " + pct + "%";
                    string secondary = (vertical ? "Низ" : "Справа") + " · " + (100 - pct) + "%";
                    TextRenderer.DrawText(e.Graphics, primary, label, Rectangle.Round(new RectangleF(f.Left + 6, f.Top + 6, f.Width - 12, (vertical ? line - f.Top : f.Height) - 8)), Color.White, TextFormatFlags.Left | TextFormatFlags.Top);
                    TextRenderer.DrawText(e.Graphics, secondary, label, vertical ? Rectangle.Round(new RectangleF(f.Left + 6, line + 6, f.Width - 12, f.Bottom - line - 8)) : Rectangle.Round(new RectangleF(line + 6, f.Top + 6, f.Right - line - 8, f.Height - 12)), Color.White, TextFormatFlags.Left | TextFormatFlags.Top);
                }
            }
            for (int i = 0; i < images.Count; i++) if (images[i] != null) e.Graphics.DrawImage(images[i], f);
            if (subtitleOverlay != null) {
                e.Graphics.DrawImage(subtitleOverlay, f);
                UpdateSubtitlePlateScreen();
                if (subtitlePlateScreen.Width > 0 && (subtitleEditing || subtitleSelected)) {
                    var box = subtitlePlateScreen; box.Inflate(4, 4);
                    using (var pen = new Pen(subtitleEditing ? Theme.Accent : Theme.TextMuted, subtitleEditing ? 2.5f : 1.5f) { DashStyle = DashStyle.Dash })
                        e.Graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                    if (subtitleEditing) using (var brush = new SolidBrush(Theme.Accent))
                        e.Graphics.FillEllipse(brush, box.Right - 8, box.Bottom - 8, 14, 14);
                }
            }
            foreach (var item in scene.Layers) {
                var l=item.Layer;
                if (string.IsNullOrWhiteSpace(item.Asset.Path)) { var empty=Box(l); using (var format=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center}) e.Graphics.DrawString("Объект "+(scene.Scene.Layers.IndexOf(l)+1)+"\n+ Изображения",Font,Brushes.White,empty,format); }
                var box = Box(l); using (var pen = new Pen(l == selected ? Theme.Accent : Theme.TextMuted, l == selected ? 2 : 1)) e.Graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                using (var brush = new SolidBrush(Theme.Accent)) { if (l == selected) e.Graphics.FillEllipse(brush, box.Right - 6, box.Bottom - 6, 12, 12); }
            }
            if (template != null && template.SplitLayout != "none")
                using (var hint = new Font("Segoe UI", Math.Max(7f, f.Width * .022f)))
                using (var brush = new SolidBrush(Color.FromArgb(190, 255, 255, 255)))
                    e.Graphics.DrawString("Границу тяните · колёсико над половиной — масштаб", hint, brush, f.X + 6, f.Bottom - 22);
            else if (subtitleDragEnabled && subtitleOverlay != null)
                using (var hint = new Font("Segoe UI", Math.Max(7f, f.Width * .022f)))
                using (var brush = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    e.Graphics.DrawString(subtitleEditing ? "Тяните плашку субтитров" : "Нажмите «Субтитры» или тяните текст", hint, brush, f.X + 6, f.Y + 6);
        }
        bool SplitLineHit(Point p, out float lineScreen) {
            lineScreen = 0;
            if (template == null || template.SplitLayout == "none") return false;
            var f = Frame; if (f.Width <= 0) return false;
            bool vertical = AssemblySplit.IsVertical(template.SplitLayout);
            AssemblySplit.PaneSizes(template, out int primarySpan, out _, out _, out _);
            float ratio = primarySpan / (float)(vertical ? template.Height : template.Width);
            lineScreen = vertical ? f.Top + f.Height * ratio : f.Left + f.Width * ratio;
            return vertical ? Math.Abs(p.Y - lineScreen) <= 12 : Math.Abs(p.X - lineScreen) <= 12;
        }
        void ApplySplitDrag(Point screen) {
            var f = Frame; if (f.Width <= 0 || template == null) return;
            bool vertical = AssemblySplit.IsVertical(template.SplitLayout);
            double ratio = vertical ? (screen.Y - f.Y) / f.Height : (screen.X - f.X) / f.Width;
            template.SplitRatio = Math.Max(.25, Math.Min(.75, ratio));
            SplitRatioChanged?.Invoke();
            Invalidate();
        }
        int SplitPaneAt(Point p) {
            if (template == null || template.SplitLayout == "none") return -1;
            var f = Frame; if (!f.Contains(p)) return -1;
            bool vertical = AssemblySplit.IsVertical(template.SplitLayout);
            AssemblySplit.PaneSizes(template, out int primarySpan, out _, out _, out _);
            float ratio = primarySpan / (float)(vertical ? template.Height : template.Width);
            float line = vertical ? f.Top + f.Height * ratio : f.Left + f.Width * ratio;
            return vertical ? (p.Y < line ? 0 : 1) : (p.X < line ? 0 : 1);
        }
        void ApplySubtitleDrag(Point screen) {
            var f = Frame;
            float localY = (screen.Y - f.Y) / Math.Max(1f, f.Height);
            int pos = (int)Math.Round(Math.Max(10, Math.Min(90, localY * 100f)));
            SubtitlePositionChanged?.Invoke(pos);
            if (subtitleStyle != null && template != null && !string.IsNullOrWhiteSpace(subtitlePreviewText)) {
                subtitleStyle.Position = pos;
                subtitleOverlay?.Dispose();
                subtitleOverlay = WarmupSubtitles.Paint(subtitlePreviewText, subtitleStyle, template.Width, template.Height);
                subtitleSelected = true;
            }
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) {
            base.OnMouseWheel(e);
            if (template == null || template.SplitLayout == "none") return;
            int pane = SplitPaneAt(e.Location);
            if (pane < 0) return;
            double step = e.Delta > 0 ? .01 : -.01;
            if (pane == 0) template.SplitPrimaryZoom = Math.Max(.85, Math.Min(1.15, template.SplitPrimaryZoom + step));
            else template.SplitSecondaryZoom = Math.Max(.85, Math.Min(1.15, template.SplitSecondaryZoom + step));
            SplitZoomChanged?.Invoke();
            Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e); if (scene == null || e.Button != MouseButtons.Left) return;
            if (SplitLineHit(e.Location, out _)) { draggingSplit = true; Capture = true; ApplySplitDrag(e.Location); return; }
            if (subtitleDragEnabled && (subtitleEditing || SubtitleHit(e.Location))) {
                draggingSubtitle = true; subtitleSelected = true; Capture = true; ApplySubtitleDrag(e.Location); return;
            }
            var hit = scene.Layers.Select(p => p.Layer).LastOrDefault(l => Box(l).Contains(e.Location));
            if (hit == null) {
                subtitleSelected = false; Invalidate(); return;
            }
            subtitleSelected = false;
            selected = hit;
            var box = Box(hit); resizing = e.X >= box.Right - 14 && e.Y >= box.Bottom - 14;
            if (resizing) { var f = Frame; hit.X = (box.X-f.X)/f.Width; hit.Y = (box.Y-f.Y)/f.Height; hit.Width = box.Width/f.Width; hit.Height = box.Height/f.Height; }
            LayerSelected?.Invoke(scene.Layers.FindIndex(p => ReferenceEquals(p.Layer, hit)));
            dragging = true; Capture = true; origin = e.Location; oldX = hit.X; oldY = hit.Y; oldWidth = hit.Width; oldHeight = hit.Height;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            if (draggingSplit) { ApplySplitDrag(e.Location); return; }
            if (template != null && template.SplitLayout != "none") {
                if (SplitLineHit(e.Location, out _)) Cursor = AssemblySplit.IsVertical(template.SplitLayout) ? Cursors.SizeNS : Cursors.SizeWE;
                else if (SplitPaneAt(e.Location) >= 0) Cursor = Cursors.SizeAll;
            }
            if (draggingSubtitle) { ApplySubtitleDrag(e.Location); return; }
            if (!dragging || selected == null) return; var f = Frame; double dx = (e.X - origin.X) / f.Width, dy = (e.Y - origin.Y) / f.Height;
            if (resizing) {
                double factor = Math.Abs(dx) >= Math.Abs(dy) ? 1 + dx / oldWidth : 1 + dy / oldHeight;
                double maximum = Math.Min((1 - selected.X) / oldWidth, (1 - selected.Y) / oldHeight);
                double minimum = Math.Max(.02 / oldWidth, .02 / oldHeight);
                factor = Math.Max(minimum, Math.Min(maximum, factor));
                selected.Width = oldWidth * factor; selected.Height = oldHeight * factor;
            }
            else { selected.X = Math.Max(0, Math.Min(1 - selected.Width, oldX + dx)); selected.Y = Math.Max(0, Math.Min(1 - selected.Height, oldY + dy)); }
            PlacementChanged?.Invoke(); Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; draggingSplit = false; draggingSubtitle = false; Capture = false; }
        protected override void Dispose(bool disposing) { if (disposing) { background?.Dispose(); subtitleOverlay?.Dispose(); foreach (var image in images) image?.Dispose(); } base.Dispose(disposing); }
    }
    public sealed class AssemblyHorizontalStrip : FlowLayoutPanel {
        int offset, maximum, naturalWidth;
        bool layingOut, dragging;
        const int ScrollBarHeight = 8;
        public AssemblyHorizontalStrip() {
            AutoScroll = false; DoubleBuffered = true; TabStop = true; BackColor = Theme.Background;
            FlowDirection = FlowDirection.LeftToRight; WrapContents = false;
        }
        int ScrollReserve => maximum > 0 ? ScrollBarHeight + 10 : 6;
        protected override void OnLayout(LayoutEventArgs e) {
            if (layingOut) return; layingOut = true;
            try {
                base.OnLayout(e);
                naturalWidth = Padding.Horizontal + Controls.Cast<Control>().Sum(c => c.Width + c.Margin.Horizontal);
                int track = Math.Max(1, ClientSize.Width);
                maximum = Math.Max(0, naturalWidth - track);
                offset = Math.Max(0, Math.Min(maximum, offset));
                int innerH = Math.Max(1, ClientSize.Height - ScrollReserve - Padding.Vertical);
                foreach (Control c in Controls) {
                    c.Left -= offset;
                    c.Top = Padding.Top + Math.Max(0, (innerH - c.Height) / 2);
                }
            } finally { layingOut = false; }
            Invalidate();
        }
        void ScrollTo(int value) { offset = Math.Max(0, Math.Min(maximum, value)); PerformLayout(); }
        protected override void OnMouseWheel(MouseEventArgs e) {
            if (maximum > 0) ScrollTo(offset - e.Delta / 120 * 56);
            else base.OnMouseWheel(e);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            if (maximum <= 0 || ClientSize.Width <= 0) return;
            int y = Height - ScrollBarHeight - 4;
            int thumb = Math.Max(36, ClientSize.Width * ClientSize.Width / Math.Max(1, naturalWidth));
            int x = offset * (ClientSize.Width - thumb) / Math.Max(1, maximum);
            using (var pen = new Pen(Theme.Border, 4)) { pen.StartCap = pen.EndCap = LineCap.Round; e.Graphics.DrawLine(pen, 4, y + 4, Width - 4, y + 4); }
            using (var pen = new Pen(Theme.TextMuted, 4)) { pen.StartCap = pen.EndCap = LineCap.Round; e.Graphics.DrawLine(pen, x + 4, y + 4, Math.Max(x + 4, x + thumb - 4), y + 4); }
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y >= Height - ScrollBarHeight - 6 && maximum > 0) { dragging = true; Capture = true; FromMouse(e.X); }
        }
        void FromMouse(int x) {
            int thumb = Math.Max(36, ClientSize.Width * ClientSize.Width / Math.Max(1, naturalWidth));
            ScrollTo((int)((x - thumb / 2.0) / Math.Max(1, ClientSize.Width - thumb) * maximum));
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) FromMouse(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
    }
    public sealed class AssemblyScrollStack : FlowLayoutPanel {
        int offset, maximum, naturalHeight;
        bool layingOut, dragging;
        public AssemblyScrollStack() { AutoScroll = false; DoubleBuffered = true; TabStop = true; BackColor = Theme.Background; }
        protected override void OnLayout(LayoutEventArgs e) {
            if (layingOut) return; layingOut = true;
            try {
                base.OnLayout(e); naturalHeight = Padding.Vertical + Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
                maximum = Math.Max(0, naturalHeight - ClientSize.Height); offset = Math.Max(0, Math.Min(maximum, offset));
                foreach (Control c in Controls) c.Top -= offset;
            } finally { layingOut = false; }
            Invalidate();
        }
        void ScrollTo(int value) { offset = Math.Max(0, Math.Min(maximum, value)); PerformLayout(); }
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(offset - e.Delta / 120 * 70); }
        protected override void OnControlAdded(ControlEventArgs e) {
            base.OnControlAdded(e);
            e.Control.Enter += (s, args) => { if (e.Control.Top < 0) ScrollTo(offset + e.Control.Top - 8); else if (e.Control.Bottom > ClientSize.Height) ScrollTo(offset + e.Control.Bottom - ClientSize.Height + 8); };
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); if (maximum <= 0 || ClientSize.Height <= 0) return;
            int thumb = Math.Max(28, ClientSize.Height * ClientSize.Height / Math.Max(1, naturalHeight));
            int y = offset * (ClientSize.Height - thumb) / Math.Max(1, maximum);
            using (var pen = new Pen(Theme.Border, 5)) { pen.StartCap = pen.EndCap = LineCap.Round; e.Graphics.DrawLine(pen, Width - 7, 4, Width - 7, Height - 4); }
            using (var pen = new Pen(Theme.TextMuted, 5)) { pen.StartCap = pen.EndCap = LineCap.Round; e.Graphics.DrawLine(pen, Width - 7, y + 4, Width - 7, Math.Max(y + 4, y + thumb - 4)); }
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && e.X >= Width - 16 && maximum > 0) { dragging = true; Capture = true; FromMouse(e.Y); } }
        void FromMouse(int y) { int thumb = Math.Max(28, ClientSize.Height * ClientSize.Height / Math.Max(1, naturalHeight)); ScrollTo((int)((y - thumb / 2.0) / Math.Max(1, ClientSize.Height - thumb) * maximum)); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) FromMouse(e.Y); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
        protected override bool IsInputKey(Keys key) { return key == Keys.PageDown || key == Keys.PageUp || base.IsInputKey(key); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.PageDown) { ScrollTo(offset + Height / 2); e.Handled = true; } if (e.KeyCode == Keys.PageUp) { ScrollTo(offset - Height / 2); e.Handled = true; } }
    }
    public sealed class AssemblyStudioButton : Button {
        bool hovered;
        public AssemblyStudioButton(string text, Action action = null, bool accent = false) {
            Text = text; AutoSize = true; MinimumSize = new Size(72, 34); Padding = new Padding(12, 6, 12, 6); Margin = new Padding(0, 0, 8, 0);
            Font = Theme.FontBody; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; BackColor = accent ? Theme.Accent : Theme.Elevated; ForeColor = Theme.TextPrimary; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            if (action != null) Click += (s, e) => action();
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) {
            var background = Parent?.BackColor ?? Theme.Background; if (background.A == 0) background = Theme.Background;
            e.Graphics.Clear(background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = new GraphicsPath()) {
                float w = Width - 1, h = Height - 1, d = Math.Min(16, h);
                path.AddArc(0,0,d,d,180,90); path.AddArc(w-d,0,d,d,270,90); path.AddArc(w-d,h-d,d,d,0,90); path.AddArc(0,h-d,d,d,90,90); path.CloseFigure();
                using (var brush = new SolidBrush(hovered && Enabled ? ControlPaint.Light(BackColor, .08f) : BackColor)) e.Graphics.FillPath(brush, path);
                if (Focused) using (var pen = new Pen(Theme.TextSecondary)) e.Graphics.DrawPath(pen, path);
            }
            var rect = new Rectangle(Padding.Left, Padding.Top, Math.Max(1, Width-Padding.Horizontal), Math.Max(1, Height-Padding.Vertical));
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            flags |= TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, Enabled ? ForeColor : Theme.TextMuted, flags);
        }
    }
    // A keyboard-accessible slider with a visible value, no technical input box.
    public sealed class AssemblySlider : Control {
        readonly NumericUpDown value;
        readonly string caption, unit;
        readonly EventHandler valueChanged, enabledChanged;
        bool dragging;
        public AssemblySlider(string caption, NumericUpDown value, string unit) {
            this.caption = caption; this.value = value; this.unit = unit;
            Name = "Slider"; Height = 66; Margin = new Padding(0, 0, 0, 8); Cursor = Cursors.Hand; TabStop = true; DoubleBuffered = true; BackColor = Theme.Card; ForeColor = Theme.TextPrimary;
            AccessibleName = caption; AccessibleRole = AccessibleRole.Slider;
            valueChanged = (s, e) => { AccessibleDescription = value.Value.ToString("0.##") + " " + unit; Invalidate(); };
            enabledChanged = (s, e) => Enabled = value.Enabled;
            value.ValueChanged += valueChanged; value.EnabledChanged += enabledChanged; Enabled = value.Enabled;
            SetStyle(ControlStyles.Selectable, true);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var text = new SolidBrush(Enabled ? Theme.TextPrimary : Theme.TextMuted)) {
                e.Graphics.DrawString(caption, Font, text, 12, 9);
                var label = value.Value.ToString("0.##") + (unit == "" ? "" : " " + unit);
                using (var format = new StringFormat { Alignment = StringAlignment.Far }) e.Graphics.DrawString(label, Font, text, new RectangleF(Width - 98, 9, 84, 24), format);
            }
            float left = 18, right = Width - 18, top = 46;
            float ratio = (float)((value.Value - value.Minimum) / (value.Maximum - value.Minimum));
            using (var track = new Pen(Theme.Border, 5)) { track.StartCap = track.EndCap = LineCap.Round; e.Graphics.DrawLine(track, left, top, right, top); }
            using (var track = new Pen(Enabled ? Theme.Accent : Theme.TextMuted, 5)) { track.StartCap = track.EndCap = LineCap.Round; e.Graphics.DrawLine(track, left, top, left + (right - left) * ratio, top); }
            using (var brush = new SolidBrush(Enabled ? Theme.TextPrimary : Theme.TextMuted)) e.Graphics.FillEllipse(brush, left + (right - left) * ratio - 7, top - 7, 14, 14);
            if (Focused) using (var pen = new Pen(Theme.Accent)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
        void At(int x) { decimal ratio = (decimal)Math.Max(0, Math.Min(1, (x - 18.0) / Math.Max(1, Width - 36))); value.Value = Math.Round(value.Minimum + ratio * (value.Maximum - value.Minimum), value.DecimalPlaces); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!Enabled || e.Button != MouseButtons.Left) return; Focus(); dragging = true; Capture = true; At(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) At(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e) {
            base.OnKeyDown(e); decimal step = value.Increment;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) { value.Value = Math.Max(value.Minimum, Math.Min(value.Maximum, value.Value + (e.KeyCode == Keys.Left ? -step : step))); e.Handled = true; }
            if (e.KeyCode == Keys.Home) { value.Value = value.Minimum; e.Handled = true; } if (e.KeyCode == Keys.End) { value.Value = value.Maximum; e.Handled = true; }
        }
        protected override void Dispose(bool disposing) { if (disposing) { value.ValueChanged -= valueChanged; value.EnabledChanged -= enabledChanged; } base.Dispose(disposing); }
    }
    public sealed class AssemblyAssetCard : Panel {
        readonly PictureBox thumbnail;
        readonly Label title;
        readonly Button add, folder;
        string currentImage;
        public readonly Label Detail;
        public event Action FolderClick;
        public event Action<string[]> FilesDropped;
        public AssemblyAssetCard(string caption, string action, Action click) {
            Height = 96; BackColor = Theme.Card; Margin = new Padding(0, 0, 0, 6); Padding = new Padding(10); AllowDrop = true;
            thumbnail = new PictureBox { BackColor = Theme.Background, SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(10, 10), Size = new Size(70, 76) }; Controls.Add(thumbnail);
            string glyph = caption.Contains("видео") ? "▶" : caption.Contains("Музыка") ? "♫" : caption.Contains("сохранять") ? "↗" : "▧";
            thumbnail.Paint += (s, e) => { if (thumbnail.Image == null) using (var font = new Font(Theme.FontBody.FontFamily, 22)) TextRenderer.DrawText(e.Graphics, glyph, font, thumbnail.ClientRectangle, Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); };
            title = new Label { Text = caption, Font = Theme.FontCardTitle, ForeColor = Theme.TextPrimary, AutoEllipsis = true, Location = new Point(94, 12), Height = 26 }; Controls.Add(title);
            Detail = new Label { ForeColor = Theme.TextSecondary, AutoEllipsis = true, Location = new Point(94, 36), Height = 20 }; Controls.Add(Detail);
            add = new AssemblyStudioButton(action, click); add.AutoSize = false; add.SetBounds(94, 57, 170, 32); Controls.Add(add);
            folder = Theme.MakeIconButton("⋯", () => FolderClick?.Invoke()); folder.Location = new Point(276, 54); Controls.Add(folder);
            SizeChanged += (s, e) => { title.Width = Detail.Width = Math.Max(80, Width - 106); folder.Left = Width - 48; add.Width = Math.Max(100, Math.Min(170, folder.Left - 102)); };
            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => FilesDropped?.Invoke(PathUtil.NormalizeAll((string[])e.Data.GetData(DataFormats.FileDrop)));
        }
        public void SetInfo(string caption, string detail, string path) {
            title.Text = caption; Detail.Text = detail; AccessibleName = caption + ". " + detail;
            if (path == currentImage) return; currentImage = path; var old = thumbnail.Image; thumbnail.Image = null; old?.Dispose();
            if (string.IsNullOrWhiteSpace(path) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) return;
            try { using (var source = Image.FromFile(path)) { var b = new Bitmap(140, 216); using (var g = Graphics.FromImage(b)) { g.Clear(Theme.Background); double r = Math.Min(140.0 / source.Width, 216.0 / source.Height); int w = Math.Max(1, (int)(source.Width * r)), h = Math.Max(1, (int)(source.Height * r)); g.DrawImage(source, (140 - w) / 2, (216 - h) / 2, w, h); } thumbnail.Image = b; } } catch { }
        }
        protected override void Dispose(bool disposing) { if (disposing) { thumbnail.Image?.Dispose(); thumbnail.Image = null; } base.Dispose(disposing); }
    }
}
