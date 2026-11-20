using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace VideoBatch {
    /// <summary>Explicit-button operation; caller marks a preset used only after this task succeeds.</summary>
    public static class TelegramProfilePresetApply {
        static string Get(Dictionary<string,object> ev,string key)=>ev.TryGetValue(key,out var value)?Convert.ToString(value):"";
        public static async Task Apply(TelegramStore store,TelegramAccount account,string name,string bio,string avatar,Action<string> log){
            if(store==null||account==null)throw new ArgumentNullException(store==null?nameof(store):nameof(account));
            // Telegram setName: 1–64 Unicode characters. Do not silently shorten or split a display name.
            int length=0;for(int i=0;i<(name??"").Length;i++,length++)if(char.IsHighSurrogate(name[i])&&i+1<name.Length&&char.IsLowSurrogate(name[i+1]))i++;
            if(string.IsNullOrWhiteSpace(name)||length>64||name.Any(char.IsControl))throw new IOException("Полное имя Telegram — от 1 до 64 символов в одну строку; текст не сокращён");
            if(bio==null||bio.IndexOfAny(new[]{'\r','\n'})>=0)throw new IOException("Биография Telegram должна быть в одну строку; лимит аккаунта проверяется до изменений");
            byte[] image=TelegramProfileTemplates.ReadImage(avatar);string hash;
            using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(image)).Replace("-","").ToLowerInvariant();
            store.Prepare();string path=TelegramProfileTemplates.AvatarPath(store,hash+".jpg");Directory.CreateDirectory(Path.GetDirectoryName(path));
            if(!File.Exists(path))File.WriteAllBytes(path,image);
            var job=new Dictionary<string,object>{{"action","profile_preset"},{"accounts",new List<TelegramAccount>{account}},{"profile",new Dictionary<string,object>{{"first_name",name},{"last_name",""},{"about",bio},{"avatar",path},{"avatar_sha256",hash}}}};
            var runner=new TelegramRunner(store);bool complete=false,failed=false;string failure="";Dictionary<string,object> confirmed=null;
            int exit=await runner.Run(job,ev=>{
                string kind=Get(ev,"event");
                if(kind=="connection"){
                    string session=Get(ev,"session");if(session!="")store.Change(data=>{var saved=data.accounts.FirstOrDefault(a=>a.id==Get(ev,"id"));if(saved!=null)saved.session=session;});
                    return; // Never log session/auth material.
                }
                if(kind=="account"){store.Change(data=>TelegramHealth.ApplyAccount(data,ev));if(Get(ev,"status")!="ready")failed=true;}
                if(kind=="proxy")store.Change(data=>TelegramHealth.ApplyProxy(data,ev));
                if(kind=="profile_preset_updated"){
                    if(Get(ev,"id")!=account.id||Get(ev,"first_name")!=name||Get(ev,"last_name")!=""||Get(ev,"about")!=bio||Get(ev,"avatar_sha256")!=hash||Get(ev,"photo_id")==""||Get(ev,"downloaded_avatar_sha256").Length!=64)
                        throw new IOException("Telegram вернул неполное подтверждение пресета; проверьте профиль перед повтором");
                    confirmed=ev;
                }
                if(kind=="complete")complete=true;
                if(kind=="profile_failed"||kind=="error"||kind=="stopped"){failed=true;failure=Get(ev,"text");}
                if(kind=="summary"&&ev.TryGetValue("failures",out var failures)&&Convert.ToInt32(failures)>0)failed=true;
                if(new[]{"stage","log","account","proxy","profile_failed","error","stopped","summary"}.Contains(kind)&&Get(ev,"text")!="")log?.Invoke(Get(ev,"text"));
            });
            if(exit!=0||!complete||failed||confirmed==null)throw new IOException(failure!=""?failure:"Пресет Telegram не подтверждён полностью; изменения могли сохраниться частично. Проверьте профиль перед повтором.");
            store.Change(data=>{var saved=data.accounts.FirstOrDefault(a=>a.id==account.id);if(saved==null)return;saved.name=name;saved.profile_photo_id=Get(confirmed,"photo_id");saved.profile_avatar_hash=hash;saved.profile_result=Get(confirmed,"text");});
            log?.Invoke(Get(confirmed,"text"));
        }
    }
}
