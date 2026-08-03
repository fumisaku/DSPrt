using System;
using System.Text.Json.Nodes;

namespace DSPrt.印刷
{
    /// <summary>
    /// 印刷ジョブの状態
    /// </summary>
    public enum PrintJobStatus
    {
        Queued,
        Processing,
        Done,
        Error,
        Cancelled
    }

    /// <summary>
    /// 印刷ジョブデータモデル
    /// </summary>
    public class PrintJob
    {
        public string JobId { get; set; } = string.Empty;
        public string LayoutId { get; set; } = string.Empty;
        public int Copies { get; set; } = 1;
        public int Priority { get; set; } = 2;
        /// <summary>DV_Result 等の JSON データ（null の場合はキャッシュを参照）</summary>
        public JsonNode? Data { get; set; }

        public PrintJobStatus Status { get; set; } = PrintJobStatus.Queued;
        public DateTime ReceivedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }

        /// <summary>再印刷かどうか（jobId に "_R" サフィックスがある）</summary>
        public bool IsReprint => JobId.EndsWith("_R", StringComparison.Ordinal);

        /// <summary>再印刷ジョブを生成する</summary>
        public PrintJob CreateReprint()
        {
            return new PrintJob
            {
                JobId     = JobId.EndsWith("_R", StringComparison.Ordinal) ? JobId : JobId + "_R",
                LayoutId  = LayoutId,
                Copies    = Copies,
                Priority  = Priority,
                Data      = Data,
                Status    = PrintJobStatus.Queued,
                ReceivedAt = DateTime.Now
            };
        }
    }
}
