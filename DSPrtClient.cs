using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DSPrt.Data;
using DSPrt.Handlers;
using DSPrt.Messages;

namespace DSPrt
{
    /// <summary>
    /// DSPrtClient — 接続・初期化・送受信のオーケストレーション Facade
    /// </summary>
    public class DSPrtClient : IDisposable
    {
        private readonly LOG_C _log;
        private readonly WebSocketClient _wsClient;
        private readonly DataManager _dataManager;
        private readonly PR_MessageHandler _messageHandler;
        private bool _isDisposed;
        private bool _isInitializing;
        private bool _everConnected;   // 初回 ConnectAsync 成功後 true にセット

        // ─── 公開プロパティ ─────────────────────────────────────────

        public LOG_C Log => _log;
        public DataManager DataManager => _dataManager;
        public PR_MessageHandler MessageHandler => _messageHandler;
        public bool IsConnected => _wsClient.IsConnected;

        // ─── イベント ────────────────────────────────────────────────

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
        public event EventHandler<CompetitionListReceivedEventArgs>? CompetitionListReceived;
        public event EventHandler? DA_MasterReceived;
        public event EventHandler? DS_StatusReceived;
        public event EventHandler<PrintJobRequestedEventArgs>? PrintJobRequested;
        public event EventHandler<PrintJobCancelRequestedEventArgs>? PrintJobCancelRequested;
        public event EventHandler<ErrorReceivedEventArgs>? ErrorReceived;

        /// <summary>
        /// 複数競技会選択コールバック。選択された CmpNo を返す。キャンセル時は null。
        /// </summary>
        public Func<List<CompetitionInfo>, Task<string?>>? CompetitionSelector { get; set; }

        // ─── コンストラクタ ──────────────────────────────────────────

        public DSPrtClient()
        {
            _log = new LOG_C();
            _log.SetLogLevel(AppSettings.Instance.LogSettings.LogLevel);
            _log.CreateFile(AppSettings.Instance.LogSettings.LogPath);

            _dataManager = new DataManager(_log);

            _wsClient = new WebSocketClient(_log);
            _wsClient.MessageReceived         += OnMessageReceived;
            _wsClient.ConnectionStateChanged  += OnConnectionStateChanged;

            _messageHandler = new PR_MessageHandler(_log, _dataManager, _wsClient);
            _messageHandler.LoginOk                  += (s, e) => { };  // DSPrtClient 側で WaitFor* に使用
            _messageHandler.CompetitionListReceived  += (s, e) => CompetitionListReceived?.Invoke(s, e);
            _messageHandler.DA_MasterReceived        += (s, e) => DA_MasterReceived?.Invoke(s, e);
            _messageHandler.DS_StatusReceived        += (s, e) => DS_StatusReceived?.Invoke(s, e);
            _messageHandler.PrintJobRequested        += (s, e) => PrintJobRequested?.Invoke(s, e);
            _messageHandler.PrintJobCancelRequested  += (s, e) => PrintJobCancelRequested?.Invoke(s, e);
            _messageHandler.ErrorReceived            += (s, e) => ErrorReceived?.Invoke(s, e);

            _isDisposed = false;
            _log.LogAdd("DSPrtClient 初期化完了", _log.INFO);
        }

        // ─── 接続・切断 ─────────────────────────────────────────────

        /// <summary>設定ファイルの接続先に接続</summary>
        public async Task<bool> ConnectAsync()
        {
            var url = AppSettings.Instance.GetWebSocketUrl();
            var uri = new Uri(url);
            _log.LogAdd($"接続開始: {url}", _log.INFO);
            bool connected = await _wsClient.ConnectAsync(uri);
            if (connected)
                _everConnected = true;
            return connected;
        }

        /// <summary>切断</summary>
        public async Task DisconnectAsync()
        {
            await _wsClient.DisconnectAsync();
        }

        // ─── 初期化シーケンス ────────────────────────────────────────

        /// <summary>
        /// 初期化シーケンスを実行する。
        /// 1. PR_LOGIN 送信
        /// 2. PR_ANS_CMP_LIST または PR_LOGIN_OK を待機（タイムアウト 30 秒）
        /// 3. 複数競技会の場合: CompetitionSelector コールバックで選択
        /// 4. PR_SEL_CMP 送信（複数の場合）
        /// 5. PR_ANS_DA 受信待機
        /// 6. PR_ANS_DS 受信待機
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            if (_isInitializing)
            {
                _log.LogAdd("初期化シーケンスはすでに実行中です", _log.WARNING);
                return false;
            }

            _isInitializing = true;
            var ws = AppSettings.Instance.WebSocketSettings;
            int timeoutMs = ws.ConnectionTimeoutMs;

            try
            {
                _log.LogAdd("初期化シーケンス開始", _log.INFO);

                // 1. PR_LOGIN 送信
                bool sent = await _messageHandler.SendLoginAsync();
                if (!sent)
                {
                    _log.LogAdd("PR_LOGIN 送信失敗", _log.ERR);
                    return false;
                }

                // 2. PR_LOGIN_OK または PR_ANS_CMP_LIST を待機
                var waitResult = await WaitForLoginOkOrCmpListAsync(timeoutMs);
                if (waitResult == null)
                {
                    _log.LogAdd("PR_LOGIN_OK / PR_ANS_CMP_LIST 受信タイムアウト", _log.ERR);
                    return false;
                }

                // 3. 複数競技会の場合: 選択ダイアログ
                if (waitResult.Value.CmpList != null)
                {
                    _log.LogAdd($"複数競技会リスト受信: {waitResult.Value.CmpList.Count} 件", _log.INFO);

                    if (CompetitionSelector == null)
                    {
                        _log.LogAdd("CompetitionSelector が未設定のため選択不可", _log.ERR);
                        return false;
                    }

                    var selectedCmpNo = await CompetitionSelector(waitResult.Value.CmpList);
                    if (string.IsNullOrEmpty(selectedCmpNo))
                    {
                        _log.LogAdd("競技会選択がキャンセルされました", _log.WARNING);
                        return false;
                    }

                    _log.LogAdd($"競技会選択: CmpNo={selectedCmpNo}", _log.INFO);

                    // 4. PR_SEL_CMP 送信
                    bool selSent = await _messageHandler.SendSelectCompetitionAsync(ws.OrgCd, selectedCmpNo);
                    if (!selSent)
                    {
                        _log.LogAdd("PR_SEL_CMP 送信失敗", _log.ERR);
                        return false;
                    }
                }

                // 5. PR_ANS_DA 受信待機
                bool daOk = await WaitForDA_MasterAsync(timeoutMs);
                if (!daOk)
                {
                    _log.LogAdd("PR_ANS_DA 受信タイムアウト", _log.ERR);
                    return false;
                }

                // 6. PR_ANS_DS 受信待機
                bool dsOk = await WaitForDS_StatusAsync(timeoutMs);
                if (!dsOk)
                {
                    _log.LogAdd("PR_ANS_DS 受信タイムアウト", _log.ERR);
                    return false;
                }

                _log.LogAdd("初期化シーケンス完了", _log.INFO);
                return true;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        // ─── 送信ヘルパー ────────────────────────────────────────────

        /// <summary>PR_ACK 送信</summary>
        public async Task<bool> SendAckAsync(string jobId)
        {
            var dm = _dataManager;
            return await _messageHandler.SendAckAsync(
                dm.OrgCd ?? AppSettings.Instance.WebSocketSettings.OrgCd,
                dm.CmpNo ?? "",
                jobId);
        }

        /// <summary>PR_DONE 送信</summary>
        public async Task<bool> SendDoneAsync(string jobId, string status, string? message = null)
        {
            var dm = _dataManager;
            return await _messageHandler.SendDoneAsync(
                dm.OrgCd ?? AppSettings.Instance.WebSocketSettings.OrgCd,
                dm.CmpNo ?? "",
                jobId, status, message);
        }

        // ─── イベントハンドラ ────────────────────────────────────────

        private async void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            await _messageHandler.HandleMessageAsync(e.Message);
        }

        private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            ConnectionStateChanged?.Invoke(sender, e);

            // 再接続成功時（切断→接続）に自動再初期化
            // _everConnected が true のとき = 一度切断後の再接続なので InitializeAsync を再実行
            // _everConnected が false = ConnectAsync 直後の初回接続イベントなので、
            //   ConnectAndInitializeAsync 側が InitializeAsync を呼ぶため二重起動しない
            if (e.IsConnected && _everConnected && !_isInitializing)
            {
                _log.LogAdd("再接続成功 — 初期化シーケンスを再実行", _log.INFO);
                _ = Task.Run(InitializeAsync);
            }
        }

        // ─── 待機ヘルパー ────────────────────────────────────────────

        /// <summary>PR_LOGIN_OK または PR_ANS_CMP_LIST を待機（どちらか先着）</summary>
        private Task<(string? AuthId, List<CompetitionInfo>? CmpList)?>
            WaitForLoginOkOrCmpListAsync(int timeoutMs)
        {
            var tcs = new TaskCompletionSource<(string?, List<CompetitionInfo>?)?>();

            EventHandler<string>? loginHandler = null;
            EventHandler<CompetitionListReceivedEventArgs>? cmpHandler = null;

            loginHandler = (s, authId) =>
            {
                _messageHandler.LoginOk                -= loginHandler;
                _messageHandler.CompetitionListReceived -= cmpHandler;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult((authId, null));
            };

            cmpHandler = (s, e) =>
            {
                _messageHandler.LoginOk                -= loginHandler;
                _messageHandler.CompetitionListReceived -= cmpHandler;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult((null, e.Competitions));
            };

            _messageHandler.LoginOk                += loginHandler;
            _messageHandler.CompetitionListReceived += cmpHandler;

            // タイムアウト処理
            _ = Task.Delay(timeoutMs).ContinueWith(_ =>
            {
                _messageHandler.LoginOk                -= loginHandler;
                _messageHandler.CompetitionListReceived -= cmpHandler;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(null);
            });

            return tcs.Task;
        }

        /// <summary>PR_ANS_DA 受信を待機</summary>
        private Task<bool> WaitForDA_MasterAsync(int timeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>();

            EventHandler? handler = null;
            handler = (s, e) =>
            {
                _messageHandler.DA_MasterReceived -= handler;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(true);
            };
            _messageHandler.DA_MasterReceived += handler;

            _ = Task.Delay(timeoutMs).ContinueWith(_ =>
            {
                _messageHandler.DA_MasterReceived -= handler;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
            });

            return tcs.Task;
        }

        /// <summary>PR_ANS_DS 受信を待機</summary>
        private Task<bool> WaitForDS_StatusAsync(int timeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>();

            EventHandler? handler = null;
            handler = (s, e) =>
            {
                _messageHandler.DS_StatusReceived -= handler;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(true);
            };
            _messageHandler.DS_StatusReceived += handler;

            _ = Task.Delay(timeoutMs).ContinueWith(_ =>
            {
                _messageHandler.DS_StatusReceived -= handler;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
            });

            return tcs.Task;
        }

        // ─── Dispose ────────────────────────────────────────────────

        public void Dispose()
        {
            if (_isDisposed) return;

            _log.LogAdd("DSPrtClient 終了処理開始", _log.INFO);

            _wsClient.MessageReceived        -= OnMessageReceived;
            _wsClient.ConnectionStateChanged -= OnConnectionStateChanged;
            _wsClient.Dispose();

            _dataManager.ClearAll();

            _log.LogAdd("DSPrtClient 終了処理完了", _log.INFO);
            _isDisposed = true;
        }
    }
}
