using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class TelegramRunner {
        readonly TelegramStore store;
        Process active;
        StreamWriter commands;
        bool stopRequested;
        public bool IsRunning => active!=null;
        public const string ExpectedWorker="2026-10-17-telegram-connect-v2";
        public static bool Prepared(TelegramStore store){
            try{return File.Exists(Path.Combine(store.Root,"venv","Scripts","python.exe"))&&File.ReadAllText(Path.Combine(store.Root,"runtime.ready"),Encoding.UTF8).Trim()==ExpectedWorker;}catch(IOException){return false;}
        }
        public TelegramRunner(TelegramStore store){this.store=store;}
        public static string Scripts() => AppPaths.CandidateInstallRoots().Select(p=>Path.Combine(p,"tools","telegram")).FirstOrDefault(p=>File.Exists(Path.Combine(p,"worker.py"))) ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","telegram");
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
