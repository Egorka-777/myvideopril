using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Reflection;
[assembly: AssemblyTitle("VideoBatch")]
[assembly: AssemblyDescription("Обработка видео и загрузка через Dolphin")]
[assembly: AssemblyVersion("4.0.2.0")]
[assembly: AssemblyFileVersion("4.0.2.0")]
namespace VideoBatch {
    public static class WindowsSupport {
        [DllImport("user32.dll")]static extern bool SetProcessDPIAware();
        public static void Dpi(){try{SetProcessDPIAware();}catch{}}
        public static string Protect(string value){return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.CurrentUser));}
        public static string Unprotect(string value){try{return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));}catch{return "";}}
        public static string[] Voices(){try{using(var s=new SpeechSynthesizer())return s.GetInstalledVoices().Where(v=>v.Enabled).Select(v=>v.VoiceInfo.Name).ToArray();}catch{return new string[0];}}
        public static Task Speak(string text,string voice,string path,CancellationToken ct) {
            var promise=new TaskCompletionSource<bool>();var thread=new Thread(()=>{
                try{using(var synth=new SpeechSynthesizer()){synth.SelectVoice(voice);synth.SetOutputToWaveFile(path);var prompt=synth.SpeakAsync(text);var start=DateTime.UtcNow;while(!prompt.IsCompleted){if(ct.IsCancellationRequested){synth.SpeakAsyncCancelAll();ct.ThrowIfCancellationRequested();}if((DateTime.UtcNow-start).TotalSeconds>180){synth.SpeakAsyncCancelAll();throw new Exception("Системная озвучка не ответила вовремя.");}Thread.Sleep(100);}}promise.SetResult(true);}
                catch(OperationCanceledException){promise.SetCanceled();}catch(Exception e){promise.SetException(e);}
            });thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();return promise.Task;
        }
    }
    static class Program {
        [STAThread]static void Main() {
            bool first;using(var single=new Mutex(true,"Local\\VideoBatchDesktop.SingleInstance",out first)){if(!first){MessageBox.Show("VideoBatch уже запущен. Используйте одно окно, чтобы не создать две очереди загрузки.","VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            WindowsSupport.Dpi();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException+=(s,e)=>ShowError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException+=(s,e)=>ShowError(e.ExceptionObject as Exception);
            try{Application.Run(new MainWindow());}catch(Exception e){ShowError(e);}}
        }
        static void ShowError(Exception e) {
            string message=e==null?"Неизвестная ошибка.":e.Message;
            try{Directory.CreateDirectory(Store.Root);File.AppendAllText(Path.Combine(Store.Root,"errors.txt"),DateTime.Now+Environment.NewLine+e+Environment.NewLine);}catch{}
            MessageBox.Show(message,"VideoBatch",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }
}
