using System;
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
        public void SelectProfile(string profileId, string market) { Window.SelectProfileById(profileId, market); }
        public void SelectProfileById(string profileId, string market) { Window.SelectProfileById(profileId, market); }
        public Task RunHttpUploadAsync() { return Window.RunHttpUploadAsync(); }
        public Task RunStudioUploadAsync() { return Window.RunStudioUploadAsync(); }
        public Task RunCheckProfilesAsync() { return Window.RunCheckProfilesAsync(); }
        public Task RunMeshWatchAsync() { return Window.RunMeshWatchAsync(); }
        public Task RunSearchAsync() { return Window.RunYouTubeSearchAsync(); }
        public void Stop() { Window.RequestStopUpload(); }
        public void AssignVideos(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public void AssignVideosToProfile(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
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
        public Task RunCheckProfilesAsync() { return Window.RunCheckProfilesAsync(); }
        public void Stop() { Window.RequestStopUpload(); }
        public void AssignVideos(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public void AssignVideosToProfile(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }

        public void Dispose() {
            try { Window.Dispose(); } catch { }
        }
    }
}
