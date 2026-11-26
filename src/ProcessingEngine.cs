using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class ProcessingEngine {
        static string N(double value) { return Core.N(value); }
        // Shared by every processing entry point. Filters remain on the CPU;
        // only the final H.264 encode is moved to a tested hardware encoder.
        public static async Task Accelerate(string ffmpeg,List<string> args,int width,int height,CancellationToken ct,Action<string> status=null,AssemblyRenderContext context=null) {
            int codec=args.IndexOf("-c:v");
            if(codec<0 || args[codec+1]!="libx264")return; // Remux/copy paths are untouched.
            context=context??new AssemblyRenderContext();
            string work=Path.Combine(Path.GetTempPath(),"vb-encoder-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try {await AssemblyRender.SelectEncoder(new AssemblyTemplate {Width=width,Height=height,Fps=30,RenderMode="auto"},ffmpeg,work,context,ct).ConfigureAwait(false);}
            finally {Directory.Delete(work,true);}
            string encoder=context.Encoder;
            status?.Invoke(encoder=="libx264"?"Кодирование: процессор (GPU-кодировщик недоступен)":"Кодирование: видеокарта · "+encoder+"; фильтры: процессор");
            AppDiagnostics.Write("Processing","","","encoder",encoder,"info");
            if(encoder=="libx264")return;
            args[codec+1]=encoder;
            int preset=args.IndexOf("-preset");
            if(preset>=0){if(encoder=="h264_amf")args.RemoveRange(preset,2);else args[preset+1]=encoder=="h264_nvenc"?"p4":"fast";}
            int quality=args.IndexOf("-crf");
            if(quality>=0) {
                string value=args[quality+1];args.RemoveRange(quality,2);
                if(encoder=="h264_nvenc")args.InsertRange(quality,new[]{"-rc","vbr","-cq",value,"-b:v","0"});
                else if(encoder=="h264_qsv")args.InsertRange(quality,new[]{"-global_quality",value});
                else args.InsertRange(quality,new[]{"-quality","speed","-rc","cqp","-qp_i",value,"-qp_p",value});
            } else if(encoder=="h264_amf")args.InsertRange(args.Count-1,new[]{"-quality","speed"});
        }
        public static void Apply(Profile p, ProcessingSettings settings, Media media, Random random, DateTime now) {
            if (settings == null || !settings.IsEnabled) return;
            settings.Validate();
            p.Crop = settings.Crop.Sample(random, p.Crop);
            p.Speed = settings.Speed.Sample(random, p.Speed * 100) / 100;
            p.Brightness = settings.Brightness.Sample(random, p.Short ? p.Brightness * 100 : 0) / 100;
            p.Contrast = settings.Contrast.Sample(random, p.Short ? p.Contrast * 100 : 100) / 100;
            double cut = media.Duration * settings.Trim.Sample(random, 0) / 100;
            double start = cut * random.NextDouble();
            bool horizontal = media.Width >= media.Height;
            double rotation = settings.Rotation.Sample(random, 0);
            if (horizontal) {
                const double cap = .2;
                if (rotation > cap) rotation = cap;
                else if (rotation < -cap) rotation = -cap;
            }
            var s = new ProcessingSample {
                TrimStart = start, TrimEnd = cut - start, Rotation = rotation,
                Sharpness = settings.Sharpness.Sample(random, 0), Noise = settings.Noise.Sample(random, 0),
                FilmDust = settings.FilmDust.Sample(random, 0),
                FrameScale = settings.FrameScale.Sample(random, 100) / 100,
                Volume = settings.Volume.Sample(random, 100) / 100, Pitch = settings.Pitch.Sample(random, 100) / 100,
                Tempo = settings.Tempo.Sample(random, 100) / 100, FadeIn = settings.FadeIn.Sample(random, 0),
                FadeOut = settings.FadeOut.Sample(random, 0), Mirror = settings.Mirror && random.Next(2) == 0,
                GridOpacity = settings.GridOpacity.Sample(random, 0) / 100,
                GridCell = settings.GridCell.Sample(random), Zoom = settings.Zoom.Sample(random, 100) / 100,
                ZoomPeriod = settings.ZoomPeriod.Sample(random), ZoomX = .3 + random.NextDouble() * .4,
                ZoomY = .3 + random.NextDouble() * .4, ModulationDepth = settings.AudioModulation.Sample(random, 0) / 100,
                ModulationFrequency = settings.ModulationFrequency.Sample(random), NoiseSeed = random.Next(),
                FilmDustSeed = random.Next()
            };
            s.Gamma=settings.Gamma.Sample(random,100)/100;
            s.RedShift=settings.RedShift.Sample(random,0)/100; s.GreenShift=settings.GreenShift.Sample(random,0)/100; s.BlueShift=settings.BlueShift.Sample(random,0)/100;
            s.Geq=settings.Geq.Sample(random,0); s.TemporalBlend=settings.TemporalBlend.Sample(random,0)/100;
            s.BlurFrame=settings.BlurFrame.Sample(random,100)/100; s.ColorLut=settings.ColorLut.Sample(random,0)/100; s.ColorPreset=settings.ColorPreset;
            s.Reverb=settings.Reverb.Sample(random,0)/100; s.Flanger=settings.Flanger.Sample(random,0);
            s.EqLow=settings.EqLow.Sample(random,0); s.EqMid=settings.EqMid.Sample(random,0); s.EqHigh=settings.EqHigh.Sample(random,0);
            s.Lowpass=settings.Lowpass.Sample(random,0); s.Highpass=settings.Highpass.Sample(random,0);
            if (settings.DeviceMetadata) {
                var device = ProcessingDevices.Sample(settings.Brands, random);
                s.Brand = device.Brand; s.Model = device.Model;
            }
            s.Software=(settings.MetadataSoftware ?? "").Trim();
            if (settings.CaptureDateEnabled) {
                s.HasCaptureDate = true;
                s.CaptureDate = settings.RandomizeCaptureTime ? now.AddSeconds(-random.NextDouble() * settings.CaptureDaysBack * 86400)
                    : now.AddDays(-random.Next(settings.CaptureDaysBack + 1));
                p.CustomDate = s.CaptureDate; p.FileDate = "custom";
            }
            p.Processing = s;
        }

        public static double OutputDuration(Profile p, Media media) {
            return (media.Duration - (p.Processing == null ? 0 : p.Processing.TrimStart + p.Processing.TrimEnd)) / p.Speed;
        }
        public static IEnumerable<string> TempoFilters(double rate) {
            while (rate > 2) { yield return "atempo=2"; rate /= 2; }
            while (rate < .5) { yield return "atempo=0.5"; rate /= .5; }
            yield return "atempo=" + N(rate);
        }
        static string FinishAudio(ProcessingSample s, double duration) {
            var f = new List<string> { "asetpts=PTS-STARTPTS" };
            if (Math.Abs(s.Pitch - 1) > .000001) {
                f.Add("aresample=48000"); f.Add("asetrate=" + N(48000 * s.Pitch)); f.Add("aresample=48000");
                f.AddRange(TempoFilters(1 / s.Pitch));
            }
            if (Math.Abs(s.Tempo - 1) > .000001) f.AddRange(TempoFilters(s.Tempo));
            if (Math.Abs(s.Volume - 1) > .000001) f.Add("volume=" + N(s.Volume));
            if (s.ModulationDepth > 0) f.Add("volume='if(isnan(t),1,1+" + N(s.ModulationDepth) + "*(0.5-0.5*cos(2*PI*" + N(s.ModulationFrequency) + "*t)))':eval=frame");
            if(s.Highpass>0)f.Add("highpass=f="+N(s.Highpass));
            if(s.Lowpass>0)f.Add("lowpass=f="+N(s.Lowpass));
            if(s.EqLow!=0)f.Add("equalizer=f=120:t=q:w=1:g="+N(s.EqLow));
            if(s.EqMid!=0)f.Add("equalizer=f=1000:t=q:w=1:g="+N(s.EqMid));
            if(s.EqHigh!=0)f.Add("equalizer=f=8000:t=q:w=1:g="+N(s.EqHigh));
            if(s.Reverb>0)f.Add("aecho=in_gain=1:out_gain="+N(1/(1+s.Reverb))+":delays=23|41:decays="+N(s.Reverb*.6)+"|"+N(s.Reverb*.4));
            if(s.Flanger>0)f.Add("flanger=delay=0:depth=0.1:regen=0:width="+N(s.Flanger)+":speed=0.2");
            if (s.Volume > 1 || s.ModulationDepth > 0 || s.Reverb>0 || s.Flanger>0 || s.EqLow>0 || s.EqMid>0 || s.EqHigh>0) f.Add("alimiter=limit=0.95:level=false:latency=1");
            f.Add("apad"); f.Add("atrim=duration=" + N(duration)); return string.Join(",", f);
        }

        internal static string VideoFilters(Profile p, Media m) {
            p.Validate(); var s = p.Processing;
            double duration = OutputDuration(p, m), end = m.Duration - s.TrimEnd;
            if (duration <= 0) throw new Exception("Подрезка оставила пустое видео.");
            int w = m.Width, h = m.Height;
            if (p.Resolution != "source") {
                double sh = Core.Parse(p.Resolution), lo = Math.Round(sh * 16 / 9);
                double factor = w >= h ? Math.Min(1, Math.Min(lo / w, sh / h)) : Math.Min(1, Math.Min(sh / w, lo / h));
                w = (int)Math.Max(2, Math.Floor(w * factor / 2) * 2); h = (int)Math.Max(2, Math.Floor(h * factor / 2) * 2);
            }
            w = (int)Math.Max(2, Math.Floor(w * p.ScalePercent / 100 / 2) * 2);
            h = (int)Math.Max(2, Math.Floor(h * p.ScalePercent / 100 / 2) * 2);
            double fps = (p.Fps == "source" ? Fraction(m.Fps) : Core.Parse(p.Fps)) * p.FpsPercent / 100;
            var vf = new List<string> { "setpts=PTS-STARTPTS", "trim=start=" + N(s.TrimStart) + ":end=" + N(end), "setpts=(PTS-STARTPTS)/" + N(p.Speed) };
            if (p.Crop > 0) {
                string rem = N(1 - 2 * p.Crop / 100);
                vf.Add("crop=trunc(iw*" + rem + "/2)*2:trunc(ih*" + rem + "/2)*2");
            }
            if (Math.Abs(s.Rotation) > .000001)
                vf.Add("rotate=" + N(s.Rotation) + "*PI/180:ow=iw:oh=ih:fillcolor=black");
            // bicubic keeps uniqueness and mild rescales visually intact; lanczos costs more for no fingerprint gain.
            vf.Add("scale=" + w + ":" + h + ":flags=bicubic"); vf.Add("setsar=1");
            if (Math.Abs(s.FrameScale - 1) > .0005) {
                if (s.FrameScale < 1)
                    vf.Add("scale=trunc(iw*" + N(s.FrameScale) + "/2)*2:trunc(ih*" + N(s.FrameScale) + "/2)*2:flags=bicubic,pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2:color=black");
                else
                    vf.Add("scale=trunc(iw*" + N(s.FrameScale) + "/2)*2:trunc(ih*" + N(s.FrameScale) + "/2)*2:flags=bicubic,crop=" + w + ":" + h);
            }
            if (s.Mirror) vf.Add("hflip");
            if (p.Gray > 0) vf.Add("hue=s=" + N(1 - p.Gray / 100));
            if (p.Short || Math.Abs(p.Brightness) > .000001 || Math.Abs(p.Contrast - 1) > .000001)
                vf.Add("eq=brightness=" + N(p.Brightness) + ":contrast=" + N(p.Contrast));
            if(Math.Abs(s.Gamma-1)>.000001)vf.Add("eq=gamma="+N(s.Gamma));
            bool channelShift = s.RedShift != 0 || s.GreenShift != 0 || s.BlueShift != 0;
            if (channelShift && s.ColorLut <= 0)
                vf.Add("colorchannelmixer=rr="+N(1+s.RedShift)+":gg="+N(1+s.GreenShift)+":bb="+N(1+s.BlueShift));
            if(s.ColorLut>0) {
                double k=s.ColorLut;
                string r="val",g="val",b="val";
                if(s.ColorPreset=="warm"){r="val*"+N(1+.06*k);b="val*"+N(1-.06*k);}
                else if(s.ColorPreset=="cool"){r="val*"+N(1-.06*k);b="val*"+N(1+.06*k);}
                else if(s.ColorPreset=="film"){r=g=b="(val-128)*"+N(1+.12*k)+"+128";}
                else {r="val*"+N(1+.04*k)+"+"+N(3*k);g="val+"+N(2*k);b="val*"+N(1-.08*k)+"+"+N(4*k);}
                // Both operations are channel-local lookup tables. Compose them once
                // instead of visiting every RGB pixel twice. Preserve the mixer's
                // clipped, nearest-even integer rounding before the LUT truncates.
                if (channelShift) {
                    r = r.Replace("val", ShiftedChannel(1 + s.RedShift));
                    g = g.Replace("val", ShiftedChannel(1 + s.GreenShift));
                    b = b.Replace("val", ShiftedChannel(1 + s.BlueShift));
                }
                vf.Add("lutrgb=r='"+r+"':g='"+g+"':b='"+b+"'");
            }
            // GEQ changes luma only. Pass chroma planes through instead of evaluating
            // two identity expressions per pixel; preserve the original yuv444p output.
            if(s.Geq>0)vf.Add("format=yuv444p,extractplanes=y+u+v[geqY][geqU][geqV];[geqY]geq=lum='clip(lum(X,Y)+"+N(s.Geq)+"*sin(X*0.73+Y*0.37+N*1.7),0,255)'[geqLuma];[geqLuma][geqU][geqV]mergeplanes=0x001020:yuv444p");
            // Duplicate the first frame before tblend so its one-frame lookback
            // neither shortens the roll nor shifts audio relative to the image.
            if(s.TemporalBlend>0)vf.Add("tpad=start=1:start_mode=clone,tblend=all_mode=normal:all_opacity="+N(1-s.TemporalBlend)+",setpts=PTS-STARTPTS");
            if (Math.Abs(s.Sharpness) > .000001) vf.Add("unsharp=5:5:" + N(s.Sharpness / 25) + ":5:5:0");
            if (s.Noise > 0) vf.Add("noise=alls=" + N(s.Noise) + ":allf=t+u:all_seed=" + s.NoiseSeed);
            if (s.FilmDust > 0) {
                double strength = Math.Max(.5, s.FilmDust * .35);
                vf.Add("noise=c0s=" + N(strength) + ":c0f=t:c1s=0:c2s=0:c0_seed=" + s.FilmDustSeed);
            }
            vf.Add("fps=" + N(fps));
            if (s.Zoom > 1) {
                // Mild zoom: fixed peak crop + moving window keeps the fingerprint motion without zoompan's per-pixel path.
                // Stronger zoom keeps the original pulsing zoompan look.
                if (s.Zoom <= 1.08) {
                    string z = N(s.Zoom), period = N(s.ZoomPeriod);
                    vf.Add("crop=trunc(iw/" + z + "/2)*2:trunc(ih/" + z + "/2)*2:'(iw-ow)*(" + N(s.ZoomX) + "+0.08*sin(2*PI*t/" + period + "))':'(ih-oh)*(" + N(s.ZoomY) + "+0.08*cos(2*PI*t/" + period + "*1.07))',scale=" + w + ":" + h + ":flags=bicubic");
                } else vf.Add("zoompan=z='1+" + N(s.Zoom - 1) + "*(0.5-0.5*cos(2*PI*on/" + N(fps * s.ZoomPeriod) + "))':x='(iw-iw/zoom)*" + N(s.ZoomX) + "':y='(ih-ih/zoom)*" + N(s.ZoomY) + "':d=1:s=" + w + "x" + h + ":fps=" + N(fps));
            }
            // drawgrid repaints every frame on one thread; draw the same grid once and overlay it.
            if (s.GridOpacity > 0) vf.Add("null[gridMain];color=c=black@0:s=" + w + "x" + h + ":r=" + N(fps) + ",format=rgba,drawgrid=w=" + Math.Max(8, (int)Math.Round(s.GridCell)) + ":h=" + Math.Max(8, (int)Math.Round(s.GridCell)) + ":t=1:c=white@" + N(s.GridOpacity)
                + ":replace=1,trim=end_frame=1,loop=loop=-1:size=1:start=0[gridLayer];[gridMain][gridLayer]overlay=0:0:format=yuv420:shortest=1");
            double fadeIn = Math.Min(s.FadeIn, duration / 2), fadeOut = Math.Min(s.FadeOut, duration / 2);
            if (fadeIn > 0) vf.Add("fade=t=in:st=0:d=" + N(fadeIn));
            if (fadeOut > 0) vf.Add("fade=t=out:st=" + N(duration - fadeOut) + ":d=" + N(fadeOut));
            // FrameScale's inner scale changes SAR to preserve its input DAR.
            // After pad/crop the output is the requested canvas again, with square pixels.
            vf.Add("setsar=1");
            if(s.BlurFrame<1) {
                int fw=Math.Max(2,(int)(w*s.BlurFrame/2)*2),fh=Math.Max(2,(int)(h*s.BlurFrame/2)*2);
                vf.Add("split=2[frameBackground][frameForeground];[frameBackground]gblur=sigma=12[frameBlur];[frameForeground]scale="+fw+":"+fh+",setsar=1[frameSmall];[frameBlur][frameSmall]overlay=(W-w)/2:(H-h)/2:shortest=1,setsar=1");
            }
            return string.Join(",", vf);
        }
        internal static string AudioTiming(Profile p, Media m, bool pad = false) {
            return "asetpts=PTS-STARTPTS," + (pad ? "apad," : "") + "atrim=start=" + N(p.Processing.TrimStart) + ":end=" + N(m.Duration-p.Processing.TrimEnd) + ",asetpts=PTS-STARTPTS," + string.Join(",", TempoFilters(p.Speed)) + ",aresample=async=1:first_pts=0,apad,atrim=duration=" + N(OutputDuration(p,m));
        }
        internal static void AppendPrefixAudio(List<string> graph, string source, Profile p, Media m, double prefix) {
            graph.Add("["+source+"]asplit=2[introSource][mainSource]");
            graph.Add("[introSource]atrim=end="+N(prefix)+",asetpts=PTS-STARTPTS[introAudio]");
            graph.Add("[mainSource]atrim=start="+N(prefix)+",asetpts=PTS-STARTPTS,"+FinishAudio(p.Processing,Math.Max(.001,OutputDuration(p,m)-prefix))+"[mainAudio]");
            graph.Add("[introAudio][mainAudio]concat=n=2:v=0:a=1[processed_audio]");
        }
        internal static void AppendFinalGraph(List<string> graph, string video, string audio, Profile p, Media m, double prefix) {
            // The processing graph owns its internal labels, just as the standalone wrapper does.
            graph.Add("["+video+"]"+VideoFilters(p,m)+"[processed_video]");
            if (audio == null) return;
            if (prefix > 0) AppendPrefixAudio(graph,audio,p,m,prefix);
            else graph.Add("["+audio+"]"+AudioTiming(p,m)+","+FinishAudio(p.Processing,OutputDuration(p,m))+"[processed_audio]");
        }
        internal static List<string> FinalEncoderArguments(string encoder, Profile p, int threads) {
            var args = AssemblyRender.EncoderArguments(encoder,threads);
            int quality = args.IndexOf(encoder == "h264_nvenc" ? "-cq" : encoder == "h264_qsv" ? "-global_quality" : encoder == "h264_amf" ? "-qp_i" : "-crf");
            args[quality+1] = N(p.Crf);
            if (encoder == "h264_amf") args[args.IndexOf("-qp_p")+1] = N(p.Crf);
            if (encoder == "libx264") args[args.IndexOf("-preset")+1] = "fast";
            return args;
        }
        public static List<string> Arguments(string input, string output, Profile p, Media m, string narration, double untouchedPrefix = 0) {
            string vf = VideoFilters(p,m);
            var s = p.Processing;
            double duration = OutputDuration(p,m);
            bool voice = !string.IsNullOrWhiteSpace(narration), background = !p.Short && !string.IsNullOrWhiteSpace(p.MusicPath);
            bool audio = p.Short || voice || m.HasAudio || background;
            var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-loglevel", "error", "-stats_period", "0.3", "-progress", "pipe:1", "-i", input };
            if (p.Short) args.AddRange(new[] { "-stream_loop", "-1", "-ss", N(p.MusicStart), "-i", p.MusicPath });
            else if (voice) args.AddRange(new[] { "-i", narration });
            if (background) args.AddRange(new[] { "-stream_loop", "-1", "-ss", N(p.MusicStart), "-i", p.MusicPath });
            args.AddRange(new[] { "-map", "0:" + m.VideoIndex });
            var graph = new List<string>();
            string source = null;
            if (p.Short || background) {
                int musicIndex = p.Short ? 1 : voice ? 2 : 1; double fade = Math.Min(p.Short ? .12 : .2, duration / 4);
                graph.Add("[" + musicIndex + ":a:0]asetpts=PTS-STARTPTS,volume=" + N(p.MusicVolume) + ",afade=t=in:d=" + N(fade) + ",afade=t=out:st=" + N(duration - fade) + ":d=" + N(fade) + ",apad,atrim=duration=" + N(duration) + "[bg]");
                source = "bg";
            }
            if (!p.Short && (voice || m.HasAudio)) {
                string main = voice ? "1:a:0" : "0:" + m.AudioIndex;
                string timing = AudioTiming(p,m,voice);
                graph.Add("[" + main + "]" + timing + "[speech]");
                if (background) { graph.Add("[speech][bg]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95:level=false:latency=1[mix]"); source = "mix"; }
                else source = "speech";
            }
            if (audio) {
                if (untouchedPrefix > 0 && !voice && !background && !p.Short) {
                    // Preserve intro audio and its timestamps; effects only see the main section.
                    AppendPrefixAudio(graph,"0:"+m.AudioIndex,p,m,untouchedPrefix);
                    // The generic full-roll speech chain is not used for this path.
                    graph.RemoveAll(line => line.EndsWith("[speech]", StringComparison.Ordinal));
                } else graph.Add("[" + source + "]" + FinishAudio(s, duration) + "[processed_audio]");
                args.AddRange(new[] { "-filter_complex", string.Join(";", graph), "-map", "[processed_audio]", "-c:a", "aac", "-b:a", "160k" });
            }
            args.AddRange(new[] { "-vf", vf, "-t", N(duration), "-c:v", "libx264", "-preset", "fast", "-pix_fmt", "yuv420p" });
            if (p.Compression == "quality") args.AddRange(new[] { "-crf", N(p.Crf) });
            else {
                double rate = p.Compression == "sourcebitrate" ? Math.Max(.05, m.VideoBitrate * p.BitratePercent / 100) : p.Bitrate;
                args.AddRange(new[] { "-b:v", N(rate) + "M", "-maxrate", N(rate * 1.5) + "M", "-bufsize", N(rate * 2) + "M" });
            }
            if (p.Metadata == "keep") args.AddRange(new[] { "-map_metadata", "0" });
            else args.AddRange(new[] { "-map_metadata", "-1", "-map_metadata:s:v", "-1", "-map_metadata:s:a", "-1", "-map_chapters", "-1" });
            // Date modes remain compatible; an offset-bearing textual date accompanies UTC movie/track dates.
            if (!s.HasCaptureDate && (p.Metadata=="now" || p.Metadata=="custom")) {
                s.HasCaptureDate=true; s.CaptureDate=p.Metadata=="custom" ? p.CustomDate : DateTime.Now;
            }
            VideoMetadata.Add(args,s,audio);
            args.AddRange(new[] { "-metadata:s:v:0", "rotate=0" });
            if (p.Format != "mkv") args.AddRange(new[] { "-movflags", string.IsNullOrEmpty(s.Brand) && !s.HasCaptureDate && string.IsNullOrWhiteSpace(s.Software) ? "+faststart" : "+faststart+use_metadata_tags" });
            args.Add(output); return args;
        }
        static string ShiftedChannel(double gain) {
            string x = "val*" + N(gain);
            return "clip(floor(" + x + ")+gt(" + x + "-floor(" + x + "),0.5)+eq(" + x
                + "-floor(" + x + "),0.5)*mod(floor(" + x + "),2),0,255)";
        }
        static double Fraction(string value) {
            var parts = value.Split('/'); return Core.Parse(parts[0]) / (parts.Length == 2 ? Core.Parse(parts[1]) : 1);
        }
    }
}
