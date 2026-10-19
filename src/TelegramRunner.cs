using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class TelegramDoctor {
        public static bool CheckAccount(string root,int index){
            return Task.Run(async()=>{
                var store=new TelegramStore(root);var data=store.Load();
                if(index<1||index>data.accounts.Count)throw new IOException("AccountIndex is outside the saved account list");
                var runner=new TelegramRunner(store);bool complete=false;int failures=0;
                int exit=await runner.Run(new Dictionary<string,object>{{"action","doctor"},{"accounts",new List<TelegramAccount>{data.accounts[index-1]}}},ev=>{
                    string kind=Convert.ToString(ev["event"]);
                    if(kind=="diagnostic_report")SaveReport(root,ev["report"]);
                    if(kind=="complete")complete=true;
                    if(kind=="account")store.Change(d=>TelegramHealth.ApplyAccount(d,ev));
                    if(kind=="proxy")store.Change(d=>TelegramHealth.ApplyProxy(d,ev));
                    if(kind=="summary"&&ev.ContainsKey("failures"))failures=Convert.ToInt32(ev["failures"]);
                    // Never print/serialize raw worker events: connection/import contain keys.
                });
                return exit==0&&complete&&failures==0;
            }).GetAwaiter().GetResult();
        }
        public static void SaveReport(string root,object report){
            var serializer=new JavaScriptSerializer{MaxJsonLength=1024*1024};
            var input=serializer.ConvertToType<Dictionary<string,object>>(report);
            var safe=new Dictionary<string,object>();
            foreach(string key in new[]{"version","utc"})if(input.ContainsKey(key))safe[key]=input[key];
            var rows=new List<Dictionary<string,object>>();
            if(input.TryGetValue("accounts",out var accounts))foreach(object item in (System.Collections.IEnumerable)accounts){
                var row=serializer.ConvertToType<Dictionary<string,object>>(item);var copy=new Dictionary<string,object>();
                foreach(string key in new[]{"slot","status","code","transport","next_action","repair"})if(row.ContainsKey(key))copy[key]=row[key];
                if(row.TryGetValue("attempts",out var attempts)){
                    var list=new List<Dictionary<string,object>>();
                    foreach(object attempt in (System.Collections.IEnumerable)attempts){
                        var source=serializer.ConvertToType<Dictionary<string,object>>(attempt);var entry=new Dictionary<string,object>();
                        foreach(string key in new[]{"transport","code"})if(source.ContainsKey(key))entry[key]=source[key];
                        list.Add(entry);
                    }
                    copy["attempts"]=list;
                }
                rows.Add(copy);
            }
            safe["accounts"]=rows;
            string folder=Path.Combine(root,"diagnostics");Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,"doctor-latest.json"),temp=path+".tmp";
            File.WriteAllText(temp,serializer.Serialize(safe),new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
    }
    public sealed class TelegramRunner {
        readonly TelegramStore store;
        Process active;
        StreamWriter commands;
        bool stopRequested;
        public bool IsRunning => active!=null;
        public const string ExpectedWorker="2026-10-19-telegram-web-v5";
        public static bool Prepared(TelegramStore store){
            try{return File.Exists(Path.Combine(store.Root,"venv","Scripts","python.exe"))&&File.ReadAllText(Path.Combine(store.Root,"runtime.ready"),Encoding.UTF8).Trim()==ExpectedWorker;}catch(IOException){return false;}
        }
        public TelegramRunner(TelegramStore store){this.store=store;}
        public static string Scripts() {
            // Also correct when a PC agent loads this assembly from PowerShell:
            // prefer scripts beside this EXE over a remembered older installation.
            string local=Path.Combine(Path.GetDirectoryName(typeof(TelegramRunner).Assembly.Location),"tools","telegram");
            if(File.Exists(Path.Combine(local,"worker.py")))return local;
            return AppPaths.CandidateInstallRoots().Select(p=>Path.Combine(p,"tools","telegram")).FirstOrDefault(p=>File.Exists(Path.Combine(p,"worker.py"))) ?? local;
        }
        public async Task<int> Run(Dictionary<string,object> job,Action<Dictionary<string,object>> receive) {
            if(active!=null)throw new InvalidOperationException("Дождитесь завершения Telegram-задачи");
            stopRequested=false;
            store.Prepare();string python=Path.Combine(store.Root,"venv","Scripts","python.exe");
            if(!Prepared(store))throw new IOException("Telegram ещё не подготовлен. Нажмите «Настройки → Подготовить Telegram». Нужен Python 3.11 x64.");
            job["root"]=store.Root;
            var start=new ProcessStartInfo(python,Core.Quote(Path.Combine(Scripts(),"worker.py"))) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Scripts() };
            start.EnvironmentVariables["PYTHONUTF8"]="1";
            var process=new Process{StartInfo=start};active=process;
            try {
                process.Start();
                commands=new StreamWriter(process.StandardInput.BaseStream,new UTF8Encoding(false)){AutoFlush=true};
                var serializer=new JavaScriptSerializer{MaxJsonLength=8*1024*1024};
                await commands.WriteLineAsync(serializer.Serialize(job));
                var errors=process.StandardError.ReadToEndAsync();
                string line;
                while((line=await process.StandardOutput.ReadLineAsync())!=null) {
                    // No echoing raw JSON: imported events contain authorization secrets.
                    Dictionary<string,object> ev;
                    try{ev=serializer.Deserialize<Dictionary<string,object>>(line);}catch{throw new IOException("Неверный ответ Telegram-worker; задание остановлено");}
                    if(ev.TryGetValue("event",out var kind)&&Convert.ToString(kind)=="version"&&Convert.ToString(ev["text"])!=ExpectedWorker)
                        throw new IOException("Запущен старый Telegram-worker. Обновите EXE и tools/telegram вместе.");
                    receive(ev); // A vault-save failure aborts before any following operation.
                }
                await Task.Run(()=>process.WaitForExit());await errors;
                return stopRequested?2:process.ExitCode;
            } finally {
                try{if(!process.HasExited)process.Kill();}catch{}
                commands=null;active=null;process.Dispose();
            }
        }
        public async Task SendLoginToken(string token){
            if(!System.Text.RegularExpressions.Regex.IsMatch(token??"","^[A-Za-z0-9_-]{16,1024}$"))throw new IOException("Не прочитан QR входа");
            if(commands==null||active==null)throw new IOException("Подключение Telegram уже завершено. Повторите вход.");
            await commands.WriteLineAsync("web_login:"+token);
        }
        public async Task Stop() {
            var process=active;if(process==null)return;
            stopRequested=true;
            try{if(commands!=null)await commands.WriteLineAsync("cancel");}catch{}
            var ended=await Task.Run(()=>{try{return process.WaitForExit(5000);}catch{return true;}});
            if(!ended)try{process.Kill();}catch{}
        }
    }
}
