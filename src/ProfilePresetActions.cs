using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    internal static class ProfilePresetActions {
        public static T ChooseAccount<T>(Control owner,string network,IEnumerable<T> accounts,Func<T,string> label,T current=null) where T:class {
            var choices=accounts.Where(a=>a!=null).ToList();
            if(choices.Count==0){MessageBox.Show(owner,"Нет локальных аккаунтов "+network+". "+(network=="Instagram"||network=="Threads"?"Добавьте профиль YouTube с Dolphin ID.":"Добавьте аккаунт в этом разделе."),"Оформить профиль",MessageBoxButtons.OK,MessageBoxIcon.Information);return null;}
            using(var dialog=new Form{Text="Оформить профиль · "+network,Name="ProfileAccountPicker",ClientSize=new Size(580,184),MinimumSize=new Size(480,220),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false}){
                Theme.StyleModal(dialog);
                var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=3};
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute,34));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
                layout.Controls.Add(new Label{Text="Выберите аккаунт → пресет и аватар → Применить",Dock=DockStyle.Fill,ForeColor=Theme.TextSecondary},0,0);
                var list=Theme.MakeCombo(choices.Select(label).ToArray());list.Name="ProfilePresetAccount";list.Dock=DockStyle.Top;
                int selected=current==null?-1:choices.IndexOf(current);list.SelectedIndex=selected>=0?selected:0;layout.Controls.Add(list,0,1);
                var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
                var cancel=Theme.MakeButton("Отмена",ghost:true);cancel.DialogResult=DialogResult.Cancel;
                var next=Theme.MakeButton("Выбрать пресет",accent:true);next.Name="ChooseProfilePreset";next.DialogResult=DialogResult.OK;
                buttons.Controls.Add(cancel);buttons.Controls.Add(next);layout.Controls.Add(buttons,0,2);dialog.Controls.Add(layout);dialog.AcceptButton=next;dialog.CancelButton=cancel;
                return dialog.ShowDialog(owner)==DialogResult.OK?choices[list.SelectedIndex]:null;
            }
        }
        public static void OpenAndroid(Control owner,string network,string profileId,string accountName,Action<string> log){
            try{
                if(network=="Threads")throw new InvalidOperationException("Автоматическое оформление Threads пока не подключено: адаптера изменения имени, описания и фото нет. Профиль не изменён, пресет не использован.");
                if(string.IsNullOrWhiteSpace(profileId))throw new InvalidOperationException("У выбранного аккаунта нет Dolphin Profile ID.");
                ProfilePresetDialog.ShowDialog(owner,network,profileId,async preset=>{
                    if(network=="TikTok" && new System.Globalization.StringInfo(preset.Name).LengthInTextElements>30)
                        throw new InvalidOperationException("Имя TikTok — не более 30 символов. Сократите название пресета; текст не обрезан.");
                    string username=AskUsername(owner,network,accountName);
                    if(username==null)throw new OperationCanceledException("Применение отменено. Пресет не использован.");
                    await ApplyAndroid(network,profileId,username,preset,log);
                });
            }catch(Exception error){MessageBox.Show(owner,error.Message,"Пресеты профиля",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
        static Task ApplyAndroid(string network,string profileId,string username,ProfilePreset preset,Action<string> log){
            return AndroidProfileEditor.ApplyAsync(network,profileId,username,preset.Name,preset.Bio,preset.AvatarPath,log);
        }
        public static void OpenTelegram(Control owner,TelegramStore store,TelegramAccount account){
            ProfilePresetDialog.ShowDialog(owner,"Telegram",account.id,preset=>TelegramProfilePresetApply.Apply(store,account,preset.Name,preset.Bio,preset.AvatarPath,null));
        }
        static string AskUsername(IWin32Window owner,string network,string accountName){
            using(var dialog=new Form{Text="Аккаунт "+network,ClientSize=new Size(510,220),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false}){
                Theme.StyleModal(dialog);
                var text=new Label{Text="Выбран: "+accountName+"\nОткройте нужный профиль в Android. Укажите его @username для сверки перед изменениями:",Location=new Point(18,18),Size=new Size(470,80),ForeColor=Theme.TextSecondary};
                var field=Theme.MakeSearchBox();field.Name="ExpectedProfileUsername";field.Location=new Point(18,102);field.Width=470;
                var ok=Theme.MakeButton("Применить",accent:true);ok.Location=new Point(220,160);ok.DialogResult=DialogResult.OK;
                var cancel=Theme.MakeButton("Отмена",ghost:true);cancel.Location=new Point(355,160);cancel.DialogResult=DialogResult.Cancel;
                dialog.Controls.AddRange(new Control[]{text,field,ok,cancel});dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                if(dialog.ShowDialog(owner)!=DialogResult.OK)return null;
                string value=field.Text.Trim().TrimStart('@');
                if(value.Length==0)throw new InvalidOperationException("Укажите @username аккаунта; профиль не изменён.");
                return value;
            }
        }
    }
}
