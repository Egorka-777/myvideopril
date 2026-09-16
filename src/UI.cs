using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace VideoBatch {
    public static class Ui {
        public static readonly Color Bg=Color.FromArgb(243,245,249),Ink=Color.FromArgb(27,39,58),Muted=Color.FromArgb(103,117,139),Blue=Color.FromArgb(52,92,234);
        public static Button Button(string text,Action action=null,bool primary=false){var b=new Button{Text=text,AutoSize=true,MinimumSize=new Size(80,36),Padding=new Padding(10,4,10,4),FlatStyle=FlatStyle.Flat,BackColor=primary?Blue:Color.FromArgb(228,235,246),ForeColor=primary?Color.White:Ink,Cursor=Cursors.Hand,Margin=new Padding(0,0,8,0)};b.FlatAppearance.BorderSize=0;if(action!=null)b.Click+=(s,e)=>action();return b;}
        public static Label Label(string text,bool muted=false){return new Label{Text=text,AutoSize=true,ForeColor=muted?Muted:Ink,Margin=new Padding(0,0,0,8),MaximumSize=new Size(540,0)};}
        public static ComboBox Combo(string[] items,string selected){var c=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=190,Anchor=AnchorStyles.Left|AnchorStyles.Right};c.Items.AddRange(items);c.SelectedItem=selected;if(c.SelectedIndex<0&&c.Items.Count>0)c.SelectedIndex=0;return c;}
        public static NumericUpDown Number(double value,double min,double max,int decimals=2){var n=new NumericUpDown{Minimum=(decimal)min,Maximum=(decimal)max,DecimalPlaces=decimals,Increment=decimals==0?1:(decimal).1,Width=130,ThousandsSeparator=false,Anchor=AnchorStyles.Left|AnchorStyles.Right};n.Value=Math.Min(n.Maximum,Math.Max(n.Minimum,(decimal)value));return n;}
        public static TableLayoutPanel Table(int cols=1){var t=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=cols,Padding=new Padding(0),AutoSize=false,BackColor=Color.Transparent};for(int i=0;i<cols;i++)t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/cols));return t;}
        public static void Row(TableLayoutPanel t,string label,Control c){int r=t.RowCount++;t.RowStyles.Add(new RowStyle(SizeType.AutoSize));var l=Label(label);l.Anchor=AnchorStyles.Left;l.Margin=new Padding(0,10,12,10);c.Margin=new Padding(0,7,0,7);t.Controls.Add(l,0,r);t.Controls.Add(c,1,r);}
        public static TableLayoutPanel Fields(){var t=Table(2);t.Dock=DockStyle.Top;t.AutoSize=true;t.ColumnStyles[0].Width=55;t.ColumnStyles[1].Width=45;return t;}
        public static FlowLayoutPanel Flow(){return new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=false,FlowDirection=FlowDirection.LeftToRight,Margin=new Padding(0),Padding=new Padding(0)};}
        public static void Error(IWin32Window owner,Exception e){MessageBox.Show(owner,e.Message,"VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        public static string File(IWin32Window owner,string filter){using(var d=new OpenFileDialog{Filter=filter})return d.ShowDialog(owner)==DialogResult.OK?d.FileName:null;}
        public static string Folder(IWin32Window owner,string current){using(var d=new FolderBrowserDialog{SelectedPath=current,Description="Выберите папку"})return d.ShowDialog(owner)==DialogResult.OK?d.SelectedPath:null;}
        public static void Open(string path){Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    }
    public class Dialog:Form {
        protected TableLayoutPanel Root;protected Panel Body;protected new FlowLayoutPanel Bottom;
        public Dialog(string title,int height=570){Text=title;ClientSize=new Size(610,height);MinimumSize=new Size(580,420);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);BackColor=Ui.Bg;ForeColor=Ui.Ink;AutoScaleMode=AutoScaleMode.Dpi;MinimizeBox=false;MaximizeBox=false;ShowInTaskbar=false;
            Root=Ui.Table();Root.Padding=new Padding(22);Root.RowCount=2;Root.RowStyles.Add(new RowStyle(SizeType.Percent,100));Root.RowStyles.Add(new RowStyle(SizeType.AutoSize));Body=new Panel{Dock=DockStyle.Fill,AutoScroll=true};Bottom=Ui.Flow();Bottom.FlowDirection=FlowDirection.RightToLeft;Bottom.Padding=new Padding(0,16,0,0);Root.Controls.Add(Body,0,0);Root.Controls.Add(Bottom,0,1);Controls.Add(Root);
        }
        protected void StandardButtons(Action save){var ok=Ui.Button("Сохранить",()=>{try{save();DialogResult=DialogResult.OK;Close();}catch(Exception e){Ui.Error(this,e);}},true);var cancel=Ui.Button("Отмена",()=>{DialogResult=DialogResult.Cancel;Close();});Bottom.Controls.Add(ok);Bottom.Controls.Add(cancel);AcceptButton=ok;CancelButton=cancel;}
    }
    public class MusicDialog:Dialog {
        public List<string> Paths;public double VolumeDb;public string Market="RU";
        NumericUpDown volume;ListBox list;Button ru,en;Label hint;bool background;
        List<string> pathsRu,pathsEn;
        public MusicDialog(List<string> ruPaths,List<string> enPaths,string market,bool background=false,double db=-7.5):base(background?"Фоновая музыка":"Музыка",560){
            this.background=background;VolumeDb=db;Market=(market??"RU").ToUpperInvariant()=="EN"?"EN":"RU";
            pathsRu=new List<string>(ruPaths??new List<string>());pathsEn=new List<string>(enPaths??new List<string>());
            ClientSize=new Size(610,560);
            var table=Ui.Table();table.RowCount=4;table.RowStyles.Add(new RowStyle(SizeType.AutoSize));table.RowStyles.Add(new RowStyle(SizeType.Absolute,44));table.RowStyles.Add(new RowStyle(SizeType.AutoSize));table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var note=Ui.Label(background
                ?"Разные треки для каждого варианта. Речь сохраняется.\n\nМузыка разделена по рынкам: русские и English — разные списки (в разных странах часть треков запрещена)."
                :"Добавьте треки для выбранного рынка. Короткие видео: музыка заменяет исходный звук.\n\nРусские и English списки раздельные.",true);
            table.Controls.Add(note,0,0);
            var marketBar=Ui.Flow();marketBar.Dock=DockStyle.Fill;marketBar.WrapContents=false;
            marketBar.Controls.Add(new Label{Text="Рынок музыки:",AutoSize=true,Margin=new Padding(0,8,10,0),ForeColor=Ui.Muted});
            ru=Ui.Button("Русские",()=>SwitchMarket("RU"));en=Ui.Button("English",()=>SwitchMarket("EN"));
            hint=new Label{AutoSize=true,Margin=new Padding(12,8,0,0),ForeColor=Ui.Muted};
            marketBar.Controls.Add(ru);marketBar.Controls.Add(en);marketBar.Controls.Add(hint);table.Controls.Add(marketBar,0,1);
            var fields=Ui.Fields();volume=Ui.Number(db,-60,-.1,1);Ui.Row(fields,"Громкость фона, дБ",volume);fields.Visible=background;table.Controls.Add(fields,0,2);
            list=new ListBox{Dock=DockStyle.Fill,SelectionMode=SelectionMode.MultiExtended,HorizontalScrollbar=true,BorderStyle=BorderStyle.None};
            table.Controls.Add(list,0,3);Body.Controls.Add(table);
            LoadMarketIntoList();
            var done=Ui.Button("Готово",()=>{SaveCurrentMarketFromList();Paths=new List<string>(Market=="EN"?pathsEn:pathsRu);VolumeDb=(double)volume.Value;DialogResult=DialogResult.OK;Close();},true);
            Bottom.Controls.Add(done);
            Bottom.Controls.Add(Ui.Button("Убрать",()=>{foreach(var p in list.SelectedItems.Cast<object>().ToArray())list.Items.Remove(p);UpdateHint();}));
            Bottom.Controls.Add(Ui.Button("+ Добавить",()=>{using(var d=new OpenFileDialog{Multiselect=true,Filter="Аудио|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.wma"})if(d.ShowDialog(this)==DialogResult.OK)foreach(var p in d.FileNames)if(!list.Items.Contains(p))list.Items.Add(p);UpdateHint();}));
            AcceptButton=done;RefreshMarketButtons();
        }
        public List<string> PathsRu{get{return pathsRu;}}
        public List<string> PathsEn{get{return pathsEn;}}
        void SwitchMarket(string next){
            next=next=="EN"?"EN":"RU";if(next==Market)return;
            SaveCurrentMarketFromList();Market=next;LoadMarketIntoList();RefreshMarketButtons();
        }
        void SaveCurrentMarketFromList(){
            var paths=list.Items.Cast<string>().ToList();
            if(Market=="EN")pathsEn=paths;else pathsRu=paths;
        }
        void LoadMarketIntoList(){
            list.Items.Clear();
            foreach(var p in Market=="EN"?pathsEn:pathsRu)list.Items.Add(p);
            UpdateHint();
        }
        void RefreshMarketButtons(){
            bool isRu=Market=="RU";
            ru.BackColor=isRu?Ui.Blue:Color.FromArgb(228,235,246);ru.ForeColor=isRu?Color.White:Ui.Ink;
            en.BackColor=!isRu?Ui.Blue:Color.FromArgb(228,235,246);en.ForeColor=!isRu?Color.White:Ui.Ink;
            Text=(background?"Фоновая музыка":"Музыка")+" · "+(isRu?"Русские":"English");
            UpdateHint();
        }
        void UpdateHint(){hint.Text=(Market=="EN"?"English":"Русские")+" · треков: "+list.Items.Count+" (RU "+pathsRu.Count+" / EN "+pathsEn.Count+")";}
    }
    public class ShortDialog:Dialog {
        public ShortSettings Settings;Dictionary<string,NumericUpDown[]> ranges=new Dictionary<string,NumericUpDown[]>();ComboBox format,fps,resolution;NumericUpDown volume;CheckBox loop,technical;
        public ShortDialog(ShortSettings source):base("Разброс настроек",570){Settings=Store.Clone(source);var all=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};Body.Controls.Add(all);all.Controls.Add(Ui.Label("Каждый вариант получит случайные значения в этих пределах. Длительность видео сохраняется.",true));
            var table=Ui.Table(3);table.AutoSize=true;table.Dock=DockStyle.None;table.Width=550;table.ColumnStyles[0].Width=54;table.ColumnStyles[1].Width=23;table.ColumnStyles[2].Width=23;table.Controls.Add(Ui.Label("Параметр"),0,0);table.Controls.Add(Ui.Label("От"),1,0);table.Controls.Add(Ui.Label("До"),2,0);table.RowCount=1;
            AddRange(table,"crop","Обрезка с каждой стороны, %",Settings.Crop,0,15);AddRange(table,"gray","Обесцвечивание, %",Settings.Gray,0,100);AddRange(table,"brightness","Яркость · 0 = исходная",Settings.Brightness,-10,10);AddRange(table,"contrast","Контраст · 100 = исходный",Settings.Contrast,80,120);AddRange(table,"crf","Сжатие · меньше = лучше",Settings.Crf,16,30);all.Controls.Add(table);
            var details=Ui.Fields();details.Width=550;details.Dock=DockStyle.None;
            format=Ui.Combo(new[]{"mp4","mov","mkv"},Settings.Format);fps=Ui.Combo(new[]{"Как в исходнике","24","25","30","60"},Settings.Fps=="source"?"Как в исходнике":Settings.Fps);resolution=Ui.Combo(new[]{"Исходный","1080","720"},Settings.Resolution=="source"?"Исходный":Settings.Resolution);volume=Ui.Number(Settings.Volume,0,150,0);
            Ui.Row(details,"Формат",format);Ui.Row(details,"Размер",resolution);Ui.Row(details,"Частота кадров",fps);Ui.Row(details,"Громкость музыки, %",volume);all.Controls.Add(details);
            technical=new CheckBox{Text="Авто: разные формат, размер и частота кадров",AutoSize=true,Checked=Settings.TechnicalVariants,Margin=new Padding(0,12,0,0)};all.Controls.Add(technical);technical.CheckedChanged+=(s,e)=>{format.Enabled=!technical.Checked;};format.Enabled=!technical.Checked;
            all.Controls.Add(Ui.Label("Авто чередует MP4 / MOV / MKV; размер меняется до 2%, частота — до 1%. У каждого варианта своя дата.",true));
            loop=new CheckBox{Text="Повторять музыку, если она короче видео",AutoSize=true,Checked=Settings.Loop,Margin=new Padding(0,12,0,0)};all.Controls.Add(loop);
            StandardButtons(()=>Read());Bottom.Controls.Add(Ui.Button("Мягкий разброс",()=>{var s=new ShortSettings();Put("crop",s.Crop);Put("gray",s.Gray);Put("brightness",s.Brightness);Put("contrast",s.Contrast);Put("crf",s.Crf);volume.Value=100;loop.Checked=true;technical.Checked=true;}));
        }
        void AddRange(TableLayoutPanel t,string key,string label,Range r,double min,double max){int row=t.RowCount++;t.RowStyles.Add(new RowStyle(SizeType.AutoSize));var a=Ui.Number(r.Min,min,max);var b=Ui.Number(r.Max,min,max);a.Width=b.Width=105;a.Margin=b.Margin=new Padding(0,6,8,6);var l=Ui.Label(label);l.Anchor=AnchorStyles.Left;l.Margin=new Padding(0,8,8,8);t.Controls.Add(l,0,row);t.Controls.Add(a,1,row);t.Controls.Add(b,2,row);ranges[key]=new[]{a,b};}
        void Put(string key,Range r){ranges[key][0].Value=(decimal)r.Min;ranges[key][1].Value=(decimal)r.Max;}
        Range Get(string key){return new Range((double)ranges[key][0].Value,(double)ranges[key][1].Value);}
        void Read(){Settings.Crop=Get("crop");Settings.Gray=Get("gray");Settings.Brightness=Get("brightness");Settings.Contrast=Get("contrast");Settings.Crf=Get("crf");Settings.Format=(string)format.SelectedItem;Settings.Fps=fps.SelectedIndex==0?"source":(string)fps.SelectedItem;Settings.Resolution=resolution.SelectedIndex==0?"source":(string)resolution.SelectedItem;Settings.Volume=(double)volume.Value;Settings.Loop=loop.Checked;Settings.TechnicalVariants=technical.Checked;Settings.Validate();}
    }
    public class ProfilesDialog:Dialog {
        public Profile[] Profiles;public bool Automatic;ComboBox variant,fps,format,resolution,mode,metadata,filedate;NumericUpDown speed,crop,gray,crf,bitrate,scale,fpsPercent,bitratePercent;DateTimePicker date;int selected=0,amount;bool loading;CheckBox auto;
        List<TableLayoutPanel> pages=new List<TableLayoutPanel>();
        public ProfilesDialog(Profile[] profiles,int count,bool automatic):base("Настройки вариантов",640){Profiles=Store.Clone(profiles);amount=count;Automatic=automatic;
            var layout=Ui.Table();layout.RowCount=4;layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            auto=new CheckBox{Text="Автоматически настроить все варианты",AutoSize=true,Checked=automatic,Margin=new Padding(0,0,0,8)};layout.Controls.Add(auto,0,0);
            layout.Controls.Add(Ui.Label("Все вкладки уже заполнены. Снимите галочку для ручных правок. MP4 / MOV / MKV чередуются; проценты считаются от исходника.",true),0,1);
            variant=Ui.Combo(Enumerable.Range(1,count).Select(i=>"Вариант "+i).ToArray(),"Вариант 1");variant.Dock=DockStyle.Top;variant.Margin=new Padding(0,0,0,16);layout.Controls.Add(variant,0,2);var tabs=new TabControl{Dock=DockStyle.Fill};layout.Controls.Add(tabs,0,3);Body.Controls.Add(layout);
            var image=Page(tabs,"Изображение");var compression=Page(tabs,"Сжатие");var dates=Page(tabs,"Даты");
            speed=Ui.Number(1,.5,2,4);crop=Ui.Number(0,0,15,3);gray=Ui.Number(0,0,100,3);fps=Ui.Combo(new[]{"Как в исходнике","23.976","24","25","29.97","30","50","59.94","60"},"Как в исходнике");fpsPercent=Ui.Number(100,80,120,3);
            Ui.Row(image,"Скорость · 1 = обычная",speed);Ui.Row(image,"Обрезка с каждой стороны, %",crop);Ui.Row(image,"Обесцвечивание, %",gray);Ui.Row(image,"Базовая частота кадров",fps);Ui.Row(image,"Частота от базовой, %",fpsPercent);
            format=Ui.Combo(new[]{"mp4","mov","mkv"},"mp4");resolution=Ui.Combo(new[]{"Исходный","1080","720"},"Исходный");scale=Ui.Number(100,50,100,3);mode=Ui.Combo(new[]{"По качеству","По битрейту","От исходного битрейта"},"По качеству");crf=Ui.Number(22,16,30,2);bitrate=Ui.Number(4,.2,100,3);bitratePercent=Ui.Number(100,30,200,3);
            Ui.Row(compression,"Формат",format);Ui.Row(compression,"Базовый размер",resolution);Ui.Row(compression,"Размер от базового, %",scale);Ui.Row(compression,"Сжатие",mode);Ui.Row(compression,"Качество · меньше = лучше",crf);Ui.Row(compression,"Битрейт, Мбит/с",bitrate);Ui.Row(compression,"Битрейт от исходного, %",bitratePercent);mode.SelectedIndexChanged+=(s,e)=>EnabledFields();
            metadata=Ui.Combo(new[]{"Очистить исходные","Очистить + текущая дата","Сохранить доступные","Очистить + своя дата"},"Очистить исходные");filedate=Ui.Combo(new[]{"Сейчас","Как у исходника","Своя дата"},"Сейчас");date=new DateTimePicker{Format=DateTimePickerFormat.Custom,CustomFormat="yyyy-MM-dd HH:mm:ss",Width=210};Ui.Row(dates,"Метаданные",metadata);Ui.Row(dates,"Дата файла",filedate);Ui.Row(dates,"Своя дата",date);
            variant.SelectedIndexChanged+=(s,e)=>{if(loading)return;SaveSelected();selected=variant.SelectedIndex;LoadSelected();};auto.CheckedChanged+=(s,e)=>{if(loading)return;if(auto.Checked)Profiles=Profile.Automatic(amount);Automatic=auto.Checked;LoadSelected();};
            LoadSelected();StandardButtons(()=>{SaveSelected();Automatic=auto.Checked;foreach(var p in Profiles)p.Validate();});Bottom.Controls.Add(Ui.Button("Автоподбор",()=>{Profiles=Profile.Automatic(amount);auto.Checked=true;LoadSelected();}));
        }
        TableLayoutPanel Page(TabControl tabs,string text){var page=new TabPage(text){Padding=new Padding(14),AutoScroll=true};tabs.TabPages.Add(page);var fields=Ui.Fields();page.Controls.Add(fields);pages.Add(fields);return fields;}
        void EnabledFields(){foreach(var page in pages)page.Enabled=!auto.Checked;crf.Enabled=mode.SelectedIndex==0;bitrate.Enabled=mode.SelectedIndex==1;bitratePercent.Enabled=mode.SelectedIndex==2;}
        void LoadSelected(){loading=true;var p=Profiles[selected];speed.Value=(decimal)p.Speed;crop.Value=(decimal)p.Crop;gray.Value=(decimal)p.Gray;crf.Value=(decimal)p.Crf;bitrate.Value=(decimal)p.Bitrate;scale.Value=(decimal)p.ScalePercent;fpsPercent.Value=(decimal)p.FpsPercent;bitratePercent.Value=(decimal)p.BitratePercent;
            string f=p.Fps=="source"?"Как в исходнике":p.Fps;if(!fps.Items.Contains(f))fps.Items.Add(f);fps.SelectedItem=f;format.SelectedItem=p.Format;resolution.SelectedItem=p.Resolution=="source"?"Исходный":p.Resolution;mode.SelectedIndex=Array.IndexOf(new[]{"quality","bitrate","sourcebitrate"},p.Compression);metadata.SelectedIndex=Array.IndexOf(new[]{"clear","now","keep","custom"},p.Metadata);filedate.SelectedIndex=Array.IndexOf(new[]{"now","keep","custom"},p.FileDate);date.Value=p.CustomDate;EnabledFields();loading=false;}
        void SaveSelected(){if(auto.Checked)return;var p=Profiles[selected];p.Speed=(double)speed.Value;p.Crop=(double)crop.Value;p.Gray=(double)gray.Value;p.Crf=(double)crf.Value;p.Bitrate=(double)bitrate.Value;p.ScalePercent=(double)scale.Value;p.FpsPercent=(double)fpsPercent.Value;p.BitratePercent=(double)bitratePercent.Value;p.Fps=fps.SelectedIndex==0?"source":(string)fps.SelectedItem;p.Format=(string)format.SelectedItem;p.Resolution=resolution.SelectedIndex==0?"source":(string)resolution.SelectedItem;p.Compression=new[]{"quality","bitrate","sourcebitrate"}[mode.SelectedIndex];p.Metadata=new[]{"clear","now","keep","custom"}[metadata.SelectedIndex];p.FileDate=new[]{"now","keep","custom"}[filedate.SelectedIndex];p.CustomDate=date.Value;p.Slot=selected+1;}
    }
    public class AudioDialog:Dialog {
        public int Slot=1;public string Attached;public bool Remove;public AudioJob Job;public Preferences Settings;
        string video;ComboBox provider,voice;TextBox srt=new TextBox{ReadOnly=true,Width=405},voiceId=new TextBox{Width=260},key=new TextBox{UseSystemPasswordChar=true,Width=260},clips=new TextBox{ReadOnly=true,Width=405};
        NumericUpDown slot;Label assignment,info;TableLayoutPanel win,eleven,files;CheckBox remember;
        public AudioDialog(string input,Preferences prefs,string ffmpeg,string ffprobe):base("Озвучка по меткам",680){video=input;Settings=Store.Clone(prefs);
            var all=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};Body.Controls.Add(all);all.Controls.Add(Ui.Label(Path.GetFileName(input)));var top=Ui.Fields();top.Width=550;top.Dock=DockStyle.None;slot=Ui.Number(1,1,10,0);Ui.Row(top,"Для какого варианта",slot);all.Controls.Add(top);
            assignment=Ui.Label("",true);all.Controls.Add(assignment);var row=Ui.Flow();row.Dock=DockStyle.None;row.Width=550;row.Controls.Add(Ui.Button("Выбрать готовый звук",()=>{var path=Ui.File(this,"Аудио|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg");if(path!=null){Attached=path;Slot=(int)slot.Value;DialogResult=DialogResult.OK;Close();}}));row.Controls.Add(Ui.Button("Вернуть исходный",()=>{Remove=true;Slot=(int)slot.Value;DialogResult=DialogResult.OK;Close();}));all.Controls.Add(row);
            all.Controls.Add(Ui.Label("Новая дорожка заменяет весь исходный звук, включая музыку.",true));all.Controls.Add(Ui.Label("Создать озвучку из SRT"));all.Controls.Add(Ui.Label("Текст должен быть уже готов: приложение сохраняет метки и паузы, но не переформулирует фразы.",true));
            var srtrow=Ui.Flow();srtrow.Dock=DockStyle.None;srtrow.Width=550;srtrow.Controls.Add(srt);srtrow.Controls.Add(Ui.Button("SRT…",()=>{try{var p=Ui.File(this,"Субтитры|*.srt");if(p!=null){var cues=AudioEngine.ReadSrt(p);srt.Text=p;info.Text="Фраз: "+cues.Count;}}catch(Exception e){Ui.Error(this,e);}}));all.Controls.Add(srtrow);info=Ui.Label("Загрузите текст с временными метками.",true);all.Controls.Add(info);
            provider=Ui.Combo(new[]{"Голоса Windows · бесплатно","ElevenLabs · свой аккаунт","Готовые фразы из папки"},"Голоса Windows · бесплатно");provider.Width=550;provider.SelectedIndex=Settings.Provider=="elevenlabs"?1:Settings.Provider=="clips"?2:0;all.Controls.Add(provider);
            win=Ui.Fields();win.Width=550;win.Dock=DockStyle.None;var voices=WindowsSupport.Voices();voice=Ui.Combo(voices.Length==0?new[]{"Нет установленных голосов"}:voices,Settings.WindowsVoice);Ui.Row(win,"Системный голос",voice);all.Controls.Add(win);
            eleven=Ui.Fields();eleven.Width=550;eleven.Dock=DockStyle.None;voiceId.Text=Settings.ElevenVoice;key.Text=WindowsSupport.Unprotect(Settings.ProtectedKey);Ui.Row(eleven,"Voice ID · код голоса",voiceId);Ui.Row(eleven,"API-ключ ElevenLabs",key);remember=new CheckBox{Text="Запомнить на компьютере",AutoSize=true,Checked=true};Ui.Row(eleven,"",remember);Ui.Row(eleven,"",Ui.Label("Текст отправится в ElevenLabs. Генерация расходует кредиты аккаунта.",true));all.Controls.Add(eleven);
            files=Ui.Fields();files.Width=550;files.Dock=DockStyle.None;Ui.Row(files,"0001.wav, 0002.wav…",Ui.Button("Папка с фразами…",()=>{var p=Ui.Folder(this,clips.Text);if(p!=null)clips.Text=p;}));Ui.Row(files,"Папка",clips);all.Controls.Add(files);
            provider.SelectedIndexChanged+=(s,e)=>Visibility();slot.ValueChanged+=(s,e)=>Assignment();Visibility();Assignment();
            var generate=Ui.Button("Создать озвучку",()=>{
                try{if(!System.IO.File.Exists(srt.Text))throw new Exception("Загрузите SRT.");AudioEngine.ReadSrt(srt.Text);string chosen=provider.SelectedIndex==0?"windows":provider.SelectedIndex==1?"elevenlabs":"clips";string selectedVoice=provider.SelectedIndex==0?(string)voice.SelectedItem:voiceId.Text.Trim();
                    if(chosen=="windows"&&voices.Length==0)throw new Exception("Выберите ElevenLabs или готовые фразы: системных голосов не найдено.");
                    if(chosen=="elevenlabs"&&(string.IsNullOrWhiteSpace(key.Text)||!System.Text.RegularExpressions.Regex.IsMatch(selectedVoice??"",@"^[A-Za-z0-9_-]{10,80}$")))throw new Exception("Введите API-ключ и Voice ID.");
                    if(chosen=="clips"&&!Directory.Exists(clips.Text))throw new Exception("Выберите папку с фразами.");
                    Settings.Provider=chosen;Settings.WindowsVoice=(string)voice.SelectedItem;Settings.ElevenVoice=voiceId.Text.Trim();Settings.ProtectedKey=remember.Checked&&!string.IsNullOrEmpty(key.Text)?WindowsSupport.Protect(key.Text):"";
                    Slot=(int)slot.Value;Job=new AudioJob{Provider=chosen,Voice=selectedVoice,Key=key.Text,Clips=clips.Text,Srt=srt.Text,Video=video,Output=prefs.Output,Cache=Path.Combine(Store.Root,"audio-cache"),FFmpeg=ffmpeg,FFprobe=ffprobe,WindowsSpeech=WindowsSupport.Speak};DialogResult=DialogResult.OK;Close();
                }catch(Exception e){Ui.Error(this,e);}
            },true);Bottom.Controls.Add(generate);Bottom.Controls.Add(Ui.Button("Закрыть",()=>Close()));
        }
        void Visibility(){win.Visible=provider.SelectedIndex==0;eleven.Visible=provider.SelectedIndex==1;files.Visible=provider.SelectedIndex==2;}
        void Assignment(){var b=Settings.Narrations.FirstOrDefault(x=>x.Video==video&&x.Slot==(int)slot.Value);assignment.Text=b==null?"Сейчас: исходный звук":"Назначено: "+Path.GetFileName(b.Audio);}
        protected override void Dispose(bool disposing){if(disposing)key.Text="";base.Dispose(disposing);}
    }
    public class MainWindow:Form {
        Preferences settings;ListBox videos=new ListBox();ComboBox mode;NumericUpDown count;Button start,cancel,options,music,narration,add,remove,choose,youtube,tiktok;Label status,pathLabel,empty;ProgressBar bar;CancellationTokenSource cancellation;bool loading,closeAfter;
        string ffmpeg=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","ffmpeg.exe"),ffprobe=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","ffprobe.exe");
        public MainWindow(){Text="VideoBatch 4.0.2";ClientSize=new Size(800,650);MinimumSize=new Size(760,640);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);BackColor=Ui.Bg;ForeColor=Ui.Ink;AutoScaleMode=AutoScaleMode.Dpi;
            try{settings=Store.Load();}catch{settings=new Preferences();Store.Normalize(settings);}
            try{Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}
            var root=Ui.Table();root.Padding=new Padding(24);root.RowCount=5;root.RowStyles.Add(new RowStyle(SizeType.Absolute,76));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,74));root.RowStyles.Add(new RowStyle(SizeType.Absolute,68));root.RowStyles.Add(new RowStyle(SizeType.Absolute,115));Controls.Add(root);
            var header=Ui.Table(2);header.ColumnStyles[0].Width=58;header.ColumnStyles[1].Width=42;var title=new Label{Text="VideoBatch",AutoSize=true,Font=new Font("Segoe UI",23,FontStyle.Bold),ForeColor=Ui.Ink,Margin=new Padding(0)};header.Controls.Add(title,0,0);
            var headerRight=Ui.Flow();headerRight.FlowDirection=FlowDirection.RightToLeft;mode=Ui.Combo(new[]{"Короткие","Длинные"},settings.Shorts?"Короткие":"Длинные");mode.Width=120;mode.Margin=new Padding(8,12,0,0);var help=Ui.Button("?",()=>MessageBox.Show(this,"1. Добавьте видео.\n2. В режиме «Короткие» добавьте музыку. В длинных фон — по желанию.\n3. Выберите количество и нажмите «Создать».\n\nКнопки YouTube и TikTok открывают отдельные рабочие пространства для загрузки.\n\nПодробная инструкция лежит рядом с VideoBatch.exe.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information));help.MinimumSize=new Size(36,36);help.Padding=new Padding(0);help.Margin=new Padding(8,8,0,0);headerRight.Controls.Add(help);headerRight.Controls.Add(mode);header.Controls.Add(headerRight,1,0);root.Controls.Add(header,0,0);
            var card=Ui.Table();card.BackColor=Color.White;card.Padding=new Padding(16);card.RowCount=2;card.RowStyles.Add(new RowStyle(SizeType.Percent,100));card.RowStyles.Add(new RowStyle(SizeType.Absolute,56));root.Controls.Add(card,0,1);
            var listHolder=new Panel{Dock=DockStyle.Fill};videos.Dock=DockStyle.Fill;videos.BorderStyle=BorderStyle.None;videos.SelectionMode=SelectionMode.MultiExtended;videos.DrawMode=DrawMode.OwnerDrawFixed;videos.ItemHeight=32;videos.DrawItem+=(s,e)=>{if(e.Index<0)return;e.DrawBackground();string p=(string)videos.Items[e.Index];int n=settings.Narrations.Count(b=>b.Video==p);TextRenderer.DrawText(e.Graphics,Path.GetFileName(p)+(n>0?"  · озвучка: "+n:""),Font,e.Bounds,e.ForeColor,TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter);e.DrawFocusRectangle();};listHolder.Controls.Add(videos);
            empty=new Label{Text="Перетащи видео сюда\nили нажми «Добавить»\n\nYouTube и TikTok — отдельные пространства",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Font=new Font("Segoe UI",14),ForeColor=Ui.Muted,BackColor=Color.White};listHolder.Controls.Add(empty);empty.BringToFront();card.Controls.Add(listHolder,0,0);
            var fileButtons=Ui.Flow();fileButtons.Padding=new Padding(0,12,0,0);add=Ui.Button("Добавить",AddVideos);remove=Ui.Button("Убрать",()=>{foreach(var p in videos.SelectedItems.Cast<object>().ToArray())videos.Items.Remove(p);RefreshView();});narration=Ui.Button("Озвучка",null);narration.Click+=async(s,e)=>await Narrate();youtube=Ui.Button("YouTube",()=>{Save();using(var d=new UploadWindow(settings))d.ShowDialog(this);Save();},true);tiktok=Ui.Button("TikTok",()=>{Save();using(var d=new TikTokUploadWindow(settings))d.ShowDialog(this);Save();},true);fileButtons.Controls.Add(add);fileButtons.Controls.Add(remove);fileButtons.Controls.Add(narration);fileButtons.Controls.Add(youtube);fileButtons.Controls.Add(tiktok);card.Controls.Add(fileButtons,0,1);empty.Click+=(s,e)=>AddVideos();
            foreach(Control c in new Control[]{this,videos,empty,listHolder,card}){c.AllowDrop=true;c.DragEnter+=(s,e)=>e.Effect=cancellation==null&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;c.DragDrop+=(s,e)=>{if(cancellation==null&&e.Data.GetDataPresent(DataFormats.FileDrop))AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));};}
            var action=Ui.Flow();action.Padding=new Padding(0,20,0,0);action.Controls.Add(new Label{Text="Вариантов",AutoSize=true,Margin=new Padding(0,7,12,0)});count=Ui.Number(settings.Shorts?settings.ShortCount:settings.VideoCount,1,10,0);count.Width=56;count.Margin=new Padding(0,5,18,0);action.Controls.Add(count);options=Ui.Button("Настройки",SettingsDialog);music=Ui.Button("Музыка",Music);action.Controls.Add(options);action.Controls.Add(music);root.Controls.Add(action,0,2);
            var destination=Ui.Table(2);destination.ColumnStyles[0].Width=80;destination.ColumnStyles[1].Width=20;var folderText=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};folderText.RowStyles.Add(new RowStyle(SizeType.Absolute,24));folderText.RowStyles.Add(new RowStyle(SizeType.Percent,100));folderText.Controls.Add(Ui.Label("Папка результата",true),0,0);pathLabel=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=Ui.Ink};folderText.Controls.Add(pathLabel,0,1);destination.Controls.Add(folderText,0,0);choose=Ui.Button("Изменить",()=>{var p=Ui.Folder(this,settings.Output);if(p!=null){settings.Output=p;Save();RefreshView();}});choose.Anchor=AnchorStyles.Right;destination.Controls.Add(choose,1,0);root.Controls.Add(destination,0,3);
            var footer=Ui.Table();footer.RowCount=3;footer.RowStyles.Add(new RowStyle(SizeType.Absolute,8));footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));footer.RowStyles.Add(new RowStyle(SizeType.Absolute,46));bar=new ProgressBar{Dock=DockStyle.Fill,Maximum=1000,Style=ProgressBarStyle.Continuous};status=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,Text="Готов к работе.",ForeColor=Ui.Muted,Padding=new Padding(0,8,0,0)};footer.Controls.Add(bar,0,0);footer.Controls.Add(status,0,1);var actions=Ui.Flow();start=Ui.Button("Создать видео",null,true);start.MinimumSize=new Size(250,42);start.Click+=async(s,e)=>await Generate();cancel=Ui.Button("Стоп",()=>{if(cancellation!=null){cancel.Enabled=false;status.Text="Останавливаю…";cancellation.Cancel();}});cancel.Visible=false;var open=Ui.Button("Открыть папку",()=>{try{Directory.CreateDirectory(settings.Output);Ui.Open(settings.Output);}catch(Exception e){Ui.Error(this,e);}});actions.Controls.Add(start);actions.Controls.Add(cancel);actions.Controls.Add(open);footer.Controls.Add(actions,0,2);root.Controls.Add(footer,0,4);
            mode.SelectedIndexChanged+=(s,e)=>{if(loading)return;settings.Shorts=mode.SelectedIndex==0;RefreshView();Save();};count.ValueChanged+=(s,e)=>{if(loading)return;if(settings.Shorts)settings.ShortCount=(int)count.Value;else {settings.VideoCount=(int)count.Value;if(settings.AutoProfiles)settings.Profiles=Profile.Automatic(settings.VideoCount);}RefreshView();Save();};
            FormClosing+=(s,e)=>{if(cancellation!=null){e.Cancel=true;closeAfter=true;cancellation.Cancel();status.Text="Останавливаю перед закрытием…";}else Save();};RefreshView();
            Shown+=(s,e)=>{try{Tools();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Не все файлы распакованы",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
        }
        void Save(){try{Store.Save(settings);}catch(Exception e){status.Text="Настройки не сохранились: "+e.Message;}}
        static string NormMusicMarket(string m){return (m??"").Trim().ToUpperInvariant()=="EN"?"EN":"RU";}
        List<string> ActiveMusic(){return settings.Shorts?(NormMusicMarket(settings.MusicMarketView)=="EN"?settings.MusicEn:settings.MusicRu):(NormMusicMarket(settings.MusicMarketView)=="EN"?settings.BackgroundMusicEn:settings.BackgroundMusicRu);}
        void SyncLegacyMusicLists(){
            settings.MusicMarketView=NormMusicMarket(settings.MusicMarketView);
            settings.Music=new List<string>(settings.MusicMarketView=="EN"?settings.MusicEn:settings.MusicRu);
            settings.BackgroundMusic=new List<string>(settings.MusicMarketView=="EN"?settings.BackgroundMusicEn:settings.BackgroundMusicRu);
        }
        void RefreshView(){loading=true;count.Maximum=10;count.Value=settings.Shorts?settings.ShortCount:settings.VideoCount;options.Text=settings.Shorts?"Разброс":"Настройки";music.Visible=true;var mkt=NormMusicMarket(settings.MusicMarketView);var n=ActiveMusic().Count;music.Text="Музыка · "+(mkt=="EN"?"EN":"RU")+" · "+n;narration.Visible=!settings.Shorts;pathLabel.Text=settings.Output;empty.Visible=videos.Items.Count==0;start.Text="Создать"+(videos.Items.Count>0?" · "+videos.Items.Count*(int)count.Value:"");videos.Invalidate();loading=false;}
        void AddVideos(){if(cancellation!=null)return;using(var d=new OpenFileDialog{Multiselect=true,Filter="Видео|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.mts;*.m2ts;*.ts;*.wmv|Все файлы|*.*"})if(d.ShowDialog(this)==DialogResult.OK)AddPaths(d.FileNames);}
        void AddPaths(IEnumerable<string> paths){foreach(string p in paths)if(File.Exists(p)&&!videos.Items.Contains(p))videos.Items.Add(p);if(videos.Items.Count>0&&videos.SelectedIndex<0)videos.SelectedIndex=0;RefreshView();}
        void Music(){
            if(settings.Shorts){
                using(var d=new MusicDialog(settings.MusicRu,settings.MusicEn,settings.MusicMarketView,false,settings.BackgroundDb))
                if(d.ShowDialog(this)==DialogResult.OK){settings.MusicRu=d.PathsRu;settings.MusicEn=d.PathsEn;settings.MusicMarketView=d.Market;SyncLegacyMusicLists();Save();RefreshView();}
            } else {
                using(var d=new MusicDialog(settings.BackgroundMusicRu,settings.BackgroundMusicEn,settings.MusicMarketView,true,settings.BackgroundDb))
                if(d.ShowDialog(this)==DialogResult.OK){settings.BackgroundMusicRu=d.PathsRu;settings.BackgroundMusicEn=d.PathsEn;settings.MusicMarketView=d.Market;settings.BackgroundDb=d.VolumeDb;SyncLegacyMusicLists();Save();RefreshView();}
            }
        }
        void SettingsDialog(){if(settings.Shorts){using(var d=new ShortDialog(settings.Ranges))if(d.ShowDialog(this)==DialogResult.OK)settings.Ranges=d.Settings;}else{using(var d=new ProfilesDialog(settings.Profiles,settings.VideoCount,settings.AutoProfiles))if(d.ShowDialog(this)==DialogResult.OK){settings.Profiles=d.Profiles;settings.AutoProfiles=d.Automatic;}}Save();}
        void Busy(bool busy){foreach(Control c in new Control[]{mode,count,options,music,narration,add,remove,choose,start,youtube,tiktok})c.Enabled=!busy;cancel.Visible=busy;cancel.Enabled=busy;if(busy)bar.Value=0;}
        IProgress<Update> Progress(){return new Progress<Update>(u=>{if(IsDisposed)return;status.Text=u.Text;bar.Value=(int)Math.Max(0,Math.Min(1000,u.Percent*10));});}
        void Tools(){if(!File.Exists(ffmpeg)||!File.Exists(ffprobe))throw new Exception("Распакуйте архив целиком: не найдена папка tools с обработчиком видео.");if(new FileInfo(ffmpeg).Length!=102856192L||new FileInfo(ffprobe).Length!=102652416L)throw new Exception("Обработчики видео повреждены или распакованы не полностью. Скачайте исправленный архив VideoBatch 4.0.2 и распакуйте его в новую папку.");}
        async Task Generate(){if(cancellation!=null)return;try{Tools();if(videos.Items.Count==0)throw new Exception("Добавьте видео.");var musicList=ActiveMusic();if(settings.Shorts&&musicList.Count==0)throw new Exception("Добавьте хотя бы один трек кнопкой «Музыка» для рынка "+(NormMusicMarket(settings.MusicMarketView)=="EN"?"English":"Русские")+".");Save();
                var snapshot=Store.Clone(settings);var jobMusic=snapshot.Shorts?(NormMusicMarket(snapshot.MusicMarketView)=="EN"?snapshot.MusicEn:snapshot.MusicRu):(NormMusicMarket(snapshot.MusicMarketView)=="EN"?snapshot.BackgroundMusicEn:snapshot.BackgroundMusicRu);
                var job=new BatchJob{Inputs=videos.Items.Cast<string>().ToList(),Music=new List<string>(jobMusic??new List<string>()),BackgroundDb=snapshot.BackgroundDb,Narrations=snapshot.Narrations,Shorts=snapshot.Shorts,Count=(int)count.Value,Output=snapshot.Output,FFmpeg=ffmpeg,FFprobe=ffprobe,Profiles=snapshot.Profiles,Ranges=snapshot.Ranges};cancellation=new CancellationTokenSource();Busy(true);var progress=Progress();var result=await Task.Run(()=>Core.Run(job,progress,cancellation.Token));
                if(result.Errors.Count>0){Directory.CreateDirectory(Store.Root);File.WriteAllLines(Path.Combine(Store.Root,"last-errors.txt"),result.Errors);MessageBox.Show(this,string.Join("\n\n",result.Errors.Take(3))+"\n\nГотовых файлов: "+result.Outputs.Count,"Обработка завершена с ошибками",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            }catch(Exception e){Ui.Error(this,e);}finally{Finish();}}
        void Bind(string video,int slot,string path){settings.Narrations.RemoveAll(b=>b.Video==video&&b.Slot==slot);if(path!=null)settings.Narrations.Add(new Binding{Video=video,Slot=slot,Audio=path});Save();RefreshView();}
        async Task Narrate(){if(cancellation!=null)return;try{Tools();if(videos.SelectedItems.Count!=1)throw new Exception("Выберите одно видео в списке.");string video=(string)videos.SelectedItem;
                using(var d=new AudioDialog(video,settings,ffmpeg,ffprobe)){if(d.ShowDialog(this)!=DialogResult.OK)return;if(d.Remove){Bind(video,d.Slot,null);return;}if(d.Attached!=null){Bind(video,d.Slot,d.Attached);return;}if(d.Job==null)return;
                    settings.Provider=d.Settings.Provider;settings.WindowsVoice=d.Settings.WindowsVoice;settings.ElevenVoice=d.Settings.ElevenVoice;settings.ProtectedKey=d.Settings.ProtectedKey;Save();cancellation=new CancellationTokenSource();Busy(true);var progress=Progress();var job=d.Job;string path;try{path=await Task.Run(()=>AudioEngine.Run(job,progress,cancellation.Token));}finally{job.Key=null;}Bind(video,d.Slot,path);status.Text="Озвучка готова и назначена варианту "+d.Slot+". Можно создавать видео.";
                }
            }catch(OperationCanceledException){status.Text="Озвучка остановлена.";}catch(Exception e){Ui.Error(this,e);}finally{Finish();}}
        void Finish(){if(cancellation!=null){cancellation.Dispose();cancellation=null;}Busy(false);if(closeAfter)Close();}
    }
}
