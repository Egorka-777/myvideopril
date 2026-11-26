using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.WinForms;

namespace VideoBatch {
    internal static class SocialFeedWarmup {
        internal const int SessionSeconds = 15 * 60;
        internal static string FeedUrl(string service) {
            switch (service) {
                case "TikTok": return "https://www.tiktok.com/";
                case "Instagram": return "https://www.instagram.com/";
                case "Threads": return "https://www.threads.com/";
                default: throw new ArgumentException("Неизвестная лента: " + service);
            }
        }
        internal static bool OwnOrigin(string service, string source) {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443) return false;
            var domains = service == "TikTok" ? new[] { "tiktok.com" } : service == "Instagram" ? new[] { "instagram.com" } : service == "Threads" ? new[] { "threads.com", "threads.net" } : new string[0];
            foreach (string domain in domains) if (uri.Host == domain || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        internal static async Task RunAsync(WebView2 view, string service, CancellationToken ct, Action<string> report) {
            FeedUrl(service);
            var startup = Stopwatch.StartNew();
            while (!view.IsDisposed && view.CoreWebView2 == null && startup.Elapsed.TotalSeconds < 35) await Task.Delay(250, ct);
            ct.ThrowIfCancellationRequested();
            if (view.IsDisposed || view.CoreWebView2 == null) throw new InvalidOperationException("Браузер прогрева не готов.");
            var session = await BrowserSessionProbe.ReadAsync(view, service, ct);
            if (session.Authorized != true) throw new InvalidOperationException(session.Detail);
            var json = new JavaScriptSerializer();
            string scripts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "uploader");
            string planner = File.ReadAllText(Path.Combine(scripts, "warmup-watch-plan.js"));
            string feed = File.ReadAllText(Path.Combine(scripts, "social-feed-warmup.js"));
            await view.CoreWebView2.ExecuteScriptAsync(planner + "\n" + feed);
            async Task<Dictionary<string, object>> Call(string action, int remaining = 0) {
                ct.ThrowIfCancellationRequested();
                if (view.IsDisposed || view.CoreWebView2 == null) throw new InvalidOperationException("Окно прогрева закрыто.");
                if (!OwnOrigin(service, view.CoreWebView2.Source)) throw new InvalidOperationException("Лента покинула выбранный сервис.");
                if (action == "read") {
                    var probe = json.Deserialize<Dictionary<string, object>>(await view.CoreWebView2.ExecuteScriptAsync(BrowserSessionProbe.Script(service)));
                    string status = Convert.ToString(probe["state"]);
                    if (status == "manual" || status == "signed_out") throw new InvalidOperationException("Сайт требует вход или ручную проверку; прогрев остановлен.");
                }
                string script = "(async()=>{try{if(!globalThis.videoBatchFeedWarmup)throw Error('Страница ленты изменилась.');return {ok:true,value:await videoBatchFeedWarmup(" + json.Serialize(action) + "," + remaining + ")};}catch(e){return {ok:false,error:e.message};}})()";
                // ExecuteScriptAsync does not await JavaScript promises. Resolve through a polled result slot.
                await view.CoreWebView2.ExecuteScriptAsync("globalThis.videoBatchWarmupResult=null;" + script + ".then(r=>globalThis.videoBatchWarmupResult=r)");
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed.TotalSeconds < 10) {
                    ct.ThrowIfCancellationRequested();
                    string raw = await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify(globalThis.videoBatchWarmupResult)");
                    string serialized = json.Deserialize<string>(raw);
                    if (serialized != null && serialized != "null") {
                        var envelope = json.Deserialize<Dictionary<string, object>>(serialized);
                        if (!Convert.ToBoolean(envelope["ok"])) throw new InvalidOperationException(Convert.ToString(envelope["error"]));
                        return envelope["value"] as Dictionary<string, object>;
                    }
                    await Task.Delay(100, ct);
                }
                throw new TimeoutException("Лента не ответила на действие прогрева.");
            }
            var random = new Random();
            var clock = Stopwatch.StartNew();
            int viewed = 0;
            report(service + ": 15 минут просмотра ленты; без поиска, лайков, комментариев и подписок.");
            try {
                while (clock.Elapsed.TotalSeconds < SessionSeconds) {
                    ct.ThrowIfCancellationRequested();
                    int remaining = Math.Max(0, SessionSeconds - (int)clock.Elapsed.TotalSeconds);
                    var state = await Call("read");
                    var loading = Stopwatch.StartNew();
                    while (state == null || !(Convert.ToBoolean(state["media"]) ? Convert.ToBoolean(state["ready"]) : service == "Threads" && Convert.ToBoolean(state["article"]))) {
                        if (clock.Elapsed.TotalSeconds >= SessionSeconds) throw new TimeoutException("Время сеанса истекло во время загрузки ленты; просмотр не подтверждён.");
                        if (loading.Elapsed.TotalSeconds >= 35) throw new InvalidOperationException("Лента не показала готовое видео или публикацию за 35 секунд.");
                        await Task.Delay(500, ct); state = await Call("read");
                    }
                    bool video = Convert.ToBoolean(state["media"]);
                    var plan = video ? await Call("plan", remaining) : null;
                    double seconds = plan == null ? 5 + random.NextDouble() * 15 : Convert.ToDouble(plan["seconds"]);
                    seconds = Math.Min(seconds, SessionSeconds - clock.Elapsed.TotalSeconds);
                    double startWatched = video ? Convert.ToDouble(state["watched"]) : 0;
                    var slot = Stopwatch.StartNew(); var stalled = Stopwatch.StartNew(); double previous = startWatched;
                    if (video) await Call("play");
                    bool pause = video && random.NextDouble() < .25, paused = false;
                    while (slot.Elapsed.TotalSeconds < seconds && clock.Elapsed.TotalSeconds < SessionSeconds) {
                        await Task.Delay(500, ct);
                        state = await Call("read");
                        if (video) {
                            if (state == null || !Convert.ToBoolean(state["media"])) throw new InvalidOperationException("Видеоплеер ленты исчез.");
                            double actual = Convert.ToDouble(state["watched"]);
                            if (actual > previous) { stalled.Restart(); previous = actual; }
                            if (Convert.ToBoolean(state["ended"])) break;
                            if (stalled.Elapsed.TotalSeconds > 15) throw new InvalidOperationException("Воспроизведение не продвигается более 15 секунд.");
                            if (pause && !paused && slot.Elapsed.TotalSeconds > seconds / 2) {
                                await Call("pause"); paused = true;
                                await Task.Delay(Math.Min(remaining * 1000, 500 + random.Next(2000)), ct);
                                await Call("play");
                            }
                        }
                    }
                    if (!video || previous - startWatched >= (Convert.ToDouble(state["duration"]) > 0 ? Math.Min(5, Convert.ToDouble(state["duration"])) : 5)) viewed++;
                    report(service + ": осталось " + TimeSpan.FromSeconds(Math.Max(0, SessionSeconds - clock.Elapsed.TotalSeconds)).ToString(@"mm\:ss") + " · просмотрено " + viewed);
                    if (clock.Elapsed.TotalSeconds < SessionSeconds) { await Call("scroll"); await Task.Delay(500 + random.Next(1000), ct); }
                }
                if (viewed == 0) throw new InvalidOperationException("За сеанс не подтверждён ни один просмотр; прогрев не засчитан.");
                report(service + ": сеанс завершён · просмотрено " + viewed + ".");
            } finally {
                // Cleanup only: do not mask the original error or cancellation.
                if (!view.IsDisposed && view.CoreWebView2 != null && OwnOrigin(service, view.CoreWebView2.Source)) {
                    try { await view.CoreWebView2.ExecuteScriptAsync("if(globalThis.videoBatchFeedWarmup)videoBatchFeedWarmup('pause').catch(()=>{});"); }
                    catch (Exception error) { report("Не удалось остановить плеер: " + error.Message); }
                }
            }
        }
    }
}
