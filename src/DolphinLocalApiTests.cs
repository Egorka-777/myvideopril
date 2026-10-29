using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class DolphinLocalApiTests {
        sealed class Stub : HttpMessageHandler {
            public readonly List<int> Calls = new List<int>();
            public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) {
                Calls.Add(request.RequestUri.Port);
                return Reply(request, cancel);
            }
        }
        static HttpResponseMessage Response(int status, string json) {
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json) };
        }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        public static bool RunSelfTests() { return RunAsync().GetAwaiter().GetResult(); }
        static async Task<bool> RunAsync() {
            var logs = new List<string>();
            string token = "test-token-\"\\\n-never-log";
            var stub = new Stub();
            stub.Reply = async (request, ct) => {
                Check(request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/v1.0/auth/login-with-token", "official auth endpoint");
                var body = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(await request.Content.ReadAsStringAsync().ConfigureAwait(false));
                Check(body["token"] == token, "correctly escaped JSON token");
                if (request.RequestUri.Port == 63464) return Response(401, "{\"success\":false}");
                if (request.RequestUri.Port == 3001) return Response(200, "<html>not the API</html>");
                return Response(200, "{\"success\":true}");
            };
            int port = await DolphinLocalApi.ResolvePortsAsync(token, new[] { 63464, 3001, 55001 }, stub, CancellationToken.None, logs.Add).ConfigureAwait(false);
            Check(port == 55001 && stub.Calls.Count == 3, "continue after 401 and HTML; one POST per candidate");
            Check(!string.Join("\n", logs).Contains(token), "secret excluded from logs");

            var bad = new Stub();
            bad.Reply = (r, ct) => Task.FromResult(Response(200, r.RequestUri.Port == 3001 ? "{}" : "{\"success\":\"true\"}"));
            bool rejected = false;
            try { await DolphinLocalApi.ResolvePortsAsync(token, new[] { 3001, 3002 }, bad, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { rejected = e.Message.Contains("Не удалось подтвердить") && !e.Message.Contains(token); }
            Check(rejected && bad.Calls.Count == 2, "ambiguous 200 is not successful authentication");

            var cancelled = new Stub();
            cancelled.Reply = async (r, ct) => { await Task.Delay(10000, ct).ConfigureAwait(false); return Response(200, "{}"); };
            using (var cts = new CancellationTokenSource(50)) {
                bool stopped = false;
                try { await DolphinLocalApi.ResolvePortsAsync(token, new[] { 3001, 3002 }, cancelled, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped && cancelled.Calls.Count == 1, "cancellation stops without another POST");
            }
            return true;
        }
    }
}
