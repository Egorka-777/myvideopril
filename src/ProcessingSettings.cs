using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace VideoBatch {
    public class ProcessingParameter {
        public bool Enabled;
        public Range Values = new Range(0, 0);
        public ProcessingParameter() { }
        public ProcessingParameter(double min, double max) { Values = new Range(min, max); }
        public double Sample(Random random, double fallback) { return Enabled ? Values.Sample(random) : fallback; }
        public void Validate(double min, double max, string name) {
            if (Values == null) throw new Exception(name + ": отсутствует диапазон.");
            Values.Validate(min, max, name);
        }
    }

    // Opt-in additions: absence in an old settings.xml leaves the existing pipeline intact.
    public class ProcessingSettings {
        public ProcessingParameter Crop = new ProcessingParameter(.3, 1.5);
        public ProcessingParameter Trim = new ProcessingParameter(1, 2);
        public ProcessingParameter Rotation = new ProcessingParameter(0, 2);
        public ProcessingParameter Speed = new ProcessingParameter(98, 102);
        public ProcessingParameter Brightness = new ProcessingParameter(-10, 10);
        public ProcessingParameter Contrast = new ProcessingParameter(90, 110);
        public ProcessingParameter Sharpness = new ProcessingParameter(-10, 10);
        public ProcessingParameter Noise = new ProcessingParameter(1, 5);
        public ProcessingParameter Volume = new ProcessingParameter(85, 110);
        public ProcessingParameter Pitch = new ProcessingParameter(98, 102);
        public ProcessingParameter Tempo = new ProcessingParameter(98, 102);
        public ProcessingParameter FadeIn = new ProcessingParameter(.3, .8);
        public ProcessingParameter FadeOut = new ProcessingParameter(.3, .8);
        public bool Mirror;
        public ProcessingParameter GridOpacity = new ProcessingParameter(3, 8);
        public Range GridCell = new Range(80, 160);
        public ProcessingParameter Zoom = new ProcessingParameter(100, 104);
        public Range ZoomPeriod = new Range(2, 5);
        public ProcessingParameter AudioModulation = new ProcessingParameter(5, 15);
        public Range ModulationFrequency = new Range(.5, 2);
        public bool DeviceMetadata, CaptureDateEnabled, RandomizeCaptureTime = true;
        public int CaptureDaysBack = 30;
        public string[] Brands = new string[] { "Apple", "Samsung", "Xiaomi", "Google", "Huawei", "OnePlus" };

        [XmlIgnore] public bool IsEnabled {
            get { return Parameters().Any(p => p.Enabled) || Mirror || DeviceMetadata || CaptureDateEnabled; }
        }
        public IEnumerable<ProcessingParameter> Parameters() {
            return new[] { Crop, Trim, Rotation, Speed, Brightness, Contrast, Sharpness, Noise, Volume, Pitch,
                Tempo, FadeIn, FadeOut, GridOpacity, Zoom, AudioModulation };
        }
        public void Validate() {
            if(Parameters().Any(p=>p==null))throw new Exception("Отсутствует параметр обработки.");
            Crop.Validate(0, 15, "Обрезка кадра, %"); Trim.Validate(0, 15, "Подрезка по времени, %");
            Rotation.Validate(-180, 180, "Поворот, °"); Speed.Validate(50, 200, "Скорость, %");
            Brightness.Validate(-50, 50, "Яркость, %"); Contrast.Validate(50, 150, "Контраст, %");
            Sharpness.Validate(-50, 50, "Резкость, %"); Noise.Validate(0, 100, "Шум, %");
            Volume.Validate(0, 200, "Громкость, %"); Pitch.Validate(50, 200, "Высота тона, %");
            Tempo.Validate(50, 200, "Темп аудио, %"); FadeIn.Validate(0, 10, "Затемнение в начале, с");
            FadeOut.Validate(0, 10, "Затемнение в конце, с"); GridOpacity.Validate(0, 50, "Сетка, %");
            Zoom.Validate(100, 130, "Зум, %"); AudioModulation.Validate(0, 50, "Всплески громкости, %");
            if (GridCell == null || ZoomPeriod == null || ModulationFrequency == null) throw new Exception("Отсутствует диапазон эффекта.");
            GridCell.Validate(8, 512, "Размер ячейки сетки, px"); ZoomPeriod.Validate(.5, 30, "Период зума, с");
            ModulationFrequency.Validate(.1, 10, "Частота всплесков, Гц");
            Profile.Check(CaptureDaysBack, 0, 3650, "Глубина даты, дней");
            if (Brands == null || Brands.Any(b => !ProcessingDevices.BrandNames.Contains(b))) throw new Exception("Неизвестный бренд метаданных.");
            if (DeviceMetadata && Brands.Length == 0) throw new Exception("Выберите хотя бы один бренд метаданных.");
        }
        public void RandomizeEnabledRanges(Random random) {
            Validate();
            var mild = new ProcessingSettings();
            var defaults = mild.Parameters().ToArray(); var current = Parameters().ToArray();
            for (int i = 0; i < current.Length; i++) if (current[i].Enabled) {
                double a = defaults[i].Values.Sample(random), b = defaults[i].Values.Sample(random);
                current[i].Values = new Range(Math.Min(a, b), Math.Max(a, b));
            }
        }
        public void EnableMildDefaults() {
            foreach (var p in Parameters()) p.Enabled = true;
            Mirror = false;
            DeviceMetadata = true;
            CaptureDateEnabled = true;
            RandomizeCaptureTime = true;
            CaptureDaysBack = 30;
            if (Brands == null || Brands.Length == 0) Brands = (string[])ProcessingDevices.BrandNames.Clone();
        }
        public static ProcessingSettings MildDefaults() {
            var s = new ProcessingSettings();
            s.EnableMildDefaults();
            s.Validate();
            return s;
        }
    }

    public class ProcessingSample {
        public double TrimStart, TrimEnd, Rotation, Sharpness, Noise, Volume = 1, Pitch = 1, Tempo = 1;
        public double FadeIn, FadeOut, GridOpacity, GridCell, Zoom = 1, ZoomPeriod, ZoomX, ZoomY;
        public double ModulationDepth, ModulationFrequency;
        public bool Mirror;
        public int NoiseSeed;
        public string Brand = "", Model = "", Software = "";
        public bool HasCaptureDate;
        public DateTime CaptureDate;
    }

    public class ProcessingDevice {
        public string Brand, Model, Software;
        public ProcessingDevice() { }
        public ProcessingDevice(string brand, string model, string software) { Brand = brand; Model = model; Software = software; }
    }
    public static class ProcessingDevices {
        // User-selected metadata templates; these values do not attest to the capture hardware.
        public static readonly string[] BrandNames = { "Apple", "Samsung", "Xiaomi", "Google", "Huawei", "OnePlus" };
        static readonly ProcessingDevice[] Templates = {
            new ProcessingDevice("Apple", "iPhone 14", "iOS 17.0"), new ProcessingDevice("Apple", "iPhone 15 Pro", "iOS 17.4"),
            new ProcessingDevice("Samsung", "Galaxy S23", "One UI 6.0"), new ProcessingDevice("Samsung", "Galaxy S24", "One UI 6.1"),
            new ProcessingDevice("Xiaomi", "13 Pro", "HyperOS 1.0"), new ProcessingDevice("Xiaomi", "14", "HyperOS 1.0"),
            new ProcessingDevice("Google", "Pixel 7", "Android 14"), new ProcessingDevice("Google", "Pixel 8", "Android 14"),
            new ProcessingDevice("Huawei", "P60 Pro", "HarmonyOS 4.0"), new ProcessingDevice("Huawei", "Mate 60 Pro", "HarmonyOS 4.0"),
            new ProcessingDevice("OnePlus", "11", "OxygenOS 14"), new ProcessingDevice("OnePlus", "12", "OxygenOS 14")
        };
        public static ProcessingDevice Sample(IList<string> brands, Random random) {
            var chosen = brands.Distinct().ToArray(); string brand = chosen[random.Next(chosen.Length)];
            var models = Templates.Where(t => t.Brand == brand).ToArray(); var t = models[random.Next(models.Length)];
            return new ProcessingDevice(t.Brand, t.Model, t.Software);
        }
    }

    public class ProcessingPreset {
        public int Version = 1;
        public ProcessingSettings Settings = new ProcessingSettings();
        public static void Save(string path, ProcessingSettings settings) {
            settings.Validate();
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = File.Create(temp)) new XmlSerializer(typeof(ProcessingPreset)).Serialize(stream, new ProcessingPreset { Settings = settings });
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static ProcessingSettings Load(string path) {
            if (new FileInfo(path).Length > 1048576) throw new Exception("Слишком большой файл пресета.");
            var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            ProcessingPreset preset;
            using (var reader = XmlReader.Create(path, options)) preset = (ProcessingPreset)new XmlSerializer(typeof(ProcessingPreset)).Deserialize(reader);
            if (preset.Version != 1 || preset.Settings == null) throw new Exception("Неподдерживаемый пресет.");
            preset.Settings.Validate(); return preset.Settings;
        }
    }
}
