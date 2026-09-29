using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VideoBatch {
    /// <summary>Stable upload copies: source paths from Downloads/UI must not be passed directly to workers.</summary>
    public static class UploadStaging {
        static readonly Regex DuplicateSuffix = new Regex(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);

        public static string Root(Preferences settings) {
            string folder = settings?.UploadStagingFolder;
            if (string.IsNullOrWhiteSpace(folder)) folder = @"C:\VideoBatch\Upload";
            return folder.Trim();
        }

        public static void EnsureDir(string folder) {
            if (string.IsNullOrWhiteSpace(folder)) folder = @"C:\VideoBatch\Upload";
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        }

        public static string SanitizeDirName(string name) {
            name = (name ?? "").Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return string.IsNullOrWhiteSpace(name) ? "profile" : name;
        }

        static string NormalizeStem(string fileName) {
            string stem = Path.GetFileNameWithoutExtension(fileName ?? "");
            stem = DuplicateSuffix.Replace(stem, "").Trim();
            return stem;
        }

        /// <summary>Find video when stored path is stale (renamed duplicate, moved, or already staged).</summary>
        public static string TryResolveVideo(string storedPath, string platformFolder, string stagingRoot, string profileId) {
            if (!string.IsNullOrWhiteSpace(storedPath) && File.Exists(storedPath)) return storedPath;

            string baseName = Path.GetFileName(storedPath ?? "");
            string stem = NormalizeStem(baseName);

            if (!string.IsNullOrWhiteSpace(profileId) && !string.IsNullOrWhiteSpace(stagingRoot)) {
                string stagedDir = Path.Combine(stagingRoot, platformFolder ?? "", SanitizeDirName(profileId));
                string fromStaging = FindInDirectory(stagedDir, baseName, stem);
                if (!string.IsNullOrWhiteSpace(fromStaging)) return fromStaging;
            }

            string sourceDir = Path.GetDirectoryName(storedPath ?? "");
            if (!string.IsNullOrWhiteSpace(sourceDir) && Directory.Exists(sourceDir)) {
                string fromSource = FindInDirectory(sourceDir, baseName, stem);
                if (!string.IsNullOrWhiteSpace(fromSource)) return fromSource;
            }

            return null;
        }

        static string FindInDirectory(string dir, string baseName, string stem) {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            if (!string.IsNullOrWhiteSpace(baseName)) {
                string exact = Path.Combine(dir, baseName);
                if (File.Exists(exact)) return exact;
            }
            if (string.IsNullOrWhiteSpace(stem)) return null;
            try {
                var matches = Directory.GetFiles(dir)
                    .Where(f => {
                        string ext = Path.GetExtension(f);
                        if (!string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(ext, ".mov", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(ext, ".mkv", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(ext, ".webm", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(ext, ".m4v", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(ext, ".avi", StringComparison.OrdinalIgnoreCase))
                            return false;
                        return string.Equals(NormalizeStem(Path.GetFileName(f)), stem, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .ToList();
                return matches.Count > 0 ? matches[0] : null;
            } catch {
                return null;
            }
        }

        public static void ValidateReady(string path, string context) {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new Exception(context + ": файл не найден: " + (path ?? ""));
            var fi = new FileInfo(path);
            if (fi.Length <= 0) throw new Exception(context + ": файл пустой: " + Path.GetFileName(path));
            if (path.Length > 240) throw new Exception(context + ": слишком длинный путь (" + path.Length + " симв.)");
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } }
            catch (IOException ex) {
                throw new Exception(context + ": файл занят — " + Path.GetFileName(path) + " (" + ex.Message + ")");
            }
        }

        public static string StageVideo(
            string stagingRoot,
            string platformFolder,
            string sourcePath,
            string profileId,
            int index,
            int total,
            string plannedFileName,
            string uploadTitle) {
            EnsureDir(stagingRoot);
            string profileDir = Path.Combine(stagingRoot, platformFolder ?? "", SanitizeDirName(profileId));
            if (!Directory.Exists(profileDir)) Directory.CreateDirectory(profileDir);

            string name = (plannedFileName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) {
                string ext = Path.GetExtension(sourcePath ?? "");
                if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";
                string safeTitle = MakeSafeFileStem(uploadTitle, index, total);
                name = safeTitle + ext;
            }

            string dest = Path.Combine(profileDir, name);
            if (File.Exists(dest) && !PathsEqual(sourcePath, dest)) {
                string ext = Path.GetExtension(name);
                string stem = Path.GetFileNameWithoutExtension(name);
                for (int n = 2; n < 100; n++) {
                    string alt = stem + " (" + n + ")" + ext;
                    string tryDest = Path.Combine(profileDir, alt);
                    if (!File.Exists(tryDest)) { dest = tryDest; break; }
                }
            }

            if (!File.Exists(dest) || new FileInfo(sourcePath).LastWriteTimeUtc > new FileInfo(dest).LastWriteTimeUtc)
                File.Copy(sourcePath, dest, true);
            return dest;
        }

        public static string PlannedTikTokFileName(string caption, int index1Based, int total, string sourcePath) {
            string ext = Path.GetExtension(sourcePath ?? "");
            if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";
            string stem = MakeSafeFileStem(caption, index1Based, total);
            if (total > 1) stem = stem + " " + index1Based + "of" + total;
            return stem + ext;
        }

        static string MakeSafeFileStem(string caption, int index1Based, int total) {
            string raw = (caption ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw)) raw = "tiktok-" + index1Based;
            raw = Regex.Replace(raw, @"[\r\n\t]+", " ");
            foreach (char ch in Path.GetInvalidFileNameChars()) raw = raw.Replace(ch, '_');
            raw = raw.Trim().TrimEnd('.');
            if (raw.Length > 80) raw = raw.Substring(0, 80).Trim();
            if (string.IsNullOrWhiteSpace(raw)) raw = "tiktok-" + index1Based;
            return raw;
        }

        static bool PathsEqual(string a, string b) {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static bool RunSelfTests() {
            string tmp = Path.Combine(Path.GetTempPath(), "vb-staging-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(tmp);
                string srcDir = Path.Combine(tmp, "src");
                Directory.CreateDirectory(srcDir);
                string src = Path.Combine(srcDir, "clip.mp4");
                File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });

                string missing = Path.Combine(srcDir, "clip (85).mp4");
                string resolved = TryResolveVideo(missing, "tiktok", tmp, "pid1");
                if (!string.Equals(resolved, src, StringComparison.OrdinalIgnoreCase)) return false;

                string staged = StageVideo(tmp, "tiktok", src, "pid1", 1, 1, "test.mp4", "caption");
                if (!File.Exists(staged)) return false;
                ValidateReady(staged, "test");

                string again = StageVideo(tmp, "tiktok", src, "pid1", 1, 1, "test.mp4", "caption");
                if (!string.Equals(staged, again, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            } catch {
                return false;
            } finally {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
