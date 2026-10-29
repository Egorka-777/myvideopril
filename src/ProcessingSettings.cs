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
        public ProcessingParameter Rotation = new ProcessingParameter(0, .25);
        public ProcessingParameter Speed = new ProcessingParameter(98, 102);
        public ProcessingParameter Brightness = new ProcessingParameter(-10, 10);
        public ProcessingParameter Contrast = new ProcessingParameter(90, 110);
        public ProcessingParameter Sharpness = new ProcessingParameter(-10, 10);
        public ProcessingParameter Noise = new ProcessingParameter(1, 5);
        /// <summary>Мерцающие точки (кино/пыль), отдельно от равномерной зернистости.</summary>
        public ProcessingParameter FilmDust = new ProcessingParameter(2, 8);
        /// <summary>Масштаб кадра в рамке: &lt;100 — поля, &gt;100 — лёгкий зум.</summary>
        public ProcessingParameter FrameScale = new ProcessingParameter(97, 103);
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
        public string MetadataSoftware = "";
        public string[] Brands = (string[])ProcessingDevices.BrandNames.Clone();

        [XmlIgnore] public bool IsEnabled {
            get { return Parameters().Any(p => p.Enabled) || Mirror || DeviceMetadata || CaptureDateEnabled || !string.IsNullOrWhiteSpace(MetadataSoftware); }
        }
        public IEnumerable<ProcessingParameter> Parameters() {
            return new[] { Crop, Trim, Rotation, Speed, Brightness, Contrast, Sharpness, Noise, FilmDust, FrameScale,
                Volume, Pitch, Tempo, FadeIn, FadeOut, GridOpacity, Zoom, AudioModulation };
        }
        public void Validate() {
            if(Parameters().Any(p=>p==null))throw new Exception("Отсутствует параметр обработки.");
            Crop.Validate(0, 15, "Обрезка кадра, %"); Trim.Validate(0, 15, "Подрезка по времени, %");
            Rotation.Validate(-180, 180, "Поворот, °"); Speed.Validate(50, 200, "Скорость, %");
            Brightness.Validate(-50, 50, "Яркость, %"); Contrast.Validate(50, 150, "Контраст, %");
            Sharpness.Validate(-50, 50, "Резкость, %"); Noise.Validate(0, 100, "Шум, %");
            FilmDust.Validate(0, 30, "Пыль / точки, %"); FrameScale.Validate(94, 106, "Масштаб в рамке, %");
            Volume.Validate(0, 200, "Громкость, %"); Pitch.Validate(50, 200, "Высота тона, %");
            Tempo.Validate(50, 200, "Темп аудио, %"); FadeIn.Validate(0, 10, "Затемнение в начале, с");
            FadeOut.Validate(0, 10, "Затемнение в конце, с"); GridOpacity.Validate(0, 50, "Сетка, %");
            Zoom.Validate(100, 130, "Зум, %"); AudioModulation.Validate(0, 50, "Всплески громкости, %");
            if (GridCell == null || ZoomPeriod == null || ModulationFrequency == null) throw new Exception("Отсутствует диапазон эффекта.");
            GridCell.Validate(8, 512, "Размер ячейки сетки, px"); ZoomPeriod.Validate(.5, 30, "Период зума, с");
            ModulationFrequency.Validate(.1, 10, "Частота всплесков, Гц");
            if ((MetadataSoftware ?? "").Length>120 || (MetadataSoftware ?? "").Any(char.IsControl)) throw new Exception("Название программы: до 120 символов, без переносов строки.");
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
        public double TrimStart, TrimEnd, Rotation, Sharpness, Noise, FilmDust, FrameScale = 1, Volume = 1, Pitch = 1, Tempo = 1;
        public double FadeIn, FadeOut, GridOpacity, GridCell, Zoom = 1, ZoomPeriod, ZoomX, ZoomY;
        public double ModulationDepth, ModulationFrequency;
        public bool Mirror;
        public int NoiseSeed, FilmDustSeed;
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
        public static readonly string[] BrandNames = { "Apple", "Samsung", "Xiaomi", "Redmi", "POCO", "Google", "OnePlus", "OPPO", "vivo", "realme", "HONOR", "Huawei", "Motorola", "Sony", "ASUS" };
        // Literal user-supplied catalog. No OS versions or capture provenance are inferred.
        static readonly ProcessingDevice[] Templates = {
            new ProcessingDevice("Apple", "iPhone 17", ""),
            new ProcessingDevice("Apple", "iPhone 17 Pro", ""),
            new ProcessingDevice("Apple", "iPhone 17 Pro Max", ""),
            new ProcessingDevice("Apple", "iPhone Air", ""),
            new ProcessingDevice("Apple", "iPhone 16", ""),
            new ProcessingDevice("Apple", "iPhone 16 Plus", ""),
            new ProcessingDevice("Apple", "iPhone 16 Pro", ""),
            new ProcessingDevice("Apple", "iPhone 16 Pro Max", ""),
            new ProcessingDevice("Apple", "iPhone 16e", ""),
            new ProcessingDevice("Apple", "iPhone 15", ""),
            new ProcessingDevice("Apple", "iPhone 15 Pro", ""),
            new ProcessingDevice("Apple", "iPhone 15 Pro Max", ""),
            new ProcessingDevice("Samsung", "Galaxy S26", ""),
            new ProcessingDevice("Samsung", "Galaxy S26+", ""),
            new ProcessingDevice("Samsung", "Galaxy S26 Ultra", ""),
            new ProcessingDevice("Samsung", "Galaxy S25", ""),
            new ProcessingDevice("Samsung", "Galaxy S25+", ""),
            new ProcessingDevice("Samsung", "Galaxy S25 Ultra", ""),
            new ProcessingDevice("Samsung", "Galaxy S25 Edge", ""),
            new ProcessingDevice("Samsung", "Galaxy S25 FE", ""),
            new ProcessingDevice("Samsung", "Galaxy Z Fold7", ""),
            new ProcessingDevice("Samsung", "Galaxy Z Flip7", ""),
            new ProcessingDevice("Samsung", "Galaxy A57 5G", ""),
            new ProcessingDevice("Samsung", "Galaxy A56 5G", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 15", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 15 Ultra", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 15T", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 15T Pro", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 14", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 14 Ultra", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 14T", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 14T Pro", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 13", ""),
            new ProcessingDevice("Xiaomi", "Xiaomi 13 Pro", ""),
            new ProcessingDevice("Redmi", "Redmi Note 14", ""),
            new ProcessingDevice("Redmi", "Redmi Note 14 5G", ""),
            new ProcessingDevice("Redmi", "Redmi Note 14 Pro", ""),
            new ProcessingDevice("Redmi", "Redmi Note 14 Pro 5G", ""),
            new ProcessingDevice("Redmi", "Redmi Note 14 Pro+ 5G", ""),
            new ProcessingDevice("Redmi", "Redmi Note 14S", ""),
            new ProcessingDevice("Redmi", "Redmi Note 13 Pro", ""),
            new ProcessingDevice("Redmi", "Redmi Note 13 Pro 5G", ""),
            new ProcessingDevice("Redmi", "Redmi Note 13 Pro+ 5G", ""),
            new ProcessingDevice("Redmi", "Redmi Note 13 5G", ""),
            new ProcessingDevice("POCO", "POCO F7", ""),
            new ProcessingDevice("POCO", "POCO F7 Pro", ""),
            new ProcessingDevice("POCO", "POCO F7 Ultra", ""),
            new ProcessingDevice("POCO", "POCO X7", ""),
            new ProcessingDevice("POCO", "POCO X7 Pro", ""),
            new ProcessingDevice("POCO", "POCO F6", ""),
            new ProcessingDevice("POCO", "POCO F6 Pro", ""),
            new ProcessingDevice("POCO", "POCO X6 Pro", ""),
            new ProcessingDevice("POCO", "POCO F5", ""),
            new ProcessingDevice("POCO", "POCO F5 Pro", ""),
            new ProcessingDevice("Google", "Pixel 10", ""),
            new ProcessingDevice("Google", "Pixel 10 Pro", ""),
            new ProcessingDevice("Google", "Pixel 10 Pro XL", ""),
            new ProcessingDevice("Google", "Pixel 10 Pro Fold", ""),
            new ProcessingDevice("Google", "Pixel 10a", ""),
            new ProcessingDevice("Google", "Pixel 9", ""),
            new ProcessingDevice("Google", "Pixel 9 Pro", ""),
            new ProcessingDevice("Google", "Pixel 9 Pro XL", ""),
            new ProcessingDevice("Google", "Pixel 9 Pro Fold", ""),
            new ProcessingDevice("Google", "Pixel 9a", ""),
            new ProcessingDevice("OnePlus", "OnePlus 15", ""),
            new ProcessingDevice("OnePlus", "OnePlus 15R", ""),
            new ProcessingDevice("OnePlus", "OnePlus 13", ""),
            new ProcessingDevice("OnePlus", "OnePlus 13R", ""),
            new ProcessingDevice("OnePlus", "OnePlus 12", ""),
            new ProcessingDevice("OnePlus", "OnePlus 12R", ""),
            new ProcessingDevice("OnePlus", "OnePlus Open", ""),
            new ProcessingDevice("OnePlus", "OnePlus Nord 5", ""),
            new ProcessingDevice("OnePlus", "OnePlus Nord CE5", ""),
            new ProcessingDevice("OnePlus", "OnePlus Nord 4", ""),
            new ProcessingDevice("OPPO", "Find X8", ""),
            new ProcessingDevice("OPPO", "Find X8 Pro", ""),
            new ProcessingDevice("OPPO", "Reno14 5G", ""),
            new ProcessingDevice("OPPO", "Reno14 Pro 5G", ""),
            new ProcessingDevice("OPPO", "Reno13 5G", ""),
            new ProcessingDevice("OPPO", "Reno13 Pro 5G", ""),
            new ProcessingDevice("OPPO", "Reno13 F 5G", ""),
            new ProcessingDevice("OPPO", "Reno13 FS 5G", ""),
            new ProcessingDevice("OPPO", "Reno16 5G", ""),
            new ProcessingDevice("OPPO", "Reno16 Pro 5G", ""),
            new ProcessingDevice("vivo", "X300 Ultra", ""),
            new ProcessingDevice("vivo", "X300 Pro", ""),
            new ProcessingDevice("vivo", "X300", ""),
            new ProcessingDevice("vivo", "X300 FE", ""),
            new ProcessingDevice("vivo", "X200 Pro", ""),
            new ProcessingDevice("vivo", "X200 FE", ""),
            new ProcessingDevice("vivo", "V60 5G", ""),
            new ProcessingDevice("vivo", "V50", ""),
            new ProcessingDevice("vivo", "V60 Lite", ""),
            new ProcessingDevice("vivo", "V50 Lite", ""),
            new ProcessingDevice("realme", "realme GT 7", ""),
            new ProcessingDevice("realme", "realme GT 7 Pro", ""),
            new ProcessingDevice("realme", "realme GT 7T", ""),
            new ProcessingDevice("realme", "realme GT 6", ""),
            new ProcessingDevice("realme", "realme GT 6T", ""),
            new ProcessingDevice("realme", "realme 14 Pro 5G", ""),
            new ProcessingDevice("realme", "realme 14 Pro+ 5G", ""),
            new ProcessingDevice("realme", "realme 14 5G", ""),
            new ProcessingDevice("realme", "realme 14T 5G", ""),
            new ProcessingDevice("realme", "realme 14x 5G", ""),
            new ProcessingDevice("HONOR", "HONOR Magic8 Pro", ""),
            new ProcessingDevice("HONOR", "HONOR Magic8 Lite", ""),
            new ProcessingDevice("HONOR", "HONOR Magic7 Pro", ""),
            new ProcessingDevice("HONOR", "HONOR Magic6 Pro", ""),
            new ProcessingDevice("HONOR", "HONOR Magic V5", ""),
            new ProcessingDevice("HONOR", "HONOR Magic V3", ""),
            new ProcessingDevice("HONOR", "HONOR 400", ""),
            new ProcessingDevice("HONOR", "HONOR 400 Lite", ""),
            new ProcessingDevice("HONOR", "HONOR 200", ""),
            new ProcessingDevice("HONOR", "HONOR 200 Pro", ""),
            new ProcessingDevice("Huawei", "HUAWEI Pura 80", ""),
            new ProcessingDevice("Huawei", "HUAWEI Pura 80 Pro", ""),
            new ProcessingDevice("Huawei", "HUAWEI Pura 80 Ultra", ""),
            new ProcessingDevice("Huawei", "HUAWEI Pura 70", ""),
            new ProcessingDevice("Huawei", "HUAWEI Pura 70 Pro", ""),
            new ProcessingDevice("Huawei", "HUAWEI Pura 70 Ultra", ""),
            new ProcessingDevice("Huawei", "HUAWEI Mate X6", ""),
            new ProcessingDevice("Huawei", "HUAWEI Mate X7", ""),
            new ProcessingDevice("Huawei", "HUAWEI Mate 80 Pro", ""),
            new ProcessingDevice("Huawei", "HUAWEI P60 Pro", ""),
            new ProcessingDevice("Motorola", "Motorola Edge 60", ""),
            new ProcessingDevice("Motorola", "Edge 60 Pro", ""),
            new ProcessingDevice("Motorola", "Edge 60 Fusion", ""),
            new ProcessingDevice("Motorola", "Edge 60 Neo", ""),
            new ProcessingDevice("Motorola", "Edge 50 Ultra", ""),
            new ProcessingDevice("Motorola", "Edge 50 Pro", ""),
            new ProcessingDevice("Motorola", "Edge 50 Neo", ""),
            new ProcessingDevice("Motorola", "Edge 50 Fusion", ""),
            new ProcessingDevice("Motorola", "Razr 60", ""),
            new ProcessingDevice("Motorola", "Razr 60 Ultra", ""),
            new ProcessingDevice("Sony", "Xperia 1 VII", ""),
            new ProcessingDevice("Sony", "Xperia 10 VII", ""),
            new ProcessingDevice("Sony", "Xperia 1 VI", ""),
            new ProcessingDevice("Sony", "Xperia 10 VI", ""),
            new ProcessingDevice("Sony", "Xperia 1 V", ""),
            new ProcessingDevice("Sony", "Xperia 5 V", ""),
            new ProcessingDevice("Sony", "Xperia 10 V", ""),
            new ProcessingDevice("ASUS", "Zenfone 12 Ultra", ""),
            new ProcessingDevice("ASUS", "Zenfone 11 Ultra", ""),
            new ProcessingDevice("ASUS", "ROG Phone 9", ""),
            new ProcessingDevice("ASUS", "ROG Phone 9 Pro", ""),
            new ProcessingDevice("ASUS", "ROG Phone 9 FE", ""),
            new ProcessingDevice("ASUS", "ROG Phone 8", ""),
            new ProcessingDevice("ASUS", "ROG Phone 8 Pro", "")
        };
        public static int ModelCount { get { return Templates.Length; } }
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
