using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace VideoBatch {
    public class Profile {
        public string Format="mp4", Fps="source", Resolution="source", Compression="quality", Metadata="clear", FileDate="now";
        public double Speed=1, Crop=0, Gray=0, Crf=22, Bitrate=4;
        public double ScalePercent=100, FpsPercent=100, BitratePercent=100;
        public DateTime CustomDate=DateTime.Now;
        public int Slot=1;
        public double Brightness=0, Contrast=1, MusicStart=0, MusicVolume=1;
        public string MusicPath="";
        public bool Short=false;
        public static Profile[] Defaults(){return Automatic(10);}
        public static Profile[] Automatic(int count) {
            if(count<1||count>10)throw new Exception("Выберите от 1 до 10 вариантов.");
            var date=DateTime.Now.AddHours(-2);var result=new Profile[10];
            for(int i=0;i<10;i++) {double t=i<count?(count==1?.5:(double)i/(count-1)):(double)i/9;
                result[i]=new Profile{Slot=i+1,Speed=Math.Round(.98+.04*t,4),Crop=Math.Round(.2+t,3),Gray=Math.Round(.5+4.5*t,3),ScalePercent=Math.Round(100-2*t,3),FpsPercent=Math.Round(99+2*t,3),Compression="sourcebitrate",BitratePercent=Math.Round(88+18*t,3),Crf=Math.Round(20+3*t,2),Bitrate=Math.Round(3.5+2*t,3),Format=new[]{"mp4","mov","mkv"}[i%3],Metadata="custom",FileDate="custom",CustomDate=date.AddDays(-i).AddMinutes(-i*17)};
            }return result;
        }
        public void Validate() {
            Check(Speed,.5,2,"Скорость"); Check(Crop,0,15,"Обрезка"); Check(Gray,0,100,"Обесцвечивание"); Check(Crf,16,30,"Сжатие"); Check(Bitrate,.2,100,"Битрейт");
            if(!new[]{"mp4","mov","mkv"}.Contains(Format)) throw new Exception("Неизвестный формат.");
            if(!new[]{"source","1080","720"}.Contains(Resolution)) throw new Exception("Неизвестный размер кадра.");
            if(!new[]{"quality","bitrate","sourcebitrate"}.Contains(Compression)) throw new Exception("Неизвестный режим сжатия.");
            if(!new[]{"clear","now","keep","custom"}.Contains(Metadata) || !new[]{"now","keep","custom"}.Contains(FileDate)) throw new Exception("Неизвестный режим дат.");
            Check(ScalePercent,50,100,"Размер, %");Check(FpsPercent,80,120,"Частота кадров, %");Check(BitratePercent,30,200,"Битрейт, %");
            if(Fps!="source") Check(Core.Parse(Fps),10,120,"FPS");
        }
        public static void Check(double v,double low,double high,string name) { if(double.IsNaN(v)||double.IsInfinity(v)||v<low||v>high) throw new Exception(name+": допустимо от "+low+" до "+high+"."); }
    }
    public class Range {
        public double Min,Max;
        public Range(){} public Range(double min,double max){Min=min;Max=max;}
        public double Sample(Random r){return Math.Round(Min+(Max-Min)*r.NextDouble(),4);}
        public void Validate(double lo,double hi,string name){Profile.Check(Min,lo,hi,name);Profile.Check(Max,lo,hi,name);if(Min>Max)throw new Exception(name+": «от» должно быть не больше «до».");}
    }
    public class ShortSettings {
        public Range Crop=new Range(.3,1.5), Gray=new Range(0,5), Brightness=new Range(-2,2), Contrast=new Range(98,102), Crf=new Range(20,23);
        public string Format="mp4", Fps="source", Resolution="source"; public double Volume=100; public bool Loop=true, TechnicalVariants=true;
        public void Validate(){ Crop.Validate(0,15,"Обрезка");Gray.Validate(0,100,"Обесцвечивание");Brightness.Validate(-10,10,"Яркость");Contrast.Validate(80,120,"Контраст");Crf.Validate(16,30,"Сжатие");Profile.Check(Volume,0,150,"Громкость");new Profile{Format=Format,Fps=Fps,Resolution=Resolution}.Validate(); }
    }
    public class Binding { public string Video="",Audio="";public int Slot=1; }
    public class Preferences {
        public bool Shorts=true; public int ShortCount=10,VideoCount=5;
        public int SettingsVersion=0;public bool AutoProfiles=true;public double BackgroundDb=-7.5;
        public List<string> BackgroundMusic=new List<string>();
        public List<string> BackgroundMusicRu=new List<string>(), BackgroundMusicEn=new List<string>();
        public string Output=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"VideoBatch");
        public Profile[] Profiles=Profile.Defaults(); public ShortSettings Ranges=new ShortSettings();
        public List<string> Music=new List<string>(); public List<string> MusicRu=new List<string>(), MusicEn=new List<string>();
        public string MusicMarketView="RU";
        public List<Binding> Narrations=new List<Binding>();
        public string Provider="windows", WindowsVoice="", ElevenVoice="", ProtectedKey="";
        public string ProtectedDolphinToken=""; public int DolphinPort=3001;
        public int MaxParallelUploads=3;
        /// <summary>safe | normal | fast | custom | auto</summary>
        public string HttpWorkerPreset="normal";
        public int HttpWorkerCustomCount=3;
        /// <summary>scheduled | immediate | private</summary>
        public string YouTubeHttpPublishMode="scheduled";
        public string YouTubeHttpLongPublishMode="scheduled";
        public string UploadStagingFolder=@"C:\VideoBatch\Upload";
        public List<YouTubeChannel> YouTubeChannels=new List<YouTubeChannel>();
        public string YouTubeSearchTitle="", YouTubeSearchUrl="", YouTubeSearchFilter="today";
        public string YouTubeMarketView="RU";
        /// <summary>Активная вкладка Shorts/Long в workspace (не скрывает каналы).</summary>
        public string YouTubeKindView="shorts";
        public bool YouTubeLongScheduled20260923Migrated;
        public bool YouTubeNetworkSchedule20260923Migrated;
        public string YouTubeSearchTitleRu="", YouTubeSearchUrlRu="", YouTubeSearchFilterRu="today";
        public string YouTubeSearchTitleEn="", YouTubeSearchUrlEn="", YouTubeSearchFilterEn="today";
        public string YouTubeSearchKeysRu="", YouTubeSearchFullTitleRu="";
        public string YouTubeSearchKeysEn="", YouTubeSearchFullTitleEn="";
        // Базы заголовков: один раз заполняете, дальше приложение само берёт нужное число (база не съедается).
        public List<string> TitleBankLongRu=new List<string>(), TitleBankLongEn=new List<string>();
        public List<string> TitleBankShortsRu=new List<string>(), TitleBankShortsEn=new List<string>();
        public int TitleCursorLongRu, TitleCursorLongEn, TitleCursorShortsRu, TitleCursorShortsEn;
        public bool TitleBanksSeeded;
        // TikTok: те же Profile ID Dolphin, отдельный список аккаунтов и база подписей.
        public List<TikTokAccount> TikTokAccounts=new List<TikTokAccount>();
        public string TikTokMarketView="RU";
        public List<string> TikTokCaptionBankRu=new List<string>(), TikTokCaptionBankEn=new List<string>();
        public List<string> TikTokDescriptionBankRu=new List<string>(), TikTokDescriptionBankEn=new List<string>();
        // Полные подписи: старые банки заголовков и описаний сохраняются для совместимости.
        public List<string> TikTokFullCaptionBankRu=new List<string>();
        public int TikTokCaptionCursorRu, TikTokCaptionCursorEn;
        public int TikTokMaxParallelUploads=1;
        public int TikTokUploadStaggerMinMinutes=2;
        public int TikTokUploadStaggerMaxMinutes=20;
        public int WatchMaxParallelProfiles=3;
        /// <summary>Min pause between Dolphin profile start requests (ms).</summary>
        public int ProfileLaunchStaggerMinMs=3000;
        /// <summary>Max pause between Dolphin profile start requests (ms).</summary>
        public int ProfileLaunchStaggerMaxMs=5000;
        public List<string> TikTokMusicRu=new List<string>(), TikTokMusicEn=new List<string>();
        /// <summary>Последний выбранный Profile ID (Dolphin) для быстрого «Добавить видео».</summary>
        public string LastSelectedYouTubeProfileIdRu="", LastSelectedYouTubeProfileIdEn="";
        public string LastSelectedYouTubeChannelIdRu="", LastSelectedYouTubeChannelIdEn="";
        public string LastSelectedTikTokProfileIdRu="", LastSelectedTikTokProfileIdEn="";
        public bool KeepDolphinProfileOpenAfterUpload=true;
        /// <summary>random | period</summary>
        public string YouTubeScheduleMode="network";
        public int YouTubeScheduleMinMinutes=15, YouTubeScheduleMaxMinutes=20;
        /// <summary>Длительность «круга» сети публикаций (мин), время ПК.</summary>
        public int YouTubeScheduleNetworkPeriodMinutes=480;
        public int YouTubeScheduleNetworkMinMinutes=3, YouTubeScheduleNetworkMaxMinutes=15;
        public bool YouTubeShortSchedule20260922Migrated;
        public int YouTubeScheduleLeadMinutes=30;
        public int YouTubeScheduleLongMinMinutes=1, YouTubeScheduleLongMaxMinutes=5;
        public int YouTubeSchedulePeriodMinGapMinutes=10;
        public string YouTubeScheduleFirstPublish="";
        public string YouTubeSchedulePeriodStart="", YouTubeSchedulePeriodEnd="";
    }

    public static class TikTokCaptionTemplates {
        public const string HashTags="#BinoDex #обучениетрейдингу #pocketoption #трейдинг #aitrading";

        public static List<string> RussianDefaults(){return new List<string>{
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nТестирую ИИ трейдинг и AI-инструменты для анализа рынка. В этом видео — Binodex: обзор платформы, сигналы, стратегия и мой опыт использования. Показываю трейдинг для начинающих, обучение трейдингу с нуля и разбираю, как работает ИИ бот для трейдинга и анализа бинарных опционов.\n\nBinodex / Бинодекс • Binodex трейдинг • Binodex обзор • Binodex сигналы • ИИ трейдинг • трейдинг с нуля\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nПоказываю, как использовать ИИ трейдинг и AI-инструменты для анализа рынка. В ролике разбираю Binodex: обзор платформы, сигналы, стратегия и личный опыт. Объясняю трейдинг для начинающих, обучение трейдингу с нуля и работу ИИ бота для анализа бинарных опционов.\n\nBinodex обзор • Binodex сигналы • Binodex трейдинг • Бинодекс • ИИ трейдинг • трейдинг с нуля\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nВ этом видео тестирую Binodex и инструменты для ИИ трейдинга. Показываю платформу, сигналы и стратегию, делюсь опытом использования. Также разбираю обучение трейдингу с нуля, трейдинг для начинающих и принцип работы AI-бота для анализа рынка и бинарных опционов.\n\nБинодекс • Binodex обзор • Binodex трейдинг • Binodex сигналы • трейдинг с нуля • ИИ трейдинг\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nРазбираю Binodex и возможности AI-инструментов для трейдинга. В видео показываю обзор платформы, торговые сигналы, стратегию и свой опыт. Материал подойдёт тем, кто изучает трейдинг с нуля и хочет понять, как ИИ бот анализирует рынок бинарных опционов.\n\nBinodex трейдинг • Binodex обзор • Бинодекс • ИИ трейдинг • Binodex сигналы • трейдинг с нуля\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nТестирую платформу Binodex вместе с ИИ-инструментами анализа рынка. Рассматриваю сигналы, стратегию и основные функции платформы. Объясняю трейдинг для начинающих и показываю, как AI-бот применяется в анализе бинарных опционов.\n\nBinodex сигналы • Binodex обзор • Binodex трейдинг • ИИ трейдинг • Бинодекс • трейдинг с нуля\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nПроверяю, как работает Binodex для анализа рынка и ИИ трейдинга. В ролике — обзор платформы, сигналы, торговая стратегия и личный опыт. Показываю основы трейдинга с нуля и работу ИИ бота с бинарными опционами.\n\nИИ трейдинг • Binodex трейдинг • Binodex сигналы • Binodex обзор • Бинодекс • трейдинг с нуля\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nЗнакомлюсь с платформой Binodex и тестирую её AI-инструменты. Разбираю торговые сигналы, стратегию и возможности анализа рынка. Видео рассчитано на начинающих, которые изучают трейдинг с нуля и применение ИИ бота в бинарных опционах.\n\nBinodex / Бинодекс • Binodex обзор • ИИ трейдинг • Binodex сигналы • Binodex трейдинг • трейдинг с нуля\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nВ видео показываю Binodex, сигналы платформы и стратегию работы с рынком. Тестирую ИИ трейдинг, делюсь своим опытом и объясняю базовые принципы для начинающих. Отдельно разбираю, как AI-бот анализирует рынок бинарных опционов.\n\nBinodex сигналы • ИИ трейдинг • Binodex обзор • Бинодекс • трейдинг с нуля • Binodex трейдинг\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nДелаю обзор Binodex и проверяю инструменты ИИ для анализа рынка. Показываю сигналы, торговую стратегию и практическое использование платформы. Объясняю трейдинг с нуля и возможности AI-бота для работы с бинарными опционами.\n\nBinodex обзор • Бинодекс • ИИ трейдинг • Binodex трейдинг • трейдинг с нуля • Binodex сигналы\n\n"+HashTags,
            "👇 ССЫЛКА НА AI БОТ — В ПРОФИЛЕ\n\nИзучаю Binodex и тестирую AI-инструменты для трейдинга. В этом ролике рассматриваю платформу, сигналы, стратегию и анализ рынка. Показываю понятный разбор для начинающих и объясняю работу ИИ бота с бинарными опционами.\n\nТрейдинг с нуля • ИИ трейдинг • Binodex сигналы • Binodex трейдинг • Binodex обзор • Бинодекс\n\n"+HashTags
        };}

        public static List<string> Allocate(IList<string> bank,int count,ref int cursor){
            if(bank==null||bank.Count==0)throw new Exception("Банк подписей TikTok пуст.");
            var clean=bank.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            if(clean.Count==0)throw new Exception("Банк подписей TikTok пуст.");
            cursor=((cursor%clean.Count)+clean.Count)%clean.Count;
            var result=new List<string>();
            for(int i=0;i<count;i++)result.Add(clean[(cursor+i)%clean.Count]);
            cursor=(cursor+count)%clean.Count;
            return result;
        }

        public static bool RunSelfTests(){
            var bank=RussianDefaults();
            if(bank.Count!=10||bank.Distinct(StringComparer.Ordinal).Count()!=10)return false;
            if(bank.Any(x=>x.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Last().Trim()!=HashTags))return false;
            int cursor=0;var first=Allocate(bank,10,ref cursor);var next=Allocate(bank,1,ref cursor);
            return first.Count==10&&next.Count==1&&next[0]==first[0]&&bank.Count==10;
        }
    }
    public static class Store {
        public static string Root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VideoBatchDesktop");
        public static string Config{get{return Path.Combine(Root,"settings.xml");}}
        public static string MeshCatalogPath{get{return Path.Combine(Root,"youtube-mesh-catalog.json");}}
        public static T Clone<T>(T data){var ser=new XmlSerializer(typeof(T));using(var m=new MemoryStream()){ser.Serialize(m,data);m.Position=0;return (T)ser.Deserialize(m);}}
        public static string ExtractYouTubeVideoId(string url){
            if(string.IsNullOrWhiteSpace(url))return "";
            var m=System.Text.RegularExpressions.Regex.Match(url,@"(?:youtu\.be\/|v=|\/shorts\/|\/embed\/|\/live\/)([A-Za-z0-9_-]{11})");
            return m.Success?m.Groups[1].Value:"";
        }
        static string JsonQuote(string s){return "\""+(s??"").Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n")+"\"";}
        /// <summary>База сетки: канал, profileId, заголовок, videoId, url — для просмотра друг другом.</summary>
        public static void SaveMeshCatalog(IEnumerable<YouTubeChannel> channels){
            try{
                Directory.CreateDirectory(Root);
                var sb=new StringBuilder();sb.Append("{\"updated\":").Append(JsonQuote(DateTime.Now.ToString("o"))).Append(",\"videos\":[");
                bool first=true;
                foreach(var ch in channels??Enumerable.Empty<YouTubeChannel>()){
                    if(ch==null)continue;
                    if(ch.Items==null)ch.Items=new List<YouTubeItem>();
                    foreach(var it in YouTubeMeshCatalog.CurrentBatchItems(ch)){
                        if(it==null||string.IsNullOrWhiteSpace(it.Title))continue;
                        if(string.IsNullOrWhiteSpace(it.PublishedVideoId)&&!string.IsNullOrWhiteSpace(it.PublishedUrl))
                            it.PublishedVideoId=ExtractYouTubeVideoId(it.PublishedUrl);
                        if(!first)sb.Append(",");
                        first=false;
                        sb.Append("{");
                        sb.Append("\"channel\":").Append(JsonQuote(ch.Name??""));
                        sb.Append(",\"profileId\":").Append(JsonQuote(ch.ProfileId??""));
                        sb.Append(",\"market\":").Append(JsonQuote(ch.Market??"RU"));
                        sb.Append(",\"kind\":").Append(JsonQuote(ch.Kind??"long"));
                        sb.Append(",\"title\":").Append(JsonQuote(it.Title??""));
                        sb.Append(",\"videoId\":").Append(JsonQuote(it.PublishedVideoId??""));
                        sb.Append(",\"url\":").Append(JsonQuote(it.PublishedUrl??""));
                        sb.Append(",\"channelUrl\":").Append(JsonQuote(ch.ChannelUrl??""));
                        sb.Append("}");
                    }
                }
                sb.Append("]}");
                string temp=MeshCatalogPath+".tmp";
                File.WriteAllText(temp,sb.ToString(),Encoding.UTF8);
                if(File.Exists(MeshCatalogPath))File.Replace(temp,MeshCatalogPath,null);else File.Move(temp,MeshCatalogPath);
            }catch{}
        }
        public static void Normalize(Preferences p){
            if(p.Profiles==null||p.Profiles.Length<1||p.Profiles.Length>10||p.ShortCount<1||p.ShortCount>10||p.VideoCount<1||p.VideoCount>10)throw new Exception("Повреждены настройки.");
            if(p.SettingsVersion<1){p.Profiles=Profile.Automatic(p.VideoCount);p.AutoProfiles=true;p.SettingsVersion=1;}
            if(p.YouTubeChannels==null)p.YouTubeChannels=new List<YouTubeChannel>();
            if(p.DolphinPort<1||p.DolphinPort>65535)p.DolphinPort=3001;
            if(string.IsNullOrWhiteSpace(p.YouTubeSearchFilter))p.YouTubeSearchFilter="today";
            if(p.YouTubeSearchTitle==null)p.YouTubeSearchTitle="";
            if(p.YouTubeSearchUrl==null)p.YouTubeSearchUrl="";
            if(string.IsNullOrWhiteSpace(p.YouTubeMarketView)||(p.YouTubeMarketView!="RU"&&p.YouTubeMarketView!="EN"))p.YouTubeMarketView="RU";
            if(string.IsNullOrWhiteSpace(p.YouTubeKindView)||(p.YouTubeKindView!="shorts"&&p.YouTubeKindView!="long"))p.YouTubeKindView="shorts";
            if(!p.YouTubeLongScheduled20260923Migrated){
                if(string.Equals((p.YouTubeHttpLongPublishMode??"").Trim(),"immediate",StringComparison.OrdinalIgnoreCase))
                    p.YouTubeHttpLongPublishMode="scheduled";
                p.YouTubeLongScheduled20260923Migrated=true;
            }
            if(!p.YouTubeNetworkSchedule20260923Migrated){
                p.YouTubeScheduleMode="network";
                p.YouTubeNetworkSchedule20260923Migrated=true;
            }
            if(string.IsNullOrWhiteSpace(p.YouTubeScheduleMode))p.YouTubeScheduleMode="network";
            if(p.YouTubeScheduleNetworkPeriodMinutes<60)p.YouTubeScheduleNetworkPeriodMinutes=480;
            if(p.YouTubeScheduleNetworkPeriodMinutes>24*60)p.YouTubeScheduleNetworkPeriodMinutes=24*60;
            if(p.YouTubeScheduleNetworkMinMinutes<1)p.YouTubeScheduleNetworkMinMinutes=3;
            if(p.YouTubeScheduleNetworkMaxMinutes<p.YouTubeScheduleNetworkMinMinutes)p.YouTubeScheduleNetworkMaxMinutes=p.YouTubeScheduleNetworkMinMinutes;
            if(p.YouTubeScheduleNetworkMaxMinutes>60)p.YouTubeScheduleNetworkMaxMinutes=60;
            // Старое значение по умолчанию 10–60 было чрезмерным для пачек Shorts.
            // Индивидуальные настройки сохраняются без изменения.
            if(!p.YouTubeShortSchedule20260922Migrated){
                if(p.YouTubeScheduleMinMinutes==10&&p.YouTubeScheduleMaxMinutes==60){p.YouTubeScheduleMinMinutes=15;p.YouTubeScheduleMaxMinutes=20;}
                p.YouTubeShortSchedule20260922Migrated=true;
            }
            if(p.YouTubeScheduleMinMinutes<1)p.YouTubeScheduleMinMinutes=15;
            if(p.YouTubeScheduleMaxMinutes<p.YouTubeScheduleMinMinutes)p.YouTubeScheduleMaxMinutes=p.YouTubeScheduleMinMinutes;
            if(p.YouTubeScheduleLongMinMinutes<1)p.YouTubeScheduleLongMinMinutes=1;
            if(p.YouTubeScheduleLongMaxMinutes<p.YouTubeScheduleLongMinMinutes)p.YouTubeScheduleLongMaxMinutes=5;
            if(p.YouTubeScheduleLeadMinutes<ScheduleGenerator.PreflightMinLeadMinutes)p.YouTubeScheduleLeadMinutes=ScheduleGenerator.DefaultLeadMinutes;
            if(string.IsNullOrWhiteSpace(p.HttpWorkerPreset))p.HttpWorkerPreset="normal";
            if(p.HttpWorkerCustomCount<1)p.HttpWorkerCustomCount=HttpWorkerSettings.DefaultWorkers;
            if(p.HttpWorkerCustomCount>HttpWorkerSettings.MaxCustomWorkers)p.HttpWorkerCustomCount=HttpWorkerSettings.MaxCustomWorkers;
            if(string.IsNullOrWhiteSpace(p.YouTubeHttpPublishMode))p.YouTubeHttpPublishMode="scheduled";
            if(string.IsNullOrWhiteSpace(p.YouTubeHttpLongPublishMode))p.YouTubeHttpLongPublishMode="scheduled";
            if(p.MaxParallelUploads<1)p.MaxParallelUploads=HttpWorkerSettings.DefaultWorkers;
            if(p.MaxParallelUploads>HttpWorkerSettings.MaxCustomWorkers)p.MaxParallelUploads=HttpWorkerSettings.MaxCustomWorkers;
            if(p.TikTokMaxParallelUploads<1)p.TikTokMaxParallelUploads=1;
            if(p.TikTokMaxParallelUploads>2)p.TikTokMaxParallelUploads=2;
            if(p.TikTokUploadStaggerMinMinutes<1)p.TikTokUploadStaggerMinMinutes=2;
            if(p.TikTokUploadStaggerMaxMinutes<p.TikTokUploadStaggerMinMinutes)p.TikTokUploadStaggerMaxMinutes=p.TikTokUploadStaggerMinMinutes;
            if(p.TikTokUploadStaggerMaxMinutes>120)p.TikTokUploadStaggerMaxMinutes=120;
            TikTokProfileSync.SyncFromYouTube(p, p.TikTokMarketView ?? "RU");
            TikTokProfileSync.SyncFromYouTube(p, p.TikTokMarketView == "EN" ? "RU" : "EN");
            if(p.WatchMaxParallelProfiles<1)p.WatchMaxParallelProfiles=1;
            if(p.WatchMaxParallelProfiles>5)p.WatchMaxParallelProfiles=5;
            if(p.ProfileLaunchStaggerMinMs<1000)p.ProfileLaunchStaggerMinMs=3000;
            if(p.ProfileLaunchStaggerMaxMs<p.ProfileLaunchStaggerMinMs)p.ProfileLaunchStaggerMaxMs=p.ProfileLaunchStaggerMinMs+2000;
            if(p.ProfileLaunchStaggerMaxMs>30000)p.ProfileLaunchStaggerMaxMs=30000;
            if(p.YouTubeSearchTitleRu==null)p.YouTubeSearchTitleRu="";
            if(p.YouTubeSearchUrlRu==null)p.YouTubeSearchUrlRu="";
            if(string.IsNullOrWhiteSpace(p.YouTubeSearchFilterRu))p.YouTubeSearchFilterRu="today";
            if(p.YouTubeSearchTitleEn==null)p.YouTubeSearchTitleEn="";
            if(p.YouTubeSearchUrlEn==null)p.YouTubeSearchUrlEn="";
            if(string.IsNullOrWhiteSpace(p.YouTubeSearchFilterEn))p.YouTubeSearchFilterEn="today";
            if(p.YouTubeSearchKeysRu==null)p.YouTubeSearchKeysRu="";
            if(p.YouTubeSearchFullTitleRu==null)p.YouTubeSearchFullTitleRu="";
            if(p.YouTubeSearchKeysEn==null)p.YouTubeSearchKeysEn="";
            if(p.YouTubeSearchFullTitleEn==null)p.YouTubeSearchFullTitleEn="";
            if(string.IsNullOrWhiteSpace(p.YouTubeSearchKeysRu)&&!string.IsNullOrWhiteSpace(p.YouTubeSearchTitleRu))
                p.YouTubeSearchKeysRu=p.YouTubeSearchTitleRu;
            if(string.IsNullOrWhiteSpace(p.YouTubeSearchKeysEn)&&!string.IsNullOrWhiteSpace(p.YouTubeSearchTitleEn))
                p.YouTubeSearchKeysEn=p.YouTubeSearchTitleEn;
            if(p.TitleBankLongRu==null)p.TitleBankLongRu=new List<string>();
            if(p.TitleBankLongEn==null)p.TitleBankLongEn=new List<string>();
            if(p.TitleBankShortsRu==null)p.TitleBankShortsRu=new List<string>();
            if(p.TitleBankShortsEn==null)p.TitleBankShortsEn=new List<string>();
            p.TitleBankLongRu=p.TitleBankLongRu.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            p.TitleBankLongEn=p.TitleBankLongEn.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            p.TitleBankShortsRu=p.TitleBankShortsRu.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            p.TitleBankShortsEn=p.TitleBankShortsEn.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            p.TitleCursorLongRu=ClampTitleCursor(p.TitleCursorLongRu,p.TitleBankLongRu.Count);
            p.TitleCursorLongEn=ClampTitleCursor(p.TitleCursorLongEn,p.TitleBankLongEn.Count);
            p.TitleCursorShortsRu=ClampTitleCursor(p.TitleCursorShortsRu,p.TitleBankShortsRu.Count);
            p.TitleCursorShortsEn=ClampTitleCursor(p.TitleCursorShortsEn,p.TitleBankShortsEn.Count);
            // Пустые банки — один раз подтянуть готовые заголовки из комплекта рядом с exe.
            if(!p.TitleBanksSeeded||(p.TitleBankLongRu.Count==0&&p.TitleBankLongEn.Count==0&&p.TitleBankShortsRu.Count==0&&p.TitleBankShortsEn.Count==0)){
                if(TryLoadBundledYouTubeTitleBanks(p))p.TitleBanksSeeded=true;
            }
            if(p.TikTokAccounts==null)p.TikTokAccounts=new List<TikTokAccount>();
            if(string.IsNullOrWhiteSpace(p.TikTokMarketView)||(p.TikTokMarketView!="RU"&&p.TikTokMarketView!="EN"))p.TikTokMarketView="RU";
            if(p.TikTokCaptionBankRu==null)p.TikTokCaptionBankRu=new List<string>();
            if(p.TikTokCaptionBankEn==null)p.TikTokCaptionBankEn=new List<string>();
            if(p.TikTokDescriptionBankRu==null)p.TikTokDescriptionBankRu=new List<string>();
            if(p.TikTokDescriptionBankEn==null)p.TikTokDescriptionBankEn=new List<string>();
            if(p.TikTokFullCaptionBankRu==null)p.TikTokFullCaptionBankRu=new List<string>();
            if(p.TikTokMusicRu==null)p.TikTokMusicRu=new List<string>();
            if(p.TikTokMusicEn==null)p.TikTokMusicEn=new List<string>();
            p.TikTokCaptionBankRu=p.TikTokCaptionBankRu.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            p.TikTokCaptionBankEn=p.TikTokCaptionBankEn.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            p.TikTokDescriptionBankRu=p.TikTokDescriptionBankRu.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            p.TikTokDescriptionBankEn=p.TikTokDescriptionBankEn.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            p.TikTokFullCaptionBankRu=p.TikTokFullCaptionBankRu.Select(x=>(x??"").Trim()).Where(x=>x.Length>0).ToList();
            if(p.TikTokFullCaptionBankRu.Count==0)p.TikTokFullCaptionBankRu=TikTokCaptionTemplates.RussianDefaults();
            p.TikTokCaptionCursorRu=((p.TikTokCaptionCursorRu%p.TikTokFullCaptionBankRu.Count)+p.TikTokFullCaptionBankRu.Count)%p.TikTokFullCaptionBankRu.Count;
            p.TikTokCaptionCursorEn=p.TikTokDescriptionBankEn.Count==0?0:((p.TikTokCaptionCursorEn%p.TikTokDescriptionBankEn.Count)+p.TikTokDescriptionBankEn.Count)%p.TikTokDescriptionBankEn.Count;
            p.TikTokMusicRu=p.TikTokMusicRu.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            p.TikTokMusicEn=p.TikTokMusicEn.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Если музыка TikTok ещё пустая — один раз копируем из коротких (общая база).
            if(p.TikTokMusicRu.Count==0&&(p.MusicRu??new List<string>()).Count>0)p.TikTokMusicRu=new List<string>(p.MusicRu);
            if(p.TikTokMusicEn.Count==0&&(p.MusicEn??new List<string>()).Count>0)p.TikTokMusicEn=new List<string>(p.MusicEn);
            foreach(var a in p.TikTokAccounts){
                if(a==null)continue;
                if(string.IsNullOrWhiteSpace(a.Market)||(a.Market!="RU"&&a.Market!="EN"))a.Market="RU";
                if(a.Items==null)a.Items=new List<TikTokItem>();
                if(!string.IsNullOrWhiteSpace(a.Description)&&(string.IsNullOrWhiteSpace(a.Caption)||a.Description.Length>a.Caption.Length))a.Caption=a.Description;
                if(string.IsNullOrWhiteSpace(a.Caption))a.Caption=a.Title;
                foreach(var it in a.Items){
                    if(it==null)continue;
                    if(!string.IsNullOrWhiteSpace(it.Description)&&(string.IsNullOrWhiteSpace(it.Caption)||it.Description.Length>it.Caption.Length))it.Caption=it.Description;
                    if(string.IsNullOrWhiteSpace(it.Caption))it.Caption=it.Title;
                    it.Caption=(it.Caption??"").Trim();
                    it.Description=it.Caption;
                }
                if(a.Items.Count==0&&(!string.IsNullOrWhiteSpace(a.Video)||!string.IsNullOrWhiteSpace(a.Title)||!string.IsNullOrWhiteSpace(a.Caption)))
                    a.Items.Add(new TikTokItem{Video=a.Video??"",Title=a.Title??"",Description=a.Caption??"",Caption=a.Caption??""});
                if(a.Items.Count>0){
                    a.Video=a.Items[0].Video??"";
                    a.Title=a.Items[0].Title??"";
                    a.Caption=a.Items[0].Caption??"";
                    a.Description=a.Caption;
                }
            }
            // Одноразовый перенос старых общих полей поиска в RU, если раздельные ещё пустые.
            if(string.IsNullOrWhiteSpace(p.YouTubeSearchTitleRu)&&string.IsNullOrWhiteSpace(p.YouTubeSearchTitleEn)&&!string.IsNullOrWhiteSpace(p.YouTubeSearchTitle))
                p.YouTubeSearchTitleRu=p.YouTubeSearchTitle;
            if(string.IsNullOrWhiteSpace(p.YouTubeSearchUrlRu)&&string.IsNullOrWhiteSpace(p.YouTubeSearchUrlEn)&&!string.IsNullOrWhiteSpace(p.YouTubeSearchUrl))
                p.YouTubeSearchUrlRu=p.YouTubeSearchUrl;
            foreach(var c in p.YouTubeChannels){
                if(c==null)continue;
                if(string.IsNullOrWhiteSpace(c.ChannelId))c.ChannelId=Guid.NewGuid().ToString("N");
                if(string.IsNullOrWhiteSpace(c.Market)||(c.Market!="RU"&&c.Market!="EN"))c.Market="RU";
                if(string.IsNullOrWhiteSpace(c.Kind)||(c.Kind!="long"&&c.Kind!="shorts"))c.Kind="long";
                if(c.Items==null)c.Items=new List<YouTubeItem>();
                // Старые одиночные Video/Title поднимаем в Items, если очередь пуста.
                if(c.Items.Count==0&&(!string.IsNullOrWhiteSpace(c.Video)||!string.IsNullOrWhiteSpace(c.Title)))
                    c.Items.Add(new YouTubeItem{Video=c.Video??"",Title=c.Title??"",Thumbnail=c.Thumbnail??""});
                foreach(var it in c.Items){
                    if(it==null)continue;
                    it.Title=CleanStoredTitle(it.Title);
                    if(it.PublishedVideoId==null)it.PublishedVideoId="";
                    if(it.PublishedUrl==null)it.PublishedUrl="";
                    if(string.IsNullOrWhiteSpace(it.PublishedVideoId)&&!string.IsNullOrWhiteSpace(it.PublishedUrl))
                        it.PublishedVideoId=Store.ExtractYouTubeVideoId(it.PublishedUrl);
                }
                if(c.ChannelUrl==null)c.ChannelUrl="";
                if(c.Items.Count>0){c.Video=c.Items[0].Video??"";c.Title=c.Items[0].Title??"";c.Thumbnail=c.Items[0].Thumbnail??"";}
                else c.Title=CleanStoredTitle(c.Title);
            }
            // Схлопываем дубликаты одного канала (раньше пачка создавала клоны строк).
            p.YouTubeChannels=MergeYouTubeChannels(p.YouTubeChannels);
            if(p.Music==null)p.Music=new List<string>();
            if(p.MusicRu==null)p.MusicRu=new List<string>();
            if(p.MusicEn==null)p.MusicEn=new List<string>();
            if(p.BackgroundMusic==null)p.BackgroundMusic=new List<string>();
            if(p.BackgroundMusicRu==null)p.BackgroundMusicRu=new List<string>();
            if(p.BackgroundMusicEn==null)p.BackgroundMusicEn=new List<string>();
            if(string.IsNullOrWhiteSpace(p.MusicMarketView)||(p.MusicMarketView!="RU"&&p.MusicMarketView!="EN"))p.MusicMarketView="RU";
            // Текущие (старые) треки считаем русскими, если раздельные списки ещё пустые.
            if(p.MusicRu.Count==0&&p.MusicEn.Count==0&&p.Music.Count>0)p.MusicRu=new List<string>(p.Music);
            if(p.BackgroundMusicRu.Count==0&&p.BackgroundMusicEn.Count==0&&p.BackgroundMusic.Count>0)p.BackgroundMusicRu=new List<string>(p.BackgroundMusic);
            // Обратная совместимость: Music / BackgroundMusic = активный рынок.
            p.Music=new List<string>(p.MusicMarketView=="EN"?p.MusicEn:p.MusicRu);
            p.BackgroundMusic=new List<string>(p.MusicMarketView=="EN"?p.BackgroundMusicEn:p.BackgroundMusicRu);
            if(p.SettingsVersion<2)p.SettingsVersion=2;
            if(p.Profiles.Length<10){var expanded=Profile.Defaults();Array.Copy(p.Profiles,expanded,p.Profiles.Length);p.Profiles=expanded;}
            foreach(var x in p.Profiles)x.Validate();p.Ranges.Validate();Profile.Check(p.BackgroundDb,-60,-.1,"Громкость фона, дБ");
        }
        public static List<YouTubeChannel> NormalizeChannels(List<YouTubeChannel> list){return MergeYouTubeChannels(list);}
        /// <summary>Сводка пачки « · +N» раньше попадала в Title из ячейки таблицы.</summary>
        public static string CleanStoredTitle(string title){
            string t=(title??"").Trim();
            t=System.Text.RegularExpressions.Regex.Replace(t,@"\s*[·•]\s*\+\d+\s*$","");
            t=System.Text.RegularExpressions.Regex.Replace(t,@"\s+\+\d+\s*$","");
            t=System.Text.RegularExpressions.Regex.Replace(t,@"\s+"," ").Trim();
            if(t.Length>100)t=t.Substring(0,100).Trim();
            return t;
        }
        public static string BundledYouTubeTitleBanksPath(){
            return Path.Combine(AppPaths.UploaderRoot,"youtube-title-banks.json");
        }
        [DataContract] class YouTubeTitleBanksDto {
            [DataMember] public string[] TitleBankLongRu;
            [DataMember] public string[] TitleBankLongEn;
            [DataMember] public string[] TitleBankShortsRu;
            [DataMember] public string[] TitleBankShortsEn;
        }
        static List<string> CleanTitleList(IEnumerable<string> src){
            return (src??Enumerable.Empty<string>()).Select(x=>(x??"").Trim()).Where(x=>x.Length>0).Select(x=>x.Length>100?x.Substring(0,100).Trim():x).ToList();
        }
        public static int ClampTitleCursor(int cursor,int bankCount){
            if(bankCount<=0)return 0;
            cursor=cursor%bankCount;
            if(cursor<0)cursor+=bankCount;
            return cursor;
        }
        public static bool TryLoadBundledYouTubeTitleBanks(Preferences p,bool overwrite=false){
            try{
                string path=BundledYouTubeTitleBanksPath();
                if(!File.Exists(path))return false;
                YouTubeTitleBanksDto dto;
                using(var f=File.OpenRead(path))dto=(YouTubeTitleBanksDto)new DataContractJsonSerializer(typeof(YouTubeTitleBanksDto)).ReadObject(f);
                if(dto==null)return false;
                var longRu=CleanTitleList(dto.TitleBankLongRu);
                var longEn=CleanTitleList(dto.TitleBankLongEn);
                var shortRu=CleanTitleList(dto.TitleBankShortsRu);
                var shortEn=CleanTitleList(dto.TitleBankShortsEn);
                if(longRu.Count+longEn.Count+shortRu.Count+shortEn.Count==0)return false;
                if(overwrite||p.TitleBankLongRu.Count==0)p.TitleBankLongRu=longRu;
                if(overwrite||p.TitleBankLongEn.Count==0)p.TitleBankLongEn=longEn;
                if(overwrite||p.TitleBankShortsRu.Count==0)p.TitleBankShortsRu=shortRu;
                if(overwrite||p.TitleBankShortsEn.Count==0)p.TitleBankShortsEn=shortEn;
                if(overwrite){
                    p.TitleCursorLongRu=ClampTitleCursor(p.TitleCursorLongRu,p.TitleBankLongRu.Count);
                    p.TitleCursorLongEn=ClampTitleCursor(p.TitleCursorLongEn,p.TitleBankLongEn.Count);
                    p.TitleCursorShortsRu=ClampTitleCursor(p.TitleCursorShortsRu,p.TitleBankShortsRu.Count);
                    p.TitleCursorShortsEn=ClampTitleCursor(p.TitleCursorShortsEn,p.TitleBankShortsEn.Count);
                }
                return true;
            }catch{return false;}
        }
        static List<YouTubeChannel> MergeYouTubeChannels(List<YouTubeChannel> list){
            if(list==null)return new List<YouTubeChannel>();
            var result=new List<YouTubeChannel>();
            foreach(var c in list){
                if(c==null)continue;
                if(c.Items==null)c.Items=new List<YouTubeItem>();
                var existing=result.FirstOrDefault(x=>{
                    if(x==null||c==null)return false;
                    if(!string.IsNullOrWhiteSpace(c.ChannelId)&&!string.IsNullOrWhiteSpace(x.ChannelId)
                        &&string.Equals(x.ChannelId.Trim(),c.ChannelId.Trim(),StringComparison.OrdinalIgnoreCase))return true;
                    if(string.IsNullOrWhiteSpace(c.ProfileId))return false;
                    return string.Equals((x.Market??"RU"),(c.Market??"RU"),StringComparison.OrdinalIgnoreCase)
                        && string.Equals((x.ProfileId??"").Trim(),(c.ProfileId??"").Trim(),StringComparison.OrdinalIgnoreCase)
                        && string.Equals((x.Name??"").Trim(),(c.Name??"").Trim(),StringComparison.OrdinalIgnoreCase);
                });
                if(existing==null){result.Add(c);continue;}
                if(existing.Items==null)existing.Items=new List<YouTubeItem>();
                foreach(var it in c.Items){
                    if(it==null)continue;
                    if(string.IsNullOrWhiteSpace(it.Video)&&string.IsNullOrWhiteSpace(it.Title))continue;
                    bool dup=existing.Items.Any(e=>string.Equals(e.Video??"",it.Video??"",StringComparison.OrdinalIgnoreCase)&&string.Equals(e.Title??"",it.Title??"",StringComparison.OrdinalIgnoreCase));
                    if(!dup)existing.Items.Add(it);
                }
                if(!string.IsNullOrWhiteSpace(c.ExpectedIp)&&string.IsNullOrWhiteSpace(existing.ExpectedIp))existing.ExpectedIp=c.ExpectedIp;
                if(existing.Items.Count>0){existing.Video=existing.Items[0].Video;existing.Title=existing.Items[0].Title;existing.Thumbnail=existing.Items[0].Thumbnail;}
                if(existing.Enabled||c.Enabled)existing.Enabled=true;
            }
            return result;
        }
        public static Preferences Load(){Directory.CreateDirectory(Root);Preferences p;if(!File.Exists(Config)){p=new Preferences();Normalize(p);return p;}using(var f=File.OpenRead(Config))p=(Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(f);
            if(p.SettingsVersion<1&&!File.Exists(Config+".v3-backup"))File.Copy(Config,Config+".v3-backup");Normalize(p);return p;}
        static readonly object SaveLock=new object();
        public static void Save(Preferences p){
            lock(SaveLock){
                Directory.CreateDirectory(Root);
                string temp=Config+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{
                    using(var f=File.Create(temp))new XmlSerializer(typeof(Preferences)).Serialize(f,p);
                    if(File.Exists(Config))File.Replace(temp,Config,null);else File.Move(temp,Config);
                }finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
            }
        }
    }
    [DataContract] public class ProbeResult {[DataMember]public ProbeStream[] streams;[DataMember]public ProbeFormat format;}
    [DataContract] public class ProbeFormat {[DataMember]public string duration,bit_rate;[DataMember]public Dictionary<string,string> tags;}
    [DataContract] public class Disposition {[DataMember]public int attached_pic;}
    [DataContract] public class SideData {[DataMember]public double rotation;}
    [DataContract] public class ProbeStream {
        [DataMember]public int index;[DataMember]public string codec_type,codec_name,duration,avg_frame_rate,r_frame_rate,color_transfer,sample_aspect_ratio,bit_rate;
        [DataMember]public int width,height;[DataMember]public Disposition disposition;[DataMember]public SideData[] side_data_list;[DataMember]public Dictionary<string,string> tags;
    }
    public class Media {public int Width,Height,VideoIndex,AudioIndex;public double Duration,VideoDuration,VideoBitrate;public string Fps;public bool HasAudio;}
    public class Track {public string Path;public double Duration;}
    public class Update {public string Text;public double Percent;public Update(string s,double p){Text=s;Percent=p;}}
    public class BatchResult {public List<string> Outputs=new List<string>(),Errors=new List<string>();public bool Cancelled;}
    public class BatchJob {
        public List<string> Inputs=new List<string>(),Music=new List<string>(); public List<Binding> Narrations=new List<Binding>();
        public double BackgroundDb=-7.5;
        public bool Shorts; public int Count=5;public string Output,FFmpeg,FFprobe;public Profile[] Profiles=Profile.Defaults(); public ShortSettings Ranges=new ShortSettings();
    }
    public static class Core {
        public static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        public static string N(double n){return n.ToString("0.########",Inv);}
        public static double Parse(string s){double v;if(!double.TryParse(s,NumberStyles.Float,Inv,out v)||double.IsNaN(v)||double.IsInfinity(v))return 0;return v;}
        public static string Quote(string s){return "\""+Regex.Replace(Regex.Replace(s,@"(\\*)""","$1$1\\\""),@"(\\+)$","$1$1")+"\"";}
        public static async Task<string> Tool(string exe,IEnumerable<string> arguments,CancellationToken cancel,Action<string> line=null,int timeout=0) {
            cancel.ThrowIfCancellationRequested();var info=new ProcessStartInfo(exe,string.Join(" ",arguments.Select(Quote))){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            using(var process=new Process{StartInfo=info}) using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
                if(timeout>0)linked.CancelAfter(TimeSpan.FromSeconds(timeout));
                if(!process.Start())throw new Exception("Не удалось запустить обработчик видео.");
                var error=process.StandardError.ReadToEndAsync();var text=new StringBuilder();
                var read=Task.Run(async ()=>{string s;while((s=await process.StandardOutput.ReadLineAsync().ConfigureAwait(false))!=null){if(text.Length<1048576)text.AppendLine(s);if(line!=null)line(s);}});
                using(linked.Token.Register(()=>{try{if(!process.HasExited)process.Kill();}catch{}})) {
                    try {
                        await Task.Run(()=>{while(!process.WaitForExit(100)){linked.Token.ThrowIfCancellationRequested();}}).ConfigureAwait(false);
                        await read.ConfigureAwait(false);string err=await error.ConfigureAwait(false);
                        if(linked.IsCancellationRequested){cancel.ThrowIfCancellationRequested();throw new Exception("Обработчик не ответил вовремя.");}
                        if(process.ExitCode!=0)throw new Exception(string.IsNullOrWhiteSpace(err)?"Обработчик завершился с ошибкой.":err.Substring(0,Math.Min(4000,err.Length)));
                        return text.ToString();
                    } catch(OperationCanceledException) { if(!cancel.IsCancellationRequested)throw new Exception("Обработчик не ответил вовремя.");throw; } finally {try{if(!process.HasExited){process.Kill();process.WaitForExit();}}catch{} try{await read.ConfigureAwait(false);await error.ConfigureAwait(false);}catch{}}
                }
            }
        }
        public static async Task<ProbeResult> Probe(string exe,string path,CancellationToken ct) {
            var json=await Tool(exe,new[]{"-v","error","-show_streams","-show_format","-of","json",path},ct,null,45).ConfigureAwait(false);
            using(var m=new MemoryStream(Encoding.UTF8.GetBytes(json)))return (ProbeResult)new DataContractJsonSerializer(typeof(ProbeResult),new DataContractJsonSerializerSettings{UseSimpleDictionaryFormat=true}).ReadObject(m);
        }
        public static Media VideoInfo(ProbeResult raw) {
            var v=(raw.streams??new ProbeStream[0]).FirstOrDefault(x=>x.codec_type=="video"&&(x.disposition==null||x.disposition.attached_pic!=1));
            if(v==null)throw new Exception("В файле нет видеодорожки.");
            if(v.color_transfer=="smpte2084"||v.color_transfer=="arib-std-b67")throw new Exception("Этот ролик в HDR. Сначала экспортируйте его в SDR.");
            double w=v.width,h=v.height,sar=1;var r=Regex.Match(v.sample_aspect_ratio??"",@"^(\d+):(\d+)$");if(r.Success&&Parse(r.Groups[2].Value)>0)sar=Parse(r.Groups[1].Value)/Parse(r.Groups[2].Value);
            double rotate=(v.side_data_list??new SideData[0]).Select(x=>x.rotation).FirstOrDefault();if(v.tags!=null&&v.tags.ContainsKey("rotate"))rotate=Parse(v.tags["rotate"]);
            if(Math.Abs(rotate)%180==90){double t=w;w=h;h=t;sar=1/sar;}
            w=Math.Floor(w*sar/2)*2;h=Math.Floor(h/2)*2;if(w<16||h<16)throw new Exception("Слишком маленькое разрешение видео.");
            double vd=Parse(v.duration),duration=raw.format==null?0:Parse(raw.format.duration);if(duration<=0)duration=vd;if(vd<=0)vd=duration;if(duration<=0)throw new Exception("Не удалось определить длительность.");
            string fps=v.avg_frame_rate;if(!ValidFps(fps))fps=v.r_frame_rate;if(!ValidFps(fps))throw new Exception("Не удалось определить частоту кадров.");
            var a=raw.streams.FirstOrDefault(x=>x.codec_type=="audio");
            double bitrate=Parse(v.bit_rate);if(bitrate<=0&&raw.format!=null)bitrate=Math.Max(0,Parse(raw.format.bit_rate)-raw.streams.Where(x=>x.codec_type=="audio").Sum(x=>Parse(x.bit_rate)));
            if(bitrate<=0){var parts=fps.Split('/');bitrate=w*h*(Parse(parts[0])/Parse(parts[1]))*.1;}
            return new Media{VideoBitrate=bitrate/1000000,Width=(int)w,Height=(int)h,Duration=duration,VideoDuration=vd,Fps=fps,VideoIndex=v.index,HasAudio=a!=null,AudioIndex=a==null?-1:a.index};
        }
        static bool ValidFps(string fps){return fps!=null&&Regex.IsMatch(fps,@"^[1-9]\d*/[1-9]\d*$");}
        public static double AudioDuration(ProbeResult p){if(p.streams==null||!p.streams.Any(x=>x.codec_type=="audio"))throw new Exception("В файле нет звуковой дорожки.");double d=p.format==null?0:Parse(p.format.duration);if(d<=0)d=p.streams.Where(x=>x.codec_type=="audio").Select(x=>Parse(x.duration)).FirstOrDefault();if(d<=0)throw new Exception("Не удалось определить длину музыки.");return d;}
        public static List<Profile> RandomProfiles(ShortSettings s,int count,List<Track> tracks,double duration,Random random) {
            s.Validate();if(count<1||count>10)throw new Exception("Выберите от 1 до 10 вариантов.");
            var pool=tracks.Where(t=>s.Loop||t.Duration>=duration).ToList();if(pool.Count==0)throw new Exception("Нет подходящей музыки. Добавьте более длинный трек или включите повтор коротких треков.");
            for(int i=pool.Count-1;i>0;i--){int j=random.Next(i+1);var t=pool[i];pool[i]=pool[j];pool[j]=t;}
            var signatures=new HashSet<string>();var result=new List<Profile>();var dates=Profile.Automatic(count);
            for(int i=0;i<count;i++) {
                var track=pool[i%pool.Count];double room=Math.Max(0,track.Duration-duration);double start=i==0?0:i==1?room:i==2?room/2:random.NextDouble()*room;
                if(track.Duration<duration&&i>0)start=random.NextDouble()*track.Duration;
                Profile p=null;
                for(int attempt=0;attempt<100;attempt++) {
                    var candidate=new Profile{Short=true,Slot=i+1,Speed=1,Crop=s.Crop.Sample(random),Gray=s.Gray.Sample(random),Brightness=s.Brightness.Sample(random)/100,Contrast=s.Contrast.Sample(random)/100,Crf=s.Crf.Sample(random),Format=s.Format,Fps=s.Fps,Resolution=s.Resolution,Metadata="now",MusicPath=track.Path,MusicStart=Math.Round(start,4),MusicVolume=s.Volume/100};
                    candidate.Metadata=candidate.FileDate="custom";candidate.CustomDate=dates[i].CustomDate;
                    if(s.TechnicalVariants){candidate.Format=dates[i].Format;candidate.ScalePercent=dates[i].ScalePercent;candidate.FpsPercent=dates[i].FpsPercent;candidate.Crf=Math.Round(s.Crf.Min+(s.Crf.Max-s.Crf.Min)*(i+random.NextDouble())/count,4);}
                    string signature=string.Join("|",new[]{N(candidate.Crop),N(candidate.Gray),N(candidate.Brightness),N(candidate.Contrast),N(candidate.Crf),candidate.Format,N(candidate.ScalePercent),N(candidate.FpsPercent),candidate.MusicPath,N(candidate.MusicStart)});
                    if(signatures.Add(signature)){p=candidate;break;}if(room>0)start=random.NextDouble()*room;
                }
                if(p==null)throw new Exception("Для разных вариантов расширьте хотя бы один диапазон или добавьте другой трек.");result.Add(p);
            }
            return result;
        }
        public static List<string> Arguments(string input,string output,Profile p,Media m,string narration) {
            p.Validate();int w=m.Width,h=m.Height;
            if(p.Resolution!="source"){double sh=Parse(p.Resolution),lo=Math.Round(sh*16/9),f=w>=h?Math.Min(1,Math.Min(lo/w,sh/h)):Math.Min(1,Math.Min(sh/w,lo/h));w=(int)Math.Max(2,Math.Floor(w*f/2)*2);h=(int)Math.Max(2,Math.Floor(h*f/2)*2);}
            w=(int)Math.Max(2,Math.Floor(w*p.ScalePercent/100/2)*2);h=(int)Math.Max(2,Math.Floor(h*p.ScalePercent/100/2)*2);
            var filters=new List<string>{p.Short?"setpts=PTS-STARTPTS":"setpts=PTS/"+N(p.Speed)};
            if(p.Crop>0){string rem=N(1-2*p.Crop/100);filters.Add("crop=trunc(iw*"+rem+"/2)*2:trunc(ih*"+rem+"/2)*2");}
            filters.Add("scale="+w+":"+h+":flags=lanczos");filters.Add("setsar=1");if(p.Gray>0)filters.Add("hue=s="+N(1-p.Gray/100));filters.Add("fps=("+(p.Fps=="source"?m.Fps:p.Fps)+")*"+N(p.FpsPercent/100));
            if(p.Short)filters.Add("eq=brightness="+N(p.Brightness)+":contrast="+N(p.Contrast));
            bool voice=!string.IsNullOrWhiteSpace(narration),background=!p.Short&&!string.IsNullOrWhiteSpace(p.MusicPath),hasAudio=p.Short||voice||m.HasAudio||background;
            double outputDuration=m.Duration/p.Speed;
            var args=new List<string>{"-hide_banner","-nostdin","-n","-loglevel","error","-stats_period","0.3","-progress","pipe:1","-i",input};
            if(p.Short)args.AddRange(new[]{"-stream_loop","-1","-ss",N(p.MusicStart),"-i",p.MusicPath});else if(voice)args.AddRange(new[]{"-i",narration});
            if(background)args.AddRange(new[]{"-stream_loop","-1","-ss",N(p.MusicStart),"-i",p.MusicPath});
            args.AddRange(new[]{"-map","0:"+m.VideoIndex});
            if(background) {
                int musicIndex=voice?2:1;double fade=Math.Min(.2,outputDuration/4);
                string graph="["+musicIndex+":a:0]asetpts=PTS-STARTPTS,volume="+N(p.MusicVolume)+",afade=t=in:d="+N(fade)+",afade=t=out:st="+N(outputDuration-fade)+":d="+N(fade)+",apad,atrim=duration="+N(outputDuration)+"[bg]";
                if(voice||m.HasAudio) {
                    string source=voice?"1:a:0":"0:"+m.AudioIndex;
                    string timing=voice?"asetpts=PTS-STARTPTS,apad,atrim=duration="+N(m.Duration)+",atempo="+N(p.Speed):"atempo="+N(p.Speed)+",asetpts=STARTPTS/"+N(p.Speed)+"+(PTS-STARTPTS)";
                    graph+=";["+source+"]"+timing+",aresample=async=1:first_pts=0,apad,atrim=duration="+N(outputDuration)+"[speech];[speech][bg]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95:level=false:latency=1[mix]";
                }else graph+=";[bg]anull[mix]";
                args.AddRange(new[]{"-filter_complex",graph,"-map","[mix]","-t",N(outputDuration)});
            }else if(p.Short||voice)args.AddRange(new[]{"-map","1:a:0"});else if(m.HasAudio)args.AddRange(new[]{"-map","0:"+m.AudioIndex});
            args.AddRange(new[]{"-vf",string.Join(",",filters),"-c:v","libx264","-preset","fast","-pix_fmt","yuv420p"});
            if(p.Compression=="quality")args.AddRange(new[]{"-crf",N(p.Crf)});else {
                double rate=p.Compression=="sourcebitrate"?Math.Max(.05,m.VideoBitrate*p.BitratePercent/100):p.Bitrate;
                args.AddRange(new[]{"-b:v",N(rate)+"M","-maxrate",N(rate*1.5)+"M","-bufsize",N(rate*2)+"M"});
            }
            if(p.Short){double fade=Math.Min(.12,m.Duration/4);args.AddRange(new[]{"-af","asetpts=PTS-STARTPTS,volume="+N(p.MusicVolume)+",afade=t=in:d="+N(fade)+",afade=t=out:st="+N(m.Duration-fade)+":d="+N(fade)+",apad,atrim=duration="+N(m.Duration),"-t",N(m.Duration)});}
            else if(!background&&voice)args.AddRange(new[]{"-af","asetpts=PTS-STARTPTS,apad,atrim=duration="+N(m.Duration)+",atempo="+N(p.Speed),"-t",N(outputDuration)});
            else if(!background&&m.HasAudio)args.AddRange(new[]{"-af","atempo="+N(p.Speed)+",asetpts=STARTPTS/"+N(p.Speed)+"+(PTS-STARTPTS)"});
            if(hasAudio)args.AddRange(new[]{"-c:a","aac","-b:a","160k"});
            if(p.Metadata=="keep")args.AddRange(new[]{"-map_metadata","0"});else {
                args.AddRange(new[]{"-map_metadata","-1","-map_metadata:s:v","-1","-map_metadata:s:a","-1","-map_chapters","-1"});
                if(p.Metadata=="now"||p.Metadata=="custom"){string stamp=(p.Metadata=="custom"?p.CustomDate:DateTime.Now).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",Inv);args.AddRange(new[]{"-metadata","creation_time="+stamp,"-metadata:s:v:0","creation_time="+stamp});if(hasAudio)args.AddRange(new[]{"-metadata:s:a:0","creation_time="+stamp});}
            }
            args.AddRange(new[]{"-metadata:s:v:0","rotate=0"});if(p.Format!="mkv")args.AddRange(new[]{"-movflags","+faststart"});args.Add(output);return args;
        }
        public static async Task<BatchResult> Run(BatchJob job,IProgress<Update> progress,CancellationToken ct) {
            var result=new BatchResult();int done=0,total=job.Inputs.Count*job.Count;var random=new Random();
            try {
                if(job.Inputs.Count==0)throw new Exception("Добавьте видео.");if(job.Count<1||job.Count>10)throw new Exception("Неверное количество вариантов.");
                Directory.CreateDirectory(job.Output);var tracks=new List<Track>();
                if(job.Shorts)job.Ranges.Validate();else{if(job.Profiles==null||job.Profiles.Length<job.Count)throw new Exception("Недостаточно настроек вариантов.");Profile.Check(job.BackgroundDb,-60,-.1,"Громкость фона, дБ");}
                foreach(string path in job.Music.Distinct(StringComparer.OrdinalIgnoreCase)){progress.Report(new Update("Читаю музыку…",0));tracks.Add(new Track{Path=path,Duration=AudioDuration(await Probe(job.FFprobe,path,ct).ConfigureAwait(false))});}
                if(job.Shorts&&tracks.Count==0)throw new Exception("Добавьте музыку.");
                foreach(string input in job.Inputs) {
                    ct.ThrowIfCancellationRequested();Media media;List<Profile> profiles;
                    try{media=VideoInfo(await Probe(job.FFprobe,input,ct).ConfigureAwait(false));if(job.Shorts){media.Duration=media.VideoDuration;profiles=RandomProfiles(job.Ranges,job.Count,tracks,media.Duration,random);}else {profiles=Store.Clone(job.Profiles).Take(job.Count).ToList();foreach(var p in profiles){p.Short=false;p.MusicPath="";}
                        if(tracks.Count>0){var pool=tracks.OrderBy(t=>random.Next()).ToList();var used=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                            for(int i=0;i<profiles.Count;i++){var p=profiles[i];var t=pool[i%pool.Count];int visit=used.ContainsKey(t.Path)?used[t.Path]:0;used[t.Path]=visit+1;double room=Math.Max(0,t.Duration-media.Duration/p.Speed);int uses=(profiles.Count-1-i%pool.Count)/pool.Count+1;
                                p.MusicPath=t.Path;p.MusicStart=room>0?(uses==1?random.NextDouble()*room:room*visit/(uses-1)):t.Duration*visit/uses;p.MusicVolume=Math.Pow(10,job.BackgroundDb/20);
                            }
                        }
                    }}
                    catch(OperationCanceledException){throw;}catch(Exception e){result.Errors.Add(Path.GetFileName(input)+": "+e.Message);done+=job.Count;continue;}
                    foreach(var profile in profiles) {
                        ct.ThrowIfCancellationRequested();string partial=null;
                        try {
                            string name=Path.GetFileNameWithoutExtension(input);if(name.Length>60)name=name.Substring(0,60);
                            string filename=name+"_v"+profile.Slot+"_"+Guid.NewGuid().ToString("N").Substring(0,8)+"."+profile.Format;
                            string dest=Path.Combine(job.Output,filename);partial=Path.Combine(job.Output,".processing_"+filename);
                            var binding=job.Narrations.FirstOrDefault(b=>b.Video==input&&b.Slot==profile.Slot);string narration=binding==null?"":binding.Audio;
                            if(!profile.Short&&narration!=""&&!File.Exists(narration))throw new Exception("Не найдена назначенная озвучка.");
                            string label="Файл "+(done+1)+" из "+total+" · "+Path.GetFileName(input);int baseDone=done;double duration=media.Duration/profile.Speed;
                            progress.Report(new Update(label,100.0*done/total));
                            await Tool(job.FFmpeg,Arguments(input,partial,profile,media,narration),ct,line=>{if(line.StartsWith("out_time_us=")){double value=Parse(line.Substring(12))/1000000/duration;progress.Report(new Update(label,100.0*(baseDone+Math.Min(.99,Math.Max(0,value)))/total));}}).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();if(!File.Exists(partial)||new FileInfo(partial).Length==0)throw new Exception("Не создан результат.");
                            File.Move(partial,dest);partial=null;result.Outputs.Add(dest);
                            try {DateTime created=DateTime.Now,modified=created;if(profile.FileDate=="keep"){created=File.GetCreationTime(input);modified=File.GetLastWriteTime(input);}else if(profile.FileDate=="custom")created=modified=profile.CustomDate;File.SetCreationTime(dest,created);File.SetLastWriteTime(dest,modified);}catch(Exception e){result.Errors.Add(filename+": видео создано, дату изменить не удалось. "+e.Message);}
                        }catch(OperationCanceledException){throw;}catch(Exception e){result.Errors.Add(Path.GetFileName(input)+", вариант "+profile.Slot+": "+e.Message);}finally{if(partial!=null&&File.Exists(partial))File.Delete(partial);}
                        done++;
                    }
                }
            }catch(OperationCanceledException){result.Cancelled=true;}catch(Exception e){result.Errors.Add(e.Message);}
            progress.Report(new Update((result.Cancelled?"Остановлено. ":"Готово. ")+"Файлов: "+result.Outputs.Count+(result.Errors.Count>0?". Ошибок: "+result.Errors.Count:""),result.Cancelled?100.0*done/Math.Max(1,total):100));return result;
        }
    }
}
