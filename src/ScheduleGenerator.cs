using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoBatch {
    /// <summary>Расписание Shorts: 15–60 мин между роликами одного профиля; состояние отдельно по ProfileId.</summary>
    public static class ScheduleGenerator {
        const int MinGapMinutes=15;
        const int MaxGapMinutes=60;
        const int FirstSlotLeadMinutes=30;

        static readonly Random Rng=new Random();

        public static List<(string date,string time)> GenerateForProfile(string profileId,int count,string statePath){
            var slots=new List<(string,string)>();
            if(count<=0||string.IsNullOrWhiteSpace(profileId))return slots;
            var state=LoadState(statePath);
            DateTime next=GetProfileNext(state,profileId.Trim());
            DateTime minimum=CeilMinute(DateTime.Now.AddMinutes(FirstSlotLeadMinutes));
            if(next<minimum)next=minimum;

            for(int i=0;i<count;i++){
                if(next.Hour>=22)next=new DateTime(next.Year,next.Month,next.Day,9,0,0).AddDays(1);
                slots.Add((next.ToString("yyyy-MM-dd"),next.ToString("HH:mm")));
                next=next.AddMinutes(Rng.Next(MinGapMinutes,MaxGapMinutes+1));
            }
            SetProfileNext(state,profileId.Trim(),next);
            SaveState(statePath,state);
            return slots;
        }

        static DateTime CeilMinute(DateTime dt){
            if(dt.Second==0&&dt.Millisecond==0)return dt;
            return new DateTime(dt.Year,dt.Month,dt.Day,dt.Hour,dt.Minute,0).AddMinutes(1);
        }

        sealed class ScheduleState {
            public Dictionary<string,string> profiles=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        }

        static ScheduleState LoadState(string statePath){
            try{
                if(!File.Exists(statePath))return new ScheduleState();
                string json=File.ReadAllText(statePath);
                var state=new ScheduleState();
                var profMatch=Regex.Match(json,@"\""profiles\""\s*:\s*\{([^}]*)\}",RegexOptions.Singleline);
                if(profMatch.Success){
                    foreach(Match m in Regex.Matches(profMatch.Groups[1].Value,@"\""([^""]+)\""\s*:\s*\""([^""]+)\""")){
                        state.profiles[m.Groups[1].Value]=m.Groups[2].Value;
                    }
                    return state;
                }
                // Старый формат {"next":"..."} — безопасно игнорируем (не привязан к ProfileId).
            }catch{}
            return new ScheduleState();
        }

        static DateTime GetProfileNext(ScheduleState state,string profileId){
            if(state.profiles.TryGetValue(profileId,out var iso)&&DateTime.TryParse(iso,null,System.Globalization.DateTimeStyles.RoundtripKind,out var parsed))
                return parsed;
            return DateTime.MinValue;
        }

        static void SetProfileNext(ScheduleState state,string profileId,DateTime next){
            state.profiles[profileId]=next.ToString("o");
        }

        static void SaveState(string statePath,ScheduleState state){
            try{
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)??Store.Root);
                var sb=new StringBuilder();
                sb.Append("{\"profiles\":{");
                bool first=true;
                foreach(var kv in state.profiles.OrderBy(k=>k.Key,StringComparer.OrdinalIgnoreCase)){
                    if(!first)sb.Append(",");
                    first=false;
                    sb.Append("\"").Append(EscapeJson(kv.Key)).Append("\":\"").Append(EscapeJson(kv.Value)).Append("\"");
                }
                sb.Append("}}");
                string temp=statePath+".tmp";
                File.WriteAllText(temp,sb.ToString(),Encoding.UTF8);
                if(File.Exists(statePath))File.Replace(temp,statePath,null);
                else File.Move(temp,statePath);
            }catch{}
        }

        static string EscapeJson(string s){
            return (s??"").Replace("\\","\\\\").Replace("\"","\\\"");
        }
    }
}
