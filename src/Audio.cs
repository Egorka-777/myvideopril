using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VideoBatch {
    public class Cue {public int Index;public double Start,End;public string Text;}
    public class AudioJob {
        public string Srt,Video,Output,Cache,FFmpeg,FFprobe,Provider,Voice,Key,Clips;
        [IgnoreDataMember] public Func<string,string,string,CancellationToken,Task> WindowsSpeech;
    }
    [DataContract] class SpeechRequest {[DataMember]public string text;[DataMember]public string model_id="eleven_multilingual_v2";}
    public static class AudioEngine {
        public static List<Cue> ReadSrt(string path) {
            string text=File.ReadAllText(path,new UTF8Encoding(false,true)).Trim().TrimStart('\ufeff');var cues=new List<Cue>();double previous=0;
            foreach(string block in Regex.Split(text,@"\r?\n[ \t]*\r?\n")) {
                if(string.IsNullOrWhiteSpace(block))continue;
                var m=Regex.Match(block,@"^\s*(?:\d+\r?\n)?(\d{2,3}):([0-5]\d):([0-5]\d)[,.](\d{3})\s*-->\s*(\d{2,3}):([0-5]\d):([0-5]\d)[,.](\d{3})[^\r\n]*\r?\n(.+)$",RegexOptions.Singleline);
                if(!m.Success)throw new Exception("Неверный блок SRT №"+(cues.Count+1)+".");
                Func<int,double> time=i=>3600*Core.Parse(m.Groups[i].Value)+60*Core.Parse(m.Groups[i+1].Value)+Core.Parse(m.Groups[i+2].Value)+Core.Parse(m.Groups[i+3].Value)/1000;
                double start=time(1),end=time(5);string line=Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(m.Groups[9].Value,"<[^>]+>","")),@"\s+"," ").Trim();
                if(end<=start||start<previous-.0001||end>21600)throw new Exception("Блок №"+(cues.Count+1)+": интервалы должны идти по порядку, без наложений. Максимум 6 часов.");
                if(line.Length<1||line.Length>4000)throw new Exception("В одной фразе должно быть от 1 до 4000 символов.");
                cues.Add(new Cue{Index=cues.Count+1,Start=start,End=end,Text=line});previous=end;
            }
            if(cues.Count==0||cues.Count>2000)throw new Exception("В SRT должно быть от 1 до 2000 фраз.");return cues;
        }
        static string CacheKey(AudioJob j,Cue c){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("desktop-v1|"+j.Provider+"|"+j.Voice+"|eleven_multilingual_v2|"+c.Text))).Replace("-","").ToLowerInvariant();}
        static async Task Generate(AudioJob j,Cue c,string path,CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if(j.Provider=="clips") {
                string prefix=Path.Combine(j.Clips,c.Index.ToString("0000"));string source=new[]{".wav",".mp3",".m4a"}.Select(x=>prefix+x).FirstOrDefault(File.Exists);
                if(source==null)throw new Exception("Не найдена фраза "+c.Index.ToString("0000")+".wav / .mp3 / .m4a.");File.Copy(source,path,true);
            } else if(j.Provider=="windows") {
                if(j.WindowsSpeech==null)throw new Exception("Системная озвучка доступна в Windows.");await j.WindowsSpeech(c.Text,j.Voice,path,ct).ConfigureAwait(false);
            } else if(j.Provider=="elevenlabs") {
                if(!Regex.IsMatch(j.Voice??"",@"^[A-Za-z0-9_-]{10,80}$")||string.IsNullOrWhiteSpace(j.Key))throw new Exception("Укажите API-ключ и Voice ID ElevenLabs.");
                ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
                using(var client=new HttpClient{Timeout=TimeSpan.FromMinutes(3)}) {
                    client.DefaultRequestHeaders.Add("xi-api-key",j.Key);
                    byte[] body;using(var m=new MemoryStream()){new DataContractJsonSerializer(typeof(SpeechRequest)).WriteObject(m,new SpeechRequest{text=c.Text});body=m.ToArray();}
                    using(var content=new ByteArrayContent(body)) {
                        content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                        using(var response=await client.PostAsync("https://api.elevenlabs.io/v1/text-to-speech/"+j.Voice+"?output_format=mp3_44100_128",content,ct).ConfigureAwait(false)) {
                            if(!response.IsSuccessStatusCode)throw new Exception("ElevenLabs: код "+(int)response.StatusCode+". Проверьте ключ, голос и баланс. Автоматического повтора запроса нет.");
                            var bytes=await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);ct.ThrowIfCancellationRequested();File.WriteAllBytes(path,bytes);
                        }
                    }
                }
            } else throw new Exception("Неизвестный источник озвучки.");
        }
        public static async Task<string> Run(AudioJob j,IProgress<Update> progress,CancellationToken ct) {
            var cues=ReadSrt(j.Srt);double duration=cues.Last().End;
            if(!string.IsNullOrEmpty(j.Video)){var m=Core.VideoInfo(await Core.Probe(j.FFprobe,j.Video,ct).ConfigureAwait(false));if(duration>m.VideoDuration+.05)throw new Exception("Последняя фраза выходит за конец видео.");duration=Math.Max(duration,m.VideoDuration);}
            Directory.CreateDirectory(j.Output);Directory.CreateDirectory(j.Cache);
            string temp=Path.Combine(Path.GetTempPath(),"videobatch-audio-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
            string stem=Path.GetFileNameWithoutExtension(j.Srt);if(stem.Length>60)stem=stem.Substring(0,60);
            string name=stem+"_voice_"+Guid.NewGuid().ToString("N").Substring(0,8)+".wav",dest=Path.Combine(j.Output,name),partial=Path.Combine(j.Output,".processing_"+name);
            try {
                using(var timeline=new WaveTimeline(partial,duration)) {
                    foreach(var cue in cues) {
                        ct.ThrowIfCancellationRequested();progress.Report(new Update("Озвучка: фраза "+cue.Index+" из "+cues.Count,100.0*(cue.Index-1)/cues.Count));
                        string clip=Path.Combine(j.Cache,CacheKey(j,cue)+".audio");
                        if(j.Provider=="clips"){clip=Path.Combine(temp,"imported.audio");await Generate(j,cue,clip,ct).ConfigureAwait(false);}
                        else if(!File.Exists(clip)){string download=Path.Combine(temp,"speech.download");await Generate(j,cue,download,ct).ConfigureAwait(false);Core.AudioDuration(await Core.Probe(j.FFprobe,download,ct).ConfigureAwait(false));File.Copy(download,clip,true);}
                        double length=Core.AudioDuration(await Core.Probe(j.FFprobe,clip,ct).ConfigureAwait(false)),slot=cue.End-cue.Start,ratio=Math.Max(1,length/slot);
                        if(ratio>1.25)throw new Exception("Сократите фразу №"+cue.Index+": она длится "+Core.N(length)+" с, а интервал — "+Core.N(slot)+" с. Остальные созданные фразы остаются в кэше.");
                        string raw=Path.Combine(temp,"phrase.pcm");await Core.Tool(j.FFmpeg,new[]{"-v","error","-nostdin","-y","-i",clip,"-vn","-af","atempo="+Core.N(ratio)+",apad,atrim=duration="+Core.N(slot),"-ac","1","-ar","48000","-f","s16le",raw},ct,null,600).ConfigureAwait(false);
                        timeline.Place(raw,cue.Start,cue.End);
                    }
                }
                ct.ThrowIfCancellationRequested();File.Move(partial,dest);progress.Report(new Update("Озвучка готова.",100));return dest;
            } finally {if(File.Exists(partial))File.Delete(partial);try{Directory.Delete(temp,true);}catch{}}
        }
    }
    public sealed class WaveTimeline:IDisposable {
        FileStream file;readonly byte[] buffer=new byte[65536];const int Rate=48000;
        public WaveTimeline(string path,double seconds) {
            long size=(long)Math.Round(seconds*Rate)*2;if(size<2||size>uint.MaxValue-36L)throw new Exception("Слишком длинная дорожка.");
            file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
            using(var w=new BinaryWriter(file,Encoding.ASCII,true)){w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write((uint)(36+size));w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(Rate);w.Write(Rate*2);w.Write((short)2);w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write((uint)size);}file.SetLength(44+size);
        }
        public void Place(string raw,double start,double end) {
            long pos=44+(long)Math.Round(start*Rate)*2,limit=44+(long)Math.Round(end*Rate)*2;if(pos<44||limit>file.Length||limit<pos)throw new Exception("Неверный интервал.");
            using(var r=File.OpenRead(raw)){if(r.Length>limit-pos+4)throw new Exception("Фраза выходит за свой интервал.");file.Position=pos;long remain=Math.Min(r.Length,limit-pos);while(remain>0){int n=r.Read(buffer,0,(int)Math.Min(remain,buffer.Length));if(n==0)throw new EndOfStreamException();file.Write(buffer,0,n);remain-=n;}}
        }
        public void Dispose(){if(file!=null){file.Dispose();file=null;}}
    }
}
