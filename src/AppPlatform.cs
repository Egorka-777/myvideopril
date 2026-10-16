using System;

namespace VideoBatch {
    public enum AppPlatform {
        Unchosen,
        YouTube,
        TikTok,
        Telegram,
        Studio
    }

    public static class AppPlatformNav {
        public static AppPlatform Parse(string value) {
            switch ((value ?? "").Trim()) {
                case "YouTube": return AppPlatform.YouTube;
                case "TikTok": return AppPlatform.TikTok;
                case "Telegram": return AppPlatform.Telegram;
                case "Studio": return AppPlatform.Studio;
                default: return AppPlatform.Unchosen;
            }
        }

        public static string ToStorage(AppPlatform platform) {
            switch (platform) {
                case AppPlatform.YouTube: return "YouTube";
                case AppPlatform.TikTok: return "TikTok";
                case AppPlatform.Telegram: return "Telegram";
                case AppPlatform.Studio: return "Studio";
                default: return "";
            }
        }

        public static string Title(AppPlatform platform) {
            switch (platform) {
                case AppPlatform.YouTube: return "YouTube";
                case AppPlatform.TikTok: return "TikTok";
                case AppPlatform.Telegram: return "Telegram";
                case AppPlatform.Studio: return "Видео и монтаж";
                default: return "VideoBatch";
            }
        }

        public static bool IsNavVisible(AppPlatform platform, NavSection section) {
            switch (platform) {
                case AppPlatform.YouTube:
                    return section == NavSection.Home || section == NavSection.Tasks || section == NavSection.Statistics
                        || section == NavSection.AccountLibrary || section == NavSection.Profiles || section == NavSection.Proxy
                        || section == NavSection.YouTube || section == NavSection.Warmup || section == NavSection.WarmupClips
                        || section == NavSection.ViewsSearch || section == NavSection.Logs || section == NavSection.Settings;
                case AppPlatform.TikTok:
                    return section == NavSection.Home || section == NavSection.Tasks || section == NavSection.Statistics
                        || section == NavSection.AccountLibrary || section == NavSection.Profiles || section == NavSection.Proxy
                        || section == NavSection.TikTok || section == NavSection.Logs || section == NavSection.Settings;
                case AppPlatform.Telegram:
                    return section == NavSection.Home || section == NavSection.Tasks
                        || section == NavSection.Telegram || section == NavSection.Logs || section == NavSection.Settings;
                case AppPlatform.Studio:
                    return section == NavSection.Home || section == NavSection.Tasks
                        || section == NavSection.VideoProcessing || section == NavSection.VideoAssembly || section == NavSection.Highlights
                        || section == NavSection.Logs || section == NavSection.Settings;
                default:
                    return true;
            }
        }

        public static NavSection DefaultSection(AppPlatform platform) {
            switch (platform) {
                case AppPlatform.YouTube: return NavSection.YouTube;
                case AppPlatform.TikTok: return NavSection.TikTok;
                case AppPlatform.Telegram: return NavSection.Telegram;
                case AppPlatform.Studio: return NavSection.VideoProcessing;
                default: return NavSection.Home;
            }
        }
    }
}
