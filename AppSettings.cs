using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DSPrt
{
    /// <summary>
    /// 印刷部数設定の1行分（採点方式×ラウンド×帳票名×配布先部数）
    /// Excelの「印刷部数サンプル」の1行に対応する。
    /// </summary>
    public class PrintCopyRow
    {
        /// <summary>採点方式: "チェック法" / "順位法" / "AJS"</summary>
        public string 採点方式 { get; set; } = "";
        /// <summary>ラウンド: "予選" / "準決勝" / "決勝"</summary>
        public string ラウンド { get; set; } = "";
        /// <summary>GMReportPrint 画面のボタン名と一致する帳票名</summary>
        public string 帳票名 { get; set; } = "";
        /// <summary>対応する LayoutId（印刷指示時に使用）</summary>
        public string LayoutId { get; set; } = "";
        public int 司会 { get; set; } = 0;
        public int 掲示 { get; set; } = 0;
        public int 選手係 { get; set; } = 0;
        public int 本部 { get; set; } = 0;
        public int ステマネ { get; set; } = 0;
        public int 賞状 { get; set; } = 0;
        public int 保存 { get; set; } = 0;
        public int GM { get; set; } = 0;
        public int レフリー { get; set; } = 0;
        public int 予備1 { get; set; } = 0;
        public int 予備2 { get; set; } = 0;
    }

    /// <summary>
    /// 配布先ラベル名（帳票右上に印刷される文字列）の設定
    /// プロパティキーは PrintCopyRow の列名と1:1対応。
    /// </summary>
    public class RoleLabels
    {
        public string 司会   { get; set; } = "司会";
        public string 掲示   { get; set; } = "掲示";
        public string 選手係 { get; set; } = "選手係";
        public string 本部   { get; set; } = "本部";
        public string ステマネ { get; set; } = "ステマネ";
        public string 賞状   { get; set; } = "賞状";
        public string 保存   { get; set; } = "保存";
        public string GM     { get; set; } = "GM";
        public string レフリー { get; set; } = "レフリー";
        public string 予備1  { get; set; } = "予備1";
        public string 予備2  { get; set; } = "予備2";
    }

    /// <summary>
    /// WebSocket 接続設定
    /// </summary>
    public class WebSocketSettings
    {
        public string ServerIpAddress { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 7269;
        /// <summary>インスタンスを一意に識別する ID。電文の From フィールドに使用。Mutex のキー。</summary>
        public string InstanceId { get; set; } = "DSPrt_001";
        /// <summary>GM クライアントの DSPrt 選択ダイアログに表示する名称</summary>
        public string DisplayName { get; set; } = "DSPrt";
        public string OrgCd { get; set; } = "JS";
        public int ReconnectIntervalMs { get; set; } = 10000;
        public int ConnectionTimeoutMs { get; set; } = 30000;
        public bool AutoReconnect { get; set; } = true;
    }

    /// <summary>
    /// ログ設定
    /// </summary>
    public class LogSettings
    {
        public int LogLevel { get; set; } = 3;
        public string LogPath { get; set; } = "./Logs";
    }

    /// <summary>
    /// 印刷設定
    /// </summary>
    public class PrintSettings
    {
        public string DefaultPrinterName { get; set; } = "";
        public string AwardPrinterName { get; set; } = "";
        public string AwardLayoutPath { get; set; } = "";
        public string SpoolDirectory { get; set; } = "./Spool";
        public int MaxQueueSize { get; set; } = 50;
        public int JobLogMaxCount { get; set; } = 200;
        /// <summary>帳票右上に印刷する配布先ラベル名</summary>
        public RoleLabels RoleLabels { get; set; } = new RoleLabels();
    }

    /// <summary>
    /// 帳票レイアウト設定
    /// </summary>
    public class LayoutSetting
    {
        public string LayoutId { get; set; } = "";
        public string FrxPath { get; set; } = "";
        /// <summary>期待するデータ種別: "DA_Master" / "DS_Status" / "DV_Result"</summary>
        public string DataType { get; set; } = "DV_Result";
        public string PrinterName { get; set; } = "";
        public int Copies { get; set; } = 1;
        /// <summary>"OneSided" / "TwoSidedLongEdge" / "TwoSidedShortEdge"</summary>
        public string Duplex { get; set; } = "OneSided";
        /// <summary>"A4" / "A3" / "B5" 等</summary>
        public string PaperSize { get; set; } = "A4";
        /// <summary>GMReportPrint 画面でのボタン表示名</summary>
        public string DisplayName { get; set; } = "";
    }

    /// <summary>
    /// アプリケーション設定
    /// </summary>
    public class AppSettings
    {
        public WebSocketSettings WebSocketSettings { get; set; } = new WebSocketSettings();
        public LogSettings LogSettings { get; set; } = new LogSettings();
        public PrintSettings PrintSettings { get; set; } = new PrintSettings();
        public List<LayoutSetting> Layouts { get; set; } = new List<LayoutSetting>();
        /// <summary>配布先別印刷部数設定（Excelの印刷部数サンプルに対応する行リスト）</summary>
        public List<PrintCopyRow> PrintCopyRows { get; set; } = new List<PrintCopyRow>();

        private static AppSettings? _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// シングルトンインスタンス
        /// </summary>
        public static AppSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new AppSettings();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// 設定ファイルから読み込み
        /// </summary>
        public static bool Load(string filePath = "DSPrt.json")
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    System.Diagnostics.Debug.WriteLine($"設定ファイルが見つかりません: {filePath}");
                    Save(filePath);
                    return false;
                }

                string jsonString = File.ReadAllText(filePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(jsonString, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (settings != null)
                {
                    lock (_lock)
                    {
                        _instance = settings;
                    }
                    System.Diagnostics.Debug.WriteLine($"設定ファイル読み込み成功: {filePath}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"設定ファイル読み込みエラー: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 設定ファイルに保存
        /// </summary>
        public static bool Save(string filePath = "DSPrt.json")
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string jsonString = JsonSerializer.Serialize(Instance, options);
                File.WriteAllText(filePath, jsonString);
                System.Diagnostics.Debug.WriteLine($"設定ファイル保存成功: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"設定ファイル保存エラー: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// WebSocket 接続 URL を取得
        /// </summary>
        public string GetWebSocketUrl()
        {
            return $"ws://{WebSocketSettings.ServerIpAddress}:{WebSocketSettings.ServerPort}";
        }
    }
}
