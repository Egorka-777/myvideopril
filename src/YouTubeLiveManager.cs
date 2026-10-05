using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public sealed class YouTubeLiveManager {
        sealed class Session {
            public YouTubeChannel Channel;
            public LiveAccount Account;
            public LiveJournalEntry Journal;
            public CancellationTokenSource Cancel = new CancellationTokenSource();
            public Task Work;
            public ILiveEncoder Encoder;
        }
        readonly object gate = new object();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly Dictionary<string, LiveView> views = new Dictionary<string, LiveView>();
        readonly List<Task> batches = new List<Task>();
        readonly SemaphoreSlim recoveryGate = new SemaphoreSlim(1, 1);
        readonly Func<LiveAccount, IYouTubeLiveApi> apiFactory;
        readonly Func<LiveOptions, CancellationToken, Task<LivePlaylist>> prepareOverride;
        readonly Func<LivePlaylist, double, string, ILiveEncoder> encoderOverride;
        public LiveStore Store { get; }
        public event Action Changed;
        public event Action<string> LogLine;
        public bool HasBusy { get { lock (gate) return sessions.Count > 0 || views.Values.Any(v => v.Busy); } }
        public LiveView View(string id) { lock (gate) { LiveView value; return views.TryGetValue(id ?? "", out value) ? value : null; } }
        public static int PollSeconds(int channels) => Math.Max(60, channels * 60);
        int LivePollSeconds() { lock (gate) return PollSeconds(sessions.Count); }
        public YouTubeLiveManager(LiveStore store, Func<LiveAccount, IYouTubeLiveApi> apiFactory = null,
            Func<LiveOptions, CancellationToken, Task<LivePlaylist>> prepare = null, Func<LivePlaylist, double, string, ILiveEncoder> encoder = null) {
            Store = store; this.apiFactory = apiFactory ?? (a => new YouTubeLiveApi(store, a)); prepareOverride = prepare; encoderOverride = encoder;
            foreach (var entry in store.PendingSnapshot()) views[entry.LocalId] = new LiveView {
                LocalId = entry.LocalId, Name = store.Account(entry.LocalId)?.Name ?? entry.LocalId, Phase = LivePhase.NeedsCleanup,
                Url = string.IsNullOrEmpty(entry.BroadcastId) ? "" : "https://www.youtube.com/watch?v=" + entry.BroadcastId,
                Detail = "Прошлый запуск не завершён подтверждённо. Нажмите «Завершить эфир»." };
        }
        void State(Session s, LivePhase phase, string detail = "") {
            lock (gate) views[s.Channel.ChannelId] = new LiveView { LocalId = s.Channel.ChannelId, Name = s.Channel.Name,
                Phase = phase, Detail = detail, Url = string.IsNullOrEmpty(s.Journal.BroadcastId) ? "" : "https://www.youtube.com/watch?v=" + s.Journal.BroadcastId };
            Log(s.Channel.Name + " · " + View(s.Channel.ChannelId).Label + (string.IsNullOrEmpty(detail) ? "" : " · " + detail));
            Changed?.Invoke();
        }
        void Log(string line) => LogLine?.Invoke("ЭФИР · " + line);
        public void Start(IReadOnlyList<YouTubeChannel> channels, LiveOptions options) {
            options.Validate();
            if (channels == null || channels.Count == 0) throw new InvalidOperationException("Отметьте каналы галочками.");
            string ffmpeg = "", ffprobe = "", hint = "";
            if (prepareOverride == null && !ToolsLocator.TryResolve(out ffmpeg, out ffprobe, out hint)) throw new InvalidOperationException(hint);
            var batch = new List<Session>();
            var prepCancel = new CancellationTokenSource();
            lock (gate) {
                var remoteIds = new HashSet<string>();
                foreach (var channel in channels) {
                    if (string.IsNullOrWhiteSpace(channel.ChannelId)) throw new InvalidOperationException("У канала отсутствует локальный ID.");
                    var account = Store.Account(channel.ChannelId);
                    if (account == null || string.IsNullOrEmpty(account.RemoteId) || string.IsNullOrEmpty(account.ProtectedRefreshToken))
                        throw new InvalidOperationException("Сначала подключите YouTube API: " + channel.Name);
                    if (!remoteIds.Add(account.RemoteId)) throw new InvalidOperationException("Две строки подключены к одному YouTube-каналу. Выберите только одну.");
                    if (sessions.ContainsKey(channel.ChannelId) || (views.ContainsKey(channel.ChannelId) && views[channel.ChannelId].Busy) || sessions.Values.Any(s => s.Account.RemoteId == account.RemoteId)
                        || Store.PendingSnapshot().Any(e => e.RemoteId == account.RemoteId)) throw new InvalidOperationException("Эфир уже запускается, работает или требует завершения: " + channel.Name);
                    batch.Add(new Session { Channel = channel, Account = account, Journal = new LiveJournalEntry {
                        LocalId = channel.ChannelId, RemoteId = account.RemoteId, OperationId = Guid.NewGuid().ToString("N") } });
                }
                foreach (var s in batch) { sessions.Add(s.Channel.ChannelId, s); State(s, LivePhase.Preparing); }
                // The playlist is prepared once, shared by all channels; no encoding per channel while live.
                var prep = Task.Run(() => prepareOverride != null ? prepareOverride(options, prepCancel.Token)
                    : YouTubeLiveMedia.Prepare(options, ffmpeg, ffprobe, Log, prepCancel.Token));
                foreach (var s in batch) s.Work = Run(s, options, ffmpeg, prep);
                batches.RemoveAll(t => t.IsCompleted);
                batches.Add(ReleaseBatch(batch, prep, prepCancel));
            }
        }
        async Task ReleaseBatch(List<Session> batch, Task<LivePlaylist> prep, CancellationTokenSource prepCancel) {
            var registrations = batch.Select(s => s.Cancel.Token.Register(() => { if (batch.All(x => x.Cancel.IsCancellationRequested)) prepCancel.Cancel(); })).ToArray();
            try {
                await Task.WhenAll(batch.Select(s => s.Work)).ConfigureAwait(false);
                prepCancel.Cancel();
                try { var playlist = await prep.ConfigureAwait(false); playlist.Dispose(); }
                catch (OperationCanceledException) { }
                catch (Exception e) { Log("Подготовка/очистка видео: " + e.Message); }
            } finally {
                foreach (var registration in registrations) registration.Dispose();
                foreach (var s in batch) s.Cancel.Dispose();
                prepCancel.Dispose();
            }
        }
        static async Task<T> AwaitCancelable<T>(Task<T> work, CancellationToken ct) {
            var canceled = new TaskCompletionSource<bool>();
            using (ct.Register(() => canceled.TrySetResult(true))) {
                if (await Task.WhenAny(work, canceled.Task).ConfigureAwait(false) != work) ct.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested(); return await work.ConfigureAwait(false);
            }
        }
        async Task Run(Session s, LiveOptions options, string ffmpeg, Task<LivePlaylist> prepared) {
            string failure = null;
            bool journaled = false;
            IYouTubeLiveApi api = null;
            try {
                api = apiFactory(s.Account);
                await api.VerifyChannel(s.Account.RemoteId, s.Channel.ChannelUrl, s.Cancel.Token).ConfigureAwait(false);
                var playlist = await AwaitCancelable(prepared, s.Cancel.Token).ConfigureAwait(false);
                State(s, LivePhase.Starting);
                s.Journal.BroadcastAttempted = true;
                Store.Journal(s.Journal); journaled = true;
                try { s.Journal.BroadcastId = await api.CreateBroadcast(options, YouTubeLiveApi.Marker(s.Journal.OperationId), s.Cancel.Token).ConfigureAwait(false); }
                catch (LiveApiException e) when (e.StatusCode < 500) { s.Journal.BroadcastAttempted = false; Store.Journal(s.Journal); throw; }
                Store.Journal(s.Journal);
                s.Journal.StreamAttempted = true; Store.Journal(s.Journal);
                Dictionary<string, object> stream;
                try { stream = await api.CreateStream(options, YouTubeLiveApi.Marker(s.Journal.OperationId), s.Cancel.Token).ConfigureAwait(false); }
                catch (LiveApiException e) when (e.StatusCode < 500) { s.Journal.StreamAttempted = false; Store.Journal(s.Journal); throw; }
                s.Journal.StreamId = LiveJson.Text(stream, "id");
                if (string.IsNullOrEmpty(s.Journal.StreamId)) throw new InvalidOperationException("YouTube не вернул ID видеопотока.");
                Store.Journal(s.Journal);
                var ingestionInfo = LiveJson.Get(LiveJson.Get(stream, "cdn"), "ingestionInfo");
                string address = LiveJson.Text(ingestionInfo, "rtmpsIngestionAddress"), key = LiveJson.Text(ingestionInfo, "streamName");
                Uri url;
                if (string.IsNullOrEmpty(key) || !Uri.TryCreate(address, UriKind.Absolute, out url) || url.Scheme != "rtmps"
                    || !(url.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) || url.Host == "youtube.com"))
                    throw new InvalidOperationException("YouTube не вернул корректный RTMPS-адрес видеопотока.");
                string ingestion = address.TrimEnd('/') + "/" + key;
                await api.Configure(s.Journal.BroadcastId, s.Journal.StreamId, options, s.Cancel.Token).ConfigureAwait(false);
                double offset = 0;
                int restarts = 0;
                while (true) {
                    s.Cancel.Token.ThrowIfCancellationRequested();
                    s.Encoder = encoderOverride != null ? encoderOverride(playlist, offset, ingestion)
                        : new LiveEncoder(ffmpeg, YouTubeLiveMedia.StreamArguments(playlist, true, offset, ingestion), ingestion);
                    var started = DateTime.UtcNow;
                    var nextCheck = DateTime.MinValue;
                    bool live = false;
                    int missedChecks = 0;
                    while (!s.Encoder.HasExited) {
                        s.Cancel.Token.ThrowIfCancellationRequested();
                        if ((DateTime.UtcNow - s.Encoder.LastProgress).TotalSeconds > 90) break;
                        if (DateTime.UtcNow < nextCheck) { await Task.Delay(1000, s.Cancel.Token).ConfigureAwait(false); continue; }
                        try {
                            string broadcast = await api.BroadcastState(s.Journal.BroadcastId, s.Cancel.Token).ConfigureAwait(false);
                            if (broadcast == "complete" || broadcast == "missing" || broadcast == "revoked") throw new InvalidOperationException("Эфир завершён на стороне YouTube.");
                            string streamState = await api.StreamState(s.Journal.StreamId, s.Cancel.Token).ConfigureAwait(false);
                            missedChecks = 0;
                            bool confirmed = broadcast == "live" && streamState == "active";
                            if (confirmed) { State(s, LivePhase.Live, "Подтверждено YouTube " + DateTime.Now.ToString("HH:mm:ss")); live = true; started = DateTime.UtcNow; }
                            if (!confirmed && live) { State(s, LivePhase.Reconnecting, "YouTube не подтверждает активную передачу."); live = false; started = DateTime.UtcNow; }
                        } catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !s.Cancel.IsCancellationRequested) || (e is LiveApiException && ((LiveApiException)e).StatusCode >= 500)) {
                            if (live) started = DateTime.UtcNow;
                            live = false; missedChecks++;
                            State(s, LivePhase.Reconnecting, "Нет подтверждения от YouTube; проверка " + missedChecks);
                            if (missedChecks >= 3) throw;
                        }
                        if (!live && (DateTime.UtcNow - started).TotalSeconds > 180) throw new InvalidOperationException("YouTube не подтвердил запуск за 3 минуты.");
                        nextCheck = DateTime.UtcNow.AddSeconds(live ? LivePollSeconds() : 10);
                        await Task.Delay(1000, s.Cancel.Token).ConfigureAwait(false);
                    }
                    double advanced = s.Encoder.Seconds;
                    string encoderError = s.Encoder.Error;
                    await s.Encoder.Stop().ConfigureAwait(false); s.Encoder.Dispose(); s.Encoder = null;
                    if (++restarts > 3) throw new InvalidOperationException("Передача видео прерывается: " + encoderError);
                    if (await api.BroadcastState(s.Journal.BroadcastId, s.Cancel.Token).ConfigureAwait(false) == "complete") throw new InvalidOperationException("YouTube завершил эфир после разрыва связи.");
                    offset = (offset + advanced) % playlist.Duration;
                    State(s, LivePhase.Reconnecting, "Повтор передачи " + restarts + "/3 в тот же эфир.");
                    await Task.Delay(TimeSpan.FromSeconds(2 * restarts), s.Cancel.Token).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) when (s.Cancel.IsCancellationRequested) { }
            catch (Exception e) { failure = e.Message; }
            finally {
                State(s, LivePhase.Stopping, failure ?? "");
                try {
                    if (s.Encoder != null) { await s.Encoder.Stop().ConfigureAwait(false); s.Encoder.Dispose(); s.Encoder = null; }
                    if (journaled) {
                        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(90))) {
                            if (api == null) api = apiFactory(s.Account);
                            await api.Complete(s.Journal, cleanup.Token).ConfigureAwait(false);
                        }
                        Store.Journal(s.Journal, true);
                    }
                    State(s, failure == null ? LivePhase.Finished : LivePhase.Error, failure ?? "");
                } catch (Exception e) {
                    State(s, LivePhase.NeedsCleanup, (failure == null ? "" : failure + " · ") + "Завершение не подтверждено: " + e.Message);
                } finally {
                    api?.Dispose();
                    lock (gate) sessions.Remove(s.Channel.ChannelId);
                    Changed?.Invoke();
                }
            }
        }
        public async Task Stop(string localId) {
            Session session;
            lock (gate) { sessions.TryGetValue(localId, out session); session?.Cancel.Cancel(); }
            if (session != null) { await session.Work.ConfigureAwait(false); return; }
            await recoveryGate.WaitAsync().ConfigureAwait(false);
            try {
                foreach (var entry in Store.PendingSnapshot().Where(e => e.LocalId == localId)) {
                    var account = Store.Account(localId);
                    if (account == null || account.RemoteId != entry.RemoteId) throw new InvalidOperationException("Подключите прежний канал для завершения старого эфира.");
                    using (var api = apiFactory(account)) using (var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90))) {
                        await api.Complete(entry, ct.Token).ConfigureAwait(false); Store.Journal(entry, true);
                    }
                }
                lock (gate) if (views.ContainsKey(localId)) { var old = views[localId]; views[localId] = new LiveView { LocalId = old.LocalId, Name = old.Name, Phase = LivePhase.Finished, Url = old.Url }; }
                Changed?.Invoke();
            } finally { recoveryGate.Release(); }
        }
        public async Task StopAll() {
            string[] ids;
            Session[] running;
            lock (gate) { ids = sessions.Keys.Concat(Store.PendingSnapshot().Select(e => e.LocalId)).Distinct().ToArray(); running = sessions.Values.ToArray(); }
            // Cancellation can synchronously finish a session and remove it. Never enumerate the live dictionary while cancelling.
            foreach (var s in running) try { s.Cancel.Cancel(); } catch (ObjectDisposedException) { /* This session has already completed. */ }
            var errors = new List<string>();
            foreach (var id in ids) try { await Stop(id).ConfigureAwait(false); } catch (Exception e) { errors.Add(e.Message); }
            Task[] cleanupTasks; lock (gate) cleanupTasks = batches.ToArray();
            await Task.WhenAll(cleanupTasks).ConfigureAwait(false);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            if (HasBusy) throw new InvalidOperationException("Не все эфиры завершены подтверждённо. Повторите завершение или проверьте YouTube Studio.");
        }
    }
}
