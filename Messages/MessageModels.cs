using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace DSPrt.Messages
{
    // ─────────────────────────────────────────
    //  共通
    // ─────────────────────────────────────────

    /// <summary>
    /// 競技会情報
    /// </summary>
    public class CompetitionInfo
    {
        public string OrgCd { get; set; } = string.Empty;
        public string CmpNo { get; set; } = string.Empty;
        public string CmpName { get; set; } = string.Empty;
        public string CmpDate { get; set; } = string.Empty;
    }

    /// <summary>
    /// エラー応答
    /// </summary>
    public class ErrorResponse
    {
        public string Error { get; set; } = string.Empty;
        public string? Reason { get; set; }
    }

    // ─────────────────────────────────────────
    //  グループ A: セッション管理
    // ─────────────────────────────────────────

    /// <summary>
    /// PR_LOGIN 要求（DSPrt → サーバー）
    /// </summary>
    public class PR_LOGIN_Request
    {
        public string InstanceId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
    }

    /// <summary>
    /// PR_LOGIN_OK 応答（サーバー → DSPrt）
    /// </summary>
    public class PR_LOGIN_OK_Response
    {
        public string AuthId { get; set; } = string.Empty;
        public string ServerVersion { get; set; } = string.Empty;
    }

    /// <summary>
    /// PR_ANS_CMP_LIST 応答（サーバー → DSPrt）
    /// </summary>
    public class PR_ANS_CMP_LIST_Response
    {
        public List<CompetitionInfo> Competitions { get; set; } = new List<CompetitionInfo>();
    }

    /// <summary>
    /// PR_SEL_CMP 要求（DSPrt → サーバー）
    /// </summary>
    public class PR_SEL_CMP_Request
    {
        public string CmpNo { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────
    //  グループ C: 印刷ジョブ制御
    // ─────────────────────────────────────────

    /// <summary>
    /// PR_PRINT 受信電文（サーバー → DSPrt）
    /// </summary>
    public class PR_PRINT_Request
    {
        public string JobId { get; set; } = string.Empty;
        public string LayoutId { get; set; } = string.Empty;
        public int Copies { get; set; } = 1;
        public int Priority { get; set; } = 2;
        /// <summary>DV_Result 等の JSON データ（空の場合はキャッシュを参照）</summary>
        public JsonNode? Data { get; set; }
    }

    /// <summary>
    /// PR_ACK 送信電文（DSPrt → サーバー）
    /// </summary>
    public class PR_ACK_Request
    {
        public string JobId { get; set; } = string.Empty;
        public string Status { get; set; } = "accepted";
    }

    /// <summary>
    /// PR_DONE 送信電文（DSPrt → サーバー）
    /// </summary>
    public class PR_DONE_Request
    {
        public string JobId { get; set; } = string.Empty;
        /// <summary>"done" | "error"</summary>
        public string Status { get; set; } = "done";
        public string? Message { get; set; }
    }

    /// <summary>
    /// PR_CANCEL 受信電文（サーバー → DSPrt）
    /// </summary>
    public class PR_CANCEL_Request
    {
        public string JobId { get; set; } = string.Empty;
    }
}
