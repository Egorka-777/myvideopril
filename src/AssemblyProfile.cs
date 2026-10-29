using System;

namespace VideoBatch {
    /// <summary>Studio profiles: RU/EN plus separate Inst and TikTok output channels.</summary>
    public static class AssemblyProfile {
        public const string Ru = "RU";
        public const string En = "EN";
        public const string RuInst = "RU-Inst";
        public const string EnInst = "EN-Inst";
        public const string RuTikTok = "RU-TikTok";
        public const string EnTikTok = "EN-TikTok";

        public static readonly string[] All = { Ru, En, RuInst, EnInst, RuTikTok, EnTikTok };
        public static readonly string[] Visible = { Ru, En, RuInst, EnInst };

        public static string Normalize(string profile) {
            if (string.IsNullOrWhiteSpace(profile)) return Ru;
            profile = profile.Trim();
            if (profile.Equals(Ru, StringComparison.OrdinalIgnoreCase)) return Ru;
            if (profile.Equals(En, StringComparison.OrdinalIgnoreCase)) return En;
            if (profile.Equals(RuInst, StringComparison.OrdinalIgnoreCase) || profile.Equals("RU Inst", StringComparison.OrdinalIgnoreCase)) return RuInst;
            if (profile.Equals(EnInst, StringComparison.OrdinalIgnoreCase) || profile.Equals("EN Inst", StringComparison.OrdinalIgnoreCase)) return EnInst;
            if (profile.Equals(RuTikTok, StringComparison.OrdinalIgnoreCase) || profile.Equals("RU TikTok", StringComparison.OrdinalIgnoreCase)) return RuTikTok;
            if (profile.Equals(EnTikTok, StringComparison.OrdinalIgnoreCase) || profile.Equals("EN TikTok", StringComparison.OrdinalIgnoreCase)) return EnTikTok;
            return Ru;
        }

        public static bool IsInst(string profile) {
            profile = Normalize(profile);
            return profile == RuInst || profile == EnInst;
        }

        public static bool IsChannelProfile(string profile) {
            profile = Normalize(profile);
            return profile == RuInst || profile == EnInst || profile == RuTikTok || profile == EnTikTok;
        }

        public static string MainProfile(string profile) {
            profile = Normalize(profile);
            return profile == En || profile == EnInst || profile == EnTikTok ? En : Ru;
        }

        public static string LedgerScope(string profile) => Normalize(profile);

        public static string ShortLabel(string profile) {
            profile = Normalize(profile);
            if (profile == RuInst) return "RU INS";
            if (profile == EnInst) return "EN INS";
            if (profile == RuTikTok) return "RU TikTok";
            if (profile == EnTikTok) return "EN TikTok";
            return profile == En ? "EN YT" : "RU YT";
        }
    }
}
