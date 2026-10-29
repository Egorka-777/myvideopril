using System;
using System.Net;
using System.Net.Http;

namespace VideoBatch {
    /// <summary>HTTP(S) clients for OpenRouter / ElevenLabs (not Dolphin SOCKS).</summary>
    public static class OutboundHttp {
        public static HttpClient Create(string proxyUrl, TimeSpan timeout, bool allowAutoRedirect) {
            var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect, UseProxy = false };
            var proxy = TryCreateWebProxy(proxyUrl);
            if (proxy != null) { handler.UseProxy = true; handler.Proxy = proxy; }
            return new HttpClient(handler) { Timeout = timeout };
        }

        public static WebProxy TryCreateWebProxy(string raw) {
            raw = (raw ?? "").Trim();
            if (raw == "") return null;
            if (!raw.Contains("://")) {
                var parts = raw.Split(new[] { ':' }, 4);
                if (parts.Length == 4)
                    raw = "http://" + Uri.EscapeDataString(parts[2]) + ":" + Uri.EscapeDataString(parts[3]) + "@" + parts[0] + ":" + parts[1];
                else raw = "http://" + raw;
            }
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                throw new Exception("Некорректный адрес прокси для нейросетей.");
            if (uri.Scheme != "http" && uri.Scheme != "https")
                throw new Exception("Для OpenRouter и ElevenLabs нужен HTTP(S)-прокси (CONNECT), например http://login:pass@host:port. SOCKS из Dolphin сюда не подставлять.");
            string address = uri.Scheme == "https"
                ? "http://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port)
                : uri.GetLeftPart(UriPartial.Authority);
            var web = new WebProxy(new Uri(address), false);
            if (!string.IsNullOrEmpty(uri.UserInfo)) {
                var cred = uri.UserInfo.Split(new[] { ':' }, 2);
                web.Credentials = new NetworkCredential(Uri.UnescapeDataString(cred[0]), cred.Length > 1 ? Uri.UnescapeDataString(cred[1]) : "");
            }
            return web;
        }
    }
}
