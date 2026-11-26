using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace VideoBatch {
  internal sealed class AndroidTikTokSessionEntry {
    public string ProfileId { get; set; } = "";
    public string AccountName { get; set; } = "";
    public DateTime SavedUtc { get; set; }
  }

  internal static class AndroidTikTokSessionStore {
    static string PathName => Path.Combine(Store.Root, "android", "tiktok-sessions.json");

    static List<AndroidTikTokSessionEntry> Load() {
      if (!File.Exists(PathName)) return new List<AndroidTikTokSessionEntry>();
      return new JavaScriptSerializer().Deserialize<List<AndroidTikTokSessionEntry>>(File.ReadAllText(PathName))
        ?? new List<AndroidTikTokSessionEntry>();
    }

    static void Write(List<AndroidTikTokSessionEntry> rows) {
      Directory.CreateDirectory(Path.GetDirectoryName(PathName));
      string temp = PathName + ".tmp";
      File.WriteAllText(temp, new JavaScriptSerializer().Serialize(rows));
      if (File.Exists(PathName)) File.Replace(temp, PathName, null);
      else File.Move(temp, PathName);
    }

    public static void SaveVerified(string profileId, string accountName) {
      profileId = (profileId ?? "").Trim();
      if (profileId.Length == 0) return;
      var rows = Load();
      rows.RemoveAll(r => string.Equals(r.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
      rows.Add(new AndroidTikTokSessionEntry {
        ProfileId = profileId,
        AccountName = (accountName ?? "").Trim(),
        SavedUtc = DateTime.UtcNow
      });
      Write(rows);
    }

    public static void RestoreSavedSessions() {
      foreach (var entry in Load().Where(e => e != null && !string.IsNullOrWhiteSpace(e.ProfileId)))
        DashboardSessionState.Record(entry.ProfileId, "TikTok", new DashboardCheckResult {
          Authorized = true, Evidence = "saved", Source = "android-saved",
          AccountName = entry.AccountName ?? "",
          Detail = "Сохранённый вход TikTok Android."
        });
    }
  }
}