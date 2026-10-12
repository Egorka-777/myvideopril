using System;

namespace VideoBatch {
    /// <summary>Per-video metadata for assembly output only; independent from the Processing tab.</summary>
    public class AssemblyOutputMetadata {
        public bool DeviceMetadata = true;
        public bool CaptureDate = true;
        public bool RandomizeCaptureTime = true;
        public int CaptureDaysBack = 30;
        public string[] Brands;
        public string Software = "";

        public static AssemblyOutputMetadata Default() {
            return new AssemblyOutputMetadata {
                DeviceMetadata = true,
                CaptureDate = true,
                RandomizeCaptureTime = true,
                CaptureDaysBack = 30,
                Brands = (string[])ProcessingDevices.BrandNames.Clone()
            };
        }

        public ProcessingSample Sample(Random random, DateTime now) {
            var settings = Effective();
            var profile = new Profile { Metadata = "clear", FileDate = "now" };
            ProcessingEngine.Apply(profile, settings, new Media { Duration = 1, Width = 1, Height = 1, HasAudio = true, VideoIndex = 0, AudioIndex = 1 }, random, now);
            return profile.Processing;
        }

        ProcessingSettings Effective() {
            var brands = Brands;
            if (brands == null || brands.Length == 0) brands = Default().Brands;
            return new ProcessingSettings {
                DeviceMetadata = DeviceMetadata,
                CaptureDateEnabled = CaptureDate,
                RandomizeCaptureTime = RandomizeCaptureTime,
                CaptureDaysBack = CaptureDaysBack,
                MetadataSoftware = Software,
                Brands = brands
            };
        }
    }
}
