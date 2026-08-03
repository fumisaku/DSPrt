using System;
using System.Text.Json.Nodes;

namespace DSPrt.Data
{
    /// <summary>
    /// DA_Master と DS_Status のメモリキャッシュ
    /// </summary>
    public class DataManager
    {
        private readonly LOG_C _log;
        private JsonNode? _daMaster;
        private JsonNode? _dsStatus;

        // ─── プロパティ ───────────────────────────────

        /// <summary>DA_Master（競技会マスター）</summary>
        public JsonNode? DA_Master
        {
            get => _daMaster;
            private set
            {
                _daMaster = value;
                DA_MasterUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>DS_Status（進行状況）</summary>
        public JsonNode? DS_Status
        {
            get => _dsStatus;
            private set
            {
                _dsStatus = value;
                DS_StatusUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>競技会番号</summary>
        public string? CmpNo { get; private set; }

        /// <summary>団体コード</summary>
        public string? OrgCd { get; private set; }

        // ─── イベント ────────────────────────────────

        public event EventHandler? DA_MasterUpdated;
        public event EventHandler? DS_StatusUpdated;

        // ─── コンストラクタ ───────────────────────────

        public DataManager(LOG_C log)
        {
            _log = log;
        }

        // ─── メソッド ────────────────────────────────

        /// <summary>DA_Master を設定（初期配信・全体再送共通）</summary>
        public void SetDA_Master(string json)
        {
            try
            {
                var node = JsonNode.Parse(json);
                if (node == null)
                {
                    _log.LogAdd("DA_Master JSONパースエラー: 結果が null", _log.ERR);
                    return;
                }

                DA_Master = node;

                OrgCd = node["DA_OrgCD"]?.ToString();
                CmpNo = node["DA_CompNo"]?.ToString();

                _log.LogAdd($"DA_Master設定完了: OrgCd={OrgCd}, CmpNo={CmpNo}", _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"DA_Master設定エラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>DS_Status を設定（初期配信・全体再送共通）</summary>
        public void SetDS_Status(string json)
        {
            try
            {
                var node = JsonNode.Parse(json);
                if (node == null)
                {
                    _log.LogAdd("DS_Status JSONパースエラー: 結果が null", _log.ERR);
                    return;
                }

                DS_Status = node;
                _log.LogAdd("DS_Status設定完了", _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"DS_Status設定エラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>すべてのキャッシュをクリア</summary>
        public void ClearAll()
        {
            _daMaster = null;
            _dsStatus = null;
            CmpNo = null;
            OrgCd = null;
            _log.LogAdd("DataManager クリア完了", _log.INFO);
        }
    }
}
