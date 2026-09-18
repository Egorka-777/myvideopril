using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VideoBatch {
    /// <summary>GeoIP lookup by existing IP only. Results cached on disk in memory.</summary>
    public static class GeoIpCache {
        static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly object Lock = new object();
        static readonly Regex IpRx = new Regex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled);

        public static string LookupSync(string ip) {
            ip = NormalizeIp(ip);
            if (string.IsNullOrWhiteSpace(ip)) return "—";
            lock (Lock) {
                if (Cache.TryGetValue(ip, out var cached)) return cached;
            }
            try {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) }) {
                    string url = "http://ip-api.com/json/" + Uri.EscapeDataString(ip) + "?fields=status,countryCode";
                    string json = client.GetStringAsync(url).GetAwaiter().GetResult();
                    var m = Regex.Match(json, @"""countryCode""\s*:\s*""([A-Z]{2})""");
                    string code = m.Success ? m.Groups[1].Value : "—";
                    lock (Lock) { Cache[ip] = code; }
                    return code;
                }
            } catch {
                lock (Lock) { Cache[ip] = "—"; }
                return "—";
            }
        }

        public static Task<string> LookupAsync(string ip) {
            ip = NormalizeIp(ip);
            if (string.IsNullOrWhiteSpace(ip)) return Task.FromResult("—");
            lock (Lock) {
                if (Cache.TryGetValue(ip, out var cached)) return Task.FromResult(cached);
            }
            return Task.Run(() => LookupSync(ip));
        }

        static string NormalizeIp(string ip) {
            ip = (ip ?? "").Trim();
            if (!IpRx.IsMatch(ip)) return "";
            return ip;
        }
    }
}
