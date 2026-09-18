using System.IO;

namespace VideoBatch {
    /// <summary>Find bundled ffmpeg/ffprobe — local EXE folder or full VideoBatch install nearby.</summary>
    public static class ToolsLocator {
        public static bool TryResolve(out string ffmpeg, out string ffprobe, out string hint) {
            ffmpeg = "";
            ffprobe = "";
            hint = "";
            if (AppPaths.TryResolveFfmpeg(out ffmpeg, out ffprobe)) return true;

            hint = "Обработка видео: не найдены ffmpeg.exe и ffprobe.exe.\n\n"
                + "VideoBatch ищет папку tools рядом с EXE и в соседних установках (например VideoBatch_Desktop).\n"
                + "После первого успешного поиска путь запоминается в:\n"
                + AppPaths.InstallRootHintPathForUser() + "\n\n"
                + "Загрузка YouTube/TikTok из этой папки работает без ffmpeg.";
            return false;
        }
    }
}
