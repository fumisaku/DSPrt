using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DSPrt.印刷
{
    /// <summary>
    /// 優先度付き印刷ジョブキュー。
    /// 非同期逐次処理（同時 1 ジョブ）。重複 jobId を排除する。
    /// </summary>
    public class PrintJobQueue : IDisposable
    {
        private readonly LOG_C _log;
        private readonly PriorityQueue<PrintJob, int> _queue = new();
        private readonly HashSet<string> _jobIds = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _lock = new();
        private readonly int _maxSize;
        private bool _isRunning;
        private bool _isDisposed;
        private CancellationTokenSource? _cts;
        private Task? _workerTask;

        // ─── イベント ────────────────────────────────────────────────
        /// <summary>ジョブ処理を要求するイベント。ハンドラで実際の帳票描画・印刷を行う。</summary>
        public event Func<PrintJob, Task>? ProcessJob;
        /// <summary>ジョブ状態変化を通知（UI 更新用）</summary>
        public event EventHandler<PrintJobStatusChangedEventArgs>? JobStatusChanged;

        public PrintJobQueue(LOG_C log, int maxSize = 50)
        {
            _log     = log;
            _maxSize = maxSize;
        }

        // ─── キュー操作 ──────────────────────────────────────────────

        /// <summary>
        /// ジョブをキューに追加する。重複 jobId は無視する。
        /// </summary>
        /// <returns>true=追加成功 / false=重複またはキュー満杯</returns>
        public bool Enqueue(PrintJob job)
        {
            lock (_lock)
            {
                if (_jobIds.Contains(job.JobId))
                {
                    _log.LogAdd($"[PrintJobQueue] 重複ジョブを無視: {job.JobId}", _log.WARNING);
                    return false;
                }
                if (_queue.Count >= _maxSize)
                {
                    _log.LogAdd($"[PrintJobQueue] キュー満杯({_maxSize})のためジョブを破棄: {job.JobId}", _log.WARNING);
                    return false;
                }
                _jobIds.Add(job.JobId);
                _queue.Enqueue(job, job.Priority);
                job.Status = PrintJobStatus.Queued;
                _log.LogAdd($"[PrintJobQueue] エンキュー: jobId={job.JobId}, priority={job.Priority}, queueSize={_queue.Count}", _log.INFO);
            }
            _signal.Release();
            NotifyStatusChanged(job);
            return true;
        }

        /// <summary>
        /// jobId に一致するキュー内ジョブをキャンセル済みにする。
        /// 処理中のジョブはキャンセルできない（プリンタースプーラーに委ねる）。
        /// </summary>
        public bool Cancel(string jobId)
        {
            lock (_lock)
            {
                _jobIds.Remove(jobId);
                _log.LogAdd($"[PrintJobQueue] キャンセル: jobId={jobId}", _log.INFO);
            }
            return true;
        }

        public int Count
        {
            get { lock (_lock) { return _queue.Count; } }
        }

        // ─── ワーカー ────────────────────────────────────────────────

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
            _log.LogAdd("[PrintJobQueue] ワーカー開始", _log.INFO);
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            _signal.Release();   // ブロック解除
            _log.LogAdd("[PrintJobQueue] ワーカー停止", _log.INFO);
        }

        private async Task WorkerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(ct);
                    if (ct.IsCancellationRequested) break;

                    PrintJob? job = null;
                    lock (_lock)
                    {
                        if (_queue.TryDequeue(out var dequeued, out _))
                        {
                            // キャンセル済み jobId は _jobIds から除去済みなので skip
                            if (_jobIds.Contains(dequeued.JobId))
                                job = dequeued;
                        }
                    }

                    if (job == null) continue;

                    job.Status    = PrintJobStatus.Processing;
                    job.StartedAt = DateTime.Now;
                    NotifyStatusChanged(job);
                    _log.LogAdd($"[PrintJobQueue] 処理開始: jobId={job.JobId}", _log.INFO);

                    try
                    {
                        if (ProcessJob != null)
                            await ProcessJob.Invoke(job);

                        job.Status      = PrintJobStatus.Done;
                        job.CompletedAt = DateTime.Now;
                        _log.LogAdd($"[PrintJobQueue] 処理完了: jobId={job.JobId}", _log.INFO);
                    }
                    catch (Exception ex)
                    {
                        job.Status       = PrintJobStatus.Error;
                        job.ErrorMessage = ex.Message;
                        job.CompletedAt  = DateTime.Now;
                        _log.LogAdd($"[PrintJobQueue] 処理エラー: jobId={job.JobId}, error={ex.Message}", _log.ERR);
                    }
                    finally
                    {
                        lock (_lock) { _jobIds.Remove(job.JobId); }
                        NotifyStatusChanged(job);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogAdd($"[PrintJobQueue] ワーカー予期しないエラー: {ex.Message}", _log.ERR);
                }
            }
        }

        private void NotifyStatusChanged(PrintJob job)
        {
            JobStatusChanged?.Invoke(this, new PrintJobStatusChangedEventArgs(job));
        }

        // ─── IDisposable ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            _cts?.Dispose();
            _signal.Dispose();
        }
    }

    public class PrintJobStatusChangedEventArgs : EventArgs
    {
        public PrintJob Job { get; }
        public PrintJobStatusChangedEventArgs(PrintJob job) { Job = job; }
    }
}
