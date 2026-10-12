using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoBatch {
    /// <summary>Windows may hand 8.3 paths (HERESW~1.MP4) for Cyrillic/long names; expand before any file IO.</summary>
    public static class PathUtil {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetLongPathName(string shortPath, StringBuilder longPath, int bufferSize);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferSize);

        public static string Normalize(string path) {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            path = path.Trim().Trim('"');
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path.Substring(8);
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;
            try { path = Path.GetFullPath(path); } catch { return path; }
            if (!path.Contains("~")) return path;
            var buffer = new StringBuilder(32768);
            if (GetLongPathName(path, buffer, buffer.Capacity) > 0 && buffer.Length > 0 && !buffer.ToString().Contains("~")) return buffer.ToString();
            return ResolveShortSegments(path);
        }

        static string ResolveShortSegments(string path) {
            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return path;
            var parts = path.Substring(root.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            string current = root.TrimEnd('\\');
            foreach (string part in parts) {
                if (!part.Contains("~")) { current = Path.Combine(current, part); continue; }
                string resolved = ResolveEntry(current, part);
                if (resolved == null) throw new FileNotFoundException("Не удалось найти файл или папку по короткому имени Windows: " + part + ". Добавьте видео через «+ Добавить файлы» в студии — файлы скопируются в материалы.", path);
                current = resolved;
            }
            return current;
        }

        static string ResolveEntry(string directory, string shortOrLongName) {
            if (!Directory.Exists(directory)) return null;
            string direct = Path.Combine(directory, shortOrLongName);
            if (File.Exists(direct) || Directory.Exists(direct)) return direct;
            foreach (string entry in Directory.GetFileSystemEntries(directory)) {
                string name = Path.GetFileName(entry);
                if (name.Equals(shortOrLongName, StringComparison.OrdinalIgnoreCase)) return entry;
                string shortName = ShortFileName(entry);
                if (shortName != null && shortName.Equals(shortOrLongName, StringComparison.OrdinalIgnoreCase)) return entry;
            }
            return null;
        }

        static string ShortFileName(string path) {
            var buffer = new StringBuilder(512);
            return GetShortPathName(path, buffer, buffer.Capacity) > 0 ? Path.GetFileName(buffer.ToString()) : null;
        }

        public static string Extended(string path) {
            path = Normalize(path);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        public static bool Exists(string path) {
            try {
                path = Normalize(path);
                if (File.Exists(path) || Directory.Exists(path)) return true;
                return File.Exists(Extended(path)) || Directory.Exists(Extended(path));
            } catch { return false; }
        }

        public static void CopyFile(string source, string destination) {
            source = Normalize(source);
            destination = Normalize(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            try { File.Copy(source, destination, true); return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            File.Copy(Extended(source), Extended(destination), true);
        }

        public static string[] NormalizeAll(IEnumerable<string> paths) {
            if (paths == null) return new string[0];
            return paths.Select(Normalize).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        }
    }
}
