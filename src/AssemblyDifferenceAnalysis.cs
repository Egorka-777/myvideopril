using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public sealed class AssemblyDifferenceReport {
        public int Version = 2;
        public List<AssemblyDifferenceCriterion> Criteria = new List<AssemblyDifferenceCriterion>();
        public string Output = "", Status = "incomplete", Summary = "", Warnings = "", Methodology = "", Error = "";
        public DateTime CreatedUtc = DateTime.UtcNow;
        public bool Completed;
        public double? DifferencePercent;
        public AssemblyDifferenceMedia OutputMedia;
        public List<AssemblyDifferenceComparison> Comparisons = new List<AssemblyDifferenceComparison>();
    }
    public sealed class AssemblyDifferenceCriterion {
        public string Key = "", Name = "", Details = "";
        public double Weight;
        public double? RawDifferencePercent, EarnedPoints;
    }
    public sealed class AssemblyDifferenceValue {
        public string Key = "", Value = "";
    }
    public sealed class AssemblyDifferenceMedia {
        public List<AssemblyDifferenceValue> TechnicalValues = new List<AssemblyDifferenceValue>(), MetadataValues = new List<AssemblyDifferenceValue>();
        public string File = "", Parameters = "", Metadata = "";
        public long Bytes;
        public double Duration;
        public bool HasVideo, HasAudio;
        public int VisualSamples, AudioSamples;
    }
    public sealed class AssemblyDifferenceComparison {
        public string File = "", Category = "", Status = "incomplete", Error = "", Details = "", MetadataDetails = "";
        public bool Completed;
        public double? VisualSimilarity, AudioSimilarity, DifferencePercent;
        public double Duration, DurationDelta;
        public AssemblyDifferenceMedia Media;
        public List<AssemblyDifferenceFragment> Fragments = new List<AssemblyDifferenceFragment>();
        public List<AssemblyDifferenceCriterion> Criteria = new List<AssemblyDifferenceCriterion>();
    }
    public sealed class AssemblyDifferenceFragment {
        public string Kind = "";
        public double OutputStart, OutputEnd, ReferenceStart, ReferenceEnd, Similarity;
    }

    /// <summary>Local sampled content comparison, not a platform uniqueness or copyright detector. All percentages are 0..100.</summary>
    public static class AssemblyDifferenceAnalysis {
        const double VideoStep = .5, AudioStep = .25;
        const int FrameSide = 32, DescriptorSide = 16, AudioRate = 8000, FftSize = 2048, Bands = 64;
        const string Method = "Локальная эвристика, не уникальность YouTube/TikTok и не гарантия отсутствия совпадений. " +
            "Видео: 2 серых кадра/с, дескриптор 16×16, варианты полного кадра и центральной обрезки по 6,25% с каждого края; " +
            "сходство = max(0, 1 − средняя абсолютная ошибка яркости / 64). Для каждого кадра результата ищется ближайший кадр " +
            "во всём файле сравнения независимо от времени (учитываются сдвиги, перестановки, повторы). " +
            "Аудио: моно 8 кГц, окно Hann 2048 отсчётов каждые 0,25 с, FFT, 64 логарифмические полосы 40–4000 Гц, " +
            "квадрат косинусного сходства нормированных спектров; выбирается ближайшее окно. " +
            "По каждой дорожке берётся среднее сходство выборок результата. Индекс = сумма баллов критериев: " +
            "видео 50, аудио 30, длительность 10, технические параметры 7, метаданные 3. " +
            "Баллы = вес × сырое отличие / 100; недоступные критерии не получают баллов, веса не перераспределяются. " +
            "Это выбранные локальные эвристические веса, не веса алгоритмов платформ. " +
            "Длительность: абсолютная разница / большая длительность. Техническое отличие — среднее по доступным " +
            "разрешению, fps, кодекам, битрейтам и расширению контейнера; числа сравниваются относительной разницей, строки — 0/100. " +
            "Метаданные: доля отличающихся ключей тегов контейнера/дорожек (название, даты, программа, устройство и остальные теги), " +
            "плюс дата изменения файла UTC. Дата создания файловой копии показана отдельно, но не оценивается. " +
            "Отсутствие значения с одной стороны считается отличием, с обеих — недоступно. " +
            "Для длительности, технических параметров и метаданных берётся минимальное отличие от одного из файлов сравнения " +
            "(отдельно для каждого критерия), а не среднее по несвязанным файлам. " +
            "Общий индекс использует лучшие совпадения каждой выборки среди ВСЕХ переданных исходников и прошлых результатов, " +
            "поэтому монтаж из нескольких известных файлов не становится новым только из-за перестановки. " +
            "Если дорожка есть только с одной стороны, её сходство 0; если отсутствует с обеих — не участвует. " +
            "При ошибке любого сравнения общий индекс не выдаётся. Фрагменты — последовательности минимум двух выборок " +
            "со сходством ≥82%, с приблизительным согласованием времени; это кандидаты, не доказательство копирования.";
        const string Limitations = "Выборочный анализ: короткие вставки между выборками могут быть пропущены. " +
            "Серые миниатюры не измеряют цвет и теряют мелкий текст; похожие простые/статичные сцены могут совпадать случайно. " +
            "Спектр не распознаёт речь/музыкальное произведение: похожие тембры, шум и тишина могут завышать сходство; " +
            "изменение высоты тона, скорости, сильный кроп, наложения и смешивание дорожек могут занижать его. " +
            "Ближайшие выборки не проверяют полную временную структуру; длины фрагментов приблизительные. " +
            "Индекс описывает только эту локальную выборку и набор сравнения; это не вероятность, не юридическая оценка " +
            "и не оценка алгоритмов платформ.";
        sealed class Fingerprint {
            public AssemblyDifferenceMedia Media;
            public List<float[]> Video = new List<float[]>(), Cropped = new List<float[]>(), Audio = new List<float[]>();
        }
        sealed class Matches {
            public double[] Scores;
            public int[] Indices;
            public Matches(int count) { Scores = new double[count]; Indices = Enumerable.Repeat(-1, count).ToArray(); }
        }

        public static async Task<AssemblyDifferenceReport> AnalyzeSaved(string ffmpeg, string probe, string output, CancellationToken ct, Action<string> progress = null) {
            string recipe = AssemblyOutputFiles.FindRecipe(output);
            if (!File.Exists(recipe)) throw new Exception("Не найден рецепт сборки этого ролика. Для анализа выберите видео, созданное в «Моей студии».");
            var plan = AssemblyFiles.Load<AssemblyPlan>(recipe);
            var sources = plan.Scenes.SelectMany(s => s.VideoSegments != null && s.VideoSegments.Count > 0
                    ? s.VideoSegments.Select(v => v.Video.Path) : new[] { s.Video.Path })
                .Concat(plan.SecondaryVideo == null ? Enumerable.Empty<string>() : new[] { plan.SecondaryVideo.Path })
                .Concat(new[] { plan.Voiceover, plan.Music, plan.Background?.Path })
                .Concat(plan.Scenes.SelectMany(s => new[] { s.Voiceover, s.Music }))
                .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
            string folder = Path.GetDirectoryName(Path.GetFullPath(output));
            var previous = Directory.EnumerateFiles(folder, "*.mp4").Where(p => !string.Equals(Path.GetFullPath(p), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase));
            var report = await Analyze(ffmpeg, probe, output, sources, previous, Path.Combine(folder, ".videobatch-reports"), ct, progress).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            AssemblyFiles.Save(AssemblyOutputFiles.Report(output, ".difference.xml"), report);
            return report;
        }
        public static async Task<AssemblyDifferenceReport> Analyze(string ffmpeg, string probe, string output,
            IEnumerable<string> sources, IEnumerable<string> previous, string work, CancellationToken ct, Action<string> progress = null) {
            var report = new AssemblyDifferenceReport { Output = output, Methodology = Method, Warnings = Limitations };
            string temporary = null;
            try {
                ct.ThrowIfCancellationRequested();
                temporary = Path.Combine(work, "difference-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                if (progress != null) progress("Анализ отличия: читаю готовый файл…");
                var result = await Extract(ffmpeg, probe, output, temporary, ct).ConfigureAwait(false);
                report.OutputMedia = result.Media;
                var requested = new List<AssemblyDifferenceComparison>();
                AddFiles(requested, sources, "source"); AddFiles(requested, previous, "previous");
                report.Comparisons = requested;
                var cache = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
                var videoBest = new double[result.Video.Count]; var audioBest = new double[result.Audio.Count];
                bool anyVideo = result.Media.HasVideo, anyAudio = result.Media.HasAudio;
                for (int n = 0; n < requested.Count; n++) {
                    ct.ThrowIfCancellationRequested(); var comparison = requested[n];
                    if (progress != null) progress("Анализ отличия: " + (n + 1) + "/" + requested.Count + " · " + Path.GetFileName(comparison.File));
                    try {
                        string key = Path.GetFullPath(comparison.File);
                        Fingerprint reference;
                        if (!cache.TryGetValue(key, out reference)) {
                            reference = string.Equals(key, Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase) ? result :
                                await Extract(ffmpeg, probe, comparison.File, temporary, ct).ConfigureAwait(false);
                            cache.Add(key, reference);
                        }
                        comparison.Media = reference.Media; comparison.Duration = reference.Media.Duration;
                        comparison.DurationDelta = result.Media.Duration - reference.Media.Duration;
                        comparison.MetadataDetails = "Результат:\r\n" + result.Media.Metadata + "\r\nСравниваемый файл:\r\n" + reference.Media.Metadata;
                        anyVideo |= reference.Media.HasVideo; anyAudio |= reference.Media.HasAudio;
                        var visual = VisualMatches(result, reference, ct);
                        var audio = AudioMatches(result, reference, ct);
                        comparison.VisualSimilarity = Similarity(result.Media.HasVideo, reference.Media.HasVideo, visual.Scores);
                        comparison.AudioSimilarity = Similarity(result.Media.HasAudio, reference.Media.HasAudio, audio.Scores);
                        comparison.Criteria = Criteria(comparison.VisualSimilarity, comparison.AudioSimilarity,
                            100 * Math.Abs(result.Media.Duration - reference.Media.Duration) / Math.Max(result.Media.Duration, reference.Media.Duration),
                            ValueDifference(result.Media.TechnicalValues, reference.Media.TechnicalValues, true),
                            ValueDifference(result.Media.MetadataValues, reference.Media.MetadataValues, false));
                        comparison.DifferencePercent = Points(comparison.Criteria);
                        Merge(videoBest, visual.Scores); Merge(audioBest, audio.Scores);
                        comparison.Fragments.AddRange(Fragments(visual, VideoStep, result.Media.Duration, reference.Media.Duration, "video"));
                        comparison.Fragments.AddRange(Fragments(audio, AudioStep, result.Media.Duration, reference.Media.Duration, "audio"));
                        comparison.Details = "Видео: " + TrackState(result.Media.HasVideo, reference.Media.HasVideo) +
                            "; аудио: " + TrackState(result.Media.HasAudio, reference.Media.HasAudio) + ".\r\nРезультат: " + result.Media.Parameters +
                            "\r\nСравниваемый файл: " + reference.Media.Parameters + ".\r\nВыборок видео/аудио: " +
                            result.Video.Count + "/" + result.Audio.Count + " против " + reference.Video.Count + "/" + reference.Audio.Count + ".";
                        comparison.Completed = true; comparison.Status = "completed";
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception e) { comparison.Status = "failed"; comparison.Error = e.Message; }
                }
                if (requested.Count == 0) {
                    report.Status = "no-comparisons"; report.Summary = "Нет исходников или прошлых результатов для сравнения; индекс не рассчитан.";
                } else if (requested.Any(x => !x.Completed)) {
                    report.Status = "incomplete"; report.Summary = "Анализ неполный: ошибок сравнения " + requested.Count(x => !x.Completed) + ". Общий индекс не рассчитан.";
                } else {
                    report.Criteria = Criteria(anyVideo ? (double?)Mean(videoBest) : null, anyAudio ? (double?)Mean(audioBest) : null,
                        Nearest(requested, "duration"), Nearest(requested, "technical"), Nearest(requested, "metadata"));
                    foreach (var criterion in report.Criteria.Where(x => x.Key != "visual" && x.Key != "audio")) {
                        var nearest = requested.Where(x => x.Criteria.Any(c => c.Key == criterion.Key && c.RawDifferencePercent.HasValue))
                            .OrderBy(x => x.Criteria.First(c => c.Key == criterion.Key).RawDifferencePercent).FirstOrDefault();
                        if (nearest != null) criterion.Details += " Ближайший файл: " + nearest.File;
                    }
                    report.DifferencePercent = Points(report.Criteria);
                    report.Completed = true; report.Status = "completed";
                    report.Summary = "Локальный индекс отличия: " + Percent(report.DifferencePercent) + ". Сравнено файлов: " + requested.Count + ". Это не процент уникальности на платформах.";
                }
            } catch (OperationCanceledException) {
                report.Status = "cancelled"; report.Summary = "Анализ остановлен; общий индекс не рассчитан."; report.DifferencePercent = null;
            } catch (Exception e) {
                report.Status = "failed"; report.Error = e.Message; report.Summary = "Анализ не завершён; общий индекс не рассчитан."; report.DifferencePercent = null;
            } finally {
                if (temporary != null && Directory.Exists(temporary)) {
                    try { Directory.Delete(temporary, true); }
                    catch (Exception e) { report.Warnings += "\r\nНе удалось удалить временные файлы «" + temporary + "»: " + e.Message; }
                }
            }
            return report;
        }
        static void AddFiles(List<AssemblyDifferenceComparison> target, IEnumerable<string> paths, string category) {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths ?? Enumerable.Empty<string>()) {
                // Empty/invalid entries remain visible errors rather than silently disappearing.
                string value = path ?? "";
                if (seen.Add(value)) target.Add(new AssemblyDifferenceComparison { File = value, Category = category });
            }
        }
        static async Task<Fingerprint> Extract(string ffmpeg, string probe, string path, string work, CancellationToken ct) {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("Не указан файл сравнения.");
            var info = new FileInfo(path); if (!info.Exists) throw new FileNotFoundException("Файл не найден: " + path);
            var raw = await Core.Probe(probe, path, ct).ConfigureAwait(false);
            var streams = raw.streams ?? new ProbeStream[0];
            var video = streams.FirstOrDefault(x => x.codec_type == "video" && (x.disposition == null || x.disposition.attached_pic != 1));
            var audio = streams.FirstOrDefault(x => x.codec_type == "audio");
            double duration = raw.format == null ? 0 : Core.Parse(raw.format.duration);
            if (duration <= 0) duration = streams.Select(x => Core.Parse(x.duration)).DefaultIfEmpty().Max();
            if (duration <= 0 || (video == null && audio == null)) throw new IOException("Не удалось определить длительность или медиадорожки: " + path);
            var result = new Fingerprint { Media = new AssemblyDifferenceMedia {
                File = path, Bytes = info.Length, Duration = duration, HasVideo = video != null, HasAudio = audio != null,
                Parameters = "расширение " + info.Extension + "; " + Core.N(duration) + " с; " + info.Length + " байт; битрейт " + (raw.format == null ? "?" : raw.format.bit_rate) +
                    "; " + string.Join("; ", streams.Select(x => "#" + x.index + " " + x.codec_type + " " + x.codec_name +
                        (x.codec_type == "video" ? " " + x.width + "×" + x.height + " fps=" + x.avg_frame_rate + " SAR=" + x.sample_aspect_ratio + " transfer=" + x.color_transfer : "") + " bitrate=" + x.bit_rate)),
                Metadata = "Дата создания файла UTC: " + info.CreationTimeUtc.ToString("o", CultureInfo.InvariantCulture) +
                    "\r\nДата изменения UTC: " + info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture) +
                    "\r\nКонтейнер: " + Tags(raw.format == null ? null : raw.format.tags) +
                    string.Concat(streams.Select(x => "\r\nДорожка #" + x.index + ": " + Tags(x.tags) +
                        "; rotation=" + string.Join(",", (x.side_data_list ?? new SideData[0]).Select(s => Core.N(s.rotation)))))
            } };
            AddValue(result.Media.TechnicalValues, "container", info.Extension.ToLowerInvariant());
            AddValue(result.Media.TechnicalValues, "bitrate", raw.format == null ? null : raw.format.bit_rate);
            if (video != null) {
                AddValue(result.Media.TechnicalValues, "width", Core.N(video.width)); AddValue(result.Media.TechnicalValues, "height", Core.N(video.height));
                AddValue(result.Media.TechnicalValues, "fps", Core.N(Fps(video.avg_frame_rate)));
                AddValue(result.Media.TechnicalValues, "video-codec", video.codec_name); AddValue(result.Media.TechnicalValues, "video-bitrate", video.bit_rate);
            }
            if (audio != null) { AddValue(result.Media.TechnicalValues, "audio-codec", audio.codec_name); AddValue(result.Media.TechnicalValues, "audio-bitrate", audio.bit_rate); }
            AddTags(result.Media.MetadataValues, "format/", raw.format == null ? null : raw.format.tags);
            for (int i = 0; i < streams.Length; i++) AddTags(result.Media.MetadataValues, streams[i].codec_type + "/" + i + "/", streams[i].tags);
            AddValue(result.Media.MetadataValues, "file/modified-utc", info.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture));
            string frameFile = Path.Combine(work, Guid.NewGuid().ToString("N") + ".gray");
            string audioFile = Path.Combine(work, Guid.NewGuid().ToString("N") + ".pcm");
            try {
                if (video != null) {
                    await Core.Tool(ffmpeg, new[] { "-hide_banner", "-nostdin", "-v", "error", "-y", "-threads", "2", "-i", path,
                        "-map", "0:" + video.index, "-an", "-vf", "setpts=PTS-STARTPTS,fps=2,scale=32:32:flags=area,format=gray", "-threads", "1", "-f", "rawvideo", frameFile }, ct).ConfigureAwait(false);
                    using (var input = File.OpenRead(frameFile)) {
                        byte[] bytes = new byte[FrameSide * FrameSide]; int count;
                        while ((count = input.Read(bytes, 0, bytes.Length)) > 0) {
                            ct.ThrowIfCancellationRequested();
                            if (count != bytes.Length) throw new IOException("Неполный кадр анализа: " + path);
                            result.Video.Add(Describe(bytes, 0)); result.Cropped.Add(Describe(bytes, 2));
                        }
                    }
                    if (result.Video.Count == 0) throw new IOException("Не получены видеовыборки: " + path);
                }
                if (audio != null) {
                    await Core.Tool(ffmpeg, new[] { "-hide_banner", "-nostdin", "-v", "error", "-y", "-threads", "2", "-i", path,
                        "-map", "0:" + audio.index, "-vn", "-ac", "1", "-ar", "8000", "-c:a", "pcm_s16le", "-f", "s16le", audioFile }, ct).ConfigureAwait(false);
                    using (var input = new BinaryReader(File.OpenRead(audioFile))) {
                        long samples = input.BaseStream.Length / 2;
                        for (long start = 0; start < samples; start += (int)(AudioRate * AudioStep)) {
                            ct.ThrowIfCancellationRequested(); input.BaseStream.Position = start * 2;
                            var values = new double[FftSize]; int available = (int)Math.Min(FftSize, samples - start);
                            for (int i = 0; i < available; i++) values[i] = input.ReadInt16() / 32768.0;
                            result.Audio.Add(Spectrum(values));
                        }
                    }
                    if (result.Audio.Count == 0) throw new IOException("Не получены аудиовыборки: " + path);
                }
                result.Media.VisualSamples = result.Video.Count; result.Media.AudioSamples = result.Audio.Count;
                return result;
            } finally {
                // Temporary ownership is confined to this unique analysis directory. Cleanup errors surface at Analyze level.
                if (File.Exists(frameFile)) File.Delete(frameFile);
                if (File.Exists(audioFile)) File.Delete(audioFile);
            }
        }
        static string Tags(Dictionary<string, string> tags) {
            return tags == null || tags.Count == 0 ? "(нет тегов)" : string.Join("; ", tags.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
        }
        static float[] Describe(byte[] pixels, int inset) {
            var result = new float[DescriptorSide * DescriptorSide];
            double scale = (FrameSide - 2.0 * inset) / DescriptorSide;
            for (int y = 0; y < DescriptorSide; y++) for (int x = 0; x < DescriptorSide; x++) {
                int x0 = inset + (int)(x * scale), x1 = inset + (int)((x + 1) * scale);
                int y0 = inset + (int)(y * scale), y1 = inset + (int)((y + 1) * scale);
                float sum = 0; for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) sum += pixels[yy * FrameSide + xx];
                result[y * DescriptorSide + x] = sum / ((x1 - x0) * (y1 - y0));
            }
            return result;
        }
        static float[] Spectrum(double[] real) {
            var imaginary = new double[FftSize]; double energy = 0;
            for (int i = 0; i < FftSize; i++) { energy += real[i] * real[i]; real[i] *= .5 - .5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)); }
            var result = new float[Bands + 1];
            if (energy / FftSize < 1e-8) { result[Bands] = 1; return result; }
            // Iterative radix-2 FFT. No external DSP dependency.
            for (int i = 1, j = 0; i < FftSize; i++) {
                int bit = FftSize >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
                if (i < j) { double t = real[i]; real[i] = real[j]; real[j] = t; }
            }
            for (int len = 2; len <= FftSize; len <<= 1) {
                double angle = -2 * Math.PI / len, wr = Math.Cos(angle), wi = Math.Sin(angle);
                for (int start = 0; start < FftSize; start += len) {
                    double ur = 1, ui = 0;
                    for (int j = 0; j < len / 2; j++) {
                        int a = start + j, b = a + len / 2;
                        double vr = real[b] * ur - imaginary[b] * ui, vi = real[b] * ui + imaginary[b] * ur;
                        real[b] = real[a] - vr; imaginary[b] = imaginary[a] - vi; real[a] += vr; imaginary[a] += vi;
                        double next = ur * wr - ui * wi; ui = ur * wi + ui * wr; ur = next;
                    }
                }
            }
            for (int bin = 1; bin < FftSize / 2; bin++) {
                double hz = (double)bin * AudioRate / FftSize; if (hz < 40) continue;
                int band = Math.Min(Bands - 1, (int)(Math.Log(hz / 40) / Math.Log(100) * Bands));
                result[band] += (float)(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
            }
            double norm = 0;
            for (int i = 0; i < Bands; i++) { result[i] = (float)Math.Sqrt(result[i]); norm += result[i] * result[i]; }
            norm = Math.Sqrt(norm); if (norm > 0) for (int i = 0; i < Bands; i++) result[i] /= (float)norm;
            return result;
        }
        static Matches VisualMatches(Fingerprint output, Fingerprint reference, CancellationToken ct) {
            var matches = new Matches(output.Video.Count);
            for (int i = 0; i < output.Video.Count; i++) {
                ct.ThrowIfCancellationRequested();
                for (int j = 0; j < reference.Video.Count; j++) {
                    double score = Math.Max(PixelSimilarity(output.Video[i], reference.Video[j]),
                        Math.Max(PixelSimilarity(output.Video[i], reference.Cropped[j]), PixelSimilarity(output.Cropped[i], reference.Video[j])));
                    SetMatch(matches, i, j, score, VideoStep, VideoStep);
                }
            }
            return matches;
        }
        static double PixelSimilarity(float[] a, float[] b) {
            double error = 0; for (int i = 0; i < a.Length; i++) error += Math.Abs(a[i] - b[i]);
            return Math.Max(0, 1 - error / (a.Length * 64));
        }
        static Matches AudioMatches(Fingerprint output, Fingerprint reference, CancellationToken ct) {
            var matches = new Matches(output.Audio.Count);
            for (int i = 0; i < output.Audio.Count; i++) {
                ct.ThrowIfCancellationRequested();
                for (int j = 0; j < reference.Audio.Count; j++) {
                    double dot = 0; for (int k = 0; k < output.Audio[i].Length; k++) dot += output.Audio[i][k] * reference.Audio[j][k];
                    SetMatch(matches, i, j, Math.Min(1, dot * dot), AudioStep, AudioStep);
                }
            }
            return matches;
        }
        static void SetMatch(Matches matches, int i, int j, double score, double outputStep, double referenceStep) {
            // Resolve equivalent static frames toward a continuous timeline, not arbitrary frame zero.
            int expected = i == 0 || matches.Indices[i - 1] < 0 ? (int)(i * outputStep / referenceStep) : matches.Indices[i - 1] + 1;
            if (score > matches.Scores[i] + .00001 || (Math.Abs(score - matches.Scores[i]) <= .00001 &&
                (matches.Indices[i] < 0 || Math.Abs(j - expected) < Math.Abs(matches.Indices[i] - expected)))) {
                matches.Scores[i] = score; matches.Indices[i] = j;
            }
        }
        static IEnumerable<AssemblyDifferenceFragment> Fragments(Matches matches, double step, double outputDuration, double referenceDuration, string kind) {
            int start = -1;
            for (int i = 0; i <= matches.Scores.Length; i++) {
                bool good = i < matches.Scores.Length && matches.Scores[i] >= .82 && matches.Indices[i] >= 0;
                bool continuous = good && start >= 0 && matches.Indices[i] >= matches.Indices[i - 1] && matches.Indices[i] <= matches.Indices[i - 1] + 2;
                if (start >= 0 && !continuous) {
                    int end = i - 1;
                    if (end > start) yield return new AssemblyDifferenceFragment { Kind = kind,
                        OutputStart = start * step, OutputEnd = Math.Min(outputDuration, (end + 1) * step),
                        ReferenceStart = matches.Indices[start] * step, ReferenceEnd = Math.Min(referenceDuration, (matches.Indices[end] + 1) * step),
                        Similarity = 100 * matches.Scores.Skip(start).Take(end - start + 1).Average() };
                    start = -1;
                }
                if (good && start < 0) start = i;
            }
        }
        static string TrackState(bool output, bool reference) {
            return output && reference ? "есть с обеих сторон" : output ? "только в результате (сходство 0%)" : reference ? "только в сравнении (сходство 0%)" : "нет с обеих сторон (не оценивается)";
        }
        static void Merge(double[] best, double[] scores) { for (int i = 0; i < best.Length; i++) best[i] = Math.Max(best[i], scores[i]); }
        static double Mean(double[] scores) { return scores.Length == 0 ? 0 : scores.Average() * 100; }
        static double? Similarity(bool a, bool b, double[] scores) { return !a && !b ? (double?)null : a && b ? Mean(scores) : 0; }
        static List<AssemblyDifferenceCriterion> Criteria(double? visual, double? audio, double? duration, double? technical, double? metadata) {
            return new List<AssemblyDifferenceCriterion> {
                Criterion("visual", "Видеосодержание", 50, visual.HasValue ? 100 - visual : null, "Ближайшие серые кадры выборки."),
                Criterion("audio", "Аудиосодержание", 30, audio.HasValue ? 100 - audio : null, "Ближайшие спектральные окна выборки."),
                Criterion("duration", "Длительность", 10, duration, "Относительная разница длительности."),
                Criterion("technical", "Технические параметры", 7, technical, "Среднее отличие доступных разрешения, fps, кодеков, битрейтов, контейнера."),
                Criterion("metadata", "Метаданные", 3, metadata, "Доля отличающихся тегов и даты изменения файла; максимум 3 балла.")
            };
        }
        static AssemblyDifferenceCriterion Criterion(string key, string name, double weight, double? raw, string details) {
            return new AssemblyDifferenceCriterion { Key = key, Name = name, Weight = weight, RawDifferencePercent = raw,
                EarnedPoints = raw.HasValue ? (double?)Math.Round(weight * Math.Max(0, Math.Min(100, raw.Value)) / 100, 4) : null, Details = details };
        }
        static double Points(List<AssemblyDifferenceCriterion> criteria) { return Math.Round(criteria.Sum(x => x.EarnedPoints ?? 0), 4); }
        static double? Nearest(List<AssemblyDifferenceComparison> comparisons, string key) {
            var values = comparisons.SelectMany(x => x.Criteria).Where(x => x.Key == key && x.RawDifferencePercent.HasValue).Select(x => x.RawDifferencePercent.Value).ToList();
            return values.Count == 0 ? (double?)null : values.Min();
        }
        static void AddValue(List<AssemblyDifferenceValue> values, string key, string value) {
            if (!string.IsNullOrWhiteSpace(value) && value != "N/A") values.Add(new AssemblyDifferenceValue { Key = key, Value = value });
        }
        static void AddTags(List<AssemblyDifferenceValue> values, string prefix, Dictionary<string, string> tags) {
            if (tags != null) foreach (var tag in tags.OrderBy(x => x.Key, StringComparer.Ordinal)) AddValue(values, prefix + tag.Key.ToLowerInvariant(), tag.Value);
        }
        static double Fps(string text) {
            var parts = (text ?? "").Split('/');
            return parts.Length == 2 && Core.Parse(parts[1]) > 0 ? Core.Parse(parts[0]) / Core.Parse(parts[1]) : Core.Parse(text);
        }
        static double? ValueDifference(List<AssemblyDifferenceValue> left, List<AssemblyDifferenceValue> right, bool numeric) {
            var a = left.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal); var b = right.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var keys = a.Keys.Union(b.Keys).ToList(); if (keys.Count == 0) return null;
            double sum = 0; int count = 0;
            foreach (string key in keys) {
                string av, bv; bool hasA = a.TryGetValue(key, out av), hasB = b.TryGetValue(key, out bv);
                bool isNumber = numeric && (key == "width" || key == "height" || key == "fps" || key.EndsWith("bitrate", StringComparison.Ordinal));
                double an = Core.Parse(av), bn = Core.Parse(bv);
                if (isNumber) { hasA &= an > 0; hasB &= bn > 0; }
                if (!hasA && !hasB) continue;
                count++;
                if (!hasA || !hasB) sum += 100;
                else if (isNumber) sum += 100 * Math.Abs(an - bn) / Math.Max(an, bn);
                else if (!string.Equals(av, bv, StringComparison.Ordinal)) sum += 100;
            }
            return count == 0 ? (double?)null : sum / count;
        }
        static void FormatCriteria(StringBuilder text, List<AssemblyDifferenceCriterion> criteria) {
            text.AppendLine("Баллы критериев (вес → сырое отличие → начислено):");
            foreach (var criterion in criteria) text.AppendLine("  " + criterion.Name + ": " + Core.N(criterion.Weight) + " → " + Percent(criterion.RawDifferencePercent) +
                " → " + (criterion.EarnedPoints.HasValue ? Core.N(criterion.EarnedPoints.Value) : "недоступно, 0") + " балла. " + criterion.Details);
        }
        static string Percent(double? value) { return value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%" : "не рассчитано"; }
        static string Time(double seconds) { return seconds.ToString("0.00", CultureInfo.InvariantCulture) + " с"; }
        public static string Format(AssemblyDifferenceReport report) {
            if (report == null) return "Отчёт анализа отсутствует.";
            var text = new StringBuilder();
            text.AppendLine("АНАЛИЗ ОТЛИЧИЯ (локальная эвристика)").AppendLine(report.Output)
                .AppendLine("Состояние: " + report.Status).AppendLine(report.Summary)
                .AppendLine("Общий индекс: " + Percent(report.Completed ? report.DifferencePercent : null));
            FormatCriteria(text, report.Criteria);
            if (!string.IsNullOrEmpty(report.Error)) text.AppendLine("ОШИБКА: " + report.Error);
            if (report.OutputMedia != null) text.AppendLine("\r\nПараметры результата: " + report.OutputMedia.Parameters).AppendLine(report.OutputMedia.Metadata);
            foreach (var c in report.Comparisons) {
                text.AppendLine("\r\n" + (c.Category == "source" ? "ИСХОДНИК" : "ПРОШЛЫЙ РЕЗУЛЬТАТ") + ": " + c.File)
                    .AppendLine("Состояние: " + c.Status);
                if (!c.Completed) { text.AppendLine("ОШИБКА / НЕ ЗАВЕРШЕНО: " + c.Error); continue; }
                text.AppendLine("Отличие: " + Percent(c.DifferencePercent) + "; сходство видео: " + Percent(c.VisualSimilarity) + "; аудио: " + Percent(c.AudioSimilarity))
                    .AppendLine("Длительность сравнения: " + Time(c.Duration) + "; разница результата: " + Time(c.DurationDelta))
                    .AppendLine(c.Details).AppendLine("Метаданные (вес до 3 баллов):").AppendLine(c.MetadataDetails)
                    .AppendLine("Кандидаты совпадающих / повторно использованных фрагментов: " + c.Fragments.Count);
                FormatCriteria(text, c.Criteria);
                foreach (var f in c.Fragments) text.AppendLine("  " + (f.Kind == "video" ? "Видео" : "Аудио") + " результата " + Time(f.OutputStart) + "–" + Time(f.OutputEnd) +
                    " ↔ файл " + Time(f.ReferenceStart) + "–" + Time(f.ReferenceEnd) + "; сходство " + Percent(f.Similarity));
            }
            text.AppendLine("\r\nМЕТОДИКА").AppendLine(report.Methodology).AppendLine("\r\nОГРАНИЧЕНИЯ И ПРЕДУПРЕЖДЕНИЯ").AppendLine(report.Warnings);
            return text.ToString();
        }
    }
}
