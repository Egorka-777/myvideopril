using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class TelegramProfileTemplate {
        public string id {get;set;}="";
        public string title {get;set;}="";
        public string first_name {get;set;}="";
        public string last_name {get;set;}="";
        public string avatar {get;set;}="";
        public override string ToString()=>title;
    }
    public static class TelegramProfileTemplates {
        public static void Validate(TelegramProfileTemplate template){
            if(template==null||!Regex.IsMatch(template.id??"",@"^[a-f0-9]{32}$"))throw new IOException("Некорректный шаблон");
            if(string.IsNullOrWhiteSpace(template.title)||template.title.Length>60)throw new IOException("Назовите шаблон: до 60 символов");
            if(string.IsNullOrWhiteSpace(template.first_name)||template.first_name.Length>64||template.last_name==null||template.last_name.Length>64)throw new IOException("Имя обязательно. Имя и фамилия — до 64 символов в приложении");
            if((template.first_name+template.last_name).Any(char.IsControl))throw new IOException("Имя и фамилия должны быть в одну строку");
            if(template.avatar!=""&&!Regex.IsMatch(template.avatar??"",@"^[a-f0-9]{64}\.jpg$"))throw new IOException("Некорректный файл фото шаблона");
        }
        public static string AvatarPath(TelegramStore store,string asset){
            if(!Regex.IsMatch(asset??"",@"^[a-f0-9]{64}\.jpg$"))throw new IOException("Некорректный файл фото шаблона");
            return Path.Combine(store.Root,"profile-avatars",asset);
        }
        public static byte[] ReadImage(string path){
            if(!new[]{".jpg",".jpeg",".png"}.Contains(Path.GetExtension(path).ToLowerInvariant())||!File.Exists(path)||new FileInfo(path).Length>10*1024*1024)throw new IOException("Выберите PNG/JPG до 10 МБ");
            try{using(var source=Image.FromFile(path)){
                if((long)source.Width*source.Height>40000000||Math.Min(source.Width,source.Height)<64)throw new IOException("Фото: минимум 64×64, не больше 40 млн пикселей");
                // The preview and saved file use the same centered square crop.
                int side=Math.Min(source.Width,source.Height),size=Math.Min(side,1024);
                using(var image=new Bitmap(size,size))using(var graphics=Graphics.FromImage(image))using(var memory=new MemoryStream()){
                    graphics.Clear(Color.White);graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source,new Rectangle(0,0,size,size),new Rectangle((source.Width-side)/2,(source.Height-side)/2,side,side),GraphicsUnit.Pixel);
                    image.Save(memory,ImageFormat.Jpeg);return memory.ToArray();
                }
            }}catch(ArgumentException e){throw new IOException("Не удалось прочитать PNG/JPG",e);}catch(OutOfMemoryException e){throw new IOException("Не удалось прочитать изображение",e);}
        }
        public static TelegramProfileTemplate Save(TelegramStore store,TelegramProfileTemplate template,byte[] image=null){
            var saved=new TelegramProfileTemplate{id=template.id,title=(template.title??"").Trim(),first_name=(template.first_name??"").Trim(),last_name=(template.last_name??"").Trim(),avatar=template.avatar??""};Validate(saved);
            store.Prepare();
            if(image!=null){
                string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(image)).Replace("-","").ToLowerInvariant();
                saved.avatar=hash+".jpg";string path=AvatarPath(store,saved.avatar);Directory.CreateDirectory(Path.GetDirectoryName(path));
                if(!File.Exists(path)){string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(temp,image);if(!File.Exists(path))File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
            }
            if(saved.avatar!=""&&!File.Exists(AvatarPath(store,saved.avatar)))throw new IOException("Фото шаблона не найдено. Выберите изображение снова");
            store.Change(data=>{int index=data.profile_templates.FindIndex(t=>t.id==saved.id);if(index<0)data.profile_templates.Add(saved);else data.profile_templates[index]=saved;});return saved;
        }
        public static Dictionary<string,object> Job(TelegramStore store,TelegramAccount account,TelegramProfileTemplate template){
            Validate(template);string path=template.avatar==""?"":AvatarPath(store,template.avatar);
            if(path!=""&&!File.Exists(path))throw new IOException("Фото шаблона не найдено. Выберите изображение снова");
            return new Dictionary<string,object>{{"action","profile_template"},{"accounts",new List<TelegramAccount>{account}},{"profile",new Dictionary<string,object>{{"template_id",template.id},{"first_name",template.first_name},{"last_name",template.last_name},{"avatar",path},{"avatar_sha256",template.avatar==""?"":Path.GetFileNameWithoutExtension(template.avatar)}}}};
        }
    }
    internal sealed class TelegramProfileTemplateDialog:Form {
        readonly TelegramStore store;readonly ComboBox choices;readonly TextBox title,first,last;readonly PictureBox preview;readonly Label message;readonly FlowLayoutPanel footer;
        string id="",avatar="";byte[] image;bool loading;
        public TelegramProfileTemplate Result{get;private set;}
        public TelegramProfileTemplateDialog(TelegramStore store,TelegramAccount account){
            this.store=store;Text="Изменить данные Telegram";ClientSize=new Size(560,650);MinimumSize=new Size(540,620);StartPosition=FormStartPosition.CenterParent;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;Theme.StyleModal(this);
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(20)};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,92));Controls.Add(layout);
            layout.Controls.Add(new Label{Text="Аккаунт: "+account.name,Dock=DockStyle.Fill,Font=Theme.FontCardTitle,AutoEllipsis=true},0,0);
            var body=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Margin=Padding.Empty};layout.Controls.Add(body,0,1);
            void Label(string text)=>body.Controls.Add(new Label{Text=text,AutoSize=true,ForeColor=Theme.TextSecondary,Margin=new Padding(0,0,0,6)});
            Label("Шаблон");var row=new FlowLayoutPanel{Width=490,Height=42,WrapContents=false,Margin=Padding.Empty};choices=Theme.MakeCombo(new string[0]);choices.Width=320;choices.Name="ProfileTemplateChoice";choices.DisplayMember="title";row.Controls.Add(choices);row.Controls.Add(Theme.MakeButton("＋ Новый",ghost:true,action:New));body.Controls.Add(row);
            Label("Название шаблона");title=Field(body,"ProfileTemplateTitle",60);Label("Имя");first=Field(body,"ProfileFirstName",64);Label("Фамилия · можно оставить пустой");last=Field(body,"ProfileLastName",64);
            var photo=new FlowLayoutPanel{Width=490,Height=132,WrapContents=false,Margin=new Padding(0,4,0,8)};preview=new PictureBox{Name="ProfileAvatarPreview",Size=new Size(112,112),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Theme.Card};photo.Controls.Add(preview);
            var photoActions=new FlowLayoutPanel{Width=340,Height=120,FlowDirection=FlowDirection.TopDown,WrapContents=false};photoActions.Controls.Add(Theme.MakeButton("Выбрать фото",ghost:true,action:ChoosePhoto));photoActions.Controls.Add(Theme.MakeButton("Без замены фото",ghost:true,action:()=>{avatar="";image=null;SetPreview(null);}));photoActions.Controls.Add(new Label{Text="PNG/JPG · квадрат по центру",AutoSize=true,ForeColor=Theme.TextMuted});photo.Controls.Add(photoActions);body.Controls.Add(photo);
            message=new Label{Text="Без картинки текущая аватарка сохранится. Пустая фамилия удалит прежнюю.",Width=485,Height=48,ForeColor=Theme.TextSecondary};body.Controls.Add(message);
            footer=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,Margin=Padding.Empty};footer.Controls.Add(Theme.MakeButton("Сохранить шаблон",ghost:true,action:()=>Save(false)));var apply=Theme.MakeButton("Применить к аккаунту",accent:true,action:()=>Save(true));apply.Name="ApplyProfileTemplate";footer.Controls.Add(apply);var close=Theme.MakeButton("Закрыть",ghost:true);close.DialogResult=DialogResult.Cancel;footer.Controls.Add(close);layout.Controls.Add(footer,0,2);CancelButton=close;
            choices.SelectedIndexChanged+=(s,e)=>{if(!loading&&choices.SelectedItem is TelegramProfileTemplate selected)LoadTemplate(selected);};RefreshChoices();if(choices.Items.Count>0)choices.SelectedIndex=0;else New();
        }
        static TextBox Field(Control body,string name,int max){var field=Theme.MakeSearchBox();field.Name=name;field.Width=485;field.MaxLength=max;field.Margin=new Padding(0,0,0,12);body.Controls.Add(field);return field;}
        void RefreshChoices(){loading=true;try{choices.Items.Clear();foreach(var template in store.Load().profile_templates.OrderBy(t=>t.title))choices.Items.Add(template);}finally{loading=false;}}
        void New(){loading=true;choices.SelectedIndex=-1;loading=false;id=Guid.NewGuid().ToString("N");title.Text="Шаблон "+(choices.Items.Count+1);first.Clear();last.Clear();avatar="";image=null;SetPreview(null);first.Focus();}
        void LoadTemplate(TelegramProfileTemplate template){id=template.id;title.Text=template.title;first.Text=template.first_name;last.Text=template.last_name;avatar=template.avatar;image=null;try{SetPreview(avatar==""?null:File.ReadAllBytes(TelegramProfileTemplates.AvatarPath(store,avatar)));message.Text="Изменения применятся только к выбранному аккаунту.";}catch(Exception e)when(e is IOException||e is ArgumentException||e is OutOfMemoryException){SetPreview(null);message.Text="Фото шаблона не читается. Выберите его заново.";}}
        void SetPreview(byte[] bytes){var old=preview.Image;preview.Image=null;if(bytes!=null)using(var memory=new MemoryStream(bytes))using(var source=Image.FromStream(memory))preview.Image=new Bitmap(source);old?.Dispose();}
        void ChoosePhoto(){using(var picker=new OpenFileDialog{Filter="Изображение PNG/JPG|*.png;*.jpg;*.jpeg",Multiselect=false})if(picker.ShowDialog(this)==DialogResult.OK)try{image=TelegramProfileTemplates.ReadImage(picker.FileName);SetPreview(image);message.Text="Предпросмотр показывает квадрат, который будет загружен в Telegram.";}catch(Exception e){message.Text=e.Message;}}
        void Save(bool apply){try{Result=TelegramProfileTemplates.Save(store,new TelegramProfileTemplate{id=id,title=title.Text,first_name=first.Text,last_name=last.Text,avatar=avatar},image);id=Result.id;avatar=Result.avatar;image=null;RefreshChoices();loading=true;choices.SelectedItem=choices.Items.Cast<TelegramProfileTemplate>().First(t=>t.id==id);loading=false;message.Text="Шаблон сохранён.";if(apply){DialogResult=DialogResult.OK;Close();}}catch(Exception e){loading=false;message.Text=e.Message;}}
        protected override void Dispose(bool disposing){if(disposing)preview?.Image?.Dispose();base.Dispose(disposing);}
    }
}
