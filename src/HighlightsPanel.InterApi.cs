using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VideoBatch {
    public static class InterApiSearch {
        public sealed class Video {
            public string id {get;set;}
            public string title {get;set;}
            public string url {get;set;}
            public string author {get;set;}
            public double? duration_sec {get;set;}
            public long? view_count {get;set;}
            public override string ToString() {return (title??url)+" · "+author+" · "+(duration_sec.HasValue?TimeSpan.FromSeconds(duration_sec.Value).ToString():"длительность неизвестна")+" · "+(view_count.HasValue?view_count.Value.ToString("N0")+" просм.":"просмотры неизвестны");}
        }
        public sealed class Response {
            public string status {get;set;}
            public List<Video> results {get;set;}
        }
        public static async Task<List<Video>> Search(HttpClient client,string key,string query,bool shorts,int limit,CancellationToken cancel) {
            var uri="https://api.interapi.dev/api/v1/youtube/search?q="+Uri.EscapeDataString(query)+"&filter="+(shorts?"shorts":"video")+"&limit="+limit;
            using(var request=new HttpRequestMessage(HttpMethod.Get,uri)) {
                request.Headers.Add("X-API-Key",key);
                using(var response=await client.SendAsync(request,cancel).ConfigureAwait(false)) {
                    if(!response.IsSuccessStatusCode)throw new Exception("InterAPI: HTTP "+(int)response.StatusCode+". "+(response.StatusCode==System.Net.HttpStatusCode.Unauthorized||response.StatusCode==System.Net.HttpStatusCode.Forbidden?"Проверьте ключ и доступ к API.":response.StatusCode==(System.Net.HttpStatusCode)429?"Достигнут лимит запросов.":"Поиск не выполнен; проверьте баланс и доступность сервиса."));
                    var raw=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Response parsed;
                    try {parsed=new JavaScriptSerializer().Deserialize<Response>(raw);}catch {throw new Exception("InterAPI: неверный формат ответа поиска.");}
                    if(parsed==null||parsed.status!="success"||parsed.results==null)throw new Exception("InterAPI не вернул успешный результат поиска.");
                    return parsed.results;
                }
            }
        }
    }
    public sealed partial class HighlightsPanel {
        Form interApiWindow;
        CancellationTokenSource interApiCancellation;
        void ShowInterApi() {
            if(interApiWindow!=null&&!interApiWindow.IsDisposed){interApiWindow.Show();interApiWindow.Activate();return;}
            if(busy)return;
            var dialog=new Form {Text="InterAPI · поиск YouTube",ClientSize=new Size(860,580),MinimumSize=new Size(740,500),StartPosition=FormStartPosition.CenterParent,BackColor=Theme.Background,ForeColor=Theme.TextPrimary,Font=Theme.FontBody};
            interApiWindow=dialog;
            dialog.FormClosed+=(s,e)=>interApiWindow=null;
            {
                var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Padding=new Padding(16)};
                foreach(int height in new[]{42,62,42})layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,64));dialog.Controls.Add(layout);
                var row=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
                var type=Theme.MakeCombo(new[]{"YouTube Shorts","YouTube Long"});type.Width=190;type.SelectedIndex=0;
                var query=Theme.MakeSearchBox();query.Width=390;
                var limit=new NumericUpDown {Minimum=1,Maximum=99,Value=20,Width=65};
                row.Controls.Add(type);row.Controls.Add(query);row.Controls.Add(limit);layout.Controls.Add(row,0,0);
                layout.Controls.Add(new Label {Dock=DockStyle.Fill,Text="Введите поисковую фразу. Число справа — максимум результатов (1–99).\n6 кредитов за успешный запрос. Без фильтра даты и сортировки по просмотрам. Long — обычный поиск video; InterAPI не гарантирует исключение Shorts.",ForeColor=Theme.TextSecondary},0,1);
                var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false};
                var search=Theme.MakeButton("Найти",accent:true);var cancel=Theme.MakeButton("Стоп",ghost:true);cancel.Enabled=false;
                var open=Theme.MakeButton("Открыть видео",ghost:true);open.Enabled=false;
                actions.Controls.Add(search);actions.Controls.Add(cancel);actions.Controls.Add(open);layout.Controls.Add(actions,0,2);
                var list=new ListBox {Dock=DockStyle.Fill,HorizontalScrollbar=true,BackColor=Theme.Card,ForeColor=Theme.TextPrimary};layout.Controls.Add(list,0,3);
                var info=new Label {Dock=DockStyle.Fill,Text="Ключ: Настройки → InterAPI. Поиск запускается только кнопкой «Найти»."};layout.Controls.Add(info,0,4);
                CancellationTokenSource pending=null;
                Action clear=()=>{list.Items.Clear();open.Enabled=false;};
                type.SelectedIndexChanged+=(s,e)=>clear();query.TextChanged+=(s,e)=>clear();limit.ValueChanged+=(s,e)=>clear();
                list.SelectedIndexChanged+=(s,e)=>open.Enabled=list.SelectedItem!=null;
                open.Click+=(s,e)=>{var item=list.SelectedItem as InterApiSearch.Video;if(item==null)return;Uri uri;if(!Uri.TryCreate(item.url,UriKind.Absolute,out uri)||uri.Scheme!="https"||!(uri.Host=="youtube.com"||uri.Host.EndsWith(".youtube.com",StringComparison.OrdinalIgnoreCase)||uri.Host=="youtu.be")){MessageBox.Show(dialog,"InterAPI вернул некорректную ссылку YouTube.");return;}System.Diagnostics.Process.Start(uri.AbsoluteUri);};
                cancel.Click+=(s,e)=>pending?.Cancel();
                dialog.FormClosing+=(s,e)=>{if(pending!=null){e.Cancel=true;pending.Cancel();}};
                search.Click+=async(s,e)=>{
                    var key=WindowsSupport.Unprotect(youtubeSettings?.ProtectedInterApiKey);
                    if(string.IsNullOrWhiteSpace(key)){MessageBox.Show(dialog,"Укажите ключ InterAPI в настройках приложения.");return;}
                    if(string.IsNullOrWhiteSpace(query.Text)){MessageBox.Show(dialog,"Введите поисковую фразу.");return;}
                    clear();pending=new CancellationTokenSource();interApiCancellation=pending;search.Enabled=false;type.Enabled=false;query.Enabled=false;limit.Enabled=false;cancel.Enabled=true;info.Text="Поиск через InterAPI…";
                    try {
                        using(var client=OutboundHttp.Create("",TimeSpan.FromSeconds(60),false)) {
                            var found=await InterApiSearch.Search(client,key,query.Text.Trim(),type.SelectedIndex==0,(int)limit.Value,pending.Token);
                            foreach(var item in found)list.Items.Add(item);
                            info.Text="Найдено: "+found.Count+". Выберите видео и нажмите «Открыть видео».";
                        }
                    }catch(OperationCanceledException){info.Text=pending.IsCancellationRequested?"Поиск остановлен. Сервис мог уже списать кредиты за обработанный запрос.":"InterAPI: время ожидания ответа истекло.";}
                    catch(Exception ex){info.Text=ex.Message;}
                    finally{pending.Dispose();pending=null;interApiCancellation=null;search.Enabled=true;type.Enabled=true;query.Enabled=true;limit.Enabled=true;cancel.Enabled=false;}
                };
                dialog.Show(FindForm());
            }
        }
    }
}
