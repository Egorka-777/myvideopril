using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class TelegramPanel {
        readonly Func<Preferences> getPreferences;
        readonly CancellationTokenSource proxyCancel=new CancellationTokenSource();
        DataGridView proxyGrid;
        Label proxyHint;
        bool autoSyncTried;
        void BuildProxyPage(Panel page) {
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(12)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,56));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
            void button(string title,Action action){var b=Theme.MakeButton(title,action:action);buttons.Controls.Add(b);workButtons.Add(b);}
            button("＋ Прокси",()=>{try{using(var f=new TelegramProxyDialog())if(f.ShowDialog(this)==DialogResult.OK){store.Change(d=>{foreach(string raw in f.Values)TelegramProxyCatalog.Add(d,raw,f.ProxyName);});RefreshProxies();}}catch(Exception e){Failure(e);}});
            button("Из Dolphin",async()=>await SyncDolphin());
            button("По умолчанию",()=>{try{var p=CurrentProxy();store.Change(d=>d.default_proxy_id=p.id);RefreshProxies();}catch(Exception e){Failure(e);}});
            button("Изменить",()=>{try{var p=CurrentProxy();using(var f=new TelegramProxyDialog(p))if(f.ShowDialog(this)==DialogResult.OK){store.Change(d=>{var saved=d.proxies.First(x=>x.id==p.id);string url=f.Values.Single();saved.name=f.ProxyName;saved.url=url;foreach(var a in d.accounts.Where(a=>a.proxy_id==p.id)){a.proxy=url;a.status="unchecked";}});RefreshProxies();RefreshData();}}catch(Exception e){Failure(e);}});
            button("Удалить",()=>{try{var p=CurrentProxy();store.Change(d=>TelegramProxyCatalog.Remove(d,p.id));RefreshProxies();RefreshData();}catch(Exception e){Failure(e);}});
            layout.Controls.Add(buttons,0,0);
            proxyHint=new Label{Dock=DockStyle.Fill,Text="Прокси выбирается при добавлении аккаунтов. «Из Dolphin» получает настройки, не открывая профили браузера.",ForeColor=Theme.TextSecondary};layout.Controls.Add(proxyHint,0,1);
            proxyGrid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};Theme.StyleGrid(proxyGrid);
            foreach(var name in new[]{"Название","Адрес","Источник","Аккаунтов","Новые аккаунты"})proxyGrid.Columns.Add(name,name);
            proxyGrid.CellDoubleClick+=(s,e)=>{if(e.RowIndex>=0){var p=CurrentProxy();store.Change(d=>d.default_proxy_id=p.id);RefreshProxies();}};
            layout.Controls.Add(proxyGrid,0,2);page.Controls.Add(layout);
        }
        TelegramProxy CurrentProxy()=>proxyGrid.CurrentRow?.Tag as TelegramProxy??throw new InvalidOperationException("Выберите прокси в списке");
        void SeedProxies() {
            var legacy=store.Load().accounts.Where(a=>a.proxy_id==""&&!string.IsNullOrWhiteSpace(a.proxy)).ToList();
            var library=new List<AccountRecord>();try{library=new AccountLibraryStore().Load().Where(a=>!string.IsNullOrWhiteSpace(a.Proxy)).ToList();}catch(IOException){proxyHint.Text="Не удалось прочитать прокси из общего раздела аккаунтов. Можно добавить вручную.";}
            if(legacy.Count==0&&library.Count==0)return;
            store.Change(d=>{
                foreach(var a in legacy){try{var p=TelegramProxyCatalog.Add(d,a.proxy);var saved=d.accounts.First(x=>x.id==a.id);saved.proxy_id=p.id;saved.proxy=p.url;}catch(ArgumentException){}}
                foreach(var a in library){try{TelegramProxyCatalog.Add(d,a.Proxy,a.Name,"Аккаунты приложения","library:"+a.Id,a.Country);}catch(ArgumentException){}}
            });
        }
        void RefreshProxies() {
            if(proxyGrid==null)return;
            var data=store.Load();string current=(proxyGrid.CurrentRow?.Tag as TelegramProxy)?.id;proxyGrid.Rows.Clear();
            foreach(var p in data.proxies){int row=proxyGrid.Rows.Add(p.name,TelegramStore.ProxyLabel(p.url),p.source,data.accounts.Count(a=>a.proxy_id==p.id),p.id==data.default_proxy_id?"По умолчанию":"—");proxyGrid.Rows[row].Tag=p;if(current==p.id)proxyGrid.CurrentCell=proxyGrid.Rows[row].Cells[0];}
        }
        async Task SyncDolphin() {
            if(busy)return;busy=true;foreach(var b in workButtons)b.Enabled=false;grid.Enabled=false;proxyGrid.Enabled=false;
            try{
                proxyHint.Text="Получаем прокси Dolphin…";var prefs=getPreferences?.Invoke()??Store.Load();
                string token=WindowsSupport.Unprotect(prefs.ProtectedDolphinToken);
                var profiles=ProfileCatalog.Build(prefs).Where(p=>!string.IsNullOrWhiteSpace(p.ProfileId)).Select(p=>new KeyValuePair<string,string>(p.ProfileId,p.DisplayName));
                var result=await TelegramProxyCatalog.FetchDolphin(token,profiles,cancel:proxyCancel.Token);
                store.Change(d=>{foreach(var p in result.Proxies)TelegramProxyCatalog.Add(d,p.url,p.name,p.source,p.source_id,p.country);});
                proxyHint.Text="Получено: "+result.Proxies.Count+". Профили браузера не запускались."+(result.Warnings.Count==0?"":" Не всё получено: "+result.Warnings.First());
                if(result.Warnings.Count>0)Log("","","proxy",proxyHint.Text);
            }catch(OperationCanceledException){proxyHint.Text="Получение прокси остановлено";}
            catch(Exception e){proxyHint.Text=e is InvalidOperationException?e.Message:"Не удалось получить прокси Dolphin. Можно добавить строку вручную.";Log("","","proxy",proxyHint.Text);}
            finally{busy=false;foreach(var b in workButtons)b.Enabled=true;grid.Enabled=true;proxyGrid.Enabled=true;RefreshProxies();RefreshData();}
        }
        void AssignProxy() {
            if(busy)return;
            try{var ids=Selected().Select(a=>a.id).ToArray();using(var f=new TelegramFields("Прокси аккаунтов","Настройка применяется только к выбранным аккаунтам.")){
                var choices=TelegramImportDialog.ProxyChoices(store.Load());f.AddControl(choices);
                if(f.ShowDialog(this)!=DialogResult.OK)return;string id=((TelegramProxy)choices.SelectedItem).id;
                store.Change(d=>TelegramProxyCatalog.Assign(d,ids,id));RefreshData();RefreshProxies();
            }}catch(Exception e){Failure(e);}
        }
    }
    internal sealed class TelegramProxyDialog:TelegramFields {
        readonly TextBox name,raw;
        public string ProxyName=>name.Text.Trim();
        public string[] Values{get;private set;}
        public TelegramProxyDialog(TelegramProxy proxy=null):base(proxy==null?"Добавить прокси":"Изменить прокси","Вставьте строку целиком. Поддерживаются SOCKS5, SOCKS4 и HTTP. Несколько прокси — каждый с новой строки."){
            name=Add("Название · необязательно",proxy?.name??"");
            AddInfo("socks5://логин:пароль@host:port  или  host:port:логин:пароль");
            raw=Add("Адрес",proxy?.url??"");raw.Multiline=true;raw.Height=110;raw.ScrollBars=ScrollBars.Vertical;
            var show=new CheckBox{Text="Показать строки",AutoSize=true,ForeColor=Theme.TextSecondary};raw.UseSystemPasswordChar=true;
            show.CheckedChanged+=(s,e)=>raw.UseSystemPasswordChar=!show.Checked;AddControl(show);
            FormClosing+=(s,e)=>{if(DialogResult!=DialogResult.OK)return;try{Values=raw.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(TelegramProxyCatalog.Normalize).Distinct().ToArray();if(Values.Length==0||proxy!=null&&Values.Length!=1)throw new ArgumentException(proxy==null?"Вставьте хотя бы один прокси":"Для изменения нужна одна строка");}catch(Exception ex){e.Cancel=true;DialogResult=DialogResult.None;MessageBox.Show(this,ex.Message,"Прокси");}};
        }
    }
    internal sealed class TelegramImportDialog:TelegramFields {
        readonly TelegramStore store;
        readonly ComboBox proxies;
        readonly Label files;
        readonly TextBox passcode;
        readonly CheckBox check;
        public string[] Paths{get;private set;}=new string[0];
        public string ProxyId=>((TelegramProxy)proxies.SelectedItem).id;
        public string Passcode=>passcode.Text;
        public bool CheckAfter=>check.Checked;
        public static ComboBox ProxyChoices(TelegramData data){
            var combo=Theme.MakeCombo(new string[0]);combo.Width=450;combo.DisplayMember="";
            combo.Items.Add(new TelegramProxy{id="",name="Без прокси",url=""});foreach(var p in data.proxies)combo.Items.Add(p);
            combo.SelectedIndex=0;for(int i=1;i<combo.Items.Count;i++)if(((TelegramProxy)combo.Items[i]).id==data.default_proxy_id)combo.SelectedIndex=i;
            return combo;
        }
        public TelegramImportDialog(TelegramStore store,string[] initial=null):base("Добавить аккаунты","Выберите папку, JSON, .session или ZIP. Вложенная TData и парные данные будут найдены автоматически. Исходники не изменяются."){
            this.store=store;Name="TelegramImport";
            var buttons=new FlowLayoutPanel{Width=450,Height=46,WrapContents=false};
            buttons.Controls.Add(Theme.MakeButton("Выбрать папку",action:()=>{using(var p=new FolderBrowserDialog{Description="Выберите внешнюю папку аккаунта или папку с несколькими аккаунтами"})if(p.ShowDialog(this)==DialogResult.OK)SetPaths(new[]{p.SelectedPath});}));
            buttons.Controls.Add(Theme.MakeButton("Выбрать файлы",ghost:true,action:()=>{using(var p=new OpenFileDialog{Filter="Аккаунты Telegram|*.json;*.session;*.zip",Multiselect=true})if(p.ShowDialog(this)==DialogResult.OK)SetPaths(p.FileNames);}));AddControl(buttons);
            files=new Label{Width=450,Height=44,AutoEllipsis=true,ForeColor=Theme.TextSecondary};AddControl(files);SetPaths(initial??new string[0]);
            AddInfo("Прокси для новых аккаунтов");proxies=ProxyChoices(store.Load());proxies.Name="ImportProxy";AddControl(proxies);
            AddControl(Theme.MakeButton("＋ Добавить прокси",ghost:true,action:()=>{using(var f=new TelegramProxyDialog())if(f.ShowDialog(this)==DialogResult.OK){string selected="";store.Change(d=>{foreach(var value in f.Values)selected=TelegramProxyCatalog.Add(d,value,f.ProxyName).id;});proxies.Items.Clear();var data=store.Load();proxies.Items.Add(new TelegramProxy{id="",name="Без прокси"});foreach(var p in data.proxies)proxies.Items.Add(p);proxies.SelectedItem=data.proxies.First(p=>p.id==selected);}}));
            check=new CheckBox{Text="Проверить после добавления",Checked=true,AutoSize=true,Margin=new Padding(0,12,0,12),ForeColor=Theme.TextPrimary};AddControl(check);
            var advanced=new Panel{Width=450,Height=0,Visible=false};
            passcode=Theme.MakeSearchBox();passcode.UseSystemPasswordChar=true;passcode.Dock=DockStyle.Bottom;
            advanced.Controls.Add(new Label{Text="Локальный код-пароль TData · обычно пусто",Dock=DockStyle.Top,Height=25,ForeColor=Theme.TextSecondary});advanced.Controls.Add(passcode);
            AddControl(Theme.MakeButton("Дополнительно",ghost:true,action:()=>{advanced.Visible=!advanced.Visible;advanced.Height=advanced.Visible?65:0;}));AddControl(advanced);
            AllowDrop=true;DragEnter+=(s,e)=>e.Effect=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;DragDrop+=(s,e)=>SetPaths((string[])e.Data.GetData(DataFormats.FileDrop));
            FormClosing+=(s,e)=>{if(DialogResult==DialogResult.OK&&Paths.Length==0){e.Cancel=true;DialogResult=DialogResult.None;MessageBox.Show(this,"Выберите файлы или папку аккаунтов","Telegram");}};
        }
        void SetPaths(string[] paths){Paths=paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();files.Text=Paths.Length==0?"Пока ничего не выбрано · можно перетащить сюда":Paths.Length==1?Path.GetFileName(Paths[0].TrimEnd(Path.DirectorySeparatorChar)):"Выбрано источников: "+Paths.Length;}
    }
}
