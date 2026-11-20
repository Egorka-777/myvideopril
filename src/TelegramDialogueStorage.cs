using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public sealed class TelegramDialoguePreset {
        public string Id="",Name="",Target="",Topic="",Facts="",Model="google/gemini-2.5-flash",Language="Русский";
        public List<TelegramDialogueParticipant> Participants=new List<TelegramDialogueParticipant>();
        public List<TelegramDialogueStep> Steps=new List<TelegramDialogueStep>();
        public int Count=10,DelayMin=10,DelayMax=30,PerAccountHour,PerAccountRun,PerTargetRun;
        public bool StartEnabled,HoursEnabled;
        public DateTime StartLocal=DateTime.Now;
        public int HourStart,HourEnd=24;
        public override string ToString()=>Name;
    }
    public sealed class TelegramDialogueHistoryEvent {
        public DateTime Utc=DateTime.UtcNow;
        public string Kind="",AccountId="",Text="",Part="";
        public int Step;
        public long MessageId;
    }
    public sealed class TelegramDialogueHistory {
        public string Id="",Target="",Status="Выполняется",Result="";
        public DateTime StartedUtc=DateTime.UtcNow,EndedUtc;
        public List<TelegramDialogueParticipant> Participants=new List<TelegramDialogueParticipant>();
        public List<TelegramDialogueStep> Steps=new List<TelegramDialogueStep>();
        public Dictionary<string,object> Limits=new Dictionary<string,object>(),Schedule=new Dictionary<string,object>();
        public List<TelegramDialogueHistoryEvent> Events=new List<TelegramDialogueHistoryEvent>();
        public int Sent;
    }
    public sealed class TelegramDialogueStorage {
        readonly string folder;
        static readonly object Gate=new object();
        static JavaScriptSerializer Json()=>new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
        public TelegramDialogueStorage(string root){folder=Path.Combine(root,"dialogue");}
        T Load<T>(string path,Func<T> empty){if(!File.Exists(path))return empty();return Json().Deserialize<T>(WindowsSupport.Unprotect(File.ReadAllText(path,Encoding.UTF8)));}
        void Save(string path,object data){Directory.CreateDirectory(Path.GetDirectoryName(path));string tmp=path+".tmp";File.WriteAllText(tmp,WindowsSupport.Protect(Json().Serialize(data)),new UTF8Encoding(false));if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}
        public List<TelegramDialoguePreset> ListPresets(){lock(Gate)return Load(Path.Combine(folder,"presets.vault"),()=>new List<TelegramDialoguePreset>());}
        public void SavePreset(TelegramDialoguePreset preset){lock(Gate){var all=ListPresets();if(string.IsNullOrWhiteSpace(preset.Name))throw new ArgumentException("Укажите название пресета.");if(string.IsNullOrEmpty(preset.Id))preset.Id=Guid.NewGuid().ToString("N");all.RemoveAll(p=>p.Id==preset.Id);all.Add(preset);Save(Path.Combine(folder,"presets.vault"),all);}}
        public void DeletePreset(string id){lock(Gate){var all=ListPresets();all.RemoveAll(p=>p.Id==id);Save(Path.Combine(folder,"presets.vault"),all);}}
        string HistoryPath(string id){if(!Guid.TryParseExact(id,"N",out var ignored))throw new ArgumentException("Неверный ID запуска.");return Path.Combine(folder,"history",id+".vault");}
        public void SaveHistory(TelegramDialogueHistory run){lock(Gate)Save(HistoryPath(run.Id),run);}
        public List<TelegramDialogueHistory> ListHistory(){lock(Gate){string path=Path.Combine(folder,"history");if(!Directory.Exists(path))return new List<TelegramDialogueHistory>();return Directory.GetFiles(path,"*.vault").Select(p=>Load(p,()=>new TelegramDialogueHistory())).OrderByDescending(r=>r.StartedUtc).ToList();}}
    }
}
