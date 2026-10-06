using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    // No real Google accounts or network are used by these tests.
    public static class YouTubeLiveSelfTests {
        public static bool RunUiSelfTests(string outputDirectory) {
            Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "UI test requires STA Windows PowerShell");
            string root = Path.Combine(Path.GetTempPath(), "videobatch-live-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Directory.CreateDirectory(outputDirectory);
            string previousRoot = Store.Root; Store.Root = root;
            try {
                var settings = new Preferences(); Store.Normalize(settings);
                settings.YouTubeChannels.Add(new YouTubeChannel { ChannelId = "ui-test", Name = "Тестовый YouTube-канал", ProfileId = "123456", Market = "RU" });
                using (var backend = new YouTubeBackend(settings)) using (var host = new System.Windows.Forms.Form { Width = 1500, Height = 950 })
                using (var panel = new YouTubeWorkspacePanel(settings, backend, new NavigationService())) {
                    host.Controls.Add(panel); host.Show(); System.Windows.Forms.Application.DoEvents();
                    using (var bmp = new System.Drawing.Bitmap(host.Width, host.Height)) { host.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(Path.Combine(outputDirectory, "live-youtube.png")); }
                    host.Hide();
                }
                using (var dialog = new YouTubeLiveDialog(new YouTubeLiveManager(new LiveStore(root)), settings.YouTubeChannels)) {
                    dialog.Show(); System.Windows.Forms.Application.DoEvents();
                    using (var bmp = new System.Drawing.Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(Path.Combine(outputDirectory, "live-dialog.png")); }
                    dialog.Close();
                }
                return true;
            } finally { Store.Root = previousRoot; Directory.Delete(root, true); }
        }
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException("Live self-test: " + message); }
        static async Task Until(Func<bool> condition) {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Live self-test timeout"); await Task.Delay(20).ConfigureAwait(false); }
        }
        sealed class FakeApi : IYouTubeLiveApi {
            public int Creates, Completes;
            public bool FailCreate, FailComplete;
            public Task VerifyChannel(string id, string url, CancellationToken ct) => Task.CompletedTask;
            public Task<string> CreateBroadcast(LiveOptions options, string marker, CancellationToken ct) {
                Creates++; if (FailCreate) throw new HttpRequestException("Response lost after creation"); return Task.FromResult("broadcast");
            }
            public Task<Dictionary<string, object>> CreateStream(LiveOptions options, string marker, CancellationToken ct) => Task.FromResult(LiveJson.Object("{\"id\":\"stream\",\"cdn\":{\"ingestionInfo\":{\"rtmpsIngestionAddress\":\"rtmps://a.rtmps.youtube.com/live2\",\"streamName\":\"secret-key\"}}}"));
            public Task Configure(string broadcast, string stream, LiveOptions options, CancellationToken ct) => Task.CompletedTask;
            public Task<string> BroadcastState(string id, CancellationToken ct) => Task.FromResult("live");
            public Task<string> StreamState(string id, CancellationToken ct) => Task.FromResult("active");
            public Task Complete(LiveJournalEntry entry, CancellationToken ct) { Completes++; if (FailComplete) throw new HttpRequestException("cleanup unavailable"); return Task.CompletedTask; }
            public void Dispose() { }
        }
        sealed class FakeEncoder : ILiveEncoder {
            bool stopped;
            public bool HasExited => stopped;
            public int ExitCode => 0;
            public double Seconds => 1;
            public DateTime LastProgress => DateTime.UtcNow;
            public string Error => "";
            public Task Stop() { stopped = true; return Task.CompletedTask; }
            public void Dispose() { }
        }
        sealed class Handler : HttpMessageHandler {
            public readonly List<string> Calls = new List<string>();
            public bool LostCreate;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                string path = request.RequestUri.AbsolutePath, query = request.RequestUri.Query;
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                Calls.Add(request.Method + " " + path + query);
                if (path.EndsWith("/token")) return Reply("{\"access_token\":\"test-token\",\"expires_in\":3600}");
                Check(request.Headers.Authorization?.Parameter == "test-token", "Bearer missing");
                if (path.EndsWith("/channels")) return Reply("{\"items\":[{\"id\":\"UCtest\"}]}");
                if (path.EndsWith("/liveBroadcasts") && request.Method == HttpMethod.Post) {
                    var payload = LiveJson.Object(body);
                    Check((bool)LiveJson.Get(LiveJson.Get(payload, "contentDetails"), "enableAutoStart"), "auto start missing");
                    Check((bool)LiveJson.Get(LiveJson.Get(payload, "contentDetails"), "enableAutoStop"), "auto stop missing");
                    if (LostCreate) throw new HttpRequestException("lost POST response");
                    return Reply("{\"id\":\"broadcast\"}");
                }
                if (path.EndsWith("/liveStreams") && request.Method == HttpMethod.Post) {
                    var payload = LiveJson.Get(LiveJson.Object(body), "cdn");
                    Check(LiveJson.Text(payload, "resolution") == "720p" && LiveJson.Text(payload, "frameRate") == "30fps", "wrong ingest format");
                    return Reply("{\"id\":\"stream\"}");
                }
                if (path.EndsWith("/videos") && request.Method == HttpMethod.Get) return Reply("{\"items\":[{\"id\":\"broadcast\",\"snippet\":{\"title\":\"before\",\"description\":\"before\",\"categoryId\":\"22\",\"defaultLanguage\":\"ru\"}}]}");
                if (path.EndsWith("/videos") && request.Method == HttpMethod.Put) {
                    var snippet = LiveJson.Get(LiveJson.Object(body), "snippet");
                    Check(LiveJson.Text(snippet, "categoryId") == "22" && LiveJson.Text(snippet, "defaultLanguage") == "ru", "snippet fields lost");
                    Check(LiveJson.Text(snippet, "title") == "Test" && LiveJson.Array(LiveJson.Get(snippet, "tags")).Length == 2, "metadata missing");
                }
                if (path.EndsWith("/thumbnails/set")) Check(request.Content.Headers.ContentType.MediaType == "image/png" && body.Length > 0, "thumbnail upload invalid");
                return Reply("{}");
            }
            static HttpResponseMessage Reply(string json) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        public static bool RunSelfTests() {
            var task = Task.Run(Run);
            if (!task.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Live tests did not finish within 60 seconds; see last stage.");
            task.GetAwaiter().GetResult(); return true;
        }
        static async Task Run() {
            string root = Path.Combine(Path.GetTempPath(), "videobatch-live-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                string folder = Path.Combine(root, "videos"); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "02.mp4"), "dummy"); File.WriteAllText(Path.Combine(folder, "01.mp4"), "dummy");
                var options = new LiveOptions { Folder = folder, Title = "Test", Tags = new[] { "a", "b" } };
                options.Validate(); Check(Path.GetFileName(options.Files()[0]) == "01.mp4", "playlist order");
                Console.WriteLine("LIVE TEST: input and playlist order passed");
                var store = new LiveStore(root); store.Config.ClientId = "test";
                var a = new LiveAccount { LocalId = "a", RemoteId = "UCtest", ProtectedRefreshToken = WindowsSupport.Protect("test-refresh") };
                var b = new LiveAccount { LocalId = "b", RemoteId = "UCtest2", ProtectedRefreshToken = "fake" };
                store.SaveAccount(a); store.SaveAccount(b);
                var apis = new Dictionary<string, FakeApi> { ["a"] = new FakeApi(), ["b"] = new FakeApi() };
                int prepares = 0;
                Func<LiveOptions, CancellationToken, Task<LivePlaylist>> prepare = (o, ct) => {
                    Interlocked.Increment(ref prepares);
                    string media = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(media);
                    return Task.FromResult(new LivePlaylist { DirectoryPath = media, Path = Path.Combine(media, "list"), Duration = 10 });
                };
                var manager = new YouTubeLiveManager(store, account => apis[account.LocalId], prepare, (p, offset, ingest) => new FakeEncoder());
                var ca = new YouTubeChannel { ChannelId = "a", Name = "A" }; var cb = new YouTubeChannel { ChannelId = "b", Name = "B" };
                manager.Start(new[] { ca, cb }, options);
                await Until(() => manager.View("a")?.Phase == LivePhase.Live && manager.View("b")?.Phase == LivePhase.Live).ConfigureAwait(false);
                bool rejected = false; try { manager.Start(new[] { ca }, options); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && apis["a"].Creates == 1 && prepares == 1, "duplicate launch / shared preparation");
                Console.WriteLine("LIVE TEST: multi-channel launch and duplicate prevention passed");
                await manager.Stop("a").ConfigureAwait(false);
                Check(manager.View("a").Phase == LivePhase.Finished && manager.View("b").Phase == LivePhase.Live, "point stop affected another channel");
                await manager.StopAll().ConfigureAwait(false); Check(!manager.HasBusy && store.PendingSnapshot().Length == 0, "stop all / journal");
                Console.WriteLine("LIVE TEST: point stop and stop all passed");
                apis["a"].FailCreate = true; apis["a"].FailComplete = true;
                manager.Start(new[] { ca }, options);
                await Until(() => manager.View("a")?.Phase == LivePhase.NeedsCleanup).ConfigureAwait(false);
                Check(store.PendingSnapshot().Length == 1 && apis["a"].Creates == 2, "uncertain creation retried or journal lost");
                try { await manager.Stop("a").ConfigureAwait(false); } catch (HttpRequestException) { /* Expected while cleanup is deliberately unavailable. */ }
                await Until(() => apis["a"].Completes >= 2).ConfigureAwait(false);
                var recovered = new YouTubeLiveManager(new LiveStore(root), account => apis[account.LocalId]);
                Check(recovered.HasBusy && recovered.View("a").Phase == LivePhase.NeedsCleanup, "crash recovery missing");
                apis["a"].FailComplete = false;
                await recovered.StopAll().ConfigureAwait(false); Check(!recovered.HasBusy, "recovery cleanup failed");
                await manager.StopAll().ConfigureAwait(false);
                Console.WriteLine("LIVE TEST: lost creation response and crash recovery passed");
                using (var image = new System.Drawing.Bitmap(2, 2)) { options.Thumbnail = Path.Combine(root, "thumb.png"); image.Save(options.Thumbnail, System.Drawing.Imaging.ImageFormat.Png); }
                var handler = new Handler();
                Console.WriteLine("LIVE TEST: begin HTTP contracts");
                using (var api = new YouTubeLiveApi(store, a, handler)) {
                    await api.VerifyChannel("UCtest", "", CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine("LIVE TEST: HTTP channel verification passed");
                    await api.CreateBroadcast(options, "marker", CancellationToken.None).ConfigureAwait(false);
                    await api.CreateStream(options, "marker", CancellationToken.None).ConfigureAwait(false);
                    await api.Configure("broadcast", "stream", options, CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine("LIVE TEST: HTTP metadata and thumbnail passed");
                    handler.LostCreate = true;
                    int previous = handler.Calls.Count(c => c.StartsWith("POST /youtube/v3/liveBroadcasts?"));
                    try { await api.CreateBroadcast(options, "marker", CancellationToken.None).ConfigureAwait(false); } catch (HttpRequestException) { }
                    Check(handler.Calls.Count(c => c.StartsWith("POST /youtube/v3/liveBroadcasts?")) == previous + 1, "POST retried after lost response");
                }
                var args = YouTubeLiveMedia.StreamArguments(new LivePlaylist { Path = "playlist" }, true, 0, "rtmps://test");
                Check(args.Contains("-stream_loop") && args.Contains("-1") && args.Contains("copy"), "infinite copy stream missing");
                Check(YouTubeLiveManager.PollSeconds(1) == 60 && YouTubeLiveManager.PollSeconds(12) == 720, "multi-channel quota polling");
                await SessionContracts().ConfigureAwait(false);
                // A real Windows kernel job: closing its handle must terminate the child process.
                using (var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true })) {
                    using (var owner = new YouTubeLiveProcessOwner(child)) { }
                    Check(child.WaitForExit(5000), "encoder ownership must stop children when app/job closes");
                }
                Console.WriteLine("LIVE TEST: session adapter and Windows child ownership passed");
            } finally { Directory.Delete(root, true); }
        }
        static async Task SessionContracts() {
            var calls = new List<Dictionary<string, object>>();
            var account = new LiveAccount { LocalId = "local", RemoteId = "UCtest", ProfileId = "123", Transport = "studio" };
            Func<Dictionary<string, object>, CancellationToken, Task<Dictionary<string, object>>> runner = (job, ct) => {
                calls.Add(job);
                string command = LiveJson.Text(job, "command");
                var reply = new Dictionary<string, object>();
                if (command == "verify") { reply["channelId"] = "UCtest"; reply["channelName"] = "Session test"; }
                if (command == "create" || command == "ingest") reply["broadcastId"] = "abcdefghijk";
                if (command == "ingest") { reply["address"] = "rtmps://a.rtmps.youtube.com/live2"; reply["key"] = "test-stream-key"; }
                if (command == "status") reply["broadcastState"] = "live";
                if (command == "stream-status") reply["streamState"] = "active";
                return Task.FromResult(reply);
            };
            using (var api = new YouTubeLiveSessionApi(account, new Preferences { DolphinPort = 3001 }, runner)) {
                var ct = CancellationToken.None; var options = new LiveOptions { Title = "Live", Tags = new[] { "one" } };
                string operation = new string('a', 32);
                await api.VerifyChannel("UCtest", "https://youtube.com/channel/UCtest", ct).ConfigureAwait(false);
                Check(api.ChannelId == "UCtest", "session binding identity");
                string id = await api.CreateBroadcast(options, YouTubeLiveApi.Marker(operation), ct).ConfigureAwait(false);
                var stream = await api.CreateStream(options, YouTubeLiveApi.Marker(operation), ct).ConfigureAwait(false);
                Check(LiveJson.Text(stream, "id") == id, "native Studio uses its broadcast handle");
                await api.Configure(id, id, options, ct).ConfigureAwait(false);
                Check(await api.BroadcastState(id, ct).ConfigureAwait(false) == "live", "native broadcast status");
                Check(await api.StreamState(id, ct).ConfigureAwait(false) == "active", "native stream status");
                await api.Complete(new LiveJournalEntry { OperationId = operation, BroadcastId = id, BroadcastAttempted = true }, ct).ConfigureAwait(false);
                Check(calls.Skip(1).All(j => LiveJson.Text(j, "operationId") == operation && LiveJson.Text(j, "profileId") == "123" && LiveJson.Text(j, "expectedChannelId") == "UCtest"), "session ownership lost");
                Check(calls.All(j => !j.ContainsKey("token") && !j.ContainsKey("refresh_token") && !j.ContainsKey("cookie")), "credentials in session jobs");
            }
        }
        public static bool RunMediaSelfTests(string ffmpeg, string ffprobe) {
            Media(ffmpeg, ffprobe).GetAwaiter().GetResult(); return true;
        }
        static async Task Media(string ffmpeg, string ffprobe) {
            string root = Path.Combine(Path.GetTempPath(), "videobatch-live-media-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string previousRoot = Store.Root; Store.Root = root;
            try {
                var ct = CancellationToken.None;
                string a = Path.Combine(root, "01.mp4"), b = Path.Combine(root, "02.mp4");
                await Core.Tool(ffmpeg, new[] { "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-t", "2", "-c:v", "libx264", a }, ct).ConfigureAwait(false);
                await Core.Tool(ffmpeg, new[] { "-y", "-f", "lavfi", "-i", "color=c=blue:size=360x640:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "2", "-c:v", "libx264", "-c:a", "aac", b }, ct).ConfigureAwait(false);
                using (var playlist = await YouTubeLiveMedia.Prepare(new LiveOptions { Folder = root, Title = "test" }, ffmpeg, ffprobe, _ => { }, ct).ConfigureAwait(false)) {
                    string output = Path.Combine(root, "loop.flv");
                    var args = YouTubeLiveMedia.StreamArguments(playlist, true, 0, output).ToList();
                    int re = args.IndexOf("-re"); args.RemoveAt(re);
                    args.InsertRange(args.Count - 3, new[] { "-t", "13", "-y" });
                    await Core.Tool(ffmpeg, args, ct, null, 60).ConfigureAwait(false);
                    var probe = Core.VideoInfo(await Core.Probe(ffprobe, output, ct).ConfigureAwait(false));
                    Check(probe.HasAudio && probe.Height == 720 && probe.Width == 1280 && probe.Duration > 12, "normalization/looping mixed sources");
                    string packets = await Core.Tool(ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=dts_time", "-of", "csv=p=0", output }, ct).ConfigureAwait(false);
                    var times = packets.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => Core.Parse(s)).ToArray();
                    Check(times.Length > 350, "not enough video frames across repeats");
                    for (int i = 1; i < times.Length; i++) Check(times[i] > times[i - 1] && times[i] - times[i - 1] < 0.1, "timestamp pause at file or loop boundary");
                }
            } finally { Store.Root = previousRoot; Directory.Delete(root, true); }
        }
    }
}
