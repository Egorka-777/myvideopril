using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Runtime.Serialization.Json;
namespace VideoBatch {
 public static class TestHost {
  public static void Run(string path,string kind){
   string dir=Path.GetDirectoryName(path);using(var ct=new CancellationTokenSource())using(var timer=new Timer(s=>{if(File.Exists(Path.Combine(dir,"cancel")))ct.Cancel();},null,0,100)) {
    var progress=new SyncProgress(u=>File.WriteAllText(Path.Combine(dir,"progress.txt"),u.Text));
    if(kind=="audio") {
     try{AudioJob j;using(var f=File.OpenRead(path))j=(AudioJob)new DataContractJsonSerializer(typeof(AudioJob)).ReadObject(f);string output=AudioEngine.Run(j,progress,ct.Token).GetAwaiter().GetResult();Write(Path.Combine(dir,"result.json"),new BatchResult{Outputs=new System.Collections.Generic.List<string>{output}});}
     catch(OperationCanceledException){Write(Path.Combine(dir,"result.json"),new BatchResult{Cancelled=true});}
     catch(Exception e){Write(Path.Combine(dir,"result.json"),new BatchResult{Errors=new System.Collections.Generic.List<string>{e.Message}});}
    } else { BatchJob j;using(var f=File.OpenRead(path))j=(BatchJob)new DataContractJsonSerializer(typeof(BatchJob)).ReadObject(f);Write(Path.Combine(dir,"result.json"),Core.Run(j,progress,ct.Token).GetAwaiter().GetResult());}
   }
  }
  static void Write(string path,BatchResult result){using(var f=File.Create(path))new DataContractJsonSerializer(typeof(BatchResult)).WriteObject(f,result);}
  public static bool SettingsRoundTrip(string root){Store.Root=root;var p=new Preferences();p.Music.Add("x.wav");p.ProtectedKey="encrypted-placeholder";p.Ranges.Volume=80.5;Store.Save(p);var loaded=Store.Load();return loaded.Ranges.Volume==80.5&&loaded.Music.Count==1&&loaded.Profiles.Length==5&&loaded.ProtectedKey==p.ProtectedKey&&Store.Clone(loaded).ShortCount==10;}
  class SyncProgress:IProgress<Update>{Action<Update> handler;public SyncProgress(Action<Update> action){handler=action;}public void Report(Update u){handler(u);}}
 }
}
