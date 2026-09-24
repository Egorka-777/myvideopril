using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    /// <summary>Hidden legacy UploadWindow — business logic only, never shown in shell.</summary>
    public sealed class YouTubeBackend : IDisposable {
        public UploadWindow Window { get; }
        public event Action<string> LogLine;

        public YouTubeBackend(Preferences settings) {
            Window = new UploadWindow(settings);
            Window.ShowInTaskbar = false;
            Window.FormBorderStyle = FormBorderStyle.None;
            Window.StartPosition = FormStartPosition.Manual;
            Window.Location = new System.Drawing.Point(-32000, -32000);
            Window.Size = new System.Drawing.Size(1, 1);
            Window.Opacity = 0;
            Window.LogLine += s => LogLine?.Invoke(s);
            var _ = Window.Handle;
        }

        public void Reload() { Window.ReloadFromSettings(); }
        public void SetMarket(string market) { Window.SetMarketView(market); }
        public void SetMarketView(string market) { Window.SetMarketView(market); }
        public void SetKindView(string kind) { Window.SetKindView(kind); }
        public void SelectProfile(string profileId, string market) { Window.SelectProfileById(profileId, market); }
        public void SelectProfileById(string profileId, string market) { Window.SelectProfileById(profileId, market); }
        public Task RunHttpUploadAsync() { return Window.RunHttpUploadAsync(); }
        public Task RunHttpUploadAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels) { return Window.RunHttpUploadAsync(channels); }
        public Task RunStudioUploadAsync() { return Window.RunStudioUploadAsync(); }
        public Task RunStudioUploadAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels) { return Window.RunStudioUploadAsync(channels); }
        public Task RunCheckProfilesAsync() { return Window.RunCheckProfilesAsync(); }
        public Task RunCheckProfilesAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels) { return Window.RunCheckProfilesAsync(channels); }
        public Task RunMeshWatchAsync() { return Window.RunMeshWatchAsync(); }
        public Task RunMeshWatchAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels) { return Window.RunMeshWatchAsync(channels); }
        public Task RunSearchAsync() { return Window.RunYouTubeSearchAsync(); }
        public void Stop() { Window.RequestStopUpload(); }
        public void AssignVideos(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public void AssignVideosToProfile(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public void AssignVideosToChannel(YouTubeChannel channel, string[] files) { Window.AssignVideosToChannel(channel, files); }
        public void SelectChannel(YouTubeChannel channel) { Window.SelectChannel(channel); }
        public bool TrySchedulePreview() { return Window.TryShowSchedulePreview(); }
        public bool TryShowSchedulePreview() { return Window.TryShowSchedulePreview(); }

        public void Dispose() {
            try { Window.Dispose(); } catch { }
        }
    }

    public sealed class TikTokBackend : IDisposable {
        public TikTokUploadWindow Window { get; }
        public event Action<string> LogLine;

        public TikTokBackend(Preferences settings) {
            Window = new TikTokUploadWindow(settings);
            Window.ShowInTaskbar = false;
            Window.FormBorderStyle = FormBorderStyle.None;
            Window.StartPosition = FormStartPosition.Manual;
            Window.Location = new System.Drawing.Point(-32000, -32000);
            Window.Size = new System.Drawing.Size(1, 1);
            Window.Opacity = 0;
            Window.LogLine += s => LogLine?.Invoke(s);
            var _ = Window.Handle;
        }

        public void Reload() { Window.ReloadFromSettings(); }
        public void SetMarket(string market) { Window.SetMarketView(market); }
        public void SetMarketView(string market) { Window.SetMarketView(market); }
        public void SelectProfile(string profileId, string market) { Window.SelectProfileById(profileId, market); }
        public void SelectProfileById(string profileId, string market) { Window.SelectProfileById(profileId, market); }
        public Task RunUploadAsync() { return Window.RunUploadAsync(); }
        public Task RunUploadAsync(IReadOnlyList<TikTokAccount> accounts) { return Window.RunUploadAsync(accounts); }
        public TikTokSyncResult SyncFromYouTube() { return Window.SyncFromYouTube(); }
        public Task RunCheckProfilesAsync() { return Window.RunCheckProfilesAsync(); }
        public void Stop() { Window.RequestStopUpload(); }
        public void AssignVideos(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public void AssignVideosToProfile(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }

        public void Dispose() {
            try { Window.Dispose(); } catch { }
        }
    }
}
