using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    // Per-render attenuation only. The original is the reference, never the loudest added asset.
    internal sealed class CreativeAudio {
        readonly string ffmpeg, probe, work;
        readonly CancellationToken ct;
        readonly Action<string> progress;
        readonly Dictionary<string,string> prepared=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        double target;
        CreativeAudio(string ffmpeg,string probe,string work,CancellationToken ct,Action<string> progress) { this.ffmpeg=ffmpeg;this.probe=probe;this.work=work;this.ct=ct;this.progress=progress; }

        internal static async Task<double?> Measure(string ffmpeg,string probe,string path,CancellationToken ct) {
            var info=await Core.Probe(probe,path,ct).ConfigureAwait(false);
            if(!info.streams.Any(s=>s.codec_type=="audio"))return null;
            double seconds=Core.AudioDuration(info);
            if(seconds<=0 || double.IsNaN(seconds) || double.IsInfinity(seconds))throw new Exception("Не удалось измерить длительность звука: "+path);
            var args=new List<string>{"-hide_banner","-nostdin","-v","error"};
            // R128 needs a 400 ms window. Repeat a short voice rather than padding it with silence,
            // which would underestimate its audible loudness. This affects measurement only.
            if(seconds<.4)args.AddRange(new[]{"-stream_loop","-1"});
            args.AddRange(new[]{"-i",path,"-t",Core.N(Math.Max(.5,seconds)),"-map","0:a:0","-vn","-af","aresample=48000,aformat=channel_layouts=stereo,ebur128=metadata=1,ametadata=print:key=lavfi.r128.I:file=-","-f","null","-"});
            string text=await Core.Tool(ffmpeg,args,ct,null,3600).ConfigureAwait(false);
            var matches=Regex.Matches(text,@"lavfi\.r128\.I=(-?\d+(?:\.\d+)?)");
            if(matches.Count==0)throw new Exception("Нет результата измерения R128: "+path);
            double value=double.Parse(matches[matches.Count-1].Groups[1].Value,Core.Inv);
            return value<=-69.9?(double?)null:value;
        }
        internal static async Task<CreativeAudio> Create(CreativeProject project,string ffmpeg,string probe,string work,CancellationToken ct,Action<string> progress) {
            if(!project.Preset.AudioLevelingEnabled)return null;
            var result=new CreativeAudio(ffmpeg,probe,work,ct,progress);
            progress?.Invoke("Креатив: измеряю громкость оригинала (R128)…");
            var reference=await Measure(ffmpeg,probe,project.Source,ct).ConfigureAwait(false);
            // Silence cannot be a useful loudness target. Use a conservative broadcast reference,
            // announce it, and never amplify existing quiet additions.
            result.target=reference??-23;
            progress?.Invoke(reference.HasValue?"Креатив: опорная громкость "+Core.N(result.target)+" LUFS; добавления только приглушаются.":"Креатив: оригинал без измеримого звука; добавления ограничены −23 LUFS, без усиления.");
            return result;
        }
        internal async Task<string> Prepare(string path) {
            path=Path.GetFullPath(path);string cached;if(prepared.TryGetValue(path,out cached))return cached;
            var level=await Measure(ffmpeg,probe,path,ct).ConfigureAwait(false);
            double gain=level.HasValue?Math.Min(0,target-level.Value):0;
            if(gain>=-.05){prepared[path]=path;return path;}
            var info=await Core.Probe(probe,path,ct).ConfigureAwait(false);
            bool video=info.streams.Any(s=>s.codec_type=="video" && (s.disposition==null || s.disposition.attached_pic!=1));
            bool webm=video && Path.GetExtension(path).Equals(".webm",StringComparison.OrdinalIgnoreCase);
            string output=Path.Combine(work,"leveled-"+prepared.Count+(video?(webm?".webm":".mov"):".wav"));
            var args=new List<string>{"-hide_banner","-nostdin","-v","error","-y","-i",path};
            if(video)args.AddRange(new[]{"-map","0:v:0","-c:v","copy"});else args.Add("-vn");
            args.AddRange(new[]{"-map","0:a:0","-af","volume="+Core.N(gain)+"dB:precision=double","-c:a",webm?"libopus":"pcm_s24le","-ar","48000",output});
            progress?.Invoke("Креатив: звук "+Path.GetFileName(path)+" "+Core.N(level.Value)+" LUFS → "+Core.N(gain)+" dB.");
            await Core.Tool(ffmpeg,args,ct,null,3600).ConfigureAwait(false);prepared[path]=output;return output;
        }
        internal async Task PrepareAds(AssemblyTemplate template,Random random) {
            foreach(var ad in template.Insertions) {
                var pool=AssemblyFiles.Pool(template.Resolve(ad.Source),AssemblyFiles.VideoExtensions.Concat(AssemblyFiles.MusicExtensions).Concat(AssemblyFiles.PictureExtensions).Distinct().ToArray());
                if(pool.Count==0)throw new Exception("Рекламная пауза: не найдены материалы «"+ad.Source+"».");
                string selected=pool[random.Next(pool.Count)];
                ad.Source=AssemblyInsertions.IsImage(selected)?selected:await Prepare(selected).ConfigureAwait(false);
            }
        }
        // Select with the existing planner FIRST. Preparing whole pools would break content hashes,
        // fixed selection and uniqueness; preparing the mixed result would also lower the original.
        internal static async Task ApplyMedia(CreativeProject project,string ffmpeg,string probe,string inputFile,string outputFile,string work,CancellationToken ct,Action<string> progress,CreativeAudio audio) {
            var p=project.Preset;var media=Core.VideoInfo(await Core.Probe(probe,inputFile,ct).ConfigureAwait(false));
            var scene=new AssemblyScene{Name="Креатив",Videos=inputFile,VideoMode="own",VoiceMode="none",Duration=media.VideoDuration,NativeAudioVolume=1,BedMusicVolume=0,Layers=new List<AssemblyLayer>(p.MediaLayers)};
            var template=new AssemblyTemplate{PackStudioVersion=1,ObjectStudioVersion=1,SceneTimingVersion=1,Materials=Directory.GetCurrentDirectory(),Videos=inputFile,Width=p.Width,Height=p.Height,Fps=p.Fps,Transition="cut",TransitionDuration=0,Count=1,PrimaryNativeAudio=true,PrimaryNativeVolume=1,MusicVolume=0,ExportSubtitleText=false,Scenes=new List<AssemblyScene>{scene}};
            var video=new AssemblyVideo{Path=inputFile,SourcePath=inputFile,Duration=media.VideoDuration,HasAudio=media.HasAudio,Hash=AssemblyFiles.Hash(inputFile)};
            var random=new Random();var batch=new AssemblyInventory(template,new[]{video},new AssemblyHistory()).CreateBatch(random,1,new AssemblyMixTracker(),ct);
            if(batch.Plans.Count==0)throw new Exception("Медиа креатива: "+batch.Limit);
            foreach(var layer in batch.Plans[0].Scenes.SelectMany(s=>s.Layers).Where(l=>l.Layer.Enabled && l.Layer.AudioEnabled && !l.Layer.IsText && l.Asset!=null)) {
                string path=await audio.Prepare(layer.Asset.Path).ConfigureAwait(false);
                layer.Asset=new AssemblyAsset{Path=path,Hash=layer.Asset.Hash};
            }
            progress?.Invoke("Креатив: медиа на весь ролик…");
            // Use the shared argument builder, but put the limiter on its float amix output
            // BEFORE AAC encoding (limiting an already encoded clipped sum is too late).
            var plan=batch.Plans[0];var context=new AssemblyRenderContext();
            int raster=0;foreach(var layer in plan.Scenes.SelectMany(s=>s.Layers)) {
                if(AssemblyOverlayMedia.IsAnimated(layer.Layer.IsText?"":layer.Asset.Path))continue;
                layer.Raster=Path.Combine(work,"media-raster-"+(raster++)+".png");AssemblyRaster.Save(template,layer,layer.Raster);
            }
            await AssemblyRender.SelectEncoder(template,ffmpeg,work,context,ct).ConfigureAwait(false);
            string script=Path.Combine(work,"media-limited.ffgraph");
            var arguments=AssemblyRender.Arguments(template,plan,context.Encoder,script,outputFile,context);
            string graph=File.ReadAllText(script);
            if(!graph.Contains("[audio]"))throw new Exception("Медиа креатива: не найден итоговый звуковой микс.");
            graph=graph.Replace("[audio]","[creativeMix]")+";[creativeMix]alimiter=limit=0.95:level=false:latency=true[audio]";
            File.WriteAllText(script,graph,new System.Text.UTF8Encoding(false));
            await Core.Tool(ffmpeg,arguments,ct,null,3600).ConfigureAwait(false);
        }
        internal static async Task Limit(string ffmpeg,string input,string output,double duration,CancellationToken ct) {
            await Core.Tool(ffmpeg,new[]{"-hide_banner","-nostdin","-v","error","-y","-i",input,"-map","0:v:0","-map","0:a:0","-c:v","copy","-af","alimiter=limit=0.95:level=false:latency=true,apad,atrim=duration="+Core.N(duration),"-c:a","aac","-b:a","192k","-t",Core.N(duration),"-movflags","+faststart",output},ct,null,3600).ConfigureAwait(false);
        }
    }
}
