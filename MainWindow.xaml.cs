using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using DSPrt.印刷;
using DSPrt.Messages;

namespace DSPrt
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// Phase 2/3 — PrintService 統合・ジョブログ・再印刷対応
    /// </summary>
    public partial class MainWindow : Window
    {
        private DSPrtClient?    _client;
        private PrintService?   _printService;
        private readonly object _logLock = new object();
        private const int MaxLogLines = 500;

        // ジョブログ（DataGrid バインド用）
        private readonly ObservableCollection<JobLogItem> _jobLog = new();
        private const int JobLogMaxCount = 200;

        // プレビュー
        private JobLogItem? _previewTargetJob;
        private string?     _lastHtmlPath;
        private const string PreviewSpoolDir = "./Spool/Preview";

        public MainWindow()
        {
            InitializeComponent();
            JobLogGrid.ItemsSource = _jobLog;
            JobLogGrid.SelectionChanged += JobLogGrid_SelectionChanged;
            Loaded  += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        // ─── ウィンドウライフサイクル ────────────────────────────────

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var ws = AppSettings.Instance.WebSocketSettings;
            Title           = $"DSPrt - {ws.DisplayName}（{ws.InstanceId}）";
            TxtServerInfo.Text = $"{ws.ServerIpAddress}:{ws.ServerPort}";
            AddLog($"DSPrt 起動: instanceId={ws.InstanceId}, server={ws.ServerIpAddress}:{ws.ServerPort}");

            // 起動時から帳票設定タブに一覧を表示（サーバー接続不要）
            RefreshLayoutTab();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            AddLog("DSPrt 終了処理");
            _printService?.Dispose();
            _printService = null;
            _client?.Dispose();
            _client = null;
        }

        // ─── 接続・切断 ─────────────────────────────────────────────

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            await ConnectAndInitializeAsync();
        }

        private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            await DisconnectAsync();
        }

        private async Task ConnectAndInitializeAsync()
        {
            try
            {
                SetConnectionUi(connecting: true);
                AddLog("接続中...");

                // PrintService 初期化（DataManager より前に必要な依存はないのでここで作成）
                // ただし DataManager は DSPrtClient が持つため、先に Client を作る
                _client = new DSPrtClient();
                _client.Log.LogOutput                += AddLog;
                _client.ConnectionStateChanged       += OnConnectionStateChanged;
                _client.DA_MasterReceived            += (s, args) => AddLog("DA_Master 受信完了");
                _client.DS_StatusReceived            += (s, args) => AddLog("DS_Status 受信完了");
                _client.PrintJobRequested            += OnPrintJobRequested;
                _client.PrintJobCancelRequested      += OnPrintJobCancelRequested;
                _client.CompetitionSelector          = OnSelectCompetitionAsync;

                bool connected = await _client.ConnectAsync();
                if (!connected)
                {
                    AddLog("接続失敗");
                    SetConnectionUi(connected: false);
                    _client?.Dispose();
                    _client = null;
                    return;
                }

                // PrintService を DataManager の後（ConnectAsync 後）に初期化
                _printService = new PrintService(_client.Log, _client.DataManager);
                _printService.JobStatusChanged += OnJobStatusChanged;
                _printService.PrintDone        += OnPrintDone;

                // 帳票設定タブ更新
                RefreshLayoutTab();

                AddLog("接続成功 — 初期化シーケンス開始");
                bool initialized = await _client.InitializeAsync();

                if (initialized)
                {
                    AddLog("初期化完了");
                    SetConnectionUi(connected: true);
                }
                else
                {
                    AddLog("初期化失敗（タイムアウトまたはキャンセル）");
                    SetConnectionUi(connected: false);
                }
            }
            catch (Exception ex)
            {
                AddLog($"接続エラー: {ex.Message}");
                SetConnectionUi(connected: false);
                _printService?.Dispose();
                _printService = null;
                _client?.Dispose();
                _client = null;
            }
        }

        private async Task DisconnectAsync()
        {
            if (_client == null) return;

            AddLog("切断中...");

            _printService?.Dispose();
            _printService = null;

            await _client.DisconnectAsync();
            _client.Dispose();
            _client = null;

            SetConnectionUi(connected: false);
            AddLog("切断完了");
        }

        // ─── 接続状態変更 ────────────────────────────────────────────

        private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (e.IsConnected)
                {
                    SetConnectionUi(connected: true);
                    AddLog($"接続状態: 接続済み{(e.Message != null ? $" ({e.Message})" : "")}");
                }
                else
                {
                    SetConnectionUi(connected: false);
                    AddLog($"接続状態: 切断{(e.Message != null ? $" ({e.Message})" : "")}");
                }
            });
        }

        // ─── 印刷ジョブ受信 ─────────────────────────────────────────

        private async void OnPrintJobRequested(object? sender, Handlers.PrintJobRequestedEventArgs e)
        {
            var req = e.Job;  // PR_PRINT_Request
            AddLog($"印刷ジョブ受信: jobId={req.JobId}, layoutId={req.LayoutId}, copies={req.Copies}");

            // PR_ACK 送信
            if (_client != null)
            {
                bool acked = await _client.SendAckAsync(req.JobId);
                AddLog(acked ? $"PR_ACK 送信: {req.JobId}" : $"PR_ACK 送信失敗: {req.JobId}");
            }

            // PR_PRINT_Request → PrintJob 変換
            var job = new PrintJob
            {
                JobId    = req.JobId,
                LayoutId = req.LayoutId,
                Copies   = req.Copies,
                Priority = req.Priority,
                Data     = req.Data
            };

            // ── [診断ログ] bib=20・種目C・Eジャッジの素点を受信データから確認 ──
            try
            {
                if (req.Data != null)
                {
                    var dataStr  = req.Data.ToJsonString();

                    // 受信データをファイルに保存（種目C bib=20 関連のデバッグ用）
                    try
                    {
                        string diagPath = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(), $"DSPrt_ReceivedData_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                        System.IO.File.WriteAllText(diagPath, dataStr, System.Text.Encoding.UTF8);
                        AddLog($"[診断] 受信Data保存: {diagPath}");
                    }
                    catch { }

                    var dataObj  = Newtonsoft.Json.Linq.JObject.Parse(dataStr);
                    var shomokuArr = dataObj["種目結果"] as Newtonsoft.Json.Linq.JArray;
                    if (shomokuArr != null)
                    {
                        foreach (var shomoku in shomokuArr.OfType<Newtonsoft.Json.Linq.JObject>())
                        {
                            if (shomoku["種目記号"]?.ToString() != "C") continue;
                            var senshu = shomoku["選手結果"] as Newtonsoft.Json.Linq.JArray;
                            if (senshu == null) break;
                            foreach (var sk in senshu.OfType<Newtonsoft.Json.Linq.JObject>())
                            {
                                if (sk["背番号"]?.ToString() != "20") continue;
                                var judges = sk["ジャッジ詳細結果"] as Newtonsoft.Json.Linq.JArray;
                                if (judges == null) break;
                                // 素点の型情報も含めて出力
                                var scores = string.Join(", ", judges.OfType<Newtonsoft.Json.Linq.JObject>()
                                    .Select(j => $"{j["ジャッジ記号"]}={j["素点"]}(type={j["素点"]?.Type})"));
                                AddLog($"[診断] 受信Data: 種目C・bib=20 ジャッジ詳細 → {scores}");
                                break;
                            }
                            break;
                        }
                    }
                    else
                    {
                        AddLog($"[診断] 受信Data: 種目結果 キーなし (keys={string.Join(",", dataObj.Properties().Select(p => p.Name).Take(10))})");
                    }
                }
                else
                {
                    AddLog("[診断] 受信Data: null");
                }
            }
            catch (Exception diagEx)
            {
                AddLog($"[診断] 受信Data 解析エラー: {diagEx.Message}");
            }
            // ── [診断ログ] ここまで ──

            // 印刷キューに追加
            if (_printService != null)
            {
                bool queued = _printService.Enqueue(job);
                AddLog(queued ? $"印刷キュー追加: {req.JobId}" : $"印刷キュー追加失敗（重複または満杯）: {req.JobId}");
                UpdateQueueInfo();
            }
        }

        private void OnPrintJobCancelRequested(object? sender, Handlers.PrintJobCancelRequestedEventArgs e)
        {
            AddLog($"印刷キャンセル受信: jobId={e.JobId}");
            _printService?.Cancel(e.JobId);
            UpdateQueueInfo();
        }

        // ─── 印刷状態コールバック ────────────────────────────────────

        private void OnJobStatusChanged(object? sender, PrintJobStatusChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var job  = e.Job;
                var item = _jobLog.FirstOrDefault(x => x.JobId == job.JobId);
                if (item != null)
                {
                    item.Status      = job.Status;
                    item.CompletedAt = job.CompletedAt;
                    item.ErrorMessage= job.ErrorMessage;
                    // DataGrid に変更を通知するため、アイテムを削除・再追加（ObservableCollection のトリック）
                    int idx = _jobLog.IndexOf(item);
                    _jobLog.RemoveAt(idx);
                    _jobLog.Insert(idx, item);
                }
                else
                {
                    // 新規ジョブ
                    _jobLog.Insert(0, new JobLogItem(job));

                    // 上限超過時に古いものを削除
                    while (_jobLog.Count > JobLogMaxCount)
                        _jobLog.RemoveAt(_jobLog.Count - 1);
                }
                UpdateQueueInfo();
            });
        }

        private async void OnPrintDone(object? sender, PrintDoneEventArgs e)
        {
            AddLog($"印刷{(e.Status == "done" ? "完了" : "エラー")}: jobId={e.JobId}{(e.Message != null ? $", msg={e.Message}" : "")}");

            // PR_DONE 送信
            if (_client != null)
            {
                bool sent = await _client.SendDoneAsync(e.JobId, e.Status, e.Message);
                AddLog(sent ? $"PR_DONE 送信: {e.JobId}" : $"PR_DONE 送信失敗: {e.JobId}");
            }
            UpdateQueueInfo();
        }

        // ─── ジョブログ操作 ─────────────────────────────────────────

        private void JobLogGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 完了またはエラーのジョブが選択されていれば再印刷ボタンを有効化
            bool canReprint = JobLogGrid.SelectedItems
                .Cast<JobLogItem>()
                .Any(item => item.Status == PrintJobStatus.Done || item.Status == PrintJobStatus.Error);
            BtnReprint.IsEnabled = canReprint;

            // プレビュー対象を更新
            var single = JobLogGrid.SelectedItems.Count == 1
                ? JobLogGrid.SelectedItems.Cast<JobLogItem>().First()
                : null;
            _previewTargetJob = single;
            TxtPreviewJobId.Text = single?.JobId ?? "（ジョブログタブで行を選択してください）";
            BtnPreview.IsEnabled = single != null && _printService != null;
        }

        private void BtnReprint_Click(object sender, RoutedEventArgs e)
        {
            if (_printService == null) return;

            var selected = JobLogGrid.SelectedItems
                .Cast<JobLogItem>()
                .Where(item => item.Status == PrintJobStatus.Done || item.Status == PrintJobStatus.Error)
                .ToList();

            foreach (var item in selected)
            {
                var reprintJob = item.OriginalJob.CreateReprint();
                bool queued = _printService.Reprint(item.OriginalJob);
                AddLog(queued ? $"再印刷キュー追加: {reprintJob.JobId}" : $"再印刷失敗: {item.JobId}");
            }
            UpdateQueueInfo();
        }

        private void BtnClearJobLog_Click(object sender, RoutedEventArgs e)
        {
            _jobLog.Clear();
        }

        // ─── プレビュータブ ──────────────────────────────────────────

        private async void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_previewTargetJob == null || _printService == null) return;

            BtnPreview.IsEnabled = false;
            AddLog($"プレビュー生成中: jobId={_previewTargetJob.JobId}");

            try
            {
                string? htmlPath = await _printService.ExportHtmlAsync(
                    _previewTargetJob.OriginalJob,
                    PreviewSpoolDir);

                if (htmlPath != null)
                {
                    string absoluteHtmlPath = System.IO.Path.GetFullPath(htmlPath);
                    _lastHtmlPath = absoluteHtmlPath;
                    BtnOpenHtml.IsEnabled = true;
                    PreviewBrowser.Navigate(new Uri(absoluteHtmlPath, UriKind.Absolute));
                    AddLog($"プレビュー完了: {absoluteHtmlPath}");
                }
                else
                {
                    AddLog($"プレビュー生成失敗: jobId={_previewTargetJob.JobId}");
                    MessageBox.Show("プレビューの生成に失敗しました。接続ログタブでエラー内容を確認してください。",
                        "プレビュー失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AddLog($"プレビューエラー: {ex.Message}");
            }
            finally
            {
                BtnPreview.IsEnabled = _previewTargetJob != null;
            }
        }

        private void BtnOpenHtml_Click(object sender, RoutedEventArgs e)
        {
            if (_lastHtmlPath == null || !System.IO.File.Exists(_lastHtmlPath)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = _lastHtmlPath,
                UseShellExecute = true
            });
        }

        // ─── 帳票設定タブ ────────────────────────────────────────────

        private void RefreshLayoutTab()
        {
            Dispatcher.Invoke(() =>
            {
                // デフォルトプリンター（PrintService があれば実プリンター名、なければ設定値を表示）
                TxtDefaultPrinter.Text = _printService != null
                    ? _printService.PrinterController.GetDefaultPrinterName()
                    : AppSettings.Instance.PrintSettings.DefaultPrinterName;

                // レイアウト一覧（PrintService がなくても AppSettings から直接表示）
                LayoutGrid.ItemsSource = _printService != null
                    ? _printService.Registry.All.ToList()
                    : AppSettings.Instance.Layouts;
            });
        }

        // ─── 帳票設定タブ（デザイナー関連）────────────────────────────

        private void LayoutGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool hasSelection = LayoutGrid.SelectedItem is LayoutSetting;
            BtnOpenDesigner.IsEnabled       = hasSelection;
            BtnTestPrint.IsEnabled          = hasSelection;
            BtnTestPreview.IsEnabled        = hasSelection;
            BtnChangeLayoutPrinter.IsEnabled = hasSelection;
        }

        private void BtnOpenDesigner_Click(object sender, RoutedEventArgs e)
        {
            if (LayoutGrid.SelectedItem is not LayoutSetting layout) return;

            string frxPath = ResolveFrxPath(layout.FrxPath);
            if (!System.IO.File.Exists(frxPath))
            {
                MessageBox.Show(
                    $".frx ファイルが見つかりません:\n{frxPath}\n\nReports フォルダにファイルを配置してください。",
                    "ファイルが見つかりません",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = frxPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"ファイルを開けませんでした:\n{ex.Message}\n\nFastReport Designer をインストールするか、.frx ファイルの関連アプリを設定してください。",
                    "エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ─── プリンター変更 ──────────────────────────────────────────

        /// <summary>
        /// デフォルトプリンターの変更（DSPrt.json の PrintSettings.DefaultPrinterName を更新）
        /// </summary>
        private void BtnChangeDefaultPrinter_Click(object sender, RoutedEventArgs e)
        {
            var selected = ShowPrinterSelectDialog("デフォルトプリンターを選択してください");
            if (selected == null) return;

            AppSettings.Instance.PrintSettings.DefaultPrinterName = selected;
            AppSettings.Save();
            RefreshLayoutTab();
            AddLog($"デフォルトプリンター変更: {selected}");
        }

        /// <summary>
        /// 選択レイアウトのプリンター変更（DSPrt.json の該当 Layout.PrinterName を更新）
        /// </summary>
        private void BtnChangeLayoutPrinter_Click(object sender, RoutedEventArgs e)
        {
            if (LayoutGrid.SelectedItem is not LayoutSetting layout) return;

            var selected = ShowPrinterSelectDialog($"「{layout.LayoutId}」のプリンターを選択してください");
            if (selected == null) return;

            // 実行中 PrintService の Registry を更新
            _printService?.Registry.UpdatePrinterName(layout.LayoutId, selected);

            // AppSettings 経由で DSPrt.json に保存
            var appLayout = AppSettings.Instance.Layouts
                .Find(l => string.Equals(l.LayoutId, layout.LayoutId, StringComparison.OrdinalIgnoreCase));
            if (appLayout != null)
                appLayout.PrinterName = selected;
            AppSettings.Save();

            // DataGrid を再描画
            RefreshLayoutTab();
            AddLog($"プリンター変更: layoutId={layout.LayoutId}, printer={selected}");
        }

        /// <summary>
        /// WPF の PrintDialog を使ってプリンターを選択させ、選択されたプリンター名を返す。
        /// キャンセル時は null を返す。
        /// </summary>
        private string? ShowPrinterSelectDialog(string description)
        {
            // WPF PrintDialog でプリンター一覧を表示
            var dlg = new System.Windows.Controls.PrintDialog();
            if (dlg.ShowDialog() != true) return null;
            return dlg.PrintQueue.FullName;
        }

        private void BtnOpenReportsFolder_Click(object sender, RoutedEventArgs e)
        {
            string reportsDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports"));
            System.IO.Directory.CreateDirectory(reportsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = reportsDir,
                UseShellExecute = true
            });
        }

        // ─── テスト印刷・プレビュー ──────────────────────────────────

        private async void BtnTestPrint_Click(object sender, RoutedEventArgs e)
        {
            if (LayoutGrid.SelectedItem is not LayoutSetting layout) return;
            await RunTestJobAsync(layout, previewOnly: false);
        }

        private async void BtnTestPreview_Click(object sender, RoutedEventArgs e)
        {
            if (LayoutGrid.SelectedItem is not LayoutSetting layout) return;
            await RunTestJobAsync(layout, previewOnly: true);
        }

        private async Task RunTestJobAsync(LayoutSetting layout, bool previewOnly)
        {
            // --- PrintService をスタンドアロンで初期化（未接続でも動作） ---
            LOG_C log;
            DSPrt.Data.DataManager dm;
            PrintService svc;

            if (_printService != null && _client != null)
            {
                // 接続中の場合はそのまま使用
                log = _client.Log;
                dm  = _client.DataManager;
                svc = _printService;
            }
            else
            {
                // 未接続の場合: ログ・DataManager・PrintService をローカルで作成
                log = new LOG_C();
                log.SetLogLevel(AppSettings.Instance.LogSettings.LogLevel);
                log.CreateFile(AppSettings.Instance.LogSettings.LogPath);
                log.LogOutput += AddLog;
                dm  = new DSPrt.Data.DataManager(log);
                svc = new PrintService(log, dm);
            }

            try
            {
                BtnTestPrint.IsEnabled   = false;
                BtnTestPreview.IsEnabled = false;

                // --- テストデータ JSON の選択 ---
                string testDataDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports", "TestData"));

                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title            = $"テストデータを選択 — {layout.LayoutId}",
                    InitialDirectory = System.IO.Directory.Exists(testDataDir) ? testDataDir : null,
                    Filter           = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
                    FileName         = $"PR_PRINT_{layout.LayoutId}_テスト.json"
                };

                if (dlg.ShowDialog() != true)
                {
                    AddLog("テスト印刷: キャンセルされました");
                    return;
                }

                // --- JSON 読み込み ---
                string jsonText = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonText);
                var root    = jsonDoc.RootElement;

                string jobId    = root.TryGetProperty("jobId",    out var jid)  ? jid.GetString()  ?? $"TEST_{DateTime.Now:yyyyMMddHHmmss}" : $"TEST_{DateTime.Now:yyyyMMddHHmmss}";
                string layoutId = root.TryGetProperty("layoutId", out var lid)  ? lid.GetString()  ?? layout.LayoutId : layout.LayoutId;
                int    copies   = previewOnly ? 0 : (root.TryGetProperty("copies",   out var cpy)  ? cpy.GetInt32() : 1);

                // data フィールド（null の場合は DataManager キャッシュを使用するため null のまま渡す）
                System.Text.Json.Nodes.JsonNode? dataNode = null;
                if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    dataNode = System.Text.Json.Nodes.JsonNode.Parse(dataProp.GetRawText());
                }

                // DA_Master / DS_Status が必要な帳票でキャッシュが未ロードの場合はファイル選択で読み込む
                // 条件: DA_Master または DS_Status を使う dataType かつキャッシュが空
                // ※ PLAYER_NOTICE_HORIZONTAL_A4 のように data に別途パラメーターが入る帳票も対象にする
                bool needsDaMaster = layout.DataType.Contains("DA_Master", StringComparison.OrdinalIgnoreCase);
                bool needsDsStatus = layout.DataType.Contains("DS_Status", StringComparison.OrdinalIgnoreCase);

                if ((needsDaMaster && dm.DA_Master == null) || (needsDsStatus && dm.DS_Status == null))
                {
                    // DA_Master の選択
                    if (needsDaMaster && dm.DA_Master == null)
                    {
                        var daDlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title            = "DA_Master.json を選択してください",
                            InitialDirectory = System.IO.Path.GetDirectoryName(dlg.FileName),
                            Filter           = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
                            FileName         = "DA_Master.json"
                        };
                        if (daDlg.ShowDialog() == true)
                        {
                            dm.SetDA_Master(System.IO.File.ReadAllText(daDlg.FileName, System.Text.Encoding.UTF8));
                            AddLog($"DA_Master ロード: {daDlg.FileName}");
                        }
                        else
                        {
                            AddLog("DA_Master の選択がキャンセルされました。空データで続行します。");
                        }
                    }

                    // DS_Status の選択
                    if (needsDsStatus && dm.DS_Status == null)
                    {
                        var dsDlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title            = "DS_Status.json を選択してください",
                            InitialDirectory = System.IO.Path.GetDirectoryName(dlg.FileName),
                            Filter           = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
                            FileName         = "DS_Status.json"
                        };
                        if (dsDlg.ShowDialog() == true)
                        {
                            dm.SetDS_Status(System.IO.File.ReadAllText(dsDlg.FileName, System.Text.Encoding.UTF8));
                            AddLog($"DS_Status ロード: {dsDlg.FileName}");
                        }
                        else
                        {
                            AddLog("DS_Status の選択がキャンセルされました。空データで続行します。");
                        }
                    }
                }

                var job = new PrintJob
                {
                    JobId    = jobId,
                    LayoutId = layoutId,
                    Copies   = copies,
                    Priority = 1,
                    Data     = dataNode
                };

                if (previewOnly)
                {
                    // --- プレビュー ---
                    AddLog($"プレビュー生成開始: jobId={jobId}, layoutId={layoutId}");
                    string? htmlPath = await svc.ExportHtmlAsync(job, PreviewSpoolDir);
                    if (htmlPath != null)
                    {
                        // 相対パスを絶対パスに変換してから Uri に渡す（相対パスのまま UriKind.Absolute を指定すると例外）
                        string absoluteHtmlPath = System.IO.Path.GetFullPath(htmlPath);
                        _lastHtmlPath = absoluteHtmlPath;
                        BtnOpenHtml.IsEnabled = true;
                        PreviewBrowser.Navigate(new Uri(absoluteHtmlPath, UriKind.Absolute));
                        AddLog($"プレビュー完了: {absoluteHtmlPath}");
                        // プレビュータブに切り替え
                        if (MainTabControl.SelectedIndex != 2)
                            MainTabControl.SelectedIndex = 2;
                    }
                    else
                    {
                        MessageBox.Show("プレビューの生成に失敗しました。接続ログを確認してください。",
                            "プレビュー失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    // --- 印刷: WPF PrintDialog でプリンターを選択して直接印刷 ---
                    var printDialog = new System.Windows.Controls.PrintDialog();
                    if (printDialog.ShowDialog() != true)
                    {
                        AddLog("テスト印刷: プリンター選択がキャンセルされました");
                        return;
                    }

                    string selectedPrinter = printDialog.PrintQueue.FullName;
                    AddLog($"テスト印刷 開始: jobId={jobId}, layoutId={layoutId}, printer={selectedPrinter}");
                    await svc.PrintDirectAsync(job, selectedPrinter);
                    AddLog($"テスト印刷 完了: {jobId}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"テスト印刷エラー: {ex.Message}");
                MessageBox.Show($"エラー:\n{ex.Message}", "テスト印刷エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                bool hasSel = LayoutGrid.SelectedItem is LayoutSetting;
                BtnTestPrint.IsEnabled           = hasSel;
                BtnTestPreview.IsEnabled         = hasSel;
                BtnChangeLayoutPrinter.IsEnabled = hasSel;

                // ローカル作成した場合のみ Dispose
                if (_printService == null || _client == null)
                {
                    svc.Dispose();
                }
            }
        }

        private static string ResolveFrxPath(string frxPath)
        {
            if (System.IO.Path.IsPathRooted(frxPath)) return frxPath;
            return System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, frxPath));
        }

        // ─── 競技会選択ダイアログ ────────────────────────────────────

        private Task<string?> OnSelectCompetitionAsync(List<CompetitionInfo> competitions)
        {
            var tcs = new TaskCompletionSource<string?>();

            Dispatcher.Invoke(() =>
            {
                var dialog = new CompetitionSelectDialog(competitions) { Owner = this };
                if (dialog.ShowDialog() == true)
                    tcs.SetResult(dialog.SelectedCmpNo);
                else
                    tcs.SetResult(null);
            });

            return tcs.Task;
        }

        // ─── ログ ────────────────────────────────────────────────────

        private void AddLog(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddLog(message));
                return;
            }

            lock (_logLock)
            {
                TxtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");

                var lines = TxtLog.Text.Split('\n');
                if (lines.Length > MaxLogLines)
                    TxtLog.Text = string.Join("\n", lines, lines.Length - MaxLogLines, MaxLogLines);

                TxtLog.ScrollToEnd();
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }

        // ─── UI ヘルパー ─────────────────────────────────────────────

        private void SetConnectionUi(bool? connected = null, bool connecting = false)
        {
            if (connecting)
            {
                StatusIndicator.Fill     = Brushes.Orange;
                TxtConnectionStatus.Text = "接続中...";
                BtnConnect.IsEnabled     = false;
                BtnDisconnect.IsEnabled  = false;
                return;
            }

            if (connected == true)
            {
                StatusIndicator.Fill     = Brushes.LimeGreen;
                TxtConnectionStatus.Text = "接続済み";
                BtnConnect.IsEnabled     = false;
                BtnDisconnect.IsEnabled  = true;
            }
            else
            {
                StatusIndicator.Fill     = Brushes.Gray;
                TxtConnectionStatus.Text = "未接続";
                BtnConnect.IsEnabled     = true;
                BtnDisconnect.IsEnabled  = false;
            }
        }

        private void UpdateQueueInfo()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(UpdateQueueInfo);
                return;
            }
            int count = _printService?.QueueCount ?? 0;
            TxtQueueInfo.Text = $"キュー: {count}";
        }
    }

    // ─── ジョブログ表示モデル ─────────────────────────────────────────

    /// <summary>
    /// DataGrid バインド用ジョブログ表示アイテム
    /// </summary>
    public class JobLogItem
    {
        public string          JobId       { get; set; }
        public string          LayoutId    { get; set; }
        public int             Copies      { get; set; }
        public PrintJobStatus  Status      { get; set; }
        public DateTime        ReceivedAt  { get; set; }
        public DateTime?       CompletedAt { get; set; }
        public string?         ErrorMessage { get; set; }
        public string          StatusText  => Status switch
        {
            PrintJobStatus.Queued     => "待機",
            PrintJobStatus.Processing => "印刷中",
            PrintJobStatus.Done       => "完了",
            PrintJobStatus.Error      => "エラー",
            PrintJobStatus.Cancelled  => "キャンセル",
            _                         => Status.ToString()
        };

        /// <summary>再印刷・ジョブログ参照用に元のジョブを保持</summary>
        public PrintJob OriginalJob { get; }

        public JobLogItem(PrintJob job)
        {
            OriginalJob  = job;
            JobId        = job.JobId;
            LayoutId     = job.LayoutId;
            Copies       = job.Copies;
            Status       = job.Status;
            ReceivedAt   = job.ReceivedAt;
            CompletedAt  = job.CompletedAt;
            ErrorMessage = job.ErrorMessage;
        }
    }
}
