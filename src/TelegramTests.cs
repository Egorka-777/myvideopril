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
                return true;
            } finally {if(Directory.Exists(temp))Directory.Delete(temp,true);}
        }
        public static void RunUiTest(string output) {
            Exception failure=null;
            var thread=new System.Threading.Thread(()=>{
                string root=Path.Combine(Path.GetTempPath(),"TelegramUi-"+Guid.NewGuid().ToString("N"));
                try {
                    var store=new TelegramStore(root);
                    store.Change(d=>{
                        d.accounts.Add(new TelegramAccount{id=new string('a',64),name="Канал RU",username="sample_ru",status="ready",proxy="socks5://user:password@127.0.0.1:1080",country="Германия"});
                        d.accounts.Add(new TelegramAccount{id=new string('b',64),name="English channel",username="sample_en",status="network",country="Польша"});
                        d.accounts.Add(new TelegramAccount{id=new string('c',64),name="Новый аккаунт",status="unchecked"});
                    });
                    using(var form=new System.Windows.Forms.Form{ClientSize=new System.Drawing.Size(1080,720),BackColor=Theme.Background})
                    using(var panel=new TelegramPanel(store)) {
                        form.Controls.Add(panel);form.Show();System.Windows.Forms.Application.DoEvents();
                        var layout=(System.Windows.Forms.TableLayoutPanel)panel.Controls[0];var tabs=layout.Controls.OfType<System.Windows.Forms.TabControl>().Single();
                        Directory.CreateDirectory(output);
                        foreach(var tab in new[]{0,1}) {
                            tabs.SelectedIndex=tab;System.Windows.Forms.Application.DoEvents();
                            using(var bitmap=new System.Drawing.Bitmap(form.ClientSize.Width,form.ClientSize.Height)){form.DrawToBitmap(bitmap,form.ClientRectangle);bitmap.Save(Path.Combine(output,tab==0?"telegram-accounts.png":"telegram-invites.png"));}
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
