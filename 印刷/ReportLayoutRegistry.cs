using System.Collections.Generic;

namespace DSPrt.印刷
{
    /// <summary>
    /// DSPrt.json の Layouts 配列を読み込み、layoutId をキーに管理する。
    /// </summary>
    public class ReportLayoutRegistry
    {
        private readonly Dictionary<string, LayoutSetting> _map;
        private readonly LOG_C _log;

        public ReportLayoutRegistry(LOG_C log)
        {
            _log = log;
            _map = new Dictionary<string, LayoutSetting>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var layout in AppSettings.Instance.Layouts)
            {
                if (string.IsNullOrWhiteSpace(layout.LayoutId))
                {
                    _log.LogAdd("[ReportLayoutRegistry] layoutId が空のエントリをスキップ", _log.WARNING);
                    continue;
                }
                _map[layout.LayoutId] = layout;
                _log.LogAdd($"[ReportLayoutRegistry] 登録: layoutId={layout.LayoutId}, frx={layout.FrxPath}", _log.INFO);
            }
        }

        /// <summary>
        /// layoutId に対応する設定を返す。見つからない場合は null。
        /// </summary>
        public LayoutSetting? Get(string layoutId)
        {
            _map.TryGetValue(layoutId, out var setting);
            if (setting == null)
                _log.LogAdd($"[ReportLayoutRegistry] 未登録の layoutId: {layoutId}", _log.WARNING);
            return setting;
        }

        public bool Contains(string layoutId) => _map.ContainsKey(layoutId);

        public IEnumerable<LayoutSetting> All => _map.Values;
    }
}
