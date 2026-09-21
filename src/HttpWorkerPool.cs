using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public sealed class HttpWorkerPool : IDisposable {
        readonly CancellationTokenSource _poolCts = new CancellationTokenSource();
        volatile bool _stopped;

        public HttpUploadBatchMetrics Metrics { get; } = new HttpUploadBatchMetrics();
        public static int ActiveWorkers { get; private set; }

        public void Stop() {
            _stopped = true;
            try { _poolCts.Cancel(); } catch { }
        }

        public async Task RunAsync(
            IList<PreparedProfileBatch> batches,
            int maxWorkers,
            bool autoMode,
            Func<PreparedProfileBatch, HttpAccountMetrics, CancellationToken, Task> runBatch,
            CancellationToken externalCancel) {
            if (batches == null || batches.Count == 0) return;
            if (runBatch == null) throw new ArgumentNullException(nameof(runBatch));
            maxWorkers = Math.Max(1, maxWorkers);

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCancel, _poolCts.Token))
            using (linked.Token.Register(Stop)) {
                var cancel = linked.Token;
                var queue = new Queue<PreparedProfileBatch>(batches);
                int workerCount = Math.Min(maxWorkers, queue.Count);
                var tasks = new Task[workerCount];
                for (int w = 0; w < workerCount; w++) {
                    tasks[w] = Task.Run(async () => {
                        while (!cancel.IsCancellationRequested && !_stopped) {
                            if (autoMode && ActiveWorkers > 0 && !HttpWorkerSettings.CanStartAnotherWorker()) {
                                await Task.Delay(2000, cancel).ConfigureAwait(false);
                                continue;
                            }
                            PreparedProfileBatch batch;
                            lock (queue) {
                                if (queue.Count == 0) break;
                                batch = queue.Dequeue();
                            }
                            Interlocked.Increment(ref _active);
                            ActiveWorkers = _active;
                            var metrics = Metrics.BeginAccount(batch.Channel?.Name ?? "", batch.ProfileId ?? "");
                            using (var workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                            using (workerCts.Token.Register(() => { try { workerCts.Cancel(); } catch { } })) {
                                workerCts.CancelAfter(HttpWorkerSettings.WorkerTimeout);
                                try {
                                    metrics.MarkStarted();
                                    await runBatch(batch, metrics, workerCts.Token).ConfigureAwait(false);
                                } catch (OperationCanceledException) {
                                    metrics.Finish("stopped");
                                } catch (Exception) {
                                    if (metrics.Outcome == "success") metrics.Finish("error");
                                } finally {
                                    Interlocked.Decrement(ref _active);
                                    ActiveWorkers = _active;
                                }
                            }
                        }
                    }, cancel);
                }
                try {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                } catch (OperationCanceledException) { }
            }
            Metrics.Complete();
            Interlocked.Exchange(ref _active, 0);
            ActiveWorkers = 0;
        }

        static int _active;

        public void Dispose() {
            Stop();
            try { _poolCts.Dispose(); } catch { }
        }
    }
}
