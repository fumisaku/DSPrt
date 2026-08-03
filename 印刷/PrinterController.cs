using System;
using System.Printing;
using System.Threading.Tasks;

namespace DSPrt.印刷
{
    /// <summary>
    /// System.Printing ラッパー。
    /// プリンター一覧の取得・存在確認・デフォルトプリンター取得を提供する。
    /// 実際の印刷送信は ReportRenderer 内の FastReport が行うため、
    /// PrinterController は主にプリンター管理と検証を担当する。
    /// </summary>
    public class PrinterController
    {
        private readonly LOG_C _log;

        public PrinterController(LOG_C log)
        {
            _log = log;
        }

        /// <summary>
        /// 指定プリンターが Windows に存在するか確認する。
        /// </summary>
        public bool PrinterExists(string printerName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) return false;
            try
            {
                using var server = new LocalPrintServer();
                foreach (var queue in server.GetPrintQueues())
                {
                    if (string.Equals(queue.FullName, printerName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[PrinterController] プリンター確認エラー: {ex.Message}", _log.WARNING);
            }
            return false;
        }

        /// <summary>
        /// インストール済みプリンター名の一覧を返す。
        /// </summary>
        public System.Collections.Generic.List<string> GetInstalledPrinters()
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                using var server = new LocalPrintServer();
                foreach (var queue in server.GetPrintQueues())
                    result.Add(queue.FullName);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[PrinterController] プリンター一覧取得エラー: {ex.Message}", _log.WARNING);
            }
            return result;
        }

        /// <summary>
        /// デフォルトプリンター名を返す。
        /// </summary>
        public string GetDefaultPrinterName()
        {
            try
            {
                using var server = new LocalPrintServer();
                return server.DefaultPrintQueue.FullName;
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[PrinterController] デフォルトプリンター取得エラー: {ex.Message}", _log.WARNING);
                return string.Empty;
            }
        }

        /// <summary>
        /// layoutId に対応するプリンター名を解決する。
        /// layoutId が空または未設定の場合はデフォルトプリンターを返す。
        /// </summary>
        public string ResolvePrinterName(LayoutSetting layout)
        {
            // 1. レイアウト設定のプリンター
            if (!string.IsNullOrWhiteSpace(layout.PrinterName))
                return layout.PrinterName;

            // 2. 印刷設定のデフォルトプリンター
            string defaultFromSettings = AppSettings.Instance.PrintSettings.DefaultPrinterName;
            if (!string.IsNullOrWhiteSpace(defaultFromSettings))
                return defaultFromSettings;

            // 3. Windows のデフォルトプリンター
            return GetDefaultPrinterName();
        }
    }
}
