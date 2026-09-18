using System;

namespace VideoBatch {
    public enum NavSection {
        Home,
        Tasks,
        Statistics,
        Profiles,
        Proxy,
        VideoProcessing,
        YouTube,
        TikTok,
        Warmup,
        ViewsSearch,
        Logs,
        Settings
    }

    public sealed class NavigationService {
        public NavSection Current { get; private set; } = NavSection.Home;
        public event Action<NavSection> Navigated;

        public void Navigate(NavSection section) {
            if (Current == section) return;
            Current = section;
            Navigated?.Invoke(section);
        }

        public static string Title(NavSection section) {
            switch (section) {
                case NavSection.Home: return "Главная";
                case NavSection.Tasks: return "Задачи";
                case NavSection.Statistics: return "Статистика";
                case NavSection.Profiles: return "Профили";
                case NavSection.Proxy: return "Прокси";
                case NavSection.VideoProcessing: return "Обработка видео";
                case NavSection.YouTube: return "YouTube";
                case NavSection.TikTok: return "TikTok";
                case NavSection.Warmup: return "Прогрев";
                case NavSection.ViewsSearch: return "Просмотры и поиск";
                case NavSection.Logs: return "Логи";
                case NavSection.Settings: return "Настройки";
                default: return "VideoBatch";
            }
        }

        public static bool IsImplemented(NavSection section) {
            switch (section) {
                case NavSection.Warmup:
                case NavSection.ViewsSearch:
                case NavSection.Statistics:
                    return false;
                default:
                    return true;
            }
        }
    }
}
