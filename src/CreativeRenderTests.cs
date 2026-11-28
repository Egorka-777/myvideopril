using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;

namespace VideoBatch {
    // Offline integration suite; the existing reflection host can call Run(ffmpeg, ffprobe).
    public static class CreativeRenderTests {
        static void Require(bool value,string message) { if(!value)throw new Exception(message); }
        static void Tool(string ffmpeg,params string[] args) { Core.Tool(ffmpeg,args,CancellationToken.None,null,120).GetAwaiter().GetResult(); }
        static void Tone(string ffmpeg,string path,int hz,double seconds) { Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","sine=frequency="+hz+":sample_rate=48000:duration="+Core.N(seconds),path); }
        static double Power(byte[] pcm,double at,double duration,int hz) {
            int from=(int)(at*8000),until=Math.Min(pcm.Length/2,(int)((at+duration)*8000));double re=0,im=0;
            for(int i=from;i<until;i++){double sample=BitConverter.ToInt16(pcm,i*2);re+=sample*Math.Cos(2*Math.PI*hz*i/8000);im+=sample*Math.Sin(2*Math.PI*hz*i/8000);}
            return (re*re+im*im)/Math.Max(1,(until-from)*(double)(until-from));
        }
        static void Marker(byte[] pcm,double at,int wanted,params int[] absent) {
            double power=Power(pcm,at,.12,wanted);Require(power>100,"Missing audio marker "+wanted+" at "+at);
            foreach(int hz in absent)Require(power>Power(pcm,at,.12,hz)*40,"Mixed audio marker "+hz+" at "+at);
        }
        static Color Pixel(string ffmpeg,string output,string root,double at,int x,int y) {
            string frame=Path.Combine(root,"frame.png");Tool(ffmpeg,"-v","error","-y","-ss",Core.N(at),"-i",output,"-frames:v","1",frame);
            using(var bitmap=new Bitmap(frame))return bitmap.GetPixel(x,y);
        }
        static double Db(byte[] pcm,double at,int hz) {return 10*Math.Log10(Math.Max(1e-9,Power(pcm,at,.12,hz)));}
        static byte[] Pcm(string ffmpeg,string path,string root) {string file=Path.Combine(root,"measure.pcm");Tool(ffmpeg,"-v","error","-y","-i",path,"-vn","-ac","1","-ar","8000","-f","s16le",file);return File.ReadAllBytes(file);}
        static void RunAudioAndMotion(string ffmpeg,string probe,string root,CreativeProject fixture) {
            var preset=new CreativePreset{PreserveSourceSize=false,Width=160,Height=90,Fps=20,MixDuration=.4,MotionDuration=0,CardAppearance="cut",CardAppearanceDuration=0,CardExitDuration=0};
            var p=new CreativeProject{Preset=preset,Source=Path.Combine(root,"moving.mp4"),IntroAudio=Path.Combine(root,"loud-intro.wav"),Comments=fixture.Comments};
            // Every source frame has a different grey value: a held first frame, skip or restart
            // at the boundary is observable numerically, unlike a static colour fixture.
            Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","nullsrc=s=160x90:r=20:d=1.6,geq=lum='30+N*5':cb=128:cr=128","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=1.6,volume=0.1","-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac",p.Source);
            Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","sine=frequency=880:sample_rate=48000:duration=0.4,volume=4",p.IntroAudio);
            string ad=Path.Combine(root,"loud-ad.wav");Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","sine=frequency=660:sample_rate=48000:duration=0.3,volume=4",ad);
            preset.Insertions.Add(new AssemblyInsertion{Source=ad,Start=1});
            var on=Path.Combine(root,"audio-on.mp4");CreativeRender.RenderAsync(p,ffmpeg,probe,on,CancellationToken.None).GetAwaiter().GetResult();
            var leveled=Pcm(ffmpeg,on,root);double source=Db(leveled,1.05,440),voice=Db(leveled,.12,880),adDb=Db(leveled,1.48,660),comment=Db(leveled,2.4,1100);
            Require(voice-source<1.5 && adDb-source<1.5 && comment-source<1.5,"Added audio exceeds original loudness reference");
            Marker(leveled,.25,880,440);Marker(leveled,.46,440,880);Marker(leveled,.82,440,880);
            foreach(double at in new[]{.75,.8,.85,.9}) {double sourceAt=at-.4;int frame=(int)Math.Round(sourceAt*20);int y=Pixel(ffmpeg,on,root,at,80,45).R;double expected=(30+frame*5-16)*255.0/219;Require(Math.Abs(y-expected)<12,"Moving source skip/restart at "+at+": "+y+" expected "+expected);}
            string bed=Path.Combine(root,"music-bed.wav");Tone(ffmpeg,bed,220,.2);
            preset.BackgroundMusic.Add(bed);preset.BackgroundMusicVolume=.2;
            string musicResult=Path.Combine(root,"music-result.mp4");CreativeRender.RenderAsync(p,ffmpeg,probe,musicResult,CancellationToken.None).GetAwaiter().GetResult();
            var musicPcm=Pcm(ffmpeg,musicResult,root);Require(Power(musicPcm,.12,.12,220)>1 && Power(musicPcm,2.4,.12,220)>1,"Looped background music missing from intro or comments");
            Require(Power(musicPcm,.12,.12,880)>100 && Power(musicPcm,1.05,.12,440)>100,"Background music replaced speech or source");
            preset.BackgroundMusic.Clear();
            preset.AudioLevelingEnabled=false;var off=Path.Combine(root,"audio-off.mp4");CreativeRender.RenderAsync(p,ffmpeg,probe,off,CancellationToken.None).GetAwaiter().GetResult();var raw=Pcm(ffmpeg,off,root);
            double delta=Db(raw,.12,880)-voice;Require(delta>25,"Disabled leveling still attenuates the voice: "+delta);
            // A real audible VIDEO object, chosen by the existing media planner, is attenuated
            // before amix. Check its marker separately from the retained original marker.
            string layer=Path.Combine(root,"loud-layer.mp4");Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","color=c=green:s=40x40:r=20:d=0.5","-f","lavfi","-i","sine=frequency=1760:sample_rate=48000:duration=0.5,volume=4","-c:v","libx264","-c:a","aac",layer);
            preset.AudioLevelingEnabled=true;preset.MediaLayers.Add(new AssemblyLayer{Source=layer,AudioEnabled=true,X=0,Y=0,Width=.1,Height=.1});
            var mixed=Path.Combine(root,"audio-media.mp4");CreativeRender.RenderAsync(p,ffmpeg,probe,mixed,CancellationToken.None).GetAwaiter().GetResult();var mixedPcm=Pcm(ffmpeg,mixed,root);double mediaDb=Db(mixedPcm,1.05,1760);
            Require(mediaDb>10,"Media audio disappeared from the final mix: "+mediaDb);
            Require(mediaDb-Db(mixedPcm,1.05,440)<1.5,"Media audio was not leveled before mixing");
            double peak=0;for(int i=0;i<mixedPcm.Length/2;i++)peak=Math.Max(peak,Math.Abs((double)BitConverter.ToInt16(mixedPcm,i*2))/32768);Require(peak<.99,"Mixed audio clipped");
            // A silent audio TRACK is distinct from no audio stream; both must be explicit.
            string silent=Path.Combine(root,"silent-track.mp4");Tool(ffmpeg,"-v","error","-y","-i",p.Source,"-f","lavfi","-i","anullsrc=r=48000:cl=stereo","-map","0:v:0","-map","1:a:0","-c:v","copy","-c:a","aac","-t","1.6",silent);
            Require(!CreativeAudio.Measure(ffmpeg,probe,silent,CancellationToken.None).GetAwaiter().GetResult().HasValue,"Silent original was treated as a measurable reference");
            p.Source=silent;preset.MediaLayers.Clear();preset.Insertions.Clear();bool fallbackLogged=false;
            var silentResult=Path.Combine(root,"silent-track-result.mp4");CreativeRender.RenderAsync(p,ffmpeg,probe,silentResult,CancellationToken.None,(s,p)=>{if(s.Contains("−23 LUFS"))fallbackLogged=true;}).GetAwaiter().GetResult();
            Require(fallbackLogged,"Silent reference fallback was hidden");Marker(Pcm(ffmpeg,silentResult,root),.12,880,440);
            p.Source=Path.Combine(root,"moving.mp4");
            // Exercise the post-mix limiter above full scale, not just on quiet leveled inputs.
            string hot=Path.Combine(root,"hot.mov"),limited=Path.Combine(root,"limited.mp4");
            Tool(ffmpeg,"-v","error","-y","-i",p.Source,"-af","volume=160","-c:v","copy","-c:a","pcm_f32le",hot);
            CreativeAudio.Limit(ffmpeg,hot,limited,1.6,CancellationToken.None).GetAwaiter().GetResult();var limitedPcm=Pcm(ffmpeg,limited,root);double hotPeak=0;for(int i=0;i<limitedPcm.Length/2;i++)hotPeak=Math.Max(hotPeak,Math.Abs((double)BitConverter.ToInt16(limitedPcm,i*2))/32768);Require(hotPeak>.8 && hotPeak<.99,"Final limiter failed on over-range mix: "+hotPeak);
            var refLevel=CreativeAudio.Measure(ffmpeg,probe,p.Source,CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("Creative AV measured PASS: reference="+refLevel+" LUFS; marker dB source="+source.ToString("F2")+", intro="+voice.ToString("F2")+", ad="+adDb.ToString("F2")+", comment="+comment.ToString("F2")+", media="+mediaDb.ToString("F2")+"; disabled intro delta="+delta.ToString("F2")+" dB; peak="+peak.ToString("F4")+"; overloaded limiter peak="+hotPeak.ToString("F4")+"; silent fallback logged; moving frame continuity .75–.90s PASS");
        }
        public static bool RunSourceSpeed(string ffmpeg) {
            var preset=new CreativePreset{SourceSpeed=1.1};preset.Validate();Require(preset.Clone().SourceSpeed==1.1,"Speed lost in preset clone");
            var serializer=new System.Xml.Serialization.XmlSerializer(typeof(CreativePreset));
            using(var xml=new MemoryStream()){serializer.Serialize(xml,preset);xml.Position=0;Require(((CreativePreset)serializer.Deserialize(xml)).SourceSpeed==1.1,"Speed lost in XML");}
            using(var xml=new StringReader("<CreativePreset />"))Require(((CreativePreset)serializer.Deserialize(xml)).SourceSpeed==1,"Legacy preset speed must default to 1");
            foreach(double rate in new[]{.5,1.0,1.1,2.0}) {
                string video=Core.Tool(ffmpeg,new[]{"-v","error","-f","lavfi","-i","testsrc2=s=32x32:r=30:d=2.2","-vf",CreativeRender.SourceVideoTiming(rate)+",fps=30","-c:v","rawvideo","-f","framecrc","-"},CancellationToken.None,null,15).GetAwaiter().GetResult();
                int count=System.Text.RegularExpressions.Regex.Matches(video,@"(?m)^0,").Count;
                Require(Math.Abs(count-66/rate)<=1,"Wrong source video speed: "+rate+"; frames="+count);
                string audio=Core.Tool(ffmpeg,new[]{"-v","error","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=2.2","-af",CreativeRender.SourceAudioFilter(0)+",atempo="+Core.N(rate),"-c:a","pcm_s16le","-f","framecrc","-"},CancellationToken.None,null,15).GetAwaiter().GetResult();
                var frames=System.Text.RegularExpressions.Regex.Matches(audio,@"(?m)^0,\s*-?\d+,\s*-?\d+,\s*(\d+),");
                long samples=0;foreach(System.Text.RegularExpressions.Match frame in frames)samples+=long.Parse(frame.Groups[1].Value);
                Require(Math.Abs(samples/48000.0-2.2/rate)<.05,"Wrong source audio speed: "+rate);
            }
            Console.WriteLine("Creative source speed PASS: 0.50/1.00/1.10/2.00 video and audio duration, XML, clone, legacy default");return true;
        }
        static void RunSourceAudioOrigin(string ffmpeg) {
            foreach(double videoStart in new[]{0.0,.1}) {
                string path=Path.Combine(Path.GetTempPath(),"creative-origin-"+Guid.NewGuid().ToString("N")+".pcm");
                try {
                    Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","aevalsrc=0.5:s=48000:d=0.1",
                        "-af","asetpts=PTS+"+Core.N(videoStart+.05)+"/TB,"+CreativeRender.SourceAudioFilter(videoStart),"-ac","1","-c:a","pcm_s16le","-f","s16le",path);
                    byte[] pcm=File.ReadAllBytes(path);int first=0;while(first<pcm.Length/2 && Math.Abs((int)BitConverter.ToInt16(pcm,first*2))<100)first++;
                    Require(Math.Abs(first-2400)<=1,"Original audio offset was lost: first sample "+first+"; video origin="+videoStart);
                } finally {if(File.Exists(path))File.Delete(path);}
            }
            Console.WriteLine("Creative source audio origin PASS: 50 ms preserved relative to video, zero/nonzero origins");
        }
        // Audio-only regression: no video encode and no user media. Final PCM timestamps
        // must follow sample count even when an upstream join supplies a timestamp jump.
        public static bool RunAudioClock(string ffmpeg) {
            RunSourceAudioOrigin(ffmpeg);
            foreach(bool limit in new[]{false,true}) {
                string text=Core.Tool(ffmpeg,new[]{"-hide_banner","-nostdin","-v","info","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=2",
                    "-af","asetpts='PTS+gte(T,1)*0.014/TB',"+CreativeRender.FinalAudioFilter(limit,2),"-c:a","pcm_s16le","-f","framecrc","-"},CancellationToken.None,null,15).GetAwaiter().GetResult();
                var frames=System.Text.RegularExpressions.Regex.Matches(text,@"(?m)^0,\s*(-?\d+),\s*(-?\d+),\s*(\d+),");
                Require(frames.Count>0,"No audio clock measurements");long samples=0;
                foreach(System.Text.RegularExpressions.Match frame in frames) {
                    long pts=long.Parse(frame.Groups[2].Value);
                    Require(Math.Abs(pts-samples)<=1,"Discontinuous final PCM clock; limiter="+limit+"; pts="+pts+"; samples="+samples);
                    samples+=int.Parse(frame.Groups[3].Value);
                }
                Require(samples>=95000 && samples<=96000,"Audio clock repair changed sample duration: "+samples);
            }
            Console.WriteLine("Creative final audio clock PASS: limiter on/off, continuous sample clock, no tempo change");return true;
        }
        public static bool Run(string ffmpeg,string ffprobe) {
            string root=Path.Combine(Path.GetTempPath(),"creative-render-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try {
                var p=new CreativeProject { Source=Path.Combine(root,"source.mp4"),IntroAudio=Path.Combine(root,"intro.wav"),IntroText="Intro" };
                p.Preset.PreserveSourceSize=false;p.Preset.Width=180;p.Preset.Height=320;p.Preset.Fps=20;p.Preset.MixDuration=.2;p.Preset.MotionDuration=.2;p.Preset.CardAppearanceDuration=.1;p.Preset.CardExitDuration=.1;
                // Alternating sound and visuals establish source-time restart and continuation after the ad.
                Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","color=c=red:s=160x90:r=20:d=0.6","-f","lavfi","-i","color=c=blue:s=160x90:r=20:d=0.6","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=0.6","-f","lavfi","-i","sine=frequency=550:sample_rate=48000:duration=0.6","-filter_complex","[0:v][1:v]concat=n=2:v=1:a=0[v];[2:a][3:a]concat=n=2:v=0:a=1[a]","-map","[v]","-map","[a]","-c:v","libx264","-pix_fmt","yuv420p","-c:a","aac",p.Source);
                Tone(ffmpeg,p.IntroAudio,880,.4);
                for(int i=0;i<3;i++) {
                    var c=new CreativeComment { ImagePath=Path.Combine(root,"card"+i+".png"),AudioPath=Path.Combine(root,"card"+i+".wav"),Text="Comment "+i };
                    using(var bmp=new Bitmap(100,40)) { using(var g=Graphics.FromImage(bmp))g.Clear(new[]{Color.Lime,Color.Yellow,Color.Magenta}[i]);bmp.Save(c.ImagePath,ImageFormat.Png); }
                    Tone(ffmpeg,c.AudioPath,1100+i*220,.3);p.Comments.Add(c);
                }
                p.Preset.IntroCover=p.Comments[0].ImagePath;p.Preset.IntroCoverBounds=new CreativeRect(.3,.4,.4,.2);p.Preset.IntroBlur=true;p.Preset.IntroMask=new CreativeRect(.1,.1,.3,.2);
                p.Preset.Banner=p.Comments[1].ImagePath;
                string ad=Path.Combine(root,"ad.mp4");Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","color=c=white:s=100x100:r=20:d=0.2","-f","lavfi","-i","sine=frequency=660:sample_rate=48000:duration=0.2","-c:v","libx264","-c:a","aac",ad);
                p.Preset.Insertions.Add(new AssemblyInsertion { Source=ad,Start=.6 });
                string xml=Path.Combine(root,"project.xml");p.Save(xml);var restored=CreativeProject.Load(xml);restored.Validate();
                Require(restored.Preset.AudioLevelingEnabled && restored.Comments[2].Text==p.Comments[2].Text && restored.Preset.IntroBlur && restored.Preset.Insertions.Count==1,"Project XML roundtrip failed");
                var clone=p.Preset.Clone();clone.FinalVideo.X=.01;clone.Speech.VoiceId="changed";clone.Insertions[0].Start=.4;
                Require(p.Preset.FinalVideo.X==.12 && p.Preset.Speech.VoiceId=="" && p.Preset.Insertions[0].Start==.6,"Preset clone is not deep");
                string presetFile=Path.Combine(root,"preset.xml");p.Preset.Save(presetFile);Require(CreativePreset.Load(presetFile).IntroCover==p.Preset.IntroCover,"Preset XML roundtrip failed");
                var geometry=new CreativePreset {Width=180,Height=320};
                var bounds=CreativeGeometry.SourceBounds(geometry,new Size(160,90));Require(bounds.Width==180 && bounds.Height==102 && bounds.X==0 && bounds.Y==109,"Default source fit mismatch");
                geometry.SourceZoom=.5;geometry.Validate();
                bounds=CreativeGeometry.SourceBounds(geometry,new Size(160,90));Require(bounds.Width==90 && bounds.Height==50 && bounds.X==45 && bounds.Y==135,"Reduced source geometry mismatch");
                geometry.SourceZoom=2;geometry.SourcePanX=.5;geometry.SourcePanY=-.25;
                bounds=CreativeGeometry.SourceBounds(geometry,new Size(160,90));Require(bounds.Width==360 && bounds.X==-45 && bounds.Y==19,"Source zoom/pan geometry mismatch");
                foreach(bool landscape in new[]{false,true}) {
                    p.Preset.Width=landscape?320:180;p.Preset.Height=landscape?180:320;
                    p.Preset.SourceZoom=landscape?1.4:.5;
                    if(landscape) {p.Preset.FinalVideo=new CreativeRect(.04,.13,.48,.74);p.Preset.Card=new CreativeRect(.55,.28,.42,.5);p.Preset.SourceZoom=1.4;p.Preset.SourcePanX=.15;p.Preset.SourcePanY=-.1;}
                    if(landscape)p.Preset.MediaLayers.Add(new AssemblyLayer {Source=p.Comments[0].ImagePath,X=0,Y=.1,Width=.1,Height=.1,PreserveAspectRatio=false});
                    string output=Path.Combine(root,landscape?"landscape.mp4":"portrait.mp4");
                    CreativeRender.RenderAsync(p,ffmpeg,ffprobe,output,CancellationToken.None).GetAwaiter().GetResult();
                    var info=Core.VideoInfo(Core.Probe(ffprobe,output,CancellationToken.None).GetAwaiter().GetResult());
                    // Intro .4 + full moving source 1.2 (first .2 under Mix) + ad .2 + motion .2 + three .5 cards.
                    Require(info.Width==p.Preset.Width && info.Height==p.Preset.Height && info.HasAudio && Math.Abs(info.VideoDuration-3.5)<.11,"Creative output metadata/timeline mismatch: "+info.VideoDuration);
                    string pcm=Path.Combine(root,"audio.pcm");Tool(ffmpeg,"-v","error","-y","-i",output,"-vn","-ac","1","-ar","8000","-f","s16le",pcm);byte[] data=File.ReadAllBytes(pcm);
                    Marker(data,.15,880,440,550);Require(Power(data,.44,.12,880)<Power(data,.44,.12,440)/1000 && Power(data,.44,.12,440)>100,"Mix must fade in original sound without a silent gap or speech tail cut: intro="+Power(data,.44,.12,880)+", source="+Power(data,.44,.12,440));
                    Marker(data,.64,440,880);Marker(data,1.04,660,440,550);Marker(data,1.29,550,660);
                    Marker(data,2.16,1100,440,550,1320);Marker(data,2.66,1320,1100,1540);Marker(data,3.16,1540,1100,1320);
                    Color mixed=Pixel(ffmpeg,output,root,.5,p.Preset.Width/2,p.Preset.Height/2);Require(mixed.R>35 && mixed.G>35 && mixed.B<60,"Mix is not a visual dissolve from intro cover to the original first frame");
                    Color bannerPixel=Pixel(ffmpeg,output,root,1.05,p.Preset.Width/2,(int)(p.Preset.Height*.04));Require(bannerPixel.R>180 && bannerPixel.G>180,"Global banner missing over ad block");
                    if(!landscape) {
                        Color margin=Pixel(ffmpeg,output,root,.68,12,p.Preset.Height/2);
                        Require(margin.R<25 && margin.G<25 && margin.B<25,"Reduced original lost its black canvas margin");
                        Color introMargin=Pixel(ffmpeg,output,root,.15,12,p.Preset.Height/2);
                        Require(introMargin.R<25 && introMargin.G<25 && introMargin.B<25,"Reduced intro framing differs from original");
                    }
                    Color start=Pixel(ffmpeg,output,root,.68,p.Preset.Width/2,p.Preset.Height/2);Require(start.R>start.B+100 && start.R>start.G+100,"Original moving beginning missing after intro dissolve");
                    Color after=Pixel(ffmpeg,output,root,1.55,p.Preset.Width/2,p.Preset.Height/2);Require(after.B>after.R+100,"Original continuation skipped/repeated at the ad");
                    int cardX=(int)((p.Preset.Card.X+p.Preset.Card.Width/2)*p.Preset.Width);
                    int[] cardY=Enumerable.Range(0,3).Select(i=>(int)((p.Preset.CommentBounds(i).Y+p.Preset.CommentBounds(i).Height/2)*p.Preset.Height)).ToArray();
                    Color card1=Pixel(ffmpeg,output,root,2.25,cardX,cardY[0]),card2=Pixel(ffmpeg,output,root,2.75,cardX,cardY[1]),card3=Pixel(ffmpeg,output,root,3.25,cardX,cardY[2]);
                    Color retained1=Pixel(ffmpeg,output,root,3.25,cardX,cardY[0]),retained2=Pixel(ffmpeg,output,root,3.25,cardX,cardY[1]);
                    Require(retained1.G>150 && retained1.R<60 && retained2.R>150 && retained2.G>150,"Earlier comments disappeared instead of accumulating");
                    Require(card1.G>150 && card1.R<60 && card2.R>150 && card2.G>150 && card3.R>150 && card3.B>150 && card3.G<60,"Screenshot order does not match comment voices");
                    var finalBounds=AssemblyRaster.FitImageBounds(new RectangleF((float)(p.Preset.FinalVideo.X*p.Preset.Width),(float)(p.Preset.FinalVideo.Y*p.Preset.Height),(float)(p.Preset.FinalVideo.Width*p.Preset.Width),(float)(p.Preset.FinalVideo.Height*p.Preset.Height)),new Size(p.Preset.Width,p.Preset.Height));
                    Color frozen=Pixel(ffmpeg,output,root,2.5,(int)(finalBounds.X+finalBounds.Width/2),(int)(finalBounds.Y+finalBounds.Height/2));Require(frozen.B>frozen.R+80,"Final composed freeze lost the last original frame");
                    if(!landscape) {Color bar=Pixel(ffmpeg,output,root,2.5,(int)(finalBounds.X+finalBounds.Width/2),(int)(finalBounds.Y+finalBounds.Height*.15));Require(bar.R<25 && bar.G<25 && bar.B<25,"Final freeze refit raw source and lost composed letterboxing");}
                    if(landscape)foreach(double at in new[]{.15,1.05,3.25}){Color mediaPixel=Pixel(ffmpeg,output,root,at,10,27);Require(mediaPixel.G>150&&mediaPixel.R<60,"Media overlay missing from joined creative at "+at);}
                    Console.WriteLine("Creative render PASS: "+ (landscape?"16:9":"9:16")+", serialisation, moving Mix with source audio, full original, ad pause, stacked comments and media layers");
                }
                var effected=Store.Clone(p);effected.Preset.Effects.ColorPreset="mono";effected.Preset.Effects.ColorStrength=1;effected.Preset.MediaLayers.Clear();effected.Preset.IntroCover="";effected.Preset.Banner="";effected.Preset.IntroBlur=false;
                string effectsOutput=Path.Combine(root,"effects-full.mp4");CreativeRender.RenderAsync(effected,ffmpeg,ffprobe,effectsOutput,CancellationToken.None).GetAwaiter().GetResult();
                foreach(double at in new[]{.15,.8,2.3}){var gray=Pixel(ffmpeg,effectsOutput,root,at,80,90);Require(Math.Abs(gray.R-gray.G)<8&&Math.Abs(gray.G-gray.B)<8,"Source effects missing at "+at);}
                var colorBounds=effected.Preset.CommentBounds(2);var intact=Pixel(ffmpeg,effectsOutput,root,3.25,(int)((colorBounds.X+colorBounds.Width/2)*effected.Preset.Width),(int)((colorBounds.Y+colorBounds.Height/2)*effected.Preset.Height));Require(intact.R>150&&intact.B>150&&intact.G<60,"Source effect changed comment overlay");
                string effectsXml=Path.Combine(root,"effects-project.xml");effected.Save(effectsXml);Require(CreativeProject.Load(effectsXml).Preset.Effects.ColorPreset=="mono","Effects settings did not persist");Console.WriteLine("Creative integrated effects PASS: intro/main/freeze, unaffected comment, XML");
                var silentPreset=p.Preset.Clone();silentPreset.MixDuration=0;silentPreset.MotionDuration=0;silentPreset.CardAppearance="cut";silentPreset.CardAppearanceDuration=0;silentPreset.CardExitDuration=0;silentPreset.IntroBlur=false;silentPreset.IntroCover="";silentPreset.Banner="";silentPreset.Insertions.Clear();
                string silentSource=Path.Combine(root,"silent-source.mp4");Tool(ffmpeg,"-v","error","-y","-i",p.Source,"-map","0:v:0","-c:v","copy","-an",silentSource);
                var silentProject=new CreativeProject {Source=silentSource,IntroAudio=p.IntroAudio,Comments=p.Comments,Preset=silentPreset};
                string silentOutput=Path.Combine(root,"silent-cut.mp4");CreativeRender.RenderAsync(silentProject,ffmpeg,ffprobe,silentOutput,CancellationToken.None).GetAwaiter().GetResult();
                var silentInfo=Core.VideoInfo(Core.Probe(ffprobe,silentOutput,CancellationToken.None).GetAwaiter().GetResult());Require(Math.Abs(silentInfo.VideoDuration-2.5)<.1,"Cut/silent-source timeline mismatch");
                string silentPcm=Path.Combine(root,"silent.pcm");Tool(ffmpeg,"-v","error","-y","-i",silentOutput,"-vn","-ac","1","-ar","8000","-f","s16le",silentPcm);byte[] silence=File.ReadAllBytes(silentPcm);Require(Power(silence,.7,.2,880)<10,"Intro speech leaked into silent main block");Marker(silence,1.68,1100,880);Marker(silence,2.03,1320,1100);Marker(silence,2.33,1540,1320);
                Console.WriteLine("Creative silent-source/cut/zero-motion PASS");
                using(var cancelled=new CancellationTokenSource()) {cancelled.Cancel();bool caught=false;try{CreativeRender.RenderAsync(p,ffmpeg,ffprobe,Path.Combine(root,"cancel.mp4"),cancelled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){caught=true;}Require(caught&&!File.Exists(Path.Combine(root,"cancel.mp4")),"Cancellation published an output");}
                bool activeCancelled=false;using(var cancel=new CancellationTokenSource()) {
                    string cancelledOutput=Path.Combine(root,"active-cancel.mp4");try {CreativeRender.RenderAsync(p,ffmpeg,ffprobe,cancelledOutput,cancel.Token,(s,p)=>{if(s.Contains("вступление"))cancel.Cancel();}).GetAwaiter().GetResult();}catch(OperationCanceledException){activeCancelled=true;}
                    Require(activeCancelled&&!File.Exists(cancelledOutput),"Active cancellation published output");
                }
                RunAudioAndMotion(ffmpeg,ffprobe,root,p);
                var native=Store.Clone(p);native.Preset.PreserveSourceSize=true;native.Preset.MediaLayers.Clear();native.Preset.Insertions.Clear();
                native.Preset.SourceZoom=1;native.Preset.SourcePanX=native.Preset.SourcePanY=0;
                string nativeOutput=Path.Combine(root,"native.mp4");CreativeRender.RenderAsync(native,ffmpeg,ffprobe,nativeOutput,CancellationToken.None).GetAwaiter().GetResult();
                var nativeInfo=Core.VideoInfo(Core.Probe(ffprobe,nativeOutput,CancellationToken.None).GetAwaiter().GetResult());
                Require(nativeInfo.Width==160 && nativeInfo.Height==90,"Native source dimensions were changed");
                string nativeFilter=CreativeGeometry.SourceFilter(native.Preset,new Size(160,90));
                Require(!nativeFilter.Contains("scale=")&&!nativeFilter.Contains("pad=")&&!nativeFilter.Contains("crop=")&&!nativeFilter.Contains("yuv444p"),"Native path still transforms geometry: "+nativeFilter);
                Console.WriteLine("Creative native-size/no redundant geometry PASS");
                foreach(string kind in new[]{"portrait","square","rotated","sar"}) {
                    string sized=Path.Combine(root,"native-"+kind+"-source.mp4");
                    if(kind=="rotated")Tool(ffmpeg,"-v","error","-y","-i",p.Source,"-c","copy","-metadata:s:v:0","rotate=90",sized);
                    else if(kind=="sar")Tool(ffmpeg,"-v","error","-y","-i",p.Source,"-vf","setsar=2/1","-c:v","libx264","-c:a","copy",sized);
                    else Tool(ffmpeg,"-v","error","-y","-f","lavfi","-i","color=c=blue:s="+(kind=="portrait"?"90x160":"120x120")+":r=20:d=1.2","-c:v","libx264",sized);
                    native.Source=sized;string resizedOutput=Path.Combine(root,"native-"+kind+".mp4");
                    CreativeRender.RenderAsync(native,ffmpeg,ffprobe,resizedOutput,CancellationToken.None).GetAwaiter().GetResult();
                    var expected=Core.VideoInfo(Core.Probe(ffprobe,sized,CancellationToken.None).GetAwaiter().GetResult());
                    var actual=Core.VideoInfo(Core.Probe(ffprobe,resizedOutput,CancellationToken.None).GetAwaiter().GetResult());
                    Require(actual.Width==expected.Width && actual.Height==expected.Height,"Native geometry mismatch for "+kind+": "+actual.Width+"x"+actual.Height);
                }
                Console.WriteLine("Creative native portrait/square/rotation/sample-aspect PASS");
                Tone(ffmpeg,p.IntroAudio,880,3.2);bool overLimit=false;try {CreativeRender.RenderAsync(p,ffmpeg,ffprobe,Path.Combine(root,"over-limit.mp4"),CancellationToken.None).GetAwaiter().GetResult();}catch(Exception e){overLimit=e.Message.Contains("не дольше 3");}Require(overLimit,"Intro above 3 seconds was accepted");
                Tone(ffmpeg,p.IntroAudio,880,1.5);bool tooLong=false;try {CreativeRender.RenderAsync(p,ffmpeg,ffprobe,Path.Combine(root,"too-long.mp4"),CancellationToken.None).GetAwaiter().GetResult();}catch(Exception e){tooLong=e.Message.Contains("длиннее исходника");}Require(tooLong,"Long intro silently looped/froze the source");
                Require(Directory.GetDirectories(root,"creative-work-*").Length==0,"Render left temporary directories behind");
                Console.WriteLine("Creative validation/cancellation PASS");return true;
            } finally {Directory.Delete(root,true);}
        }
    }
}
