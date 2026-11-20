using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
namespace VideoBatch {
 public static class TelegramDialogueStorageTests {
  public static bool RunSelfTests(){string root=Path.Combine(Path.GetTempPath(),"dialogue-store-"+Guid.NewGuid().ToString("N"));try{
   var storage=new TelegramDialogueStorage(root);if(storage.ListPresets().Count!=0||storage.ListHistory().Count!=0)throw new Exception("Empty storage");
   var preset=new TelegramDialoguePreset{Name="Тестовый пресет",Target="https://t.me/+test",PerAccountHour=5,HoursEnabled=true,HourStart=22,HourEnd=6,Steps=new List<TelegramDialogueStep>{new TelegramDialogueStep{account_id="a",text="Пример",media_path="C:\\image.png"}}};
   storage.SavePreset(preset);var loaded=storage.ListPresets().Single();if(loaded.Id!=preset.Id||loaded.HourEnd!=6||loaded.Steps[0].media_path!="C:\\image.png")throw new Exception("Preset roundtrip");
   preset.Name="Изменён";storage.SavePreset(preset);if(storage.ListPresets().Count!=1)throw new Exception("Preset upsert");
   var run=new TelegramDialogueHistory{Id=Guid.NewGuid().ToString("N"),Target=preset.Target,Steps=preset.Steps,Sent=1,Status="Завершён",EndedUtc=DateTime.UtcNow};run.Events.Add(new TelegramDialogueHistoryEvent{Kind="dialogue_sent",MessageId=123,Part="media"});storage.SaveHistory(run);
   var history=storage.ListHistory().Single();if(history.Sent!=1||history.Events[0].MessageId!=123||history.Steps[0].text!="Пример")throw new Exception("History roundtrip");
   foreach(string file in Directory.GetFiles(root,"*.vault",SearchOption.AllDirectories))if(File.ReadAllText(file).Contains("Пример"))throw new Exception("Unprotected content");
   storage.DeletePreset(preset.Id);if(storage.ListPresets().Count!=0)throw new Exception("Delete preset");Console.WriteLine("PASS dialogue presets/history: protected roundtrip, upsert, delete, attachments, events");return true;
  }finally{if(Directory.Exists(root))Directory.Delete(root,true);}}
 }
}
