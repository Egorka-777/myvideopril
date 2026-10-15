using System;

namespace VideoBatch {
    /// <summary>Four independent studio profiles: RU, EN, RU-Inst, EN-Inst.</summary>
    public static class AssemblyProfile {
        public const string Ru = "RU";
        public const string En = "EN";
        public const string RuInst = "RU-Inst";
        public const string EnInst = "EN-Inst";

        public static string Normalize(string profile) {
            if (string.IsNullOrWhiteSpace(profile)) return Ru;
            profile = profile.Trim();
            if (profile.Equals(Ru, StringComparison.OrdinalIgnoreCase)) return Ru;
            if (profile.Equals(En, StringComparison.OrdinalIgnoreCase)) return En;
            if (profile.Equals(RuInst, StringComparison.OrdinalIgnoreCase) || profile.Equals("RU Inst", StringComparison.OrdinalIgnoreCase)) return RuInst;
            if (profile.Equals(EnInst, StringComparison.OrdinalIgnoreCase) || profile.Equals("EN Inst", StringComparison.OrdinalIgnoreCase)) return EnInst;
            return Ru;
        }

        public static bool IsInst(string profile) {
            profile = Normalize(profile);
            return profile == RuInst || profile == EnInst;
        }

        public static string MainProfile(string profile) {
            profile = Normalize(profile);
            return profile == En || profile == EnInst ? En : Ru;
        }

        public static string LedgerScope(string profile) => Normalize(profile);

        public static string ShortLabel(string profile) {
            profile = Normalize(profile);
            if (profile == RuInst) return "RU Inst";
            if (profile == EnInst) return "EN Inst";
            return profile;
        }
    }
}
