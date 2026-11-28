using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VideoBatch {
    public static class CreativeLinkImport {
        public static string ValidateUrl(string value) {
            if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||(uri.Scheme!="https"&&uri.Scheme!="http")||!string.IsNullOrEmpty(uri.UserInfo))throw new Exception("Нужна ссылка на видео: "+value);
            string host=uri.IdnHost;
            if(!new[]{"instagram.com","tiktok.com","youtube.com","youtu.be"}.Any(h=>host.Equals(h,StringComparison.OrdinalIgnoreCase)||host.EndsWith("."+h,StringComparison.OrdinalIgnoreCase)))throw new Exception("Поддерживаются ссылки Instagram, TikTok и YouTube: "+value);
            return uri.AbsoluteUri;
        }
        public static string Downloader() {
            foreach(string root in AppPaths.CandidateInstallRoots()) {string file=Path.Combine(root,"tools","yt-dlp.exe");if(File.Exists(file))return file;}
            throw new FileNotFoundException("Не найден tools\\yt-dlp.exe. Нужен официальный yt-dlp для скачивания по ссылкам.");
        }
        public static async Task<string> Download(string url,string directory,string ffmpeg,CancellationToken ct,Action<string> progress) {
            url=ValidateUrl(url);Directory.CreateDirectory(directory);
            var args=new List<string>{"--ignore-config","--encoding","utf-8","--no-playlist","--no-simulate","--no-progress","--socket-timeout","30","--retries","0","--fragment-retries","0","--ffmpeg-location",ffmpeg,
                "--write-info-json","--format","bv*+ba/b","--merge-output-format","mp4","--print","after_move:filepath","--output",Path.Combine(directory,"video.%(ext)s")};
            string node=Path.Combine(AppPaths.UploaderRoot,"node.exe");if(File.Exists(node))args.AddRange(new[]{"--js-runtimes","node:"+node});
            args.Add("--");args.Add(url);progress?.Invoke("Скачиваю видео…");
            string output=await Core.Tool(Downloader(),args,ct,null,900);
            string path=output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).LastOrDefault(File.Exists);
            if(path==null)throw new IOException("Загрузчик не вернул готовый видеофайл.");return path;
        }
    }
    // Separate session from publishing accounts. Only watches for a NEW clipboard image;
    // the user explicitly pastes it into the selected card after this window closes.
    internal sealed class CreativeCommentCapture : Form {
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        readonly WebView2 view=new WebView2{Dock=DockStyle.Fill};
        readonly Label hint=new Label{Dock=DockStyle.Top,Height=52,Text="Откройте нужные комментарии и выделите их через Print Screen / Win+Shift+S. После копирования картинки окно закроется. Затем нажмите Ctrl+V в карточке."};
        readonly System.Windows.Forms.Timer clipboardTimer=new System.Windows.Forms.Timer{Interval=250};
        readonly string url;
        uint clipboardSequence;
        public bool ScreenshotReady {get;private set;}
        public CreativeCommentCapture(string url) {
            this.url=CreativeLinkImport.ValidateUrl(url);Text="Скриншот комментариев";ClientSize=new Size(1100,780);StartPosition=FormStartPosition.CenterParent;
            var close=Theme.MakeButton("Вернуться к карточке",action:()=>Close());close.Dock=DockStyle.Bottom;
            Controls.Add(view);Controls.Add(hint);Controls.Add(close);
            Shown+=async(s,e)=>await OpenPage();
            clipboardTimer.Tick+=(s,e)=> {
                uint sequence=GetClipboardSequenceNumber();if(sequence==clipboardSequence)return;
                try {if(Clipboard.ContainsImage()){ScreenshotReady=true;Close();}else clipboardSequence=sequence;}
                catch(ExternalException) { /* The snipping tool still owns the clipboard. Check on its next update. */ }
            };
            FormClosed+=(s,e)=>clipboardTimer.Stop();
        }
        async Task OpenPage() {
            clipboardSequence=GetClipboardSequenceNumber();clipboardTimer.Start();
            try {
                var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Store.Root,"creative-link-browser"));if(IsDisposed)return;
                await view.EnsureCoreWebView2Async(env);if(IsDisposed)return;
                view.CoreWebView2.NewWindowRequested+=(s,e)=>e.Handled=true;
                view.CoreWebView2.DownloadStarting+=(s,e)=>e.Cancel=true;
                view.CoreWebView2.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
                view.CoreWebView2.NavigationCompleted+=(s,e)=>{if(!e.IsSuccess)hint.Text="Страница не загрузилась: "+e.WebErrorStatus+". Можно закрыть окно и открыть исходную ссылку в браузере.";};
                view.CoreWebView2.Navigate(url);
            }catch(Exception ex){if(!IsDisposed)hint.Text="Не удалось открыть видео: "+ex.Message;AppDiagnostics.Write("Creative","","","comment_browser",ex.Message,"error");}
        }
        protected override void Dispose(bool disposing){if(disposing)clipboardTimer.Dispose();base.Dispose(disposing);}
    }
}
