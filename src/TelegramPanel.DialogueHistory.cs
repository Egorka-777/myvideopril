using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class TelegramPanel {
        DataGridView dialogueHistoryGrid;
        RichTextBox dialogueHistoryDetail;
        TelegramDialogueHistory activeDialogueHistory;
        TelegramDialogueStorage DialogueStorage=>new TelegramDialogueStorage(store.Root);
        void BuildDialogueHistoryPage(Panel page){
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(12)};
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,48));root.RowStyles.Add(new RowStyle(SizeType.Percent,45));root.RowStyles.Add(new RowStyle(SizeType.Percent,55));page.Controls.Add(root);
            var bar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};bar.Controls.Add(new Label{Text="История нейрочатинга",Font=Theme.FontCardTitle,AutoSize=true,Margin=new Padding(0,8,20,0)});bar.Controls.Add(Theme.MakeButton("Обновить",ghost:true,action:()=>{try{RefreshDialogueHistory();}catch(Exception e){Failure(e);}}));root.Controls.Add(bar,0,0);
            dialogueHistoryGrid=new DataGridView{Name="DialogueHistory",Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};Theme.StyleGrid(dialogueHistoryGrid);
            foreach(var x in new[]{new[]{"date","Начало"},new[]{"target","Чат"},new[]{"accounts","Аккаунты"},new[]{"sent","Отправлено"},new[]{"state","Результат"}})dialogueHistoryGrid.Columns.Add(x[0],x[1]);root.Controls.Add(dialogueHistoryGrid,0,1);
            dialogueHistoryDetail=Theme.MakeLogBox();dialogueHistoryDetail.Name="DialogueHistoryDetail";root.Controls.Add(dialogueHistoryDetail,0,2);dialogueHistoryGrid.SelectionChanged+=(s,e)=>ShowDialogueHistory();
        }
        void RefreshDialogueHistory(){
            if(dialogueHistoryGrid==null)return;string selected=(dialogueHistoryGrid.CurrentRow?.Tag as TelegramDialogueHistory)?.Id;dialogueHistoryGrid.Rows.Clear();
            foreach(var run in DialogueStorage.ListHistory()){
                string state=run.Status;if(run.EndedUtc==default(DateTime)&&run.Id!=activeDialogueHistory?.Id)state="Прерван / приложение закрыто";
                int index=dialogueHistoryGrid.Rows.Add(run.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm:ss"),run.Target,string.Join(", ",run.Participants.Select(p=>p.name)),run.Sent,state);dialogueHistoryGrid.Rows[index].Tag=run;
                if(run.Id==selected)dialogueHistoryGrid.CurrentCell=dialogueHistoryGrid.Rows[index].Cells[0];
            }ShowDialogueHistory();
        }
        void ShowDialogueHistory(){
            var run=dialogueHistoryGrid?.CurrentRow?.Tag as TelegramDialogueHistory;if(run==null){if(dialogueHistoryDetail!=null)dialogueHistoryDetail.Clear();return;}
            var text=new StringBuilder();text.AppendLine(run.Target).AppendLine((run.EndedUtc==default(DateTime)&&run.Id!=activeDialogueHistory?.Id?"Прерван / приложение закрыто":run.Status)+" · "+run.Result);
            text.AppendLine("Лимиты: "+string.Join("; ",run.Limits.Select(p=>p.Key+"="+p.Value)));
            text.AppendLine("Расписание: "+string.Join("; ",run.Schedule.Select(p=>p.Key+"="+p.Value)));
            for(int i=0;i<run.Steps.Count;i++){
                var step=run.Steps[i];string name=run.Participants.FirstOrDefault(p=>p.id==step.account_id)?.name??step.account_id;
                text.AppendLine().AppendLine((i+1)+". "+name+" · ответ №"+step.reply_to).AppendLine(step.text);
                if(!string.IsNullOrEmpty(step.media_path))text.AppendLine("Медиа: "+step.media_path);
                if(!string.IsNullOrEmpty(step.sticker_path))text.AppendLine("Стикер: "+step.sticker_path);
            }
            text.AppendLine().AppendLine("События:");foreach(var e in run.Events)text.AppendLine(e.Utc.ToLocalTime().ToString("HH:mm:ss")+" · "+e.Kind+" · "+(run.Participants.FirstOrDefault(p=>p.id==e.AccountId)?.name??e.AccountId)+" · "+e.Text+(e.MessageId!=0?" · ID "+e.MessageId:"")+(e.Part!=""?" · "+e.Part:""));dialogueHistoryDetail.Text=text.ToString();
        }
        void BeginDialogueHistory(string id,Dictionary<string,object> job){
            if(Convert.ToString(job["action"])!="ai_dialogue")return;
            var accounts=(List<TelegramAccount>)job["accounts"];
            var run=new TelegramDialogueHistory{Id=id,Target=Convert.ToString(job["target"]),Participants=DialogueParticipants(accounts),Steps=(List<TelegramDialogueStep>)job["steps"]};
            if(job.TryGetValue("limits",out var limits))run.Limits=(Dictionary<string,object>)limits;
            if(job.TryGetValue("schedule",out var schedule))run.Schedule=(Dictionary<string,object>)schedule;
            DialogueStorage.SaveHistory(run);activeDialogueHistory=run;RefreshDialogueHistory();
        }
        void RecordDialogueHistory(Dictionary<string,object> ev){
            var run=activeDialogueHistory;if(run==null)return;string kind=Get(ev,"event");
            if(!new[]{"dialogue_joined","dialogue_sent","dialogue_failed","dialogue_complete","dialogue_waiting","summary","error","stopped"}.Contains(kind))return;
            var entry=new TelegramDialogueHistoryEvent{Kind=kind,AccountId=Get(ev,"account_id"),Text=Get(ev,"text"),Part=Get(ev,"part")};if(ev.TryGetValue("step",out var step))entry.Step=Convert.ToInt32(step);if(ev.TryGetValue("message_id",out var msg))entry.MessageId=Convert.ToInt64(msg);run.Events.Add(entry);if(kind=="dialogue_sent")run.Sent++;DialogueStorage.SaveHistory(run);
        }
        void FinishDialogueHistory(string state,string result){var run=activeDialogueHistory;if(run==null)return;run.Status=state;run.Result=result;run.EndedUtc=DateTime.UtcNow;DialogueStorage.SaveHistory(run);activeDialogueHistory=null;RefreshDialogueHistory();}
    }
}
