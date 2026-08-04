using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DSPrt
{
    /// <summary>
    /// WebSocket メッセージ受信イベント引数
    /// </summary>
    public class MessageReceivedEventArgs : EventArgs
    {
        public string Message { get; set; }
        public DateTime ReceivedTime { get; set; }

        public MessageReceivedEventArgs(string message)
        {
            Message = message;
            ReceivedTime = DateTime.Now;
        }
    }

    /// <summary>
    /// WebSocket 接続状態変更イベント引数
    /// </summary>
    public class ConnectionStateChangedEventArgs : EventArgs
    {
        public bool IsConnected { get; set; }
        public string? Message { get; set; }

        public ConnectionStateChangedEventArgs(bool isConnected, string? message = null)
        {
            IsConnected = isConnected;
            Message = message;
        }
    }

    /// <summary>
    /// WebSocket クライアント（自動再接続ループ付き）
    /// </summary>
    public class WebSocketClient : IDisposable
    {
        private ClientWebSocket? _client;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _receiveTask;
        private Task? _reconnectTask;
        private readonly LOG_C _log;
        private bool _isDisposed;
        private bool _isConnected;
        private Uri? _lastUri;
        private bool _userDisconnected;  // ユーザー操作による切断フラグ

        // イベント
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        /// <summary>接続状態</summary>
        public bool IsConnected => _isConnected && _client?.State == WebSocketState.Open;

        public WebSocketClient(LOG_C log)
        {
            _log = log;
            _isDisposed = false;
            _isConnected = false;
        }

        /// <summary>
        /// サーバーに接続
        /// </summary>
        public async Task<bool> ConnectAsync(Uri uri)
        {
            try
            {
                if (_isConnected)
                {
                    _log.LogAdd("既に接続されています", _log.WARNING);
                    return true;
                }

                _lastUri = uri;
                _userDisconnected = false;

                _client = new ClientWebSocket();
                _cancellationTokenSource = new CancellationTokenSource();

                _log.LogAdd($"接続開始: {uri}", _log.INFO);
                await _client.ConnectAsync(uri, _cancellationTokenSource.Token);

                _isConnected = true;
                _log.LogAdd($"接続成功: {uri}", _log.INFO);

                ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(true, "接続成功"));

                // 受信ループ開始
                _receiveTask = Task.Run(ReceiveLoop);

                return true;
            }
            catch (Exception ex)
            {
                _log.LogAdd($"接続エラー: {ex.Message}", _log.ERR);
                _isConnected = false;
                ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(false, $"接続エラー: {ex.Message}"));

                // 自動再接続が有効であれば再接続ループを起動
                if (AppSettings.Instance.WebSocketSettings.AutoReconnect && !_userDisconnected)
                    EnsureReconnectLoop();

                return false;
            }
        }

        /// <summary>
        /// メッセージを送信
        /// </summary>
        public async Task<bool> SendMessageAsync(string message)
        {
            try
            {
                if (_client == null || !IsConnected)
                {
                    _log.LogAdd("未接続のため送信できません", _log.WARNING);
                    return false;
                }

                byte[] buffer = Encoding.UTF8.GetBytes(message);
                await _client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);

                _log.LogAdd($"電文送信: {message}", _log.INFO);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogAdd($"送信エラー: {ex.Message}", _log.ERR);
                return false;
            }
        }

        /// <summary>
        /// メッセージ受信ループ
        /// </summary>
        private async Task ReceiveLoop()
        {
            var buffer = new byte[8192];
            // バイト列を累積してから一括 UTF-8 デコードする（マルチバイト文字の分断を防ぐ）
            var byteAccumulator = new System.IO.MemoryStream();

            try
            {
                while (_client != null && IsConnected && !_cancellationTokenSource!.Token.IsCancellationRequested)
                {
                    byteAccumulator.SetLength(0);
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _log.LogAdd("サーバーから切断されました", _log.INFO);
                            _isConnected = false;
                            Application.Current?.Dispatcher.Invoke(() =>
                                ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(false, "サーバーから切断されました")));

                            if (AppSettings.Instance.WebSocketSettings.AutoReconnect && !_userDisconnected)
                                EnsureReconnectLoop();
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            byteAccumulator.Write(buffer, 0, result.Count);
                        }
                    }
                    while (!result.EndOfMessage);

                    if (byteAccumulator.Length > 0)
                    {
                        string message = Encoding.UTF8.GetString(byteAccumulator.GetBuffer(), 0, (int)byteAccumulator.Length);
                        _log.LogAdd($"電文受信: {message.Substring(0, Math.Min(200, message.Length))}...", _log.INFO);

                        Application.Current?.Dispatcher.Invoke(() =>
                            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message)));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _log.LogAdd("受信ループがキャンセルされました", _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"受信エラー: {ex.Message}", _log.ERR);
                _isConnected = false;

                Application.Current?.Dispatcher.Invoke(() =>
                    ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(false, $"受信エラー: {ex.Message}")));

                if (AppSettings.Instance.WebSocketSettings.AutoReconnect && !_userDisconnected)
                    EnsureReconnectLoop();
            }
        }

        /// <summary>
        /// 再接続ループが未起動であれば起動する
        /// </summary>
        private void EnsureReconnectLoop()
        {
            if (_reconnectTask == null || _reconnectTask.IsCompleted)
                _reconnectTask = Task.Run(ReconnectLoop);
        }

        /// <summary>
        /// 自動再接続ループ（10 秒間隔）
        /// </summary>
        private async Task ReconnectLoop()
        {
            while (!_isDisposed && !_userDisconnected)
            {
                int intervalMs = AppSettings.Instance.WebSocketSettings.ReconnectIntervalMs;
                _log.LogAdd($"再接続まで {intervalMs / 1000} 秒待機...", _log.INFO);

                try
                {
                    await Task.Delay(intervalMs);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                if (_isDisposed || _userDisconnected) return;

                if (!IsConnected && _lastUri != null)
                {
                    _log.LogAdd("再接続試行中...", _log.INFO);

                    // 古いリソースをクリアしてから再接続
                    _cancellationTokenSource?.Cancel();
                    _client?.Dispose();
                    _client = null;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                    _isConnected = false;

                    try
                    {
                        _client = new ClientWebSocket();
                        _cancellationTokenSource = new CancellationTokenSource();

                        await _client.ConnectAsync(_lastUri, _cancellationTokenSource.Token);
                        _isConnected = true;

                        _log.LogAdd("再接続成功", _log.INFO);

                        Application.Current?.Dispatcher.Invoke(() =>
                            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(true, "再接続成功")));

                        _receiveTask = Task.Run(ReceiveLoop);
                        return;  // 成功したらループ終了
                    }
                    catch (Exception ex)
                    {
                        _log.LogAdd($"再接続失敗: {ex.Message}", _log.WARNING);
                        _isConnected = false;
                    }
                }
                else if (IsConnected)
                {
                    return;  // 別の経路で接続済みになった場合
                }
            }
        }

        /// <summary>
        /// 切断（ユーザー操作）
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                _userDisconnected = true;

                if (_client != null && IsConnected)
                {
                    _log.LogAdd("切断開始", _log.INFO);
                    _cancellationTokenSource?.Cancel();

                    await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "正常終了", CancellationToken.None);

                    _isConnected = false;
                    _log.LogAdd("切断完了", _log.INFO);

                    ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(false, "切断完了"));
                }
            }
            catch (Exception ex)
            {
                _log.LogAdd($"切断エラー: {ex.Message}", _log.ERR);
                _isConnected = false;
            }
            finally
            {
                _client?.Dispose();
                _client = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _userDisconnected = true;
            DisconnectAsync().Wait();
        }
    }
}
