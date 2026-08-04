using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FastReport;
using FastReport.Export.Html;
using FastReport.Export.Image;
using FastReport.Utils;
using DSPrt.Data;
using Newtonsoft.Json.Linq;

namespace DSPrt.印刷
{
    /// <summary>
    /// FastReport (.frx) を用いた帳票レンダリングクラス。
    /// - DV_Result は PR_PRINT.data から取得
    /// - DA_Master / DS_Status は DataManager キャッシュから参照
    ///
    /// ■ 印刷方式
    ///   FastReport.OpenSource に Report.Print() は存在しないが、
    ///   PreparedPages.GetPage(i).Draw(FRPaintEventArgs) を使うことで
    ///   PNG 変換を経由せず PrintDocument.PrintPage の Graphics に
    ///   FastReport ページを直接描画できる（高品質・高速）。
    ///
    /// ■ プレビュー方式
    ///   HTMLExport (シングルページ HTML) でファイル出力し、
    ///   WPF WebBrowser または外部ブラウザで表示する。
    /// </summary>
    public class ReportRenderer
    {
        private readonly LOG_C _log;
        private readonly DataManager _dataManager;

        public ReportRenderer(LOG_C log, DataManager dataManager)
        {
            _log         = log;
            _dataManager = dataManager;

            // FastReport ライセンスダイアログを非表示にする（OS版では費用不要）
            FastReport.Utils.Config.WebMode = true;
        }

        // ─── 印刷 ────────────────────────────────────────────────────

        /// <summary>
        /// 帳票を描画して指定プリンターへ送る。
        /// PreparedPages.GetPage(i).Draw(FRPaintEventArgs) で PNG 変換なしに
        /// PrintDocument.PrintPage の Graphics へ直接描画する。
        /// </summary>
        public async Task PrintAsync(PrintJob job, LayoutSetting layout)
            => await PrintAsync(job, layout, null);

        /// <summary>
        /// printerNameOverride を指定するとレイアウト設定のプリンター名を上書きして印刷する。
        /// テスト印刷・プリンター選択ダイアログ経由の印刷に使用する。
        /// </summary>
        public async Task PrintAsync(PrintJob job, LayoutSetting layout, string? printerNameOverride)
        {
            await Task.Run(() =>
            {
                var effectiveLayout = string.IsNullOrWhiteSpace(printerNameOverride) ? layout
                    : new LayoutSetting
                    {
                        LayoutId    = layout.LayoutId,
                        FrxPath     = layout.FrxPath,
                        DataType    = layout.DataType,
                        PrinterName = printerNameOverride!,
                        Copies      = layout.Copies,
                        Duplex      = layout.Duplex,
                        PaperSize   = layout.PaperSize
                    };

                // ─── 対策1: ジャッジ票は1ページずつ個別 Prepare・印刷する ───────────
                // 全ページを1つの Report に詰め込むと report.Prepare() が全ページ分を
                // 一度にコンパイルするため、ページ数に比例して準備時間が急増する。
                // 1ページ（= 1ジャッジ × 1種目）ずつ Prepare → PrintDirect することで
                // 各 Prepare は常に1ページ分のみとなり大幅に高速化できる。
                string dtUpper = layout.DataType.ToUpperInvariant()
                    .Replace("+", "_").Replace(" ", "_");
                if (dtUpper == "DA_MASTER_DS_STATUS_JUDGE_SHEET")
                {
                    PrintJudgeSheetPerPage(job, effectiveLayout);
                    return;
                }

                // 1. FastReport で帳票を準備
                using var report = LoadAndPrepare(job, layout);

                int copies = job.Copies > 0 ? job.Copies : effectiveLayout.Copies;
                int pageCount = report.PreparedPages.Count;
                _log.LogAdd($"[ReportRenderer] 印刷開始: jobId={job.JobId}, printer={effectiveLayout.PrinterName}, copies={copies}, pages={pageCount}", _log.INFO);

                // 2. PNG を経由せず PrintDocument に直接描画して印刷
                PrintDirect(report, effectiveLayout, copies, job.JobId);

                _log.LogAdd($"[ReportRenderer] 印刷完了: jobId={job.JobId}", _log.INFO);
            });
        }

        /// <summary>
        /// ジャッジ票専用印刷メソッド（対策1）。
        /// ジャッジ×種目の組み合わせごとに1ページの Report を個別に Prepare・印刷する。
        /// これにより report.Prepare() のコストを常に1ページ分に抑える。
        /// </summary>
        private void PrintJudgeSheetPerPage(PrintJob job, LayoutSetting layout)
        {
            int copies = job.Copies > 0 ? job.Copies : layout.Copies;

            // ジャッジ票のコンテキスト（ジャッジリスト・種目リスト・ヒート情報等）を構築
            var ctx = BuildJudgeSheetContext(job.Data);
            if (ctx == null)
            {
                _log.LogAdd($"[ReportRenderer] BindJudgeSheet: コンテキスト構築失敗 jobId={job.JobId}", _log.WARNING);
                return;
            }

            int totalPages = ctx.TargetJudges.Count * Math.Max(1, ctx.Dances.Count);
            _log.LogAdd(
                $"[ReportRenderer] BindJudgeSheet 完了: kbn={ctx.KbnNo}/{ctx.KbnDspName}, rnd={ctx.RndName}, " +
                $"dances={ctx.Dances.Count}, judges={ctx.TargetJudges.Count}, totalPages={totalPages}",
                _log.INFO);
            _log.LogAdd($"[ReportRenderer] 印刷開始: jobId={job.JobId}, printer={layout.PrinterName}, copies={copies}, pages={totalPages}", _log.INFO);

            string frxPath = ResolveFrxPath(layout.FrxPath);
            if (!File.Exists(frxPath))
                throw new FileNotFoundException($".frx ファイルが見つかりません: {frxPath}");

            foreach (var (jdgCd, jdgName) in ctx.TargetJudges)
            {
                for (int dncIdx = 0; dncIdx < Math.Max(1, ctx.Dances.Count); dncIdx++)
                {
                    // 対策3: スクリプトなし frx のためコンパイルをスキップして Prepare する
                    using var report = LoadFrxAndPrepareNoScript(frxPath);

                    // 1ページ分のデータをセット（suffix = "" : 常に1ページ目のオブジェクト名）
                    ApplyJudgeSheetPage(report, ctx, jdgCd, jdgName, dncIdx, suffix: "");

                    PrintDirect(report, layout, copies, job.JobId);
                }
            }

            _log.LogAdd($"[ReportRenderer] 印刷完了: jobId={job.JobId}", _log.INFO);
        }

        /// <summary>
        /// 帳票を HTML としてファイル保存する（プレビュー用）。
        /// FastReport.OpenSource には PDF エクスポートが含まれないため HTML を使用する。
        /// </summary>
        public async Task<string> ExportHtmlAsync(PrintJob job, LayoutSetting layout, string outputDir)
        {
            return await Task.Run(() =>
            {
                using var report = LoadAndPrepare(job, layout);

                Directory.CreateDirectory(outputDir);
                string filePath = Path.Combine(outputDir, $"{job.JobId}_{DateTime.Now:yyyyMMddHHmmss}.html");

                var htmlExport = new HTMLExport
                {
                    EmbedPictures = true,
                    SinglePage    = true,
                    Format        = HTMLExportFormat.HTML
                };
                report.Export(htmlExport, filePath);

                _log.LogAdd($"[ReportRenderer] HTML プレビュー出力: {filePath}", _log.INFO);
                return filePath;
            });
        }

        // ─── 内部処理 ────────────────────────────────────────────────

        /// <summary>
        /// .frx を読み込み、データをバインドして Prepare 済みの Report を返す。
        /// </summary>
        private Report LoadAndPrepare(PrintJob job, LayoutSetting layout)
        {
            string frxPath = ResolveFrxPath(layout.FrxPath);
            if (!File.Exists(frxPath))
                throw new FileNotFoundException($".frx ファイルが見つかりません: {frxPath}");

            var report = new Report();
            report.Load(frxPath);

            // Load 直後: DM_Masters DataSource 存在確認
            var dsCheck = report.Dictionary.DataSources.FindByName("DM_Masters");
            _log.LogAdd($"[FR-DEBUG] after Load: DM_Masters={dsCheck?.GetType().Name ?? "NOT FOUND"}", _log.INFO);

            // データ種別に応じてバインド（Load 後、Prepare 前）
            string dt = layout.DataType.ToUpperInvariant()
                .Replace("+", "_").Replace(" ", "_");

            switch (dt)
            {
                case "DV_RESULT":
                    BindDvResult(report, job.Data);
                    break;

                case "DA_MASTER":
                    BindDaMaster(report);
                    break;

                case "DS_STATUS":
                    BindDsStatus(report);
                    break;

                case "DA_MASTER_DS_STATUS":
                    BindDaMaster(report);
                    BindDsStatus(report);
                    // 出場者連絡票用: DA_Master + DS_Status から HeatPlayers テーブルとヘッダーパラメーターを生成
                    // DS_Status がない場合は job.Data（テストデータ）からフォールバック
                    BindPlayerNoticeExtra(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_HORIZONTAL":
                    // 出場者連絡票（横向き・ヒート表_横.frx）
                    // job.Data に { "KbnNo":"...", "RndNo":"...", "DGrpNo":"..." } を含む
                    BindPlayerNoticeHorizontal(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_VERTICAL":
                    // 出場者ヒート表（縦向き・出場者ヒート表_縦.frx）
                    // job.Data に { "KbnNo":"...", "RndNo":"...", "DGrpNo":"..." } を含む
                    BindPlayerHeatVertical(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_AJS_FINAL":
                    // 出場者連絡票（AJS決勝用・出場者連絡票_AJS決勝.frx）
                    // job.Data に { "KbnNo":"...", "RndNo":"...", "DGrpNo":"..." } を含む
                    // DataBand に1行のダミー DataTable をバインドして DataSource エラーを回避
                    {
                        var dummyTable = new System.Data.DataTable("AjsFinalDummy");
                        dummyTable.Columns.Add("Idx", typeof(int));
                        dummyTable.Rows.Add(0);
                        report.RegisterData(dummyTable, "AjsFinalDummy");
                    }
                    BindPlayerNoticeAjsFinal(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_FINAL_ENTRY":
                    // 決勝進出者名簿（横向き・決勝進出者名簿_横.frx）
                    // job.Data に { "KbnNo":"...", "RndNo":"...", "DGrpNo":"..." } を含む
                    // DataBand に1行のダミー DataTable をバインドして DataSource エラーを回避
                    {
                        var dummyFe = new System.Data.DataTable("FinalEntryDummy");
                        dummyFe.Columns.Add("Idx", typeof(int));
                        dummyFe.Rows.Add(0);
                        report.RegisterData(dummyFe, "FinalEntryDummy");
                    }
                    BindFinalEntryList(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_SEMI_FINAL_ENTRY":
                    // 準決勝進出者名簿（縦向き・準決勝進出者名簿_縦.frx）
                    // job.Data に { "KbnNo":"...", "RndNo":"...", "DGrpNo":"..." } を含む
                    // DataBand に1行のダミー DataTable をバインドして DataSource エラーを回避
                    {
                        var dummySfe = new System.Data.DataTable("SemiFinalEntryDummy");
                        dummySfe.Columns.Add("Idx", typeof(int));
                        dummySfe.Rows.Add(0);
                        report.RegisterData(dummySfe, "SemiFinalEntryDummy");
                    }
                    BindSemiFinalEntryList(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_DV_RESULT_AJS_SCORE":
                    // 得点一覧表（AJS採点方式・横向き・得点一覧表_AJS_横.frx）
                    // job.Data に { "KbnNo":"...", "RndNo":"...", "DGrpNo":"..." } と DV_Result を含む
                    {
                        var dummyAjs = new System.Data.DataTable("AjsScoreDummy");
                        dummyAjs.Columns.Add("Idx", typeof(int));
                        dummyAjs.Rows.Add(0);
                        report.RegisterData(dummyAjs, "AjsScoreDummy");
                    }
                    BindAjsScoreList(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_DV_RESULT_FINAL_AWARD":
                    // 決勝入賞者名簿（縦向き・決勝入賞者名簿_縦.frx）
                    // job.Data に DV_Result + { "KbnNo":"...", "RndNo":"..." } を含む
                    {
                        var dummyFa = new System.Data.DataTable("FinalAwardDummy");
                        dummyFa.Columns.Add("Idx", typeof(int));
                        dummyFa.Rows.Add(0);
                        report.RegisterData(dummyFa, "FinalAwardDummy");
                    }
                    BindFinalAwardList(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_JUDGE_SHEET":
                    // ジャッジ票（横向き・ジャッジ票_横.frx）
                    // job.Data に { "KbnNo":"...", "RndNo":"...", "DGrpNo":"...", "JudgeCd":"..." } を含む
                    // JudgeCd が空の場合は全ジャッジ分を一括印刷（ジャッジごとにまとめる）
                    // JudgeCd が指定の場合は、そのジャッジ分のみ印刷
                    // 種目ごとにページを分けて印刷する
                    BindJudgeSheet(report, job.Data);
                    break;

                case "DA_MASTER_DS_STATUS_DV_RESULT_CHECK_SCORE":
                    // 得点一覧表（チェック法採点方式）
                    // layoutId の末尾 "_PORT" で縦版、それ以外は横版
                    {
                        var dummyChk = new System.Data.DataTable("CheckScoreDummy");
                        dummyChk.Columns.Add("Idx", typeof(int));
                        dummyChk.Rows.Add(0);
                        report.RegisterData(dummyChk, "CheckScoreDummy");
                    }
                    if (job.LayoutId?.EndsWith("_PORT", StringComparison.OrdinalIgnoreCase) == true)
                        // 縦版: A4縦 最大32行・最大30ジャッジ列/ページ（C06〜C39=34列）
                        BindCheckScoreList(report, job.Data, pairsPerPage: 32,
                            maxJudgeColsPerPage: 30, maxFrxCheckCols: 34);
                    else
                        // 横版: A4横 最大20行・最大65ジャッジ列/ページ（C06〜C74=69列）
                        BindCheckScoreList(report, job.Data, pairsPerPage: 20,
                            maxJudgeColsPerPage: 65, maxFrxCheckCols: 69);
                    break;

                case "DS_STATUS_DV_RESULT":
                    BindDsStatus(report);
                    BindDvResult(report, job.Data);
                    break;

                case "DA_MASTER_DV_RESULT":
                    BindDaMaster(report);
                    BindDvResult(report, job.Data);
                    break;

                default:
                    _log.LogAdd($"[ReportRenderer] 不明な dataType '{layout.DataType}'、DV_Result として処理", _log.WARNING);
                    BindDvResult(report, job.Data);
                    break;
            }

            // Prepare 直前: DM_Masters 接続状態確認
            var dsBeforePrepare = report.Dictionary.DataSources.FindByName("DM_Masters") as FastReport.Data.TableDataSource;
            _log.LogAdd($"[FR-DEBUG] before Prepare: DM_Masters table={dsBeforePrepare?.Table?.TableName ?? "null"}, rows={dsBeforePrepare?.Table?.Rows.Count.ToString() ?? "N/A"}", _log.INFO);

            // Prepare 時のみ WebMode=false（スクリプトコンパイルを有効化）
            // WebMode=true のままでは [DataSource.Field] 式がコンパイルできない
            bool prevWebMode = FastReport.Utils.Config.WebMode;
            FastReport.Utils.Config.WebMode = false;
            try
            {
                report.Prepare();
            }
            finally
            {
                FastReport.Utils.Config.WebMode = prevWebMode;
            }
            return report;
        }

        /// <summary>
        /// .frx パスを直接受け取り、スクリプトコンパイルなしで Prepare する（対策3）。
        /// スクリプト・DataSource を持たない frx（ジャッジ票等）専用。
        /// FastReport は WebMode=true のときスクリプトをコンパイルしない。
        /// この frx はスクリプトを使用しないため WebMode をそのまま true に保って Prepare することで
        /// C# コンパイラ起動を省略し高速化する。
        /// </summary>
        private static Report LoadFrxAndPrepareNoScript(string frxPath)
        {
            var report = new Report();
            report.Load(frxPath);
            // WebMode=true のまま Prepare() を呼ぶことでスクリプトコンパイルをスキップする（対策3）
            // ジャッジ票 frx はスクリプト・DataSource・式を一切使わないため問題なし
            report.Prepare();
            return report;
        }

        /// <summary>
        /// FastReport の PreparedPages を PrintDocument の Graphics に直接描画して印刷する。
        /// PNG 変換を経由しないため高品質・高速。
        ///
        /// ■ FastReport 座標系
        ///   - PaperWidth / PaperHeight : mm 単位（frx の属性値と同じ）
        ///   - ReportPage.Width / Height（継承 ComponentBase）: FastReport 内部単位（96dpi px）
        ///     WidthInPixels  = PaperWidth  * Units.Millimeters  (= PaperWidth  * 3.78f)
        ///     HeightInPixels = PaperHeight * Units.Millimeters  (= PaperHeight * 3.78f)
        ///   - 各 ReportComponent の AbsLeft/AbsTop/Width/Height : 内部単位（96dpi px）
        ///
        /// ■ FRPaintEventArgs(g, scaleX, scaleY, cache) の scaleX/scaleY
        ///   「内部単位(px) × scaleX = Graphics 座標系の1単位」（IsVisible の実装より確認済み）
        ///
        /// ■ PrintDocument の Graphics の DPI
        ///   Microsoft Print to PDF = 600dpi、実プリンターは 300〜1200dpi 等様々。
        ///   GraphicsUnit.Display は 100dpi 相当、GraphicsUnit.Pixel はプリンター実 DPI。
        ///
        /// ■ 採用方式: GraphicsUnit.Pixel + scaleX = PrinterDpi / 96f
        ///   - FastReport 内部単位 = 96dpi px
        ///   - プリンターが 600dpi なら 1内部単位 = 600/96 ≈ 6.25 プリンター px
        ///   - フォントも含めてすべて物理 DPI に合わせてスケールされるため正確な印刷が得られる
        /// </summary>
        private void PrintDirect(Report report, LayoutSetting layout, int copies, string jobId)
        {
            int pageIndex = 0;
            int pageCount = report.PreparedPages.Count;

            // FastReport 内部単位（96dpi px）
            // Units.Millimeters = 3.78f : 1mm = 3.78 内部単位
            const float frUnitsPerMm = 3.78f;

            var doc = new PrintDocument();
            doc.DocumentName = jobId;

            // プリンター設定
            if (!string.IsNullOrWhiteSpace(layout.PrinterName))
                doc.PrinterSettings.PrinterName = layout.PrinterName;

            doc.PrinterSettings.Copies = (short)Math.Min(copies, short.MaxValue);

            // 余白を 0 に（プリンタードライバーのデフォルト余白を除去）
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            // 用紙サイズを設定
            SetPaperSize(doc, layout.PaperSize);

            // 両面印刷
            if (layout.Duplex == "TwoSidedLongEdge")
                doc.PrinterSettings.Duplex = Duplex.Horizontal;
            else if (layout.Duplex == "TwoSidedShortEdge")
                doc.PrinterSettings.Duplex = Duplex.Vertical;
            else
                doc.PrinterSettings.Duplex = Duplex.Simplex;

            // 用紙向きを Print() 前に設定する（イベント内での変更は反映されないため）
            // 最初のページの PaperWidth/PaperHeight で判定する
            if (pageCount > 0)
            {
                var firstPage = report.PreparedPages.GetPage(0);
                bool firstIsLandscape = firstPage.PaperWidth > firstPage.PaperHeight;
                doc.DefaultPageSettings.Landscape = firstIsLandscape;
                _log.LogAdd($"[PrintDirect] 用紙向き: {(firstIsLandscape ? "横" : "縦")} ({firstPage.PaperWidth}×{firstPage.PaperHeight}mm)", _log.INFO);
            }

            doc.PrintPage += (sender, e) =>
            {
                if (pageIndex >= pageCount) { e.HasMorePages = false; return; }

                var page = report.PreparedPages.GetPage(pageIndex++);

                // IsPrinting=true のとき TextObject のフォントサイズが scaleX でスケールされない。
                // GetPage() で取得した PreparedPage の TextObject を scaleX 倍に補正する。
                // scaleX = 100/96 ≈ 1.042 だが体感では変化が小さすぎるため微調整する。
                AdjustFontSizesForPrinting(page, 1.2f);

                var g = e.Graphics!;

                // PrintDocument の PrintPage イベントの Graphics は
                // PageUnit = Display (= 100dpi 相当) がデフォルト。
                // g.DpiX はプリンターの物理解像度（600dpi 等）を返すが、
                // 座標系は 100dpi 論理ピクセル。
                //
                // FastReport 内部単位 = 96dpi px
                //   1内部単位 = 1/96 inch
                //   PageUnit=Display の 1単位 = 1/100 inch
                //   → scaleX = (1/96) / (1/100) = 100/96 ≈ 1.042
                float scaleX = g.DpiX / 96f;
                float scaleY = g.DpiY / 96f;
                // ただし PageUnit=Display 座標系では g.DpiX は論理 DPI (100) を使うべき
                // g.DpiX がプリンター物理 DPI (600) を返す場合は論理 DPI で上書き
                const float logicalDpi = 100f;
                if (g.PageUnit == System.Drawing.GraphicsUnit.Display)
                {
                    scaleX = logicalDpi / 96f;  // ≈ 1.042
                    scaleY = logicalDpi / 96f;
                }

                _log.LogAdd($"[PrintDirect] g.DpiX={g.DpiX}, PageUnit={g.PageUnit}, scaleX={scaleX:F3}", _log.INFO);

                // ReportPage.Width/Height は内部単位(px@96dpi)
                // PaperWidth は mm なので Units.Millimeters(=3.78) を掛けて内部単位に変換
                page.Width  = page.PaperWidth  * frUnitsPerMm;
                page.Height = page.PaperHeight * frUnitsPerMm;


                var paintArgs = new FRPaintEventArgs(
                    g,
                    scaleX,
                    scaleY,
                    report.GraphicCache);

                page.Draw(paintArgs);

                e.HasMorePages = pageIndex < pageCount;
            };

            doc.Print();
        }

        /// <summary>
        /// PrintDocument 印刷時のフォントサイズ補正。
        /// FastReport の TextObject は IsPrinting=true（印刷時デフォルト）のとき
        /// フォントサイズを scaleX でスケールしない。その結果レイアウト座標が scaleX 倍されるのに
        /// フォントサイズだけ据え置きになり、相対的に小さく見える。
        /// PreparedPages.GetPage() で取得した ReportPage の TextObject を直接補正する。
        /// </summary>
        private void AdjustFontSizesForPrinting(FastReport.ReportPage page, float scaleX)
        {
            if (Math.Abs(scaleX - 1.0f) < 0.001f) return;
            int count = 0;
            // 対策2: 同一 FontFamily+Size+Style の Font を使い回すキャッシュ
            // new Font() は GDI リソースを確保するためページ内で何度も呼ぶとコストが高い
            var fontCache = new Dictionary<(string familyName, float size, System.Drawing.FontStyle style), System.Drawing.Font>();
            AdjustFontsInObject(page, scaleX, ref count, fontCache);
            _log.LogAdd($"[PrintDirect] フォントサイズ補正: {count}個 ×{scaleX:F4}", _log.INFO);
        }

        private static void AdjustFontsInObject(
            object obj, float scaleX, ref int count,
            Dictionary<(string, float, System.Drawing.FontStyle), System.Drawing.Font> fontCache)
        {
            if (obj is FastReport.TextObject txt)
            {
                float newSize = txt.Font.Size * scaleX;
                var key = (txt.Font.FontFamily.Name, newSize, txt.Font.Style);
                if (!fontCache.TryGetValue(key, out var cachedFont))
                {
                    cachedFont = new System.Drawing.Font(
                        txt.Font.FontFamily,
                        newSize,
                        txt.Font.Style,
                        System.Drawing.GraphicsUnit.Point);
                    fontCache[key] = cachedFont;
                }
                txt.Font = cachedFont;
                count++;
            }
            if (obj is FastReport.IParent parent)
            {
                var children = new FastReport.ObjectCollection();
                parent.GetChildObjects(children);
                foreach (FastReport.Base child in children)
                    AdjustFontsInObject(child, scaleX, ref count, fontCache);
            }
        }

        private static void SetPaperSize(PrintDocument doc, string paperSizeName)
        {
            if (string.IsNullOrWhiteSpace(paperSizeName)) return;
            foreach (PaperSize ps in doc.PrinterSettings.PaperSizes)
            {
                if (string.Equals(ps.PaperName, paperSizeName, StringComparison.OrdinalIgnoreCase))
                {
                    doc.DefaultPageSettings.PaperSize = ps;
                    return;
                }
            }
        }

        // ─── バインドメソッド ────────────────────────────────────────

        private void BindDvResult(Report report, JsonNode? data)
        {
            if (data == null)
            {
                _log.LogAdd("[ReportRenderer] DV_Result データが null です", _log.WARNING);
                return;
            }
            var ds = JsonNodeToDataSet(data, "DV_Result");
            foreach (DataTable dt in ds.Tables)
                report.RegisterData(dt, dt.TableName);

            // Heats[].Players[] 構造があれば HeatPlayers フラットテーブルも生成して登録する
            // （出場者連絡票等のヒート一覧帳票向け）
            var heatPlayersTable = TryBuildHeatPlayersTable(data);
            if (heatPlayersTable != null)
                report.RegisterData(heatPlayersTable, "HeatPlayers");

            _log.LogAdd("[ReportRenderer] DV_Result バインド完了", _log.INFO);
        }

        /// <summary>
        /// data に Heats[].Players[] 構造がある場合、フラットな HeatPlayers DataTable を生成する。
        /// 列: HeatNo, PlayerNo, PlayerName
        /// </summary>
        private DataTable? TryBuildHeatPlayersTable(JsonNode data)
        {
            try
            {
                string json = data.ToJsonString();
                var jObj = JObject.Parse(json);
                var heats = jObj["Heats"] as JArray;
                if (heats == null || heats.Count == 0) return null;

                var dt = new DataTable("HeatPlayers");
                dt.Columns.Add("HeatNo",     typeof(string));
                dt.Columns.Add("PlayerNo",   typeof(string));
                dt.Columns.Add("PlayerName", typeof(string));

                foreach (var heat in heats)
                {
                    string heatNo = heat["HeatNo"]?.ToString() ?? "";
                    var players = heat["Players"] as JArray;
                    if (players == null) continue;
                    foreach (var player in players)
                    {
                        var row = dt.NewRow();
                        row["HeatNo"]     = heatNo;
                        row["PlayerNo"]   = player["PlayerNo"]?.ToString()   ?? "";
                        row["PlayerName"] = player["PlayerName"]?.ToString() ?? "";
                        dt.Rows.Add(row);
                    }
                }

                // Header テーブルも生成（KbnNo, KbnName 等のスカラー値）
                _log.LogAdd($"[ReportRenderer] HeatPlayers テーブル生成: {dt.Rows.Count} 行", _log.INFO);
                return dt;
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] HeatPlayers テーブル生成エラー: {ex.Message}", _log.WARNING);
                return null;
            }
        }

        private void BindDaMaster(Report report)
        {
            if (_dataManager.DA_Master == null)
            {
                _log.LogAdd("[ReportRenderer] DA_Master キャッシュが空です", _log.WARNING);
                return;
            }

            try
            {
                string json = _dataManager.DA_Master.ToJsonString();
                var root = JObject.Parse(json);

                // ─── 競技会ヘッダーをパラメーターとして設定
                SetReportParam(report, "CompName",  root["DA_CompName"]?.ToString() ?? "");
                SetReportParam(report, "CompDate",  root["DA_CompDate"]?.ToString() ?? "");
                SetReportParam(report, "CompPlace", root["DA_CompPlace"]?.ToString() ?? "");
                SetReportParam(report, "OrgCD",     root["DA_OrgCD"]?.ToString() ?? "");
                SetReportParam(report, "CmpNo",     root["DA_CompNo"]?.ToString() ?? "");

                // ─── DM_Masters テーブル（全選手フラットリスト）
                var memberTable = new DataTable("DM_Masters");
                string[] memberCols = { "DM_No","DM_LDispName","DM_PDispName",
                    "DM_Ctry","DM_OrgName","DM_ENTRYs" };
                foreach (var c in memberCols)
                    memberTable.Columns.Add(c, typeof(string));

                // DM_MEMBERs[] > DM_MASTERs[] を展開してフラット化
                var dmMembers = root["DM_MEMBERs"] as JArray;
                if (dmMembers != null)
                {
                    foreach (var member in dmMembers)
                    {
                        var masters = member["DM_MASTERs"] as JArray;
                        if (masters == null) continue;
                        foreach (var m in masters)
                        {
                            var row = memberTable.NewRow();
                            foreach (var c in memberCols)
                                row[c] = m[c]?.ToString() ?? "";
                            memberTable.Rows.Add(row);
                        }
                    }
                }

                // FindByName で取得した既存 TableDataSource の .Table に直接セット
                // （RegisterData は別インスタンスを生成するため既存 DataSource に反映されない）
                BindTableToReport(report, memberTable);

                _log.LogAdd($"[ReportRenderer] DA_Master バインド完了: 選手={memberTable.Rows.Count}件", _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] DA_Master バインドエラー: {ex.Message}", _log.ERR);
                throw;
            }
        }

        /// <summary>
        /// レポートパラメーターを安全にセットする。
        /// パラメーターが .frx に定義されていない場合は無視する。
        /// </summary>
        private static void SetReportParam(Report report, string name, object value)
        {
            try { report.SetParameterValue(name, value); }
            catch { /* .frx に未定義のパラメーターは無視 */ }
        }

        private void BindDsStatus(Report report)
        {
            if (_dataManager.DS_Status == null)
            {
                _log.LogAdd("[ReportRenderer] DS_Status キャッシュが空です", _log.WARNING);
                return;
            }
            var ds = JsonNodeToDataSet(_dataManager.DS_Status, "DS_Status");
            foreach (DataTable dt in ds.Tables)
                BindTableToReport(report, dt);
            _log.LogAdd("[ReportRenderer] DS_Status バインド完了", _log.INFO);
        }

        /// <summary>
        /// 出場者連絡票（PLAYER_NOTICE_A4）専用バインド。
        /// DA_Master + DS_Status から以下を生成してレポートにセットする:
        ///   - Parameter: KbnNo, KbnName, RndName, RndFlagsText, ScrMtd, Dances, TotalEntries, PickupCount
        ///   - DataTable "HeatPlayers": HeatNo, PlayerNo, PlayerName
        ///
        /// DS_Status の構造:
        ///   DS_FLOORs[].DS_PRGRSs[]:
        ///     DS_KbnNo, DS_RndNo
        ///     PlayerAssignments[]: PlayerNo, AssignedHeatIds[]
        ///     DS_PRGDANCEs[0].DS_PRGHEATs[]: DS_HeatId, DS_HeatNo
        ///
        /// ロジック:
        ///   「現在進行中（DS_PrgSts != 9完了）のラウンドのうち最初の1件」
        ///   または「全ての中で最初の1件」を対象ラウンドとして使用する。
        ///   DS_PRGDANCEs[0]（第1種目）の DS_PRGHEATs を基に HeatId→HeatNo マップを作成。
        ///   PlayerAssignments の AssignedHeatIds[0] を使いヒート番号を解決する。
        /// </summary>
        /// <summary>
        /// DA_Master の DM_MEMBERs から選手情報マップ（背番号→選手情報）を構築する。
        /// targetMasNo: 使用するマスタ番号（DB_KbnSenM の値）。
        ///   指定マスタ番号が存在しない場合はブランク（フォールバックなし）。
        /// </summary>
        private static Dictionary<string, (string lName, string pName, string lKana, string pKana, string lCtry, string pCtry)>
            BuildPlayerInfoMap(JObject daJson, int targetMasNo)
        {
            var result = new Dictionary<string, (string, string, string, string, string, string)>();
            var members = daJson["DM_MEMBERs"] as JArray;
            if (members == null) return result;

            foreach (var m in members.OfType<JObject>())
            {
                var masterList = m["DM_MASTERs"] as JArray;
                if (masterList == null) continue;

                // 指定マスタ番号のエントリのみを使用（存在しない場合はスキップ）
                JObject? chosen = masterList.OfType<JObject>()
                    .FirstOrDefault(ms => ms["DM_MasNo"]?.ToObject<int>() == targetMasNo);

                if (chosen == null) continue;

                string no    = chosen["DM_No"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(no)) continue;

                string lName = chosen["DM_LDispName"]?.ToString() ?? "";
                string pName = chosen["DM_PDispName"]?.ToString() ?? "";
                string lKana = chosen["DM_LKana"]?.ToString() ?? "";
                string pKana = chosen["DM_PKana"]?.ToString() ?? "";
                string ctry  = chosen["DM_Ctry"]?.ToString()  ?? "";
                string lCtryRaw = chosen["DM_LCtry"]?.ToString() ?? "";
                string pCtryRaw = chosen["DM_PCtry"]?.ToString() ?? "";
                string lCtry = string.IsNullOrEmpty(lCtryRaw) ? ctry : lCtryRaw;
                string pCtry = string.IsNullOrEmpty(pCtryRaw) ? ctry : pCtryRaw;

                result[no] = (lName, pName, lKana, pKana, lCtry, pCtry);
            }
            return result;
        }

        /// <summary>
        /// DA_Master の DM_MEMBERs から選手マスタマップ（背番号→JObject）を構築する。
        /// targetMasNo: 使用するマスタ番号（DB_KbnSenM の値）。
        ///   指定マスタ番号が存在しない場合はその選手はマップに含めない（フォールバックなし）。
        /// </summary>
        private static Dictionary<string, JObject>
            BuildMasterMap(JObject daJson, int targetMasNo)
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var members = daJson["DM_MEMBERs"] as JArray;
            if (members == null) return result;

            foreach (var m in members.OfType<JObject>())
            {
                var masterList = m["DM_MASTERs"] as JArray;
                if (masterList == null) continue;

                // 指定マスタ番号のエントリのみを使用（存在しない場合はスキップ）
                JObject? chosen = masterList.OfType<JObject>()
                    .FirstOrDefault(ms => ms["DM_MasNo"]?.ToObject<int>() == targetMasNo);

                if (chosen == null) continue;

                string no = chosen["DM_No"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(no) && !result.ContainsKey(no))
                    result[no] = chosen;
            }
            return result;
        }

        private void BindPlayerNoticeExtra(Report report, JsonNode? fallbackData)
        {
            // DS_Status/DA_Master がない場合（テスト時等）は job.Data の Heats[] からフォールバック
            if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
            {
                if (fallbackData != null)
                    BindPlayerNoticeFromFallback(report, fallbackData);
                return;
            }

            try
            {
                var dsJson  = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                var daJson  = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                // ── 対象ラウンドを選択（現在進行中 or 最初のラウンド）──────────────
                JObject? prgrs = null;
                var floors = dsJson["DS_FLOORs"] as JArray;
                if (floors != null)
                {
                    foreach (var floor in floors)
                    {
                        var prgrsList = floor["DS_PRGRSs"] as JArray;
                        if (prgrsList == null) continue;
                        // 進行中（PrgSts != "9"）を優先
                        prgrs = prgrsList
                            .OfType<JObject>()
                            .FirstOrDefault(p => p["DS_PrgSts"]?.ToString() != "9")
                            ?? prgrsList.OfType<JObject>().FirstOrDefault();
                        if (prgrs != null) break;
                    }
                }

                if (prgrs == null)
                {
                    _log.LogAdd("[ReportRenderer] PlayerNotice: 対象ラウンドが見つかりません", _log.WARNING);
                    return;
                }

                string kbnNo = prgrs["DS_KbnNo"]?.ToString() ?? "";
                string rndNo = prgrs["DS_RndNo"]?.ToString() ?? "";

                // ── DA_Master から区分名・ラウンド名・種目名を解決 ──────────────────
                string kbnName = "", rndName = "", dances = "", scrMtd = "";
                var kbunList = daJson["DB_KUBUNs"] as JArray;
                JObject? kbnObj = kbunList?.OfType<JObject>()
                    .FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);
                if (kbnObj != null)
                {
                    kbnName = kbnObj["DB_KbnDispName"]?.ToString()
                              ?? kbnObj["DB_KbnName"]?.ToString() ?? "";

                    var rndList = kbnObj["DC_ROUNDs"] as JArray;
                    var rndObj  = rndList?.OfType<JObject>()
                        .FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);
                    if (rndObj != null)
                        rndName = rndObj["DC_RndName_J"]?.ToString() ?? rndNo;

                    // 種目コード・採点方式
                    var dgList   = kbnObj["DC_ROUNDs"]?.OfType<JObject>()
                        .FirstOrDefault()?["DD_DGRPs"] as JArray;
                    var dgrp     = dgList?.OfType<JObject>().FirstOrDefault();
                    var dncList  = dgrp?["DE_DANCEs"] as JArray;
                    if (dncList != null)
                        dances = string.Join(" ", dncList.Select(d => d["DE_DncCd"]?.ToString() ?? ""));

                    scrMtd = prgrs["DS_ScrMtdName"]?.ToString() ?? "";
                }

                // ── HeatId → HeatNo マップ（DS_PRGDANCEs[0] の DS_PRGHEATs を使用）─────
                var heatIdToNo = new Dictionary<string, int>();
                var dncArr = prgrs["DS_PRGDANCEs"] as JArray;
                var firstDnc = dncArr?.OfType<JObject>().FirstOrDefault();
                var heatArr = firstDnc?["DS_PRGHEATs"] as JArray;
                if (heatArr != null)
                {
                    foreach (var h in heatArr.OfType<JObject>())
                    {
                        string? heatId = h["DS_HeatId"]?.ToString();
                        int     heatNo = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                        if (!string.IsNullOrEmpty(heatId))
                            heatIdToNo[heatId] = heatNo;
                    }
                }

                int totalHeats = heatIdToNo.Count;

                // ── DA_Master から選手名解決マップ ────────────────────────────────────
                int targetMasNo0 = int.TryParse(kbnObj?["DB_KbnSenM"]?.ToString(), out int senM0) ? senM0 : 1;
                var infoMap0     = BuildPlayerInfoMap(daJson, targetMasNo0);
                // 後方互換のため (string playerName) 形式のマップに変換
                var playerNameMap = infoMap0.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.lName,
                    StringComparer.Ordinal);

                // ── HeatPlayers テーブル生成 ──────────────────────────────────────────
                // HeatNo -> List<(PlayerNo, PlayerName)> で集約
                var heatDict = new SortedDictionary<int, List<(string no, string name)>>();
                for (int i = 1; i <= totalHeats; i++)
                    heatDict[i] = new List<(string, string)>();

                var assignments = prgrs["PlayerAssignments"] as JArray;
                if (assignments != null)
                {
                    foreach (var pa in assignments.OfType<JObject>())
                    {
                        string playerNo   = pa["PlayerNo"]?.ToString() ?? "";
                        string playerName = playerNameMap.TryGetValue(playerNo, out var pn) ? pn : playerNo;
                        var   heatIds    = pa["AssignedHeatIds"] as JArray;
                        if (heatIds == null || heatIds.Count == 0) continue;

                        // AssignedHeatIds の先頭（ダンス1）のヒートIDを使用
                        string firstHeatId = heatIds[0]?.ToString() ?? "";
                        if (heatIdToNo.TryGetValue(firstHeatId, out int hn) && heatDict.ContainsKey(hn))
                            heatDict[hn].Add((playerNo, playerName));
                    }
                }

                var heatTable = new DataTable("HeatPlayers");
                heatTable.Columns.Add("HeatNo",     typeof(string));
                heatTable.Columns.Add("PlayerNo",   typeof(string));
                heatTable.Columns.Add("PlayerName", typeof(string));

                foreach (var kv in heatDict)
                {
                    // 背番号の数値順にソート
                    var sorted = kv.Value.OrderBy(p =>
                        int.TryParse(p.no, out int n) ? n : int.MaxValue).ToList();
                    foreach (var (no, name) in sorted)
                    {
                        var row = heatTable.NewRow();
                        row["HeatNo"]     = kv.Key.ToString();
                        row["PlayerNo"]   = no;
                        row["PlayerName"] = name;
                        heatTable.Rows.Add(row);
                    }
                }

                // ── ラウンドチェック行テキスト生成 ────────────────────────────────────
                // DA_Master の DC_ROUNDs を順番に並べ、現在ラウンドに●、他は○
                string rndFlagsText = "";
                if (kbnObj != null)
                {
                    var rndItems = new List<string>();
                    var rounds   = kbnObj["DC_ROUNDs"] as JArray;
                    if (rounds != null)
                    {
                        foreach (var r in rounds.OfType<JObject>())
                        {
                            string rno   = r["DC_RndNo"]?.ToString() ?? "";
                            string rname = r["DC_RndName_J"]?.ToString() ?? rno;
                            rndItems.Add((rno == rndNo ? "●" : "○") + rname);
                        }
                    }
                    rndFlagsText = string.Join("　", rndItems);
                }

                // ── パラメーターセット ────────────────────────────────────────────────
                int totalEntries = assignments?.Count ?? 0;
                int pickupCount  = prgrs["DS_PickupCnt"]?.ToObject<int>()
                                   ?? prgrs["DS_UpCnt"]?.ToObject<int>() ?? 0;

                SetReportParam(report, "KbnNo",        kbnNo);
                SetReportParam(report, "KbnName",      kbnName);
                SetReportParam(report, "RndName",      rndName);
                SetReportParam(report, "RndFlagsText", rndFlagsText);
                SetReportParam(report, "ScrMtd",       scrMtd);
                SetReportParam(report, "Dances",       dances);
                SetReportParam(report, "TotalEntries", totalEntries.ToString());
                SetReportParam(report, "PickupCount",  pickupCount.ToString());

                // HeatPlayers テーブルをバインド
                BindTableToReport(report, heatTable);

                _log.LogAdd($"[ReportRenderer] PlayerNotice バインド完了: kbn={kbnNo}/{kbnName}, rnd={rndName}, heats={totalHeats}, players={totalEntries}", _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] PlayerNotice バインドエラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>
        /// 出場者連絡票（横向き・ヒート表_横.frx 用）バインド。
        /// DA_Master + DS_Status キャッシュと job.Data の KbnNo / RndNo / DGrpNo を使用して
        /// frx 内の固定名 TextObject に直接値をセットする。
        ///
        /// job.Data 形式:
        ///   { "KbnNo": "1", "RndNo": "1", "DGrpNo": "1" }
        ///   DGrpNo を省略した場合は該当区分・ラウンドの最初の種目グループを使用する。
        ///
        /// frx の TextObject 名（ヒート表_横.frx）:
        ///   Title, SendTo
        ///   PRGNO, KubunName
        ///   Round1〜Round7 （現在ラウンドは背景色 DarkOrange）
        ///   TotalHeat, TotalComp, UP, ScoreMethod
        ///   DS1〜DS5, DC1〜DC5
        ///   HeatNo1〜HeatNo6
        ///   No01_01〜No06_20, Name01_01〜Name06_20
        ///   Table4〜Table9（ヒート行テーブル：未使用ヒートは Visible=false）
        /// </summary>
        private void BindPlayerNoticeHorizontal(Report report, JsonNode? jobData)
        {
            try
            {
                // ── job.Data から KbnNo / RndNo / DGrpNo を取得 ──────────────────
                string kbnNo  = "";
                string rndNo  = "";
                string dGrpNo = "";

                if (jobData != null)
                {
                    var jd = JObject.Parse(jobData.ToJsonString());
                    kbnNo  = jd["KbnNo"]?.ToString()  ?? "";
                    rndNo  = jd["RndNo"]?.ToString()  ?? "";
                    dGrpNo = jd["DGrpNo"]?.ToString() ?? "";
                }

                if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
                {
                    _log.LogAdd("[ReportRenderer] BindPlayerNoticeHorizontal: DS_Status/DA_Master が未受信", _log.WARNING);
                    return;
                }

                var dsJson = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                var daJson = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                // ── フロア一覧を取得（フロア数が1つか複数か判定用）────────────────
                var floors = dsJson["DS_FLOORs"] as JArray;
                int floorCount = floors?.Count ?? 0;

                // ── 対象 DS_PRGRS を KbnNo / RndNo で検索（複数フロア・複数進行番号対応）──
                // DGrpNo が指定されている場合はそれで絞り込む
                JObject? prgrs    = null;
                string   flrCd    = "";
                string   prgNo    = "";
                string   prgSubNo = "";

                if (floors != null)
                {
                    foreach (var floor in floors.OfType<JObject>())
                    {
                        var prgrsList = floor["DS_PRGRSs"] as JArray;
                        if (prgrsList == null) continue;

                        foreach (var p in prgrsList.OfType<JObject>())
                        {
                            if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                            if (p["DS_RndNo"]?.ToString()  != rndNo)  continue;

                            // DGrpNo 指定がある場合はさらに絞り込む
                            if (!string.IsNullOrEmpty(dGrpNo) &&
                                p["DS_DGrpNo"]?.ToString() != dGrpNo)
                                continue;

                            prgrs    = p;
                            flrCd    = floor["DS_FlrCd"]?.ToString()  ?? "";
                            prgNo    = p["DS_PrgNo"]?.ToString()     ?? "";
                            prgSubNo = p["DS_PrgSubNo"]?.ToString()  ?? "";
                            break;
                        }
                        if (prgrs != null) break;
                    }
                }

                if (prgrs == null)
                {
                    _log.LogAdd($"[ReportRenderer] BindPlayerNoticeHorizontal: 対象ラウンドが見つかりません KbnNo={kbnNo} RndNo={rndNo}", _log.WARNING);
                    return;
                }

                // ── DA_Master から区分オブジェクトを取得 ────────────────────────
                var kbnList = daJson["DB_KUBUNs"] as JArray;
                JObject? kbnObj = kbnList?.OfType<JObject>()
                    .FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);

                // ── 区分コード・区分名 ───────────────────────────────────────────
                string kbnCd      = kbnObj?["DB_KbnCd"]?.ToString() ?? "";
                string kbnDspName = kbnObj?["DB_KbnDsipName"]?.ToString()
                                    ?? kbnObj?["DB_KbnDispName"]?.ToString()
                                    ?? kbnObj?["DB_KbnName"]?.ToString() ?? "";

                // ── ラウンド一覧（Round1〜7 表示用）・対象ラウンドオブジェクト ────
                var rndList = kbnObj?["DC_ROUNDs"] as JArray;
                JObject? rndObj = rndList?.OfType<JObject>()
                    .FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);

                string rndName  = rndObj?["DC_RndName_J"]?.ToString() ?? rndNo;
                int    rndUpPln = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
                string scrMtd   = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";

                // ── 種目グループ一覧・対象 DGroup ───────────────────────────────
                // DGrpNo 指定があればそれを、なければ最初のグループを使用
                var dgList = rndObj?["DD_DGRPs"] as JArray;
                int dgCount = dgList?.Count ?? 0;

                JObject? dgrpObj = null;
                if (dgList != null)
                {
                    if (!string.IsNullOrEmpty(dGrpNo))
                        dgrpObj = dgList.OfType<JObject>()
                            .FirstOrDefault(g => g["DD_DGrpNo"]?.ToString() == dGrpNo);
                    dgrpObj ??= dgList.OfType<JObject>().FirstOrDefault();
                }

                string dgrpName = dgrpObj?["DD_DGrpName"]?.ToString() ?? "";

                // ── 種目コード・SG種別（最大5種目）──────────────────────────────
                var dncList = dgrpObj?["DE_DANCEs"] as JArray;
                var dances  = dncList?.OfType<JObject>().OrderBy(d =>
                    d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                string[] dsCodes = new string[5];
                string[] dcTypes = new string[5];
                for (int i = 0; i < 5; i++)
                {
                    if (i < dances.Count)
                    {
                        dsCodes[i] = dances[i]["DE_DncCd"]?.ToString() ?? "";
                        dcTypes[i] = dances[i]["DE_DncSG"]?.ToString() ?? "";
                    }
                }

                // ── KubunName 構築 ───────────────────────────────────────────────
                // 区分番号（2桁ゼロ埋め）+ 区分コード + 区分名 [+ 種目グループ名] [+ フロア名]
                string kbnNoDisplay = int.TryParse(kbnNo, out int kbnNoInt)
                    ? kbnNoInt.ToString("D2")
                    : kbnNo;

                var kubunParts = new System.Text.StringBuilder();
                kubunParts.Append(kbnNoDisplay);
                if (!string.IsNullOrEmpty(kbnCd))
                    kubunParts.Append(" ").Append(kbnCd);
                if (!string.IsNullOrEmpty(kbnDspName))
                    kubunParts.Append(" ").Append(kbnDspName);
                if (dgCount > 1 && !string.IsNullOrEmpty(dgrpName))
                    kubunParts.Append(" ").Append(dgrpName);
                if (floorCount > 1 && !string.IsNullOrEmpty(flrCd))
                    kubunParts.Append(" ").Append(flrCd).Append("フロア");

                string kubunName = kubunParts.ToString();

                // ── PRGNO 構築（進行番号3桁ゼロ埋め + 枝番）────────────────────
                string prgNoFormatted = int.TryParse(prgNo, out int prgNoInt)
                    ? prgNoInt.ToString("D3")
                    : prgNo;
                string prgNoDisplay = string.IsNullOrEmpty(prgSubNo) || prgSubNo == "0" || prgSubNo == "1"
                    ? prgNoFormatted
                    : $"{prgNoFormatted}-{prgSubNo}";

                // ── HeatId→HeatNo マップ（全種目のヒート数の最大値）──────────────
                // 各種目の DS_PRGHEATs を調べ、最大ヒート数を取得
                var heatIdToNo   = new Dictionary<string, int>();
                int maxHeatCount = 0;
                var dncArr = prgrs["DS_PRGDANCEs"] as JArray;
                if (dncArr != null)
                {
                    foreach (var dnc in dncArr.OfType<JObject>())
                    {
                        var heatArr = dnc["DS_PRGHEATs"] as JArray;
                        if (heatArr == null) continue;
                        int cnt = 0;
                        foreach (var h in heatArr.OfType<JObject>())
                        {
                            string? hid = h["DS_HeatId"]?.ToString();
                            int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                            if (!string.IsNullOrEmpty(hid) && !heatIdToNo.ContainsKey(hid))
                                heatIdToNo[hid] = hno;
                            cnt++;
                        }
                        if (cnt > maxHeatCount) maxHeatCount = cnt;
                    }
                }
                // 全種目同一ヒート構成の場合、いずれかの種目の最大 HeatNo を取得
                int totalHeats = heatIdToNo.Count > 0
                    ? heatIdToNo.Values.Max()
                    : maxHeatCount;

                // ── 選手名解決マップ ─────────────────────────────────────────────
                int targetMasNoH = int.TryParse(kbnObj?["DB_KbnSenM"]?.ToString(), out int senMH) ? senMH : 1;
                var infoMapH     = BuildPlayerInfoMap(daJson, targetMasNoH);
                // 苗字のみ（スペース区切りの先頭）に変換
                var playerNameMap = infoMapH.ToDictionary(
                    kv => kv.Key,
                    kv => ExtractLastName(kv.Value.lName),
                    StringComparer.Ordinal);

                // ── ヒート別選手リスト構築（最大6ヒート行 × 最大40名（行分割20名ずつ））──
                // heatRows[rowIdx] = (heatNo, players[])
                // 1ヒートの出場者が20名を超える場合は2行以上に分割
                var heatRows = new List<(int heatNo, List<(string no, string name)> players)>();

                var assignments = prgrs["PlayerAssignments"] as JArray;

                // HeatNo → List<(PlayerNo, PlayerName)> で集約
                var heatDict = new SortedDictionary<int, List<(string no, string name)>>();
                for (int i = 1; i <= totalHeats; i++)
                    heatDict[i] = new List<(string, string)>();

                if (assignments != null)
                {
                    foreach (var pa in assignments.OfType<JObject>())
                    {
                        string playerNo   = pa["PlayerNo"]?.ToString() ?? "";
                        string playerName = playerNameMap.TryGetValue(playerNo, out var pn) ? pn : "";
                        var   heatIds    = pa["AssignedHeatIds"] as JArray;
                        if (heatIds == null || heatIds.Count == 0) continue;

                        string firstHeatId = heatIds[0]?.ToString() ?? "";
                        if (heatIdToNo.TryGetValue(firstHeatId, out int hn) && heatDict.ContainsKey(hn))
                            heatDict[hn].Add((playerNo, playerName));
                    }
                }

                // 背番号数値順ソート → 20名ずつ分割して heatRows に追加
                // 選手が1人もいないヒートは行を追加しない（空行が印刷されるのを防ぐ）
                foreach (var kv in heatDict)
                {
                    var sorted = kv.Value.OrderBy(p =>
                        int.TryParse(p.no, out int n) ? n : int.MaxValue).ToList();

                    if (sorted.Count == 0) continue;

                    for (int offset = 0; offset < sorted.Count; offset += 20)
                    {
                        var chunk = sorted.Skip(offset).Take(20).ToList();
                        heatRows.Add((kv.Key, chunk));
                    }
                }

                int totalEntries = assignments?.Count ?? 0;

                // ── シャッフル判定（DS_PrgShuffle 等があれば参照）───────────────
                bool isShuffle = prgrs["DS_PrgShuffle"]?.ToObject<bool>() ?? false;
                string upText = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

                // ══════════════════════════════════════════════════════════
                // report.FindObject で TextObject を取得して Text を上書き
                // ══════════════════════════════════════════════════════════

                // Title / SendTo
                SetTextObject(report, "Title",   "出場者連絡票");
                SetTextObject(report, "SendTo",  "【　司会　】");

                // PRGNO / KubunName
                SetTextObject(report, "PRGNO",     prgNoDisplay);
                SetTextObject(report, "KubunName", kubunName);

                // Round1〜Round7（ラウンド名を順番に設定、現在ラウンドは背景色をDarkOrangeに）
                var roundObjs = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                for (int i = 1; i <= 7; i++)
                {
                    string objName = $"Round{i}";
                    if (i - 1 < roundObjs.Count)
                    {
                        var r     = roundObjs[i - 1];
                        string rn = r["DC_RndName_J"]?.ToString() ?? "";
                        bool isCur = r["DC_RndNo"]?.ToString() == rndNo;

                        SetTextObject(report, objName, rn);
                        if (isCur)
                            SetTextObjectFill(report, objName, System.Drawing.Color.DarkOrange);
                        else
                            SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                    else
                    {
                        SetTextObject(report, objName, "");
                        SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                }

                // TotalHeat / TotalComp / UP / ScoreMethod
                SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                SetTextObject(report, "UP",          upText);
                SetTextObject(report, "ScoreMethod", scrMtd);

                // DS1〜DS5 / DC1〜DC5
                // この帳票に含まれる種目（値が入るセル）は背景を DarkOrange、含まれない（空欄）は Transparent
                for (int i = 1; i <= 5; i++)
                {
                    bool hasDance = i <= dances.Count;
                    SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                    SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                    var fillColor = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                    SetTextObjectFill(report, $"DS{i}", fillColor);
                    SetTextObjectFill(report, $"DC{i}", fillColor);
                }

                // ヒート行（最大6行）
                // TableObjectの Visible を制御して未使用行を非表示にする
                // Table4=行1, Table5=行2, ..., Table9=行6
                string[] tableNames = { "Table4", "Table5", "Table6", "Table7", "Table8", "Table9" };

                for (int rowIdx = 0; rowIdx < 6; rowIdx++)
                {
                    string tableName = tableNames[rowIdx];
                    string rowNum    = $"{rowIdx + 1:D2}";  // "01"〜"06"

                    if (rowIdx < heatRows.Count)
                    {
                        var (heatNo, players) = heatRows[rowIdx];

                        // テーブル自体を表示
                        SetTableVisible(report, tableName, true);

                        // HeatNoX
                        SetTextObject(report, $"HeatNo{rowIdx + 1}", heatNo.ToString());

                        // No・Name 各20列
                        for (int col = 1; col <= 20; col++)
                        {
                            string colNum = $"{col:D2}";
                            string noName   = $"No{rowNum}_{colNum}";
                            string nameName = $"Name{rowNum}_{colNum}";

                            if (col <= players.Count)
                            {
                                SetTextObject(report, noName,   players[col - 1].no);
                                SetTextObject(report, nameName, players[col - 1].name);
                            }
                            else
                            {
                                SetTextObject(report, noName,   "");
                                SetTextObject(report, nameName, "");
                            }
                        }
                    }
                    else
                    {
                        // 未使用行は非表示
                        SetTableVisible(report, tableName, false);
                    }
                }

                _log.LogAdd(
                    $"[ReportRenderer] BindPlayerNoticeHorizontal 完了: " +
                    $"kbn={kbnNo}/{kbnDspName}, rnd={rndName}, " +
                    $"totalHeats={totalHeats}, totalEntries={totalEntries}, heatRows={heatRows.Count}",
                    _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] BindPlayerNoticeHorizontal エラー: {ex.Message}", _log.ERR);
            }
        }

        // ── ジャッジ票コンテキスト ──────────────────────────────────────────────
        // BuildJudgeSheetContext で構築したページ描画に必要な情報をまとめたレコード。
        // PrintJudgeSheetPerPage（対策1）と BindJudgeSheet（後方互換）の両方から参照する。
        private sealed record JudgeSheetContext(
            string KbnNo,
            string KbnDspName,
            string RndNo,
            string RndName,
            string ScrMtd,
            string KubunName,
            string PrgNoDisplay,
            string UpText,
            int TotalHeats,
            int TotalEntries,
            string[] DsCodes,
            string[] DcTypes,
            List<JObject> Dances,
            List<JObject> RoundObjs,
            List<(int heatNo, List<(string no, string name)> players)> HeatRows,
            List<(string jdgCd, string jdgName)> TargetJudges
        );

        /// <summary>
        /// job.Data + DataManager キャッシュからジャッジ票描画に必要なコンテキストを構築する。
        /// 失敗した場合は null を返す（エラーログ出力済み）。
        /// </summary>
        private JudgeSheetContext? BuildJudgeSheetContext(JsonNode? jobData)
        {
            // ── job.Data から KbnNo / RndNo / DGrpNo / JudgeCd を取得 ────────
            string kbnNo  = "";
            string rndNo  = "";
            string dGrpNo = "";
            string judgeCdFilter = "";

            if (jobData != null)
            {
                var jd = JObject.Parse(jobData.ToJsonString());
                kbnNo         = jd["KbnNo"]?.ToString()   ?? "";
                rndNo         = jd["RndNo"]?.ToString()   ?? "";
                dGrpNo        = jd["DGrpNo"]?.ToString()  ?? "";
                judgeCdFilter = jd["JudgeCd"]?.ToString() ?? "";
            }

            if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
            {
                _log.LogAdd("[ReportRenderer] BindJudgeSheet: DS_Status/DA_Master が未受信", _log.WARNING);
                return null;
            }

            var dsJson = JObject.Parse(_dataManager.DS_Status.ToJsonString());
            var daJson = JObject.Parse(_dataManager.DA_Master.ToJsonString());

            // ── フロア一覧（フロア数判定用）─────────────────────────────────
            var floors     = dsJson["DS_FLOORs"] as JArray;
            int floorCount = floors?.Count ?? 0;

            // ── 対象 DS_PRGRS を KbnNo / RndNo で検索 ────────────────────────
            JObject? prgrs    = null;
            string   flrCd    = "";
            string   prgNo    = "";
            string   prgSubNo = "";

            if (floors != null)
            {
                foreach (var floor in floors.OfType<JObject>())
                {
                    var prgrsList = floor["DS_PRGRSs"] as JArray;
                    if (prgrsList == null) continue;
                    foreach (var p in prgrsList.OfType<JObject>())
                    {
                        if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                        if (p["DS_RndNo"]?.ToString()  != rndNo)  continue;
                        if (!string.IsNullOrEmpty(dGrpNo) &&
                            p["DS_DGrpNo"]?.ToString() != dGrpNo)
                            continue;
                        prgrs    = p;
                        flrCd    = floor["DS_FlrCd"]?.ToString()  ?? "";
                        prgNo    = p["DS_PrgNo"]?.ToString()     ?? "";
                        prgSubNo = p["DS_PrgSubNo"]?.ToString()  ?? "";
                        break;
                    }
                    if (prgrs != null) break;
                }
            }

            if (prgrs == null)
            {
                _log.LogAdd($"[ReportRenderer] BindJudgeSheet: 対象ラウンドが見つかりません KbnNo={kbnNo} RndNo={rndNo}", _log.WARNING);
                return null;
            }

            // ── DA_Master から区分・ラウンド・種目情報を取得 ─────────────────
            var kbnList = daJson["DB_KUBUNs"] as JArray;
            JObject? kbnObj = kbnList?.OfType<JObject>()
                .FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);

            string kbnCd      = kbnObj?["DB_KbnCd"]?.ToString() ?? "";
            string kbnDspName = kbnObj?["DB_KbnDsipName"]?.ToString()
                                ?? kbnObj?["DB_KbnDispName"]?.ToString()
                                ?? kbnObj?["DB_KbnName"]?.ToString() ?? "";

            var rndList  = kbnObj?["DC_ROUNDs"] as JArray;
            JObject? rndObj = rndList?.OfType<JObject>()
                .FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);

            string rndName  = rndObj?["DC_RndName_J"]?.ToString() ?? rndNo;
            int    rndUpPln = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
            string scrMtd   = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";

            // ── 種目グループ・種目一覧 ──────────────────────────────────────
            var dgList  = rndObj?["DD_DGRPs"] as JArray;
            int dgCount = dgList?.Count ?? 0;

            JObject? dgrpObj = null;
            if (dgList != null)
            {
                if (!string.IsNullOrEmpty(dGrpNo))
                    dgrpObj = dgList.OfType<JObject>()
                        .FirstOrDefault(g => g["DD_DGrpNo"]?.ToString() == dGrpNo);
                dgrpObj ??= dgList.OfType<JObject>().FirstOrDefault();
            }

            string dgrpName = dgrpObj?["DD_DGrpName"]?.ToString() ?? "";
            var dncList     = dgrpObj?["DE_DANCEs"] as JArray;
            var dances      = dncList?.OfType<JObject>()
                .OrderBy(d => d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                ?? new List<JObject>();

            string[] dsCodes = new string[5];
            string[] dcTypes = new string[5];
            for (int i = 0; i < 5; i++)
            {
                if (i < dances.Count)
                {
                    dsCodes[i] = dances[i]["DE_DncCd"]?.ToString() ?? "";
                    dcTypes[i] = dances[i]["DE_DncSG"]?.ToString() ?? "";
                }
            }

            // ── KubunName / PRGNO 構築 ───────────────────────────────────────
            string kbnNoDisplay = int.TryParse(kbnNo, out int kbnNoInt)
                ? kbnNoInt.ToString("D2") : kbnNo;
            var kubunParts = new System.Text.StringBuilder();
            kubunParts.Append(kbnNoDisplay);
            if (!string.IsNullOrEmpty(kbnCd))      kubunParts.Append(" ").Append(kbnCd);
            if (!string.IsNullOrEmpty(kbnDspName)) kubunParts.Append(" ").Append(kbnDspName);
            if (dgCount > 1 && !string.IsNullOrEmpty(dgrpName))
                kubunParts.Append(" ").Append(dgrpName);
            if (floorCount > 1 && !string.IsNullOrEmpty(flrCd))
                kubunParts.Append(" ").Append(flrCd).Append("フロア");
            string kubunName = kubunParts.ToString();

            string prgNoFormatted = int.TryParse(prgNo, out int prgNoInt)
                ? prgNoInt.ToString("D3") : prgNo;
            string prgNoDisplay = string.IsNullOrEmpty(prgSubNo) || prgSubNo == "0" || prgSubNo == "1"
                ? prgNoFormatted : $"{prgNoFormatted}-{prgSubNo}";

            // ── HeatId → HeatNo マップ ─────────────────────────────────────────
            var heatIdToNo   = new Dictionary<string, int>();
            int maxHeatCount = 0;
            var dncArr = prgrs["DS_PRGDANCEs"] as JArray;
            if (dncArr != null)
            {
                foreach (var dnc in dncArr.OfType<JObject>())
                {
                    var heatArr = dnc["DS_PRGHEATs"] as JArray;
                    if (heatArr == null) continue;
                    int cnt = 0;
                    foreach (var h in heatArr.OfType<JObject>())
                    {
                        string? hid = h["DS_HeatId"]?.ToString();
                        int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                        if (!string.IsNullOrEmpty(hid) && !heatIdToNo.ContainsKey(hid))
                            heatIdToNo[hid] = hno;
                        cnt++;
                    }
                    if (cnt > maxHeatCount) maxHeatCount = cnt;
                }
            }
            int totalHeats = heatIdToNo.Count > 0 ? heatIdToNo.Values.Max() : maxHeatCount;

            // ── ヒート別選手リスト構築（背番号のみ、名前はブランク）──────────
            var heatRows = new List<(int heatNo, List<(string no, string name)> players)>();
            var assignments = prgrs["PlayerAssignments"] as JArray;

            var heatDict = new SortedDictionary<int, List<(string no, string name)>>();
            for (int i = 1; i <= totalHeats; i++)
                heatDict[i] = new List<(string, string)>();

            if (assignments != null)
            {
                foreach (var pa in assignments.OfType<JObject>())
                {
                    string playerNo = pa["PlayerNo"]?.ToString() ?? "";
                    var   heatIds  = pa["AssignedHeatIds"] as JArray;
                    if (heatIds == null || heatIds.Count == 0) continue;

                    string firstHeatId = heatIds[0]?.ToString() ?? "";
                    if (heatIdToNo.TryGetValue(firstHeatId, out int hn) && heatDict.ContainsKey(hn))
                        heatDict[hn].Add((playerNo, ""));   // 選手名はブランク
                }
            }

            // 背番号数値順ソート → 20名ずつ分割して heatRows に追加
            // 選手が1人もいないヒートは行を追加しない（空行が印刷されるのを防ぐ）
            foreach (var kv in heatDict)
            {
                var sorted = kv.Value.OrderBy(p =>
                    int.TryParse(p.no, out int n) ? n : int.MaxValue).ToList();

                if (sorted.Count == 0) continue;

                for (int offset = 0; offset < sorted.Count; offset += 20)
                {
                    var chunk = sorted.Skip(offset).Take(20).ToList();
                    heatRows.Add((kv.Key, chunk));
                }
            }

            int totalEntries = assignments?.Count ?? 0;

            bool isShuffle = prgrs["DS_PrgShuffle"]?.ToObject<bool>() ?? false;
            string upText = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

            // ── ラウンドチェック表示用オブジェクト ────────────────────────────
            var roundObjs = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();

            // ── DA_Master から対象ジャッジリストを取得 ──────────────────────────
            var allJudges = new List<(string jdgCd, string jdgName)>();
            var djJudges = daJson["DJ_JUDGEs"] as JArray;
            if (djJudges != null)
            {
                foreach (var dj in djJudges.OfType<JObject>())
                {
                    string jcd  = dj["DJ_JdgCd"]?.ToString() ?? "";
                    string jnm  = dj["DJ_JdgDispName"]?.ToString()
                                  ?? dj["DJ_JdgName"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(jcd))
                        allJudges.Add((jcd, jnm));
                }
            }

            List<(string jdgCd, string jdgName)> targetJudges;
            if (!string.IsNullOrEmpty(judgeCdFilter))
            {
                targetJudges = allJudges
                    .Where(j => string.Equals(j.jdgCd, judgeCdFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (targetJudges.Count == 0)
                    targetJudges.Add((judgeCdFilter, ""));
            }
            else
            {
                targetJudges = allJudges;
            }

            if (targetJudges.Count == 0)
                targetJudges.Add(("", ""));

            return new JudgeSheetContext(
                KbnNo:         kbnNo,
                KbnDspName:    kbnDspName,
                RndNo:         rndNo,
                RndName:       rndName,
                ScrMtd:        scrMtd,
                KubunName:     kubunName,
                PrgNoDisplay:  prgNoDisplay,
                UpText:        upText,
                TotalHeats:    totalHeats,
                TotalEntries:  totalEntries,
                DsCodes:       dsCodes,
                DcTypes:       dcTypes,
                Dances:        dances,
                RoundObjs:     roundObjs,
                HeatRows:      heatRows,
                TargetJudges:  targetJudges
            );
        }

        /// <summary>
        /// ジャッジ票の1ページ分のデータを Report の TextObject に適用する（対策1）。
        /// suffix: 1ページ目は ""、複数ページ複製時は "_Pxx"。
        /// 1ページずつ個別印刷する場合は suffix = "" を使用する。
        /// </summary>
        private void ApplyJudgeSheetPage(Report report, JudgeSheetContext ctx,
            string jdgCd, string jdgName, int dncIdx, string suffix)
        {
            string sendTo = string.IsNullOrEmpty(jdgCd) && string.IsNullOrEmpty(jdgName)
                ? "【　ジャッジ　】"
                : $"【{jdgCd}　{jdgName}】";
            SetTextObject(report, $"Title{suffix}",   "ジャッジ票");
            SetTextObject(report, $"SendTo{suffix}",  sendTo);
            SetTextObject(report, $"PRGNO{suffix}",     ctx.PrgNoDisplay);
            SetTextObject(report, $"KubunName{suffix}", ctx.KubunName);

            for (int i = 1; i <= 7; i++)
            {
                string objName = $"Round{i}{suffix}";
                if (i - 1 < ctx.RoundObjs.Count)
                {
                    var r      = ctx.RoundObjs[i - 1];
                    string rn  = r["DC_RndName_J"]?.ToString() ?? "";
                    bool isCur = r["DC_RndNo"]?.ToString() == ctx.RndNo;
                    SetTextObject(report, objName, rn);
                    SetTextObjectFill(report, objName,
                        isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                }
                else
                {
                    SetTextObject(report, objName, "");
                    SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                }
            }

            SetTextObject(report, $"TotalHeat{suffix}",   $"{ctx.TotalHeats} Heat");
            SetTextObject(report, $"TotalComp{suffix}",   $"出場　{ctx.TotalEntries}組");
            SetTextObject(report, $"UP{suffix}",          ctx.UpText);
            SetTextObject(report, $"ScoreMethod{suffix}", ctx.ScrMtd);

            int danceCount = ctx.Dances.Count;
            for (int i = 1; i <= 5; i++)
            {
                bool hasDance = i <= danceCount;
                SetTextObject(report, $"DS{i}{suffix}", hasDance ? ctx.DsCodes[i - 1] : "");
                SetTextObject(report, $"DC{i}{suffix}", hasDance ? ctx.DcTypes[i - 1] : "");
                bool isCurDnc = hasDance && (i - 1 == dncIdx);
                var fillColor = hasDance
                    ? (isCurDnc ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent)
                    : System.Drawing.Color.Transparent;
                SetTextObjectFill(report, $"DS{i}{suffix}", fillColor);
                SetTextObjectFill(report, $"DC{i}{suffix}", fillColor);
            }

            string[] tableNames = { "Table4", "Table5", "Table6", "Table7", "Table8", "Table9" };
            for (int rowIdx = 0; rowIdx < 6; rowIdx++)
            {
                string tableName = $"{tableNames[rowIdx]}{suffix}";
                string rowNum    = $"{rowIdx + 1:D2}";

                if (rowIdx < ctx.HeatRows.Count)
                {
                    var (heatNo, players) = ctx.HeatRows[rowIdx];
                    SetTableVisible(report, tableName, true);
                    SetTextObject(report, $"HeatNo{rowIdx + 1}{suffix}", heatNo.ToString());

                    for (int col = 1; col <= 20; col++)
                    {
                        string colNum   = $"{col:D2}";
                        string noName   = $"No{rowNum}_{colNum}{suffix}";
                        string nameName = $"Name{rowNum}_{colNum}{suffix}";

                        if (col <= players.Count)
                        {
                            SetTextObject(report, noName,   players[col - 1].no);
                            SetTextObject(report, nameName, "");  // 選手名はブランク
                        }
                        else
                        {
                            SetTextObject(report, noName,   "");
                            SetTextObject(report, nameName, "");
                        }
                    }
                }
                else
                {
                    SetTableVisible(report, tableName, false);
                }
            }
        }

        /// <summary>
        /// ジャッジ票（横向き・ジャッジ票_横.frx 用）バインド。
        /// ※ PrintJudgeSheetPerPage（対策1）経由では呼ばれない。
        ///   LoadAndPrepare の switch 文から呼ばれる後方互換パス（ExportHtmlAsync 等）で使用。
        /// </summary>
        private void BindJudgeSheet(Report report, JsonNode? jobData)
        {
            var ctx = BuildJudgeSheetContext(jobData);
            if (ctx == null) return;

            int danceCount = ctx.Dances.Count;
            int totalPages = ctx.TargetJudges.Count * danceCount;
            if (totalPages == 0) totalPages = 1;

            // ── ページ複製（複数ページの場合）──────────────────────────────
            if (totalPages > 1)
            {
                string origXml = report.SaveToString();
                string? newXml = DuplicatePageInReportXml(origXml, totalPages);
                if (newXml != null)
                    report.LoadFromString(newXml);
            }

            // ── 各ページにデータをセット ───────────────────────────────────
            int pageNum = 0;
            foreach (var (jdgCd, jdgName) in ctx.TargetJudges)
            {
                for (int dncIdx = 0; dncIdx < Math.Max(1, danceCount); dncIdx++)
                {
                    string suffix = pageNum == 0 ? "" : $"_P{pageNum + 1:D2}";
                    ApplyJudgeSheetPage(report, ctx, jdgCd, jdgName, dncIdx, suffix);
                    pageNum++;
                }
            }

            _log.LogAdd(
                $"[ReportRenderer] BindJudgeSheet 完了: kbn={ctx.KbnNo}/{ctx.KbnDspName}, rnd={ctx.RndName}, " +
                $"dances={danceCount}, judges={ctx.TargetJudges.Count}, totalPages={totalPages}",
                _log.INFO);
        }



        /// <summary>
        /// DS_Status/DA_Master がない場合（テスト時）の出場者連絡票フォールバックバインド。
        /// job.Data の Heats[].Players[] から HeatPlayers テーブルを生成し、
        /// KbnNo/KbnName/RndName/RndFlagsText 等もテストデータから設定する。
        /// </summary>
        private void BindPlayerNoticeFromFallback(Report report, JsonNode data)
        {
            try
            {
                var jObj = JObject.Parse(data.ToJsonString());

                // ── パラメーター（テストデータのスカラー値から直接取得）────────────
                SetReportParam(report, "KbnNo",        jObj["KbnNo"]?.ToString()        ?? "");
                SetReportParam(report, "KbnName",      jObj["KbnName"]?.ToString()      ?? "");
                SetReportParam(report, "RndName",      jObj["RndName"]?.ToString()      ?? "");
                SetReportParam(report, "ScrMtd",       jObj["ScrMtd"]?.ToString()       ?? "");
                SetReportParam(report, "Dances",       jObj["Dances"]?.ToString()       ?? "");
                SetReportParam(report, "TotalEntries", jObj["TotalEntries"]?.ToString() ?? "");
                SetReportParam(report, "PickupCount",  jObj["PickupCount"]?.ToString()  ?? "");

                // ── RndFlagsText: RndFlags[] から ●○ 文字列を生成 ─────────────────
                var rndFlags = jObj["RndFlags"] as JArray;
                string rndFlagsText = "";
                if (rndFlags != null)
                {
                    var items = rndFlags.OfType<JObject>().Select(r =>
                    {
                        bool isCurrent = r["IsCurrent"]?.ToObject<bool>() ?? false;
                        string label   = r["RndLabel"]?.ToString() ?? "";
                        return (isCurrent ? "●" : "○") + label;
                    });
                    rndFlagsText = string.Join("　", items);
                }
                SetReportParam(report, "RndFlagsText", rndFlagsText);

                // ── HeatPlayers テーブル: Heats[].Players[] をフラット化 ──────────
                var heatTable = new DataTable("HeatPlayers");
                heatTable.Columns.Add("HeatNo",     typeof(string));
                heatTable.Columns.Add("PlayerNo",   typeof(string));
                heatTable.Columns.Add("PlayerName", typeof(string));

                var heats = jObj["Heats"] as JArray;
                if (heats != null)
                {
                    foreach (var heat in heats.OfType<JObject>())
                    {
                        string heatNo  = heat["HeatNo"]?.ToString() ?? "";
                        var   players = heat["Players"] as JArray;
                        if (players == null) continue;
                        foreach (var p in players.OfType<JObject>())
                        {
                            var row = heatTable.NewRow();
                            row["HeatNo"]     = heatNo;
                            row["PlayerNo"]   = p["PlayerNo"]?.ToString()   ?? "";
                            row["PlayerName"] = p["PlayerName"]?.ToString() ?? "";
                            heatTable.Rows.Add(row);
                        }
                    }
                }

                BindTableToReport(report, heatTable);
                _log.LogAdd($"[ReportRenderer] PlayerNotice フォールバックバインド完了: heats={heats?.Count ?? 0}, rows={heatTable.Rows.Count}", _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] PlayerNotice フォールバックバインドエラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>
        /// 出場者ヒート表（縦向き・出場者ヒート表_縦.frx 用）バインド。
        /// DA_Master + DS_Status キャッシュと job.Data の KbnNo / RndNo / DGrpNo を使用して
        /// frx の TextObject に直接値をセットし、DataBand DataSource で複数ページを処理する。
        ///
        /// ■ frx TextObject 名規則
        ///   PageHeader: PRGNO, KubunName, Round1〜7, TotalHeat, TotalComp, UP, ScoreMethod, DS1〜5, DC1〜5
        ///   DataBand 列ヘッダー: D01〜D10（種目コード）
        ///   DataBand データ行:   VRxx_C01（背番号）, VRxx_C02（選手名）, VRxx_C03〜C12（ヒート番号） xx=01〜30
        ///
        /// ■ 複数ページ対応
        ///   全選手を背番号昇順で並べ、30行/ページで分割する。
        ///   DataTable "PlayerHeatPage"（行数=ページ数、列: PageIdx, VR01_C01〜VR30_C12）を生成し
        ///   DataBand の DataSource として RegisterData する。
        ///   frx DataBand 内の各 TextObject の Text は [PlayerHeatPage.VRxx_Cyy] 式で参照する。
        ///   FastReport が DataBand を行数分繰り返すことで自動改ページされる。
        /// </summary>
        private void BindPlayerHeatVertical(Report report, JsonNode? jobData)
        {
            try
            {
                // ── job.Data から KbnNo / RndNo / DGrpNo を取得 ──────────────────
                string kbnNo  = "";
                string rndNo  = "";
                string dGrpNo = "";

                if (jobData != null)
                {
                    var jd = JObject.Parse(jobData.ToJsonString());
                    kbnNo  = jd["KbnNo"]?.ToString()  ?? "";
                    rndNo  = jd["RndNo"]?.ToString()  ?? "";
                    dGrpNo = jd["DGrpNo"]?.ToString() ?? "";
                }

                if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
                {
                    _log.LogAdd("[ReportRenderer] BindPlayerHeatVertical: DS_Status/DA_Master が未受信", _log.WARNING);
                    return;
                }

                var dsJson = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                var daJson = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                // ── フロア一覧（フロア数判定用）─────────────────────────────────
                var floors     = dsJson["DS_FLOORs"] as JArray;
                int floorCount = floors?.Count ?? 0;

                // ── 対象 DS_PRGRS を KbnNo / RndNo で検索 ────────────────────────
                JObject? prgrs    = null;
                string   flrCd    = "";
                string   prgNo    = "";
                string   prgSubNo = "";

                if (floors != null)
                {
                    foreach (var floor in floors.OfType<JObject>())
                    {
                        var prgrsList = floor["DS_PRGRSs"] as JArray;
                        if (prgrsList == null) continue;
                        foreach (var p in prgrsList.OfType<JObject>())
                        {
                            if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                            if (p["DS_RndNo"]?.ToString()  != rndNo)  continue;
                            if (!string.IsNullOrEmpty(dGrpNo) &&
                                p["DS_DGrpNo"]?.ToString() != dGrpNo)
                                continue;
                            prgrs    = p;
                            flrCd    = floor["DS_FlrCd"]?.ToString()  ?? "";
                            prgNo    = p["DS_PrgNo"]?.ToString()     ?? "";
                            prgSubNo = p["DS_PrgSubNo"]?.ToString()  ?? "";
                            break;
                        }
                        if (prgrs != null) break;
                    }
                }

                if (prgrs == null)
                {
                    _log.LogAdd($"[ReportRenderer] BindPlayerHeatVertical: 対象ラウンドが見つかりません KbnNo={kbnNo} RndNo={rndNo}", _log.WARNING);
                    return;
                }

                // ── DA_Master から区分・ラウンド情報を取得 ────────────────────────
                var kbnList = daJson["DB_KUBUNs"] as JArray;
                JObject? kbnObj = kbnList?.OfType<JObject>()
                    .FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);

                string kbnCd      = kbnObj?["DB_KbnCd"]?.ToString() ?? "";
                string kbnDspName = kbnObj?["DB_KbnDsipName"]?.ToString()
                                    ?? kbnObj?["DB_KbnDispName"]?.ToString()
                                    ?? kbnObj?["DB_KbnName"]?.ToString() ?? "";

                var rndList  = kbnObj?["DC_ROUNDs"] as JArray;
                JObject? rndObj = rndList?.OfType<JObject>()
                    .FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);

                string rndName  = rndObj?["DC_RndName_J"]?.ToString() ?? rndNo;
                int    rndUpPln = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
                string scrMtd   = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";

                // ── 種目グループ・種目一覧（最大10種目）───────────────────────────
                var dgList = rndObj?["DD_DGRPs"] as JArray;
                int dgCount = dgList?.Count ?? 0;

                JObject? dgrpObj = null;
                if (dgList != null)
                {
                    if (!string.IsNullOrEmpty(dGrpNo))
                        dgrpObj = dgList.OfType<JObject>()
                            .FirstOrDefault(g => g["DD_DGrpNo"]?.ToString() == dGrpNo);
                    dgrpObj ??= dgList.OfType<JObject>().FirstOrDefault();
                }

                string dgrpName = dgrpObj?["DD_DGrpName"]?.ToString() ?? "";
                var dncList     = dgrpObj?["DE_DANCEs"] as JArray;
                var dances      = dncList?.OfType<JObject>()
                    .OrderBy(d => d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                int danceCount = Math.Min(dances.Count, 10);  // 最大10種目

                // 種目コード・SG種別（最大10）
                string[] dsCodes = new string[10];
                string[] dcTypes = new string[10];
                for (int i = 0; i < 10; i++)
                {
                    if (i < dances.Count)
                    {
                        dsCodes[i] = dances[i]["DE_DncCd"]?.ToString() ?? "";
                        dcTypes[i] = dances[i]["DE_DncSG"]?.ToString() ?? "";
                    }
                }

                // ── KubunName 構築 ────────────────────────────────────────────────
                string kbnNoDisplay = int.TryParse(kbnNo, out int kbnNoInt)
                    ? kbnNoInt.ToString("D2") : kbnNo;
                var kubunParts = new System.Text.StringBuilder();
                kubunParts.Append(kbnNoDisplay);
                if (!string.IsNullOrEmpty(kbnCd))      kubunParts.Append(" ").Append(kbnCd);
                if (!string.IsNullOrEmpty(kbnDspName)) kubunParts.Append(" ").Append(kbnDspName);
                if (dgCount > 1 && !string.IsNullOrEmpty(dgrpName))
                    kubunParts.Append(" ").Append(dgrpName);
                if (floorCount > 1 && !string.IsNullOrEmpty(flrCd))
                    kubunParts.Append(" ").Append(flrCd).Append("フロア");
                string kubunName = kubunParts.ToString();

                // ── PRGNO 構築 ────────────────────────────────────────────────────
                string prgNoFormatted = int.TryParse(prgNo, out int prgNoInt)
                    ? prgNoInt.ToString("D3") : prgNo;
                string prgNoDisplay = string.IsNullOrEmpty(prgSubNo) || prgSubNo == "0" || prgSubNo == "1"
                    ? prgNoFormatted : $"{prgNoFormatted}-{prgSubNo}";

                // ── HeatId → HeatNo マップ ─────────────────────────────────────────
                var heatIdToNo   = new Dictionary<string, int>();
                int maxHeatCount = 0;
                var dncArr = prgrs["DS_PRGDANCEs"] as JArray;
                if (dncArr != null)
                {
                    foreach (var dnc in dncArr.OfType<JObject>())
                    {
                        var heatArr = dnc["DS_PRGHEATs"] as JArray;
                        if (heatArr == null) continue;
                        int cnt = 0;
                        foreach (var h in heatArr.OfType<JObject>())
                        {
                            string? hid = h["DS_HeatId"]?.ToString();
                            int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                            if (!string.IsNullOrEmpty(hid) && !heatIdToNo.ContainsKey(hid))
                                heatIdToNo[hid] = hno;
                            cnt++;
                        }
                        if (cnt > maxHeatCount) maxHeatCount = cnt;
                    }
                }
                int totalHeats = heatIdToNo.Count > 0 ? heatIdToNo.Values.Max() : maxHeatCount;

                // ── 各種目ごとの HeatId→HeatNo マップ（種目別ヒート番号解決用）────
                // dncArr の順序は DS_DncNo 順（DA_Master の種目番号順ではなく進行順）
                // DA_Master の種目コード順（dances）と対応させるため DS_DncNo でソートする
                var dncObjsSorted = dncArr?.OfType<JObject>()
                    .OrderBy(d => d["DS_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                // danceIdx → { HeatId → HeatNo } マップ
                var danceHeatMaps = new List<Dictionary<string, int>>();
                for (int di = 0; di < danceCount; di++)
                {
                    var map = new Dictionary<string, int>();
                    if (di < dncObjsSorted.Count)
                    {
                        var heatArr2 = dncObjsSorted[di]["DS_PRGHEATs"] as JArray;
                        if (heatArr2 != null)
                        {
                            foreach (var h in heatArr2.OfType<JObject>())
                            {
                                string? hid = h["DS_HeatId"]?.ToString();
                                int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                                if (!string.IsNullOrEmpty(hid) && !map.ContainsKey(hid))
                                    map[hid] = hno;
                            }
                        }
                    }
                    danceHeatMaps.Add(map);
                }

                // ── 選手名解決マップ ────────────────────────────────────────────────
                // DB_KbnSenM で指定されたマスタ番号の選手情報を使用する（選手マスタ考慮）
                int targetMasNoV = int.TryParse(kbnObj?["DB_KbnSenM"]?.ToString(), out int senMV) ? senMV : 1;
                var infoMapV     = BuildPlayerInfoMap(daJson, targetMasNoV);
                // 表示形式:「L選手名の苗字・選手名の苗字」（例: 田中・鈴木）
                var playerNameMap = infoMapV.ToDictionary(
                    kv => kv.Key,
                    kv =>
                    {
                        string lLast = ExtractLastName(kv.Value.lName);
                        string pLast = ExtractLastName(kv.Value.pName);
                        return string.IsNullOrEmpty(pLast) ? lLast : $"{lLast}・{pLast}";
                    },
                    StringComparer.Ordinal);

                // ── PlayerAssignments を背番号昇順で並べ、種目別ヒート番号を解決 ──
                var assignments = prgrs["PlayerAssignments"] as JArray;
                int totalEntries = assignments?.Count ?? 0;

                // 選手ごとの行データ: (背番号, 選手名, D01〜D10 のヒート番号文字列[])
                var playerRows = new List<(string bibNo, string lName, string[] heatNos)>();

                if (assignments != null)
                {
                    foreach (var pa in assignments.OfType<JObject>())
                    {
                        string playerNo   = pa["PlayerNo"]?.ToString() ?? "";
                        string playerName = playerNameMap.TryGetValue(playerNo, out var pn) ? pn : "";
                        var   heatIds    = pa["AssignedHeatIds"] as JArray;

                        string[] heatNos = new string[10];
                        if (heatIds != null)
                        {
                            for (int di = 0; di < danceCount; di++)
                            {
                                string heatId = di < heatIds.Count
                                    ? heatIds[di]?.ToString() ?? ""
                                    : (heatIds.Count > 0 ? heatIds[0]?.ToString() ?? "" : "");
                                if (danceHeatMaps[di].TryGetValue(heatId, out int hn))
                                    heatNos[di] = hn.ToString();
                            }
                        }
                        playerRows.Add((playerNo, playerName, heatNos));
                    }
                }

                // 背番号数値昇順ソート
                playerRows.Sort((a, b) =>
                {
                    bool aOk = int.TryParse(a.bibNo, out int ai);
                    bool bOk = int.TryParse(b.bibNo, out int bi);
                    if (aOk && bOk) return ai.CompareTo(bi);
                    if (aOk) return -1;
                    if (bOk) return 1;
                    return string.Compare(a.bibNo, b.bibNo, StringComparison.Ordinal);
                });

                // ── シャッフル判定 ───────────────────────────────────────────────
                bool isShuffle = prgrs["DS_PrgShuffle"]?.ToObject<bool>() ?? false;
                string upText  = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

                // ══════════════════════════════════════════════════════════════
                // PageHeader: 共通ヘッダー部分（BindPlayerNoticeHorizontal と同様）
                // ══════════════════════════════════════════════════════════════

                SetTextObject(report, "Title",   "出場者ヒート表");
                SetTextObject(report, "SendTo",  "【　選手　】");
                SetTextObject(report, "PRGNO",     prgNoDisplay);
                SetTextObject(report, "KubunName", kubunName);

                // Round1〜Round7
                var roundObjs = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                for (int i = 1; i <= 7; i++)
                {
                    string objName = $"Round{i}";
                    if (i - 1 < roundObjs.Count)
                    {
                        var r     = roundObjs[i - 1];
                        string rn = r["DC_RndName_J"]?.ToString() ?? "";
                        bool isCur = r["DC_RndNo"]?.ToString() == rndNo;
                        SetTextObject(report, objName, rn);
                        SetTextObjectFill(report, objName,
                            isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                    }
                    else
                    {
                        SetTextObject(report, objName, "");
                        SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                }

                SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                SetTextObject(report, "UP",          upText);
                SetTextObject(report, "ScoreMethod", scrMtd);

                // DS1〜DS5 / DC1〜DC5（PageHeader の Table3 は5種目まで）
                for (int i = 1; i <= 5; i++)
                {
                    bool hasDance = i <= danceCount;
                    SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                    SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                    var fillColor = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                    SetTextObjectFill(report, $"DS{i}", fillColor);
                    SetTextObjectFill(report, $"DC{i}", fillColor);
                }

                // ══════════════════════════════════════════════════════════════
                // DataBand Table4: 列ヘッダー行（D01〜D10 に種目コードを設定）
                // ══════════════════════════════════════════════════════════════

                for (int i = 1; i <= 10; i++)
                {
                    string colHeaderName = $"D{i:D2}";
                    bool hasDance = i <= danceCount;
                    SetTextObjectDirect(report, colHeaderName, hasDance ? dsCodes[i - 1] : "",
                        hasDance ? FastReport.BorderLines.All : FastReport.BorderLines.None);
                    // データ列ヘッダー（D01〜D10）は背景色なし（Transparent）
                    // 種目コードの有無に関わらず背景色は付けない
                    SetTextObjectFill(report, colHeaderName, System.Drawing.Color.Transparent);
                }

                // ══════════════════════════════════════════════════════════════
                // TextObject 直接セット方式（DataSource バインドなし）
                // 複数ページ対応: report.SaveToString() → XML でページ複製 →
                // report.LoadFromString() → 各ページの TextObject に値をセット
                // ══════════════════════════════════════════════════════════════

                const int RowsPerPage = 30;
                int pageCount = Math.Max(1, (int)Math.Ceiling((double)playerRows.Count / RowsPerPage));

                if (pageCount > 1)
                {
                    // XML でページを複製してから再ロード
                    string origXml = report.SaveToString();
                    string? newXml = DuplicatePageInReportXml(origXml, pageCount);
                    if (newXml != null)
                    {
                        report.LoadFromString(newXml);
                        // 再ロード後、ヘッダー TextObject を再設定（LoadFromString でリセットされるため）
                        SetTextObject(report, "Title",       "出場者ヒート表");
                        SetTextObject(report, "SendTo",      "【　選手　】");
                        SetTextObject(report, "PRGNO",       prgNoDisplay);
                        SetTextObject(report, "KubunName",   kubunName);
                        var roundObjsR = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                        for (int i = 1; i <= 7; i++)
                        {
                            if (i - 1 < roundObjsR.Count)
                            {
                                var r2    = roundObjsR[i - 1];
                                string rn = r2["DC_RndName_J"]?.ToString() ?? "";
                                bool isCur = r2["DC_RndNo"]?.ToString() == rndNo;
                                SetTextObject(report, $"Round{i}", rn);
                                SetTextObjectFill(report, $"Round{i}",
                                    isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                            }
                            else
                            {
                                SetTextObject(report, $"Round{i}", "");
                                SetTextObjectFill(report, $"Round{i}", System.Drawing.Color.Transparent);
                            }
                        }
                        SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                        SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                        SetTextObject(report, "UP",          upText);
                        SetTextObject(report, "ScoreMethod", scrMtd);
                        for (int i = 1; i <= 5; i++)
                        {
                            bool hasDance2 = i <= danceCount;
                            SetTextObject(report, $"DS{i}", hasDance2 ? dsCodes[i - 1] : "");
                            SetTextObject(report, $"DC{i}", hasDance2 ? dcTypes[i - 1] : "");
                            var fc = hasDance2 ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                            SetTextObjectFill(report, $"DS{i}", fc);
                            SetTextObjectFill(report, $"DC{i}", fc);
                        }
                        for (int i = 1; i <= 10; i++)
                        {
                            bool hasDance3 = i <= danceCount;
                            SetTextObjectDirect(report, $"D{i:D2}", hasDance3 ? dsCodes[i - 1] : "",
                                hasDance3 ? FastReport.BorderLines.All : FastReport.BorderLines.None);
                            SetTextObjectFill(report, $"D{i:D2}",
                                hasDance3 ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                        }
                    }
                }

                // 各ページの TextObject にデータ行をセット
                // ページ複製済みの場合、2ページ目以降の名前は _P02, _P03... サフィックス付き
                for (int pg = 0; pg < pageCount; pg++)
                {
                    string suffix = pg == 0 ? "" : $"_P{pg:D2}";
                    SetPageDataRows(report, playerRows, pg, RowsPerPage, danceCount, suffix);
                }

                _log.LogAdd(
                    $"[ReportRenderer] BindPlayerHeatVertical 完了: " +
                    $"kbn={kbnNo}/{kbnDspName}, rnd={rndName}, " +
                    $"totalHeats={totalHeats}, totalEntries={totalEntries}, pages={pageCount}",
                    _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] BindPlayerHeatVertical エラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>
        /// 指定名の TextObject を検索して Text を設定する。見つからない場合は無視。
        /// </summary>
        private static void SetTextObject(Report report, string name, string text)
        {
            var obj = report.FindObject(name) as FastReport.TextObject;
            if (obj != null) obj.Text = text;
        }

        /// <summary>
        /// 指定名の TextObject の背景色（FillColor）を設定する。
        /// </summary>
        private static void SetTextObjectFill(Report report, string name, System.Drawing.Color color)
        {
            var obj = report.FindObject(name) as FastReport.TextObject;
            if (obj == null) return;
            // TextObject.FillColor は System.Drawing.Color 型
            obj.FillColor = color;
        }

        /// <summary>
        /// 指定名の TableObject（またはその親 BandBase）の Visible を設定する。
        /// frx では TableObject は DataBand の子として配置されているため、
        /// TableObject そのものか、TableObject を直接含む親バンドを非表示にする。
        /// </summary>
        private static void SetTableVisible(Report report, string name, bool visible)
        {
            var obj = report.FindObject(name) as FastReport.Table.TableObject;
            if (obj != null) obj.Visible = visible;
        }

        /// <summary>
        /// 指定ページの VRxx_C01〜C12 TextObject にページチャンク分の選手データをセットする。
        /// pageIndex * RowsPerPage 番目の選手から最大 RowsPerPage 行分をセットし、
        /// 残りのセルは空文字にする。
        /// </summary>
        /// <summary>
        /// 指定ページのデータ行 TextObject（VRxx_C01〜C12）に選手データをセット。
        /// report.FindObject で名前検索し、Text と Border.Lines を直接書き換える。
        /// ページ複製後は名前に _Pxx サフィックスが付くため suffix で指定する。
        /// </summary>
        private static void SetPageDataRows(
            Report report,
            List<(string bibNo, string lName, string[] heatNos)> playerRows,
            int pageIndex,
            int rowsPerPage,
            int danceCount,
            string nameSuffix = "")
        {
            int startIdx = pageIndex * rowsPerPage;

            for (int r = 1; r <= rowsPerPage; r++)
            {
                string rr = r.ToString("D2");
                int playerIdx = startIdx + r - 1;
                bool hasData = playerIdx < playerRows.Count;

                string bibNo = "", lName = "";
                string[] heatNos = new string[10];
                if (hasData)
                    (bibNo, lName, heatNos) = playerRows[playerIdx];

                var borderLines = hasData
                    ? FastReport.BorderLines.All
                    : FastReport.BorderLines.None;

                SetTextObjectDirect(report, $"VR{rr}_C01{nameSuffix}", bibNo, borderLines);
                // 選手名セルは長い場合にフォントサイズを自動縮小する（最小6pt）
                SetTextObjectDirectEx(report, $"VR{rr}_C02{nameSuffix}", lName, borderLines, autoShrink: hasData);
                for (int di = 0; di < 10; di++)
                {
                    // hasData かつ danceCount 以内の列だけ罫線あり
                    var colBorder = (hasData && di < danceCount)
                        ? FastReport.BorderLines.All
                        : FastReport.BorderLines.None;
                    SetTextObjectDirect(report, $"VR{rr}_C{di + 3:D2}{nameSuffix}", heatNos[di], colBorder);
                }
            }
        }

        /// <summary>
        /// report.FindObject で TextObject を取得し Text と Border.Lines を設定する。
        /// </summary>
        private static void SetTextObjectDirect(Report report, string name, string text,
            FastReport.BorderLines borderLines = FastReport.BorderLines.All)
        {
            var obj = report.FindObject(name) as FastReport.TextObject;
            if (obj == null) return;
            obj.Text = text;
            obj.Border.Lines = borderLines;
        }

        /// <summary>
        /// 決勝進出者名簿の1ページ分データ行をセットする。
        /// AutoShrink を設定して長い氏名・フリガナ・所属が枠内に収まるようにする。
        /// </summary>
        private static void SetFinalEntryPageRows(
            Report report,
            List<(string bibNo,
                  string lName, string lKana, string lCtry,
                  string pName, string pKana, string pCtry,
                  string[] heatNos)> playerRows,
            int pageIndex,
            int pairsPerPage,
            int danceCount,
            FastReport.BorderLines blLtb,
            FastReport.BorderLines blLrt,
            FastReport.BorderLines blLrtb,
            FastReport.BorderLines blLrb,
            FastReport.BorderLines blLb,
            FastReport.BorderLines blNone,
            string suffix = "")
        {
            int startIdx = pageIndex * pairsPerPage;

            for (int slot = 1; slot <= pairsPerPage; slot++)
            {
                string nn       = slot.ToString("D2");
                int    dataIdx  = startIdx + slot - 1;
                bool   hasData  = dataIdx < playerRows.Count;

                if (hasData)
                {
                    var (bibNo, lName, lKana, lCtry, pName, pKana, pCtry, heatNos) = playerRows[dataIdx];

                    // L行
                    SetTextObjectDirectEx(report, $"DL_{nn}_C01{suffix}", bibNo,  blLtb,  autoShrink: false);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C02{suffix}", lName,  blLrt,  autoShrink: true);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C03{suffix}", lKana,  blLrt,  autoShrink: true);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C04{suffix}", lCtry,  blLrt,  autoShrink: true);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C05{suffix}", "",     blNone, autoShrink: false);
                    for (int di = 0; di < 5; di++)
                    {
                        string cName = $"DL_{nn}_C{di + 6:D2}{suffix}";
                        bool   hasD  = di < danceCount;
                        SetTextObjectDirectEx(report, cName, hasD ? heatNos[di] : "", hasD ? blLrtb : blNone, autoShrink: false);
                    }

                    // P行（C01・C06〜C10 は RowSpan 消費セルのため TextObject なし → 無視される）
                    SetTextObjectDirectEx(report, $"DP_{nn}_C01{suffix}", "",    blLb,  autoShrink: false);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C02{suffix}", pName, blLrb, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C03{suffix}", pKana, blLrb, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C04{suffix}", pCtry, blLrb, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C05{suffix}", "",    blNone, autoShrink: false);
                    for (int di = 0; di < 5; di++)
                    {
                        string cName = $"DP_{nn}_C{di + 6:D2}{suffix}";
                        bool   hasD  = di < danceCount;
                        SetTextObjectDirectEx(report, cName, "", hasD ? blLrb : blNone, autoShrink: false);
                    }
                }
                else
                {
                    // 未使用行: 全列 None・空文字
                    for (int c = 1; c <= 10; c++)
                    {
                        SetTextObjectDirectEx(report, $"DL_{nn}_C{c:D2}{suffix}", "", blNone, autoShrink: false);
                        SetTextObjectDirectEx(report, $"DP_{nn}_C{c:D2}{suffix}", "", blNone, autoShrink: false);
                    }
                }
            }
        }

        /// <summary>
        /// TextObject に Text / Border.Lines / AutoShrink(最小6pt) を設定する。
        /// </summary>
        private static void SetTextObjectDirectEx(Report report, string name, string text,
            FastReport.BorderLines borderLines, bool autoShrink)
        {
            var obj = report.FindObject(name) as FastReport.TextObject;
            if (obj == null) return;
            obj.Text             = text;
            obj.Border.Lines     = borderLines;
            obj.AutoShrink       = autoShrink ? FastReport.AutoShrinkMode.FontSize : FastReport.AutoShrinkMode.None;
            obj.AutoShrinkMinSize = autoShrink ? 6f : obj.AutoShrinkMinSize;
        }



        private static void SetTextInObject(FastReport.Base obj, string name, string text,
            FastReport.BorderLines borderLines = FastReport.BorderLines.All)
        {
            if (obj is FastReport.TextObject txt && txt.Name == name)
            {
                txt.Text = text;
                txt.Border.Lines = borderLines;
                return;
            }
            if (obj is FastReport.IParent parent)
            {
                var children = new FastReport.ObjectCollection();
                parent.GetChildObjects(children);
                foreach (FastReport.Base child in children)
                    SetTextInObject(child, name, text, borderLines);
            }
        }

        /// <summary>
        /// ReportPage を XML シリアライズ → デシリアライズで複製する。
        /// 複製したページの全 TextObject 名に pg サフィックスを付けて名前重複を回避する。
        /// </summary>
        /// <summary>
        /// report.SaveToString() で得た XML 文字列から ReportPage ブロックを取り出し、
        /// 連番サフィックスを付けて複製した XML を返す。
        /// 呼び出し側は report.LoadFromString(newXml) でページを追加した Report に更新する。
        /// </summary>
        private static string? DuplicatePageInReportXml(string reportXml, int totalPages)
        {
            // <ReportPage Name="Page1" ...>...</ReportPage> を取り出す
            // 最初の ReportPage ブロックを全コピーして totalPages 分に増やす
            const string pageOpenTag  = "<ReportPage ";
            const string pageCloseTag = "</ReportPage>";

            int firstOpen  = reportXml.IndexOf(pageOpenTag, StringComparison.Ordinal);
            int firstClose = reportXml.IndexOf(pageCloseTag, StringComparison.Ordinal);
            if (firstOpen < 0 || firstClose < 0) return null;

            string pageXml = reportXml.Substring(firstOpen, firstClose - firstOpen + pageCloseTag.Length);

            // </Report> の直前に追加するページを組み立てる
            var sb = new System.Text.StringBuilder();
            for (int pg = 2; pg <= totalPages; pg++)
            {
                // Name="Page1" → Name="Page{pg}" に置換、全コンポーネント名に _Pxx サフィックス
                string newPageXml = pageXml.Replace("Page1", $"Page{pg}");
                // Band 名・TextObject 名にサフィックスを付ける
                // 簡易置換: Name="XXX" → Name="XXX_P{pg:D2}"（Page{pg} の二重置換を避けるため後から）
                newPageXml = System.Text.RegularExpressions.Regex.Replace(
                    newPageXml,
                    @"Name=""([^""]+)""",
                    m => $"Name=\"{m.Groups[1].Value}_P{pg:D2}\"");
                sb.Append(newPageXml);
            }

            // 元の XML の </Report> 直前に追加
            int insertPos = reportXml.LastIndexOf("</Report>", StringComparison.Ordinal);
            if (insertPos < 0) return null;

            return reportXml.Substring(0, insertPos) + sb.ToString() + reportXml.Substring(insertPos);
        }

        /// <summary>
        /// 出場者連絡票（AJS決勝用・出場者連絡票_AJS決勝.frx）バインド。
        ///
        /// ■ frx 構造
        ///   PageHeader: Table1(区分/ラウンド) / Table2(集計) / Table3(種目) — 縦向き A4 共通
        ///   ReportSummaryBand: Row_01_L〜Row_40_L（左列: 種目名 or ヒート番号）
        ///                      Row_01_R〜Row_40_R（右列: 背番号リスト or ソロ選手情報）
        ///   全 TextObject は Visible="false" で配置。コードが必要行数分だけ表示する。
        ///
        /// ■ 行の種類
        ///   種目名行: _L=種目日本語名（太字・全幅使用）、_R=空
        ///   ヒート行（複数組）: _L="  N Heat"、_R=背番号をスペース区切りで列挙
        ///   ヒート行（1組のみ）: _L="  N Heat"、_R="背番号　L選手名・P選手名　LCtry／PCtry"
        ///
        /// ■ 種目順・ヒート順
        ///   DA_Master.DE_DANCEs の DE_DncNo 昇順 → DS_PRGHEATs の DS_HeatNo 昇順
        /// </summary>
        private void BindPlayerNoticeAjsFinal(Report report, JsonNode? jobData)
        {
            const int MaxRows = 40;

            try
            {
                // ── job.Data から KbnNo / RndNo / DGrpNo を取得 ──────────────────
                string kbnNo  = "";
                string rndNo  = "";
                string dGrpNo = "";

                if (jobData != null)
                {
                    var jd = JObject.Parse(jobData.ToJsonString());
                    kbnNo  = jd["KbnNo"]?.ToString()  ?? "";
                    rndNo  = jd["RndNo"]?.ToString()  ?? "";
                    dGrpNo = jd["DGrpNo"]?.ToString() ?? "";
                }

                _log.LogAdd($"[ReportRenderer] BindPlayerNoticeAjsFinal 開始: KbnNo={kbnNo}, RndNo={rndNo}, DA_Master={((_dataManager.DA_Master != null) ? "OK" : "null")}, DS_Status={((_dataManager.DS_Status != null) ? "OK" : "null")}", _log.INFO);

                if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
                {
                    _log.LogAdd("[ReportRenderer] BindPlayerNoticeAjsFinal: DS_Status/DA_Master が未受信", _log.WARNING);
                    return;
                }

                var dsJson = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                var daJson = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                // ── フロア一覧 ────────────────────────────────────────────────────
                var floors     = dsJson["DS_FLOORs"] as JArray;
                int floorCount = floors?.Count ?? 0;

                // ── 対象 DS_PRGRS を KbnNo / RndNo で検索 ────────────────────────
                JObject? prgrs    = null;
                string   flrCd    = "";
                string   prgNo    = "";
                string   prgSubNo = "";

                if (floors != null)
                {
                    foreach (var floor in floors.OfType<JObject>())
                    {
                        var prgrsList = floor["DS_PRGRSs"] as JArray;
                        if (prgrsList == null) continue;
                        foreach (var p in prgrsList.OfType<JObject>())
                        {
                            if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                            if (p["DS_RndNo"]?.ToString()  != rndNo)  continue;
                            if (!string.IsNullOrEmpty(dGrpNo) &&
                                p["DS_DGrpNo"]?.ToString() != dGrpNo) continue;
                            prgrs    = p;
                            flrCd    = floor["DS_FlrCd"]?.ToString()  ?? "";
                            prgNo    = p["DS_PrgNo"]?.ToString()     ?? "";
                            prgSubNo = p["DS_PrgSubNo"]?.ToString()  ?? "";
                            break;
                        }
                        if (prgrs != null) break;
                    }
                }

                if (prgrs == null)
                {
                    _log.LogAdd($"[ReportRenderer] BindPlayerNoticeAjsFinal: 対象ラウンドが見つかりません KbnNo={kbnNo} RndNo={rndNo}", _log.WARNING);
                    return;
                }

                // ── DA_Master から区分・ラウンド・種目情報を取得 ──────────────────
                var kbnList = daJson["DB_KUBUNs"] as JArray;
                JObject? kbnObj = kbnList?.OfType<JObject>()
                    .FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);

                string kbnCd      = kbnObj?["DB_KbnCd"]?.ToString() ?? "";
                string kbnDspName = kbnObj?["DB_KbnDsipName"]?.ToString()
                                    ?? kbnObj?["DB_KbnDispName"]?.ToString()
                                    ?? kbnObj?["DB_KbnName"]?.ToString() ?? "";

                var rndList  = kbnObj?["DC_ROUNDs"] as JArray;
                JObject? rndObj = rndList?.OfType<JObject>()
                    .FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);

                string rndName  = rndObj?["DC_RndName_J"]?.ToString() ?? rndNo;
                int    rndUpPln = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
                string scrMtd   = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";

                // 種目グループ
                var dgList  = rndObj?["DD_DGRPs"] as JArray;
                int dgCount = dgList?.Count ?? 0;

                JObject? dgrpObj = null;
                if (dgList != null)
                {
                    if (!string.IsNullOrEmpty(dGrpNo))
                        dgrpObj = dgList.OfType<JObject>()
                            .FirstOrDefault(g => g["DD_DGrpNo"]?.ToString() == dGrpNo);
                    dgrpObj ??= dgList.OfType<JObject>().FirstOrDefault();
                }

                string dgrpName = dgrpObj?["DD_DGrpName"]?.ToString() ?? "";
                var dncList     = dgrpObj?["DE_DANCEs"] as JArray;
                var dances      = dncList?.OfType<JObject>()
                    .OrderBy(d => d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                int danceCount = Math.Min(dances.Count, 5);

                string[] dsCodes  = new string[5];
                string[] dcTypes  = new string[5];
                string[] dncNamesJ = new string[5];
                for (int i = 0; i < 5; i++)
                {
                    if (i < dances.Count)
                    {
                        dsCodes[i]   = dances[i]["DE_DncCd"]?.ToString()   ?? "";
                        dcTypes[i]   = dances[i]["DE_DncSG"]?.ToString()   ?? "";
                        dncNamesJ[i] = dances[i]["DE_DncNm_J"]?.ToString() ?? dsCodes[i];
                    }
                }

                // ── KubunName / PRGNO 構築 ────────────────────────────────────────
                string kbnNoDisplay = int.TryParse(kbnNo, out int kbnNoInt)
                    ? kbnNoInt.ToString("D2") : kbnNo;

                var kubunParts = new System.Text.StringBuilder();
                kubunParts.Append(kbnNoDisplay);
                if (!string.IsNullOrEmpty(kbnCd))      kubunParts.Append(" ").Append(kbnCd);
                if (!string.IsNullOrEmpty(kbnDspName)) kubunParts.Append(" ").Append(kbnDspName);
                if (dgCount > 1 && !string.IsNullOrEmpty(dgrpName))
                    kubunParts.Append(" ").Append(dgrpName);
                if (floorCount > 1 && !string.IsNullOrEmpty(flrCd))
                    kubunParts.Append(" ").Append(flrCd).Append("フロア");
                string kubunName = kubunParts.ToString();

                string prgNoFormatted = int.TryParse(prgNo, out int prgNoInt)
                    ? prgNoInt.ToString("D3") : prgNo;
                string prgNoDisplay = string.IsNullOrEmpty(prgSubNo) || prgSubNo == "0" || prgSubNo == "1"
                    ? prgNoFormatted : $"{prgNoFormatted}-{prgSubNo}";

                // ── HeatId→HeatNo マップ（全種目共通）────────────────────────────
                var heatIdToNo   = new Dictionary<string, int>();
                int maxHeatCount = 0;
                var dncArr = prgrs["DS_PRGDANCEs"] as JArray;
                if (dncArr != null)
                {
                    foreach (var dnc in dncArr.OfType<JObject>())
                    {
                        var heatArr = dnc["DS_PRGHEATs"] as JArray;
                        if (heatArr == null) continue;
                        int cnt = 0;
                        foreach (var h in heatArr.OfType<JObject>())
                        {
                            string? hid = h["DS_HeatId"]?.ToString();
                            int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                            if (!string.IsNullOrEmpty(hid) && !heatIdToNo.ContainsKey(hid))
                                heatIdToNo[hid] = hno;
                            cnt++;
                        }
                        if (cnt > maxHeatCount) maxHeatCount = cnt;
                    }
                }
                int totalHeats = heatIdToNo.Count > 0 ? heatIdToNo.Values.Max() : maxHeatCount;

                // ── 種目別 HeatId→HeatNo マップ ──────────────────────────────────
                var dncObjsSorted = dncArr?.OfType<JObject>()
                    .OrderBy(d => d["DS_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                var danceHeatMaps = new List<Dictionary<string, int>>();
                for (int di = 0; di < danceCount; di++)
                {
                    var map = new Dictionary<string, int>();
                    if (di < dncObjsSorted.Count)
                    {
                        var heatArr2 = dncObjsSorted[di]["DS_PRGHEATs"] as JArray;
                        if (heatArr2 != null)
                        {
                            foreach (var h in heatArr2.OfType<JObject>())
                            {
                                string? hid = h["DS_HeatId"]?.ToString();
                                int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                                if (!string.IsNullOrEmpty(hid) && !map.ContainsKey(hid))
                                    map[hid] = hno;
                            }
                        }
                    }
                    danceHeatMaps.Add(map);
                }

                // ── 選手情報マップ（背番号→選手情報）────────────────────────────
                // DB_KbnSenM で指定されたマスタ番号のみを使用する。
                // 同一背番号に複数のマスタが存在する場合、区分に対応するマスタを選択する。
                int targetMasNo = int.TryParse(kbnObj?["DB_KbnSenM"]?.ToString(), out int senM) ? senM : 1;
                var playerInfoMap = BuildPlayerInfoMap(daJson, targetMasNo);

                // ── 種目×ヒートの行リスト構築 ─────────────────────────────────
                // (rowKind: "dance"|"heat", text_L, text_R)
                var assignments = prgrs["PlayerAssignments"] as JArray;

                // 種目インデックス di → ヒート番号 → 選手番号リスト（昇順）
                var danceHeatPlayers = new List<SortedDictionary<int, List<string>>>();
                for (int di = 0; di < danceCount; di++)
                    danceHeatPlayers.Add(new SortedDictionary<int, List<string>>());

                if (assignments != null)
                {
                    foreach (var pa in assignments.OfType<JObject>())
                    {
                        string playerNo = pa["PlayerNo"]?.ToString() ?? "";
                        var   heatIds  = pa["AssignedHeatIds"] as JArray;
                        if (heatIds == null) continue;

                        for (int di = 0; di < danceCount; di++)
                        {
                            string heatId = di < heatIds.Count
                                ? heatIds[di]?.ToString() ?? ""
                                : (heatIds.Count > 0 ? heatIds[0]?.ToString() ?? "" : "");

                            if (!danceHeatMaps[di].TryGetValue(heatId, out int hn)) continue;
                            if (!danceHeatPlayers[di].ContainsKey(hn))
                                danceHeatPlayers[di][hn] = new List<string>();
                            danceHeatPlayers[di][hn].Add(playerNo);
                        }
                    }
                }

                // 背番号数値昇順にソート
                for (int di = 0; di < danceCount; di++)
                    foreach (var kv in danceHeatPlayers[di])
                        kv.Value.Sort((a, b) =>
                        {
                            bool aOk = int.TryParse(a, out int ai);
                            bool bOk = int.TryParse(b, out int bi);
                            if (aOk && bOk) return ai.CompareTo(bi);
                            return string.Compare(a, b, StringComparison.Ordinal);
                        });

                // 表示行リスト: (leftText, rightText, isBold)
                var displayRows = new List<(string L, string R, bool bold)>();

                for (int di = 0; di < danceCount; di++)
                {
                    // 種目名行
                    displayRows.Add((dncNamesJ[di], "", true));

                    // ヒート行
                    foreach (var kv in danceHeatPlayers[di])
                    {
                        int heatNo       = kv.Key;
                        var players      = kv.Value;
                        string heatLabel = $"  {heatNo} Heat";

                        if (players.Count == 1)
                        {
                            // 1組のみ → ソロ表示
                            string no = players[0];
                            if (playerInfoMap.TryGetValue(no, out var info))
                            {
                                // 選手名（フリガナ）形式 ※フリガナなしの場合は括弧ごと省略
                                string lPart = string.IsNullOrEmpty(info.lKana)
                                    ? info.lName
                                    : $"{info.lName}（{info.lKana}）";
                                string pPart = string.IsNullOrEmpty(info.pKana)
                                    ? info.pName
                                    : $"{info.pName}（{info.pKana}）";
                                // 書式: 背番号　　L選手名（LKana）・P選手名（PKana）　　LCtry／PCtry
                                string soloText = $"{no}\u3000\u3000{lPart}・{pPart}\u3000\u3000{info.lCtry}／{info.pCtry}";
                                displayRows.Add((heatLabel, soloText, false));
                            }
                            else
                            {
                                displayRows.Add((heatLabel, no, false));
                            }
                        }
                        else
                        {
                            // 複数組 → 背番号をスペース区切りで並べる
                            string bibNos = string.Join("  ", players.Select(p =>
                                int.TryParse(p, out int n) ? n.ToString() : p));
                            displayRows.Add((heatLabel, bibNos, false));
                        }
                    }
                }

                int totalEntries = assignments?.Count ?? 0;
                bool isShuffle   = prgrs["DS_PrgShuffle"]?.ToObject<bool>() ?? false;
                string upText    = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

                // ══════════════════════════════════════════════════════════════
                // PageHeader: 共通ヘッダー
                // ══════════════════════════════════════════════════════════════
                SetTextObject(report, "Title",   "出場者連絡票");
                SetTextObject(report, "SendTo",  "【　司会　】");
                SetTextObject(report, "PRGNO",     prgNoDisplay);
                SetTextObject(report, "KubunName", kubunName);

                var roundObjs = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                for (int i = 1; i <= 7; i++)
                {
                    string objName = $"Round{i}";
                    if (i - 1 < roundObjs.Count)
                    {
                        var r2     = roundObjs[i - 1];
                        string rn  = r2["DC_RndName_J"]?.ToString() ?? "";
                        bool isCur = r2["DC_RndNo"]?.ToString() == rndNo;
                        SetTextObject(report, objName, rn);
                        SetTextObjectFill(report, objName,
                            isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                    }
                    else
                    {
                        SetTextObject(report, objName, "");
                        SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                }

                SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                SetTextObject(report, "UP",          upText);
                SetTextObject(report, "ScoreMethod", scrMtd);

                for (int i = 1; i <= 5; i++)
                {
                    bool hasDance = i <= danceCount;
                    SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                    SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                    var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                    SetTextObjectFill(report, $"DS{i}", fc);
                    SetTextObjectFill(report, $"DC{i}", fc);
                }

                // ══════════════════════════════════════════════════════════════
                // ReportSummaryBand: Row_XX_L / Row_XX_R に行データをセット
                // ══════════════════════════════════════════════════════════════
                int rowCount = Math.Min(displayRows.Count, MaxRows);

                for (int r = 1; r <= MaxRows; r++)
                {
                    string nn = r.ToString("D2");
                    string nameL = $"Row_{nn}_L";
                    string nameR = $"Row_{nn}_R";

                    var objL = report.FindObject(nameL) as FastReport.TextObject;
                    var objR = report.FindObject(nameR) as FastReport.TextObject;
                    if (objL == null || objR == null) continue;

                    if (r <= rowCount)
                    {
                        var (lText, rText, bold) = displayRows[r - 1];
                        objL.Text    = lText;
                        objR.Text    = rText;
                        objL.Visible = true;
                        objR.Visible = true;

                        // 右列（選手情報）: テキストが長い場合にフォントサイズを自動縮小
                        objR.AutoShrink     = FastReport.AutoShrinkMode.FontSize;
                        objR.AutoShrinkMinSize = 6f; // 最小フォントサイズ 6pt まで縮小

                        if (bold)
                        {
                            // 種目名行: 太字・左列は全幅使用
                            objL.Font = new System.Drawing.Font(objL.Font.FontFamily,
                                objL.Font.Size, System.Drawing.FontStyle.Bold);
                            // 種目名行は左列を全幅に広げ、右列を非表示にする
                            objL.Width = objL.Width + objR.Width;
                            objR.Visible = false;
                        }
                    }
                    else
                    {
                        objL.Visible = false;
                        objR.Visible = false;
                    }
                }

                _log.LogAdd(
                    $"[ReportRenderer] BindPlayerNoticeAjsFinal 完了: " +
                    $"kbn={kbnNo}/{kbnDspName}, rnd={rndName}, " +
                    $"totalHeats={totalHeats}, totalEntries={totalEntries}, rows={rowCount}",
                    _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] BindPlayerNoticeAjsFinal エラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>
        /// 決勝進出者名簿（横向き・決勝進出者名簿_横.frx）バインド。
        ///
        /// ■ frx 構造
        ///   PageHeader : Table1(区分/ラウンド) / Table2(集計) / Table3(種目) — 横向き A4 共通
        ///   DataBand   : Table4 — 列ヘッダー行(RowH) + データ行 DL_nn_Cxx / DP_nn_Cxx
        ///
        /// ■ 行の種類（1組 = 2行）
        ///   DL_{nn}_C01〜C10 : L選手行（背番号・L氏名・Lフリガナ・L所属 + 種目別ヒート番号）
        ///   DP_{nn}_C01〜C10 : P選手行（背番号列は空・P氏名・Pフリガナ・P所属 + ヒート列は空）
        ///
        /// ■ 列構成（C01〜C10）
        ///   C01: 背番号  C02: 氏名  C03: フリガナ  C04: 所属  C05: 区切(罫線なし)
        ///   C06〜C10: 種目1〜5 のヒート番号（例: "1H"）
        ///
        /// ■ 罫線設計
        ///   L行 C01: Left+Top（Bottom なし: 背番号は1組に1つのため下罫線不要）
        ///   L行 C02〜C04: Left+Right+Top
        ///   L行 C06〜C10(種目): Left+Right+Top
        ///   P行 C01: Left+Bottom (背番号なし・下罫線で組区切り)
        ///   P行 C02〜C04: Left+Right+Bottom
        ///   P行 C06〜C10(種目): Left+Right+Bottom
        ///   C05(区切): 常に None
        ///   使用しない種目列・空行: None
        /// </summary>
        private void BindFinalEntryList(Report report, JsonNode? jobData)
        {
            const int PairsPerPage = 10;  // 1ページあたり最大10組
            const int MaxDances    =  5;  // 最大5種目

            try
            {
                // ── job.Data から KbnNo / RndNo / DGrpNo を取得 ──────────────────
                string kbnNo  = "";
                string rndNo  = "";
                string dGrpNo = "";

                if (jobData != null)
                {
                    var jd = JObject.Parse(jobData.ToJsonString());
                    kbnNo  = jd["KbnNo"]?.ToString()  ?? "";
                    rndNo  = jd["RndNo"]?.ToString()  ?? "";
                    dGrpNo = jd["DGrpNo"]?.ToString() ?? "";
                }

                _log.LogAdd($"[ReportRenderer] BindFinalEntryList 開始: KbnNo={kbnNo}, RndNo={rndNo}", _log.INFO);

                if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
                {
                    _log.LogAdd("[ReportRenderer] BindFinalEntryList: DS_Status/DA_Master が未受信", _log.WARNING);
                    return;
                }

                var dsJson = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                var daJson = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                // ── フロア一覧 ────────────────────────────────────────────────────
                var floors     = dsJson["DS_FLOORs"] as JArray;
                int floorCount = floors?.Count ?? 0;

                // ── 対象 DS_PRGRS を KbnNo / RndNo で検索 ────────────────────────
                JObject? prgrs    = null;
                string   flrCd    = "";
                string   prgNo    = "";
                string   prgSubNo = "";

                if (floors != null)
                {
                    foreach (var floor in floors.OfType<JObject>())
                    {
                        var prgrsList = floor["DS_PRGRSs"] as JArray;
                        if (prgrsList == null) continue;
                        foreach (var p in prgrsList.OfType<JObject>())
                        {
                            if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                            if (p["DS_RndNo"]?.ToString()  != rndNo) continue;
                            if (!string.IsNullOrEmpty(dGrpNo) &&
                                p["DS_DGrpNo"]?.ToString() != dGrpNo)
                                continue;
                            prgrs    = p;
                            flrCd    = floor["DS_FlrCd"]?.ToString()  ?? "";
                            prgNo    = p["DS_PrgNo"]?.ToString()     ?? "";
                            prgSubNo = p["DS_PrgSubNo"]?.ToString()  ?? "";
                            break;
                        }
                        if (prgrs != null) break;
                    }
                }

                if (prgrs == null)
                {
                    _log.LogAdd($"[ReportRenderer] BindFinalEntryList: 対象ラウンドが見つかりません KbnNo={kbnNo} RndNo={rndNo}", _log.WARNING);
                    return;
                }

                // ── DA_Master から区分・ラウンド情報を取得 ────────────────────────
                var kbnList = daJson["DB_KUBUNs"] as JArray;
                JObject? kbnObj = kbnList?.OfType<JObject>()
                    .FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);

                string kbnCd      = kbnObj?["DB_KbnCd"]?.ToString() ?? "";
                string kbnDspName = kbnObj?["DB_KbnDsipName"]?.ToString()
                                    ?? kbnObj?["DB_KbnDispName"]?.ToString()
                                    ?? kbnObj?["DB_KbnName"]?.ToString() ?? "";

                var rndList  = kbnObj?["DC_ROUNDs"] as JArray;
                JObject? rndObj = rndList?.OfType<JObject>()
                    .FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);

                string rndName  = rndObj?["DC_RndName_J"]?.ToString() ?? rndNo;
                int    rndUpPln = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
                string scrMtd   = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";

                // ── 種目グループ・種目一覧（最大5種目）───────────────────────────
                var dgList  = rndObj?["DD_DGRPs"] as JArray;
                int dgCount = dgList?.Count ?? 0;

                JObject? dgrpObj = null;
                if (dgList != null)
                {
                    if (!string.IsNullOrEmpty(dGrpNo))
                        dgrpObj = dgList.OfType<JObject>()
                            .FirstOrDefault(g => g["DD_DGrpNo"]?.ToString() == dGrpNo);
                    dgrpObj ??= dgList.OfType<JObject>().FirstOrDefault();
                }

                string dgrpName = dgrpObj?["DD_DGrpName"]?.ToString() ?? "";
                var dncList     = dgrpObj?["DE_DANCEs"] as JArray;
                var dances      = dncList?.OfType<JObject>()
                    .OrderBy(d => d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                int danceCount = Math.Min(dances.Count, MaxDances);

                string[] dsCodes = new string[MaxDances];
                string[] dcTypes = new string[MaxDances];
                for (int i = 0; i < MaxDances; i++)
                {
                    if (i < dances.Count)
                    {
                        dsCodes[i] = dances[i]["DE_DncCd"]?.ToString() ?? "";
                        dcTypes[i] = dances[i]["DE_DncSG"]?.ToString() ?? "";
                    }
                }

                // ── KubunName 構築 ────────────────────────────────────────────────
                string kbnNoDisplay = int.TryParse(kbnNo, out int kbnNoInt)
                    ? kbnNoInt.ToString("D2") : kbnNo;
                var kubunParts = new System.Text.StringBuilder();
                kubunParts.Append(kbnNoDisplay);
                if (!string.IsNullOrEmpty(kbnCd))      kubunParts.Append(" ").Append(kbnCd);
                if (!string.IsNullOrEmpty(kbnDspName)) kubunParts.Append(" ").Append(kbnDspName);
                if (dgCount > 1 && !string.IsNullOrEmpty(dgrpName))
                    kubunParts.Append(" ").Append(dgrpName);
                if (floorCount > 1 && !string.IsNullOrEmpty(flrCd))
                    kubunParts.Append(" ").Append(flrCd).Append("フロア");
                string kubunName = kubunParts.ToString();

                // ── PRGNO 構築 ────────────────────────────────────────────────────
                string prgNoFormatted = int.TryParse(prgNo, out int prgNoInt)
                    ? prgNoInt.ToString("D3") : prgNo;
                string prgNoDisplay = string.IsNullOrEmpty(prgSubNo) || prgSubNo == "0" || prgSubNo == "1"
                    ? prgNoFormatted : $"{prgNoFormatted}-{prgSubNo}";

                // ── 各種目ごとの HeatId→HeatNo マップ ────────────────────────────
                var dncArr = prgrs["DS_PRGDANCEs"] as JArray;
                var dncObjsSorted = dncArr?.OfType<JObject>()
                    .OrderBy(d => d["DS_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                int maxHeatCount = 0;
                var allHeatIdToNo = new Dictionary<string, int>();
                var danceHeatMaps = new List<Dictionary<string, int>>();

                for (int di = 0; di < danceCount; di++)
                {
                    var map = new Dictionary<string, int>();
                    if (di < dncObjsSorted.Count)
                    {
                        var heatArr = dncObjsSorted[di]["DS_PRGHEATs"] as JArray;
                        if (heatArr != null)
                        {
                            int cnt = 0;
                            foreach (var h in heatArr.OfType<JObject>())
                            {
                                string? hid = h["DS_HeatId"]?.ToString();
                                int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                                if (!string.IsNullOrEmpty(hid))
                                {
                                    if (!map.ContainsKey(hid)) map[hid] = hno;
                                    if (!allHeatIdToNo.ContainsKey(hid)) allHeatIdToNo[hid] = hno;
                                }
                                cnt++;
                            }
                            if (cnt > maxHeatCount) maxHeatCount = cnt;
                        }
                    }
                    danceHeatMaps.Add(map);
                }

                int totalHeats = allHeatIdToNo.Count > 0 ? allHeatIdToNo.Values.Max() : maxHeatCount;

                // ── 選手マスタ解決マップ（DM_No → DM_MASTER_J オブジェクト）────
                int targetMasNoF = int.TryParse(kbnObj?["DB_KbnSenM"]?.ToString(), out int senMF) ? senMF : 1;
                var masterMap    = BuildMasterMap(daJson, targetMasNoF);

                // ── PlayerAssignments を背番号昇順で並べ、種目別ヒート番号を解決 ──
                var assignments  = prgrs["PlayerAssignments"] as JArray;
                int totalEntries = assignments?.Count ?? 0;

                // 選手ごとのデータ: (背番号, L氏名, Lフリガナ, L所属, P氏名, Pフリガナ, P所属, ヒート番号[])
                var playerRows = new List<(string bibNo,
                    string lName, string lKana, string lCtry,
                    string pName, string pKana, string pCtry,
                    string[] heatNos)>();

                if (assignments != null)
                {
                    foreach (var pa in assignments.OfType<JObject>())
                    {
                        string playerNo = pa["PlayerNo"]?.ToString() ?? "";
                        JObject? master = masterMap.TryGetValue(playerNo, out var m2) ? m2 : null;

                        string lName = master?["DM_LDispName"]?.ToString()
                                       ?? master?["DM_LName"]?.ToString() ?? "";
                        string lKana = master?["DM_LKana"]?.ToString() ?? "";
                        string lCtry = master?["DM_LCtry"]?.ToString()
                                       ?? master?["DM_Ctry"]?.ToString() ?? "";

                        string pName = master?["DM_PDispName"]?.ToString()
                                       ?? master?["DM_PName"]?.ToString() ?? "";
                        string pKana = master?["DM_PKana"]?.ToString() ?? "";
                        string pCtry = master?["DM_PCtry"]?.ToString()
                                       ?? master?["DM_Ctry"]?.ToString() ?? "";

                        var heatIds  = pa["AssignedHeatIds"] as JArray;
                        string[] heatNos = new string[MaxDances];
                        if (heatIds != null)
                        {
                            for (int di = 0; di < danceCount; di++)
                            {
                                string heatId = di < heatIds.Count
                                    ? heatIds[di]?.ToString() ?? ""
                                    : (heatIds.Count > 0 ? heatIds[0]?.ToString() ?? "" : "");
                                if (danceHeatMaps[di].TryGetValue(heatId, out int hn))
                                    heatNos[di] = $"{hn}H";
                            }
                        }

                        playerRows.Add((playerNo, lName, lKana, lCtry, pName, pKana, pCtry, heatNos));
                    }
                }

                // 背番号数値昇順ソート
                playerRows.Sort((a, b) =>
                {
                    bool aOk = int.TryParse(a.bibNo, out int ai);
                    bool bOk = int.TryParse(b.bibNo, out int bi);
                    if (aOk && bOk) return ai.CompareTo(bi);
                    if (aOk) return -1;
                    if (bOk) return 1;
                    return string.Compare(a.bibNo, b.bibNo, StringComparison.Ordinal);
                });

                // ── シャッフル判定 ─────────────────────────────────────────────
                bool isShuffle = prgrs["DS_PrgShuffle"]?.ToObject<bool>() ?? false;
                string upText  = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

                // ══════════════════════════════════════════════════════════════
                // PageHeader: 共通ヘッダー
                // ══════════════════════════════════════════════════════════════
                SetTextObject(report, "Title",       "決勝進出者名簿");
                SetTextObject(report, "SendTo",      "【　単票　　　】");
                SetTextObject(report, "PRGNO",       prgNoDisplay);
                SetTextObject(report, "KubunName",   kubunName);

                var roundObjs = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                for (int i = 1; i <= 7; i++)
                {
                    string objName = $"Round{i}";
                    if (i - 1 < roundObjs.Count)
                    {
                        var r     = roundObjs[i - 1];
                        string rn = r["DC_RndName_J"]?.ToString() ?? "";
                        bool isCur = r["DC_RndNo"]?.ToString() == rndNo;
                        SetTextObject(report, objName, rn);
                        SetTextObjectFill(report, objName,
                            isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                    }
                    else
                    {
                        SetTextObject(report, objName, "");
                        SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                }

                SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                SetTextObject(report, "UP",          upText);
                SetTextObject(report, "ScoreMethod", scrMtd);

                for (int i = 1; i <= 5; i++)
                {
                    bool hasDance = i <= danceCount;
                    SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                    SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                    var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                    SetTextObjectFill(report, $"DS{i}", fc);
                    SetTextObjectFill(report, $"DC{i}", fc);
                }

                // ══════════════════════════════════════════════════════════════
                // DataBand Table4: 列ヘッダー行（DH_C06〜C10 に種目コードを設定）
                // ══════════════════════════════════════════════════════════════
                for (int i = 1; i <= MaxDances; i++)
                {
                    bool hasDance = i <= danceCount;
                    string colName = $"DH_C{i + 5:D2}";   // DH_C06〜DH_C10
                    SetTextObjectDirect(report, colName, hasDance ? dsCodes[i - 1] : "",
                        hasDance ? FastReport.BorderLines.All : FastReport.BorderLines.None);
                }

                // ══════════════════════════════════════════════════════════════
                // データ行: DL_{nn}_Cxx（L行）/ DP_{nn}_Cxx（P行）
                // 罫線:
                //   L行 C01     : Left, Top, Bottom（背番号: 右は開放）
                //   L行 C02〜C04: Left, Right, Top
                //   L行 C05     : None（区切列）
                //   L行 C06〜C10: Left, Right, Top（種目あり列のみ）
                //   P行 C01     : Left, Bottom（背番号列空・下罫で組区切り）
                //   P行 C02〜C04: Left, Right, Bottom
                //   P行 C05     : None
                //   P行 C06〜C10: Left, Right, Bottom（種目あり列のみ）
                // ══════════════════════════════════════════════════════════════

                // 罫線定数
                // RowSpan=2 の結合セル（C01背番号, C06〜C10ヒート列）は L行 TextObject が
                // 2行分の高さを持つため、Bottom を付けることで組の下罫線を描画する
                var blLtb  = FastReport.BorderLines.Left  | FastReport.BorderLines.Top    | FastReport.BorderLines.Bottom;
                var blLrt  = FastReport.BorderLines.Left  | FastReport.BorderLines.Right  | FastReport.BorderLines.Top;
                var blLrtb = FastReport.BorderLines.All;   // Left+Right+Top+Bottom（結合ヒート列）
                var blLrb  = FastReport.BorderLines.Left  | FastReport.BorderLines.Right  | FastReport.BorderLines.Bottom;
                var blLb   = FastReport.BorderLines.Left  | FastReport.BorderLines.Bottom;
                var blNone = FastReport.BorderLines.None;

                // ══════════════════════════════════════════════════════════════
                // 複数ページ対応: 11組以降は XML でページ複製して 2ページ目以降に表示
                // ══════════════════════════════════════════════════════════════
                int pageCount = Math.Max(1, (int)Math.Ceiling((double)playerRows.Count / PairsPerPage));

                if (pageCount > 1)
                {
                    string origXml = report.SaveToString();
                    string? newXml = DuplicatePageInReportXml(origXml, pageCount);
                    if (newXml != null)
                    {
                        report.LoadFromString(newXml);
                        // 再ロード後にヘッダー TextObject を再設定
                        SetTextObject(report, "Title",       "決勝進出者名簿");
                        SetTextObject(report, "SendTo",      "【　単票　　　】");
                        SetTextObject(report, "PRGNO",       prgNoDisplay);
                        SetTextObject(report, "KubunName",   kubunName);
                        var roundObjsR = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                        for (int i = 1; i <= 7; i++)
                        {
                            if (i - 1 < roundObjsR.Count)
                            {
                                var r2    = roundObjsR[i - 1];
                                string rn = r2["DC_RndName_J"]?.ToString() ?? "";
                                bool isCur = r2["DC_RndNo"]?.ToString() == rndNo;
                                SetTextObject(report, $"Round{i}", rn);
                                SetTextObjectFill(report, $"Round{i}",
                                    isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                            }
                            else
                            {
                                SetTextObject(report, $"Round{i}", "");
                                SetTextObjectFill(report, $"Round{i}", System.Drawing.Color.Transparent);
                            }
                        }
                        SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                        SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                        SetTextObject(report, "UP",          upText);
                        SetTextObject(report, "ScoreMethod", scrMtd);
                        for (int i = 1; i <= 5; i++)
                        {
                            bool hasDance = i <= danceCount;
                            SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                            SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                            var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                            SetTextObjectFill(report, $"DS{i}", fc);
                            SetTextObjectFill(report, $"DC{i}", fc);
                        }
                        // 列ヘッダーも再設定
                        for (int i = 1; i <= MaxDances; i++)
                        {
                            bool hasDance = i <= danceCount;
                            string colName = $"DH_C{i + 5:D2}";
                            SetTextObjectDirect(report, colName, hasDance ? dsCodes[i - 1] : "",
                                hasDance ? FastReport.BorderLines.All : FastReport.BorderLines.None);
                        }
                    }
                }

                // 各ページのデータ行をセット
                for (int pg = 0; pg < pageCount; pg++)
                {
                    string suffix = pg == 0 ? "" : $"_P{pg + 1:D2}";
                    SetFinalEntryPageRows(report, playerRows, pg, PairsPerPage, danceCount,
                        blLtb, blLrt, blLrtb, blLrb, blLb, blNone, suffix);
                }

                _log.LogAdd(
                    $"[ReportRenderer] BindFinalEntryList 完了: " +
                    $"kbn={kbnNo}/{kbnDspName}, rnd={rndName}, " +
                    $"totalHeats={totalHeats}, totalEntries={totalEntries}",
                    _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] BindFinalEntryList エラー: {ex.Message}", _log.ERR);
            }
        }


        /// <summary>
        /// 準決勝進出者名簿（縦向き・準決勝進出者名簿_縦.frx）バインド。
        ///
        /// ■ frx 構造（A4縦・ヒート列なし）
        ///   列構成: C01=背番号(48.77pt) / C02=氏名(201.28pt) / C03=フリガナ(147.64pt) / C04=所属(253.70pt)
        ///   1ページ最大18組（PairsPerPage=18）
        ///   複数ページ対応: DuplicatePageInReportXml で19組以降は2ページ目以降に表示
        ///
        /// ■ TextObject 命名規則
        ///   L行: DL_{nn}_C01〜C04  P行: DP_{nn}_C01〜C04  （nn=01〜18）
        ///   C01（背番号）は RowSpan=2
        /// </summary>
        private void BindSemiFinalEntryList(Report report, JsonNode? jobData)
        {
            const int PairsPerPage = 18;  // 1ページあたり最大18組

            try
            {
                // ── job.Data から KbnNo / RndNo / DGrpNo を取得 ──────────────────
                string kbnNo  = "";
                string rndNo  = "";
                string dGrpNo = "";

                if (jobData != null)
                {
                    var jd = JObject.Parse(jobData.ToJsonString());
                    kbnNo  = jd["KbnNo"]?.ToString()  ?? "";
                    rndNo  = jd["RndNo"]?.ToString()  ?? "";
                    dGrpNo = jd["DGrpNo"]?.ToString() ?? "";
                }

                _log.LogAdd($"[ReportRenderer] BindSemiFinalEntryList 開始: KbnNo={kbnNo}, RndNo={rndNo}", _log.INFO);

                if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
                {
                    _log.LogAdd("[ReportRenderer] BindSemiFinalEntryList: DS_Status/DA_Master が未受信", _log.WARNING);
                    return;
                }

                var dsJson = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                var daJson = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                // ── フロア一覧 ────────────────────────────────────────────────────
                var floors     = dsJson["DS_FLOORs"] as JArray;
                int floorCount = floors?.Count ?? 0;

                // ── 対象 DS_PRGRS を KbnNo / RndNo で検索 ────────────────────────
                JObject? prgrs    = null;
                string   flrCd    = "";
                string   prgNo    = "";
                string   prgSubNo = "";

                if (floors != null)
                {
                    foreach (var floor in floors.OfType<JObject>())
                    {
                        var prgrsList = floor["DS_PRGRSs"] as JArray;
                        if (prgrsList == null) continue;
                        foreach (var p in prgrsList.OfType<JObject>())
                        {
                            if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                            if (p["DS_RndNo"]?.ToString()  != rndNo)  continue;
                            if (!string.IsNullOrEmpty(dGrpNo) && p["DS_DGrpNo"]?.ToString() != dGrpNo) continue;
                            prgrs    = p;
                            flrCd    = floor["DS_FlrCd"]?.ToString() ?? "";
                            prgNo    = p["DS_PrgNo"]?.ToString()    ?? "";
                            prgSubNo = p["DS_PrgSubNo"]?.ToString() ?? "";
                            break;
                        }
                        if (prgrs != null) break;
                    }
                }

                if (prgrs == null)
                {
                    _log.LogAdd($"[ReportRenderer] BindSemiFinalEntryList: 対象ラウンドが見つかりません KbnNo={kbnNo} RndNo={rndNo}", _log.WARNING);
                    return;
                }

                // ── DA_Master から区分・ラウンド情報を取得 ──────────────────────
                var kubuns    = daJson["DB_KUBUNs"] as JArray;
                JObject? kubun = kubuns?.OfType<JObject>().FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);

                string kbnDspName = kubun?["DB_KbnDsipName"]?.ToString()
                                 ?? kubun?["DB_KbnDispName"]?.ToString()
                                 ?? kubun?["DB_KbnName"]?.ToString()
                                 ?? "";
                string kbnCd      = kubun?["DB_KbnCd"]?.ToString() ?? "";

                var rndList = kubun?["DC_ROUNDs"] as JArray;
                JObject? rndObj = rndList?.OfType<JObject>().FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);

                string rndName   = rndObj?["DC_RndName_J"]?.ToString() ?? "";
                int    rndUpPln  = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
                string scrMtd    = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";

                // 種目コード・SG種別（ヘッダー Table3 用）
                var dgrps = !string.IsNullOrEmpty(dGrpNo)
                    ? rndObj?["DD_DGRPs"] as JArray
                    : rndObj?["DD_DGRPs"] as JArray;
                JObject? dgrp = dgrps?.OfType<JObject>()
                    .FirstOrDefault(g => string.IsNullOrEmpty(dGrpNo) || g["DD_DGrpNo"]?.ToString() == dGrpNo);
                var dances = (dgrp?["DE_DANCEs"] as JArray)?.OfType<JObject>()
                    .OrderBy(d => d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();
                int danceCount = dances.Count;
                var dsCodes  = dances.Select(d => d["DE_DncCd"]?.ToString()  ?? "").ToArray();
                var dcTypes  = dances.Select(d => d["DE_DncSG"]?.ToString()  ?? "").ToArray();

                // ── PlayerAssignments から選手一覧を構築 ─────────────────────────
                var assignments = (prgrs["PlayerAssignments"] as JArray)?.OfType<JObject>().ToList()
                               ?? new List<JObject>();
                int totalEntries = assignments.Count;
                int totalHeats   = (prgrs["DS_PRGDANCEs"] as JArray)?.OfType<JObject>()
                    .SelectMany(d => (d["DS_PRGHEATs"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    .Select(h => h["DS_HeatNo"]?.ToObject<int>() ?? 0)
                    .DefaultIfEmpty(0).Max() ?? 0;

                // DA_Master 選手マップ（DM_No → master）
                int targetMasNoSF = int.TryParse(kubun?["DB_KbnSenM"]?.ToString(), out int senMSF) ? senMSF : 1;
                var memberMap     = BuildMasterMap(daJson, targetMasNoSF);

                // 選手行リスト（bibNo / lName / lKana / lCtry / pName / pKana / pCtry）
                var playerRows = new List<(string bibNo,
                    string lName, string lKana, string lCtry,
                    string pName, string pKana, string pCtry)>();

                foreach (var assign in assignments)
                {
                    string playerNo = assign["PlayerNo"]?.ToString() ?? "";
                    if (!memberMap.TryGetValue(playerNo, out var master)) continue;

                    string lName = master["DM_LDispName"]?.ToString() ?? master["DM_LName"]?.ToString() ?? "";
                    string lKana = master["DM_LKana"]?.ToString() ?? "";
                    string lCtry = master["DM_LCtry"]?.ToString() ?? master["DM_Ctry"]?.ToString() ?? "";
                    string pName = master["DM_PDispName"]?.ToString() ?? master["DM_PName"]?.ToString() ?? "";
                    string pKana = master["DM_PKana"]?.ToString() ?? "";
                    string pCtry = master["DM_PCtry"]?.ToString() ?? master["DM_Ctry"]?.ToString() ?? "";

                    playerRows.Add((playerNo, lName, lKana, lCtry, pName, pKana, pCtry));
                }

                // 背番号数値昇順ソート
                playerRows.Sort((a, b) =>
                {
                    bool aOk = int.TryParse(a.bibNo, out int ai);
                    bool bOk = int.TryParse(b.bibNo, out int bi);
                    if (aOk && bOk) return ai.CompareTo(bi);
                    if (aOk) return -1;
                    if (bOk) return 1;
                    return string.Compare(a.bibNo, b.bibNo, StringComparison.Ordinal);
                });

                bool isShuffle = prgrs["DS_PrgShuffle"]?.ToObject<bool>() ?? false;
                string upText  = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

                // ── 進行番号表示 ─────────────────────────────────────────────────
                string prgNoDisplay = int.TryParse(prgNo, out int pn) ? pn.ToString("D3") : prgNo;
                if (!string.IsNullOrEmpty(prgSubNo) && prgSubNo != "0" && prgSubNo != "1")
                    prgNoDisplay += $"-{prgSubNo}";

                // ── 区分名表示 ──────────────────────────────────────────────────
                int kbnNoInt = int.TryParse(kbnNo, out int kni) ? kni : 0;
                string kubunNameDisplay = $"{kbnNoInt:D2}";
                if (!string.IsNullOrEmpty(kbnCd))       kubunNameDisplay += $" {kbnCd}";
                if (!string.IsNullOrEmpty(kbnDspName))   kubunNameDisplay += $" {kbnDspName}";

                // ── PageHeader: 共通ヘッダー ──────────────────────────────────────
                SetTextObject(report, "Title",       "準決勝進出者名簿");
                SetTextObject(report, "SendTo",      "【　単票　　　】");
                SetTextObject(report, "PRGNO",       prgNoDisplay);
                SetTextObject(report, "KubunName",   kubunNameDisplay);

                var roundObjs = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                for (int i = 1; i <= 7; i++)
                {
                    string objName = $"Round{i}";
                    if (i - 1 < roundObjs.Count)
                    {
                        var r      = roundObjs[i - 1];
                        string rn  = r["DC_RndName_J"]?.ToString() ?? "";
                        bool isCur = r["DC_RndNo"]?.ToString() == rndNo;
                        SetTextObject(report, objName, rn);
                        SetTextObjectFill(report, objName,
                            isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                    }
                    else
                    {
                        SetTextObject(report, objName, "");
                        SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                }

                SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                SetTextObject(report, "UP",          upText);
                SetTextObject(report, "ScoreMethod", scrMtd);

                for (int i = 1; i <= 5; i++)
                {
                    bool hasDance = i <= danceCount;
                    SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                    SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                    var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                    SetTextObjectFill(report, $"DS{i}", fc);
                    SetTextObjectFill(report, $"DC{i}", fc);
                }

                // ── 複数ページ対応 ─────────────────────────────────────────────
                int pageCount = Math.Max(1, (int)Math.Ceiling((double)playerRows.Count / PairsPerPage));

                if (pageCount > 1)
                {
                    string origXml = report.SaveToString();
                    string? newXml = DuplicatePageInReportXml(origXml, pageCount);
                    if (newXml != null)
                    {
                        report.LoadFromString(newXml);
                        // 再ロード後にヘッダーを再セット
                        SetTextObject(report, "Title",     "準決勝進出者名簿");
                        SetTextObject(report, "SendTo",    "【　単票　　　】");
                        SetTextObject(report, "PRGNO",     prgNoDisplay);
                        SetTextObject(report, "KubunName", kubunNameDisplay);
                        var roundObjsR = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                        for (int i = 1; i <= 7; i++)
                        {
                            if (i - 1 < roundObjsR.Count)
                            {
                                var r2    = roundObjsR[i - 1];
                                string rn = r2["DC_RndName_J"]?.ToString() ?? "";
                                bool isCur = r2["DC_RndNo"]?.ToString() == rndNo;
                                SetTextObject(report, $"Round{i}", rn);
                                SetTextObjectFill(report, $"Round{i}",
                                    isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                            }
                            else
                            {
                                SetTextObject(report, $"Round{i}", "");
                                SetTextObjectFill(report, $"Round{i}", System.Drawing.Color.Transparent);
                            }
                        }
                        SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                        SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                        SetTextObject(report, "UP",          upText);
                        SetTextObject(report, "ScoreMethod", scrMtd);
                        for (int i = 1; i <= 5; i++)
                        {
                            bool hasDance = i <= danceCount;
                            SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                            SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                            var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                            SetTextObjectFill(report, $"DS{i}", fc);
                            SetTextObjectFill(report, $"DC{i}", fc);
                        }
                    }
                }

                // ── 各ページのデータ行をセット ─────────────────────────────────
                var blLt  = FastReport.BorderLines.Left  | FastReport.BorderLines.Top;
                var blLrt = FastReport.BorderLines.Left  | FastReport.BorderLines.Right | FastReport.BorderLines.Top;
                var blLrb = FastReport.BorderLines.Left  | FastReport.BorderLines.Right | FastReport.BorderLines.Bottom;
                var blLb  = FastReport.BorderLines.Left  | FastReport.BorderLines.Bottom;

                for (int pg = 0; pg < pageCount; pg++)
                {
                    string suffix = pg == 0 ? "" : $"_P{pg + 1:D2}";
                    SetSemiFinalEntryPageRows(report, playerRows, pg, PairsPerPage, blLt, blLrt, blLrb, blLb, suffix);
                }

                _log.LogAdd(
                    $"[ReportRenderer] BindSemiFinalEntryList 完了: " +
                    $"kbn={kbnNo}/{kbnDspName}, rnd={rndName}, totalEntries={totalEntries}",
                    _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] BindSemiFinalEntryList エラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>
        /// 準決勝進出者名簿の1ページ分データ行をセットする（ヒート列なし・4列構成）。
        /// C01=背番号(RowSpan=2) / C02=氏名 / C03=フリガナ / C04=所属
        /// </summary>
        private static void SetSemiFinalEntryPageRows(
            Report report,
            List<(string bibNo,
                  string lName, string lKana, string lCtry,
                  string pName, string pKana, string pCtry)> playerRows,
            int pageIndex,
            int pairsPerPage,
            FastReport.BorderLines blLt,
            FastReport.BorderLines blLrt,
            FastReport.BorderLines blLrb,
            FastReport.BorderLines blLb,
            string suffix = "")
        {
            int startIdx = pageIndex * pairsPerPage;
            var blNone = FastReport.BorderLines.None;
            var blLtb  = FastReport.BorderLines.Left | FastReport.BorderLines.Top | FastReport.BorderLines.Bottom;

            for (int slot = 1; slot <= pairsPerPage; slot++)
            {
                string nn      = slot.ToString("D2");
                int    dataIdx = startIdx + slot - 1;
                bool   hasData = dataIdx < playerRows.Count;

                if (hasData)
                {
                    var (bibNo, lName, lKana, lCtry, pName, pKana, pCtry) = playerRows[dataIdx];

                    // L行: C01=背番号(Left+Top+Bottom=ltb), C02〜C04=Left+Right+Top
                    SetTextObjectDirectEx(report, $"DL_{nn}_C01{suffix}", bibNo,  blLtb, autoShrink: false);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C02{suffix}", lName,  blLrt, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C03{suffix}", lKana,  blLrt, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C04{suffix}", lCtry,  blLrt, autoShrink: true);

                    // P行: C01=空(Left+Bottom), C02〜C04=Left+Right+Bottom
                    SetTextObjectDirectEx(report, $"DP_{nn}_C01{suffix}", "",    blLb,  autoShrink: false);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C02{suffix}", pName, blLrb, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C03{suffix}", pKana, blLrb, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C04{suffix}", pCtry, blLrb, autoShrink: true);
                }
                else
                {
                    // 未使用行: 全列 None・空文字
                    for (int c = 1; c <= 4; c++)
                    {
                        SetTextObjectDirectEx(report, $"DL_{nn}_C{c:D2}{suffix}", "", blNone, autoShrink: false);
                        SetTextObjectDirectEx(report, $"DP_{nn}_C{c:D2}{suffix}", "", blNone, autoShrink: false);
                    }
                }
            }
        }


        /// <summary>
        /// 得点一覧表（AJS採点方式・横向き・得点一覧表_AJS_横.frx）バインド。
        ///
        /// ■ job.Data フォーマット（PR_PRINT.data）
        ///   {
        ///     "KbnNo":  "2",
        ///     "RndNo":  "4",
        ///     "DGrpNo": "",
        ///     "採点方式ID":  "AJS31",
        ///     "採点方式名":  "AJS3.1J for PD",
        ///     "区分番号": "2",
        ///     "区分名":   "...",
        ///     "ラウンド番号": "4",
        ///     "ラウンド名":   "決勝",
        ///     "総合結果": [...],
        ///     "種目結果": [...]
        ///   }
        ///
        /// ■ frx TextObject 名規則
        ///   PageHeader: PRGNO, KubunName, Round1〜7, TotalHeat, TotalComp, UP, ScoreMethod, DS1〜5, DC1〜5
        ///   列ヘッダー: CH_C01=背番号, CH_C02=L選手, CH_C03=P選手, CH_C04=合計点, CH_C05=順位, CH_C06=結果, CH_C07〜C11=種目1〜5
        ///   データ行: DR_{nn}_C01〜C11 （nn=01〜16）
        ///
        /// ■ 複数ページ対応
        ///   1ページ最大16組。17組以降は DuplicatePageInReportXml で2ページ目以降。
        /// </summary>
        private void BindAjsScoreList(Report report, JsonNode? jobData)
        {
            const int PairsPerPage = 16;
            const int MaxDances    =  7;

            try
            {
                // ── job.Data（DV_Result）から KbnNo / RndNo / DGrpNo を取得 ───
                // サーバーは DV_Result をそのまま data に入れて送ってくる。
                // DV_Result の "区分番号" / "ラウンド番号" を優先し、
                // テスト用の "KbnNo" / "RndNo" をフォールバックとして使用する。
                string kbnNo  = "";
                string rndNo  = "";
                string dGrpNo = "";

                JObject? jd = null;
                if (jobData != null)
                {
                    jd     = JObject.Parse(jobData.ToJsonString());
                    kbnNo  = jd["区分番号"]?.ToString()
                             ?? jd["KbnNo"]?.ToString()  ?? "";
                    rndNo  = jd["ラウンド番号"]?.ToString()
                             ?? jd["RndNo"]?.ToString()  ?? "";
                    dGrpNo = jd["DGrpNo"]?.ToString() ?? "";
                }

                _log.LogAdd($"[ReportRenderer] BindAjsScoreList 開始: KbnNo={kbnNo}, RndNo={rndNo}", _log.INFO);

                if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
                {
                    _log.LogAdd("[ReportRenderer] BindAjsScoreList: DS_Status/DA_Master が未受信", _log.WARNING);
                    return;
                }

                var dsJson = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                var daJson = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                // ── フロア一覧 ─────────────────────────────────────────────────
                var floors     = dsJson["DS_FLOORs"] as JArray;
                int floorCount = floors?.Count ?? 0;

                // ── 対象 DS_PRGRS を KbnNo / RndNo で検索 ──────────────────────
                JObject? prgrs    = null;
                string   flrCd    = "";
                string   prgNo    = "";
                string   prgSubNo = "";

                if (floors != null)
                {
                    foreach (var floor in floors.OfType<JObject>())
                    {
                        var prgrsList = floor["DS_PRGRSs"] as JArray;
                        if (prgrsList == null) continue;
                        foreach (var p in prgrsList.OfType<JObject>())
                        {
                            if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                            if (p["DS_RndNo"]?.ToString()  != rndNo)  continue;
                            if (!string.IsNullOrEmpty(dGrpNo) &&
                                p["DS_DGrpNo"]?.ToString() != dGrpNo)
                                continue;
                            prgrs    = p;
                            flrCd    = floor["DS_FlrCd"]?.ToString()  ?? "";
                            prgNo    = p["DS_PrgNo"]?.ToString()     ?? "";
                            prgSubNo = p["DS_PrgSubNo"]?.ToString()  ?? "";
                            break;
                        }
                        if (prgrs != null) break;
                    }
                }

                if (prgrs == null)
                {
                    _log.LogAdd($"[ReportRenderer] BindAjsScoreList: 対象ラウンドが見つかりません KbnNo={kbnNo} RndNo={rndNo}", _log.WARNING);
                    return;
                }

                // ── DA_Master から区分・ラウンド情報を取得 ─────────────────────
                var kbnList = daJson["DB_KUBUNs"] as JArray;
                JObject? kbnObj = kbnList?.OfType<JObject>()
                    .FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);

                string kbnCd      = kbnObj?["DB_KbnCd"]?.ToString() ?? "";
                string kbnDspName = kbnObj?["DB_KbnDsipName"]?.ToString()
                                    ?? kbnObj?["DB_KbnDispName"]?.ToString()
                                    ?? kbnObj?["DB_KbnName"]?.ToString() ?? "";

                var rndList  = kbnObj?["DC_ROUNDs"] as JArray;
                JObject? rndObj = rndList?.OfType<JObject>()
                    .FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);

                string rndName  = rndObj?["DC_RndName_J"]?.ToString() ?? rndNo;
                int    rndUpPln = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
                string scrMtd   = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";

                // ── 種目グループ・種目一覧（最大5種目）──────────────────────────
                var dgList  = rndObj?["DD_DGRPs"] as JArray;
                int dgCount = dgList?.Count ?? 0;

                JObject? dgrpObj = null;
                if (dgList != null)
                {
                    if (!string.IsNullOrEmpty(dGrpNo))
                        dgrpObj = dgList.OfType<JObject>()
                            .FirstOrDefault(g => g["DD_DGrpNo"]?.ToString() == dGrpNo);
                    dgrpObj ??= dgList.OfType<JObject>().FirstOrDefault();
                }

                string dgrpName = dgrpObj?["DD_DGrpName"]?.ToString() ?? "";
                var dncList     = dgrpObj?["DE_DANCEs"] as JArray;
                var dances      = dncList?.OfType<JObject>()
                    .OrderBy(d => d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                int danceCount = Math.Min(dances.Count, MaxDances);

                string[] dsCodes = new string[MaxDances];
                string[] dcTypes = new string[MaxDances];
                for (int i = 0; i < MaxDances; i++)
                {
                    if (i < dances.Count)
                    {
                        dsCodes[i] = dances[i]["DE_DncCd"]?.ToString() ?? "";
                        dcTypes[i] = dances[i]["DE_DncSG"]?.ToString() ?? "";
                    }
                }

                // ── KubunName 構築 ──────────────────────────────────────────────
                string kbnNoDisplay = int.TryParse(kbnNo, out int kbnNoInt)
                    ? kbnNoInt.ToString("D2") : kbnNo;
                var kubunParts = new System.Text.StringBuilder();
                kubunParts.Append(kbnNoDisplay);
                if (!string.IsNullOrEmpty(kbnCd))      kubunParts.Append(" ").Append(kbnCd);
                if (!string.IsNullOrEmpty(kbnDspName)) kubunParts.Append(" ").Append(kbnDspName);
                if (dgCount > 1 && !string.IsNullOrEmpty(dgrpName))
                    kubunParts.Append(" ").Append(dgrpName);
                if (floorCount > 1 && !string.IsNullOrEmpty(flrCd))
                    kubunParts.Append(" ").Append(flrCd).Append("フロア");
                string kubunName = kubunParts.ToString();

                // ── PRGNO 構築 ──────────────────────────────────────────────────
                string prgNoFormatted = int.TryParse(prgNo, out int prgNoInt)
                    ? prgNoInt.ToString("D3") : prgNo;
                string prgNoDisplay = string.IsNullOrEmpty(prgSubNo) || prgSubNo == "0" || prgSubNo == "1"
                    ? prgNoFormatted : $"{prgNoFormatted}-{prgSubNo}";

                // ── ヒート数・出場者数 ──────────────────────────────────────────
                var dncArr = prgrs["DS_PRGDANCEs"] as JArray;
                var heatIdToNo = new Dictionary<string, int>();
                int maxHeatCount = 0;
                if (dncArr != null)
                {
                    foreach (var dnc in dncArr.OfType<JObject>())
                    {
                        var heatArr = dnc["DS_PRGHEATs"] as JArray;
                        if (heatArr == null) continue;
                        int cnt = 0;
                        foreach (var h in heatArr.OfType<JObject>())
                        {
                            string? hid = h["DS_HeatId"]?.ToString();
                            int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                            if (!string.IsNullOrEmpty(hid) && !heatIdToNo.ContainsKey(hid))
                                heatIdToNo[hid] = hno;
                            cnt++;
                        }
                        if (cnt > maxHeatCount) maxHeatCount = cnt;
                    }
                }
                int totalHeats   = heatIdToNo.Count > 0 ? heatIdToNo.Values.Max() : maxHeatCount;
                var assignments  = prgrs["PlayerAssignments"] as JArray;
                int totalEntries = assignments?.Count ?? 0;
                bool isShuffle   = prgrs["DS_PrgShuffle"]?.ToObject<bool>() ?? false;

                // ── 次ラウンドの PlayerAssignments を取得（UP判定用）──────────
                // ラウンド番号の昇順定義: 010-020-030-040-050-090-100-200-400
                // 整数値で比較して現在より大きい最初のラウンドを「次ラウンド」とする
                var nextRndAssignedPlayerNos = new HashSet<string>();
                if (rndList != null)
                {
                    // 全ラウンドを DC_RndNo の整数値昇順でソート
                    var sortedRnds = rndList.OfType<JObject>()
                        .Select(r => new {
                            obj    = r,
                            rndNoStr = r["DC_RndNo"]?.ToString() ?? "",
                            rndNoInt = int.TryParse(r["DC_RndNo"]?.ToString(), out int ri) ? ri : int.MaxValue
                        })
                        .OrderBy(x => x.rndNoInt)
                        .ToList();

                    // 現在ラウンド番号の整数値
                    int curRndNoInt = int.TryParse(rndNo, out int crni) ? crni : int.MaxValue;

                    // 現在より大きい最初のラウンド番号を探す
                    string? nextRndNoStr = sortedRnds.FirstOrDefault(x => x.rndNoInt > curRndNoInt)?.rndNoStr;

                    if (!string.IsNullOrEmpty(nextRndNoStr))
                    {
                        // DS_Status の DS_PRGRSs から次ラウンドの PlayerAssignments を取得
                        if (floors != null)
                        {
                            foreach (var floor in floors.OfType<JObject>())
                            {
                                var prgrsList = floor["DS_PRGRSs"] as JArray;
                                if (prgrsList == null) continue;
                                foreach (var p in prgrsList.OfType<JObject>())
                                {
                                    if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                                    if (p["DS_RndNo"]?.ToString()  != nextRndNoStr)  continue;
                                    // DGrpNo が指定されている場合は絞り込む
                                    if (!string.IsNullOrEmpty(dGrpNo) &&
                                        p["DS_DGrpNo"]?.ToString() != dGrpNo) continue;
                                    var nextAssigns = p["PlayerAssignments"] as JArray;
                                    if (nextAssigns != null)
                                    {
                                        foreach (var pa in nextAssigns.OfType<JObject>())
                                        {
                                            string? no = pa["PlayerNo"]?.ToString();
                                            if (!string.IsNullOrEmpty(no))
                                                nextRndAssignedPlayerNos.Add(no);
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }

                // ── 選手名解決マップ（背番号 → L苗字, P苗字）───────────────────
                int targetMasNoA = int.TryParse(kbnObj?["DB_KbnSenM"]?.ToString(), out int senMA) ? senMA : 1;
                var infoMapA     = BuildPlayerInfoMap(daJson, targetMasNoA);
                var playerNameMap = infoMapA.ToDictionary(
                    kv => kv.Key,
                    kv => (ExtractLastName(kv.Value.lName), ExtractLastName(kv.Value.pName)),
                    StringComparer.Ordinal);

                // ── DV_Result から採点データを解析 ─────────────────────────────
                // job.Data の 総合結果[] と 種目結果[].選手結果[] を使用
                // 背番号 → (総合得点, 総合順位表記, 種目得点[])
                var scoreMap = new Dictionary<string, (double totalScore, string rankDisplay, double[] danceScores)>();

                if (jd != null)
                {
                    var soGoKekka = jd["総合結果"] as JArray;
                    var shomokuKekka = jd["種目結果"] as JArray;

                    // 種目結果を種目順（種目順）でソート → 各選手の種目得点配列を構築
                    // 種目コード → danceIndex マップを構築（DA_Master の種目順に対応）
                    var danceCodeToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int di = 0; di < danceCount; di++)
                        danceCodeToIdx[dsCodes[di]] = di;

                    // 選手ごとの種目得点 (背番号 → double[danceCount])
                    var playerDanceScores = new Dictionary<string, double[]>();

                    if (shomokuKekka != null)
                    {
                        foreach (var shomoku in shomokuKekka.OfType<JObject>())
                        {
                            string dncCd = shomoku["種目記号"]?.ToString() ?? "";
                            if (!danceCodeToIdx.TryGetValue(dncCd, out int di)) continue;

                            var senshuKekka = shomoku["選手結果"] as JArray;
                            if (senshuKekka == null) continue;

                            foreach (var sk in senshuKekka.OfType<JObject>())
                            {
                                string bibNo = sk["背番号"]?.ToString() ?? "";
                                if (string.IsNullOrEmpty(bibNo)) continue;

                                if (!playerDanceScores.ContainsKey(bibNo))
                                    playerDanceScores[bibNo] = new double[MaxDances];

                                double score = sk["種目得点"]?.ToObject<double>() ?? 0.0;
                                playerDanceScores[bibNo][di] = score;
                            }
                        }
                    }

                    if (soGoKekka != null)
                    {
                        foreach (var sg in soGoKekka.OfType<JObject>())
                        {
                            string bibNo   = sg["背番号"]?.ToString() ?? "";
                            double total   = sg["総合得点"]?.ToObject<double>() ?? 0.0;
                            string rankStr = sg["総合順位表記"]?.ToString() ?? "";

                            if (string.IsNullOrEmpty(bibNo)) continue;

                            double[] dScores = playerDanceScores.TryGetValue(bibNo, out var ds2)
                                ? ds2 : new double[MaxDances];

                            scoreMap[bibNo] = (total, rankStr, dScores);
                        }
                    }
                }

                // ── 出場者リストを背番号昇順で構築 ─────────────────────────────
                // PlayerAssignments の背番号順に並べ、scoreMap と結合
                var playerRows = new List<(
                    string bibNo,
                    string lLastName,
                    string pLastName,
                    double totalScore,
                    string rankDisplay,
                    string upResult,
                    double[] danceScores)>();

                if (assignments != null)
                {
                    foreach (var pa in assignments.OfType<JObject>())
                    {
                        string playerNo = pa["PlayerNo"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(playerNo)) continue;

                        var (lLast, pLast) = playerNameMap.TryGetValue(playerNo, out var names)
                            ? names : ("", "");

                        string up = nextRndAssignedPlayerNos.Contains(playerNo) ? "UP" : "";

                        double total    = 0.0;
                        string rankDisp = "";
                        double[] dScores = new double[MaxDances];

                        if (scoreMap.TryGetValue(playerNo, out var sc))
                        {
                            total    = sc.totalScore;
                            rankDisp = sc.rankDisplay;
                            dScores  = sc.danceScores;
                        }

                        playerRows.Add((playerNo, lLast, pLast, total, rankDisp, up, dScores));
                    }
                }

                // 背番号数値昇順ソート
                playerRows.Sort((a, b) =>
                {
                    bool aOk = int.TryParse(a.bibNo, out int ai);
                    bool bOk = int.TryParse(b.bibNo, out int bi);
                    if (aOk && bOk) return ai.CompareTo(bi);
                    if (aOk) return -1;
                    if (bOk) return 1;
                    return string.Compare(a.bibNo, b.bibNo, StringComparison.Ordinal);
                });

                string upText = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

                // ══════════════════════════════════════════════════════════════
                // PageHeader: 共通ヘッダー部分
                // ══════════════════════════════════════════════════════════════
                SetTextObject(report, "Title",   "得点一覧表");
                SetTextObject(report, "SendTo",  "");
                SetTextObject(report, "PRGNO",     prgNoDisplay);
                SetTextObject(report, "KubunName", kubunName);

                var roundObjs = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                for (int i = 1; i <= 7; i++)
                {
                    string objName = $"Round{i}";
                    if (i - 1 < roundObjs.Count)
                    {
                        var r     = roundObjs[i - 1];
                        string rn = r["DC_RndName_J"]?.ToString() ?? "";
                        bool isCur = r["DC_RndNo"]?.ToString() == rndNo;
                        SetTextObject(report, objName, rn);
                        SetTextObjectFill(report, objName,
                            isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                    }
                    else
                    {
                        SetTextObject(report, objName, "");
                        SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                }

                SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                SetTextObject(report, "UP",          upText);
                SetTextObject(report, "ScoreMethod", scrMtd);

                for (int i = 1; i <= 5; i++)
                {
                    bool hasDance = i <= danceCount;
                    SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                    SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                    var fillColor = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                    SetTextObjectFill(report, $"DS{i}", fillColor);
                    SetTextObjectFill(report, $"DC{i}", fillColor);
                }

                // ── 列ヘッダーの種目コード（CH_C06〜C12）─────────────────────
                for (int i = 1; i <= MaxDances; i++)
                {
                    bool hasDance = i <= danceCount;
                    string colName = $"CH_C{i + 5:D2}";   // CH_C06〜CH_C12
                    SetTextObjectDirect(report, colName, hasDance ? dsCodes[i - 1] : "",
                        hasDance ? FastReport.BorderLines.All : FastReport.BorderLines.None);
                }

                // ══════════════════════════════════════════════════════════════
                // 複数ページ対応
                // ══════════════════════════════════════════════════════════════
                int pageCount = Math.Max(1, (int)Math.Ceiling((double)playerRows.Count / PairsPerPage));

                if (pageCount > 1)
                {
                    string origXml = report.SaveToString();
                    string? newXml = DuplicatePageInReportXml(origXml, pageCount);
                    if (newXml != null)
                    {
                        report.LoadFromString(newXml);
                        // 再ロード後にヘッダーを再設定
                        SetTextObject(report, "Title",       "得点一覧表");
                        SetTextObject(report, "SendTo",      "");
                        SetTextObject(report, "PRGNO",       prgNoDisplay);
                        SetTextObject(report, "KubunName",   kubunName);
                        var roundObjsR = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                        for (int i = 1; i <= 7; i++)
                        {
                            if (i - 1 < roundObjsR.Count)
                            {
                                var r2    = roundObjsR[i - 1];
                                string rn = r2["DC_RndName_J"]?.ToString() ?? "";
                                bool isCur = r2["DC_RndNo"]?.ToString() == rndNo;
                                SetTextObject(report, $"Round{i}", rn);
                                SetTextObjectFill(report, $"Round{i}",
                                    isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                            }
                            else
                            {
                                SetTextObject(report, $"Round{i}", "");
                                SetTextObjectFill(report, $"Round{i}", System.Drawing.Color.Transparent);
                            }
                        }
                        SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                        SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                        SetTextObject(report, "UP",          upText);
                        SetTextObject(report, "ScoreMethod", scrMtd);
                        for (int i = 1; i <= 5; i++)
                        {
                            bool hasDance = i <= danceCount;
                            SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                            SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                            var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                            SetTextObjectFill(report, $"DS{i}", fc);
                            SetTextObjectFill(report, $"DC{i}", fc);
                        }
                        for (int i = 1; i <= MaxDances; i++)
                        {
                            bool hasDance = i <= danceCount;
                            string colName = $"CH_C{i + 5:D2}";   // CH_C06〜CH_C12
                            SetTextObjectDirect(report, colName, hasDance ? dsCodes[i - 1] : "",
                                hasDance ? FastReport.BorderLines.All : FastReport.BorderLines.None);
                        }
                    }
                }

                // ── 各ページのデータ行をセット ─────────────────────────────────
                for (int pg = 0; pg < pageCount; pg++)
                {
                    string suffix = pg == 0 ? "" : $"_P{pg + 1:D2}";
                    SetAjsScorePageRows(report, playerRows, pg, PairsPerPage, danceCount, suffix);
                }

                _log.LogAdd(
                    $"[ReportRenderer] BindAjsScoreList 完了: " +
                    $"kbn={kbnNo}/{kbnDspName}, rnd={rndName}, " +
                    $"totalEntries={totalEntries}, pages={pageCount}",
                    _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] BindAjsScoreList エラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>
        /// 得点一覧表の1ページ分データ行をセットする。
        /// 列構成: C01=背番号, C02=選手名(L苗字・P苗字), C03=合計点, C04=順位, C05=結果(UP), C06〜C12=種目得点(最大7)
        /// UP行（C05="UP"）は背景色を水色(#00FFFF)にする。
        /// </summary>
        private static void SetAjsScorePageRows(
            Report report,
            List<(string bibNo, string lLastName, string pLastName,
                  double totalScore, string rankDisplay, string upResult,
                  double[] danceScores)> playerRows,
            int pageIndex,
            int pairsPerPage,
            int danceCount,
            string suffix = "")
        {
            int startIdx = pageIndex * pairsPerPage;
            var blAll  = FastReport.BorderLines.All;
            var blNone = FastReport.BorderLines.None;
            var lightBlue = System.Drawing.Color.FromArgb(0x00, 0xFF, 0xFF); // #00FFFF

            for (int slot = 1; slot <= pairsPerPage; slot++)
            {
                string nn      = slot.ToString("D2");
                int    dataIdx = startIdx + slot - 1;
                bool   hasData = dataIdx < playerRows.Count;

                if (hasData)
                {
                    var (bibNo, lLast, pLast, total, rankDisp, upResult, dScores) = playerRows[dataIdx];
                    bool isUp = upResult == "UP";

                    // C01: 背番号
                    SetTextObjectDirect(report, $"DR_{nn}_C01{suffix}", bibNo, blAll);
                    // C02: 選手名（L苗字・P苗字 形式・AutoShrink）
                    string playerName = string.IsNullOrEmpty(pLast) ? lLast : $"{lLast}・{pLast}";
                    SetTextObjectDirectEx(report, $"DR_{nn}_C02{suffix}", playerName, blAll, autoShrink: true);
                    // C03: 合計点（小数3桁）
                    string totalStr = total == 0.0 ? "" : total.ToString("F3");
                    SetTextObjectDirect(report, $"DR_{nn}_C03{suffix}", totalStr, blAll);
                    // C04: 順位
                    SetTextObjectDirect(report, $"DR_{nn}_C04{suffix}", rankDisp, blAll);
                    // C05: 結果（UP or 空）
                    SetTextObjectDirect(report, $"DR_{nn}_C05{suffix}", upResult, blAll);

                    // UP行の背景色（C01〜C05+種目列すべて水色）
                    if (isUp)
                    {
                        for (int c = 1; c <= 5; c++)
                            SetTextObjectFill(report, $"DR_{nn}_C{c:D2}{suffix}", lightBlue);
                    }

                    // C06〜C12: 種目別得点（小数3桁、最大7種目）
                    for (int di = 0; di < 7; di++)
                    {
                        string cName = $"DR_{nn}_C{di + 6:D2}{suffix}";
                        bool hasDance = di < danceCount;
                        if (hasDance)
                        {
                            string scoreStr = dScores[di] == 0.0 ? "" : dScores[di].ToString("F3");
                            SetTextObjectDirect(report, cName, scoreStr, blAll);
                            if (isUp)
                                SetTextObjectFill(report, cName, lightBlue);
                        }
                        else
                        {
                            SetTextObjectDirect(report, cName, "", blNone);
                        }
                    }
                }
                else
                {
                    // 未使用行: 全列空文字・罫線なし
                    for (int c = 1; c <= 12; c++)
                        SetTextObjectDirect(report, $"DR_{nn}_C{c:D2}{suffix}", "", blNone);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // 得点一覧表（チェック法）バインド
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 得点一覧表（チェック法採点方式）バインド。
        ///
        /// ■ 列構成
        ///   C01=背番号、C02=選手名（L苗字・P苗字）、C03=合計点、C04=順位、C05=結果
        ///   C06〜 = 種目順×ジャッジ順のチェック列（最大65列＝5種目×13ジャッジ）
        ///
        /// ■ ヘッダー行
        ///   CH_HD_C{i:D2}: 種目記号（種目ごとにジャッジ数分を先頭セルに表示）
        ///   CH_HJ_C{i:D2}: ジャッジ記号（各列にジャッジ記号）
        ///
        /// ■ データ行
        ///   DR_{nn}_C{i:D2}: 素点 0→空、1→"*"
        ///
        /// ■ ページ分割
        ///   1ページ最大16組（行方向）。17組目以降は DuplicatePageInReportXml で2ページ目へ。
        ///   列方向: 1ページ最大65列（C06〜C70）、それ超える場合は次ページへ展開。
        ///   ただし通常5種目×13ジャッジ=65列が上限のため2ページで収まる想定。
        /// </summary>
        private void BindCheckScoreList(Report report, JsonNode? jobData,
            int pairsPerPage = 16, int maxJudgeColsPerPage = 65, int maxFrxCheckCols = 69)
        {
            int PairsPerPage        = pairsPerPage;
            int MaxJudgeColsPerPage = maxJudgeColsPerPage;
            int MaxFrxCheckCols     = maxFrxCheckCols;

            try
            {
                // ── job.Data から KbnNo / RndNo / DGrpNo を取得 ───────────────────
                string kbnNo  = "";
                string rndNo  = "";
                string dGrpNo = "";
                JObject? jd = null;

                if (jobData != null)
                {
                    jd     = JObject.Parse(jobData.ToJsonString());
                    kbnNo  = jd["区分番号"]?.ToString()  ?? jd["KbnNo"]?.ToString()  ?? "";
                    rndNo  = jd["ラウンド番号"]?.ToString() ?? jd["RndNo"]?.ToString()  ?? "";
                    dGrpNo = jd["DGrpNo"]?.ToString() ?? "";
                }

                _log.LogAdd($"[ReportRenderer] BindCheckScoreList 開始: KbnNo={kbnNo}, RndNo={rndNo}", _log.INFO);

                if (_dataManager.DS_Status == null || _dataManager.DA_Master == null)
                {
                    _log.LogAdd("[ReportRenderer] BindCheckScoreList: DS_Status/DA_Master が未受信", _log.WARNING);
                    return;
                }

                var dsJson = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                var daJson = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                // ── フロア一覧 ───────────────────────────────────────────────────
                var floors     = dsJson["DS_FLOORs"] as JArray;
                int floorCount = floors?.Count ?? 0;

                // ── 対象 DS_PRGRS を KbnNo / RndNo で検索 ────────────────────────
                JObject? prgrs    = null;
                string   flrCd    = "";
                string   prgNo    = "";
                string   prgSubNo = "";

                if (floors != null)
                {
                    foreach (var floor in floors.OfType<JObject>())
                    {
                        var prgrsList = floor["DS_PRGRSs"] as JArray;
                        if (prgrsList == null) continue;
                        foreach (var p in prgrsList.OfType<JObject>())
                        {
                            if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                            if (p["DS_RndNo"]?.ToString()  != rndNo)  continue;
                            if (!string.IsNullOrEmpty(dGrpNo) &&
                                p["DS_DGrpNo"]?.ToString() != dGrpNo) continue;
                            prgrs    = p;
                            flrCd    = floor["DS_FlrCd"]?.ToString()  ?? "";
                            prgNo    = p["DS_PrgNo"]?.ToString()     ?? "";
                            prgSubNo = p["DS_PrgSubNo"]?.ToString()  ?? "";
                            break;
                        }
                        if (prgrs != null) break;
                    }
                }

                if (prgrs == null)
                {
                    _log.LogAdd($"[ReportRenderer] BindCheckScoreList: 対象ラウンドが見つかりません KbnNo={kbnNo} RndNo={rndNo}", _log.WARNING);
                    return;
                }

                // ── DA_Master から区分・ラウンド情報を取得 ─────────────────────────
                var kbnList = daJson["DB_KUBUNs"] as JArray;
                JObject? kbnObj = kbnList?.OfType<JObject>()
                    .FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);

                string kbnCd      = kbnObj?["DB_KbnCd"]?.ToString() ?? "";
                string kbnDspName = kbnObj?["DB_KbnDsipName"]?.ToString()
                                    ?? kbnObj?["DB_KbnDispName"]?.ToString()
                                    ?? kbnObj?["DB_KbnName"]?.ToString() ?? "";

                var rndList  = kbnObj?["DC_ROUNDs"] as JArray;
                JObject? rndObj = rndList?.OfType<JObject>()
                    .FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);

                string rndName  = rndObj?["DC_RndName_J"]?.ToString() ?? rndNo;
                int    rndUpPln = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
                string scrMtd   = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";

                // ── 種目グループ・種目一覧 ──────────────────────────────────────────
                var dgList  = rndObj?["DD_DGRPs"] as JArray;
                int dgCount = dgList?.Count ?? 0;

                JObject? dgrpObj = null;
                if (dgList != null)
                {
                    if (!string.IsNullOrEmpty(dGrpNo))
                        dgrpObj = dgList.OfType<JObject>()
                            .FirstOrDefault(g => g["DD_DGrpNo"]?.ToString() == dGrpNo);
                    dgrpObj ??= dgList.OfType<JObject>().FirstOrDefault();
                }

                string dgrpName = dgrpObj?["DD_DGrpName"]?.ToString() ?? "";
                var dncList     = dgrpObj?["DE_DANCEs"] as JArray;
                var dances      = dncList?.OfType<JObject>()
                    .OrderBy(d => d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                    ?? new List<JObject>();

                int danceCount = dances.Count;
                string[] dsCodes = dances.Select(d => d["DE_DncCd"]?.ToString() ?? "").ToArray();
                string[] dcTypes = dances.Select(d => d["DE_DncSG"]?.ToString() ?? "").ToArray();

                // ── KubunName / PRGNO 構築 ──────────────────────────────────────────
                string kbnNoDisplay = int.TryParse(kbnNo, out int kbnNoInt)
                    ? kbnNoInt.ToString("D2") : kbnNo;
                var kubunParts = new System.Text.StringBuilder();
                kubunParts.Append(kbnNoDisplay);
                if (!string.IsNullOrEmpty(kbnCd))      kubunParts.Append(" ").Append(kbnCd);
                if (!string.IsNullOrEmpty(kbnDspName)) kubunParts.Append(" ").Append(kbnDspName);
                if (dgCount > 1 && !string.IsNullOrEmpty(dgrpName))
                    kubunParts.Append(" ").Append(dgrpName);
                if (floorCount > 1 && !string.IsNullOrEmpty(flrCd))
                    kubunParts.Append(" ").Append(flrCd).Append("フロア");
                string kubunName = kubunParts.ToString();

                string prgNoFormatted = int.TryParse(prgNo, out int prgNoInt)
                    ? prgNoInt.ToString("D3") : prgNo;
                string prgNoDisplay = string.IsNullOrEmpty(prgSubNo) || prgSubNo == "0" || prgSubNo == "1"
                    ? prgNoFormatted : $"{prgNoFormatted}-{prgSubNo}";

                // ── ヒート数・出場者数 ──────────────────────────────────────────────
                var dncArr = prgrs["DS_PRGDANCEs"] as JArray;
                var heatIdToNo = new Dictionary<string, int>();
                int maxHeatCount = 0;
                if (dncArr != null)
                {
                    foreach (var dnc in dncArr.OfType<JObject>())
                    {
                        var heatArr = dnc["DS_PRGHEATs"] as JArray;
                        if (heatArr == null) continue;
                        int cnt = 0;
                        foreach (var h in heatArr.OfType<JObject>())
                        {
                            string? hid = h["DS_HeatId"]?.ToString();
                            int     hno = h["DS_HeatNo"]?.ToObject<int>() ?? 0;
                            if (!string.IsNullOrEmpty(hid) && !heatIdToNo.ContainsKey(hid))
                                heatIdToNo[hid] = hno;
                            cnt++;
                        }
                        if (cnt > maxHeatCount) maxHeatCount = cnt;
                    }
                }
                int totalHeats   = heatIdToNo.Count > 0 ? heatIdToNo.Values.Max() : maxHeatCount;
                var assignments  = prgrs["PlayerAssignments"] as JArray;
                int totalEntries = assignments?.Count ?? 0;
                bool isShuffle   = prgrs["DS_PrgShuffle"]?.ToObject<bool>() ?? false;

                // ── 次ラウンドの PlayerAssignments を取得（UP判定用）────────────────
                // ラウンド番号の昇順定義: 010-020-030-040-050-090-100-200-400
                var nextRndAssignedPlayerNos = new HashSet<string>();
                if (rndList != null)
                {
                    var sortedRnds = rndList.OfType<JObject>()
                        .Select(r => new {
                            obj      = r,
                            rndNoStr = r["DC_RndNo"]?.ToString() ?? "",
                            rndNoInt = int.TryParse(r["DC_RndNo"]?.ToString(), out int ri) ? ri : int.MaxValue
                        })
                        .OrderBy(x => x.rndNoInt)
                        .ToList();

                    int curRndNoInt = int.TryParse(rndNo, out int crni) ? crni : int.MaxValue;
                    string? nextRndNoStr = sortedRnds.FirstOrDefault(x => x.rndNoInt > curRndNoInt)?.rndNoStr;

                    if (!string.IsNullOrEmpty(nextRndNoStr) && floors != null)
                    {
                        foreach (var floor in floors.OfType<JObject>())
                        {
                            var prgrsList = floor["DS_PRGRSs"] as JArray;
                            if (prgrsList == null) continue;
                            foreach (var p in prgrsList.OfType<JObject>())
                            {
                                if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                                if (p["DS_RndNo"]?.ToString()  != nextRndNoStr) continue;
                                if (!string.IsNullOrEmpty(dGrpNo) &&
                                    p["DS_DGrpNo"]?.ToString() != dGrpNo) continue;
                                var nextAssigns = p["PlayerAssignments"] as JArray;
                                if (nextAssigns != null)
                                {
                                    foreach (var pa in nextAssigns.OfType<JObject>())
                                    {
                                        string? no = pa["PlayerNo"]?.ToString();
                                        if (!string.IsNullOrEmpty(no))
                                            nextRndAssignedPlayerNos.Add(no);
                                    }
                                }
                                break;
                            }
                        }
                    }
                }

                // ── 選手名解決マップ（背番号 → L苗字, P苗字）──────────────────────
                int targetMasNoC = int.TryParse(kbnObj?["DB_KbnSenM"]?.ToString(), out int senMC) ? senMC : 1;
                var infoMapC     = BuildPlayerInfoMap(daJson, targetMasNoC);
                var playerNameMap = infoMapC.ToDictionary(
                    kv => kv.Key,
                    kv => (ExtractLastName(kv.Value.lName), ExtractLastName(kv.Value.pName)),
                    StringComparer.Ordinal);

                // ── DV_Result からジャッジ詳細チェックデータを解析 ─────────────────
                // 構造: 種目結果[].選手結果[].ジャッジ詳細結果[].素点
                // 種目順（種目記号）→ ジャッジ記号一覧 を確定
                // 背番号 → (総合得点, 総合順位表記, 種目×ジャッジの素点[danceIdx][judgeIdx])

                // まずジャッジ記号のマスターリストを種目ごとに確定する
                // 種目順は dances の順（DA_Master の DE_DncNo 順）に従う
                var judgesPerDance = new List<List<string>>();   // [danceIdx] = judgeSymbols[]
                for (int di = 0; di < danceCount; di++)
                    judgesPerDance.Add(new List<string>());

                // 背番号 → 種目ごとのジャッジ詳細: [danceIdx][judgeSymbol] = 素点(decimal)
                var checkScoreMap = new Dictionary<string, decimal[][]>();
                // 背番号 → 総合得点・順位
                var totalScoreMap = new Dictionary<string, (decimal totalScore, string rankDisplay)>();

                if (jd != null)
                {
                    var shomokuKekka = jd["種目結果"] as JArray;
                    var soGoKekka    = jd["総合結果"]  as JArray;

                    // 種目記号 → danceIndex マップ
                    var danceCodeToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int di = 0; di < danceCount; di++)
                        danceCodeToIdx[dsCodes[di]] = di;

                    if (shomokuKekka != null)
                    {
                        foreach (var shomoku in shomokuKekka.OfType<JObject>())
                        {
                            string dncCd = shomoku["種目記号"]?.ToString() ?? "";
                            if (!danceCodeToIdx.TryGetValue(dncCd, out int di)) continue;

                            var senshuKekka = shomoku["選手結果"] as JArray;
                            if (senshuKekka == null) continue;

                            foreach (var sk in senshuKekka.OfType<JObject>())
                            {
                                string bibNo = sk["背番号"]?.ToString() ?? "";
                                if (string.IsNullOrEmpty(bibNo)) continue;

                                var judgeDetails = sk["ジャッジ詳細結果"] as JArray;
                                if (judgeDetails == null) continue;

                                // ジャッジ記号をマスターに追加（初回のみ）
                                if (judgesPerDance[di].Count == 0)
                                {
                                    foreach (var jd2 in judgeDetails.OfType<JObject>())
                                    {
                                        string sym = jd2["ジャッジ記号"]?.ToString() ?? "";
                                        if (!string.IsNullOrEmpty(sym))
                                            judgesPerDance[di].Add(sym);
                                    }
                                }

                                // 素点を取得
                                if (!checkScoreMap.ContainsKey(bibNo))
                                {
                                    checkScoreMap[bibNo] = new decimal[danceCount][];
                                    for (int d2 = 0; d2 < danceCount; d2++)
                                        checkScoreMap[bibNo][d2] = Array.Empty<decimal>();
                                }

                                int jCount = judgeDetails.Count;
                                var rawScores = new decimal[jCount];
                                int ji = 0;
                                foreach (var jd3 in judgeDetails.OfType<JObject>())
                                {
                                    rawScores[ji++] = jd3["素点"]?.ToObject<decimal>() ?? 0m;
                                }
                                checkScoreMap[bibNo][di] = rawScores;

                                // ── [診断ログ] bib=20・種目C(di=1) の素点を詳細記録 ──
                                if (bibNo == "20" && di == 1)
                                {
                                    string judgeNames = string.Join(",", judgesPerDance[di]);
                                    string rawStr     = string.Join(",", rawScores.Select(s => s.ToString("F0")));
                                    _log.LogAdd($"[診断] checkScoreMap bib=20 種目C(di=1): judges=[{judgeNames}] rawScores=[{rawStr}]", _log.INFO);

                                    // 各ジャッジの素点トークンを個別に記録（型・生値）
                                    int ji2 = 0;
                                    foreach (var jd3 in judgeDetails.OfType<JObject>())
                                    {
                                        var tok = jd3["素点"];
                                        string sym = jd3["ジャッジ記号"]?.ToString() ?? $"[{ji2}]";
                                        _log.LogAdd($"[診断]   {sym}: token={tok} type={tok?.Type} converted={rawScores[ji2]}", _log.INFO);
                                        ji2++;
                                    }
                                }
                                // ── [診断ログ] ここまで ──
                            }
                        }
                    }

                    if (soGoKekka != null)
                    {
                        foreach (var sg in soGoKekka.OfType<JObject>())
                        {
                            string bibNo   = sg["背番号"]?.ToString() ?? "";
                            decimal total  = sg["総合得点"]?.ToObject<decimal>() ?? 0m;
                            string rankStr = sg["総合順位表記"]?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(bibNo))
                                totalScoreMap[bibNo] = (total, rankStr);
                        }
                    }
                }

                // ── ジャッジ列リストを構築（全種目を連結）──────────────────────────
                // checkCols[i] = (danceIdx, judgeIdx)
                //   danceIdx == -1 → 種目間セパレータ列（空白・罫線なし）
                var checkCols = new List<(int danceIdx, int judgeIdx)>();
                for (int di = 0; di < danceCount; di++)
                {
                    if (di > 0)
                        checkCols.Add((-1, -1));   // 種目間セパレータ
                    int jc = judgesPerDance[di].Count;
                    for (int ji = 0; ji < jc; ji++)
                        checkCols.Add((di, ji));
                }

                // ジャッジ列数のみカウント（セパレータ除く）
                int totalJudgeCols = checkCols.Count(c => c.danceIdx >= 0);
                int totalCheckCols = checkCols.Count;  // セパレータ込みの総列数

                // ── 出場者リストを背番号昇順で構築 ──────────────────────────────────
                var playerRows = new List<(
                    string bibNo,
                    string lLastName,
                    string pLastName,
                    decimal totalScore,
                    string rankDisplay,
                    string upResult,
                    decimal[][] checkScores)>();   // [danceIdx][judgeIdx]

                if (assignments != null)
                {
                    foreach (var pa in assignments.OfType<JObject>())
                    {
                        string playerNo = pa["PlayerNo"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(playerNo)) continue;

                        var (lLast, pLast) = playerNameMap.TryGetValue(playerNo, out var names)
                            ? names : ("", "");
                        string up = nextRndAssignedPlayerNos.Contains(playerNo) ? "UP" : "";

                        decimal total    = 0m;
                        string rankDisp  = "";
                        if (totalScoreMap.TryGetValue(playerNo, out var ts))
                            (total, rankDisp) = ts;

                        decimal[][] cScores = checkScoreMap.TryGetValue(playerNo, out var cs)
                            ? cs : new decimal[danceCount][];
                        for (int di = 0; di < danceCount; di++)
                            cScores[di] ??= Array.Empty<decimal>();

                        playerRows.Add((playerNo, lLast, pLast, total, rankDisp, up, cScores));
                    }
                }

                // 背番号数値昇順ソート
                playerRows.Sort((a, b) =>
                {
                    bool aOk = int.TryParse(a.bibNo, out int ai);
                    bool bOk = int.TryParse(b.bibNo, out int bi);
                    if (aOk && bOk) return ai.CompareTo(bi);
                    if (aOk) return -1;
                    if (bOk) return 1;
                    return string.Compare(a.bibNo, b.bibNo, StringComparison.Ordinal);
                });

                string upText = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

                // ── 列ページ分割: 種目の境界で区切る ────────────────────────────────
                // 種目の途中でページを切らないよう、MaxJudgeColsPerPage 以内に収まる
                // 最大の種目数でページを確定する。
                var colPageRanges = BuildColumnPageRanges(
                    judgesPerDance, checkCols, MaxJudgeColsPerPage);
                int colPagesCount = colPageRanges.Count;

                // ── 行ページ分割 ──────────────────────────────────────────────────────
                int rowPagesCount = Math.Max(1, (int)Math.Ceiling((double)playerRows.Count / PairsPerPage));

                // 総ページ数 = 行ページ × 列ページ
                int totalPageCount = rowPagesCount * colPagesCount;

                // ══════════════════════════════════════════════════════════════
                // PageHeader: 共通ヘッダー部分をセット
                // ══════════════════════════════════════════════════════════════
                SetTextObject(report, "Title",       "得点一覧表");
                SetTextObject(report, "SendTo",      "");
                SetTextObject(report, "PRGNO",       prgNoDisplay);
                SetTextObject(report, "KubunName",   kubunName);

                var roundObjs = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                for (int i = 1; i <= 7; i++)
                {
                    string objName = $"Round{i}";
                    if (i - 1 < roundObjs.Count)
                    {
                        var r     = roundObjs[i - 1];
                        string rn = r["DC_RndName_J"]?.ToString() ?? "";
                        bool isCur = r["DC_RndNo"]?.ToString() == rndNo;
                        SetTextObject(report, objName, rn);
                        SetTextObjectFill(report, objName,
                            isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                    }
                    else
                    {
                        SetTextObject(report, objName, "");
                        SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                }

                SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                SetTextObject(report, "UP",          upText);
                SetTextObject(report, "ScoreMethod", scrMtd);

                for (int i = 1; i <= 5; i++)
                {
                    bool hasDance = i <= danceCount;
                    SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                    SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                    var fillColor = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                    SetTextObjectFill(report, $"DS{i}", fillColor);
                    SetTextObjectFill(report, $"DC{i}", fillColor);
                }

                // ── ページ複製（複数ページの場合）──────────────────────────────────
                if (totalPageCount > 1)
                {
                    string origXml = report.SaveToString();
                    string? newXml = DuplicatePageInReportXml(origXml, totalPageCount);
                    if (newXml != null)
                    {
                        report.LoadFromString(newXml);
                        // 再ロード後にヘッダーを再設定
                        SetTextObject(report, "Title",       "得点一覧表");
                        SetTextObject(report, "SendTo",      "");
                        SetTextObject(report, "PRGNO",       prgNoDisplay);
                        SetTextObject(report, "KubunName",   kubunName);
                        var roundObjsR = rndList?.OfType<JObject>().ToList() ?? new List<JObject>();
                        for (int i = 1; i <= 7; i++)
                        {
                            if (i - 1 < roundObjsR.Count)
                            {
                                var r2    = roundObjsR[i - 1];
                                string rn = r2["DC_RndName_J"]?.ToString() ?? "";
                                bool isCur = r2["DC_RndNo"]?.ToString() == rndNo;
                                SetTextObject(report, $"Round{i}", rn);
                                SetTextObjectFill(report, $"Round{i}",
                                    isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                            }
                            else
                            {
                                SetTextObject(report, $"Round{i}", "");
                                SetTextObjectFill(report, $"Round{i}", System.Drawing.Color.Transparent);
                            }
                        }
                        SetTextObject(report, "TotalHeat",   $"{totalHeats} Heat");
                        SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                        SetTextObject(report, "UP",          upText);
                        SetTextObject(report, "ScoreMethod", scrMtd);
                        for (int i = 1; i <= 5; i++)
                        {
                            bool hasDance = i <= danceCount;
                            SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                            SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                            var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                            SetTextObjectFill(report, $"DS{i}", fc);
                            SetTextObjectFill(report, $"DC{i}", fc);
                        }
                    }
                }

                // ── [診断ログ] playerRows・checkScoreMap の状態を記録 ──
                {
                    // bib=20 の行インデックスと種目C(di=1)・Eジャッジ(judgeIdx=4)の素点を確認
                    int bib20RowIdx = playerRows.FindIndex(r => r.bibNo == "20");
                    _log.LogAdd($"[診断] playerRows 件数={playerRows.Count}, bib=20のインデックス={bib20RowIdx}", _log.INFO);

                    if (bib20RowIdx >= 0)
                    {
                        var r20 = playerRows[bib20RowIdx];
                        // checkCols の中で danceIdx=1(種目C) の各 judgeIdx を表示
                        var cCols = checkCols
                            .Select((c, idx) => (c.danceIdx, c.judgeIdx, checkColIdx: idx))
                            .Where(c => c.danceIdx == 1)
                            .ToList();
                        var colInfo = string.Join(", ", cCols.Select(c =>
                        {
                            decimal sc = 0m;
                            if (c.danceIdx < r20.checkScores.Length &&
                                r20.checkScores[c.danceIdx] != null &&
                                c.judgeIdx < r20.checkScores[c.danceIdx].Length)
                                sc = r20.checkScores[c.danceIdx][c.judgeIdx];
                            string jSym = c.judgeIdx < judgesPerDance[c.danceIdx].Count
                                ? judgesPerDance[c.danceIdx][c.judgeIdx] : "?";
                            int frxSlot = c.checkColIdx + 6;  // frxCol
                            return $"J={jSym}(judgeIdx={c.judgeIdx},frxC{frxSlot:D2},score={sc})";
                        }));
                        _log.LogAdd($"[診断] bib=20 種目C列: {colInfo}", _log.INFO);

                        // 種目C の rawScores 生配列
                        string raw20C = r20.checkScores.Length > 1 && r20.checkScores[1] != null
                            ? string.Join(",", r20.checkScores[1].Select(s => s.ToString("F0")))
                            : "(none)";
                        _log.LogAdd($"[診断] bib=20 checkScores[1](種目C) raw=[{raw20C}]", _log.INFO);
                    }

                    // judgesPerDance[1] の内容
                    string jPD1 = judgesPerDance.Count > 1
                        ? string.Join(",", judgesPerDance[1]) : "(none)";
                    _log.LogAdd($"[診断] judgesPerDance[1](種目C)=[{jPD1}]", _log.INFO);
                }
                // ── [診断ログ] ここまで ──

                // ── 各ページのデータ行・列ヘッダーをセット ──────────────────────────
                // 列ページ分割: ジャッジ列 MaxJudgeColsPerPage 本を1ページの基準にする。
                // checkCols にはセパレータ(-1,-1)が含まれるので、ページ境界はジャッジ列数で計算。
                for (int rowPg = 0; rowPg < rowPagesCount; rowPg++)
                {
                    for (int colPg = 0; colPg < colPagesCount; colPg++)
                    {
                        int pageIdx = rowPg * colPagesCount + colPg;
                        string suffix = pageIdx == 0 ? "" : $"_P{pageIdx + 1:D2}";

                        // 種目境界で区切った列範囲（checkCols インデックス）
                        var (colStart, colEnd) = colPageRanges[colPg];

                        // 列ヘッダー（種目記号・ジャッジ記号）を設定
                        SetCheckColumnHeaders(report, checkCols, judgesPerDance, dsCodes,
                            colStart, colEnd, MaxFrxCheckCols, suffix);

                        // データ行を設定
                        SetCheckScorePageRows(report, playerRows, checkCols,
                            rowPg, PairsPerPage, colStart, colEnd, MaxFrxCheckCols, suffix);
                    }
                }

                _log.LogAdd(
                    $"[ReportRenderer] BindCheckScoreList 完了: " +
                    $"kbn={kbnNo}/{kbnDspName}, rnd={rndName}, " +
                    $"totalEntries={totalEntries}, checkCols={totalCheckCols}, pages={totalPageCount}",
                    _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] BindCheckScoreList エラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>
        /// 得点一覧表（チェック法）の列ヘッダー行をセットする。
        /// RowH1（種目記号行）: 種目の先頭ジャッジ列に種目記号を表示し ColSpan をジャッジ数に設定。
        ///                      同一種目内の残列は非表示（ColSpan消費）。セパレータ列はブランク。
        /// RowH2（ジャッジ記号行）: 各ジャッジ記号。セパレータ列はブランク。
        /// 罫線: 種目先頭でLeft、種目末尾でRight（セパレータの左列が末尾）、Top/Bottom常時あり。
        ///       セパレータ列・未使用列は罫線なし。
        /// </summary>
        private static void SetCheckColumnHeaders(
            Report report,
            List<(int danceIdx, int judgeIdx)> checkCols,
            List<List<string>> judgesPerDance,
            string[] dsCodes,
            int colStart, int colEnd,
            int maxFrxCheckCols,
            string suffix)
        {
            var blNone = FastReport.BorderLines.None;
            var blTop  = FastReport.BorderLines.Top;
            var blBot  = FastReport.BorderLines.Bottom;
            var blLeft = FastReport.BorderLines.Left;
            var blRight= FastReport.BorderLines.Right;
            var blTB   = blTop | blBot;
            var blLTB  = blLeft | blTop | blBot;
            var blRTB  = blRight | blTop | blBot;
            var blAll  = FastReport.BorderLines.All;

            for (int slot = 0; slot < maxFrxCheckCols; slot++)
            {
                int colIdx = colStart + slot;
                int frxCol = slot + 6;   // C06〜
                string colStr = frxCol.ToString("D2");

                string hdCellName = $"CHCell_H1_{colStr}{suffix}";   // TableCell（ColSpan設定用）
                string hdName     = $"CH_HD_C{colStr}{suffix}";      // TextObject（種目記号）
                string hjName     = $"CH_HJ_C{colStr}{suffix}";      // TextObject（ジャッジ記号）

                if (colIdx >= colEnd || colIdx >= checkCols.Count)
                {
                    // 未使用列
                    SetTextObjectDirect(report, hdName, "", blNone);
                    SetTextObjectDirect(report, hjName, "", blNone);
                    continue;
                }

                var (danceIdx, judgeIdx) = checkCols[colIdx];

                if (danceIdx < 0)
                {
                    // セパレータ列
                    SetTextObjectDirect(report, hdName, "", blNone);
                    SetTextObjectDirect(report, hjName, "", blNone);
                    continue;
                }

                // このジャッジ列の罫線を決定
                bool isFirst = judgeIdx == 0;  // 種目先頭
                // 種目末尾判定: 次の checkCols がセパレータか範囲外か
                int nextIdx = colIdx + 1;
                bool isLast = nextIdx >= colEnd || nextIdx >= checkCols.Count
                    || checkCols[nextIdx].danceIdx < 0
                    || checkCols[nextIdx].danceIdx != danceIdx;

                FastReport.BorderLines hdBorder = blTB;
                if (isFirst && isLast) hdBorder = blAll;
                else if (isFirst)      hdBorder = blLTB;
                else if (isLast)       hdBorder = blRTB;

                // RowH1（種目記号）: 種目先頭列にのみ種目記号を表示
                if (isFirst)
                {
                    // ColSpan をジャッジ数に設定（先頭列のTableCellを操作）
                    // ただし FRX の残り列数（maxFrxCheckCols - slot）を超えないようクランプする
                    int judgeCount = judgesPerDance[danceIdx].Count;
                    int remainingFrxCols = maxFrxCheckCols - slot;   // このスロット以降に残るFRX列数
                    int colSpanValue = Math.Min(judgeCount, remainingFrxCols);
                    var hdCell = report.FindObject(hdCellName) as FastReport.Table.TableCell;
                    if (hdCell != null)
                        hdCell.ColSpan = colSpanValue;

                    // ColSpan で複数列を1セルが覆うため、Left/Right 両方の罫線が必要
                    SetTextObjectDirect(report, hdName, dsCodes[danceIdx], blAll);
                }
                else
                {
                    // 先頭列以外: ColSpanで消費されるため空・罫線なし（FastReportが自動処理）
                    SetTextObjectDirect(report, hdName, "", blNone);
                }

                // RowH2（ジャッジ記号）
                string judgeSymbol = judgeIdx < judgesPerDance[danceIdx].Count
                    ? judgesPerDance[danceIdx][judgeIdx] : "";
                FastReport.BorderLines hjBorder = blTB;
                if (isFirst && isLast) hjBorder = blAll;
                else if (isFirst)      hjBorder = blLTB;
                else if (isLast)       hjBorder = blRTB;
                SetTextObjectDirect(report, hjName, judgeSymbol, hjBorder);
            }
        }

        /// <summary>
        /// 得点一覧表（チェック法）の1ページ分データ行をセットする。
        /// 素点が 0 のときブランク、1 のとき "*"。
        /// UP行は背景色を水色(#00FFFF)にする。
        /// 罫線: 種目先頭でLeft・種目末尾でRight・Top/Bottom常時。セパレータ・未使用はなし。
        /// </summary>
        private static void SetCheckScorePageRows(
            Report report,
            List<(string bibNo, string lLastName, string pLastName,
                  decimal totalScore, string rankDisplay, string upResult,
                  decimal[][] checkScores)> playerRows,
            List<(int danceIdx, int judgeIdx)> checkCols,
            int rowPageIndex,
            int pairsPerPage,
            int colStart, int colEnd,
            int maxFrxCheckCols,
            string suffix)
        {
            int startIdx  = rowPageIndex * pairsPerPage;
            var blNone    = FastReport.BorderLines.None;
            var blTop     = FastReport.BorderLines.Top;
            var blBot     = FastReport.BorderLines.Bottom;
            var blLeft    = FastReport.BorderLines.Left;
            var blRight   = FastReport.BorderLines.Right;
            var blTB      = blTop | blBot;
            var blLTB     = blLeft | blTop | blBot;
            var blRTB     = blRight | blTop | blBot;
            var blAll     = FastReport.BorderLines.All;
            var lightBlue = System.Drawing.Color.FromArgb(0x00, 0xFF, 0xFF);

            for (int slot = 1; slot <= pairsPerPage; slot++)
            {
                string nn      = slot.ToString("D2");
                int    dataIdx = startIdx + slot - 1;
                bool   hasData = dataIdx < playerRows.Count;

                if (hasData)
                {
                    var (bibNo, lLast, pLast, total, rankDisp, upResult, cScores) = playerRows[dataIdx];
                    bool isUp = upResult == "UP";

                    // 固定列（C01〜C05）
                    SetTextObjectDirect(report, $"DR_{nn}_C01{suffix}", bibNo, blAll);
                    string playerName = string.IsNullOrEmpty(pLast) ? lLast : $"{lLast}・{pLast}";
                    SetTextObjectDirectEx(report, $"DR_{nn}_C02{suffix}", playerName, blAll, autoShrink: true);
                    string totalStr = total == 0m ? "" : total.ToString("F0");
                    SetTextObjectDirect(report, $"DR_{nn}_C03{suffix}", totalStr, blAll);
                    SetTextObjectDirect(report, $"DR_{nn}_C04{suffix}", rankDisp, blAll);
                    SetTextObjectDirect(report, $"DR_{nn}_C05{suffix}", upResult, blAll);

                    if (isUp)
                    {
                        for (int c = 1; c <= 5; c++)
                            SetTextObjectFill(report, $"DR_{nn}_C{c:D2}{suffix}", lightBlue);
                    }

                    // チェック列（C06〜）
                    for (int slotJ = 0; slotJ < maxFrxCheckCols; slotJ++)
                    {
                        int colIdx = colStart + slotJ;
                        int frxCol = slotJ + 6;
                        string cName = $"DR_{nn}_C{frxCol:D2}{suffix}";

                        if (colIdx >= colEnd || colIdx >= checkCols.Count)
                        {
                            SetTextObjectDirect(report, cName, "", blNone);
                            continue;
                        }

                        var (danceIdx, judgeIdx) = checkCols[colIdx];

                        if (danceIdx < 0)
                        {
                            // セパレータ列: 空・罫線なし
                            SetTextObjectDirect(report, cName, "", blNone);
                            continue;
                        }

                        // 罫線判定
                        bool isFirst = judgeIdx == 0;
                        int  nextIdx = colIdx + 1;
                        bool isLast  = nextIdx >= colEnd || nextIdx >= checkCols.Count
                            || checkCols[nextIdx].danceIdx < 0
                            || checkCols[nextIdx].danceIdx != danceIdx;

                        FastReport.BorderLines border = blTB;
                        if (isFirst && isLast) border = blAll;
                        else if (isFirst)      border = blLTB;
                        else if (isLast)       border = blRTB;

                        // 素点: 0 → ブランク、1 → "*"
                        decimal rawScore = 0m;
                        if (danceIdx < cScores.Length && cScores[danceIdx] != null &&
                            judgeIdx < cScores[danceIdx].Length)
                            rawScore = cScores[danceIdx][judgeIdx];

                        string checkMark = rawScore == 0m ? "" : "*";
                        SetTextObjectDirect(report, cName, checkMark, border);
                        if (isUp)
                            SetTextObjectFill(report, cName, lightBlue);
                    }
                }
                else
                {
                    // 未使用行: 全列空文字・罫線なし
                    for (int c = 1; c <= 5; c++)
                        SetTextObjectDirect(report, $"DR_{nn}_C{c:D2}{suffix}", "", blNone);
                    for (int slotJ = 0; slotJ < maxFrxCheckCols; slotJ++)
                    {
                        int frxCol = slotJ + 6;
                        SetTextObjectDirect(report, $"DR_{nn}_C{frxCol:D2}{suffix}", "", blNone);
                    }
                }
            }
        }

        /// <summary>
        /// checkCols リスト内で、judgeOrdinal 番目（0-based）のジャッジ列（danceIdx &gt;= 0）に
        /// 対応する checkCols のインデックスを返す。
        /// judgeOrdinal が総ジャッジ列数以上の場合は checkCols.Count を返す。
        /// </summary>
        private static int FindCheckColIndexByJudgeOrdinal(
            List<(int danceIdx, int judgeIdx)> checkCols, int judgeOrdinal)
        {
            int found = 0;
            for (int i = 0; i < checkCols.Count; i++)
            {
                if (checkCols[i].danceIdx < 0) continue;  // セパレータをスキップ
                if (found == judgeOrdinal) return i;
                found++;
            }
            return checkCols.Count;
        }

        /// <summary>
        /// 決勝入賞者名簿（縦向き・決勝入賞者名簿_縦.frx）バインド。
        ///
        /// ■ データソース
        ///   job.Data は DV_Result JSON（区分番号・ラウンド番号・採点方式ID・総合結果 等）。
        ///   DA_Master キャッシュから選手名（L/P）を引く。
        ///
        /// ■ 並び順
        ///   DV_Result.総合結果 を 総合順位番号 昇順で表示。
        ///
        /// ■ 得点表示条件
        ///   採点方式ID が AJS を含む（大文字小文字不問）場合のみ 総合得点 を表示。
        ///   それ以外はブランク（タイトル行 DH_C05「得点」もブランクにする）。
        ///
        /// ■ frx TextObject 命名規則
        ///   ヘッダー: DH_C00=順位, DH_C01=背番号, DH_C02=氏名, DH_C03=フリガナ, DH_C04=所属, DH_C05=得点
        ///   データ行: DL_{nn}_C00=順位(RowSpan=2), DL_{nn}_C01=背番号(RowSpan=2),
        ///             DL_{nn}_C02=L氏名, DL_{nn}_C03=Lフリガナ, DL_{nn}_C04=L所属,
        ///             DL_{nn}_C05=得点(RowSpan=2)
        ///             DP_{nn}_C02=P氏名, DP_{nn}_C03=Pフリガナ, DP_{nn}_C04=P所属
        ///
        /// ■ 1ページ最大12組（PairsPerPage=12）
        /// </summary>
        private void BindFinalAwardList(Report report, JsonNode? jobData)
        {
            const int PairsPerPage = 12;

            try
            {
                // ── job.Data（DV_Result JSON）を解析 ──────────────────────────
                JObject? jd = null;
                if (jobData != null)
                    jd = JObject.Parse(jobData.ToJsonString());

                string kbnNo       = jd?["区分番号"]?.ToString()    ?? jd?["KbnNo"]?.ToString()  ?? "";
                string rndNo       = jd?["ラウンド番号"]?.ToString() ?? jd?["RndNo"]?.ToString()  ?? "";
                string dGrpNo      = jd?["DGrpNo"]?.ToString()     ?? "";
                string kbnName     = jd?["区分名"]?.ToString()      ?? "";
                string rndName     = jd?["ラウンド名"]?.ToString()   ?? "";
                string scrMtdId    = jd?["採点方式ID"]?.ToString()   ?? "";
                string scrMtdName  = jd?["採点方式名"]?.ToString()   ?? "";

                _log.LogAdd($"[ReportRenderer] BindFinalAwardList 開始: KbnNo={kbnNo}, RndNo={rndNo}, ScrMtdId={scrMtdId}", _log.INFO);

                // ── 採点方式: AJS系かどうか判定 ───────────────────────────────
                bool isAjs = scrMtdId.IndexOf("AJS", StringComparison.OrdinalIgnoreCase) >= 0;

                // ── DV_Result.総合結果 を順位番号昇順で取得 ───────────────────
                var totalResults = (jd?["総合結果"] as JArray)?.OfType<JObject>()
                    .OrderBy(r => r["総合順位番号"]?.ToObject<int>() ?? int.MaxValue)
                    .ToList() ?? new List<JObject>();

                int totalEntries = totalResults.Count;

                // ── DA_Master から選手名マップ（背番号 → master）を構築 ──────
                // masterMap は kubun 取得後に初期化（DB_KbnSenM 参照のため宣言のみ）
                var masterMap = new Dictionary<string, JObject>(StringComparer.Ordinal);

                // ── DS_Status から共通ヘッダー情報を補完 ──────────────────────
                string kbnCd           = "";
                string prgNoDisplay    = "";
                string upText          = "";
                string scrMtd          = scrMtdName;
                var    rndList         = new JArray();
                int    danceCount      = 0;
                string[] dsCodes       = new string[5];
                string[] dcTypes       = new string[5];
                string kubunNameDisplay = "";
                int    totalHeats      = 0;
                string kbnDspName      = kbnName;

                if (_dataManager.DS_Status != null && _dataManager.DA_Master != null)
                {
                    var dsJson     = JObject.Parse(_dataManager.DS_Status.ToJsonString());
                    var daJson2    = JObject.Parse(_dataManager.DA_Master.ToJsonString());

                    var floors     = dsJson["DS_FLOORs"] as JArray;
                    int floorCount = floors?.Count ?? 0;

                    JObject? prgrs    = null;
                    string   flrCd    = "";
                    string   prgNo    = "";
                    string   prgSubNo = "";

                    if (floors != null)
                    {
                        foreach (var floor in floors.OfType<JObject>())
                        {
                            var prgrsList = floor["DS_PRGRSs"] as JArray;
                            if (prgrsList == null) continue;
                            foreach (var p in prgrsList.OfType<JObject>())
                            {
                                if (p["DS_KbnNo"]?.ToString() != kbnNo) continue;
                                if (p["DS_RndNo"]?.ToString()  != rndNo)  continue;
                                if (!string.IsNullOrEmpty(dGrpNo) && p["DS_DGrpNo"]?.ToString() != dGrpNo) continue;
                                prgrs    = p;
                                flrCd    = floor["DS_FlrCd"]?.ToString() ?? "";
                                prgNo    = p["DS_PrgNo"]?.ToString()    ?? "";
                                prgSubNo = p["DS_PrgSubNo"]?.ToString() ?? "";
                                break;
                            }
                            if (prgrs != null) break;
                        }
                    }

                    var kubuns  = daJson2["DB_KUBUNs"] as JArray;
                    JObject? kubun = kubuns?.OfType<JObject>().FirstOrDefault(k => k["DB_KbnNo"]?.ToString() == kbnNo);
                    kbnCd      = kubun?["DB_KbnCd"]?.ToString() ?? "";
                    kbnDspName = kubun?["DB_KbnDsipName"]?.ToString()
                              ?? kubun?["DB_KbnDispName"]?.ToString()
                              ?? kubun?["DB_KbnName"]?.ToString()
                              ?? kbnName;

                    // DB_KbnSenM で指定されたマスタ番号を使って masterMap を構築
                    int targetMasNoAW = int.TryParse(kubun?["DB_KbnSenM"]?.ToString(), out int senMAW) ? senMAW : 1;
                    masterMap = BuildMasterMap(daJson2, targetMasNoAW);

                    rndList    = (kubun?["DC_ROUNDs"] as JArray) ?? new JArray();
                    JObject? rndObj = rndList.OfType<JObject>().FirstOrDefault(r => r["DC_RndNo"]?.ToString() == rndNo);
                    if (string.IsNullOrEmpty(scrMtd)) scrMtd = rndObj?["DC_RndScrMtd"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(rndName)) rndName = rndObj?["DC_RndName_J"]?.ToString() ?? rndNo;

                    int rndUpPln  = rndObj?["DC_RndUpPln"]?.ToObject<int>() ?? 0;
                    bool isShuffle = prgrs?["DS_PrgShuffle"]?.ToObject<bool>() ?? false;
                    upText = $"UP数 {rndUpPln} 組" + (isShuffle ? " シャッフル" : "");

                    var dgrps   = rndObj?["DD_DGRPs"] as JArray;
                    JObject? dgrp = dgrps?.OfType<JObject>()
                        .FirstOrDefault(g => string.IsNullOrEmpty(dGrpNo) || g["DD_DGrpNo"]?.ToString() == dGrpNo);
                    var dances  = (dgrp?["DE_DANCEs"] as JArray)?.OfType<JObject>()
                        .OrderBy(d => d["DE_DncNo"]?.ToObject<int>() ?? 0).ToList()
                        ?? new List<JObject>();
                    danceCount = Math.Min(dances.Count, 5);
                    dsCodes    = dances.Take(5).Select(d => d["DE_DncCd"]?.ToString() ?? "").ToArray();
                    dcTypes    = dances.Take(5).Select(d => d["DE_DncSG"]?.ToString() ?? "").ToArray();

                    string dgName = ((dgrps?.Count ?? 0) > 1 ? dgrp?["DD_DGrpName"]?.ToString() : "") ?? "";

                    int kbnNoInt2 = int.TryParse(kbnNo, out int kni2) ? kni2 : 0;
                    kubunNameDisplay = $"{kbnNoInt2:D2}";
                    if (!string.IsNullOrEmpty(kbnCd))      kubunNameDisplay += $" {kbnCd}";
                    if (!string.IsNullOrEmpty(kbnDspName)) kubunNameDisplay += $" {kbnDspName}";
                    if (!string.IsNullOrEmpty(dgName))     kubunNameDisplay += $" {dgName}";
                    if (floorCount > 1 && !string.IsNullOrEmpty(flrCd))
                        kubunNameDisplay += $" {flrCd}フロア";

                    prgNoDisplay = int.TryParse(prgNo, out int pn) ? pn.ToString("D3") : prgNo;
                    if (!string.IsNullOrEmpty(prgSubNo) && prgSubNo != "0" && prgSubNo != "1")
                        prgNoDisplay += $"-{prgSubNo}";

                    totalHeats = (prgrs?["DS_PRGDANCEs"] as JArray)?.OfType<JObject>()
                        .SelectMany(d => (d["DS_PRGHEATs"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                        .Select(h => h["DS_HeatNo"]?.ToObject<int>() ?? 0)
                        .DefaultIfEmpty(0).Max() ?? 0;
                }
                else
                {
                    // DS_Status/DA_Master がない場合は DV_Result の情報だけで最低限表示
                    int kbnNoInt3 = int.TryParse(kbnNo, out int kni3) ? kni3 : 0;
                    kubunNameDisplay = string.IsNullOrEmpty(kbnDspName)
                        ? $"{kbnNoInt3:D2}" : $"{kbnNoInt3:D2} {kbnDspName}";
                    scrMtd = scrMtdName;
                }

                // ── PageHeader: 共通ヘッダー ──────────────────────────────────
                SetTextObject(report, "Title",       "決勝入賞者名簿");
                SetTextObject(report, "SendTo",      "【　単票　　　】");
                SetTextObject(report, "PRGNO",       prgNoDisplay);
                SetTextObject(report, "KubunName",   kubunNameDisplay);

                var roundObjs = rndList.OfType<JObject>().ToList();
                for (int i = 1; i <= 7; i++)
                {
                    string objName = $"Round{i}";
                    if (i - 1 < roundObjs.Count)
                    {
                        var r      = roundObjs[i - 1];
                        string rn  = r["DC_RndName_J"]?.ToString() ?? "";
                        bool isCur = r["DC_RndNo"]?.ToString() == rndNo;
                        SetTextObject(report, objName, rn);
                        SetTextObjectFill(report, objName,
                            isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                    }
                    else
                    {
                        SetTextObject(report, objName, "");
                        SetTextObjectFill(report, objName, System.Drawing.Color.Transparent);
                    }
                }

                SetTextObject(report, "TotalHeat",   totalHeats > 0 ? $"{totalHeats} Heat" : "");
                SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                SetTextObject(report, "UP",          upText);
                SetTextObject(report, "ScoreMethod", scrMtd);

                for (int i = 1; i <= 5; i++)
                {
                    bool hasDance = i <= danceCount;
                    SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                    SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                    var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                    SetTextObjectFill(report, $"DS{i}", fc);
                    SetTextObjectFill(report, $"DC{i}", fc);
                }

                // 列ヘッダー: 得点列（AJS以外はブランク・罫線も消す）
                SetTextObjectDirect(report, "DH_C05", isAjs ? "得点" : "",
                    isAjs ? FastReport.BorderLines.All : FastReport.BorderLines.None);

                // ── 選手行リスト構築（順位番号昇順） ─────────────────────────
                var playerRows = new List<(string rankDisp, string bibNo,
                    string lName, string lKana, string lCtry,
                    string pName, string pKana, string pCtry,
                    string scoreText)>();

                foreach (var result in totalResults)
                {
                    string bibNo    = result["背番号"]?.ToString() ?? "";
                    string rankDisp = result["総合順位表記"]?.ToString() ?? "";
                    decimal score   = result["総合得点"]?.ToObject<decimal>() ?? 0m;
                    string scoreText = isAjs && score != 0m ? score.ToString("F2") : "";

                    masterMap.TryGetValue(bibNo, out var master);
                    string lName = master?["DM_LDispName"]?.ToString() ?? master?["DM_LName"]?.ToString() ?? "";
                    string lKana = master?["DM_LKana"]?.ToString() ?? "";
                    string lCtry = master?["DM_LCtry"]?.ToString() ?? master?["DM_Ctry"]?.ToString() ?? "";
                    string pName = master?["DM_PDispName"]?.ToString() ?? master?["DM_PName"]?.ToString() ?? "";
                    string pKana = master?["DM_PKana"]?.ToString() ?? "";
                    string pCtry = master?["DM_PCtry"]?.ToString() ?? master?["DM_Ctry"]?.ToString() ?? "";

                    playerRows.Add((rankDisp, bibNo, lName, lKana, lCtry, pName, pKana, pCtry, scoreText));
                }

                // ── 複数ページ対応 ──────────────────────────────────────────
                int pageCount = Math.Max(1, (int)Math.Ceiling((double)playerRows.Count / PairsPerPage));

                if (pageCount > 1)
                {
                    string origXml = report.SaveToString();
                    string? newXml = DuplicatePageInReportXml(origXml, pageCount);
                    if (newXml != null)
                    {
                        report.LoadFromString(newXml);
                        // 再ロード後にヘッダーを再セット
                        SetTextObject(report, "Title",       "決勝入賞者名簿");
                        SetTextObject(report, "SendTo",      "【　単票　　　】");
                        SetTextObject(report, "PRGNO",       prgNoDisplay);
                        SetTextObject(report, "KubunName",   kubunNameDisplay);
                        var roundObjsR = rndList.OfType<JObject>().ToList();
                        for (int i = 1; i <= 7; i++)
                        {
                            if (i - 1 < roundObjsR.Count)
                            {
                                var r2    = roundObjsR[i - 1];
                                string rn = r2["DC_RndName_J"]?.ToString() ?? "";
                                bool isCur = r2["DC_RndNo"]?.ToString() == rndNo;
                                SetTextObject(report, $"Round{i}", rn);
                                SetTextObjectFill(report, $"Round{i}",
                                    isCur ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent);
                            }
                            else
                            {
                                SetTextObject(report, $"Round{i}", "");
                                SetTextObjectFill(report, $"Round{i}", System.Drawing.Color.Transparent);
                            }
                        }
                        SetTextObject(report, "TotalHeat",   totalHeats > 0 ? $"{totalHeats} Heat" : "");
                        SetTextObject(report, "TotalComp",   $"出場　{totalEntries}組");
                        SetTextObject(report, "UP",          upText);
                        SetTextObject(report, "ScoreMethod", scrMtd);
                        for (int i = 1; i <= 5; i++)
                        {
                            bool hasDance = i <= danceCount;
                            SetTextObject(report, $"DS{i}", hasDance ? dsCodes[i - 1] : "");
                            SetTextObject(report, $"DC{i}", hasDance ? dcTypes[i - 1] : "");
                            var fc = hasDance ? System.Drawing.Color.DarkOrange : System.Drawing.Color.Transparent;
                            SetTextObjectFill(report, $"DS{i}", fc);
                            SetTextObjectFill(report, $"DC{i}", fc);
                        }
                        SetTextObjectDirect(report, "DH_C05", isAjs ? "得点" : "",
                            isAjs ? FastReport.BorderLines.All : FastReport.BorderLines.None);
                    }
                }

                // ── 各ページのデータ行をセット ─────────────────────────────
                for (int pg = 0; pg < pageCount; pg++)
                {
                    string suffix = pg == 0 ? "" : $"_P{pg + 1:D2}";
                    SetFinalAwardPageRows(report, playerRows, pg, PairsPerPage, suffix);
                }

                _log.LogAdd(
                    $"[ReportRenderer] BindFinalAwardList 完了: " +
                    $"kbn={kbnNo}/{kbnDspName}, rnd={rndName}, " +
                    $"totalEntries={totalEntries}, isAjs={isAjs}",
                    _log.INFO);
            }
            catch (Exception ex)
            {
                _log.LogAdd($"[ReportRenderer] BindFinalAwardList エラー: {ex.Message}", _log.ERR);
            }
        }

        /// <summary>
        /// 決勝入賞者名簿の1ページ分データ行をセットする。
        /// C00=順位(RowSpan=2) / C01=背番号(RowSpan=2) / C02=L氏名 / C03=Lフリガナ / C04=L所属 / C05=得点(RowSpan=2)
        /// P行: C02=P氏名 / C03=Pフリガナ / C04=P所属
        /// </summary>
        private static void SetFinalAwardPageRows(
            Report report,
            List<(string rankDisp, string bibNo,
                  string lName, string lKana, string lCtry,
                  string pName, string pKana, string pCtry,
                  string scoreText)> playerRows,
            int pageIndex,
            int pairsPerPage,
            string suffix = "")
        {
            int startIdx = pageIndex * pairsPerPage;
            var blNone = FastReport.BorderLines.None;
            var blAll  = FastReport.BorderLines.All;
            var blLrt  = FastReport.BorderLines.Left | FastReport.BorderLines.Right | FastReport.BorderLines.Top;
            var blLrb  = FastReport.BorderLines.Left | FastReport.BorderLines.Right | FastReport.BorderLines.Bottom;

            for (int slot = 1; slot <= pairsPerPage; slot++)
            {
                string nn      = slot.ToString("D2");
                int    dataIdx = startIdx + slot - 1;
                bool   hasData = dataIdx < playerRows.Count;

                if (hasData)
                {
                    var (rankDisp, bibNo, lName, lKana, lCtry, pName, pKana, pCtry, scoreText) = playerRows[dataIdx];

                    // L行: C00=順位(All/RowSpan=2), C01=背番号(All/RowSpan=2)
                    //       C02=L氏名(LRT), C03=Lフリガナ(LRT), C04=L所属(LRT)
                    //       C05=得点(All/RowSpan=2)
                    SetTextObjectDirectEx(report, $"DL_{nn}_C00{suffix}", rankDisp,  blAll, autoShrink: false);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C01{suffix}", bibNo,     blAll, autoShrink: false);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C02{suffix}", lName,     blLrt, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C03{suffix}", lKana,     blLrt, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C04{suffix}", lCtry,     blLrt, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DL_{nn}_C05{suffix}", scoreText, blAll, autoShrink: false);

                    // P行: C02=P氏名(LRB), C03=Pフリガナ(LRB), C04=P所属(LRB)
                    //      C00, C01, C05 は RowSpan=2 のためP行セルは空（frxで定義済み）
                    SetTextObjectDirectEx(report, $"DP_{nn}_C02{suffix}", pName, blLrb, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C03{suffix}", pKana, blLrb, autoShrink: true);
                    SetTextObjectDirectEx(report, $"DP_{nn}_C04{suffix}", pCtry, blLrb, autoShrink: true);
                }
                else
                {
                    // 未使用行: 全列 None・空文字
                    for (int c = 0; c <= 5; c++)
                    {
                        SetTextObjectDirectEx(report, $"DL_{nn}_C{c:D2}{suffix}", "", blNone, autoShrink: false);
                        if (c >= 2 && c <= 4)
                            SetTextObjectDirectEx(report, $"DP_{nn}_C{c:D2}{suffix}", "", blNone, autoShrink: false);
                    }
                }
            }
        }

        /// <summary>
        /// フルネームから苗字（最初のトークン）を取得する。
        /// 半角スペース・全角スペースで分割する。
        /// </summary>
        private static string ExtractLastName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            // 半角スペース・全角スペースで分割
            var parts = fullName.Split(new char[] { ' ', '\u3000' },
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : fullName;
        }

        /// <summary>
        /// 列ページ範囲を種目の境界で計算する。
        /// MaxJudgeColsPerPage に収まる最大の種目数でページを区切る。
        /// 1種目のジャッジ数が MaxJudgeColsPerPage を超える場合は、その種目1つで1ページとする。
        /// 戻り値: (colStart, colEnd) の checkCols インデックスリスト。
        /// </summary>
        private static List<(int colStart, int colEnd)> BuildColumnPageRanges(
            List<List<string>> judgesPerDance,
            List<(int danceIdx, int judgeIdx)> checkCols,
            int maxJudgeColsPerPage)
        {
            var ranges = new List<(int colStart, int colEnd)>();

            // 各種目のジャッジ数リスト（0件種目は除外済み想定）
            var danceCounts = judgesPerDance
                .Select((j, di) => (danceIdx: di, count: j.Count))
                .Where(x => x.count > 0)
                .ToList();

            if (danceCounts.Count == 0)
            {
                ranges.Add((0, checkCols.Count));
                return ranges;
            }

            // 種目を MaxJudgeColsPerPage 以内でグループ化
            var pageGroups = new List<List<int>>();   // 各ページに入る danceIdx のリスト
            var currentGroup = new List<int>();
            int currentCount = 0;

            foreach (var (danceIdx, count) in danceCounts)
            {
                if (currentGroup.Count > 0 && currentCount + count > maxJudgeColsPerPage)
                {
                    // 現グループを確定して新グループ開始
                    pageGroups.Add(currentGroup);
                    currentGroup = new List<int>();
                    currentCount = 0;
                }
                currentGroup.Add(danceIdx);
                currentCount += count;
            }
            if (currentGroup.Count > 0)
                pageGroups.Add(currentGroup);

            // 各グループを checkCols のインデックス範囲に変換
            foreach (var group in pageGroups)
            {
                int firstDance = group[0];
                int lastDance  = group[group.Count - 1];

                // colStart: firstDance の最初のジャッジ列
                int colStart = 0;
                for (int i = 0; i < checkCols.Count; i++)
                {
                    if (checkCols[i].danceIdx == firstDance && checkCols[i].judgeIdx == 0)
                    { colStart = i; break; }
                }

                // colEnd: lastDance の次のセパレータまたはリスト末尾
                int colEnd = checkCols.Count;
                for (int i = colStart; i < checkCols.Count; i++)
                {
                    var (di, ji) = checkCols[i];
                    if (di >= 0 && di > lastDance)
                    { colEnd = i; break; }
                }

                ranges.Add((colStart, colEnd));
            }

            return ranges;
        }

        private static void RenameObjectsInPage(FastReport.Base obj, string suffix)
        {
            if (!string.IsNullOrEmpty(obj.Name))
                obj.Name = obj.Name + suffix;
            if (obj is FastReport.IParent parent)
            {
                var children = new FastReport.ObjectCollection();
                parent.GetChildObjects(children);
                foreach (FastReport.Base child in children)
                    RenameObjectsInPage(child, suffix);
            }
        }

        private void BindTableToReport(Report report, DataTable table)
        {
            var src = report.Dictionary.DataSources.FindByName(table.TableName);
            var tds = src as FastReport.Data.TableDataSource;
            if (tds != null)
            {
                // Table と Reference を両方セット
                // InitSchema() は Connection==null 時に "table = Reference as DataTable" するため
                // Reference にも同じオブジェクトをセットしておく必要がある
                tds.Table     = table;
                tds.Reference = table;

                // IgnoreConnection=true: Connection プロパティが常に null を返すようになり
                // DB 再取得・FillTable 呼び出しを確実に防ぐ（internal プロパティのためリフレクション経由）
                try
                {
                    var prop = typeof(FastReport.Data.TableDataSource)
                        .GetProperty("IgnoreConnection",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic);
                    prop?.SetValue(tds, true);
                }
                catch { /* リフレクション失敗時は無視（Table+Reference で対処済み）*/ }

                _log.LogAdd($"[ReportRenderer] BindTable: '{table.TableName}' → tds.Table/Reference セット完了 ({table.Rows.Count}行)", _log.INFO);
            }
            else
            {
                report.RegisterData(table, table.TableName);
                _log.LogAdd($"[ReportRenderer] BindTable: '{table.TableName}' → RegisterData (tds not found)", _log.INFO);
            }
        }

        // ─── ユーティリティ ──────────────────────────────────────────

        /// <summary>JsonNode → DataSet 変換</summary>
        private DataSet JsonNodeToDataSet(JsonNode node, string rootName)
        {
            var ds = new DataSet(rootName);
            string json = node.ToJsonString();
            var jToken  = JToken.Parse(json);

            if (jToken is JObject jObj)
            {
                var rootTable = new DataTable(rootName);
                foreach (var prop in jObj.Properties())
                {
                    if (prop.Value is JArray arr)
                        ds.Tables.Add(JsonArrayToDataTable(arr, prop.Name));
                    else if (!rootTable.Columns.Contains(prop.Name))
                        rootTable.Columns.Add(prop.Name, typeof(string));
                }
                if (rootTable.Columns.Count > 0)
                {
                    var row = rootTable.NewRow();
                    foreach (var prop in jObj.Properties())
                        if (rootTable.Columns.Contains(prop.Name))
                            row[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                    rootTable.Rows.Add(row);
                    ds.Tables.Add(rootTable);
                }
            }
            else if (jToken is JArray jArr)
            {
                ds.Tables.Add(JsonArrayToDataTable(jArr, rootName));
            }

            return ds;
        }

        private static DataTable JsonArrayToDataTable(JArray arr, string tableName)
        {
            var dt = new DataTable(tableName);
            foreach (var item in arr)
            {
                if (item is not JObject obj) continue;
                foreach (var prop in obj.Properties())
                    if (!dt.Columns.Contains(prop.Name))
                        dt.Columns.Add(prop.Name, typeof(string));
                var row = dt.NewRow();
                foreach (var prop in obj.Properties())
                    if (dt.Columns.Contains(prop.Name))
                        row[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                dt.Rows.Add(row);
            }
            return dt;
        }

        private static string ResolveFrxPath(string frxPath)
        {
            if (Path.IsPathRooted(frxPath)) return frxPath;
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, frxPath));
        }
    }
}
