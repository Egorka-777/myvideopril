using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class TelegramPanel {
        Panel dialogueBody;
        CheckedListBox dialogueAccounts;
        DataGridView dialogueRoles, dialoguePreview;
        TextBox dialogueTarget, dialogueTopic, dialogueFacts, dialogueModel, dialogueLanguage;
        NumericUpDown dialogueCount, dialogueDelayMin, dialogueDelayMax;
        CheckBox dialogueApproval, dialogueStartEnabled, dialogueHoursEnabled;
        TextBox dialoguePerAccountHour, dialoguePerAccountRun, dialoguePerTargetRun, dialoguePresetName;
        DateTimePicker dialogueStartLocal;
        NumericUpDown dialogueHourStart, dialogueHourEnd;
        ComboBox dialoguePresets;
        TelegramDialogueStorage dialogueStorage;
        List<TelegramDialoguePreset> dialoguePresetItems=new List<TelegramDialoguePreset>();
        Button dialogueGenerate, dialogueStart, dialogueCancel;
        CancellationTokenSource dialogueCancelSource;
        bool dialogueRefreshing;
        string dialogueAccountSignature="";
        readonly Dictionary<string,string> dialogueRoleValues=new Dictionary<string,string>();

        void BuildDialoguePage(Panel page) {
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(12),Margin=Padding.Empty};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,94));
            page.Controls.Add(layout);
            dialogueBody=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty};layout.Controls.Add(dialogueBody,0,0);
            var body=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Margin=Padding.Empty,Padding=new Padding(0,0,18,8)};
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));dialogueBody.Controls.Add(body);
            void row(Control control,int height){int index=body.RowCount++;body.RowStyles.Add(new RowStyle(SizeType.Absolute,height));control.Dock=DockStyle.Fill;body.Controls.Add(control,0,index);}
            Label label(string text){return new Label{Text=text,ForeColor=Theme.TextSecondary,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};}
            TextBox field(string title,string value,int height=36,bool multiline=false){row(label(title),26);var box=Theme.MakeSearchBox();box.Text=value;box.Multiline=multiline;if(multiline){box.AcceptsReturn=true;box.ScrollBars=ScrollBars.Vertical;}row(box,height);box.TextChanged+=(s,e)=>InvalidateDialogueApproval();return box;}
            row(new Label{Name="dialogueHeading",Text="Нейрочатинг",Font=Theme.FontCardTitle,TextAlign=ContentAlignment.MiddleLeft},36);
            dialogueStorage=new TelegramDialogueStorage(store.Root);
            row(new Label{Name="dialoguePresetLabel",Text="Пресеты · загрузка ничего не отправляет",ForeColor=Theme.TextSecondary,TextAlign=ContentAlignment.MiddleLeft},26);
            var presetBar=new FlowLayoutPanel{Name="dialoguePresetBar",WrapContents=true};
            dialoguePresets=new ComboBox{Name="dialoguePresets",DropDownStyle=ComboBoxStyle.DropDownList,Width=210};presetBar.Controls.Add(dialoguePresets);
            dialoguePresetName=new TextBox{Name="dialoguePresetName",Width=190};presetBar.Controls.Add(dialoguePresetName);
            dialoguePresets.SelectedIndexChanged+=(s,e)=>{var preset=dialoguePresets.SelectedItem as TelegramDialoguePreset;if(preset!=null)dialoguePresetName.Text=preset.Name;};
            var presetSave=Theme.MakeButton("Сохранить",ghost:true,action:SaveDialoguePreset);presetSave.Name="dialoguePresetSave";presetBar.Controls.Add(presetSave);
            var presetLoad=Theme.MakeButton("Загрузить",ghost:true,action:LoadDialoguePreset);presetLoad.Name="dialoguePresetLoad";presetBar.Controls.Add(presetLoad);
            var presetDelete=Theme.MakeButton("Удалить",ghost:true,action:DeleteDialoguePreset);presetDelete.Name="dialoguePresetDelete";presetBar.Controls.Add(presetDelete);row(presetBar,76);
            row(label("Сначала создайте и проверьте сценарий. Каждое сообщение помечается [ИИ-диалог]. Генерация ничего не отправляет."),42);
            dialogueTarget=field("Целевой чат, которым вы управляете","https://t.me/+eT4x_vqimZoxNDNh");
            row(label("Участники · отметьте аккаунты явно"),28);
            var participantBar=new FlowLayoutPanel{WrapContents=false};
            participantBar.Controls.Add(Theme.MakeButton("Выбрать все",ghost:true,action:()=>{for(int i=0;i<dialogueAccounts.Items.Count;i++)dialogueAccounts.SetItemChecked(i,true);}));
            participantBar.Controls.Add(Theme.MakeButton("Снять выбор",ghost:true,action:()=>{for(int i=0;i<dialogueAccounts.Items.Count;i++)dialogueAccounts.SetItemChecked(i,false);}));row(participantBar,40);
            dialogueAccounts=new CheckedListBox{CheckOnClick=true,DisplayMember="name",BackColor=Theme.Card,ForeColor=Theme.TextPrimary,BorderStyle=BorderStyle.FixedSingle,IntegralHeight=false};
            dialogueAccounts.ItemCheck+=(s,e)=>{if(!dialogueRefreshing)UpdateDialogueParticipants(e.Index,e.NewValue==CheckState.Checked);};row(dialogueAccounts,132);
            row(label("Роли выбранных участников · редактируются перед генерацией"),28);
            dialogueRoles=DialogueGrid();dialogueRoles.ReadOnly=false;
            dialogueRoles.Columns.Add(new DataGridViewTextBoxColumn{Name="account",HeaderText="Аккаунт",ReadOnly=true,FillWeight=35});
            dialogueRoles.Columns.Add(new DataGridViewTextBoxColumn{Name="role",HeaderText="Роль / манера общения",FillWeight=65});
            dialogueRoles.CellValueChanged+=(s,e)=>{if(!dialogueRefreshing&&e.RowIndex>=0){var r=dialogueRoles.Rows[e.RowIndex];dialogueRoleValues[(string)r.Tag]=Convert.ToString(r.Cells["role"].Value);InvalidateDialogueApproval();}};row(dialogueRoles,132);
            dialogueTopic=field("Тема","Обсуждение Creovix и BinoDex AI: задачи сервисов и вопросы перед использованием");
            dialogueFacts=field("Сведения из публичных описаний · не независимая проверка. Не придумывайте отзывы и результаты.","Creovix — https://creovix.app. На сайте заявлены генерация изображений и видео, озвучка, ИИ-чат и карусели; оплата за действия без подписки.\r\nBinoDex AI — https://t.me/BinoDexAI_bot. В описании: торговый ИИ-бот с сигналами 24/7. Не является инвестиционной рекомендацией. Результат не гарантирован.\r\nФункции внутри аккаунта, качество, цены и результаты не проверены. Личного опыта использования нет.",120,true);
            dialogueModel=field("Модель OpenRouter","google/gemini-2.5-flash");
            dialogueLanguage=field("Язык диалога","Русский");
            var options=new FlowLayoutPanel{WrapContents=true};
            NumericUpDown number(string title,int minimum,int maximum,int value){options.Controls.Add(new Label{Text=title,AutoSize=true,ForeColor=Theme.TextSecondary,Margin=new Padding(0,8,8,0)});var n=new NumericUpDown{Minimum=minimum,Maximum=maximum,Value=value,Width=74,Margin=new Padding(0,4,18,0)};options.Controls.Add(n);n.ValueChanged+=(s,e)=>InvalidateDialogueApproval();return n;}
            dialogueCount=number("Сообщений",2,100,10);dialogueDelayMin=number("Пауза от, сек",0,3600,10);dialogueDelayMax=number("до, сек",0,3600,30);row(options,72);
            row(new Label{Name="dialogueLimitsLabel",Text="Лимиты исходящих: пусто / 0 — без лимита. Стикер = текст + стикер (2 сообщения). Лимиты запуска относятся к текущему запуску.",ForeColor=Theme.TextSecondary,TextAlign=ContentAlignment.MiddleLeft},42);
            var limits=new FlowLayoutPanel{Name="dialogueLimits",WrapContents=true};
            TextBox limit(string title,string name){limits.Controls.Add(new Label{Name=name+"Label",Text=title,AutoSize=true,ForeColor=Theme.TextSecondary,Margin=new Padding(0,7,6,0)});var box=new TextBox{Name=name,Width=72};box.TextChanged+=(s,e)=>InvalidateDialogueApproval();limits.Controls.Add(box);return box;}
            dialoguePerAccountHour=limit("На аккаунт / час","dialoguePerAccountHour");dialoguePerAccountRun=limit("На аккаунт / запуск","dialoguePerAccountRun");dialoguePerTargetRun=limit("На чат / запуск","dialoguePerTargetRun");row(limits,64);
            var schedule=new FlowLayoutPanel{Name="dialogueSchedule",WrapContents=true};
            dialogueStartEnabled=new CheckBox{Name="dialogueStartEnabled",Text="Отложенный старт (местное время)",AutoSize=true};schedule.Controls.Add(dialogueStartEnabled);
            dialogueStartLocal=new DateTimePicker{Name="dialogueStartLocal",Format=DateTimePickerFormat.Custom,CustomFormat="dd.MM.yyyy HH:mm",Width=160,Enabled=false};schedule.Controls.Add(dialogueStartLocal);
            dialogueStartEnabled.CheckedChanged+=(s,e)=>{dialogueStartLocal.Enabled=dialogueStartEnabled.Checked;InvalidateDialogueApproval();};dialogueStartLocal.ValueChanged+=(s,e)=>InvalidateDialogueApproval();
            dialogueHoursEnabled=new CheckBox{Name="dialogueHoursEnabled",Text="Ежедневно, местные часы",AutoSize=true};schedule.Controls.Add(dialogueHoursEnabled);
            dialogueHourStart=new NumericUpDown{Name="dialogueHourStart",Minimum=0,Maximum=23,Width=52,Enabled=false};schedule.Controls.Add(dialogueHourStart);
            schedule.Controls.Add(new Label{Name="dialogueHoursSeparator",Text="—",AutoSize=true});
            dialogueHourEnd=new NumericUpDown{Name="dialogueHourEnd",Minimum=1,Maximum=24,Value=24,Width=52,Enabled=false};schedule.Controls.Add(dialogueHourEnd);
            dialogueHoursEnabled.CheckedChanged+=(s,e)=>{dialogueHourStart.Enabled=dialogueHourEnd.Enabled=dialogueHoursEnabled.Checked;InvalidateDialogueApproval();};dialogueHourStart.ValueChanged+=(s,e)=>InvalidateDialogueApproval();dialogueHourEnd.ValueChanged+=(s,e)=>InvalidateDialogueApproval();row(schedule,76);
            row(new Label{Name="dialogueScheduleHint",Text="Расписание работает только пока приложение открыто. Окончание часа не включается; переход через полночь поддерживается. Начало и конец не должны совпадать.",ForeColor=Theme.TextSecondary,TextAlign=ContentAlignment.MiddleLeft},42);
            row(label("Предпросмотр · текст обязателен; ответ: 0 — без ответа, 1…N — предыдущая реплика"),30);
            dialoguePreview=DialogueGrid();dialoguePreview.ReadOnly=false;dialoguePreview.RowHeadersVisible=true;dialoguePreview.RowHeadersWidth=52;
            dialoguePreview.Columns.Add(new DataGridViewComboBoxColumn{Name="account_id",HeaderText="Аккаунт",DisplayMember="name",ValueMember="id",FillWeight=28,FlatStyle=FlatStyle.Flat});
            dialoguePreview.Columns.Add(new DataGridViewTextBoxColumn{Name="text",HeaderText="Сообщение · [ИИ-диалог]",FillWeight=60});
            dialoguePreview.Columns.Add(new DataGridViewTextBoxColumn{Name="reply_to",HeaderText="Ответ №",FillWeight=12});
            dialoguePreview.Columns.Add(new DataGridViewTextBoxColumn{Name="attachment",HeaderText="Вложение",ReadOnly=true,FillWeight=30});
            dialoguePreview.Columns["text"].DefaultCellStyle.WrapMode=DataGridViewTriState.True;
            dialoguePreview.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
            dialoguePreview.DefaultCellStyle.NullValue="";
            dialoguePreview.CellValueChanged+=(s,e)=>{if(!dialogueRefreshing)InvalidateDialogueApproval();};
            dialoguePreview.CurrentCellDirtyStateChanged+=(s,e)=>{if(dialoguePreview.IsCurrentCellDirty){InvalidateDialogueApproval();if(dialoguePreview.CurrentCell is DataGridViewComboBoxCell)dialoguePreview.CommitEdit(DataGridViewDataErrorContexts.Commit);}};
            dialoguePreview.CellBeginEdit+=(s,e)=>InvalidateDialogueApproval();
            dialoguePreview.DataError+=(s,e)=>{e.ThrowException=false;InvalidateDialogueApproval();status.Text="Проверьте аккаунт и номер ответа в сценарии.";};row(dialoguePreview,330);
            var attachmentBar=new FlowLayoutPanel{Name="dialogueAttachmentBar",WrapContents=true};
            var attachMedia=Theme.MakeButton("Фото / видео",ghost:true,action:()=>AttachDialogueFile(false));attachMedia.Name="dialogueAttachMedia";attachmentBar.Controls.Add(attachMedia);
            var attachSticker=Theme.MakeButton("Стикер (+ сообщение)",ghost:true,action:()=>AttachDialogueFile(true));attachSticker.Name="dialogueAttachSticker";attachmentBar.Controls.Add(attachSticker);
            var clearAttachment=Theme.MakeButton("Убрать вложение",ghost:true,action:ClearDialogueAttachment);clearAttachment.Name="dialogueClearAttachment";attachmentBar.Controls.Add(clearAttachment);row(attachmentBar,48);
            row(new Label{Name="dialogueAttachmentHint",Text="Фото / видео: текст станет подписью [ИИ-диалог]. Стикер: отдельный маркированный текст и затем стикер (2 исходящих).",ForeColor=Theme.TextSecondary,TextAlign=ContentAlignment.MiddleLeft},42);
            var footer=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};footer.RowStyles.Add(new RowStyle(SizeType.Absolute,38));footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.Controls.Add(footer,0,1);
            dialogueApproval=new CheckBox{Text="Сценарий проверен; участники помечены как ИИ",Dock=DockStyle.Fill,AutoSize=true,ForeColor=Theme.TextPrimary};
            dialogueApproval.CheckedChanged+=(s,e)=>UpdateDialogueActions();footer.Controls.Add(dialogueApproval,0,0);
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};footer.Controls.Add(buttons,0,1);
            dialogueGenerate=Theme.MakeButton("Создать сценарий",accent:true,action:async()=>await GenerateDialogue());buttons.Controls.Add(dialogueGenerate);
            dialogueCancel=Theme.MakeButton("Отмена генерации",ghost:true,action:()=>dialogueCancelSource?.Cancel());dialogueCancel.Enabled=false;buttons.Controls.Add(dialogueCancel);
            dialogueStart=Theme.MakeButton("Запустить диалог",accent:true,action:async()=>await StartDialogue());dialogueStart.Enabled=false;buttons.Controls.Add(dialogueStart);
            try{RefreshDialoguePresets();}catch(Exception e){Failure(e);}
        }

        void RefreshDialoguePresets(string selectedId=null) {
            dialoguePresetItems=dialogueStorage.ListPresets();dialoguePresets.DataSource=null;dialoguePresets.DataSource=dialoguePresetItems;
            if(selectedId!=null)dialoguePresets.SelectedItem=dialoguePresetItems.FirstOrDefault(p=>p.Id==selectedId);
        }
        static int DialogueLimit(TextBox box) {
            if(string.IsNullOrWhiteSpace(box.Text))return 0;
            int value;if(!int.TryParse(box.Text.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out value)||value<0)throw new InvalidOperationException("Лимиты должны быть целыми неотрицательными числами; пусто / 0 — без лимита.");return value;
        }
        void ValidateDialogueSchedule() {
            if(dialogueHoursEnabled.Checked&&dialogueHourStart.Value==dialogueHourEnd.Value)throw new InvalidOperationException("Начало и конец ежедневного интервала не должны совпадать.");
        }
        Dictionary<string,object> DialogueScheduleJob() {
            ValidateDialogueSchedule();
            return new Dictionary<string,object>{{"start_utc",dialogueStartEnabled.Checked?DateTime.SpecifyKind(dialogueStartLocal.Value,DateTimeKind.Local).ToUniversalTime().ToString("o",CultureInfo.InvariantCulture):""},{"enabled",dialogueStartEnabled.Checked||dialogueHoursEnabled.Checked},{"hour_start",dialogueHoursEnabled.Checked?(int)dialogueHourStart.Value:0},{"hour_end",dialogueHoursEnabled.Checked?(int)dialogueHourEnd.Value:24}};
        }
        List<TelegramDialogueStep> ReadDialogueSteps() {
            dialoguePreview.EndEdit();var steps=new List<TelegramDialogueStep>();
            foreach(DataGridViewRow row in dialoguePreview.Rows){int reply;if(!int.TryParse(Convert.ToString(row.Cells["reply_to"].Value),out reply))throw new InvalidOperationException("Укажите целый номер ответа; 0 — без ответа.");var attachment=row.Tag as TelegramDialogueStep;steps.Add(new TelegramDialogueStep{account_id=Convert.ToString(row.Cells["account_id"].Value),text=Convert.ToString(row.Cells["text"].Value),reply_to=reply,media_path=attachment?.media_path??"",sticker_path=attachment?.sticker_path??""});}return steps;
        }
        void FillDialoguePreview(IEnumerable<TelegramDialogueStep> steps) {
            dialogueRefreshing=true;
            try{dialoguePreview.Rows.Clear();foreach(var step in steps){int i=dialoguePreview.Rows.Add(step.account_id,DialogueVisibleText(step.text),step.reply_to);var row=dialoguePreview.Rows[i];row.HeaderCell.Value=(i+1).ToString();row.Tag=new TelegramDialogueStep{media_path=step.media_path??"",sticker_path=step.sticker_path??""};UpdateDialogueAttachmentCell(row);}}finally{dialogueRefreshing=false;}
        }
        void UpdateDialogueAttachmentCell(DataGridViewRow row) {
            var step=row.Tag as TelegramDialogueStep;string media=step?.media_path??"",sticker=step?.sticker_path??"";
            row.Cells["attachment"].Value=media.Length>0?"Медиа: "+Path.GetFileName(media):sticker.Length>0?"Стикер: "+Path.GetFileName(sticker):"";row.Cells["attachment"].ToolTipText=media.Length>0?media:sticker;
        }
        void AttachDialogueFile(bool sticker) {
            if(dialoguePreview.CurrentRow==null){status.Text="Выберите реплику в предпросмотре.";return;}
            using(var dialog=new OpenFileDialog{Title=sticker?"Стикер для выбранной реплики":"Фото / видео для выбранной реплики",Filter=sticker?"Стикеры|*.webp;*.tgs;*.webm":"Фото / видео|*.jpg;*.jpeg;*.png;*.mp4;*.mov",CheckFileExists=true,Multiselect=false}){
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                var row=dialoguePreview.CurrentRow;row.Tag=new TelegramDialogueStep{media_path=sticker?"":dialog.FileName,sticker_path=sticker?dialog.FileName:""};UpdateDialogueAttachmentCell(row);InvalidateDialogueApproval();
            }
        }
        void ClearDialogueAttachment() {
            var row=dialoguePreview.CurrentRow;if(row==null)return;row.Tag=null;UpdateDialogueAttachmentCell(row);InvalidateDialogueApproval();
        }
        int ValidateDialogueOutgoing(IList<TelegramDialogueStep> steps,int perAccountRun,int perTargetRun) {
            var counts=new Dictionary<string,int>();int total=0;
            for(int i=0;i<steps.Count;i++){
                var step=steps[i];string media=step.media_path??"",sticker=step.sticker_path??"";
                if(media.Length>0&&sticker.Length>0)throw new InvalidOperationException("Реплика "+(i+1)+": можно выбрать только медиа или стикер.");
                string path=media.Length>0?media:sticker;
                if(path.Length>0){var extensions=media.Length>0?new[]{".jpg",".jpeg",".png",".mp4",".mov"}:new[]{".webp",".tgs",".webm"};if(!extensions.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase))throw new InvalidOperationException("Реплика "+(i+1)+": неподдерживаемый тип вложения.");if(!File.Exists(path))throw new InvalidOperationException("Реплика "+(i+1)+": файл не найден: "+path);}
                int count=sticker.Length>0?2:1;total+=count;if(!counts.ContainsKey(step.account_id))counts[step.account_id]=0;counts[step.account_id]+=count;
            }
            if(perAccountRun>0&&counts.Any(p=>p.Value>perAccountRun))throw new InvalidOperationException("Сценарий превышает лимит исходящих на аккаунт за текущий запуск (стикер считается дополнительным сообщением).");
            if(perTargetRun>0&&total>perTargetRun)throw new InvalidOperationException("Сценарий превышает лимит исходящих на чат за текущий запуск (стикер считается дополнительным сообщением).");return total;
        }
        void SaveDialoguePreset() {
            try{
                dialogueRoles.EndEdit();string name=dialoguePresetName.Text.Trim();if(name.Length==0)throw new InvalidOperationException("Введите имя пресета.");
                var accounts=DialogueSelectedAccounts();var steps=ReadDialogueSteps();if(steps.Count>0)ValidateDialogueSteps(steps,accounts,(int)dialogueCount.Value);ValidateDialogueSchedule();
                var selected=dialoguePresets.SelectedItem as TelegramDialoguePreset;
                var preset=new TelegramDialoguePreset{Id=selected!=null&&selected.Name==name?selected.Id:Guid.NewGuid().ToString("N"),Name=name,Target=dialogueTarget.Text,Topic=dialogueTopic.Text,Facts=dialogueFacts.Text,Model=dialogueModel.Text,Language=dialogueLanguage.Text,Participants=DialogueParticipants(accounts),Steps=steps,Count=(int)dialogueCount.Value,DelayMin=(int)dialogueDelayMin.Value,DelayMax=(int)dialogueDelayMax.Value,PerAccountHour=DialogueLimit(dialoguePerAccountHour),PerAccountRun=DialogueLimit(dialoguePerAccountRun),PerTargetRun=DialogueLimit(dialoguePerTargetRun),StartEnabled=dialogueStartEnabled.Checked,HoursEnabled=dialogueHoursEnabled.Checked,StartLocal=dialogueStartLocal.Value,HourStart=(int)dialogueHourStart.Value,HourEnd=(int)dialogueHourEnd.Value};
                dialogueStorage.SavePreset(preset);RefreshDialoguePresets(preset.Id);status.Text="Пресет сохранён. Сообщения не отправлялись.";
            }catch(Exception e){Failure(e);}
        }
        void LoadDialoguePreset() {
            try{
                var preset=dialoguePresets.SelectedItem as TelegramDialoguePreset;if(preset==null)throw new InvalidOperationException("Выберите пресет.");
                InvalidateDialogueApproval();var available=store.Load().accounts;var participants=preset.Participants??new List<TelegramDialogueParticipant>();
                var missing=participants.Where(p=>!available.Any(a=>a.id==p.id)).Select(p=>p.name+" ("+p.id+")").ToList();
                if(missing.Count>0)throw new InvalidOperationException("Пресет не загружен: отсутствуют аккаунты: "+string.Join(", ",missing)+". Подмена участников не выполняется.");
                if(preset.Count<2||preset.Count>100||preset.DelayMin<0||preset.DelayMax>3600||preset.DelayMin>preset.DelayMax||preset.PerAccountHour<0||preset.PerAccountRun<0||preset.PerTargetRun<0||preset.HourStart<0||preset.HourStart>23||preset.HourEnd<1||preset.HourEnd>24||(preset.HoursEnabled&&preset.HourStart==preset.HourEnd)||(preset.StartEnabled&&(preset.StartLocal<dialogueStartLocal.MinDate||preset.StartLocal>dialogueStartLocal.MaxDate)))throw new InvalidOperationException("Пресет содержит недопустимые параметры.");
                var steps=preset.Steps??new List<TelegramDialogueStep>();if(steps.Count>0)TelegramDialogue.Validate(steps,participants,preset.Count);
                RefreshDialogueAccounts(available);dialogueRefreshing=true;
                try{for(int i=0;i<dialogueAccounts.Items.Count;i++)dialogueAccounts.SetItemChecked(i,participants.Any(p=>p.id==((TelegramAccount)dialogueAccounts.Items[i]).id));foreach(var participant in participants)dialogueRoleValues[participant.id]=participant.role;}finally{dialogueRefreshing=false;}
                // Account selection rebuilds and clears the preview, so restore the steps last.
                UpdateDialogueParticipants();
                dialoguePresetName.Text=preset.Name;dialogueTarget.Text=preset.Target;dialogueTopic.Text=preset.Topic;dialogueFacts.Text=preset.Facts;dialogueModel.Text=preset.Model;dialogueLanguage.Text=preset.Language;
                dialogueCount.Value=preset.Count;dialogueDelayMin.Value=preset.DelayMin;dialogueDelayMax.Value=preset.DelayMax;dialoguePerAccountHour.Text=preset.PerAccountHour.ToString(CultureInfo.InvariantCulture);dialoguePerAccountRun.Text=preset.PerAccountRun.ToString(CultureInfo.InvariantCulture);dialoguePerTargetRun.Text=preset.PerTargetRun.ToString(CultureInfo.InvariantCulture);
                dialogueStartEnabled.Checked=preset.StartEnabled;if(preset.StartLocal>=dialogueStartLocal.MinDate&&preset.StartLocal<=dialogueStartLocal.MaxDate)dialogueStartLocal.Value=preset.StartLocal;dialogueHoursEnabled.Checked=preset.HoursEnabled;dialogueHourStart.Value=preset.HourStart;dialogueHourEnd.Value=preset.HourEnd;
                FillDialoguePreview(steps);InvalidateDialogueApproval();status.Text="Пресет загружен. Проверьте сценарий и вложения; запуск не выполнялся.";
            }catch(Exception e){Failure(e);}
        }
        void DeleteDialoguePreset() {
            try{var preset=dialoguePresets.SelectedItem as TelegramDialoguePreset;if(preset==null)throw new InvalidOperationException("Выберите пресет.");dialogueStorage.DeletePreset(preset.Id);RefreshDialoguePresets();dialoguePresetName.Clear();status.Text="Пресет удалён.";}catch(Exception e){Failure(e);}
        }

        DataGridView DialogueGrid() {
            var result=new DataGridView{AllowUserToAddRows=false,AllowUserToDeleteRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.CellSelect,MultiSelect=false,RowHeadersVisible=false};
            Theme.StyleGrid(result);return result;
        }
        void InvalidateDialogueApproval(){if(dialogueApproval!=null)dialogueApproval.Checked=false;UpdateDialogueActions();}
        void UpdateDialogueActions(){if(dialogueGenerate==null)return;dialogueGenerate.Enabled=!busy&&!unavailable;dialogueStart.Enabled=!busy&&!unavailable&&dialogueApproval.Checked&&dialoguePreview.Rows.Count>0;dialogueApproval.Enabled=!busy;dialogueCancel.Enabled=dialogueCancelSource!=null;}
        void RefreshDialogueAccounts(List<TelegramAccount> accounts) {
            if(dialogueAccounts==null||busy)return;
            string signature=string.Join("\n",accounts.Select(a=>a.id+"\t"+a.name));if(signature==dialogueAccountSignature)return;
            var selected=new HashSet<string>(dialogueAccounts.CheckedItems.Cast<TelegramAccount>().Select(a=>a.id));
            dialogueRefreshing=true;
            try{dialogueAccounts.Items.Clear();foreach(var account in accounts)dialogueAccounts.Items.Add(account,selected.Contains(account.id));dialogueAccountSignature=signature;}finally{dialogueRefreshing=false;}
            UpdateDialogueParticipants();
        }
        void UpdateDialogueParticipants(int changedIndex=-1,bool isChecked=false) {
            dialogueRoles.EndEdit();
            var selected=new List<TelegramAccount>();for(int i=0;i<dialogueAccounts.Items.Count;i++)if(i==changedIndex?isChecked:dialogueAccounts.GetItemChecked(i))selected.Add((TelegramAccount)dialogueAccounts.Items[i]);
            dialogueRefreshing=true;
            try{
                dialogueRoles.Rows.Clear();foreach(var a in selected){string role;if(!dialogueRoleValues.TryGetValue(a.id,out role)){role="ИИ-персонаж: задаёт вопросы и уточняет только подтверждённые факты";dialogueRoleValues[a.id]=role;}int i=dialogueRoles.Rows.Add(a.name,role);dialogueRoles.Rows[i].Tag=a.id;}
                dialoguePreview.Rows.Clear();((DataGridViewComboBoxColumn)dialoguePreview.Columns["account_id"]).DataSource=selected;
            }finally{dialogueRefreshing=false;}
            InvalidateDialogueApproval();
        }
        List<TelegramAccount> DialogueSelectedAccounts() {
            var ids=new HashSet<string>(dialogueAccounts.CheckedItems.Cast<TelegramAccount>().Select(a=>a.id));
            if(ids.Count<2)throw new InvalidOperationException("Отметьте минимум два аккаунта для ИИ-диалога.");
            if(ids.Count>(int)dialogueCount.Value)throw new InvalidOperationException("Число сообщений должно быть не меньше числа участников.");
            var selected=store.Load().accounts.Where(a=>ids.Contains(a.id)).ToList();
            if(selected.Count!=ids.Count)throw new InvalidOperationException("Список аккаунтов изменился. Обновите выбор участников.");
            return selected;
        }
        void SetTelegramBusyUI(bool active) {
            foreach(var b in workButtons)b.Enabled=!active;
            actions.Enabled=!active&&!unavailable;stop.Enabled=active;
            grid.Enabled=!active;search.Enabled=!active;proxyGrid.Enabled=!active;pages[2].Enabled=!active;
            if(dialogueBody!=null)dialogueBody.Enabled=!active;
            UpdateDialogueActions();
        }
        async Task GenerateDialogue() {
            if(busy||unavailable)return;
            CancellationTokenSource cancel=null;
            try{
                dialogueRoles.EndEdit();var accounts=DialogueSelectedAccounts();
                if(string.IsNullOrWhiteSpace(dialogueTopic.Text)||string.IsNullOrWhiteSpace(dialogueModel.Text)||string.IsNullOrWhiteSpace(dialogueLanguage.Text))throw new InvalidOperationException("Укажите тему, модель и язык.");
                var prefs=getPreferences?.Invoke()??Store.Load();string key=WindowsSupport.Unprotect(prefs.ProtectedOpenRouterKey);
                if(string.IsNullOrWhiteSpace(key))throw new InvalidOperationException("Укажите ключ OpenRouter в настройках приложения.");
                var participants=DialogueParticipants(accounts);
                string topic=dialogueTopic.Text.Trim()+"\nЯзык всех сообщений: "+dialogueLanguage.Text.Trim()+". Это открытая ИИ-симуляция в собственном тестовом чате, не независимые отзывы пользователей. Не выдумывай личный опыт, доходность или неподтверждённые свойства продуктов.";
                InvalidateDialogueApproval();dialoguePreview.Rows.Clear();
                cancel=new CancellationTokenSource();dialogueCancelSource=cancel;busy=true;SetTelegramBusyUI(true);status.Text="Генерация ИИ-сценария… Ничего не отправляется в Telegram.";
                var steps=await TelegramDialogue.GenerateAsync(key,WindowsSupport.Unprotect(prefs.ProtectedWarmupHttpProxy),dialogueModel.Text.Trim(),topic,dialogueFacts.Text.Trim(),participants,(int)dialogueCount.Value,cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                ValidateDialogueSteps(steps,accounts,(int)dialogueCount.Value);
                FillDialoguePreview(steps);
                status.Text="Сценарий готов. Проверьте сообщения, участников и ответы; затем подтвердите запуск.";
            }catch(OperationCanceledException){status.Text="Генерация отменена. Сообщения не отправлялись.";}
            catch(Exception e){Failure(e);}
            finally{if(cancel!=null){dialogueCancelSource=null;cancel.Dispose();busy=false;SetTelegramBusyUI(false);RefreshData();}}
        }
        static string DialogueVisibleText(string text){text=(text??"").Trim();return text.StartsWith("[ИИ-диалог]",StringComparison.Ordinal)?text:"[ИИ-диалог] "+text;}
        List<TelegramDialogueParticipant> DialogueParticipants(List<TelegramAccount> accounts){return accounts.Select(a=>new TelegramDialogueParticipant{id=a.id,name=a.name,role=dialogueRoleValues.ContainsKey(a.id)?dialogueRoleValues[a.id]:""}).ToList();}
        void ValidateDialogueSteps(IList<TelegramDialogueStep> steps,List<TelegramAccount> accounts,int expectedCount) {
            if(steps==null||steps.Count<2||steps.Count>100||steps.Count!=expectedCount)throw new InvalidOperationException("Сценарий должен содержать выбранное число сообщений (2–100).");
            var ids=new HashSet<string>(accounts.Select(a=>a.id));
            for(int i=0;i<steps.Count;i++){
                var step=steps[i];if(step==null||!ids.Contains(step.account_id))throw new InvalidOperationException("Сообщение "+(i+1)+": выберите известного участника.");
                string text=(step.text??"").Trim();if(text.StartsWith("[ИИ-диалог]",StringComparison.Ordinal))text=text.Substring("[ИИ-диалог]".Length).Trim();
                if(text.Length==0)throw new InvalidOperationException("Сообщение "+(i+1)+": текст пуст.");
                if(step.reply_to<0||step.reply_to>i)throw new InvalidOperationException("Сообщение "+(i+1)+": ответ может ссылаться только на предыдущее сообщение (0 — без ответа).");
            }
            TelegramDialogue.Validate(steps,DialogueParticipants(accounts),expectedCount);
        }
        async Task StartDialogue() {
            try{
                if(busy||unavailable)return;
                dialoguePreview.EndEdit();dialogueRoles.EndEdit();
                if(!dialogueApproval.Checked)throw new InvalidOperationException("Проверьте сценарий и подтвердите маркировку участников как ИИ.");
                var accounts=DialogueSelectedAccounts();string destination=dialogueTarget.Text.Trim();
                if(!System.Text.RegularExpressions.Regex.IsMatch(destination,@"^https://t\.me/(?:\+|joinchat/)[A-Za-z0-9_-]+$"))throw new InvalidOperationException("Укажите пригласительную ссылку собственного тестового чата: https://t.me/+…");
                if(dialogueDelayMin.Value>dialogueDelayMax.Value)throw new InvalidOperationException("Минимальная пауза не должна превышать максимальную.");
                var steps=ReadDialogueSteps();
                ValidateDialogueSteps(steps,accounts,(int)dialogueCount.Value);
                int perAccountHour=DialogueLimit(dialoguePerAccountHour),perAccountRun=DialogueLimit(dialoguePerAccountRun),perTargetRun=DialogueLimit(dialoguePerTargetRun);
                ValidateDialogueSchedule();int outgoing=ValidateDialogueOutgoing(steps,perAccountRun,perTargetRun);
                // Preview shows the visible marker; the worker unconditionally adds it when sending.
                foreach(var step in steps){step.text=step.text.Trim();if(step.text.StartsWith("[ИИ-диалог]",StringComparison.Ordinal))step.text=step.text.Substring("[ИИ-диалог]".Length).Trim();}
                string confirmation="Запустить открытый ИИ-диалог в своём тестовом чате?\n\nЧат: "+destination+"\nУчастники: "+string.Join(", ",accounts.Select(a=>a.name))+"\nПроверок / возможных вступлений в чат: "+accounts.Count+"\nРеплик: "+steps.Count+"; исходящих (включая стикеры): "+outgoing+"\nЛимиты: аккаунт/час "+perAccountHour+", аккаунт/запуск "+perAccountRun+", чат/запуск "+perTargetRun+" (0 — без лимита)\nСтарт: "+(dialogueStartEnabled.Checked?dialogueStartLocal.Value.ToString("dd.MM.yyyy HH:mm"):"сейчас")+"\nЕжедневные часы: "+(dialogueHoursEnabled.Checked?dialogueHourStart.Value+"–"+dialogueHourEnd.Value:"без ограничения")+" (местное время; только пока приложение открыто)\nПауза: "+dialogueDelayMin.Value+"–"+dialogueDelayMax.Value+" сек\n\nВсе сообщения помечены [ИИ-диалог]. Участники должны быть явно обозначены как ИИ. Будут использованы реальные Telegram-аккаунты.";
                if(MessageBox.Show(this,confirmation,"Подтвердить ИИ-диалог",MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK)return;
                InvalidateDialogueApproval();
                await Run(new Dictionary<string,object>{{"action","ai_dialogue"},{"accounts",accounts},{"target",destination},{"steps",steps},{"delay_min",(int)dialogueDelayMin.Value},{"delay_max",(int)dialogueDelayMax.Value},{"limits",new Dictionary<string,object>{{"per_account_hour",perAccountHour},{"per_account_run",perAccountRun},{"per_target_run",perTargetRun}}},{"schedule",DialogueScheduleJob()}});
            }catch(Exception e){Failure(e);}
        }
    }
}
