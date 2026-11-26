using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    // Saved service-specific auth cookies are connection evidence, not an online authorization check.
    internal sealed class SocialAccountChecker {
        readonly Preferences settings;
        readonly TelegramStore telegram;
        readonly LiveStore suppliedLiveStore;
        readonly SemaphoreSlim gate = new SemaphoreSlim(2, 2);
        readonly SemaphoreSlim telegramGate = new SemaphoreSlim(1, 1);

        internal SocialAccountChecker(Preferences settings, TelegramStore telegram = null, LiveStore liveStore = null) {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.telegram = telegram ?? new TelegramStore();
            suppliedLiveStore = liveStore;
        }

        // Compatible with SocialDashboard.CheckSession through (profile, service, ct) => CheckAsync(service, profile, ct).
        // The caller owns batch progress and dashboard observation persistence; Telegram also updates its existing vault.
        internal async Task<DashboardCheckResult> CheckAsync(string service, DolphinBrowserProfile profile,
            CancellationToken ct, IProgress<string> progress = null) {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try {
                ct.ThrowIfCancellationRequested();
                progress?.Report(service + " · " + profile.Name + ": проверка сохранённого подключения…");
                DashboardCheckResult result;
                switch (service) {
                    case "Telegram":
                        await telegramGate.WaitAsync(ct).ConfigureAwait(false);
                        try { result = await CheckTelegram(profile, ct, progress).ConfigureAwait(false); }
                        finally { telegramGate.Release(); }
                        break;
                    case "YouTube":
                    case "TikTok":
                    case "Instagram":
                    case "Threads":
                        using(var cookies=new DolphinCookieSessionProvider()) {
                            var saved=await cookies.ReadAsync(WindowsSupport.Unprotect(settings.ProtectedDolphinToken),profile.Id,ct).ConfigureAwait(false);
                            result=saved[service];
                        }
                        if(service=="YouTube") {
                            var oauth=await CheckYouTube(profile,ct,progress).ConfigureAwait(false);
                            if(oauth.Authorized==true)result=oauth;
                            else if(result.Authorized!=true)result.Detail+="\n"+oauth.Detail;
                        }
                        break;
                    default: throw new ArgumentException("Неизвестный сервис: " + service, nameof(service));
                }
                ct.ThrowIfCancellationRequested();
                return result;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) {
                ct.ThrowIfCancellationRequested();
                // Do not include raw API/worker payloads, tokens, or proxy credentials in dashboard details.
                AppDiagnostics.Write(service, profile.Id, "", "session_check_error", error.GetType().Name, "error");
                return Unknown("Проверка не завершена (" + error.GetType().Name + "). Состояние входа неизвестно; это не подтверждение выхода.", true);
            } finally { gate.Release(); }
        }

        async Task<DashboardCheckResult> CheckWebView(string service,DolphinBrowserProfile profile,CancellationToken ct,IProgress<string> progress) {
            bool tiktok=service=="TikTok";
            if(tiktok?TikTokWebWindow.IsProfileOpen(profile.Id):InstagramWebWindow.IsProfileOpen(profile.Id))
                return Unknown("Этот профиль уже открыт или проверяется. Открытое окно не изменялось.");
            string root=Path.Combine(Store.Root,tiktok?"tiktok":"instagram","web",profile.Id);
            if(!System.Text.RegularExpressions.Regex.IsMatch(profile.Id??"",@"^\d+$")||!Directory.Exists(root))
                return Unknown("Нет сохранённой локальной WebView2-сессии приложения. Сессия Dolphin отдельно не проверялась.");
            var route=await TelegramProxyCatalog.FetchDolphin(WindowsSupport.Unprotect(settings.ProtectedDolphinToken),
                new[]{new KeyValuePair<string,string>(profile.Id,profile.Name)},includeCatalog:false).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var proxy=route.Proxies.SingleOrDefault();
            if(proxy==null)return Unknown("Назначенный профилю прокси не получен; прямое подключение запрещено.",true);
            progress?.Report(service+" · "+profile.Name+": фоновая загрузка сохранённой WebView2-сессии…");
            var completion=new TaskCompletionSource<DashboardCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>{
                using(var dispatcher=new System.Windows.Forms.Control()) {
                    var handle=dispatcher.Handle;
                    System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    dispatcher.BeginInvoke(new Action(async()=>{
                        try {
                            ct.ThrowIfCancellationRequested();
                            if(tiktok) {
                                using(var window=new TikTokWebWindow(profile.Id,profile.Name,proxy.url))
                                    completion.TrySetResult(await window.CheckBackground(ct));
                            } else {
                                using(var window=new InstagramWebWindow(profile.Id,profile.Name,proxy.url))
                                    completion.TrySetResult(await window.CheckBackground(service,ct));
                            }
                        } catch(OperationCanceledException) when(ct.IsCancellationRequested){completion.TrySetCanceled();}
                        catch(Exception error){completion.TrySetException(error);}
                        finally{System.Windows.Forms.Application.ExitThread();}
                    }));
                    System.Windows.Forms.Application.Run();
                }
            });
            thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
            return await completion.Task.ConfigureAwait(false);
        }

        async Task<DashboardCheckResult> CheckYouTube(DolphinBrowserProfile profile, CancellationToken ct, IProgress<string> progress) {
            var channels = (settings.YouTubeChannels ?? new List<YouTubeChannel>())
                .Where(c => c != null && string.Equals((c.ProfileId ?? "").Trim(), profile.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (channels.Count == 0) return Unknown("Нет добавленного YouTube-канала для этого профиля. Вход Dolphin не проверен.");
            var liveStore = suppliedLiveStore ?? new LiveStore();
            var details = new List<string>();
            var verified = new List<string>();
            bool hasError = false;
            foreach (var channel in channels) {
                ct.ThrowIfCancellationRequested();
                var account = liveStore.Account(channel.ChannelId);
                if (account == null || account.Transport == "studio" || string.IsNullOrWhiteSpace(account.ProtectedRefreshToken)
                    || string.IsNullOrWhiteSpace(account.RemoteId) || string.IsNullOrWhiteSpace(liveStore.Config.ClientId)) {
                    details.Add(channel.Name + ": нет сохранённого OAuth-подключения; сессия Dolphin не проверена.");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(account.ProfileId) && account.ProfileId != profile.Id) {
                    details.Add(channel.Name + ": OAuth-запись связана с другим профилем; проверка пропущена.");
                    continue;
                }
                progress?.Report("YouTube · " + channel.Name + ": проверяю сохранённое OAuth-подключение…");
                try {
                    // Documented channels.list: mine=true requires user authorization and returns owned channel IDs.
                    // Reuses existing refresh-token handling; no OAuth consent, browser launch or write request to YouTube.
                    using (var api = new YouTubeLiveApi(liveStore, account))
                        await api.VerifyChannel(account.RemoteId, "", ct).ConfigureAwait(false);
                    verified.Add(channel.Name);
                    details.Add(channel.Name + ": YouTube API подтвердил канал " + account.RemoteId + " через OAuth. Это не проверка входа браузера Dolphin.");
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) {
                    hasError = true;
                    var apiError = error as LiveApiException;
                    details.Add(channel.Name + ": OAuth не подтверждён (" + (apiError == null ? error.GetType().Name : "HTTP " + apiError.StatusCode) + "); состояние браузерного входа неизвестно.");
                    AppDiagnostics.Write("YouTube", profile.Id, "", "session_check_error", error.GetType().Name, "error");
                }
            }
            return new DashboardCheckResult {
                Authorized = verified.Count > 0 ? (bool?)true : null, Source = "oauth",
                AccountName = string.Join(", ", verified),
                Detail = string.Join(Environment.NewLine, details),
                IsError = hasError
            };
        }

        async Task<DashboardCheckResult> CheckTelegram(DolphinBrowserProfile profile, CancellationToken ct, IProgress<string> progress) {
            var data = telegram.Load();
            string key = "dolphin:profile:" + profile.Id;
            // Match explicit profile ownership, never a shared proxy URL.
            var proxyIds = new HashSet<string>(data.proxies.Where(p => p.source_id == key
                || (p.source_ids ?? new List<string>()).Contains(key)).Select(p => p.id));
            var accounts = data.accounts.Where(a => proxyIds.Contains(a.proxy_id)).ToList();
            if (accounts.Count == 0) return Unknown("Нет аккаунта Telegram с явной привязкой к этому Dolphin-профилю. Существование сессии не проверено.");
            if (!TelegramRunner.Prepared(telegram)) return Unknown("Telegram не подготовлен. Подготовьте существующий Telegram runtime в настройках; автоматическая установка не выполнялась.", true);

            var runner = new TelegramRunner(telegram);
            var results = new Dictionary<string, string>();
            var details = new List<string>();
            bool complete = false, workerError = false;
            var job = new Dictionary<string, object> { { "action", "check" }, { "accounts", accounts } };
            var running = runner.Run(job, ev => {
                string kind = Value(ev, "event");
                if (kind == "complete") complete = true;
                if (kind == "error") workerError = true;
                if (kind == "account") {
                    string id = Value(ev, "id"), state = Value(ev, "status");
                    var account = accounts.FirstOrDefault(a => a.id == id);
                    if (account == null) throw new InvalidDataException("Telegram вернул неизвестный аккаунт.");
                    telegram.Change(d => TelegramHealth.ApplyAccount(d, ev));
                    results[id] = state;
                    details.Add(account.name + ": " + TelegramStore.Status(state));
                    progress?.Report("Telegram · " + account.name + ": " + TelegramStore.Status(state));
                }
                if (kind == "proxy") telegram.Change(d => TelegramHealth.ApplyProxy(d, ev));
            });
            // Stop the actual worker on cancellation, then await cleanup before another profile can start.
            using (var cancelWait = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                var cancelled = Task.Delay(Timeout.Infinite, cancelWait.Token);
                try {
                    if (await Task.WhenAny(running, cancelled).ConfigureAwait(false) != running) {
                        await runner.Stop().ConfigureAwait(false);
                        try { await running.ConfigureAwait(false); } catch (Exception) when (ct.IsCancellationRequested) { }
                        ct.ThrowIfCancellationRequested();
                    }
                    int exit = await running.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (exit != 0 || !complete || workerError || results.Count != accounts.Count)
                        return Unknown("Telegram-worker не подтвердил завершение всех проверок. Состояние входа неизвестно.", true);
                } finally { cancelWait.Cancel(); }
            }
            var ready = accounts.Where(a => results[a.id] == "ready").ToList();
            bool allUnauthorized = accounts.All(a => results[a.id] == "unauthorized");
            bool errors = results.Values.Any(s => s != "ready" && s != "unauthorized");
            return new DashboardCheckResult {
                Authorized = ready.Count > 0 ? (bool?)true : allUnauthorized ? (bool?)false : null,
                AccountName = string.Join(", ", ready.Select(a => a.name)),
                Detail = "MTProto: is_user_authorized + get_me. Подтверждено аккаунтов: " + ready.Count + "/" + accounts.Count + ".\n" + string.Join(Environment.NewLine, details),
                IsError = errors
            };
        }

        static string Value(Dictionary<string, object> value, string key) => value.TryGetValue(key, out var item) ? Convert.ToString(item) : "";
        static DashboardCheckResult Unknown(string detail, bool error = false) => new DashboardCheckResult { Authorized = null, Detail = detail, IsError = error };
    }
}
