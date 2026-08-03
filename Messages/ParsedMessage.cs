using System;

namespace DSPrt.Messages
{
    /// <summary>
    /// パース済み電文
    /// フォーマット: OrgCd,CmpNo,From,Command,{JSON Body}
    /// </summary>
    public class ParsedMessage
    {
        /// <summary>団体コード</summary>
        public string OrgCd { get; set; } = string.Empty;
        /// <summary>競技会番号</summary>
        public string CmpNo { get; set; } = string.Empty;
        /// <summary>送信元（instanceId / SVR）</summary>
        public string From { get; set; } = string.Empty;
        /// <summary>コマンド名</summary>
        public string Command { get; set; } = string.Empty;
        /// <summary>メッセージ詳細（JSON 文字列）</summary>
        public string MsgDetail { get; set; } = string.Empty;
        /// <summary>元の電文</summary>
        public string RawMessage { get; set; } = string.Empty;

        /// <summary>
        /// 電文をパース
        /// </summary>
        public static ParsedMessage? Parse(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return null;

            try
            {
                // フォーマット: OrgCd,CmpNo,From,Command,MsgDetail
                var parts = message.Split(',', 5);
                if (parts.Length < 4)
                    return null;

                return new ParsedMessage
                {
                    OrgCd     = parts[0],
                    CmpNo     = parts[1],
                    From      = parts[2],
                    Command   = parts[3],
                    MsgDetail = parts.Length > 4 ? parts[4] : string.Empty,
                    RawMessage = message
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 電文を組み立てる。From フィールドに instanceId を使用。
        /// フォーマット: {orgCd},{cmpNo},{instanceId},{command},{json}
        /// </summary>
        public static string Build(string orgCd, string cmpNo, string command, string msgDetail = "")
        {
            var instanceId = AppSettings.Instance.WebSocketSettings.InstanceId;
            return $"{orgCd},{cmpNo},{instanceId},{command},{msgDetail}";
        }

        /// <summary>エラー応答かどうか</summary>
        public bool IsError => Command.EndsWith("_NG");

        public override string ToString()
            => $"[{Command}] OrgCd={OrgCd}, CmpNo={CmpNo}, From={From}";
    }
}
