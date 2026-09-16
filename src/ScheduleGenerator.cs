using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VideoBatch {
    /// <summary>Расписание Shorts: 2–5 мин внутри аккаунта, 10–15 мин между аккаунтами.</summary>
    public static class ScheduleGenerator {
        const int WithinAccountMinMinutes=2;
        const int WithinAccountMaxMinutes=5;
        const int BetweenAccountsMinMinutes=10;
        const int BetweenAccountsMaxMinutes=15;

        static readonly Random Rng=new Random();

        public static List<(string date,string time)> GenerateForProfileBatches(IList<int> itemCountsPerProfile,string statePath){
            var slots=new List<(string,string)>();
            if(itemCountsPerProfile==null||itemCountsPerProfile.Count==0)return slots;
            DateTime next=LoadNextSlot(statePath);
            DateTime minimum=DateTime.Now.AddHours(2);
            if(next<minimum)next=new DateTime(DateTime.Now.Year,DateTime.Now.Month,DateTime.Now.Day,12,0,0).AddDays(1);

            for(int p=0;p<itemCountsPerProfile.Count;p++){
                int count=itemCountsPerProfile[p];
                if(count<=0)continue;
                if(p>0)next=next.AddMinutes(Rng.Next(BetweenAccountsMinMinutes,BetweenAccountsMaxMinutes+1));
                for(int i=0;i<count;i++){
                    if(next.Hour>=22)next=new DateTime(next.Year,next.Month,next.Day,9,0,0).AddDays(1);
                    slots.Add((next.ToString("yyyy-MM-dd"),next.ToString("HH:mm")));
                    if(i<count-1)next=next.AddMinutes(Rng.Next(WithinAccountMinMinutes,WithinAccountMaxMinutes+1));
                }
            }
            SaveNextSlot(statePath,next);
            return slots;
        }

        static DateTime LoadNextSlot(string statePath){
            try{
                string json=File.ReadAllText(statePath);
                int i=json.IndexOf("\"next\"",StringComparison.OrdinalIgnoreCase);
                if(i>=0){
                    int q1=json.IndexOf('"',i+6);
                    int q2=json.IndexOf('"',q1+1);
                    if(q1>=0&&q2>q1&&DateTime.TryParse(json.Substring(q1+1,q2-q1-1),null,System.Globalization.DateTimeStyles.RoundtripKind,out var parsed))
                        return parsed;
                }
            }catch{}
            return DateTime.MinValue;
        }

        static void SaveNextSlot(string statePath,DateTime next){
            try{
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)??Store.Root);
                string temp=statePath+".tmp";
                File.WriteAllText(temp,"{\"next\":\""+next.ToString("o")+"\"}",Encoding.UTF8);
                if(File.Exists(statePath))File.Replace(temp,statePath,null);
                else File.Move(temp,statePath);
            }catch{}
        }
    }
}
