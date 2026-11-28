using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    // The creative workspace is stored per studio profile, separately from legacy scene templates.
    public sealed class CreativeWindow : Form {
        readonly Preferences prefs;
        string workspacePath;
        CreativeProject project;
        TextBox source, intro, voiceId, model, language;
        readonly TextBox[] images = new TextBox[3], texts = new TextBox[3];
        readonly Label[] audioLabels = new Label[4];
        ComboBox voices, gender;
        NumericUpDown speed, stability, similarity, expression, sourceSpeed;
        CheckBox speakerBoost, audioLeveling, autoIntroLanguage, translateTexts;
        TextBox translationLanguage;
        CheckedListBox outputHashtags;
        CheckBox rewriteOutputTitle;
        readonly ToolTip tips = new ToolTip();
        TableLayoutPanel settingsBody;
        Panel settingsStorage;
        Label message, sourceSize;
        ProgressBar progress;
        FlowLayoutPanel actions;
        Panel editing;
        CancellationTokenSource operation;
        string currentPath;
        bool binding;
        public bool HasWork => operation != null;
        public void SaveState() { Persist(); }
        public void OpenBatchProject(string path) {
            var loaded=AssemblyFiles.Load<CreativeProject>(path);
            project=loaded; Normalize(); currentPath=Path.GetFullPath(path); workspacePath=Path.GetDirectoryName(currentPath); Bind();
        }
        public CreativeWindow(Preferences preferences, string profile, bool embedded = false) {
            prefs = preferences;
            workspacePath = Path.Combine(Store.Root, "creatives", AssemblyProfile.Normalize(profile));
            Directory.CreateDirectory(workspacePath);
            currentPath = Path.Combine(workspacePath, "project.xml");
            project = File.Exists(currentPath) ? AssemblyFiles.Load<CreativeProject>(currentPath) : new CreativeProject();
            Normalize();
            Text = "Креатив из видео · " + AssemblyProfile.ShortLabel(profile);
            Name = "CreativeWorkspace"; Font = Theme.FontBody; BackColor = Theme.Background; ForeColor = Theme.TextPrimary;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(1050, 800); MinimumSize = new Size(850, 600);
            var area = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(Math.Min(1050, area.Width - 32), Math.Min(800, area.Height - 70));
            if (embedded) { TopLevel=false; FormBorderStyle=FormBorderStyle.None; Dock=DockStyle.Fill; ShowInTaskbar=false; }
            Build(); Bind();
            VisibleChanged += (s,e) => { if (!Visible && !IsDisposed && operation == null) Guard(Persist); };
            FormClosing += (s,e) => {
                if (operation != null) { e.Cancel = true; operation.Cancel(); message.Text = "Отменяю текущую операцию…"; return; }
                try { Persist(); } catch (Exception ex) { e.Cancel = true; Ui.Error(this, ex); }
            };
        }
        void Normalize() {
            if (project.Preset == null) project.Preset = new CreativePreset();
            if (project.Preset.Speech == null) project.Preset.Speech = new AssemblySpeechSettings();
            if (project.Comments == null) project.Comments = new List<CreativeComment>();
            while (project.Comments.Count < 3) project.Comments.Add(new CreativeComment());
            if (project.Comments.Count != 3) throw new Exception("В проекте должны быть три комментария.");
        }
        void Build() {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,74));
            editing = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var body = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Top | AnchorStyles.Left, ColumnCount = 1, Padding = new Padding(0,0,18,0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); editing.Controls.Add(body); root.Controls.Add(editing,0,0);
            editing.SizeChanged += (s,e) => { int width=Math.Max(300,Math.Min(900,editing.ClientSize.Width-24)); body.MinimumSize=new Size(width,0); body.MaximumSize=new Size(width,0); };
            Add(body, new Label { Text = "Креатив из видео", Font = Theme.FontPageTitle, AutoSize = true });
            Add(body, new Label { Text = "Отдельное вступление → Mix → оригинал целиком + реклама → три озвученных комментария", AutoSize = true, ForeColor = Theme.TextSecondary });
            var files = Bar();
            files.Controls.Add(IconButton("\uE8E5", "Открыть проект…", "CreativeOpen", () => Guard(OpenProject)));
            files.Controls.Add(IconButton("\uE74E", "Сохранить проект как…", "CreativeSaveAs", () => Guard(SaveAs)));
            var templates = Theme.MakeContextMenu();
            templates.Items.Add("Открыть шаблон…", null, (s,e) => Guard(LoadPreset));
            templates.Items.Add("Сохранить текущий шаблон…", null, (s,e) => Guard(SavePreset));
            files.Controls.Add(MenuButton("Шаблон", "CreativeTemplates", templates));
            var layout = Theme.MakeContextMenu();
            layout.Items.Add("Кадрирование и расположение…", null, async (s,e) => await Run(EditLayout));
            layout.Items.Add("Добавить слой…", null, (s,e) => Guard(AddLayer));
            layout.Items.Add("Рекламная пауза…", null, async (s,e) => await Run(Ads));
            files.Controls.Add(MenuButton("Оформление", "CreativeLayoutMenu", layout));
            files.Controls.Add(Btn("Музыка…",()=>Guard(()=>{Pull();using(var dialog=new CreativeMusicDialog(project.Preset))if(dialog.ShowDialog(this)==DialogResult.OK){project.Preset.BackgroundMusic=dialog.Paths;project.Preset.BackgroundMusicVolume=dialog.Volume;Persist();}})));
            var effectsButton=Btn("Эффекты…",()=>Guard(EditEffects));effectsButton.Name="CreativeEffects";files.Controls.Add(effectsButton);
            var settings = Btn("Настройки…", () => Guard(EditSettings)); settings.Name="CreativeSettings"; files.Controls.Add(settings);
            var batch = Btn("Пачка…", () => Guard(() => { Persist(); using(var dialog=new CreativeBatchWindow(prefs,project.Preset)) dialog.ShowDialog(this); }));
            batch.Name="CreativeBatch"; files.Controls.Add(batch);
            Add(body, files);
            sourceSize = Hint("Размер кадра — как у исходного видео, без увеличения до Shorts.");
            sourceSize.Name = "CreativeSourceSize"; Add(body,sourceSize);
            source = FileRow(body, "Исходное видео", "Видео|*.mp4;*.mov;*.mkv;*.webm;*.avi", () => {
                project.IntroText = project.IntroAudio = "";
                intro.Text = "";
            }, ResolveSourceSize); source.Name = "CreativeSource";
            Add(body,Heading("Заголовок и хэштеги результата · до 100 символов"));
            rewriteOutputTitle=new CheckBox{Text="Переформулировать исходный заголовок с сохранением смысла",AutoSize=true,Name="CreativeRewriteTitle"};Add(body,rewriteOutputTitle);
            outputHashtags=new CheckedListBox{Width=550,Height=110,CheckOnClick=true,MultiColumn=true,ColumnWidth=240,Name="CreativeOutputHashtags"};
            Add(body,outputHashtags);
            Add(body,CreativeOutputTitle.HashtagInput(outputHashtags,Persist));
            Add(body,Hint("Свои хэштеги — через пробел. Знак # добавится автоматически."));
            Add(body,Hint("Сначала заголовок, затем выбранные хэштеги. Их длина заранее вычитается из лимита 100 символов. Записывается в имя MP4 и метаданные."));
            Add(body, Heading("Вступление"));
            intro = TextArea(62); intro.Name = "CreativeIntroText";
            intro.TextChanged += (s,e) => { if (!binding && project.IntroText != intro.Text) project.IntroAudio = ""; };
            Add(body,intro); audioLabels[0] = Hint(""); Add(body,audioLabels[0]);
            Add(body,Btn("Прослушать вступление",()=>Guard(()=>Listen(project.IntroAudio))));
            Add(body,Hint("Вступление до 3 секунд · ориентир до 44 символов с пробелами. Исходный звук выключен. Маска субтитров — «Добавить слой → Маска размытия»."));
            var importComments=AsyncBtn("Изображение с тремя комментариями…",ImportComments);importComments.Name="CreativeImportComments";Add(body,importComments);
            var comments = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=1, Visible=false, Name="CreativeCommentsDetails" };
            comments.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            var details = Btn("Комментарии / подробности  ▸", null); details.Name="CreativeCommentsToggle";
            details.Click += (s,e) => { comments.Visible=!comments.Visible; details.Text="Комментарии / подробности  " + (comments.Visible ? "▾" : "▸"); };
            Add(body,details); Add(body,comments);
            for (int i=0;i<3;i++) {
                int index=i;
                Add(comments,Heading("Комментарий " + (i+1)));
                images[i] = FileRow(comments,"Скриншот", "Изображение|*.png;*.jpg;*.jpeg;*.webp;*.bmp", () => {
                    project.Comments[index].Text = ""; project.Comments[index].AudioPath = ""; texts[index].Text = "";
                }); images[i].Name = "CreativeCommentImage" + (i+1);
                texts[i] = TextArea(48); texts[i].Name = "CreativeCommentText" + (i+1);
                texts[i].TextChanged += (s,e) => { if (!binding && project.Comments[index].Text != texts[index].Text) project.Comments[index].AudioPath = ""; };
                Add(comments,texts[i]); audioLabels[i+1] = Hint(""); Add(comments,audioLabels[i+1]);
                Add(comments,Btn("Прослушать комментарий " + (i+1),()=>Guard(()=>Listen(project.Comments[index].AudioPath))));
            }
            settingsStorage = new Panel { Visible=false }; Controls.Add(settingsStorage);
            settingsBody = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=1, Name="CreativeSettingsBody" };
            settingsBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); settingsStorage.Controls.Add(settingsBody);
            Add(settingsBody,Heading("Оригинал"));
            sourceSpeed=new NumericUpDown{Minimum=.5m,Maximum=2m,DecimalPlaces=2,Increment=.01m,Value=1,Visible=false,Name="CreativeSourceSpeedValue"};Add(settingsBody,sourceSpeed);
            Add(settingsBody,new AssemblySlider("Скорость оригинала",sourceSpeed,"×"){Width=550,Name="CreativeSourceSpeed"});
            Add(settingsBody,Hint("Видео во вступлении и основной оригинал ускоряются вместе с родным звуком. Тон голоса, озвучка и музыка не меняются."));
            Add(settingsBody,Heading("Голос и нейросеть"));
            var voiceRow = Bar();
            gender = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Width=120 }; gender.Items.AddRange(new object[]{"Мужские","Женские"}); gender.SelectedIndex=0;
            voices = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Width=300, Name="CreativeVoices" };
            voices.SelectedIndexChanged += (s,e) => { var v=voices.SelectedItem as ElevenLabsVoice; if(v!=null) voiceId.Text=v.voice_id; };
            voiceRow.Controls.Add(gender); voiceRow.Controls.Add(voices); voiceRow.Controls.Add(AsyncBtn("Загрузить голоса", LoadVoices)); Add(settingsBody,voiceRow);
            voiceId = Field(settingsBody,"Voice ID ElevenLabs"); voiceId.Name="CreativeVoiceId";
            var sound = Bar();
            speed = Number(sound,"Скорость",.7m,1.2m,.05m,2);
            stability = Number(sound,"Стабильность",0,1,.05m,2);
            similarity = Number(sound,"Сходство",0,1,.05m,2);
            expression = Number(sound,"Стиль",0,1,.05m,2);
            speakerBoost = new CheckBox { Text="Speaker boost",AutoSize=true }; sound.Controls.Add(speakerBoost); Add(settingsBody,sound);
            model = Field(settingsBody,"Модель OpenRouter"); language = Field(settingsBody,"Язык вручную (если автоматический режим выключен)");
            autoIntroLanguage=new CheckBox{Text="Вступление и комментарии — на языке исходного заголовка",AutoSize=true,Name="CreativeAutoIntroLanguage"};Add(settingsBody,autoIntroLanguage);
            translateTexts=new CheckBox{Text="Вместо языка заголовка использовать заданный язык перевода",AutoSize=true,Name="CreativeTranslateTexts"};Add(settingsBody,translateTexts);
            translationLanguage=Field(settingsBody,"Язык перевода (en / ru / es / de …)");translationLanguage.Name="CreativeTranslationLanguage";
            Add(settingsBody,Btn("Перевести подготовленные тексты",()=>{_ = Run(TranslatePrepared);}));
            Add(settingsBody,Hint("Перевод меняет только вступление и тексты комментариев. Речь и изображение оригинала не переводятся."));
            audioLeveling = new CheckBox { Text="Выравнивать громкость", AutoSize=true, Checked=true, Name="CreativeAudioLeveling" }; Add(settingsBody,audioLeveling);
            Add(settingsBody,Hint("Ключи OpenRouter и ElevenLabs, HTTP-прокси — из общих настроек. Анализ отправляет кадры, скриншоты и распознанную речь. Озвучка — четыре платных запроса ElevenLabs, без автоматических повторов."));
            actions = Bar(); actions.Name="CreativeActions";
            var assemble=AsyncBtn("Собрать всё…", All); assemble.Name="CreativeAssemble"; assemble.BackColor=Theme.Accent; assemble.ForeColor=Color.White; actions.Controls.Add(assemble);
            var preview=AsyncBtn("Предпросмотр", ct => Render(ct,true)); preview.Name="CreativePreview"; actions.Controls.Add(preview);
            var advanced = Theme.MakeContextMenu();
            advanced.Items.Add("Распознать и подготовить текст", null, async (s,e) => await Run(Analyze));
            advanced.Items.Add("Подготовить озвучку", null, async (s,e) => await Run(Generate));
            advanced.Items.Add(new ToolStripSeparator());
            advanced.Items.Add("Собрать MP4 из подготовленного", null, async (s,e) => await Run(ct => Render(ct,false)));
            actions.Controls.Add(MenuButton("Дополнительно", "CreativeAdvanced", advanced));
            var stop=Btn("Отмена операции",()=>operation?.Cancel()); stop.Name="CreativeCancel"; stop.Enabled=false; actions.Controls.Add(stop);
            root.Controls.Add(actions,0,1);
            var footer=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,Margin=Padding.Empty};
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute,18)); footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            progress=new ProgressBar{Dock=DockStyle.Fill,Maximum=1000,Visible=false};
            message = new Label { Dock=DockStyle.Fill,Text="Добавьте видео и изображение с тремя комментариями. Для нескольких роликов откройте «Пачка».",ForeColor=Theme.TextSecondary,AutoEllipsis=true,Padding=new Padding(0,6,0,0) };
            footer.Controls.Add(progress,0,0); footer.Controls.Add(message,0,1); root.Controls.Add(footer,0,2); Controls.Add(root);
        }
        async Task ImportComments(CancellationToken ct) {
            Pull();using(var picker=new OpenFileDialog{Filter="Изображение с тремя комментариями|*.png;*.jpg;*.jpeg;*.bmp;*.webp"}) {
                if(picker.ShowDialog(this)!=DialogResult.OK)return;
                List<string> paths;using(var splitter=new CreativeCommentSplit())paths=await splitter.SplitAsync(picker.FileName,Path.Combine(workspacePath,"comments-"+Guid.NewGuid().ToString("N")),WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey),Proxy,project.Preset.Model,ct,new Progress<string>(Report));
                if(!CreativeBatchWindow.ReviewComments(this,paths))return;
                for(int i=0;i<3;i++){project.Comments[i].ImagePath=paths[i];project.Comments[i].Text="";project.Comments[i].AudioPath="";}
                Bind();Persist();message.Text="Три комментария вырезаны и проверены. Можно запускать сборку.";
            }
        }
        void EditEffects(){Pull();Tools(out string ffmpeg,out string probe);var settings=CreativeEffectsDialog.Edit(this,project.Source,project.Preset.Effects??new CreativeEffectsSettings(),ffmpeg,probe);if(settings!=null){project.Preset.Effects=settings;Persist();}}
        void EditSettings() {
            Pull();
            using(var dialog=new Form { Text="Настройки креатива", Name="CreativeSettingsDialog", Font=Theme.FontBody, BackColor=Theme.Background, ForeColor=Theme.TextPrimary, StartPosition=FormStartPosition.CenterParent, ClientSize=new Size(760,520), MinimumSize=new Size(620,420), MinimizeBox=false, MaximizeBox=false }) {
                var root=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(18), ColumnCount=1, RowCount=2 };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var scroll=new Panel { Dock=DockStyle.Fill, AutoScroll=true }; root.Controls.Add(scroll,0,0);
                var close=Btn("Готово", () => dialog.Close()); close.Name="CreativeSettingsDone"; root.Controls.Add(close,0,1); dialog.CancelButton=close;
                scroll.Controls.Add(settingsBody); dialog.Controls.Add(root);
                dialog.FormClosing += (s,e) => { if(operation!=null) { e.Cancel=true; operation.Cancel(); message.Text="Отменяю текущую операцию…"; } };
                try { dialog.ShowDialog(this); }
                finally { settingsStorage.Controls.Add(settingsBody); }
            }
            Persist(); RefreshAudioLabels();
        }
        Button IconButton(string glyph,string caption,string name,Action action) {
            var button=Btn(glyph,action); button.Name=name; button.AccessibleName=caption;
            var font=new Font("Segoe MDL2 Assets",14f); button.Font=font; button.AutoSize=false; button.MinimumSize=new Size(36,36); button.Size=new Size(36,36); button.Padding=Padding.Empty;
            tips.SetToolTip(button,caption); button.Disposed+=(s,e)=>font.Dispose(); return button;
        }
        Button MenuButton(string text,string name,ContextMenuStrip menu) {
            var button=Btn(text+"  ▾",null); button.Name=name;
            button.Click+=(s,e)=>menu.Show(button,new Point(0,button.Height)); button.Disposed+=(s,e)=>menu.Dispose(); return button;
        }
        protected override void Dispose(bool disposing) {
            if(disposing) tips.Dispose();
            base.Dispose(disposing);
        }
        async Task ResolveSourceSize(CancellationToken ct) {
            Pull();
            if(string.IsNullOrWhiteSpace(project.Source))return;
            Tools(out _,out string probe);
            var size=await project.ResolveSourceSizeAsync(probe,ct);
            sourceSize.Text=(project.Preset.PreserveSourceSize?"Исходный размер · ":"Размер из шаблона · ")+size.Width+" × "+size.Height;
        }
        void Bind() {
            binding=true;
            try {
                sourceSize.Text=project.Preset.PreserveSourceSize?"Размер кадра — как у исходного видео, без увеличения до Shorts.":"Размер из шаблона · "+project.Preset.Width+" × "+project.Preset.Height;
                source.Text=project.Source; intro.Text=project.IntroText;
                rewriteOutputTitle.Checked=project.Preset.RewriteOutputTitle;
                CreativeOutputTitle.FillHashtags(outputHashtags,project.Preset.OutputHashtags);
                for(int i=0;i<3;i++){ images[i].Text=project.Comments[i].ImagePath; texts[i].Text=project.Comments[i].Text; }
                voiceId.Text=project.Preset.Speech.VoiceId; model.Text=project.Preset.Model; language.Text=project.Preset.Language;
                speed.Value=Clamp(project.Preset.Speech.Speed,speed); stability.Value=Clamp(project.Preset.Speech.Stability,stability);
                similarity.Value=Clamp(project.Preset.Speech.Similarity,similarity); expression.Value=Clamp(project.Preset.Speech.Style,expression);
                speakerBoost.Checked=project.Preset.Speech.SpeakerBoost;
                audioLeveling.Checked=project.Preset.AudioLevelingEnabled;
                sourceSpeed.Value=Clamp(project.Preset.SourceSpeed,sourceSpeed);
                autoIntroLanguage.Checked=project.Preset.AutoIntroLanguage;translateTexts.Checked=project.Preset.TranslateTexts;translationLanguage.Text=project.Preset.TranslationLanguage;
                RefreshAudioLabels();
            } finally { binding=false; }
        }
        static decimal Clamp(double value,NumericUpDown n) => Math.Min(n.Maximum,Math.Max(n.Minimum,(decimal)value));
        void Pull() {
            if(binding)return;
            string nextSource=source.Text.Trim();
            if(project.Source!=nextSource){project.IntroAudio="";project.SourceTitle="";project.SourceUrl="";project.OutputTitle="";project.OutputTitleKey="";}
            project.Source=nextSource;
            if(project.IntroText!=intro.Text.Trim()) project.IntroAudio="";
            project.IntroText=intro.Text.Trim();
            for(int i=0;i<3;i++) {
                var c=project.Comments[i];
                if(c.Text!=texts[i].Text.Trim() || c.ImagePath!=images[i].Text.Trim())c.AudioPath="";
                c.ImagePath=images[i].Text.Trim();c.Text=texts[i].Text.Trim();
            }
            var speech=project.Preset.Speech;
            bool voiceChanged=speech.VoiceId!=voiceId.Text.Trim() || speech.Speed!=(double)speed.Value || speech.Stability!=(double)stability.Value || speech.Similarity!=(double)similarity.Value || speech.Style!=(double)expression.Value || speech.SpeakerBoost!=speakerBoost.Checked || project.Preset.Language!=language.Text.Trim();
            if(voiceChanged){project.IntroAudio="";foreach(var c in project.Comments)c.AudioPath="";}
            speech.VoiceId=voiceId.Text.Trim(); speech.Speed=(double)speed.Value; speech.Stability=(double)stability.Value;
            speech.Similarity=(double)similarity.Value; speech.Style=(double)expression.Value; speech.SpeakerBoost=speakerBoost.Checked;
            project.Preset.Model=model.Text.Trim();project.Preset.Language=language.Text.Trim();
            project.Preset.AudioLevelingEnabled=audioLeveling.Checked;
            project.Preset.SourceSpeed=(double)sourceSpeed.Value;
            project.Preset.RewriteOutputTitle=rewriteOutputTitle.Checked;project.Preset.OutputHashtags=outputHashtags.CheckedItems.Cast<string>().ToList();
            string target=translationLanguage.Text.Trim().ToLowerInvariant();
            if(project.Preset.AutoIntroLanguage!=autoIntroLanguage.Checked){project.IntroAudio="";foreach(var c in project.Comments)c.AudioPath="";}
            if(project.Preset.TranslateTexts!=translateTexts.Checked||project.Preset.TranslationLanguage!=target){project.IntroAudio="";foreach(var c in project.Comments)c.AudioPath="";}
            project.Preset.AutoIntroLanguage=autoIntroLanguage.Checked;project.Preset.TranslateTexts=translateTexts.Checked;project.Preset.TranslationLanguage=target;
        }
        void Listen(string path){Pull();if(string.IsNullOrWhiteSpace(path)||!File.Exists(path)||path!=project.IntroAudio&&!project.Comments.Any(c=>c.AudioPath==path))throw new Exception("Сначала создайте озвучку для текущего текста и голоса.");Ui.Open(path);}
        void Persist(){Pull();AssemblyFiles.Save(currentPath,project);}
        void RefreshAudioLabels(){
            audioLabels[0].Text=AudioLabel(project.IntroAudio);
            for(int i=0;i<3;i++)audioLabels[i+1].Text=AudioLabel(project.Comments[i].AudioPath);
        }
        static string AudioLabel(string path)=>string.IsNullOrWhiteSpace(path)?"Озвучка ещё не создана или текст/голос изменён.":"Озвучка: "+Path.GetFileName(path);
        string Proxy=>WindowsSupport.Unprotect(prefs.ProtectedWarmupHttpProxy);
        string ElevenKey{get{string k=WindowsSupport.Unprotect(prefs.AssemblyElevenLabsKey);return string.IsNullOrWhiteSpace(k)?WindowsSupport.Unprotect(prefs.ProtectedElevenLabsKey):k;}}
        void Tools(out string ffmpeg,out string probe){if(!ToolsLocator.TryResolve(out ffmpeg,out probe,out string hint))throw new Exception(hint);}
        async Task LoadVoices(CancellationToken ct){
            using(var client=new ElevenLabsClient(ElevenKey,Proxy)){
                var list=await client.ListVoicesAsync(gender.SelectedIndex==1?"female":"male",ct);
                voices.Items.Clear();foreach(var v in list)voices.Items.Add(v);
                voices.SelectedItem=list.FirstOrDefault(v=>v.voice_id==voiceId.Text);
                message.Text="Получено голосов: "+list.Count+". Выберите голос или введите Voice ID.";
            }
        }
        async Task Analyze(CancellationToken ct){
            Pull();Tools(out string ffmpeg,out string probe);
            bool replaceIntro=project.IntroText.Length>CreativeAnalysisClient.MaxIntroCharacters;
            if(!string.IsNullOrWhiteSpace(project.IntroAudio)&&File.Exists(project.IntroAudio))
                replaceIntro |= Core.AudioDuration(await Core.Probe(probe,project.IntroAudio,ct))>3;
            await CreativePreparation.AnalyzeAsync(project,ffmpeg,probe,workspacePath,WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey),Proxy,ct,Report,replaceIntro);
            Bind();Persist(); message.Text="Тексты подготовлены. Можно исправить их перед озвучкой.";
        }
        async Task TranslatePrepared(CancellationToken ct){
            Pull();Tools(out string ffmpeg,out string probe);
            if(!project.Preset.AutoIntroLanguage){project.Preset.TranslateTexts=true;translateTexts.Checked=true;}
            await CreativePreparation.PrepareLanguageAsync(project,ffmpeg,probe,workspacePath,ct,Report,WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey),Proxy);
            await CreativePreparation.TranslateTextsAsync(project,WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey),Proxy,ct,Report);Bind();Persist();
        }
        async Task Generate(CancellationToken ct){
            Pull();Tools(out string ffmpeg,out string probe);
            bool longIntro=project.IntroText.Length>CreativeAnalysisClient.MaxIntroCharacters;
            if(!string.IsNullOrWhiteSpace(project.IntroAudio)&&File.Exists(project.IntroAudio))longIntro |= Core.AudioDuration(await Core.Probe(probe,project.IntroAudio,ct))>3;
            if(longIntro){Report("Заменяю длинное вступление коротким; готовые комментарии сохраняются…");await Analyze(ct);}
            await CreativePreparation.PrepareLanguageAsync(project,ffmpeg,probe,workspacePath,ct,Report,WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey),Proxy);
            await CreativePreparation.TranslateTextsAsync(project,WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey),Proxy,ct,Report);
            Bind();Persist();
            await CreativePreparation.GenerateSpeechAsync(project,workspacePath,ElevenKey,Proxy,ct,Report);
            RefreshAudioLabels();Persist();message.Text="Все четыре озвучки готовы. Уже созданные файлы использованы без повторной генерации.";
        }
        async Task EditMediaLayers(CancellationToken ct){await ResolveSourceSize(ct);var layers=CreativeMediaDialog.Edit(this,project,prefs);if(layers!=null){project.Preset.MediaLayers=layers;Persist();}}
        Task EditLayout(CancellationToken ct){return OpenLayout(5,false,ct);}
        void AddLayer(){
            var menu=Theme.MakeContextMenu();
            menu.Items.Add("Видео / изображение / пачка…",null,async(s,e)=>await Run(EditMediaLayers));
            menu.Items.Add("Маска размытия",null,async(s,e)=>await Run(ct=>OpenLayout(0,true,ct)));
            menu.Items.Add("Закрывающее изображение",null,async(s,e)=>await Run(ct=>OpenLayout(1,false,ct)));
            menu.Items.Add("Баннер",null,async(s,e)=>await Run(ct=>OpenLayout(4,false,ct)));
            menu.Show(Cursor.Position);
        }
        async Task OpenLayout(int target,bool addBlur,CancellationToken ct){await ResolveSourceSize(ct);Tools(out string ffmpeg,out string probe);using(var dialog=new CreativeLayoutDialog(project,ffmpeg,probe,target,addBlur))if(dialog.ShowDialog(this)==DialogResult.OK){Bind();Persist();}}
        async Task Ads(CancellationToken ct){
            Pull(); Tools(out string ffmpeg,out string probe);
            CreativeProject.RequireFile(project.Source,"Исходное видео");
            string frame=Path.Combine(Path.GetTempPath(),"creative-ad-background-"+Guid.NewGuid().ToString("N")+".png");
            try {
                var media=Core.VideoInfo(await Core.Probe(probe,project.Source,ct));
                project.Preset.ApplySourceSize(media);
                string filter=CreativeGeometry.SourceFilter(project.Preset,new Size(media.Width,media.Height));
                await Core.Tool(ffmpeg,new[]{"-v","error","-nostdin","-y","-i",project.Source,"-map","0:v:0","-frames:v","1","-vf",filter+",scale=540:540:force_original_aspect_ratio=decrease","-threads","1",frame},ct,null,60);
                ct.ThrowIfCancellationRequested();
                using(var background=new Bitmap(frame)){
                    var result=CreativeAdsDialog.Edit(this,project.Preset.Insertions,project.Preset.Width,project.Preset.Height,()=>new Bitmap(background));
                    if(result!=null){project.Preset.Insertions=result;Persist();}
                }
            } finally { if(File.Exists(frame))File.Delete(frame); }
        }
        async Task Render(CancellationToken ct,bool preview){
            Pull();string output;
            if(preview){Directory.CreateDirectory(Path.Combine(workspacePath,"preview"));output=Path.Combine(workspacePath,"preview","preview-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".mp4");}
            else {output=OutputPath();if(output==null)return;}
            output=await RenderTo(output,ct);
            if(preview)Play(output,ct);else message.Text="Готово: "+output;
        }
        string OutputPath(){using(var picker=new SaveFileDialog{Filter="Видео MP4|*.mp4",FileName="creative-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".mp4",InitialDirectory=VideoBatchOutputPaths.DailySessionFolder(VideoBatchOutputPaths.Creative)})return picker.ShowDialog(this)==DialogResult.OK?picker.FileName:null;}
        async Task<string> RenderTo(string output,CancellationToken ct){
            Tools(out string ffmpeg,out string probe);Persist();
            await CreativeOutputTitle.PrepareAsync(project,ffmpeg,probe,WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey),Proxy,ct,Report);Persist();
            if(!output.Contains(Path.DirectorySeparatorChar+"preview"+Path.DirectorySeparatorChar))output=CreativeOutputTitle.OutputPath(Path.GetDirectoryName(output),project);
            await CreativeRender.RenderAsync(project,ffmpeg,probe,output,ct,(text,percent)=>Report(text,percent));
            ct.ThrowIfCancellationRequested();return output;
        }
        async Task All(CancellationToken ct){
            string output=OutputPath();if(output==null)return;Pull();Tools(out _,out string probe);
            bool needsText=string.IsNullOrWhiteSpace(project.IntroText)||project.IntroText.Length>CreativeAnalysisClient.MaxIntroCharacters||project.Comments.Any(c=>string.IsNullOrWhiteSpace(c.Text));
            if(!string.IsNullOrWhiteSpace(project.IntroAudio)&&File.Exists(project.IntroAudio))needsText |= Core.AudioDuration(await Core.Probe(probe,project.IntroAudio,ct))>3;
            if(needsText)await Analyze(ct);
            await Generate(ct);output=await RenderTo(output,ct);message.Text="Готово: "+output;
        }
        void Play(string path,CancellationToken ct){
            using(var player=new Form{Text="Предпросмотр · итоговый рендер",ClientSize=new Size(850,650),StartPosition=FormStartPosition.CenterParent,BackColor=Theme.Background})
            using(var web=new Microsoft.Web.WebView2.WinForms.WebView2{Dock=DockStyle.Fill}){
                player.Controls.Add(web);
                player.Shown+=async(s,e)=>{try{await web.EnsureCoreWebView2Async();if(web.IsDisposed)return;web.CoreWebView2.SetVirtualHostNameToFolderMapping("creative-preview.local",Path.GetDirectoryName(path),Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);web.NavigateToString("<html><body style='margin:0;background:#111'><video controls autoplay style='width:100%;height:100vh' src='https://creative-preview.local/"+Uri.EscapeDataString(Path.GetFileName(path))+"'></video></body></html>");}catch(Exception ex){Ui.Error(player,ex);}};
                ct.ThrowIfCancellationRequested();player.ShowDialog(this);
            }
        }
        void OpenProject(){using(var p=new OpenFileDialog{Filter="Проект креатива XML|*.xml"})if(p.ShowDialog(this)==DialogResult.OK){Persist();var loaded=AssemblyFiles.Load<CreativeProject>(p.FileName);project=loaded;Normalize();currentPath=p.FileName;workspacePath=Path.GetDirectoryName(currentPath);Bind();message.Text="Открыт проект: "+currentPath;}}
        void SaveAs(){Pull();using(var p=new SaveFileDialog{Filter="Проект креатива XML|*.xml",FileName="creative-project.xml"})if(p.ShowDialog(this)==DialogResult.OK){AssemblyFiles.Save(p.FileName,project);currentPath=p.FileName;}}
        void LoadPreset(){using(var p=new OpenFileDialog{Filter="Оформление креатива XML|*.xml"})if(p.ShowDialog(this)==DialogResult.OK){Pull();project.Preset=AssemblyFiles.Load<CreativePreset>(p.FileName);Normalize();project.IntroAudio="";foreach(var c in project.Comments)c.AudioPath="";Bind();Persist();}}
        void SavePreset(){Pull();using(var p=new SaveFileDialog{Filter="Оформление креатива XML|*.xml",FileName="creative-preset.xml"})if(p.ShowDialog(this)==DialogResult.OK)AssemblyFiles.Save(p.FileName,project.Preset);}
        void Report(string text)=>Report(text,-1);
        void Report(string text,double percent){
            if(IsDisposed)return;
            if(InvokeRequired){BeginInvoke((Action)(()=>Report(text,percent)));return;}
            message.Text=text;
            if(percent>=0){progress.Visible=true;progress.Value=(int)Math.Max(0,Math.Min(1000,percent*10));}
        }
        async Task Run(Func<CancellationToken,Task> action){
            if(operation!=null)return;
            operation=new CancellationTokenSource();editing.Enabled=false;settingsBody.Enabled=false;progress.Visible=true;progress.Value=0;
            foreach(Control c in actions.Controls)c.Enabled=c.Name=="CreativeCancel";
            try{await action(operation.Token);}
            catch(OperationCanceledException){message.Text="Операция отменена. Готовность ролика не подтверждена.";}
            catch(Exception ex){message.Text="Ошибка: "+ex.Message;Ui.Error(this,ex);}
            finally{
                operation.Dispose();operation=null;editing.Enabled=true;settingsBody.Enabled=true;foreach(Control c in actions.Controls)c.Enabled=c.Name!="CreativeCancel";
                progress.Value=0;progress.Visible=false;
                RefreshAudioLabels();
                try { Persist(); } catch (Exception ex) { message.Text="Не удалось сохранить проект: "+ex.Message; Ui.Error(this,ex); }
            }
        }
        void Guard(Action action){try{action();}catch(Exception ex){message.Text=ex.Message;Ui.Error(this,ex);}}
        Button AsyncBtn(string text,Func<CancellationToken,Task> action){var b=Btn(text,null);b.Click+=async(s,e)=>await Run(action);return b;}
        static Button Btn(string text,Action action)=>Theme.MakeButton(text,action:action);
        static FlowLayoutPanel Bar()=>new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Margin=new Padding(0,6,0,6)};
        static Label Heading(string text)=>new Label{Text=text,Font=Theme.FontCardTitle,AutoSize=true,Margin=new Padding(0,14,0,4)};
        static Label Hint(string text)=>new Label{Text=text,AutoSize=true,MaximumSize=new Size(900,0),ForeColor=Theme.TextSecondary,Margin=new Padding(0,3,0,5)};
        static TextBox TextArea(int height)=>new TextBox{Dock=DockStyle.Top,Multiline=true,Height=height,ScrollBars=ScrollBars.Vertical,BackColor=Theme.Card,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.FixedSingle};
        static void Add(TableLayoutPanel body,Control c){int i=body.RowCount++;body.RowStyles.Add(new RowStyle(SizeType.AutoSize));body.Controls.Add(c,0,i);}
        static TextBox Field(TableLayoutPanel body,string label){Add(body,Hint(label));var t=new TextBox{Dock=DockStyle.Top,BackColor=Theme.Card,ForeColor=Theme.TextPrimary};Add(body,t);return t;}
        TextBox FileRow(TableLayoutPanel body,string label,string filter,Action changed,Func<CancellationToken,Task> afterChanged=null){
            Add(body,Hint(label));var row=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2};row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text=new TextBox{Dock=DockStyle.Fill,ReadOnly=true,BackColor=Theme.Card,ForeColor=Theme.TextPrimary};
            var choose=Btn("Выбрать…",null);
            choose.Click+=async(s,e)=>{
                bool selected=false;
                Guard(()=>{using(var picker=new OpenFileDialog{Filter=filter})if(picker.ShowDialog(this)==DialogResult.OK){text.Text=picker.FileName;changed();RefreshAudioLabels();selected=true;}});
                if(selected&&afterChanged!=null)await Run(afterChanged);
            };
            row.Controls.Add(text,0,0);row.Controls.Add(choose,1,0);Add(body,row);return text;
        }
        static NumericUpDown Number(FlowLayoutPanel row,string caption,decimal min,decimal max,decimal increment,int decimals){row.Controls.Add(new Label{Text=caption,AutoSize=true,Padding=new Padding(0,5,0,0)});var n=new NumericUpDown{Minimum=min,Maximum=max,Increment=increment,DecimalPlaces=decimals,Width=65};row.Controls.Add(n);return n;}
    }
}
