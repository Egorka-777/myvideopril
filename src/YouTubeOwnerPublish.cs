using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed class OwnerPublishSelection {
        public YouTubeChannel Channel;
        public YouTubeItem Item;
        public string CommentText;
        public string VideoId=>YouTubeOwnerPublish.VideoId(Item);
    }
    public static class YouTubeOwnerPublish {
        public static string VideoId(YouTubeItem item){
            string id=(item?.PublishedVideoId??"").Trim();
            if(!Store.IsValidYouTubeVideoId(id))id=Store.ExtractYouTubeVideoId(item?.PublishedUrl??"");
            return Store.IsValidYouTubeVideoId(id)?id:"";
        }
        public static bool CommentComplete(YouTubeItem item)=>item?.OwnerCommentState=="confirmed"||item?.OwnerCommentState=="confirmed_manual";
        public static string RenderComment(string template,DateTime pcDate){
            string result=Regex.Replace(template??"",@"\{([^{}]+)\}",m=>{
                switch(m.Groups[1].Value){
                    case "ddMM":return pcDate.ToString("ddMM",CultureInfo.InvariantCulture);
                    case "dd":return pcDate.ToString("dd",CultureInfo.InvariantCulture);
                    case "MM":return pcDate.ToString("MM",CultureInfo.InvariantCulture);
                    case "yyyy":return pcDate.ToString("yyyy",CultureInfo.InvariantCulture);
                    case "date":return pcDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
                    default:throw new Exception("Неизвестная вставка в комментарии: "+m.Value);
                }
            }).Replace("\r\n","\n").Trim();
            if(result.Length>10000||result.Any(c=>char.IsControl(c)&&c!='\n'&&c!='\t'))throw new Exception("Комментарий: до 10 000 символов, без управляющих символов.");
            return result;
        }
        public static List<OwnerPublishSelection> Candidates(IEnumerable<YouTubeChannel> channels,DateTime pcDate){
            var result=new List<OwnerPublishSelection>();
            foreach(var channel in channels??Enumerable.Empty<YouTubeChannel>()){
                if(channel==null)continue;
                foreach(var item in channel.Items??new List<YouTubeItem>()){
                    if(item==null||item.StudioPending||VideoId(item)=="")continue;
                    result.Add(new OwnerPublishSelection{Channel=channel,Item=item,CommentText=(item.OwnerCommentState=="pending"||CommentComplete(item))?item.OwnerCommentText:RenderComment(channel.OwnerCommentTemplate,pcDate)});
                }
            }
            return result;
        }
        public static void EnsureAttached(Preferences settings,OwnerPublishSelection choice){
            if(!(settings.YouTubeChannels??new List<YouTubeChannel>()).Contains(choice.Channel)||!(choice.Channel.Items??new List<YouTubeItem>()).Contains(choice.Item))
                throw new Exception("Список аккаунтов или видео изменился во время публикации. Действие остановлено до отправки комментария.");
        }
        public static UploadItemJob ToJob(OwnerPublishSelection choice){
            if(choice==null||choice.Channel==null||choice.Item==null||choice.VideoId=="")throw new Exception("Нет подтверждённого ID видео.");
            if(choice.Item.StudioPending)throw new Exception("Сначала проверьте незавершённую загрузку в Studio.");
            if(choice.Item.OwnerCommentState=="pending")throw new Exception("Предыдущая отправка комментария не подтверждена. Сначала проверьте видео.");
            if(string.IsNullOrWhiteSpace(choice.Item.StudioUploadId))choice.Item.StudioUploadId=Guid.NewGuid().ToString("N");
            return new UploadItemJob{title=choice.Item.Title,publishedVideoId=choice.VideoId,localJobId=choice.Item.StudioUploadId,
                commentText=choice.CommentText??"",commentState=CommentComplete(choice.Item)?"confirmed":""};
        }
        public static void Apply(OwnerPublishSelection choice,UploadMessage message){
            if(choice==null||message.videoId!=choice.VideoId||message.localJobId!=choice.Item.StudioUploadId)throw new Exception("Ответ публикации относится к другому видео. Действие остановлено.");
            var item=choice.Item;
            if(message.stage=="owner_video_public"){item.StudioVisibility="public";}
            else if(message.stage=="owner_comment_submitting"){
                if(CommentComplete(item)||item.OwnerCommentState=="pending")throw new Exception("Комментарий уже отправлен или ожидает проверки.");
                if(message.commentText!=choice.CommentText)throw new Exception("Текст комментария изменился после предпросмотра.");
                item.OwnerCommentText=choice.CommentText;item.OwnerCommentState="pending";
            }else if(message.stage=="owner_comment_confirmed"){
                if(string.IsNullOrWhiteSpace(message.commentId)||message.commentText!=choice.CommentText)throw new Exception("YouTube не подтвердил ID и текст комментария.");
                item.OwnerCommentText=choice.CommentText;item.OwnerCommentId=message.commentId;item.OwnerCommentState="confirmed";
            }
        }
    }
    public partial class UploadWindow {
        public bool OwnerPublishing{get;private set;}
        public Task RunOwnerPublishAsync(IReadOnlyList<OwnerPublishSelection> choices)=>TrackOperation(()=>PublishOwned(choices));
        async Task PublishOwned(IReadOnlyList<OwnerPublishSelection> choices){
            if(cancellation!=null||uploadsInFlight>0)throw new Exception("Дождитесь текущей операции или нажмите Стоп.");
            if(choices==null||choices.Count==0)throw new Exception("Выберите видео для публикации.");
            DolphinRunner.CheckFiles();
            string token=WindowsSupport.Unprotect(settings.ProtectedDolphinToken);
            if(string.IsNullOrWhiteSpace(token))throw new Exception("Укажите токен Dolphin в настройках.");
            // Snapshot the whole plan before the first profile starts. No files needed.
            var batches=choices.GroupBy(c=>(c.Channel.ProfileId??"").Trim(),StringComparer.OrdinalIgnoreCase).Select(group=>{
                if(group.Key=="")throw new Exception("Не указан профиль Dolphin.");
                var list=group.ToList();
                if(list.Select(c=>c.Channel).Distinct().Count()>1)throw new Exception("Для одного профиля выбраны разные каналы. Запустите их по отдельности.");
                foreach(var choice in list)YouTubeOwnerPublish.EnsureAttached(settings,choice);
                var items=list.Select(YouTubeOwnerPublish.ToJob).ToArray();
                if(items.Select(i=>i.publishedVideoId).Distinct().Count()!=items.Length)throw new Exception("Одно видео выбрано дважды.");
                return new {Choices=list,Items=items,ProfileId=group.Key};
            }).ToList();
            lock(saveLock)Store.Save(settings);
            lock(uploadLock){if(batches.Any(b=>busyProfiles.Contains(b.ProfileId)))throw new Exception("Выбранный профиль занят.");uploadCts=new CancellationTokenSource();foreach(var b in batches)busyProfiles.Add(b.ProfileId);}
            var ct=uploadCts.Token;OwnerPublishing=true;UploadBusyStart();
            try{
                foreach(var batch in batches){
                    ct.ThrowIfCancellationRequested();
                    var channel=batch.Choices[0].Channel;var row=FindChannelRow(channel);
                    Action<string> report=text=>{if(row!=null)Status(row,text);else Write(channel.Name+": "+text);};
                    try{
                        var job=new UploadJob{token=token,localPort=settings.DolphinPort,profileId=batch.ProfileId,channelUrl=channel.ChannelUrl,
                            studioPublishExisting=true,skipQueueDelay=true,keepProfileOpen=settings.KeepDolphinProfileOpenAfterUpload,items=batch.Items};
                        await DolphinRunner.Run(job,message=>{
                            if(!string.IsNullOrWhiteSpace(message.text))report(message.text);
                            if(message.stage=="owner_video_public"||message.stage=="owner_comment_submitting"||message.stage=="owner_comment_confirmed"){
                                if(message.packIndex<1||message.packIndex>batch.Choices.Count)throw new Exception("Неверный номер видео в ответе worker.");
                                lock(saveLock){var choice=batch.Choices[message.packIndex-1];YouTubeOwnerPublish.EnsureAttached(settings,choice);YouTubeOwnerPublish.Apply(choice,message);Store.Save(settings);}
                            }
                        },ct);
                        report("Опубликовано ✓ · "+batch.Items.Length);
                    }catch(OperationCanceledException){report("Остановлено · подтверждённые результаты сохранены");throw;}
                    catch(Exception ex){report("Ошибка: "+ex.Message);throw;}
                }
            }finally{lock(uploadLock)foreach(var b in batches)busyProfiles.Remove(b.ProfileId);OwnerPublishing=false;UploadBusyEnd();}
        }
    }
    internal sealed class OwnerCommentTemplateDialog:Form {
        readonly TextBox text;
        public string Template=>text.Text;
        public OwnerCommentTemplateDialog(YouTubeChannel channel){
            Text="Комментарий автора · "+channel.Name;ClientSize=new Size(620,400);MinimumSize=new Size(500,360);
            StartPosition=FormStartPosition.CenterParent;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=4};
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,62));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(root);
            root.Controls.Add(new Label{Text="Текст для этого аккаунта",AutoSize=true,Font=Theme.FontCardTitle,Margin=new Padding(0,0,0,12)},0,0);
            text=new TextBox{Multiline=true,AcceptsReturn=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Theme.Card,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.FixedSingle,Text=(channel.OwnerCommentTemplate??"").Replace("\r\n","\n").Replace("\n",Environment.NewLine)};root.Controls.Add(text,0,1);
            root.Controls.Add(new Label{Text="Дата ПК: {ddMM} → "+DateTime.Now.ToString("ddMM")+" · {date} → "+DateTime.Now.ToString("yyyy-MM-dd")+"\nТакже: {dd}, {MM}, {yyyy}. Пустой шаблон — без комментария.",Dock=DockStyle.Fill,ForeColor=Theme.TextSecondary,Padding=new Padding(0,10,0,0)},0,2);
            var buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};var cancel=Theme.MakeButton("Отмена",ghost:true,action:()=>{DialogResult=DialogResult.Cancel;Close();});
            var save=Theme.MakeButton("Сохранить",accent:true,action:()=>{try{YouTubeOwnerPublish.RenderComment(text.Text,DateTime.Now);DialogResult=DialogResult.OK;Close();}catch(Exception e){MessageBox.Show(this,e.Message);}});buttons.Controls.Add(cancel);buttons.Controls.Add(save);root.Controls.Add(buttons,0,3);CancelButton=cancel;Shown+=(s,e)=>{text.Focus();text.SelectionStart=text.TextLength;text.SelectionLength=0;};
        }
    }
    internal sealed class OwnerPublishDialog:Form {
        readonly DataGridView videos;
        readonly TextBox preview;
        internal string PreviewText=>preview.Text;
        readonly Preferences settings;
        readonly List<OwnerPublishSelection> candidates;
        public List<OwnerPublishSelection> Choices{get;private set;}
        public OwnerPublishDialog(Preferences prefs,IEnumerable<YouTubeChannel> channels){
            settings=prefs;candidates=YouTubeOwnerPublish.Candidates(channels,DateTime.Now);
            Text="Опубликовать · YouTube";ClientSize=new Size(850,560);MinimumSize=new Size(650,480);StartPosition=FormStartPosition.CenterParent;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;Font=Theme.FontBody;
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=5};
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,58));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.Percent,42));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(root);
            root.Controls.Add(new Label{Text="Выберите загруженные видео · время ПК "+DateTime.Now.ToString("dd.MM.yyyy HH:mm zzz"),AutoSize=true,Margin=new Padding(0,0,0,12)},0,0);
            videos=new DataGridView{Dock=DockStyle.Fill,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect};Theme.StyleGrid(videos);
            videos.Columns.Add(new DataGridViewCheckBoxColumn{Name="on",HeaderText="✓",Width=38,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});videos.Columns.Add("account","Аккаунт");videos.Columns.Add("title","Видео");videos.Columns.Add("state","Состояние");
            foreach(DataGridViewColumn c in videos.Columns)if(c.Name!="on")c.ReadOnly=true;
            foreach(var c in candidates){int i=videos.Rows.Add(c.Item.StudioVisibility=="scheduled"&&c.Item.OwnerCommentState!="pending",c.Channel.Name,c.Item.Title,State(c));videos.Rows[i].Tag=c;}
            videos.SelectionChanged+=(s,e)=>ShowPreview();videos.CellMouseDown+=(s,e)=>{if(e.RowIndex>=0&&e.Button==MouseButtons.Right){videos.CurrentCell=videos.Rows[e.RowIndex].Cells["title"];var c=(OwnerPublishSelection)videos.Rows[e.RowIndex].Tag;var menu=Theme.MakeContextMenu();menu.Items.Add("Открыть видео",null,(a,b)=>Ui.Open("https://www.youtube.com/watch?v="+c.VideoId));if(c.Item.OwnerCommentState=="pending"){
                menu.Items.Add("Проверил: комментарий уже есть",null,(a,b)=>Reconcile(c,e.RowIndex,true));menu.Items.Add("Проверил: комментария нет — разрешить повтор",null,(a,b)=>Reconcile(c,e.RowIndex,false));}menu.Show(Cursor.Position);}};
            root.Controls.Add(videos,0,1);root.Controls.Add(new Label{Text="Комментарий выбранного видео",AutoSize=true,ForeColor=Theme.TextSecondary,Margin=new Padding(0,12,0,6)},0,2);
            preview=new TextBox{ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Theme.Card,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.None,Font=Theme.FontBody};root.Controls.Add(preview,0,3);
            var buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Margin=new Padding(0,12,0,0)};
            var cancel=Theme.MakeButton("Отмена",ghost:true,action:()=>{DialogResult=DialogResult.Cancel;Close();});
            var publish=Theme.MakeButton("Опубликовать выбранные",accent:true,action:()=>{try{videos.EndEdit();Choices=videos.Rows.Cast<DataGridViewRow>().Where(r=>Convert.ToBoolean(r.Cells["on"].Value??false)).Select(r=>(OwnerPublishSelection)r.Tag).ToList();if(Choices.Count==0)throw new Exception("Отметьте видео галочкой.");foreach(var c in Choices)YouTubeOwnerPublish.ToJob(c);DialogResult=DialogResult.OK;Close();}catch(Exception e){MessageBox.Show(this,e.Message,"YouTube");}});
            buttons.Controls.Add(cancel);buttons.Controls.Add(publish);root.Controls.Add(buttons,0,4);CancelButton=cancel;Shown+=(s,e)=>ShowPreview();ShowPreview();
        }
        static string State(OwnerPublishSelection c)=>c.Item.OwnerCommentState=="pending"?"Комментарий: проверить":c.Item.OwnerCommentState=="confirmed_manual"?"Проверено вручную":YouTubeOwnerPublish.CommentComplete(c.Item)?"Комментарий сохранён":c.Item.StudioVisibility=="public"?"Открытый доступ":c.Item.StudioVisibility=="scheduled"?"Запланировано":"Проверить доступ";
        void ShowPreview(){if(preview==null)return;var c=(videos.CurrentRow?.Tag??(videos.Rows.Count>0?videos.Rows[0].Tag:null)) as OwnerPublishSelection;preview.Text=c==null?"Нет сохранённых видео. Сначала загрузите ролик через Studio.":"https://www.youtube.com/watch?v="+c.VideoId+Environment.NewLine+Environment.NewLine+(string.IsNullOrWhiteSpace(c.CommentText)?"Без комментария. Шаблон: правая кнопка по аккаунту → Комментарий автора.":c.CommentText.Replace("\r\n","\n").Replace("\n",Environment.NewLine));}
        void Reconcile(OwnerPublishSelection c,int index,bool exists){
            if(MessageBox.Show(this,exists?"Вы проверили страницу видео, и этот комментарий уже опубликован?":"Вы проверили страницу видео, и комментария точно нет? Следующий запуск сможет отправить его снова.","Проверка комментария",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
            string oldState=c.Item.OwnerCommentState,oldText=c.Item.OwnerCommentText,oldId=c.Item.OwnerCommentId,oldPreview=c.CommentText;
            try{
                c.Item.OwnerCommentState=exists?"confirmed_manual":"";
                if(!exists){c.Item.OwnerCommentText="";c.Item.OwnerCommentId="";c.CommentText=YouTubeOwnerPublish.RenderComment(c.Channel.OwnerCommentTemplate,DateTime.Now);}
                Store.Save(settings);videos.Rows[index].Cells["state"].Value=State(c);ShowPreview();
            }catch(Exception e){c.Item.OwnerCommentState=oldState;c.Item.OwnerCommentText=oldText;c.Item.OwnerCommentId=oldId;c.CommentText=oldPreview;MessageBox.Show(this,"Не удалось сохранить проверку: "+e.Message,"YouTube");}
        }
    }
}
