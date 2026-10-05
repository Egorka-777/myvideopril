using System;
using System.Collections.Generic;
using System.Linq;

namespace VideoBatch {
    public static class ProcessingEngine {
        static string N(double value) { return Core.N(value); }
        public static void Apply(Profile p, ProcessingSettings settings, Media media, Random random, DateTime now) {
            if (settings == null || !settings.IsEnabled) return;
            settings.Validate();
            p.Crop = settings.Crop.Sample(random, p.Crop);
            p.Speed = settings.Speed.Sample(random, p.Speed * 100) / 100;
            p.Brightness = settings.Brightness.Sample(random, p.Short ? p.Brightness * 100 : 0) / 100;
            p.Contrast = settings.Contrast.Sample(random, p.Short ? p.Contrast * 100 : 100) / 100;
            double cut = media.Duration * settings.Trim.Sample(random, 0) / 100;
            double start = cut * random.NextDouble();
            var s = new ProcessingSample {
                TrimStart = start, TrimEnd = cut - start, Rotation = settings.Rotation.Sample(random, 0),
                Sharpness = settings.Sharpness.Sample(random, 0), Noise = settings.Noise.Sample(random, 0),
                Volume = settings.Volume.Sample(random, 100) / 100, Pitch = settings.Pitch.Sample(random, 100) / 100,
                Tempo = settings.Tempo.Sample(random, 100) / 100, FadeIn = settings.FadeIn.Sample(random, 0),
                FadeOut = settings.FadeOut.Sample(random, 0), Mirror = settings.Mirror,
                GridOpacity = settings.GridOpacity.Sample(random, 0) / 100,
                GridCell = settings.GridCell.Sample(random), Zoom = settings.Zoom.Sample(random, 100) / 100,
                ZoomPeriod = settings.ZoomPeriod.Sample(random), ZoomX = .3 + random.NextDouble() * .4,
                ZoomY = .3 + random.NextDouble() * .4, ModulationDepth = settings.AudioModulation.Sample(random, 0) / 100,
                ModulationFrequency = settings.ModulationFrequency.Sample(random), NoiseSeed = random.Next()
            };
            if (settings.DeviceMetadata) {
                var device = ProcessingDevices.Sample(settings.Brands, random);
                s.Brand = device.Brand; s.Model = device.Model; s.Software = device.Software;
            }
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
            if (s.Volume > 1 || s.ModulationDepth > 0) f.Add("alimiter=limit=0.95:level=false:latency=1");
            f.Add("apad"); f.Add("atrim=duration=" + N(duration)); return string.Join(",", f);
        }

        public static List<string> Arguments(string input, string output, Profile p, Media m, string narration) {
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
            if (Math.Abs(s.Rotation) > .000001) vf.Add("rotate=" + N(s.Rotation) + "*PI/180:ow=iw:oh=ih:fillcolor=black");
            vf.Add("scale=" + w + ":" + h + ":flags=lanczos"); vf.Add("setsar=1");
            if (s.Mirror) vf.Add("hflip");
            if (p.Gray > 0) vf.Add("hue=s=" + N(1 - p.Gray / 100));
            if (p.Short || Math.Abs(p.Brightness) > .000001 || Math.Abs(p.Contrast - 1) > .000001)
                vf.Add("eq=brightness=" + N(p.Brightness) + ":contrast=" + N(p.Contrast));
            if (Math.Abs(s.Sharpness) > .000001) vf.Add("unsharp=5:5:" + N(s.Sharpness / 25) + ":5:5:0");
            if (s.Noise > 0) vf.Add("noise=alls=" + N(s.Noise) + ":allf=t+u:all_seed=" + s.NoiseSeed);
            vf.Add("fps=" + N(fps));
            if (s.Zoom > 1) vf.Add("zoompan=z='1+" + N(s.Zoom - 1) + "*(0.5-0.5*cos(2*PI*on/" + N(fps * s.ZoomPeriod) + "))':x='(iw-iw/zoom)*" + N(s.ZoomX) + "':y='(ih-ih/zoom)*" + N(s.ZoomY) + "':d=1:s=" + w + "x" + h + ":fps=" + N(fps));
            if (s.GridOpacity > 0) vf.Add("drawgrid=w=" + Math.Max(8, (int)Math.Round(s.GridCell)) + ":h=" + Math.Max(8, (int)Math.Round(s.GridCell)) + ":t=1:c=white@" + N(s.GridOpacity));
            double fadeIn = Math.Min(s.FadeIn, duration / 2), fadeOut = Math.Min(s.FadeOut, duration / 2);
            if (fadeIn > 0) vf.Add("fade=t=in:st=0:d=" + N(fadeIn));
            if (fadeOut > 0) vf.Add("fade=t=out:st=" + N(duration - fadeOut) + ":d=" + N(fadeOut));
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
                string timing = "asetpts=PTS-STARTPTS," + (voice ? "apad," : "") + "atrim=start=" + N(s.TrimStart) + ":end=" + N(end) + ",asetpts=PTS-STARTPTS," + string.Join(",", TempoFilters(p.Speed)) + ",aresample=async=1:first_pts=0,apad,atrim=duration=" + N(duration);
                graph.Add("[" + main + "]" + timing + "[speech]");
                if (background) { graph.Add("[speech][bg]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.95:level=false:latency=1[mix]"); source = "mix"; }
                else source = "speech";
            }
            if (audio) {
                graph.Add("[" + source + "]" + FinishAudio(s, duration) + "[processed_audio]");
                args.AddRange(new[] { "-filter_complex", string.Join(";", graph), "-map", "[processed_audio]", "-c:a", "aac", "-b:a", "160k" });
            }
            args.AddRange(new[] { "-vf", string.Join(",", vf), "-t", N(duration), "-c:v", "libx264", "-preset", "fast", "-pix_fmt", "yuv420p" });
            if (p.Compression == "quality") args.AddRange(new[] { "-crf", N(p.Crf) });
            else {
                double rate = p.Compression == "sourcebitrate" ? Math.Max(.05, m.VideoBitrate * p.BitratePercent / 100) : p.Bitrate;
                args.AddRange(new[] { "-b:v", N(rate) + "M", "-maxrate", N(rate * 1.5) + "M", "-bufsize", N(rate * 2) + "M" });
            }
            if (p.Metadata == "keep") args.AddRange(new[] { "-map_metadata", "0" });
            else args.AddRange(new[] { "-map_metadata", "-1", "-map_metadata:s:v", "-1", "-map_metadata:s:a", "-1", "-map_chapters", "-1" });
            if (s.HasCaptureDate || p.Metadata == "now" || p.Metadata == "custom") {
                DateTime date = s.HasCaptureDate ? s.CaptureDate : p.Metadata == "custom" ? p.CustomDate : DateTime.Now;
                string stamp = date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", Core.Inv);
                args.AddRange(new[] { "-metadata", "creation_time=" + stamp, "-metadata:s:v:0", "creation_time=" + stamp });
                if (audio) args.AddRange(new[] { "-metadata:s:a:0", "creation_time=" + stamp });
            }
            if (!string.IsNullOrEmpty(s.Brand)) args.AddRange(new[] { "-metadata", "make=" + s.Brand, "-metadata", "model=" + s.Model, "-metadata", "software=" + s.Software });
            args.AddRange(new[] { "-metadata:s:v:0", "rotate=0" });
            if (p.Format != "mkv") args.AddRange(new[] { "-movflags", string.IsNullOrEmpty(s.Brand) ? "+faststart" : "+faststart+use_metadata_tags" });
            args.Add(output); return args;
        }
        static double Fraction(string value) {
            var parts = value.Split('/'); return Core.Parse(parts[0]) / (parts.Length == 2 ? Core.Parse(parts[1]) : 1);
        }
    }
}
