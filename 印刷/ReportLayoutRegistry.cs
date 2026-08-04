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

        /// <summary>
        /// 指定 layoutId のプリンター名を実行時に更新する。
        /// AppSettings.Instance.Layouts の該当エントリも同時に更新するため、
        /// AppSettings.Save() を呼ぶことで DSPrt.json に永続化できる。
        /// </summary>
        public void UpdatePrinterName(string layoutId, string printerName)
        {
            if (!_map.TryGetValue(layoutId, out var setting)) return;

            setting.PrinterName = printerName;

            // AppSettings 側も同期（Save で永続化できるよう）
            var appLayout = AppSettings.Instance.Layouts
                .Find(l => string.Equals(l.LayoutId, layoutId, System.StringComparison.OrdinalIgnoreCase));
            if (appLayout != null)
                appLayout.PrinterName = printerName;

            _log.LogAdd($"[ReportLayoutRegistry] プリンター更新: layoutId={layoutId}, printer={printerName}", _log.INFO);
        }
    }
}
