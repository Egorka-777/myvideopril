using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch.HttpTest {
    public sealed class HttpTestForm:Form {
        readonly ComboBox account=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=690};
        readonly TextBox video=new TextBox{Width=600,ReadOnly=true};
        readonly TextBox title=new TextBox{Width=690};
        readonly DateTimePicker schedule=new DateTimePicker{Width=260,Format=DateTimePickerFormat.Custom,CustomFormat="dd.MM.yyyy HH:mm",ShowUpDown=true};
        readonly TextBox log=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Consolas",9)};
        readonly ProgressBar progress=new ProgressBar{Dock=DockStyle.Fill,Minimum=0,Maximum=100};
        readonly Button run=new Button{Text="Загрузить 1 тестовое видео",AutoSize=true,Height=38};
        readonly Button cancel=new Button{Text="Стоп",AutoSize=true,Height=38,Enabled=false};
        readonly Label safety=new Label{AutoSize=true,MaximumSize=new Size(760,0),ForeColor=Color.DarkGreen};
        SettingsSnapshot snapshot;
        Process worker;
        CancellationTokenSource cancellation;

        public HttpTestForm() {
            Text="VideoBatch · HTTP/cookies · безопасный тест";
            ClientSize=new Size(820,700);MinimumSize=new Size(760,600);StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Segoe UI",9.5f);Padding=new Padding(16);
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=10};
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(new Label{Text="Изолированный тест: один Dolphin-профиль → один ролик",AutoSize=true,Font=new Font("Segoe UI",16,FontStyle.Bold)},0,0);
            safety.Text="Рабочий VideoBatch и его settings.xml не изменяются. Очередь основного приложения не используется. Профиль Dolphin после теста остаётся открытым.";
            root.Controls.Add(safety,0,1);
            root.Controls.Add(Row("Аккаунт",account),0,2);
            var pick=new Button{Text="Выбрать видео…",AutoSize=true};pick.Click+=(s,e)=>PickVideo();
            var videoRow=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=false};videoRow.Controls.Add(video);videoRow.Controls.Add(pick);root.Controls.Add(Row("Видео",videoRow),0,3);
            root.Controls.Add(Row("Заголовок",title),0,4);
            root.Controls.Add(Row("Публикация",schedule),0,5);
            var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};actions.Controls.Add(run);actions.Controls.Add(cancel);root.Controls.Add(actions,0,6);
            root.Controls.Add(progress,0,7);root.Controls.Add(log,0,8);
            root.Controls.Add(new Label{Text="Если после передачи файла YouTube не подтвердит videoId, автоповтор запрещён: сначала проверьте открытый профиль, чтобы не создать дубль.",AutoSize=true,MaximumSize=new Size(760,0),ForeColor=Color.DarkRed},0,9);

            schedule.Value=DateTime.Now.AddHours(2);schedule.MinDate=DateTime.Now.AddMinutes(20);
            title.TextChanged+=(s,e)=>ValidateTitleVisual();
            run.Click+=async(s,e)=>await RunUpload();
            cancel.Click+=(s,e)=>CancelWorker();
            FormClosing+=(s,e)=>CancelWorker();
            Shown+=(s,e)=>LoadSnapshot();
        }

        static Control Row(string caption,Control control) {
            var row=new TableLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(0,5,0,5)};
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,105));row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            row.Controls.Add(new Label{Text=caption,AutoSize=true,Anchor=AnchorStyles.Left},0,0);row.Controls.Add(control,1,0);return row;
        }

        void LoadSnapshot() {
            try {
                snapshot=SettingsSnapshot.CreateReadOnlyCopy();
                var channels=snapshot.Preferences.YouTubeChannels
                    .Where(x=>x!=null&&!string.IsNullOrWhiteSpace(x.ProfileId))
                    .OrderBy(x=>x.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
                account.Items.Clear();account.Items.AddRange(channels.Cast<object>().ToArray());
                if(account.Items.Count>0)account.SelectedIndex=0;
                Append("Снимок рабочих настроек создан в отдельной папке.");
                Append("Подключено аккаунтов с Profile ID: "+channels.Length+".");
                if(channels.Length==0)throw new Exception("В рабочих настройках нет YouTube-аккаунтов с Profile ID.");
            } catch(Exception error) { run.Enabled=false;Append("ОШИБКА: "+error.Message);MessageBox.Show(this,error.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error); }
        }

        void PickVideo() {
            using(var dialog=new OpenFileDialog{Filter="Видео|*.mp4;*.mov;*.mkv;*.webm;*.m4v;*.avi|Все файлы|*.*",Multiselect=false}) {
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;
                video.Text=dialog.FileName;title.Text=HttpTestTitle.FromFile(dialog.FileName);
                Append("Выбран файл: "+Path.GetFileName(dialog.FileName));
            }
        }

        void ValidateTitleVisual() {
            string value=(title.Text??"").Trim();
            title.BackColor=value.Length>0&&value.Length<=100&&!value.Contains("<")&&!value.Contains(">")?Color.White:Color.MistyRose;
        }

        async Task RunUpload() {
            if(worker!=null)return;
            try {
                if(snapshot==null)throw new Exception("Снимок настроек не загружен.");
                var channel=account.SelectedItem as ImportedYouTubeChannel;
                if(channel==null)throw new Exception("Выберите аккаунт.");
                if(string.IsNullOrWhiteSpace(channel.ExpectedIp))throw new Exception("У аккаунта нет сохранённого IP. Сначала нажмите «Проверить профили» в обычном VideoBatch.");
                if(!File.Exists(video.Text))throw new Exception("Выберите существующий видеофайл.");
                string clean=(title.Text??"").Trim();
                if(clean.Length<1||clean.Length>100||clean.Contains("<")||clean.Contains(">"))throw new Exception("Заголовок должен содержать 1–100 символов без < и >.");
                if(schedule.Value<=DateTime.Now.AddMinutes(15))throw new Exception("Публикация должна быть минимум через 15 минут.");
                string token=SettingsSnapshot.Unprotect(snapshot.Preferences.ProtectedDolphinToken);
                if(string.IsNullOrWhiteSpace(token))throw new Exception("Не удалось расшифровать Dolphin API-токен. Запустите тест под тем же пользователем Windows, что и обычный VideoBatch.");
                snapshot.AssertSourceUnchanged();

                var confirm=MessageBox.Show(this,
                    "Будет реально загружен ОДИН ролик.\n\nАккаунт: "+channel.Name+"\nProfile ID: "+channel.ProfileId+"\nIP: "+channel.ExpectedIp+"\nПубликация: "+schedule.Value.ToString("dd.MM.yyyy HH:mm")+"\n\nПродолжить?",
                    "Подтверждение реальной загрузки",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2);
                if(confirm!=DialogResult.Yes)return;

                Directory.CreateDirectory(HttpTestPaths.Jobs);
                string jobPath=Path.Combine(HttpTestPaths.Jobs,"http-"+Guid.NewGuid().ToString("N")+".json");
                var dto=new HttpUploadJob{
                    profileId=channel.ProfileId.Trim(),expectedIp=channel.ExpectedIp.Trim(),expectedChannelId=ExtractChannelId(channel.ChannelUrl),localPort=snapshot.Preferences.DolphinPort,
                    video=Path.GetFullPath(video.Text),title=clean,
                    scheduledUnixSeconds=new DateTimeOffset(schedule.Value).ToUnixTimeSeconds()
                };
                using(var output=File.Create(jobPath))new DataContractJsonSerializer(typeof(HttpUploadJob)).WriteObject(output,dto);
                cancellation=new CancellationTokenSource();SetBusy(true);progress.Value=0;
                Append("СТАРТ: один аккаунт, один ролик. Рабочие настройки доступны только для чтения.");
                try { await RunWorker(jobPath,token,cancellation.Token); }
                finally {
                    token="";
                    try { File.Delete(jobPath); } catch {}
                    snapshot.AssertSourceUnchanged();
                }
            } catch(OperationCanceledException) { Append("Остановлено пользователем. Профиль Dolphin оставлен открытым."); }
            catch(Exception error) { Append("ОШИБКА: "+error.Message);MessageBox.Show(this,error.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error); }
            finally { SetBusy(false);if(cancellation!=null){cancellation.Dispose();cancellation=null;}worker=null; }
        }

        static string ExtractChannelId(string value) {
            var match=Regex.Match(value??"",@"(?:youtube\.com/)?channel/(UC[A-Za-z0-9_-]+)",RegexOptions.IgnoreCase);
            return match.Success?match.Groups[1].Value:"";
        }

        async Task RunWorker(string jobPath,string token,CancellationToken ct) {
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tools","uploader");
            string node=Path.Combine(root,"node.exe"),script=Path.Combine(root,"worker-http-test.js");
            if(!File.Exists(node)||!File.Exists(script)||!Directory.Exists(Path.Combine(root,"node_modules","playwright-core")))
                throw new Exception("Рядом с тестовым EXE нет полного tools\\uploader (node.exe, worker-http-test.js, node_modules\\playwright-core).");
            var info=new ProcessStartInfo(node,Quote(script)+" "+Quote(jobPath)){
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=root
            };
            info.EnvironmentVariables["VIDEOBATCH_DOLPHIN_TOKEN"]=token;
            var result=new TaskCompletionSource<int>();
            string reportedError="";
            worker=new Process{StartInfo=info,EnableRaisingEvents=true};
            worker.Exited+=(s,e)=>{try{result.TrySetResult(worker.ExitCode);}catch{result.TrySetResult(-1);}};
            if(!worker.Start())throw new Exception("Не удалось запустить тестовый worker.");
            var stderr=worker.StandardError.ReadToEndAsync();
            try { using(ct.Register(()=>{try{if(worker!=null&&!worker.HasExited)worker.Kill();}catch{}result.TrySetCanceled();})) {
                string line;
                bool success=false;
                var serializer=new DataContractJsonSerializer(typeof(HttpWorkerMessage));
                while((line=await worker.StandardOutput.ReadLineAsync())!=null) {
                    HttpWorkerMessage message=null;
                    try { using(var memory=new MemoryStream(Encoding.UTF8.GetBytes(line)))message=(HttpWorkerMessage)serializer.ReadObject(memory); } catch {}
                    if(message==null){Append(line);continue;}
                    Append((message.stage??"log")+": "+(message.text??message.error??""));
                    int value=(int)Math.Max(0,Math.Min(100,message.percent));progress.Value=value;
                    if(message.stage=="done"&&message.success){success=true;MessageBox.Show(this,message.text,Text,MessageBoxButtons.OK,MessageBoxIcon.Information);}
                    if(message.stage=="error")reportedError=message.error??message.text??"Тестовый worker завершился с ошибкой.";
                }
                int exit=await result.Task;string err=await stderr;
                if(!string.IsNullOrWhiteSpace(reportedError))throw new Exception(reportedError);
                if(exit!=0)throw new Exception(string.IsNullOrWhiteSpace(err)?"Тестовый worker завершился с кодом "+exit+".":err.Trim());
                if(!success)throw new Exception("Worker завершился без подтверждения успешной отложенной публикации.");
            }} finally { try { if(worker!=null&&!worker.HasExited)worker.Kill(); } catch {} }
        }

        static string Quote(string value) { return "\""+(value??"").Replace("\\","\\\\").Replace("\"","\\\"")+"\""; }

        void Append(string text) {
            if(InvokeRequired){BeginInvoke(new Action<string>(Append),text);return;}
            string line=DateTime.Now.ToString("HH:mm:ss")+"  "+text;
            log.AppendText(line+Environment.NewLine);
            try { Directory.CreateDirectory(HttpTestPaths.TestRoot);File.AppendAllText(HttpTestPaths.Log,line+Environment.NewLine,Encoding.UTF8); } catch {}
        }

        void SetBusy(bool busy) {
            if(InvokeRequired){BeginInvoke(new Action<bool>(SetBusy),busy);return;}
            run.Enabled=!busy&&account.Items.Count>0;cancel.Enabled=busy;account.Enabled=!busy;video.Enabled=!busy;title.Enabled=!busy;schedule.Enabled=!busy;
        }

        void CancelWorker() {
            if(cancellation!=null&&!cancellation.IsCancellationRequested)cancellation.Cancel();
            try { if(worker!=null&&!worker.HasExited)worker.Kill(); } catch {}
        }
    }
}
