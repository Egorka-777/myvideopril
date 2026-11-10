using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class AssemblyProcessing {
        public static ProcessingSettings Defaults() {
            var settings = new ProcessingSettings();
            settings.EnableMildDefaults();
            settings.Trim.Enabled = settings.Rotation.Enabled = false;
            return settings;
        }
        public static ProcessingSettings Effective(ProcessingSettings source) {
            var settings = Store.Clone(source ?? Defaults());
            settings.Trim.Enabled = settings.Rotation.Enabled = false;
            // Output tags are applied once, after image/audio processing.
            settings.DeviceMetadata = settings.CaptureDateEnabled = false;
            settings.MetadataSoftware = "";
            settings.Validate();
            return settings;
        }
        public static async Task Apply(AssemblyTemplate template, string ffmpeg, string probe, string input, Random random, CancellationToken ct) {
            var settings = Effective(template.OutputProcessing);
            if (!settings.IsEnabled) return;
            var media = Core.VideoInfo(await Core.Probe(probe, input, ct).ConfigureAwait(false));
            media.Duration = media.VideoDuration;
            var profile = new Profile { Short = false, Resolution = "source", Fps = "source", Metadata = "clear", Format = "mp4" };
            ProcessingEngine.Apply(profile, settings, media, random, DateTime.Now);
            string output = Path.Combine(Path.GetDirectoryName(input), "processed-" + Guid.NewGuid().ToString("N") + ".mp4");
            try {
                await Core.Tool(ffmpeg, ProcessingEngine.Arguments(input, output, profile, media, null), ct, null, 1800).ConfigureAwait(false);
                var result = Core.VideoInfo(await Core.Probe(probe, output, ct).ConfigureAwait(false));
                if (result.Width != media.Width || result.Height != media.Height || Math.Abs(result.VideoDuration - ProcessingEngine.OutputDuration(profile, media)) > .2 || media.HasAudio && !result.HasAudio)
                    throw new Exception("Обработка сборки: результат не прошёл проверку. Размер " + result.Width + "×" + result.Height + " (ожидался " + media.Width + "×" + media.Height + "), длительность " + Core.N(result.VideoDuration) + " с (ожидалась " + Core.N(ProcessingEngine.OutputDuration(profile, media)) + " с), звук " + result.HasAudio + " (исходный " + media.HasAudio + ").");
                ct.ThrowIfCancellationRequested();
                File.Delete(input); File.Move(output, input);
            } finally { if (File.Exists(output)) File.Delete(output); }
        }
    }
}
