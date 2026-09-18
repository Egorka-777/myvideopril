namespace VideoBatch {
    /// <summary>Cross-page selection: one profile flows into YouTube/TikTok upload without re-picking.</summary>
    public static class NavigationContext {
        public static string SelectedProfileId { get; set; } = "";
        public static string SelectedAccountName { get; set; } = "";
        public static string SelectedMarket { get; set; } = "RU";
        public static string SelectedPlatform { get; set; } = "YouTube";

        public static void SelectYouTube(YouTubeChannel ch) {
            if (ch == null) return;
            SelectedProfileId = (ch.ProfileId ?? "").Trim();
            SelectedAccountName = ch.Name ?? "";
            SelectedMarket = string.IsNullOrWhiteSpace(ch.Market) ? "RU" : ch.Market.Trim().ToUpperInvariant();
            SelectedPlatform = "YouTube";
        }

        public static void SelectTikTok(TikTokAccount acc) {
            if (acc == null) return;
            SelectedProfileId = (acc.ProfileId ?? "").Trim();
            SelectedAccountName = acc.Name ?? "";
            SelectedMarket = string.IsNullOrWhiteSpace(acc.Market) ? "RU" : acc.Market.Trim().ToUpperInvariant();
            SelectedPlatform = "TikTok";
        }

        public static void Clear() {
            SelectedProfileId = "";
            SelectedAccountName = "";
            SelectedMarket = "RU";
            SelectedPlatform = "";
        }
    }
}
