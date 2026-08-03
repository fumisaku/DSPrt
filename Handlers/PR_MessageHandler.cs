using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using DSPrt.Data;
using DSPrt.Messages;

namespace DSPrt.Handlers
{
    /// <summary>
    /// 印刷ジョブ要求イベント引数
    /// </summary>
    public class PrintJobRequestedEventArgs : EventArgs
    {
        public PR_PRINT_Request Job { get; }
        public PrintJobRequestedEventArgs(PR_PRINT_Request job) { Job = job; }
    }

    /// <summary>
    /// 印刷ジョブキャンセル要求イベント引数
    /// </summary>
    public class PrintJobCancelRequestedEventArgs : EventArgs
    {
        public string JobId { get; }
        public PrintJobCancelRequestedEventArgs(string jobId) { JobId = jobId; }
    }

    /// <summary>
    /// 競技会リスト受信イベント引数
    /// </summary>
    public class CompetitionListReceivedEventArgs : EventArgs
    {
        public List<CompetitionInfo> Competitions { get; }
        public CompetitionListReceivedEventArgs(List<CompetitionInfo> competitions)
        {
            Competitions = competitions;
        }
    }

    /// <summary>
    /// エラー受信イベント引数
    /// </summary>
    public class ErrorReceivedEventArgs : EventArgs
    {
        public string Command { get; }
        public string ErrorMessage { get; }
        public ErrorReceivedEventArgs(string command, string errorMessage)
        {
            Command = command;
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>
    /// PR_ プレフィックス電文のクライアント側ハンドラ
    /// </summary>
    public class PR_MessageHandler
    {
        private readonly LOG_C _log;
        private readonly DataManager _dataManager;
        private readonly WebSocketClient _wsClient;

        private static readonly JsonSerializerOptions _jsonOpts =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // ─── イベント ────────────────────────────────────────────────

        /// <summary>PR_LOGIN_OK 受信時</summary>
        public event EventHandler<string>? LoginOk;                          // authId
        /// <summary>PR_ANS_CMP_LIST 受信時（複数競技会）</summary>
        public event EventHandler<CompetitionListReceivedEventArgs>? CompetitionListReceived;
        /// <summary>DA_Master 受信・更新時</summary>
        public event EventHandler? DA_MasterReceived;
        /// <summary>DS_Status 受信・更新時</summary>
        public event EventHandler? DS_StatusReceived;
        /// <summary>PR_PRINT 受信時（印刷ジョブ要求）</summary>
        public event EventHandler<PrintJobRequestedEventArgs>? PrintJobRequested;
        /// <summary>PR_CANCEL 受信時</summary>
        public event EventHandler<PrintJobCancelRequestedEventArgs>? PrintJobCancelRequested;
        /// <summary>エラー受信時</summary>
        public event EventHandler<ErrorReceivedEventArgs>? ErrorReceived;

        // ─── コンストラクタ ──────────────────────────────────────────

        public PR_MessageHandler(LOG_C log, DataManager dataManager, WebSocketClient wsClient)
        {
            _log = log;
            _dataManager = dataManager;
            _wsClient = wsClient;
        }

        // ─── メッセージ処理 ─────────────────────────────────────────

        /// <summary>受信電文を処理</summary>
        public async Task HandleMessageAsync(string message)
        {
            var parsed = ParsedMessage.Parse(message);
            if (parsed == null)
            {
                _log.LogAdd($"電文パースエラー: {message}", _log.ERR);
                return;
            }

            _log.LogAdd($"電文処理: {parsed.Command}", _log.DEBUG);

            try
            {
                switch (parsed.Command)
                {
                    case "PR_LOGIN_OK":
                        await Handle_PR_LOGIN_OK(parsed);
                        break;

                    case "PR_ANS_CMP_LIST":
                        await Handle_PR_ANS_CMP_LIST(parsed);
                        break;

                    case "PR_ANS_DA":
                    case "PR_UPD_DA":
                        await Handle_DA_Master(parsed);
                        break;

                    case "PR_ANS_DS":
                        await Handle_DS_Status(parsed);
                        break;

                    case "PR_PRINT":
                        await Handle_PR_PRINT(parsed);
                        break;

                    case "PR_CANCEL":
                        await Handle_PR_CANCEL(parsed);
                        break;

                    default:
                        if (parsed.IsError)
                            HandleError(parsed);
                        else
                            _log.LogAdd($"未対応の電文: {parsed.Command}", _log.WARNING);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogAdd($"電文処理エラー [{parsed.Command}]: {ex.Message}", _log.ERR);
            }
        }

        // ─── 各コマンドハンドラ ─────────────────────────────────────

        private async Task Handle_PR_LOGIN_OK(ParsedMessage msg)
        {
            if (string.IsNullOrEmpty(msg.MsgDetail))
            {
                _log.LogAdd("PR_LOGIN_OK: MsgDetail が空", _log.WARNING);
                LoginOk?.Invoke(this, string.Empty);
                await Task.CompletedTask;
                return;
            }

            try
            {
                var resp = JsonSerializer.Deserialize<PR_LOGIN_OK_Response>(msg.MsgDetail, _jsonOpts);
                var authId = resp?.AuthId ?? string.Empty;
                _log.LogAdd($"PR_LOGIN_OK: authId={authId}", _log.INFO);
                LoginOk?.Invoke(this, authId);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"PR_LOGIN_OK パースエラー: {ex.Message}", _log.ERR);
                LoginOk?.Invoke(this, string.Empty);
            }

            await Task.CompletedTask;
        }

        private async Task Handle_PR_ANS_CMP_LIST(ParsedMessage msg)
        {
            try
            {
                var resp = JsonSerializer.Deserialize<PR_ANS_CMP_LIST_Response>(msg.MsgDetail, _jsonOpts);
                if (resp != null)
                {
                    _log.LogAdd($"PR_ANS_CMP_LIST: {resp.Competitions.Count} 件", _log.INFO);
                    CompetitionListReceived?.Invoke(this, new CompetitionListReceivedEventArgs(resp.Competitions));
                }
            }
            catch (Exception ex)
            {
                _log.LogAdd($"PR_ANS_CMP_LIST パースエラー: {ex.Message}", _log.ERR);
            }

            await Task.CompletedTask;
        }

        private async Task Handle_DA_Master(ParsedMessage msg)
        {
            if (string.IsNullOrEmpty(msg.MsgDetail))
            {
                _log.LogAdd($"{msg.Command}: MsgDetail が空", _log.WARNING);
                await Task.CompletedTask;
                return;
            }

            _dataManager.SetDA_Master(msg.MsgDetail);
            DA_MasterReceived?.Invoke(this, EventArgs.Empty);
            await Task.CompletedTask;
        }

        private async Task Handle_DS_Status(ParsedMessage msg)
        {
            if (string.IsNullOrEmpty(msg.MsgDetail))
            {
                _log.LogAdd("PR_ANS_DS: MsgDetail が空", _log.WARNING);
                await Task.CompletedTask;
                return;
            }

            _dataManager.SetDS_Status(msg.MsgDetail);
            DS_StatusReceived?.Invoke(this, EventArgs.Empty);
            await Task.CompletedTask;
        }

        private async Task Handle_PR_PRINT(ParsedMessage msg)
        {
            if (string.IsNullOrEmpty(msg.MsgDetail))
            {
                _log.LogAdd("PR_PRINT: MsgDetail が空", _log.WARNING);
                await Task.CompletedTask;
                return;
            }

            try
            {
                var req = JsonSerializer.Deserialize<PR_PRINT_Request>(msg.MsgDetail, _jsonOpts);
                if (req == null)
                {
                    _log.LogAdd("PR_PRINT: デシリアライズ失敗", _log.ERR);
                    await Task.CompletedTask;
                    return;
                }

                _log.LogAdd($"PR_PRINT 受信: jobId={req.JobId}, layoutId={req.LayoutId}", _log.INFO);
                PrintJobRequested?.Invoke(this, new PrintJobRequestedEventArgs(req));
            }
            catch (Exception ex)
            {
                _log.LogAdd($"PR_PRINT パースエラー: {ex.Message}", _log.ERR);
            }

            await Task.CompletedTask;
        }

        private async Task Handle_PR_CANCEL(ParsedMessage msg)
        {
            try
            {
                var req = JsonSerializer.Deserialize<PR_CANCEL_Request>(msg.MsgDetail, _jsonOpts);
                if (req != null)
                {
                    _log.LogAdd($"PR_CANCEL 受信: jobId={req.JobId}", _log.INFO);
                    PrintJobCancelRequested?.Invoke(this, new PrintJobCancelRequestedEventArgs(req.JobId));
                }
            }
            catch (Exception ex)
            {
                _log.LogAdd($"PR_CANCEL パースエラー: {ex.Message}", _log.ERR);
            }

            await Task.CompletedTask;
        }

        private void HandleError(ParsedMessage msg)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ErrorResponse>(msg.MsgDetail, _jsonOpts);
                var errorMsg = error?.Error ?? msg.MsgDetail;
                _log.LogAdd($"サーバーエラー [{msg.Command}]: {errorMsg}", _log.ERR);
                ErrorReceived?.Invoke(this, new ErrorReceivedEventArgs(msg.Command, errorMsg));
            }
            catch
            {
                _log.LogAdd($"サーバーエラー [{msg.Command}]: {msg.MsgDetail}", _log.ERR);
                ErrorReceived?.Invoke(this, new ErrorReceivedEventArgs(msg.Command, msg.MsgDetail));
            }
        }

        // ─── 送信メソッド ────────────────────────────────────────────

        /// <summary>PR_LOGIN 送信</summary>
        public async Task<bool> SendLoginAsync()
        {
            var ws = AppSettings.Instance.WebSocketSettings;
            var req = new PR_LOGIN_Request
            {
                InstanceId  = ws.InstanceId,
                DisplayName = ws.DisplayName,
                Version     = "1.0.0"
            };
            var json = JsonSerializer.Serialize(req);
            var msg  = ParsedMessage.Build(ws.OrgCd, "", "PR_LOGIN", json);
            return await _wsClient.SendMessageAsync(msg);
        }

        /// <summary>PR_SEL_CMP 送信</summary>
        public async Task<bool> SendSelectCompetitionAsync(string orgCd, string cmpNo)
        {
            var req = new PR_SEL_CMP_Request { CmpNo = cmpNo };
            var json = JsonSerializer.Serialize(req);
            var msg  = ParsedMessage.Build(orgCd, cmpNo, "PR_SEL_CMP", json);
            return await _wsClient.SendMessageAsync(msg);
        }

        /// <summary>PR_ACK 送信</summary>
        public async Task<bool> SendAckAsync(string orgCd, string cmpNo, string jobId)
        {
            var req = new PR_ACK_Request { JobId = jobId, Status = "accepted" };
            var json = JsonSerializer.Serialize(req);
            var msg  = ParsedMessage.Build(orgCd, cmpNo, "PR_ACK", json);
            return await _wsClient.SendMessageAsync(msg);
        }

        /// <summary>PR_DONE 送信</summary>
        public async Task<bool> SendDoneAsync(string orgCd, string cmpNo, string jobId, string status, string? message = null)
        {
            var req = new PR_DONE_Request { JobId = jobId, Status = status, Message = message };
            var json = JsonSerializer.Serialize(req);
            var msg  = ParsedMessage.Build(orgCd, cmpNo, "PR_DONE", json);
            return await _wsClient.SendMessageAsync(msg);
        }
    }
}
