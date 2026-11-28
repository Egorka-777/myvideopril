using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public class CreativeBatchWindow : Form {
        readonly Preferences preferences;
        readonly CreativePreset preset;
        readonly string folder=Path.Combine(Store.Root,"creative-batch"), manifest;
        readonly CreativeBatch batch;
        readonly FlowLayoutPanel cards=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,WrapContents=true,Padding=new Padding(12)};
        readonly FlowLayoutPanel toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(12)};
        readonly Label status=new Label{Dock=DockStyle.Bottom,Height=42,Padding=new Padding(12),AutoEllipsis=true};
        readonly Dictionary<string,Label> labels=new Dictionary<string,Label>();
        CancellationTokenSource operation;
        CreativeBatchItem selectedItem;
        readonly Dictionary<string,Control> cardControls=new Dictionary<string,Control>();
        string Proxy=>WindowsSupport.Unprotect(preferences.ProtectedWarmupHttpProxy);
        string Key=>WindowsSupport.Unprotect(preferences.ProtectedOpenRouterKey);
        public CreativeBatchWindow(Preferences preferences,CreativePreset preset) {
            this.preferences=preferences;this.preset=preset.Clone();manifest=Path.Combine(folder,"batch.xml");Directory.CreateDirectory(folder);batch=CreativeBatch.Load(manifest);
            batch.OutputDirectory=VideoBatchOutputPaths.Creative;
            string musicFile=Path.Combine(folder,"music-preset.xml");if(File.Exists(musicFile)){var music=CreativePreset.Load(musicFile);this.preset.BackgroundMusic=music.BackgroundMusic;this.preset.BackgroundMusicVolume=music.BackgroundMusicVolume;}
            Text="Креативы · пакетная сборка";Font=Theme.FontBody;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;ClientSize=new Size(1100,760);MinimumSize=new Size(850,600);StartPosition=FormStartPosition.CenterParent;
            toolbar.Controls.Add(Button("Добавить видео…",async()=>await AddVideos()));
            toolbar.Controls.Add(Button("Добавить ссылки…",AddLinks));
            toolbar.Controls.Add(Button("Открыть результаты",()=>{Ui.Open(VideoBatchOutputPaths.DailySessionFolder(batch.OutputDirectory));return Task.CompletedTask;}));
            string speedFile=Path.Combine(folder,"speed-preset.xml");if(File.Exists(speedFile))this.preset.SourceSpeed=CreativePreset.Load(speedFile).SourceSpeed;
            toolbar.Controls.Add(Button("Фоновая музыка…",EditMusic));
            string languageFile=Path.Combine(folder,"language-preset.xml");if(File.Exists(languageFile)){var saved=CreativePreset.Load(languageFile);this.preset.AutoIntroLanguage=saved.AutoIntroLanguage;this.preset.TranslateTexts=saved.TranslateTexts;this.preset.TranslationLanguage=saved.TranslationLanguage;}
            toolbar.Controls.Add(Button("Скорость оригинала…",EditSourceSpeed));
            toolbar.Controls.Add(Button("Язык и перевод…",EditLanguage));
            string titlesFile=Path.Combine(folder,"title-preset.xml");if(File.Exists(titlesFile)){var saved=CreativePreset.Load(titlesFile);this.preset.OutputHashtags=saved.OutputHashtags;this.preset.RewriteOutputTitle=saved.RewriteOutputTitle;}
            toolbar.Controls.Add(Button("Заголовки и хэштеги…",EditOutputTitles));
            toolbar.Controls.Add(Button("Шаблон ко всей пачке",ApplyPreset));
            toolbar.Controls.Add(Button("Запустить пачку",RunBatch));
            var stop=Theme.MakeButton("Стоп",action:()=>operation?.Cancel());toolbar.Controls.Add(stop);
            Controls.Add(cards);Controls.Add(toolbar);Controls.Add(status);Rebuild();
            Shown+=async(s,e)=>await Run(RefreshThumbnails);
            FormClosing+=(s,e)=>{if(operation!=null){e.Cancel=true;operation.Cancel();status.Text="Отменяю… Готовые этапы сохраняются.";}else Save();};
            FormClosed+=(s,e)=>DisposeImages();
        }
        Button Button(string text,Func<Task> action) {var button=Theme.MakeButton(text,action:null);button.Click+=async(s,e)=>await Run(action);return button;}
        async Task Run(Func<Task> action) {
            if(operation!=null)return;operation=new CancellationTokenSource();SetEnabled(false);
            try{await action();}
            catch(OperationCanceledException){status.Text="Остановлено. Готовые этапы сохранены.";}
            catch(Exception ex){status.Text="Ошибка: "+ex.Message;Ui.Error(this,ex);}
            finally{operation.Dispose();operation=null;SetEnabled(true);Save();}
        }
        void SetEnabled(bool enabled){cards.Enabled=enabled;foreach(Control control in toolbar.Controls)control.Enabled=enabled||control.Text=="Стоп";}
        void Save()=>batch.Save(manifest);
        void DisposeImages(){foreach(var picture in cards.Controls.Cast<Control>().SelectMany(All).OfType<PictureBox>()){var image=picture.Image;picture.Image=null;image?.Dispose();}}
        static IEnumerable<Control> All(Control root){yield return root;foreach(Control child in root.Controls)foreach(var nested in All(child))yield return nested;}
        void Rebuild(){DisposeImages();foreach(Control control in cards.Controls.Cast<Control>().ToArray())control.Dispose();cards.Controls.Clear();labels.Clear();cardControls.Clear();foreach(var item in batch.Items)AddCard(item);if(selectedItem!=null&&cardControls.ContainsKey(selectedItem.Id))SelectCard(selectedItem);}
        void AddCard(CreativeBatchItem item) {
            var project=CreativeProject.Load(item.ProjectFile);
            var card=new FlowLayoutPanel{Width=320,Height=625,FlowDirection=FlowDirection.TopDown,WrapContents=false,BackColor=Theme.Card,Padding=new Padding(12),Margin=new Padding(8)};
            card.Controls.Add(new Label{Text=!string.IsNullOrWhiteSpace(project.SourceTitle)?project.SourceTitle:string.IsNullOrWhiteSpace(project.Source)?item.SourceUrl:Path.GetFileName(project.Source),Width=292,Height=24,AutoEllipsis=true,ForeColor=Theme.TextPrimary});
            if(!string.IsNullOrWhiteSpace(item.SourceUrl)) {
                var link=new LinkLabel{Text=item.SourceUrl,Width=292,Height=22,AutoEllipsis=true};link.LinkClicked+=(s,e)=>{try{Ui.Open(CreativeLinkImport.ValidateUrl(item.SourceUrl));}catch(Exception ex){Ui.Error(this,ex);}};card.Controls.Add(link);
                var actions=new FlowLayoutPanel{Width=292,Height=38,WrapContents=false};
                var manual=Button("Сфотать комментарии",()=>CaptureComments(item));manual.AutoSize=false;manual.Width=190;manual.Padding=Padding.Empty;manual.Margin=new Padding(0,0,4,0);manual.Font=new Font(Font.FontFamily,9);actions.Controls.Add(manual);
                var auto=Button(File.Exists(project.Source)?"Вставить":"Скачать",()=>File.Exists(project.Source)?PasteScreenshot(item):DownloadLink(item));auto.AutoSize=false;auto.Width=94;auto.Padding=Padding.Empty;auto.Margin=Padding.Empty;auto.Font=manual.Font;actions.Controls.Add(auto);
                card.Controls.Add(actions);
            }
            var preview=new PictureBox{Name="VideoPreview",Width=292,Height=145,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.Black};
            string thumbnail=Path.Combine(Path.GetDirectoryName(item.ProjectFile),"thumbnail.png");LoadImage(preview,thumbnail);card.Controls.Add(preview);
            var screenshot=new PictureBox{Name="CommentScreenshot",Width=292,Height=130,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Theme.Background,AllowDrop=true,Cursor=Cursors.Hand};LoadImage(screenshot,item.Screenshot);
            screenshot.Click+=(s,e)=>SelectCard(item);
            screenshot.DoubleClick+=async(s,e)=>await Run(()=>ChooseScreenshot(item));
            screenshot.DragEnter+=(s,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};
            screenshot.DragDrop+=async(s,e)=>{var files=(string[])e.Data.GetData(DataFormats.FileDrop);if(files.Length!=1){status.Text="Перетащите одну картинку с тремя комментариями.";return;}await Run(()=>SetScreenshot(item,files[0]));};
            card.Controls.Add(screenshot);
            card.Controls.Add(new Label{Text="Нажмите здесь → Ctrl+V. Или перетащите картинку с 3 комментариями",Width=292,Height=34,ForeColor=Theme.TextSecondary});
            var row=new FlowLayoutPanel{Width=292,Height=40,WrapContents=false};
            row.Controls.Add(Button("Комментарии…",()=>ChooseScreenshot(item)));
            row.Controls.Add(Button("Править…",()=>{Edit(item);return Task.CompletedTask;}));
            var outputRow=new FlowLayoutPanel{Width=292,Height=38,WrapContents=false};
            var open=Button("Результат ↗",()=>{if(!File.Exists(item.Output))throw new Exception("Готовый файл пока не создан.");Ui.Open(item.Output);return Task.CompletedTask;});open.Enabled=File.Exists(item.Output);outputRow.Controls.Add(open);
            row.Controls.Add(Button("Убрать",()=>{batch.Items.Remove(item);Save();Rebuild();return Task.CompletedTask;}));foreach(Control button in row.Controls){button.AutoSize=false;button.Width=92;button.Margin=new Padding(0,0,4,0);button.Padding=Padding.Empty;}card.Controls.Add(row);card.Controls.Add(outputRow);
            var label=new Label{Width=292,Height=72,AutoEllipsis=true,Text=ItemText(item),ForeColor=Theme.TextSecondary};labels[item.Id]=label;card.Controls.Add(label);cards.Controls.Add(card);cardControls[item.Id]=card;
            card.Click+=(s,e)=>SelectCard(item);preview.Click+=(s,e)=>SelectCard(item);
            card.DoubleClick+=(s,e)=>{if(!string.IsNullOrWhiteSpace(item.Output)&&File.Exists(item.Output))Ui.Open(item.Output);};
        }
        static string ItemText(CreativeBatchItem item)=>item.Status+(string.IsNullOrWhiteSpace(item.Error)?"":" · "+item.Error);
        static void LoadImage(PictureBox box,string path){var old=box.Image;box.Image=null;old?.Dispose();if(File.Exists(path))using(var image=Image.FromFile(path))box.Image=new Bitmap(image);}
        async Task AddVideos(){using(var picker=new OpenFileDialog{Filter="Видео|*.mp4;*.mov;*.mkv;*.webm;*.avi",Multiselect=true}){
            if(picker.ShowDialog(this)!=DialogResult.OK)return;
            if(!ToolsLocator.TryResolve(out _,out string probe,out string hint))throw new Exception(hint);
            foreach(string source in picker.FileNames){var item=new CreativeBatchItem();string dir=Path.Combine(folder,item.Id);Directory.CreateDirectory(dir);item.ProjectFile=Path.Combine(dir,"project.xml");var project=new CreativeProject{Source=source,Preset=preset.Clone()};for(int i=0;i<3;i++)project.Comments.Add(new CreativeComment());await project.ResolveSourceSizeAsync(probe,operation.Token);project.Save(item.ProjectFile);batch.Items.Add(item);Save();}
            Rebuild();await RefreshThumbnails();}}
        async Task AddLinks() {
            string[] urls;
            using(var dialog=new Form{Text="Ссылки · Instagram, TikTok, YouTube",ClientSize=new Size(650,350),StartPosition=FormStartPosition.CenterParent,Font=Theme.FontBody}) {
                var input=new TextBox{Multiline=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical};
                var info=new Label{Text="Вставьте ссылки на видео, по одной в строке. После скачивания откройте «Сфотать комментарии», затем вставьте скриншот через Ctrl+V.",Dock=DockStyle.Top,Height=45};
                var add=new Button{Text="Добавить",Dock=DockStyle.Bottom,Height=42,DialogResult=DialogResult.OK};dialog.Controls.Add(input);dialog.Controls.Add(info);dialog.Controls.Add(add);
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                urls=input.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(s=>CreativeLinkImport.ValidateUrl(s.Trim())).Distinct().ToArray();
            }
            if(urls.Length==0)return;
            if(!ToolsLocator.TryResolve(out _,out _,out string hint))throw new Exception(hint);
            CreativeLinkImport.Downloader();
            var added=new List<CreativeBatchItem>();
            foreach(string url in urls) {
                var item=new CreativeBatchItem{SourceUrl=url};string dir=Path.Combine(folder,item.Id);Directory.CreateDirectory(dir);item.ProjectFile=Path.Combine(dir,"project.xml");
                var project=new CreativeProject{Preset=preset.Clone()};for(int i=0;i<3;i++)project.Comments.Add(new CreativeComment());project.Save(item.ProjectFile);
                batch.Items.Add(item);added.Add(item);
            }
            Save();Rebuild();
            foreach(var item in added){operation.Token.ThrowIfCancellationRequested();await DownloadLink(item);}
            status.Text="Импорт завершён: видео скачаны "+added.Count(i=>File.Exists(CreativeProject.Load(i.ProjectFile).Source))+" / "+added.Count+". Проверьте статусы и комментарии карточек.";
        }
        void ReportItem(CreativeBatchItem item,string text){item.Status=text;status.Text=text;if(labels.TryGetValue(item.Id,out var label))label.Text=ItemText(item);Save();}
        async Task DownloadLink(CreativeBatchItem item) {
            try {
                if(!ToolsLocator.TryResolve(out string ffmpeg,out string probe,out string hint))throw new Exception(hint);
                item.Error="";ReportItem(item,"Скачивание");
                string source=await CreativeLinkImport.Download(item.SourceUrl,Path.GetDirectoryName(item.ProjectFile),ffmpeg,operation.Token,text=>ReportItem(item,text));
                var project=CreativeProject.Load(item.ProjectFile);project.Source=source;project.SourceUrl=item.SourceUrl;project.Save(item.ProjectFile);
                await CreativeOutputTitle.LoadSourceTitleAsync(project,probe,operation.Token,text=>ReportItem(item,text));
                project.Source=await VideoSdr.EnsureReady(ffmpeg,probe,source,operation.Token);await project.ResolveSourceSizeAsync(probe,operation.Token);project.Save(item.ProjectFile);
                ReportItem(item,"Видео скачано · нужны комментарии");await RefreshThumbnails();
            } catch(OperationCanceledException){ReportItem(item,"Импорт отменён");throw;}
            catch(Exception ex){item.Error=ex.Message;ReportItem(item,"Ошибка скачивания");AppDiagnostics.Write("Creative", "",item.Id,"link_download",ex.Message,"error");Rebuild();}
        }
        Task CaptureComments(CreativeBatchItem item) {
            SelectCard(item);
            using(var capture=new CreativeCommentCapture(item.SourceUrl)) {
                capture.ShowDialog(this);
                status.Text=capture.ScreenshotReady?"Скриншот в буфере. Нажмите Ctrl+V — он добавится в выбранную карточку.":"Выбрана карточка. Сделайте скриншот и вставьте через Ctrl+V.";
            }
            Activate();return Task.CompletedTask;
        }
        void SelectCard(CreativeBatchItem item) {
            selectedItem=item;foreach(var pair in cardControls)pair.Value.BackColor=pair.Key==item.Id?Theme.Elevated:Theme.Card;
            if(cardControls.TryGetValue(item.Id,out var card))cards.ScrollControlIntoView(card);
        }
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
            if(keyData==(Keys.Control|Keys.V)&&operation==null) {
                if(selectedItem==null)status.Text="Сначала выберите карточку для скриншота.";
                else _=Run(()=>PasteScreenshot(selectedItem));return true;
            }
            return base.ProcessCmdKey(ref msg,keyData);
        }
        async Task PasteScreenshot(CreativeBatchItem item) {
            SelectCard(item);
            if(!Clipboard.ContainsImage())throw new Exception("В буфере нет картинки. Сделайте скриншот через Print Screen / Win+Shift+S.");
            string path=Path.Combine(Path.GetDirectoryName(item.ProjectFile),"comments-clipboard-"+Guid.NewGuid().ToString("N")+".png");
            using(var image=Clipboard.GetImage())image.Save(path,System.Drawing.Imaging.ImageFormat.Png);
            await SetScreenshot(item,path);
        }
        async Task RefreshThumbnails(){if(!ToolsLocator.TryResolve(out string ffmpeg,out _,out string hint))throw new Exception(hint);
            foreach(var item in batch.Items){operation?.Token.ThrowIfCancellationRequested();string image=Path.Combine(Path.GetDirectoryName(item.ProjectFile),"thumbnail.png");if(File.Exists(image))continue;
                try{var project=CreativeProject.Load(item.ProjectFile);if(!File.Exists(project.Source))continue;await Core.Tool(ffmpeg,new[]{"-v","error","-nostdin","-y","-i",project.Source,"-frames:v","1","-vf","scale=292:145:force_original_aspect_ratio=decrease","-threads","1",image},operation?.Token??CancellationToken.None,null,60);}
                catch(OperationCanceledException){throw;}catch(Exception ex){item.Error="Превью: "+ex.Message;item.Status="Ошибка";}
            }Rebuild();}
        async Task ChooseScreenshot(CreativeBatchItem item){SelectCard(item);using(var picker=new OpenFileDialog{Filter="Картинка с тремя комментариями|*.png;*.jpg;*.jpeg;*.bmp;*.webp"})if(picker.ShowDialog(this)==DialogResult.OK)await SetScreenshot(item,picker.FileName);}
        async Task SetScreenshot(CreativeBatchItem item,string path){
            var project=CreativeProject.Load(item.ProjectFile);string dir=Path.Combine(Path.GetDirectoryName(item.ProjectFile),"comments-"+Guid.NewGuid().ToString("N"));
            var paths=await SplitComments(path,dir,project.Preset,operation.Token);
            if(!ReviewComments(this,paths))return;
            for(int i=0;i<3;i++){project.Comments[i].ImagePath=paths[i];project.Comments[i].Text="";project.Comments[i].AudioPath="";}
            project.Save(item.ProjectFile);item.Screenshot=path;item.CommentsReviewed=true;item.Output="";item.Status="Готов к запуску";item.Error="";Save();Rebuild();
        }
        async Task<List<string>> SplitComments(string path,string directory,CreativePreset settings,CancellationToken ct){using(var splitter=new CreativeCommentSplit())return await splitter.SplitAsync(path,directory,Key,Proxy,settings.Model,ct,new Progress<string>(text=>status.Text=text));}
        public static bool ReviewComments(IWin32Window owner,List<string> paths){using(var dialog=new Form{Text="Проверьте: три комментария сверху вниз, текст не обрезан",ClientSize=new Size(940,520),StartPosition=FormStartPosition.CenterParent,BackColor=Theme.Background,ForeColor=Theme.TextPrimary}){
            var pictures=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,Padding=new Padding(12)};for(int i=0;i<3;i++){pictures.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.33f));var cell=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};cell.Controls.Add(new Label{Text="Комментарий "+(i+1),AutoSize=true});var picture=new PictureBox{Width=285,Height=390,SizeMode=PictureBoxSizeMode.Zoom};LoadImage(picture,paths[i]);cell.Controls.Add(picture);pictures.Controls.Add(cell,i,0);}
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=50,FlowDirection=FlowDirection.RightToLeft};var ok=Theme.MakeButton("Всё верно",action:()=>dialog.DialogResult=DialogResult.OK);var cancel=Theme.MakeButton("Не применять",action:()=>dialog.DialogResult=DialogResult.Cancel);buttons.Controls.Add(ok);buttons.Controls.Add(cancel);dialog.Controls.Add(pictures);dialog.Controls.Add(buttons);try{return dialog.ShowDialog(owner)==DialogResult.OK;}finally{foreach(var picture in All(dialog).OfType<PictureBox>())picture.Image?.Dispose();}}}
        void Edit(CreativeBatchItem item){
            if(!File.Exists(CreativeProject.Load(item.ProjectFile).Source))throw new Exception("Сначала скачайте видео кнопкой «Скачать».");
            using(var editor=new CreativeWindow(preferences,AssemblyProfile.Test)) {
                editor.OpenBatchProject(item.ProjectFile);
                editor.ShowDialog(this);
            }
            var updated=CreativeProject.Load(item.ProjectFile);
            if(updated.Comments.Count==3&&updated.Comments.All(c=>File.Exists(c.ImagePath)))item.CommentsReviewed=true;
            item.Status="Изменён · готов к запуску";item.Output="";item.Error="";Save();Rebuild();
        }
        Task EditOutputTitles() {
            using(var dialog=new Form{Text="Заголовки и хэштеги · вся пачка",ClientSize=new Size(610,425),StartPosition=FormStartPosition.CenterParent,Font=Theme.FontBody}) {
                var body=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),FlowDirection=FlowDirection.TopDown,WrapContents=false};dialog.Controls.Add(body);
                var rewrite=new CheckBox{Text="Переформулировать исходные заголовки",Checked=preset.RewriteOutputTitle,AutoSize=true};body.Controls.Add(rewrite);
                var tags=new CheckedListBox{Width=550,Height=160,CheckOnClick=true};CreativeOutputTitle.FillHashtags(tags,CreativeOutputTitle.Tags(preset));body.Controls.Add(tags);
                body.Controls.Add(CreativeOutputTitle.HashtagInput(tags));
                body.Controls.Add(new Label{Text="Свои хэштеги — через пробел. Знак # добавится автоматически.",AutoSize=true});
                body.Controls.Add(new Label{Text="Заголовок + пробелы + хэштеги — до 100 символов. Применение не сбрасывает озвучку и готовые видео.",Width=550,Height=45});
                body.Controls.Add(Theme.MakeButton("Сохранить для пачки",action:()=>dialog.DialogResult=DialogResult.OK));
                if(dialog.ShowDialog(this)!=DialogResult.OK)return Task.CompletedTask;
                preset.OutputHashtags=tags.CheckedItems.Cast<string>().ToList();preset.RewriteOutputTitle=rewrite.Checked;CreativeOutputTitle.TitleBudget(preset);
                foreach(var item in batch.Items){var project=CreativeProject.Load(item.ProjectFile);project.Preset.OutputHashtags=new List<string>(preset.OutputHashtags);project.Preset.RewriteOutputTitle=preset.RewriteOutputTitle;project.Save(item.ProjectFile);}
                preset.Save(Path.Combine(folder,"title-preset.xml"));
            }
            return Task.CompletedTask;
        }
        Task EditLanguage() {
            using(var dialog=new Form{Text="Язык и перевод · вся пачка",ClientSize=new Size(610,275),StartPosition=FormStartPosition.CenterParent,Font=Theme.FontBody,BackColor=Theme.Background,ForeColor=Theme.TextPrimary}) {
                var body=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),FlowDirection=FlowDirection.TopDown,WrapContents=false};dialog.Controls.Add(body);
                var auto=new CheckBox{Text="Вступление и комментарии — на языке исходного заголовка",Checked=preset.AutoIntroLanguage,AutoSize=true};body.Controls.Add(auto);
                var translate=new CheckBox{Text="Вместо языка заголовка — заданный язык перевода",Checked=preset.TranslateTexts,AutoSize=true};body.Controls.Add(translate);
                body.Controls.Add(new Label{Text="Язык перевода: en / ru / es / de …",AutoSize=true});
                var target=new TextBox{Text=preset.TranslationLanguage,Width=150};body.Controls.Add(target);
                body.Controls.Add(new Label{Text="Речь оригинала не меняется. Перевод выполняется после подготовки текстов, перед озвучкой. Существующие тексты заменяются переводом.",Width=560,Height=55});
                body.Controls.Add(Theme.MakeButton("Применить ко всей пачке",action:()=>{if(translate.Checked&&!System.Text.RegularExpressions.Regex.IsMatch(target.Text.Trim(),@"\A[a-z]{2,3}\z")){MessageBox.Show(dialog,"Укажите код языка: en, ru, es …");return;}dialog.DialogResult=DialogResult.OK;}));
                if(dialog.ShowDialog(this)!=DialogResult.OK)return Task.CompletedTask;
                preset.AutoIntroLanguage=auto.Checked;preset.TranslateTexts=translate.Checked;preset.TranslationLanguage=target.Text.Trim();
                foreach(var item in batch.Items){var project=CreativeProject.Load(item.ProjectFile);project.Preset.AutoIntroLanguage=auto.Checked;project.Preset.TranslateTexts=translate.Checked;project.Preset.TranslationLanguage=target.Text.Trim();project.IntroAudio="";foreach(var c in project.Comments)c.AudioPath="";project.Save(item.ProjectFile);item.Output="";item.Status="Язык изменён · нужна подготовка";item.Error="";}
                preset.Save(Path.Combine(folder,"language-preset.xml"));Save();Rebuild();
            }
            return Task.CompletedTask;
        }
        Task EditSourceSpeed() {
            using(var dialog=new Form{Text="Скорость оригинала · вся пачка",ClientSize=new Size(590,225),StartPosition=FormStartPosition.CenterParent,Font=Theme.FontBody,BackColor=Theme.Background,ForeColor=Theme.TextPrimary}) {
                var body=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),FlowDirection=FlowDirection.TopDown,WrapContents=false};dialog.Controls.Add(body);
                var value=new NumericUpDown{Minimum=.5m,Maximum=2m,DecimalPlaces=2,Increment=.01m,Value=(decimal)preset.SourceSpeed,Visible=false};body.Controls.Add(value);
                body.Controls.Add(new AssemblySlider("Скорость оригинала",value,"×"){Width=550,Name="CreativeBatchSourceSpeed"});
                body.Controls.Add(new Label{Text="Видео во вступлении и оригинал — с выбранной скоростью. Родной звук синхронный, без изменения тона. Озвучка и музыка — без изменений.",Width=550,Height=55});
                body.Controls.Add(Theme.MakeButton("Применить ко всей пачке",action:()=>dialog.DialogResult=DialogResult.OK));
                if(dialog.ShowDialog(this)!=DialogResult.OK)return Task.CompletedTask;
                preset.SourceSpeed=(double)value.Value;
                foreach(var item in batch.Items) {
                    var project=CreativeProject.Load(item.ProjectFile);project.Preset.SourceSpeed=preset.SourceSpeed;project.Save(item.ProjectFile);
                    item.Output="";item.Status="Скорость изменена · нужна сборка";item.Error="";
                }
                preset.Save(Path.Combine(folder,"speed-preset.xml"));Save();Rebuild();
            }
            return Task.CompletedTask;
        }
        Task EditMusic() {
            using(var dialog=new CreativeMusicDialog(preset)) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return Task.CompletedTask;
                preset.BackgroundMusic=dialog.Paths;preset.BackgroundMusicVolume=dialog.Volume;
                foreach(var item in batch.Items) {
                    var project=CreativeProject.Load(item.ProjectFile);project.Preset.BackgroundMusic=new List<string>(dialog.Paths);project.Preset.BackgroundMusicVolume=dialog.Volume;project.Save(item.ProjectFile);
                    item.Output="";item.Status="Музыка изменена · нужна сборка";item.Error="";
                }
                preset.Save(Path.Combine(folder,"music-preset.xml"));Save();Rebuild();
            }
            return Task.CompletedTask;
        }
        async Task ApplyPreset(){if(batch.Items.Count==0)return;if(MessageBox.Show(this,"Применить текущий шаблон, голос и звук ко всей пачке? Изменённые параметры голоса потребуют новой озвучки.","Общий шаблон",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
            if(!ToolsLocator.TryResolve(out _,out string probe,out string hint))throw new Exception(hint);
            foreach(var item in batch.Items){var project=CreativeProject.Load(item.ProjectFile);bool voiceChanged=project.Preset.Language!=preset.Language||!SameVoice(project.Preset.Speech,preset.Speech);project.Preset=preset.Clone();if(File.Exists(project.Source))await project.ResolveSourceSizeAsync(probe,operation.Token);if(voiceChanged){project.IntroAudio="";foreach(var c in project.Comments)c.AudioPath="";}project.Save(item.ProjectFile);item.Output="";item.Status="Шаблон применён";item.Error="";}Save();Rebuild();}
        static bool SameVoice(AssemblySpeechSettings a,AssemblySpeechSettings b)=>a.VoiceId==b.VoiceId&&a.ModelId==b.ModelId&&a.Speed==b.Speed&&a.Stability==b.Stability&&a.Similarity==b.Similarity&&a.Style==b.Style&&a.SpeakerBoost==b.SpeakerBoost;
        async Task RunBatch(){if(batch.Items.Count==0)throw new Exception("Добавьте видео.");if(string.IsNullOrWhiteSpace(batch.OutputDirectory))throw new Exception("Выберите папку результата.");
            if(batch.Items.Any(i=>!i.CommentsReviewed))throw new Exception("Проверьте три комментария у каждой карточки перед запуском.");
            if(!ToolsLocator.TryResolve(out string ffmpeg,out string probe,out string hint))throw new Exception(hint);
            string eleven=WindowsSupport.Unprotect(preferences.AssemblyElevenLabsKey);if(string.IsNullOrWhiteSpace(eleven))eleven=WindowsSupport.Unprotect(preferences.ProtectedElevenLabsKey);
            await CreativeBatchRunner.RunAsync(batch,ffmpeg,probe,Key,eleven,Proxy,operation.Token,Save,(item,text)=>{status.Text="Карточка "+(batch.Items.IndexOf(item)+1)+" / "+batch.Items.Count+" · "+text;if(labels.TryGetValue(item.Id,out var label))label.Text=ItemText(item);});
            int failed=batch.Items.Count(i=>i.Status=="Ошибка");status.Text="Готово: "+batch.Items.Count(i=>i.Status=="Готово")+" / "+batch.Items.Count+" · ошибок: "+failed+" · "+VideoBatchOutputPaths.DailySessionFolder(batch.OutputDirectory);Rebuild();
        }
    }
}
