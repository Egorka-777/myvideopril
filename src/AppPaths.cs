using System;
using System.Collections.Generic;
using System.IO;

namespace VideoBatch {
    /// <summary>Resolve install root, uploader and ffmpeg regardless of EXE folder (asphalt-test, repo, full install).</summary>
    public static class AppPaths {
        static string cachedInstallRoot;
        static string cachedUploaderRoot;
        static List<string> cachedCandidates;
        static string cachedFfmpeg;
        static string cachedFfprobe;

        public static string ExeDirectory => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

        public static string InstallRoot {
            get {
                if (!string.IsNullOrWhiteSpace(cachedInstallRoot)) return cachedInstallRoot;
                foreach (string root in CandidateInstallRoots()) {
                    if (HasFfmpeg(root) || HasUploader(root)) {
                        cachedInstallRoot = root;
                        RememberInstallRoot(root);
                        return root;
                    }
                }
                cachedInstallRoot = ExeDirectory;
                return cachedInstallRoot;
            }
        }

        public static string UploaderRoot {
            get {
                if (!string.IsNullOrWhiteSpace(cachedUploaderRoot)) return cachedUploaderRoot;
                string local = Path.Combine(ExeDirectory, "tools", "uploader");
                if (HasCompleteUploader(local)) {
                    cachedUploaderRoot = local;
                    return local;
                }
                foreach (string root in CandidateInstallRoots()) {
                    string candidate = Path.Combine(root, "tools", "uploader");
                    if (HasCompleteUploader(candidate)) {
                        cachedUploaderRoot = candidate;
                        RememberInstallRoot(root);
                        return candidate;
                    }
                }
                cachedUploaderRoot = local;
                return local;
            }
        }

        public static bool TryResolveFfmpeg(out string ffmpeg, out string ffprobe) {
            if (!string.IsNullOrWhiteSpace(cachedFfmpeg) && !string.IsNullOrWhiteSpace(cachedFfprobe)) {
                ffmpeg = cachedFfmpeg;
                ffprobe = cachedFfprobe;
                return true;
            }
            ffmpeg = "";
            ffprobe = "";
            foreach (string root in CandidateInstallRoots()) {
                string f = Path.Combine(root, "tools", "ffmpeg.exe");
                string p = Path.Combine(root, "tools", "ffprobe.exe");
                if (!File.Exists(f) || !File.Exists(p)) continue;
                if (!LooksLikeFfmpeg(f) || !LooksLikeFfmpeg(p)) continue;
                cachedFfmpeg = f;
                cachedFfprobe = p;
                ffmpeg = f;
                ffprobe = p;
                RememberInstallRoot(root);
                return true;
            }
            return false;
        }

        public static IEnumerable<string> CandidateInstallRoots() {
            if (cachedCandidates != null) return cachedCandidates;

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void add(string path) {
                if (string.IsNullOrWhiteSpace(path)) return;
                try {
                    path = Path.GetFullPath(path).TrimEnd('\\', '/');
                    if (seen.Add(path)) list.Add(path);
                } catch { }
            }

            add(ExeDirectory);

            string hint = LoadRememberedInstallRoot();
            if (!string.IsNullOrWhiteSpace(hint)) add(hint);

            string env = (Environment.GetEnvironmentVariable("VIDEOBATCH_HOME") ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(env)) add(env);

            add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoBatchDesktop"));

            try {
                var dir = new DirectoryInfo(ExeDirectory);
                for (int i = 0; i < 8 && dir?.Parent != null; i++) {
                    dir = dir.Parent;
                    add(dir.FullName);
                    ScanNestedInstalls(dir.FullName, add);
                }
            } catch { }

            cachedCandidates = list;
            return list;
        }

        static void ScanNestedInstalls(string parent, Action<string> add) {
            if (!Directory.Exists(parent)) return;
            try {
                foreach (string sub in Directory.GetDirectories(parent)) {
                    if (HasFfmpeg(sub) || HasUploader(sub)) add(sub);
                    try {
                        foreach (string sub2 in Directory.GetDirectories(sub)) {
                            if (HasFfmpeg(sub2) || HasUploader(sub2)) add(sub2);
                        }
                    } catch { }
                }
            } catch { }
        }

        static bool HasFfmpeg(string root) {
            return File.Exists(Path.Combine(root, "tools", "ffmpeg.exe"))
                && File.Exists(Path.Combine(root, "tools", "ffprobe.exe"));
        }

        static bool HasUploader(string root) {
            return Directory.Exists(Path.Combine(root, "tools", "uploader"));
        }

        public static bool HasCompleteUploader(string uploaderDir) {
            if (string.IsNullOrWhiteSpace(uploaderDir) || !Directory.Exists(uploaderDir)) return false;
            return File.Exists(Path.Combine(uploaderDir, "worker.js"))
                && File.Exists(Path.Combine(uploaderDir, "worker-http.js"))
                && File.Exists(Path.Combine(uploaderDir, "node.exe"))
                && Directory.Exists(Path.Combine(uploaderDir, "node_modules", "playwright-core"));
        }

        static bool LooksLikeFfmpeg(string path) {
            try {
                var len = new FileInfo(path).Length;
                return len == 102856192L || len == 102652416L || len > 50_000_000L;
            } catch { return false; }
        }

        public static string InstallRootHintPathForUser() => Path.Combine(Store.Root, "install-root.txt");

        static string InstallRootHintFile => InstallRootHintPathForUser();

        static string LoadRememberedInstallRoot() {
            try {
                if (File.Exists(InstallRootHintFile))
                    return (File.ReadAllText(InstallRootHintFile) ?? "").Trim();
            } catch { }
            return "";
        }

        static void RememberInstallRoot(string root) {
            if (string.IsNullOrWhiteSpace(root)) return;
            try {
                Directory.CreateDirectory(Store.Root);
                string full = Path.GetFullPath(root).TrimEnd('\\', '/');
                if (string.Equals(LoadRememberedInstallRoot(), full, StringComparison.OrdinalIgnoreCase)) return;
                File.WriteAllText(InstallRootHintFile, full);
            } catch { }
        }
    }
}
