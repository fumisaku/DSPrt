using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DSPrt.印刷
{
    /// <summary>
    /// サーバーが生成した HTML（Web採点結果表）を WebView2 で描画して印刷するサービス。
    /// PR_PRINT の Data.Html に HTML 本文が入っているジョブを対象とする。
    /// </summary>
    public sealed class HtmlPrintService
    {
        /// <summary>HTML 印刷対象の LayoutId 接頭辞</summary>
        public const string LayoutIdPrefix = "WEB_SCORE_RESULT_";

        private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);

        private readonly LOG_C _log;
        private readonly PrinterController _printerController;

        public HtmlPrintService(LOG_C log, PrinterController printerController)
        {
            _log = log;
            _printerController = printerController;
        }

        /// <summary>HTML 印刷ジョブかどうか（LayoutId と Data.Html の両方で判定）</summary>
        public static bool IsHtmlJob(PrintJob job)
        {
            return (job.LayoutId.StartsWith(LayoutIdPrefix, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(job.LayoutId, "JUDGE_ANALYSIS_SHEET", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(job.LayoutId, "WEB_JUDGE_ANALYSIS", StringComparison.OrdinalIgnoreCase))
                   && !string.IsNullOrEmpty(GetHtml(job));
        }

        private static string? GetHtml(PrintJob job)
        {
            return (job.Data as JsonObject)?["Html"]?.GetValue<string>();
        }

        /// <summary>
        /// HTML ジョブを印刷する。UI スレッドで実行すること。
        /// </summary>
        public async Task PrintAsync(PrintJob job, string? printerName = null)
        {
            string html = GetHtml(job) ?? throw new InvalidOperationException("Data.Html が空です。");

            printerName = string.IsNullOrWhiteSpace(printerName) ? ResolvePrinterName() : printerName;
            if (!string.IsNullOrEmpty(printerName) && !_printerController.PrinterExists(printerName))
            {
                _log.LogAdd($"[HtmlPrintService] プリンター '{printerName}' が見つかりません。Windows デフォルトプリンターを使用します。", _log.WARNING);
                printerName = _printerController.GetDefaultPrinterName();
            }

            // 画面に表示しない専用ウィンドウに WebView2 をホストする（WebView2 は HWND が必要）
            var window = new Window
            {
                Width = 1,
                Height = 1,
                Left = -10000,
                Top = -10000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize
            };
            var webView = new WebView2();
            window.Content = webView;

            try
            {
                window.Show();

                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DSPrt", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                var navigated = new TaskCompletionSource<bool>();
                webView.CoreWebView2.NavigationCompleted += (s, e) => navigated.TrySetResult(e.IsSuccess);
                webView.NavigateToString(html);

                var completed = await Task.WhenAny(navigated.Task, Task.Delay(NavigationTimeout));
                if (completed != navigated.Task || !navigated.Task.Result)
                    throw new InvalidOperationException("HTML の読み込みに失敗しました。");

                var settings = env.CreatePrintSettings();
                // Web採点結果（順位法）のみ A4 縦。それ以外（AJS/チェック法）は従来どおり横
                bool isPortrait = job.LayoutId.Equals("WEB_SCORE_RESULT_RANK", StringComparison.OrdinalIgnoreCase);
                settings.Orientation = isPortrait
                    ? CoreWebView2PrintOrientation.Portrait
                    : CoreWebView2PrintOrientation.Landscape;
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;
                settings.Copies = Math.Max(1, job.Copies);
                if (!string.IsNullOrEmpty(printerName))
                    settings.PrinterName = printerName;

                var status = await webView.CoreWebView2.PrintAsync(settings);
                if (status != CoreWebView2PrintStatus.Succeeded)
                    throw new InvalidOperationException($"HTML 印刷に失敗しました: status={status}");

                _log.LogAdd($"[HtmlPrintService] 印刷完了: jobId={job.JobId}, layoutId={job.LayoutId}, printer={printerName}", _log.INFO);
            }
            finally
            {
                webView.Dispose();
                window.Close();
            }
        }

        /// <summary>設定のデフォルトプリンター → Windows デフォルトプリンターの順で解決する</summary>
        private string ResolvePrinterName()
        {
            string fromSettings = AppSettings.Instance.PrintSettings.DefaultPrinterName;
            return !string.IsNullOrWhiteSpace(fromSettings)
                ? fromSettings
                : _printerController.GetDefaultPrinterName();
        }
    }
}
