using System;

namespace VideoBatch {
    /// <summary>Separate combination ledgers for RU and EN upload channels on the same studio materials.</summary>
    public static class AssemblyHistoryScope {
        public const string Shared = "Shared";
        public const string Ru = "RU";
        public const string En = "EN";

        public static string Normalize(string scope) {
            if (string.IsNullOrWhiteSpace(scope)) return Shared;
            scope = scope.Trim();
            if (scope.Equals(Ru, StringComparison.OrdinalIgnoreCase)) return Ru;
            if (scope.Equals(En, StringComparison.OrdinalIgnoreCase)) return En;
            if (scope.Equals(Shared, StringComparison.OrdinalIgnoreCase) || scope.Equals("Общий", StringComparison.OrdinalIgnoreCase)) return Shared;
            throw new Exception("Журнал связок: выберите RU, EN или общий.");
        }

        public static string MenuLabel(string scope) {
            scope = Normalize(scope);
            if (scope == Ru) return "RU · русские каналы";
            if (scope == En) return "EN · English channels";
            return "Общий · все каналы";
        }

        public static string ShortLabel(string scope) {
            scope = Normalize(scope);
            if (scope == Ru) return "RU";
            if (scope == En) return "EN";
            return "Общий";
        }
    }
}
