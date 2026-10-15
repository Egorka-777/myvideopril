using System;

namespace VideoBatch {
    /// <summary>Separate combination ledgers for RU and EN upload channels on the same studio materials.</summary>
    public static class AssemblyHistoryScope {
        public const string Shared = "Shared";
        public const string Ru = "RU";
        public const string En = "EN";
        public const string RuInst = "RU-Inst";
        public const string EnInst = "EN-Inst";

        public static string Normalize(string scope) {
            if (string.IsNullOrWhiteSpace(scope)) return Shared;
            scope = scope.Trim();
            if (scope.Equals(Ru, StringComparison.OrdinalIgnoreCase)) return Ru;
            if (scope.Equals(En, StringComparison.OrdinalIgnoreCase)) return En;
            if (scope.Equals(RuInst, StringComparison.OrdinalIgnoreCase) || scope.Equals("RU Inst", StringComparison.OrdinalIgnoreCase)) return RuInst;
            if (scope.Equals(EnInst, StringComparison.OrdinalIgnoreCase) || scope.Equals("EN Inst", StringComparison.OrdinalIgnoreCase)) return EnInst;
            if (scope.Equals(Shared, StringComparison.OrdinalIgnoreCase) || scope.Equals("Общий", StringComparison.OrdinalIgnoreCase)) return Shared;
            throw new Exception("Журнал связок: выберите RU, EN, RU Inst, EN Inst или общий.");
        }

        public static string MenuLabel(string scope) {
            scope = Normalize(scope);
            if (scope == Ru) return "RU · русские каналы";
            if (scope == En) return "EN · English channels";
            if (scope == RuInst) return "RU Inst · Instagram";
            if (scope == EnInst) return "EN Inst · Instagram";
            return "Общий · все каналы";
        }

        public static string ShortLabel(string scope) {
            scope = Normalize(scope);
            if (scope == Ru) return "RU";
            if (scope == En) return "EN";
            if (scope == RuInst) return "RU Inst";
            if (scope == EnInst) return "EN Inst";
            return "Общий";
        }
    }
}
