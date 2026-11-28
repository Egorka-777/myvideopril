using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class CreativeGeometry {
        // Pan is a fraction of HALF THE OUTPUT CANVAS on each axis, independent of overflow.
        // Integer/even raster bounds are shared by the UI and FFmpeg, including letterboxed axes.
        public static RectangleF SourceBounds(CreativePreset preset,Size sourceSize) {
            if(preset==null)throw new ArgumentNullException(nameof(preset));
            if(sourceSize.Width<=0 || sourceSize.Height<=0)throw new ArgumentException("Неверный размер исходника.",nameof(sourceSize));
            double fit=Math.Min((double)preset.Width/sourceSize.Width,(double)preset.Height/sourceSize.Height)*preset.SourceZoom;
            int width=Math.Max(2,(int)Math.Round(sourceSize.Width*fit/2)*2),height=Math.Max(2,(int)Math.Round(sourceSize.Height*fit/2)*2);
            int x=(int)Math.Round((preset.Width-width)/2.0+preset.SourcePanX*preset.Width/2.0);
            int y=(int)Math.Round((preset.Height-height)/2.0+preset.SourcePanY*preset.Height/2.0);
            return new RectangleF(x,y,width,height);
        }
        public static string SourceFilter(CreativePreset preset,Size sourceSize) {
            var b=SourceBounds(preset,sourceSize);int x=(int)b.X,y=(int)b.Y,w=(int)b.Width,h=(int)b.Height;
            int px=Math.Max(0,x),py=Math.Max(0,y),cx=Math.Max(0,-x),cy=Math.Max(0,-y);
            int canvasW=Math.Max(preset.Width+cx,w+px),canvasH=Math.Max(preset.Height+cy,h+py);
            var filters=new List<string>{"fps="+preset.Fps};
            if(w!=sourceSize.Width || h!=sourceSize.Height)filters.Add("scale="+w+":"+h);
            filters.Add("setsar=1");
            if(x!=0 || y!=0 || w!=preset.Width || h!=preset.Height) {
                // 4:4:4 is needed only for exact odd-pixel placement, not the native-size path.
                filters.Add("format=yuv444p");
                if(canvasW!=w || canvasH!=h || px!=0 || py!=0)filters.Add("pad="+canvasW+":"+canvasH+":"+px+":"+py+":color=black");
                if(canvasW!=preset.Width || canvasH!=preset.Height || cx!=0 || cy!=0)filters.Add("crop="+preset.Width+":"+preset.Height+":"+cx+":"+cy);
            }
            filters.Add("format=yuv420p");filters.Add("settb=1/"+preset.Fps);
            return string.Join(",",filters);
        }
    }
    // A deliberately fixed three-block timeline. Ads operate on the original block only.
    public static class CreativeRender {
        public delegate void ProgressReporter(string text, double percent = -1);
        static string N(double value) { return Core.N(value); }
        static double Frames(double seconds,int fps) { return Math.Ceiling(seconds*fps-.000001)/fps; }
        static List<string> Args() { return new List<string>{"-hide_banner","-nostdin","-y","-loglevel","error","-filter_complex_threads",Math.Min(4,Math.Max(2,Environment.ProcessorCount-1)).ToString()}; }
        static string Audio(string source,double duration,string label) {
            return (source==null?"anullsrc=r=48000:cl=stereo":"["+source+"]asetpts=PTS-STARTPTS")+",aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=duration="+N(duration)+",asetpts=PTS-STARTPTS["+label+"]";
        }
        internal static string SourceVideoTiming(double speed) {
            return speed==1?"setpts=PTS-STARTPTS":"setpts=(PTS-STARTPTS)/"+N(speed);
        }
        internal static string SourceAudioFilter(double videoStart) {
            // FFmpeg has already subtracted the container start time. Preserve the
            // audio's offset from the video origin instead of zeroing it independently.
            return "asetpts=PTS-("+N(videoStart)+")/TB,aresample=48000:async=1:first_pts=0";
        }
        internal static string FinalAudioFilter(bool limit,double duration) {
            return (limit?"alimiter=limit=0.95:level=false:latency=true,":"")+"apad,atrim=duration="+N(duration)+",asetpts=N/SR/TB";
        }
        static AssemblyLayerPlan Layer(AssemblyTemplate t,string path,CreativeRect bounds,string work,string key,string appearance="cut",double fadeIn=0,double fadeOut=0) {
            var layer=new AssemblyLayer { Source=path,X=bounds.X,Y=bounds.Y,Width=bounds.Width,Height=bounds.Height,PreserveAspectRatio=true,AppearanceTransition=appearance,AppearanceDuration=fadeIn,ExitTransition=fadeOut>0?"fade":"cut",ExitDuration=fadeOut };
            var plan=new AssemblyLayerPlan { Layer=layer,Asset=new AssemblyAsset{Path=path},Raster=Path.Combine(work,key+".png") };
            if(!AssemblyOverlayMedia.IsAnimated(path)) AssemblyRaster.Save(t,plan,plan.Raster);
            return plan;
        }
        static async Task Encode(AssemblyTemplate t,AssemblyRenderContext context,string ffmpeg,string work,string key,List<string> args,List<string> graph,double seconds,string output,CancellationToken ct,Action<double> encodeFraction=null) {
            string script=Path.Combine(work,key+".ffgraph");File.WriteAllText(script,string.Join(";",graph),new UTF8Encoding(false));
            args.AddRange(new[]{"-filter_complex_script",script,"-map","[v]","-map","[finalAudio]","-t",N(seconds)});
            args.AddRange(AssemblyRender.EncoderArguments(context.Encoder,context.EncoderThreads));
            args.AddRange(new[]{"-r",t.Fps.ToString(),"-fps_mode","cfr","-c:a","aac","-b:a","192k","-ar","48000","-movflags","+faststart","-progress","pipe:1","-nostats",output});
            await Core.Tool(ffmpeg,args,ct,line=>{
                if(encodeFraction==null||!line.StartsWith("out_time_us=",StringComparison.Ordinal))return;
                encodeFraction(Math.Min(.99,Math.Max(0,Core.Parse(line.Substring(12))/1000000/seconds)));
            },3600).ConfigureAwait(false);
        }
        static async Task<double> SpeechDuration(string probe,string file,CancellationToken ct) {
            var info=await Core.Probe(probe,file,ct).ConfigureAwait(false);
            if(!info.streams.Any(s=>s.codec_type=="audio")) throw new Exception("Озвучка не содержит звуковой дорожки: "+file);
            double seconds=Core.AudioDuration(info);
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<=0) throw new Exception("Не удалось измерить озвучку: "+file);
            return seconds;
        }
        public static async Task RenderAsync(CreativeProject project,string ffmpeg,string probe,string output,CancellationToken ct,ProgressReporter progress=null,AssemblyRenderContext context=null) {
            ct.ThrowIfCancellationRequested(); if(project==null) throw new ArgumentNullException(nameof(project));project.Validate();
            output=Path.GetFullPath(output);
            var inputs=new[]{project.Source,project.IntroAudio,project.Preset.Banner,project.Preset.IntroCover}.Concat(project.Comments.SelectMany(c=>new[]{c.ImagePath,c.AudioPath})).Concat(project.Preset.Insertions.SelectMany(i=>new[]{i.Source,i.VisualSource}));
            if(inputs.Where(p=>!string.IsNullOrWhiteSpace(p)).Any(p=>string.Equals(Path.GetFullPath(p),output,StringComparison.OrdinalIgnoreCase))) throw new Exception("Выходной файл не должен заменять исходные материалы.");
            var timer=Stopwatch.StartNew();
            await project.ResolveSourceSizeAsync(probe,ct).ConfigureAwait(false);
            var p=project.Preset;
            var sourceInfo=await Core.Probe(probe,project.Source,ct).ConfigureAwait(false);
            var media=Core.VideoInfo(sourceInfo);
            if(media.Width<=0 || media.Height<=0 || media.VideoDuration<=0) throw new Exception("Исходный файл не содержит видео.");
            string effects=CreativeEffects.VideoFilter(p.Effects);
            var videoStream=sourceInfo.streams.First(s=>s.index==media.VideoIndex);
            double rotation=(videoStream.side_data_list??new SideData[0]).Select(s=>s.rotation).FirstOrDefault();
            if(videoStream.tags!=null && videoStream.tags.ContainsKey("rotate"))rotation=Core.Parse(videoStream.tags["rotate"]);
            bool rotated=Math.Abs(rotation)%180==90;
            int decodedWidth=rotated?videoStream.height:videoStream.width,decodedHeight=rotated?videoStream.width:videoStream.height;
            string normalize=decodedWidth==media.Width && decodedHeight==media.Height?"":"scale="+media.Width+":"+media.Height+",setsar=1,";
            string framing=normalize+(string.IsNullOrEmpty(effects)?"":effects+",")+CreativeGeometry.SourceFilter(p,new Size(media.Width,media.Height));
            double speech=await SpeechDuration(probe,project.IntroAudio,ct).ConfigureAwait(false);
            if(speech>3.000001) throw new Exception("Озвучка вступления длится "+N(speech)+" с. Нужно не дольше 3 с: сократите текст и создайте озвучку заново.");
            if(speech>media.VideoDuration/p.SourceSpeed+.000001) throw new Exception("Озвучка вступления длиннее исходника с выбранной скоростью ("+N(speech)+" > "+N(media.VideoDuration/p.SourceSpeed)+" с). Сократите вступительный текст.");
            double intro=Frames(speech,p.Fps), main=Frames(media.VideoDuration/p.SourceSpeed,p.Fps), motion=Frames(p.MotionDuration,p.Fps);
            double mix=Math.Min(Frames(p.MixDuration,p.Fps),main);
            var voices=new List<double>();foreach(var c in project.Comments) voices.Add(await SpeechDuration(probe,c.AudioPath,ct).ConfigureAwait(false));
            var cardDurations=voices.Select(d=>Frames(p.CardAppearanceDuration+d+p.CardExitDuration,p.Fps)).ToList();
            double outro=motion+cardDurations.Sum();
            string directory=Path.GetDirectoryName(output);Directory.CreateDirectory(directory);
            string work=Path.Combine(directory,"creative-work-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
            var t=new AssemblyTemplate { Width=p.Width,Height=p.Height,Fps=p.Fps,Insertions=AssemblyInsertions.Copy(p.Insertions),Materials=Directory.GetCurrentDirectory() };
            context=context??new AssemblyRenderContext();
            try {
                ProgressReporter stage=(text,percent)=>progress?.Invoke(text,percent);
                stage("Креатив: подготовка…",5);
                var audioLevels=await CreativeAudio.Create(project,ffmpeg,probe,work,ct,text=>stage(text,-1)).ConfigureAwait(false);
                string introAudio=audioLevels==null?project.IntroAudio:await audioLevels.Prepare(project.IntroAudio).ConfigureAwait(false);
                var commentAudio=new List<string>();foreach(var c in project.Comments)commentAudio.Add(audioLevels==null?c.AudioPath:await audioLevels.Prepare(c.AudioPath).ConfigureAwait(false));
                var random=new Random();if(audioLevels!=null)await audioLevels.PrepareAds(t,random).ConfigureAwait(false);
                stage("Креатив: проверяю кодировщик…",12);await AssemblyRender.SelectEncoder(t,ffmpeg,work,context,ct).ConfigureAwait(false);
                string last=Path.Combine(work,"last.png");
                // Reverse only the final second, not the entire source. This picks the actual last decoded frame.
                await Core.Tool(ffmpeg,new[]{"-v","error","-y","-sseof","-1","-i",project.Source,"-vf","reverse","-frames:v","1",last},ct).ConfigureAwait(false);
                stage("Креатив: вступление и Mix…",18);
                var args=Args();args.AddRange(new[]{"-t",N(speech*p.SourceSpeed),"-i",project.Source,"-i",introAudio});
                var graph=new List<string>();
                graph.Add("[0:v:0]"+SourceVideoTiming(p.SourceSpeed)+","+framing+",trim=duration="+N(speech)+",tpad=stop_mode=clone:stop_duration="+N(intro+mix)+",trim=duration="+N(intro+mix)+"[introBase]");
                string current="introBase";
                if(p.IntroBlur) {
                    int x=(int)(p.IntroMask.X*t.Width)/2*2,y=(int)(p.IntroMask.Y*t.Height)/2*2;
                    int w=Math.Min(t.Width-x,Math.Max(2,(int)(p.IntroMask.Width*t.Width)/2*2)),h=Math.Min(t.Height-y,Math.Max(2,(int)(p.IntroMask.Height*t.Height)/2*2));
                    graph.Add("["+current+"]split[maskBase][maskCrop]");
                    graph.Add("[maskCrop]crop="+w+":"+h+":"+x+":"+y+",gblur=sigma="+N(p.BlurStrength)+"[maskBlur]");
                    graph.Add("[maskBase][maskBlur]overlay="+x+":"+y+"[introMasked]");current="introMasked";
                }
                int input=2;
                if(!string.IsNullOrWhiteSpace(p.IntroCover)) {
                    var cover=Layer(t,p.IntroCover,p.IntroCoverBounds,work,"cover");
                    AssemblyRender.AppendLayerOverlay(t,cover,0,intro+mix,"1",current,"introCovered","auto",args,ref input,graph);current="introCovered";
                }
                // Keep the intro tail for the overlap. The joined graph dissolves directly into
                // the moving main stream, which is never restarted or trimmed at the Mix boundary.
                graph.Add("["+current+"]format=yuv420p[introVideo]");
                graph.Add(Audio("1:a:0",intro+mix,"introSound"));
                // Independent demuxers prevent concat/xfade backpressure from blocking
                // audio packet delivery behind the video's paused insertion branch.
                int original=input++;args.AddRange(new[]{"-an","-i",project.Source});
                int originalAudio=-1;
                if(media.HasAudio){originalAudio=input++;args.AddRange(new[]{"-vn","-i",project.Source});}
                graph.Add("["+original+":v:0]"+SourceVideoTiming(p.SourceSpeed)+","+framing+",tpad=stop_mode=clone:stop_duration="+N(1.0/t.Fps)+",trim=duration="+N(main)+"[originalVideo]");
                if(media.HasAudio) {
                    double videoStart=Core.Parse(videoStream.start_time)-Core.Parse(sourceInfo.format?.start_time);
                    graph.Add("["+originalAudio+":a:0]"+SourceAudioFilter(videoStart)+(p.SourceSpeed==1?"":",atempo="+N(p.SourceSpeed))+",aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=duration="+N(main)+",asetpts=N/SR/TB[originalSound]");
                } else graph.Add(Audio(null,main,"originalSound"));
                string mainVideo="originalVideo",mainSound="originalSound";
                if(t.Insertions.Count>0) {
                    var ads=await AssemblyInsertions.AppendAsync(t,probe,mainVideo,mainSound,main,t.Width,t.Height,args,graph,input,ct,random).ConfigureAwait(false);
                    mainVideo=ads.VideoLabel;mainSound=ads.AudioLabel;main=ads.Duration;input=ads.NextInput;
                }
                stage("Креатив: финальный кадр и комментарии…",22);
                int freeze=input++;args.AddRange(new[]{"-loop","1","-framerate",t.Fps.ToString(),"-t",N(outro),"-i",last});
                // Freeze the framed/cropped canvas, not a refit of the raw source: zoom/pan survives the shrink.
                Size size=new Size(t.Width,t.Height);
                var from=new RectangleF(0,0,t.Width,t.Height);
                var to=AssemblyRaster.FitImageBounds(new RectangleF((float)(p.FinalVideo.X*t.Width),(float)(p.FinalVideo.Y*t.Height),(float)(p.FinalVideo.Width*t.Width),(float)(p.FinalVideo.Height*t.Height)),size);
                string ratio=motion>0?"min(t/"+N(motion)+",1)":"1";
                Func<double,double,string> lerp=(a,b)=>N(a)+"+("+N(b-a)+")*("+ratio+")";
                graph.Add("color=c=black:s="+t.Width+"x"+t.Height+":r="+t.Fps+":d="+N(outro)+"[outroBg]");
                graph.Add("["+freeze+":v:0]setpts=PTS-STARTPTS,"+framing+",scale=w='max(2,trunc(("+lerp(from.Width,to.Width)+")/2)*2)':h='max(2,trunc(("+lerp(from.Height,to.Height)+")/2)*2)':eval=frame,setsar=1[shrinking]");
                // Centre interpolation keeps rounding of even scale dimensions from displacing the rectangle.
                graph.Add("[outroBg][shrinking]overlay=x='("+lerp(from.X+from.Width/2,to.X+to.Width/2)+")-w/2':y='("+lerp(from.Y+from.Height/2,to.Y+to.Height/2)+")-h/2':eval=frame:shortest=1[outroBase]");
                current="outroBase";double cursor=motion;
                var audioPieces=new List<string>();
                if(motion>0) { graph.Add(Audio(null,motion,"motionAudio"));audioPieces.Add("[motionAudio]"); }
                for(int i=0;i<3;i++) {
                    double end=cursor+cardDurations[i];var c=project.Comments[i];
                    var card=Layer(t,c.ImagePath,p.CommentBounds(i),work,"card"+i,p.CardAppearance,p.CardAppearanceDuration,p.CardExitDuration);
                    string next="card"+i;
                    AssemblyRender.AppendLayerOverlay(t,card,cursor,outro,"gte(t,"+N(cursor)+")*lt(t,"+N(outro)+")",current,next,"auto",args,ref input,graph);current=next;
                    int audio=input++;args.AddRange(new[]{"-i",commentAudio[i]});
                    graph.Add("["+audio+":a:0]asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay="+Math.Round(p.CardAppearanceDuration*48000).ToString()+"S:all=1,apad,atrim=duration="+N(cardDurations[i])+",asetpts=PTS-STARTPTS[commentAudio"+i+"]");
                    audioPieces.Add("[commentAudio"+i+"]");cursor=end;
                }
                graph.Add("["+current+"]trim=duration="+N(outro)+",setpts=PTS-STARTPTS,format=yuv420p[outroVideo]");graph.Add(string.Join("",audioPieces)+"concat=n="+audioPieces.Count+":v=0:a=1[outroSound]");
                graph.Add("["+mainVideo+"][outroVideo]concat=n=2:v=1:a=0[bodyVideo]");
                graph.Add("["+mainSound+"][outroSound]concat=n=2:v=0:a=1[bodySound]");
                graph.Add("[bodyVideo]settb=1/"+t.Fps+",setpts=N,fps="+t.Fps+"[joinV1]");
                graph.Add("[introVideo]settb=1/"+t.Fps+",setpts=N,fps="+t.Fps+"[joinV0]");
                graph.Add("[introSound]anull[joinA0]");graph.Add("[bodySound]anull[joinA1]");
                double total=intro+main+outro;
                if(mix>0) {
                    // Dissolve only the short intro tail over the moving body. Unlike a
                    // whole-body xfade this does not couple two long timeline demands.
                    graph.Add("[joinV0]split[introHeadSource][introTailSource]");
                    graph.Add("[introHeadSource]trim=duration="+N(intro)+",setpts=PTS-STARTPTS[introHead]");
                    graph.Add("[introTailSource]trim=start="+N(intro)+":duration="+N(mix)+",setpts=PTS-STARTPTS,format=yuva420p,fade=t=out:st=0:d="+N(mix)+":alpha=1[introTail]");
                    graph.Add("[joinV1][introTail]overlay=0:0:eof_action=pass:repeatlast=0:format=yuv420:enable='lt(t,"+N(mix)+")'[dissolvedBody]");
                    graph.Add("[introHead][dissolvedBody]concat=n=2:v=1:a=0[joined]");
                    // Speech finishes before the dissolve begins. Concatenate its full duration
                    // with the fading source; adelay after concat produces invalid PTS in the shipped FFmpeg.
                    graph.Add("[joinA0]atrim=duration="+N(intro)+",asetpts=PTS-STARTPTS[introSpeech]");
                    graph.Add("[joinA1]afade=t=in:st=0:d="+N(mix)+",asetpts=PTS-STARTPTS[mainFaded]");
                    graph.Add("[introSpeech][mainFaded]concat=n=2:v=0:a=1,apad,atrim=duration="+N(total)+"[a]");
                } else {
                    graph.Add("[joinV0][joinV1]concat=n=2:v=1:a=0[joined]");
                    graph.Add("[joinA0][joinA1]concat=n=2:v=0:a=1[a]");
                }
                if(!string.IsNullOrWhiteSpace(p.Banner)) {
                    var banner=Layer(t,p.Banner,p.BannerBounds,work,"banner");AssemblyRender.AppendLayerOverlay(t,banner,0,total,"1","joined","bannerVideo","auto",args,ref input,graph);current="bannerVideo";
                } else current="joined";
                string sound="a";
                if(p.MediaLayers.Any(l=>l.Enabled)) {
                    var layers=await CreativeMediaRender.AppendAsync(project,ffmpeg,probe,work,total,current,sound,args,graph,input,ct,text=>stage(text,-1),audioLevels).ConfigureAwait(false);
                    current=layers.VideoLabel;sound=layers.AudioLabel;input=layers.NextInput;
                }
                graph.Add("["+current+"]fps="+t.Fps+",tpad=stop_mode=clone:stop_duration="+N(1.0/t.Fps)+",trim=duration="+N(total)+",settb=1/"+t.Fps+",setpts=N,format=yuv420p[v]");
                if(p.BackgroundMusic.Count>0&&p.BackgroundMusicVolume>0) {
                    string music=p.BackgroundMusic[random.Next(p.BackgroundMusic.Count)];CreativeProject.RequireFile(music,"Фоновая музыка");
                    int musicInput=input++;args.AddRange(new[]{"-stream_loop","-1","-i",music});
                    graph.Add("["+musicInput+":a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume="+N(p.BackgroundMusicVolume)+",atrim=duration="+N(total)+",asetpts=PTS-STARTPTS[musicBed]");
                    graph.Add("["+sound+"][musicBed]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[musicMix]");sound="musicMix";
                }
                // Concat/resampling can change audio frame sizes at the intro boundary.
                // The latency-compensated limiter can then emit overlapping PTS. Clock the
                // final continuous PCM by sample count, without changing samples or tempo.
                graph.Add("["+sound+"]"+FinalAudioFilter(audioLevels!=null,total)+"[finalAudio]");
                // The limiter sees the float mix before the only AAC encode.
                string result=Path.Combine(work,"result.mp4");
                stage("Креатив: единый рендер · "+context.Encoder+" · "+t.Width+"×"+t.Height+"…",25);
                await Encode(t,context,ffmpeg,work,"final",args,graph,total,result,ct,fraction=>{
                    double percent=25+70*fraction;
                    stage("Креатив: рендер "+(fraction*100).ToString("0",Core.Inv)+"% · "+context.Encoder+" · "+t.Width+"×"+t.Height,percent);
                }).ConfigureAwait(false);
                var check=Core.VideoInfo(await Core.Probe(probe,result,ct).ConfigureAwait(false));
                if(check.Width!=t.Width || check.Height!=t.Height || !check.HasAudio || Math.Abs(check.VideoDuration-total)>Math.Max(.15,2.0/t.Fps)) throw new Exception("Креатив не прошёл проверку: кадр "+check.Width+"×"+check.Height+" (ожидается "+t.Width+"×"+t.Height+"), длительность "+N(check.VideoDuration)+" с (ожидается "+N(total)+" с), звук: "+check.HasAudio+".");
                if(!string.IsNullOrWhiteSpace(project.OutputTitle))await CreativeOutputTitle.WriteMetadataAsync(ffmpeg,probe,result,project,ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();File.Copy(result,output,true);
                AppDiagnostics.Write("Креатив","","render","complete","encoder="+context.Encoder+"; canvas="+t.Width+"x"+t.Height+"; duration="+N(total)+"; videoPasses=1; elapsed="+N(timer.Elapsed.TotalSeconds)+"s");
                stage("Креатив готов · один видеопроход · "+N(timer.Elapsed.TotalSeconds)+" с.",100);
            } finally { try { Directory.Delete(work,true); } catch(IOException) { } catch(UnauthorizedAccessException) { } }
        }
    }
}
