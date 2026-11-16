using System;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    /// <summary>Limits concurrent Dolphin profile launches and enforces a minimum gap between start requests.</summary>
    public sealed class ProfileLaunchGate {
        readonly SemaphoreSlim _running;
        readonly CancellationTokenSource _pendingStarts = new CancellationTokenSource();
        readonly object _startLock = new object();
        readonly int _minGapMs, _maxGapMs;
        readonly Random _rng = new Random();
        DateTime _nextStartUtc = DateTime.MinValue;

        public ProfileLaunchGate(int maxConcurrent, int minGapMs, int maxGapMs) {
            maxConcurrent = Math.Max(1, maxConcurrent);
            _running = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            _minGapMs = Math.Max(500, minGapMs);
            _maxGapMs = Math.Max(_minGapMs, maxGapMs);
        }

        public static ProfileLaunchGate FromSettings(Preferences prefs, int maxConcurrent) {
            int minMs = prefs?.ProfileLaunchStaggerMinMs ?? 3000;
            int maxMs = prefs?.ProfileLaunchStaggerMaxMs ?? 5000;
            return new ProfileLaunchGate(maxConcurrent, minMs, maxMs);
        }

        public static int ResolveStudioMaxConcurrent(Preferences prefs) {
            int configured = prefs?.MaxParallelUploads ?? 3;
            return configured <= 0 ? 3 : Math.Max(1, Math.Min(3, configured));
        }

        public static ProfileLaunchGate ForStudioUpload(Preferences prefs) {
            return new ProfileLaunchGate(ResolveStudioMaxConcurrent(prefs), 5000, 5000);
        }

        public static bool ShouldPauseAfterStudioFailure(Exception error) {
            return !System.Text.RegularExpressions.Regex.IsMatch(error?.Message??"",
                "Target page, context or browser has been closed|browser has been closed|browser disconnected",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>Stops queued starts only; tokens belonging to already running jobs are unchanged.</summary>
        public void StopPendingStarts() {
            _pendingStarts.Cancel();
        }

        public async Task EnterAsync(CancellationToken ct) {
            using (var start = CancellationTokenSource.CreateLinkedTokenSource(ct, _pendingStarts.Token)) {
                await _running.WaitAsync(start.Token).ConfigureAwait(false);
                try {
                    int waitMs = 0;
                    lock (_startLock) {
                        var now = DateTime.UtcNow;
                        if (now < _nextStartUtc)
                            waitMs = (int)Math.Ceiling((_nextStartUtc - now).TotalMilliseconds);
                        int gap = _rng.Next(_minGapMs, _maxGapMs + 1);
                        var startAt = waitMs > 0 ? now.AddMilliseconds(waitMs) : now;
                        _nextStartUtc = startAt.AddMilliseconds(gap);
                    }
                    if (waitMs > 0) await Task.Delay(waitMs, start.Token).ConfigureAwait(false);
                    start.Token.ThrowIfCancellationRequested();
                } catch { _running.Release(); throw; }
            }
        }

        /// <summary>Staggered gap between profile starts, then releases the slot (for long parallel jobs like mesh watch).</summary>
        public async Task WaitStaggeredStartAsync(CancellationToken ct) {
            await EnterAsync(ct).ConfigureAwait(false);
            Exit();
        }

        public void Exit() {
            try { _running.Release(); } catch { }
        }
    }
}

