using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class TelegramAccount {
        public string id { get; set; } = "";
        public string session { get; set; } = "";
        public int api_id { get; set; }
        public string api_hash { get; set; } = "";
        public string name { get; set; } = "";
        public string username { get; set; } = "";
        public string phone { get; set; } = "";
        public long user_id { get; set; }
        public string device { get; set; } = "VideoBatch Desktop";
        public string system { get; set; } = "Windows";
        public string app_version { get; set; } = "1.0";
        public string lang { get; set; } = "en";
        public string system_lang { get; set; } = "en";
        public string proxy { get; set; } = "";
        public string proxy_id { get; set; } = "";
        public string api_source { get; set; } = "";
        public long expected_user_id { get; set; }
        public string country { get; set; } = "";
        public string note { get; set; } = "";
        public string status { get; set; } = "unchecked";
        public string checked_at { get; set; } = "";
        public string last_attempt_utc {get;set;}="";
        public string last_success_utc {get;set;}="";
        public string last_error {get;set;}="";
        public string last_code {get;set;}="";
        public string proxy_status {get;set;}="unchecked";
        public string proxy_detail {get;set;}="";
        public string proxy_checked_at {get;set;}="";
        public string wait_until { get; set; } = "";
        public override string ToString() => string.IsNullOrWhiteSpace(name) ? id.Substring(0,Math.Min(10,id.Length)) : name;
    }
    public sealed class TelegramData {
        public int version { get; set; } = 1;
        public int api_id { get; set; }
        public string api_hash { get; set; } = "";
        public string target { get; set; } = "";
        public int delay { get; set; } = 60;
        public int invite_limit {get;set;}=1;
        public string default_proxy_id {get;set;}="";
        public List<TelegramProxy> proxies {get;set;}=new List<TelegramProxy>();
        public List<TelegramAccount> accounts { get; set; } = new List<TelegramAccount>();
    }
    public sealed class TelegramStore {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8*1024*1024 };
        static readonly byte[] Header = Encoding.ASCII.GetBytes("VBTG1\n");
        public string Root { get; }
        public string Vault => Path.Combine(Root,"telegram.vault");
        public TelegramStore(string root=null) { Root = root ?? Path.Combine(Store.Root,"telegram"); }
        public void Prepare() {
            Directory.CreateDirectory(Root);
            // Secrets and transient TData readable only by this Windows user and SYSTEM.
            if(Environment.OSVersion.Platform==PlatformID.Win32NT) {
                var acl=new DirectorySecurity(); acl.SetAccessRuleProtection(true,false);
                foreach(var sid in new[]{WindowsIdentity.GetCurrent().User,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                    acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
                Directory.SetAccessControl(Root,acl);
            }
        }
        T Locked<T>(Func<T> action) {
            string key;using(var hash=SHA256.Create())key=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).ToUpperInvariant()))).Replace("-","");
            using(var mutex=new Mutex(false,"Local\\VideoBatchTelegramVault_"+key)) {
                bool held=false;try {try{held=mutex.WaitOne(8000);}catch(AbandonedMutexException){held=true;}
                    if(!held)throw new IOException("Telegram занят другим окном приложения");return action();
                } finally {if(held)mutex.ReleaseMutex();}
            }
        }
        public TelegramData Load() => Locked(Read);
        TelegramData Read() {
            if(!File.Exists(Vault))return new TelegramData();
            byte[] plain=null;
            try {
                var bytes=File.ReadAllBytes(Vault);
                if(bytes.Length<=Header.Length || !bytes.Take(Header.Length).SequenceEqual(Header))throw new InvalidDataException();
                plain=ProtectedData.Unprotect(bytes.Skip(Header.Length).ToArray(),null,DataProtectionScope.CurrentUser);
                var data=Json.Deserialize<TelegramData>(Encoding.UTF8.GetString(plain));
                if(data==null||data.version!=1||data.accounts==null||data.accounts.Any(a=>a==null||a.id==null||a.id.Length!=64)||data.accounts.Select(a=>a.id).Distinct().Count()!=data.accounts.Count)throw new InvalidDataException();
                if(data.proxies==null)data.proxies=new List<TelegramProxy>();
                return data;
            }catch(Exception e)when(e is CryptographicException||e is InvalidDataException||e is ArgumentException||e is InvalidOperationException){
                throw new IOException("Не удалось открыть Telegram-хранилище. Сохранённые данные не перезаписаны. Нужен тот же пользователь Windows или восстановление резервной копии.",e);
            }finally{if(plain!=null)Array.Clear(plain,0,plain.Length);}
        }
        public void Change(Action<TelegramData> action) {Locked(()=>{var data=Read();action(data);Write(data);return true;});}
        void Write(TelegramData data) {
            Prepare();byte[] plain=Encoding.UTF8.GetBytes(Json.Serialize(data));string temp=Vault+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                var encrypted=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);
                using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(Header,0,Header.Length);file.Write(encrypted,0,encrypted.Length);file.Flush(true);}
                if(File.Exists(Vault))File.Replace(temp,Vault,Vault+".bak");else File.Move(temp,Vault);
            }finally{Array.Clear(plain,0,plain.Length);if(File.Exists(temp))File.Delete(temp);}
        }
        public static string Status(string value) {
            switch(value){case "ready":return "Работает";case "blocked":return "Заблокирован";case "unauthorized":return "Нужен вход";
                case "deactivated":return "Удалён";case "network":return "Нет связи";case "flood_wait":return "Ожидание Telegram";case "restricted":return "Ограничен";
                case "configuration":return "Нужна настройка";case "permissions":return "Нет прав";case "error":return "Ошибка проверки";default:return "Не проверен";}
        }
        public static string ProxyLabel(string raw) {
            if(string.IsNullOrWhiteSpace(raw))return "Без прокси";
            if(Uri.TryCreate(raw.Contains("://")?raw:"socks5://"+raw,UriKind.Absolute,out var uri)&&!string.IsNullOrWhiteSpace(uri.Host))return uri.Host+":"+uri.Port;
            var p=raw.Split(':');return p.Length>=2?p[0]+":"+p[1]:"Настроен";
        }
    }
}
