using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
namespace VideoBatch {
    public static class TelegramTests {
        public static bool RunSelfTests() {
            string temp=Path.Combine(Path.GetTempPath(),"VideoBatchTelegramTest-"+Guid.NewGuid().ToString("N"));
            try {
                var store=new TelegramStore(temp);string marker="secret-auth-key-"+Guid.NewGuid().ToString("N");
                var account=new TelegramAccount{id=new string('a',64),name="Тест 🇷🇺",session=marker,api_id=1234,api_hash=new string('b',32),proxy="socks5://user:password@127.0.0.1:1080"};
                store.Change(d=>{d.accounts.Add(account);d.target="@group";d.delay=85;});
                var reloaded=new TelegramStore(temp).Load();
                if(reloaded.accounts.Single().session!=marker||reloaded.delay!=85||reloaded.target!="@group")throw new Exception("Vault roundtrip");
                if(Encoding.UTF8.GetString(File.ReadAllBytes(store.Vault)).Contains(marker))throw new Exception("Plaintext secret persisted");
                store.Change(d=>d.accounts[0].status="network");
                if(store.Load().accounts[0].status!="network"||TelegramStore.Status("network")==TelegramStore.Status("blocked"))throw new Exception("State distinction");
                string payload=new JavaScriptSerializer().Serialize(reloaded.accounts[0]);
                if(!payload.Contains("\"session\":")||!payload.Contains("\"api_id\":1234")||!payload.Contains("\"proxy\":"))throw new Exception("Worker contract");
                if(TelegramStore.ProxyLabel("socks5://u:secret@127.0.0.1:1080").Contains("secret"))throw new Exception("Proxy credential leak");
                if(TelegramStore.ProxyLabel("127.0.0.1:1080:u:secret").Contains("secret"))throw new Exception("Legacy proxy leak");
                if(!File.Exists(store.Vault+".bak"))throw new Exception("Atomic backup");
                var corrupt=File.ReadAllBytes(store.Vault);corrupt[corrupt.Length-1]^=0xff;File.WriteAllBytes(store.Vault,corrupt);
                bool rejected=false;try{store.Change(d=>d.accounts.Clear());}catch(IOException){rejected=true;}
                if(!rejected||!File.ReadAllBytes(store.Vault).SequenceEqual(corrupt))throw new Exception("Corrupt vault overwritten");
                if(NavigationService.Title(NavSection.Telegram)!="Telegram")throw new Exception("Navigation");
                var imported=new TelegramAccount{id=new string('d',64),name="Saved",proxy="socks5://localhost:1080",note="Keep",api_id=1,api_hash="old",session="key",expected_user_id=9,status="configuration"};
                var data=new TelegramData();data.accounts.Add(imported);
                if(TelegramStore.ImportAccount(data,new TelegramAccount{id=imported.id,api_id=2,api_hash="new",session="key",source_format="pyrogram",expected_user_id=9}))throw new Exception("Duplicate import");
                if(data.accounts.Count!=1||imported.api_id!=2||imported.name!="Saved"||imported.note!="Keep"||imported.proxy!="socks5://localhost:1080"||imported.source_format!="pyrogram"||imported.status!="unchecked")throw new Exception("Reimport repair/preserve user settings");
                bool wrongIdentity=false;try{TelegramStore.ImportAccount(data,new TelegramAccount{id=imported.id,expected_user_id=10});}catch(IOException){wrongIdentity=true;}
                if(!wrongIdentity||imported.api_id!=2)throw new Exception("Reimport identity mismatch");
                imported.api_source="json";
                TelegramStore.ImportAccount(data,new TelegramAccount{id=imported.id,api_source="tdata_desktop",api_id=99,api_hash="fallback",session="key"});
                if(imported.api_id!=2||imported.api_hash!="new"||imported.api_source!="json")throw new Exception("Fallback import replaced explicit API metadata");
                Directory.CreateDirectory(Path.Combine(temp,"venv","Scripts"));File.WriteAllText(Path.Combine(temp,"venv","Scripts","python.exe"),"");
                if(TelegramRunner.Prepared(store))throw new Exception("Python file alone is not a prepared runtime");
                File.WriteAllText(Path.Combine(temp,"runtime.ready"),"old");if(TelegramRunner.Prepared(store))throw new Exception("Stale runtime marker");
                File.WriteAllText(Path.Combine(temp,"runtime.ready"),TelegramRunner.ExpectedWorker);if(!TelegramRunner.Prepared(store))throw new Exception("Prepared runtime contract");

                return true;
            } finally {if(Directory.Exists(temp))Directory.Delete(temp,true);}
        }
        public static bool RunWorkerProtocolTest(string root) {
            return System.Threading.Tasks.Task.Run(async()=>{
                var store=new TelegramStore(root);store.Prepare();
                string fixture=Path.Combine(root,"Тест импорта","account.session");Directory.CreateDirectory(Path.GetDirectoryName(fixture));
                string script="import sqlite3,sys,json;from pathlib import Path;p=Path(sys.argv[1]);d=sqlite3.connect(str(p));d.execute('CREATE TABLE sessions(dc_id INTEGER,server_address TEXT,port INTEGER,auth_key BLOB)');d.execute('INSERT INTO sessions VALUES(2,?,?,?)',('149.154.167.51',443,bytes(range(256))));d.commit();d.close();p.with_suffix('.json').write_text(json.dumps(dict(api_id=1234,api_hash='a'*32,first_name='Тест импорта')),encoding='utf-8')";
                var info=new System.Diagnostics.ProcessStartInfo(Path.Combine(root,"venv","Scripts","python.exe"),"-c "+Core.Quote(script)+" "+Core.Quote(fixture)){UseShellExecute=false,CreateNoWindow=true};
                using(var process=System.Diagnostics.Process.Start(info)){process.WaitForExit();if(process.ExitCode!=0)throw new Exception("Synthetic session fixture");}
                var before=File.ReadAllBytes(fixture);var runner=new TelegramRunner(store);bool version=false,complete=false;string last="";int imported=0;var stages=new System.Collections.Generic.List<string>();
                int code=await runner.Run(new System.Collections.Generic.Dictionary<string,object>{{"action","import"},{"paths",new[]{Path.ChangeExtension(fixture,".json")}},{"api_id",0},{"api_hash",""}},ev=>{
                    string kind=Convert.ToString(ev["event"]);stages.Add(kind+(ev.ContainsKey("code")?":"+Convert.ToString(ev["code"]):"")+(ev.ContainsKey("text")?":"+Convert.ToString(ev["text"]):""));if(kind=="version")version=true;if(kind=="complete"){complete=true;last=Convert.ToString(ev["text"]);}
                    if(kind=="imported"){
                        var account=new JavaScriptSerializer().ConvertToType<TelegramAccount>(ev["account"]);
                        if(account.name!="Тест импорта"||account.api_id!=1234||account.session.Length<300)throw new Exception("Import JSON contract");
                        store.Change(d=>d.accounts.Add(account));imported++;
                    }
                });
                if(code!=0||!version||!complete||last!="Задача завершена"||runner.IsRunning||imported!=1)throw new Exception("Real desktop/Python UTF8 protocol: exit="+code+", version="+version+", complete="+complete+", imported="+imported+", running="+runner.IsRunning+"; "+string.Join(" | ",stages));
                if(!before.SequenceEqual(File.ReadAllBytes(fixture))||store.Load().accounts.Count!=1)throw new Exception("Import changed source or failed persistence");
                // Keep a real local SOCKS socket open without replying. Stop must kill the
                // blocked worker after its grace period and report cancellation, not failure.
                var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
                try {
                    var account=store.Load().accounts.Single();account.proxy="socks5://127.0.0.1:"+((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                    var accept=listener.AcceptTcpClientAsync();
                    var running=runner.Run(new System.Collections.Generic.Dictionary<string,object>{{"action","check"},{"accounts",new[]{account}}},ev=>{});
                    if(await System.Threading.Tasks.Task.WhenAny(accept,System.Threading.Tasks.Task.Delay(15000))!=accept){await runner.Stop();await running;throw new Exception("Worker never connected to configured SOCKS proxy");}
                    using(var socket=await accept){await runner.Stop();if(await running!=2||runner.IsRunning)throw new Exception("Stopped worker reported an error instead of cancellation");}
                }finally{listener.Stop();}
                return true;
            }).GetAwaiter().GetResult();
        }
        static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control root){foreach(System.Windows.Forms.Control c in root.Controls){yield return c;foreach(var child in Descendants(c))yield return child;}}
        public static void RunUiTest(string output) {
            Exception failure=null;
            var thread=new System.Threading.Thread(()=>{
                string root=Path.Combine(Path.GetTempPath(),"TelegramUi-"+Guid.NewGuid().ToString("N"));
                try {
                    System.Windows.Forms.Application.EnableVisualStyles();
                    var store=new TelegramStore(root);
                    store.Change(d=>{
                        d.accounts.Add(new TelegramAccount{id=new string('a',64),name="Канал RU",username="sample_ru",status="ready",checked_at=DateTime.Now.ToString("dd.MM HH:mm"),last_success_utc=DateTime.UtcNow.ToString("o"),proxy_status="ready",proxy_detail="Туннель до Telegram доступен",proxy="socks5://user:password@127.0.0.1:1080",country="Германия"});
                        d.accounts.Add(new TelegramAccount{id=new string('b',64),name="English channel",username="sample_en",status="network",checked_at=DateTime.Now.ToString("dd.MM HH:mm"),proxy="socks5://127.0.0.2:1080",last_error="Прокси не ответил вовремя",proxy_status="failed",country="Польша"});
                        d.accounts.Add(new TelegramAccount{id=new string('c',64),name="Новый аккаунт",status="unchecked"});
                    });
                    using(var form=new System.Windows.Forms.Form{ClientSize=new System.Drawing.Size(1080,720),BackColor=Theme.Background})
                    using(var panel=new TelegramPanel(store)) {
                        form.Controls.Add(panel);form.Show();System.Windows.Forms.Application.DoEvents();
                        Directory.CreateDirectory(output);
                        foreach(var tab in new[]{0,1,2}) {
                            panel.SelectTab(tab);System.Windows.Forms.Application.DoEvents();
                            if(tab==2){foreach(System.Windows.Forms.Control c in Descendants(panel))if(c.Name=="InviteLink")c.Text="https://t.me/+local_fixture";}
                            using(var bitmap=new System.Drawing.Bitmap(panel.ClientSize.Width,panel.ClientSize.Height)){panel.DrawToBitmap(bitmap,panel.ClientRectangle);bitmap.Save(Path.Combine(output,tab==0?"telegram-accounts.png":tab==1?"telegram-proxies.png":"telegram-invites.png"));}
                        }
                        using(var wizard=new TelegramImportDialog(store,new[]{@"C:\Downloads\700001.json"})){
                            wizard.Show();System.Windows.Forms.Application.DoEvents();
                            using(var bitmap=new System.Drawing.Bitmap(wizard.Width,wizard.Height)){wizard.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,wizard.Width,wizard.Height));bitmap.Save(Path.Combine(output,"telegram-import.png"));}wizard.Close();
                        }
                        form.Close();
                    }
                }catch(Exception e){failure=e;}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);thread.Start();thread.Join();
            if(failure!=null)throw new Exception("Telegram UI fixture",failure);
        }
    }
}
