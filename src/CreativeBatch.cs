using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public class CreativeBatchItem {
        public string Id=Guid.NewGuid().ToString("N"), ProjectFile="", Screenshot="", Output="", Status="Ожидает материалов", Error="";
        public string SourceUrl="";
        public bool CommentsReviewed;
    }
    public class CreativeBatch {
        public string OutputDirectory="";
        public List<CreativeBatchItem> Items=new List<CreativeBatchItem>();
        public static CreativeBatch Load(string path) {
            var batch=File.Exists(path)?AssemblyFiles.Load<CreativeBatch>(path):new CreativeBatch();
            foreach(var item in batch.Items.Where(i=>!string.IsNullOrWhiteSpace(i.Output)&&!File.Exists(i.Output)&&File.Exists(i.ProjectFile))) {
                var project=CreativeProject.Load(item.ProjectFile);
                if(!string.IsNullOrWhiteSpace(project.OutputTitle)) {
                    string renamed=Path.Combine(Path.GetDirectoryName(item.Output),project.OutputTitle+".mp4");
                    if(File.Exists(renamed))item.Output=renamed;
                }
            }
            return batch;
        }
        public void Save(string path) => AssemblyFiles.Save(path,this);
    }
    public static class CreativeBatchRunner {
        public static async Task RunAsync(CreativeBatch batch,string ffmpeg,string probe,string apiKey,string elevenKey,string proxy,
            CancellationToken ct,Action save,Action<CreativeBatchItem,string> progress=null) {
            if(string.IsNullOrWhiteSpace(batch.OutputDirectory))throw new Exception("Выберите папку результата.");
            string outputDirectory=VideoBatchOutputPaths.DailySessionFolder(batch.OutputDirectory);
            // Keep one prepared item ahead of the single renderer. All state/save
            // continuations retain the caller's (WinForms) synchronization context.
            var synchronizationContext=SynchronizationContext.Current;
            Action<CreativeBatchItem,string> report=(item,text)=> {
                if(progress==null)return;
                if(synchronizationContext==null||SynchronizationContext.Current==synchronizationContext)progress(item,text);
                else synchronizationContext.Post(_=>progress(item,text),null);
            };
            using(var operation=CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                var token=operation.Token;
                var context=new AssemblyRenderContext();
                int index=0;
                CreativeBatchItem TakeNext() {
                    while(index<batch.Items.Count) {
                        var item=batch.Items[index++];
                        if(item.Status=="Готово"&&File.Exists(item.Output))continue;
                        return item;
                    }
                    return null;
                }
                Task<CreativeProject> pending=null;
                try {
                    token.ThrowIfCancellationRequested();
                    var next=TakeNext();
                    if(next!=null)pending=PrepareAsync(next,ffmpeg,probe,apiKey,elevenKey,proxy,token,save,report);
                    while(pending!=null) {
                        var item=next;
                        var project=await pending;
                        pending=null;
                        token.ThrowIfCancellationRequested();
                        next=TakeNext();
                        if(next!=null)pending=PrepareAsync(next,ffmpeg,probe,apiKey,elevenKey,proxy,token,save,report);
                        if(project==null)continue;
                        try {
                            token.ThrowIfCancellationRequested();
                            item.Status="Рендер";save();
                            string output=CreativeOutputTitle.OutputPath(outputDirectory,project);
                            await CreativeRender.RenderAsync(project,ffmpeg,probe,output,token,(text,percent)=>report(item,percent>=0?text+" · "+percent.ToString("0",Core.Inv)+"%":text),context);
                            token.ThrowIfCancellationRequested();item.Output=output;item.Status="Готово";save();report(item,item.Status);
                        } catch(OperationCanceledException) {item.Status="Отменено";save();throw;}
                        catch(Exception ex) {item.Status="Ошибка";item.Error=ex.Message;AppDiagnostics.Write("Креатив","",item.Id,"render_failed",ex.ToString(),"error");save();report(item,"Ошибка: "+ex.Message);}
                    }
                } finally {
                    // Do not leave paid requests or project writes running after exit.
                    operation.Cancel();
                    if(pending!=null) {
                        try {await pending;}
                        catch(OperationCanceledException) when(operation.IsCancellationRequested) {}
                    }
                }
            }
        }
        static async Task<CreativeProject> PrepareAsync(CreativeBatchItem item,string ffmpeg,string probe,string apiKey,string elevenKey,string proxy,
            CancellationToken ct,Action save,Action<CreativeBatchItem,string> progress) {
                try {
                    ct.ThrowIfCancellationRequested();
                    CreativeProject project;
                    item.Error="";
                    if(!item.CommentsReviewed)throw new Exception("Сначала проверьте нарезку трёх комментариев.");
                    project=CreativeProject.Load(item.ProjectFile);project.SourceUrl=item.SourceUrl;
                    CreativeProject.RequireFile(project.Source,"Исходное видео");
                    foreach(var comment in project.Comments)CreativeProject.RequireFile(comment.ImagePath,"Комментарий");
                    Action<string> report=text=>progress?.Invoke(item,text);
                    item.Status="Анализ";save();progress?.Invoke(item,item.Status);
                    bool needsText=string.IsNullOrWhiteSpace(project.IntroText)||project.IntroText.Length>CreativeAnalysisClient.MaxIntroCharacters||project.Comments.Any(c=>string.IsNullOrWhiteSpace(c.Text));
                    bool replaceIntro=project.IntroText.Length>CreativeAnalysisClient.MaxIntroCharacters;
                    if(!string.IsNullOrWhiteSpace(project.IntroAudio)&&File.Exists(project.IntroAudio)) {
                        bool longAudio=Core.AudioDuration(await Core.Probe(probe,project.IntroAudio,ct))>3;
                        needsText|=longAudio;replaceIntro|=longAudio;
                    }
                    string folder=Path.GetDirectoryName(item.ProjectFile);
                    if(needsText)await CreativePreparation.AnalyzeAsync(project,ffmpeg,probe,folder,apiKey,proxy,ct,report,replaceIntro);
                    await CreativePreparation.PrepareLanguageAsync(project,ffmpeg,probe,folder,ct,report,apiKey,proxy);
                    await CreativePreparation.TranslateTextsAsync(project,apiKey,proxy,ct,report);
                    await CreativeOutputTitle.PrepareAsync(project,ffmpeg,probe,apiKey,proxy,ct,report);
                    project.Save(item.ProjectFile);item.Status="Озвучка";save();
                    try {await CreativePreparation.GenerateSpeechAsync(project,folder,elevenKey,proxy,ct,report);}
                    finally {project.Save(item.ProjectFile);}
                    ct.ThrowIfCancellationRequested();
                    return project;
                } catch(OperationCanceledException) {item.Status="Отменено";save();throw;}
                catch(Exception ex) {item.Status="Ошибка";item.Error=ex.Message;AppDiagnostics.Write("Креатив","",item.Id,"prepare_failed",ex.ToString(),"error");save();progress?.Invoke(item,"Ошибка: "+ex.Message);return null;}
        }
    }
}
