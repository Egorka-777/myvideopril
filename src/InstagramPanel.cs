using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    internal sealed class InstagramPanel : UserControl {
        readonly Preferences settings;
        readonly DataGridView grid;
        readonly Label status;
        readonly Button open;
        readonly Button openAndroid;
        bool openingAndroid;
        readonly Dictionary<string,InstagramWebWindow> windows=new Dictionary<string,InstagramWebWindow>();
        bool loading;
        public InstagramPanel(Preferences settings) {
            this.settings=settings;Dock=DockStyle.Fill;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;
            var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=54,Padding=new Padding(8),WrapContents=false};
            open=Theme.MakeButton("Instagram / Threads / Meta",accent:true,action:async()=>await OpenSelected());bar.Controls.Add(open);
            openAndroid=Theme.MakeButton("Открыть Android",ghost:true,action:async()=>await OpenSelectedAndroid());openAndroid.Name="OpenInstagramAndroid";bar.Controls.Add(openAndroid);
            var profileBar=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(8,8,8,0),WrapContents=true};
            var fillProfile=Theme.MakeButton("Оформить профиль · Instagram",accent:true,action:()=>OpenProfilePresets("Instagram"));
            fillProfile.Name="FillInstagramProfile";profileBar.Controls.Add(fillProfile);
            var fillThreads=Theme.MakeButton("Оформить профиль · Threads",accent:true,action:()=>OpenProfilePresets("Threads"));
            fillThreads.Name="FillThreadsProfile";profileBar.Controls.Add(fillThreads);
            bar.Controls.Add(Theme.MakeButton("Обновить профили",ghost:true,action:RefreshProfiles));
            status=new Label{Dock=DockStyle.Bottom,Height=48,Padding=new Padding(10),Text="Профили из YouTube. Используется только назначенный профилю прокси Dolphin.",ForeColor=Theme.TextSecondary};
            grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Theme.Background,RowHeadersVisible=false,AutoGenerateColumns=false};
            Theme.StyleGrid(grid);
            grid.Columns.Add("name","Профиль YouTube");grid.Columns.Add("market","Регион");grid.Columns.Add("profile","Dolphin ID");
            grid.CellDoubleClick+=async(s,e)=>{if(e.RowIndex>=0)await OpenSelected();};
            Controls.Add(grid);Controls.Add(bar);Controls.Add(profileBar);Controls.Add(status);RefreshProfiles();
            Disposed+=(s,e)=>{foreach(var w in windows.Values.ToArray())if(!w.IsDisposed)w.Close();};
        }
        void OpenProfilePresets(string network) {
            var profiles=(settings.YouTubeChannels??new List<YouTubeChannel>()).Where(c=>c!=null&&!string.IsNullOrWhiteSpace(c.ProfileId))
                .GroupBy(c=>c.ProfileId.Trim()).Select(g=>g.First());
            var account=ProfilePresetActions.ChooseAccount(this,network,profiles,c=>c.Name+" · "+c.Market+" · Dolphin "+c.ProfileId.Trim());
            if(account!=null)ProfilePresetActions.OpenAndroid(this,network,account.ProfileId.Trim(),account.Name,line=>{if(!IsDisposed)status.Text=line;});
        }
        public void RefreshProfiles() {
            if(loading)return;
            string selected=grid.CurrentRow?.Tag as string;grid.Rows.Clear();
            foreach(var c in (settings.YouTubeChannels??new List<YouTubeChannel>()).Where(c=>c!=null&&!string.IsNullOrWhiteSpace(c.ProfileId)).GroupBy(c=>c.ProfileId.Trim()).Select(g=>g.First())){
                int i=grid.Rows.Add(c.Name,c.Market,c.ProfileId.Trim());grid.Rows[i].Tag=c.ProfileId.Trim();
                if(c.ProfileId.Trim()==selected)grid.CurrentCell=grid.Rows[i].Cells[0];
            }
        }
        async Task OpenSelectedAndroid() {
            if(openingAndroid)return;
            string id=grid.CurrentRow?.Tag as string;
            if(id==null){status.Text="Выберите профиль";return;}
            string name=Convert.ToString(grid.CurrentRow.Cells[0].Value);
            openingAndroid=true;openAndroid.Enabled=false;
            try{
                await AndroidProfileLauncher.Open(settings,id,name,"com.instagram.android",line=>{if(!IsDisposed)status.Text=line;});
            }catch(Exception error){
                if(!IsDisposed)status.Text=error is InvalidOperationException?error.Message:"Не удалось открыть Android; подробности в журнале.";
                AppDiagnostics.Write("Android",id,"","open_error",error.GetType().Name,"error");
            }finally{openingAndroid=false;if(!IsDisposed)openAndroid.Enabled=true;}
        }
        async Task OpenSelected() {
            if(loading)return;
            string id=grid.CurrentRow?.Tag as string;if(id==null){status.Text="Выберите профиль";return;}
            if(windows.TryGetValue(id,out var existing)&&!existing.IsDisposed){existing.Show();existing.Activate();return;}
            loading=true;open.Enabled=false;grid.Enabled=false;
            try{
                status.Text="Получаю назначенный этому профилю прокси Dolphin…";
                string name=Convert.ToString(grid.CurrentRow.Cells[0].Value);
                var result=await TelegramProxyCatalog.FetchDolphin(WindowsSupport.Unprotect(settings.ProtectedDolphinToken),new[]{new KeyValuePair<string,string>(id,name)},includeCatalog:false);
                var proxy=result.Proxies.SingleOrDefault();
                if(proxy==null)throw new InvalidOperationException(result.Warnings.Count>0?string.Join("; ",result.Warnings):"В Dolphin этому профилю не назначен прокси. Прямое соединение не используется.");
                var window=new InstagramWebWindow(id,name,proxy.url);windows[id]=window;
                window.FormClosed+=(s,e)=>{if(windows.TryGetValue(id,out var current)&&ReferenceEquals(current,window))windows.Remove(id);};
                window.Show(FindForm());status.Text="Открыто: "+name+". Instagram, Threads и Центр аккаунтов Meta доступны в одном окне; сессия сохраняется в профиле.";
            }catch(Exception e){status.Text=e is InvalidOperationException?e.Message:"Не удалось получить профиль или открыть окно. Подробности в журнале Instagram.";AppDiagnostics.Write("Instagram",id,"","open_error",e.GetType().Name,"error");}
            finally{loading=false;open.Enabled=true;grid.Enabled=true;}
        }
    }
}
