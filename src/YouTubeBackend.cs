using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoBatch {
    /// <summary>Hidden legacy UploadWindow — business logic only, never shown in shell.</summary>
    public sealed class YouTubeBackend : IDisposable {
        readonly Preferences settings;
        public UploadWindow Window { get; }
        public event Action<string> LogLine;

        public YouTubeBackend(Preferences settings) {
            this.settings = settings;
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
        public Task RunOwnerPublishAsync(System.Collections.Generic.IReadOnlyList<OwnerPublishSelection> choices) { return Window.RunOwnerPublishAsync(choices); }
        public Task RunCheckProfilesAsync() { return Window.RunCheckProfilesAsync(); }
        public Task RunCheckProfilesAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels) { return Window.RunCheckProfilesAsync(channels); }
        public Task RunMeshWatchAsync() { return Window.RunMeshWatchAsync(); }
        public Task RunMeshWatchAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels) { return Window.RunMeshWatchAsync(channels); }
        public Task RunMeshWatchAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels, System.Collections.Generic.IReadOnlyDictionary<string, string> channelUrlsByProfileId) { return Window.RunMeshWatchAsync(channels, channelUrlsByProfileId); }
        public Task RunMeshWatchAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels, System.Collections.Generic.IReadOnlyDictionary<string, string> channelUrlsByProfileId, string market) { return Window.RunMeshWatchAsync(channels, channelUrlsByProfileId, market); }
        public Task RunCrossMeshWatchAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> viewerChannels, System.Collections.Generic.IReadOnlyList<YouTubeChannel> targetChannels, System.Collections.Generic.IReadOnlyDictionary<string, string> channelUrlsByProfileId, string viewerMarket) { return Window.RunCrossMeshWatchAsync(viewerChannels, targetChannels, channelUrlsByProfileId, viewerMarket); }
        public Task RunLinksWatchAsync(string linksText) { return Window.RunLinksWatchAsync(linksText); }
        public Task RunLinksWatchAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels, string linksText) { return Window.RunLinksWatchAsync(channels, linksText); }
        public Task RunShortsWarmupAsync(System.Collections.Generic.IReadOnlyList<YouTubeChannel> channels) { return Window.RunShortsWarmupAsync(channels); }
        public Task RunSearchAsync() { return Window.RunYouTubeSearchAsync(); }
        public void Stop() { Window.RequestStopUpload(); }
        public void AssignVideos(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public void AssignVideosToProfile(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public void AssignVideosToChannel(YouTubeChannel channel, string[] files) { Window.AssignVideosToChannel(channel, files); }
        public async Task AssignPreparedVideosToChannelAsync(YouTubeChannel channel, string[] files, IProgress<UploadPreparationProgress> progress) {
            string[] prepared = null;
            try {
                prepared = await UploadPreparation.PrepareAsync(UploadPreparation.YouTubeKey(channel), "YouTube", channel.ChannelId ?? channel.ProfileId, files, settings, progress);
                if (Window.IsDisposed) throw new OperationCanceledException();
                Window.AssignVideosToChannel(channel, prepared, true);
            } catch { UploadPreparation.DeleteOutputs(prepared); throw; }
        }
        public void SelectChannel(YouTubeChannel channel) { Window.SelectChannel(channel); }
        public bool TrySchedulePreview() { return Window.TryShowSchedulePreview(); }
        public bool TryShowSchedulePreview() { return Window.TryShowSchedulePreview(); }

        public void Dispose() {
            try { Window.Dispose(); } catch { }
        }
    }

    public sealed class TikTokBackend : IDisposable {
        readonly Preferences settings;
        public TikTokUploadWindow Window { get; }
        public event Action<string> LogLine;

        public TikTokBackend(Preferences settings) {
            this.settings = settings;
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
        public Task RunUploadAsync(IReadOnlyList<TikTokAccount> accounts) { return Window.RunUploadStudioAsync(accounts); }
        public Task RecheckContentAsync(IReadOnlyList<TikTokAccount> accounts) { return Window.RecheckContentAsync(accounts); }
        public void ReconcileUploads(IReadOnlyList<TikTokAccount> accounts) { Window.ReconcileUploadResults(accounts); }
        public Task RunUploadHttpAsync(IReadOnlyList<TikTokAccount> accounts) { return Window.RunUploadHttpAsync(accounts); }
        public Task RunUploadStudioAsync(IReadOnlyList<TikTokAccount> accounts) { return Window.RunUploadStudioAsync(accounts); }
        public TikTokSyncResult SyncFromYouTube() { return Window.SyncFromYouTube(); }
        public Task RunCheckProfilesAsync() { return Window.RunCheckProfilesAsync(); }
        public void Stop() { Window.RequestStopUpload(); }
        public void AssignVideos(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public void AssignVideosToProfile(string profileId, string market, string[] files) { Window.AssignVideosToProfile(profileId, market, files); }
        public async Task AssignPreparedVideosToProfileAsync(TikTokAccount account, string[] files, IProgress<UploadPreparationProgress> progress) {
            string[] prepared = null;
            try {
                prepared = await UploadPreparation.PrepareAsync(UploadPreparation.TikTokKey(account), "TikTok", account.ProfileId, files, settings, progress);
                if (Window.IsDisposed) throw new OperationCanceledException();
                Window.AssignVideosToProfile(account.ProfileId, account.Market, prepared, true);
            } catch { UploadPreparation.DeleteOutputs(prepared); throw; }
        }

        public void Dispose() {
            try { Window.Dispose(); } catch { }
        }
    }
}

