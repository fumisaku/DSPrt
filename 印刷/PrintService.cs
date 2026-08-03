using System;
using System.Threading.Tasks;
using DSPrt.Data;

namespace DSPrt.印刷
{
    /// <summary>
    /// 印刷処理の全体を束ねるサービスクラス。
    /// DSPrtClient から呼ばれ、PrintJobQueue・ReportRenderer・PrinterController を協調させる。
    /// </summary>
    public class PrintService : IDisposable
    {
        private readonly LOG_C _log;
        private readonly DataManager _dataManager;
        private readonly PrintJobQueue _queue;
        private readonly ReportLayoutRegistry _registry;
        private readonly ReportRenderer _renderer;
        private readonly PrinterController _printerController;
        private bool _isDisposed;

        // ─── イベント ────────────────────────────────────────────────

        /// <summary>ジョブ状態変化通知（UI のジョブログ更新用）</summary>
        public event EventHandler<PrintJobStatusChangedEventArgs>? JobStatusChanged;

        /// <summary>
        /// 印刷完了通知（PR_DONE 送信のトリガー）。
        /// args: jobId, status("done"|"error"), message
        /// </summary>
        public event EventHandler<PrintDoneEventArgs>? PrintDone;

        // ─── コンストラクタ ──────────────────────────────────────────

        public PrintService(LOG_C log, DataManager dataManager)
        {
            _log             = log;
            _dataManager     = dataManager;
            _registry        = new ReportLayoutRegistry(log);
            _renderer        = new ReportRenderer(log, dataManager);
            _printerController = new PrinterController(log);

            _queue = new PrintJobQueue(log, AppSettings.Instance.PrintSettings.MaxQueueSize);
            _queue.ProcessJob      += OnProcessJobAsync;
            _queue.JobStatusChanged += (s, e) => JobStatusChanged?.Invoke(s, e);

            _queue.Start();
            _log.LogAdd("[PrintService] 起動完了", _log.INFO);
        }

        // ─── 公開メソッド ────────────────────────────────────────────

        /// <summary>
        /// 印刷ジョブをキューに追加する。
        /// </summary>
        public bool Enqueue(PrintJob job)
        {
            // layoutId の存在チェック
            var layout = _registry.Get(job.LayoutId);
            if (layout == null)
            {
                _log.LogAdd($"[PrintService] 未登録の layoutId: {job.LayoutId}", _log.WARNING);
                // layoutId 未登録でもキューには入れる（テスト印刷などのため）
            }

            return _queue.Enqueue(job);
        }

        /// <summary>
        /// 印刷ジョブをキャンセルする。
        /// </summary>
        public bool Cancel(string jobId) => _queue.Cancel(jobId);

        /// <summary>
        /// ジョブを再印刷する（元の jobId に _R サフィックスを付けて再エンキュー）。
        /// </summary>
        public bool Reprint(PrintJob originalJob)
        {
            var reprintJob = originalJob.CreateReprint();
            _log.LogAdd($"[PrintService] 再印刷: original={originalJob.JobId}, reprint={reprintJob.JobId}", _log.INFO);
            return _queue.Enqueue(reprintJob);
        }

        /// <summary>現在のキューサイズ</summary>
        public int QueueCount => _queue.Count;

        /// <summary>レイアウトレジストリ（帳票設定タブ用）</summary>
        public ReportLayoutRegistry Registry => _registry;

        /// <summary>プリンターコントローラー（設定タブ用）</summary>
        public PrinterController PrinterController => _printerController;

        /// <summary>
        /// プリンター名を指定して直接印刷する（テスト印刷・プリンター選択ダイアログ向け）。
        /// キューを経由せず即時印刷する。
        /// </summary>
        public async Task PrintDirectAsync(PrintJob job, string printerName)
        {
            var layout = _registry.Get(job.LayoutId);
            if (layout == null)
            {
                _log.LogAdd($"[PrintService] PrintDirect: 未登録 layoutId={job.LayoutId}", _log.WARNING);
                throw new InvalidOperationException($"layoutId '{job.LayoutId}' が登録されていません。");
            }
            await _renderer.PrintAsync(job, layout, printerName);
        }

        /// <summary>
        /// 帳票を HTML に出力する（プレビュー・保存用）。
        /// FastReport.OpenSource には PDF エクスポートが含まれないため HTML を使用する。
        /// </summary>
        public async Task<string?> ExportHtmlAsync(PrintJob job, string outputDir)
        {
            var layout = _registry.Get(job.LayoutId);
            if (layout == null)
            {
                _log.LogAdd($"[PrintService] HTML 出力: 未登録 layoutId={job.LayoutId}", _log.WARNING);
                return null;
            }

            try
            {
                return await _renderer.ExportHtmlAsync(job, layout, outputDir);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[PrintService] HTML 出力エラー: {ex.Message}", _log.ERR);
                return null;
            }
        }

        // ─── ジョブ処理（PrintJobQueue から呼ばれる）────────────────

        private async Task OnProcessJobAsync(PrintJob job)
        {
            var layout = _registry.Get(job.LayoutId);
            if (layout == null)
            {
                // layoutId 未登録 → エラーとして完了
                throw new InvalidOperationException($"layoutId '{job.LayoutId}' がDSPrt.jsonのLayoutsに登録されていません。");
            }

            // プリンター名を解決
            string printerName = _printerController.ResolvePrinterName(layout);
            if (!_printerController.PrinterExists(printerName))
            {
                _log.LogAdd($"[PrintService] プリンター '{printerName}' が見つかりません。デフォルトプリンターを使用します。", _log.WARNING);
                // フォールバック: Windows デフォルトプリンターを使用
                string winDefault = _printerController.GetDefaultPrinterName();
                // layout のコピーを作って上書き
                layout = new LayoutSetting
                {
                    LayoutId    = layout.LayoutId,
                    FrxPath     = layout.FrxPath,
                    DataType    = layout.DataType,
                    PrinterName = winDefault,
                    Copies      = layout.Copies,
                    Duplex      = layout.Duplex,
                    PaperSize   = layout.PaperSize
                };
            }

            try
            {
                await _renderer.PrintAsync(job, layout);
                PrintDone?.Invoke(this, new PrintDoneEventArgs(job.JobId, "done", null));
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[PrintService] 印刷失敗: jobId={job.JobId}, error={ex.Message}", _log.ERR);
                PrintDone?.Invoke(this, new PrintDoneEventArgs(job.JobId, "error", ex.Message));
                throw;   // PrintJobQueue 側でも Error 状態にするため再スロー
            }
        }

        // ─── IDisposable ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _queue.Dispose();
            _log.LogAdd("[PrintService] 停止", _log.INFO);
        }
    }

    public class PrintDoneEventArgs : EventArgs
    {
        public string JobId   { get; }
        public string Status  { get; }  // "done" | "error"
        public string? Message { get; }

        public PrintDoneEventArgs(string jobId, string status, string? message)
        {
            JobId   = jobId;
            Status  = status;
            Message = message;
        }
    }
}
