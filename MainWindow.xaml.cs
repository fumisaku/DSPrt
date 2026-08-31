using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using DSPrt.印刷;
using DSPrt.Messages;
using DSPrt.賞状;
using static DSPrt.賞状.AwardDisplayNames;

namespace DSPrt
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// Phase 2/3 — PrintService 統合・ジョブログ・再印刷対応
    /// </summary>
    public partial class MainWindow : Window
    {
        private DSPrtClient? _client;
        private PrintService? _printService;
        private readonly object _logLock = new object();
        private const int MaxLogLines = 500;

        // ジョブログ（DataGrid バインド用）
        private readonly ObservableCollection<JobLogItem> _jobLog = new();
        private const int JobLogMaxCount = 200;

        // プレビュー
        private JobLogItem? _previewTargetJob;
        private string? _lastHtmlPath;
        private const string PreviewSpoolDir = "./Spool/Preview";

        // 賞状
        private AwardLayout _awardLayout = new AwardLayout();
        private string? _awardLayoutPath;
        private bool _updatingAwardUi;
        private readonly ObservableCollection<AwardRankRow> _awardRankRows = new();

        public MainWindow()
        {
            InitializeComponent();
            JobLogGrid.ItemsSource = _jobLog;
            JobLogGrid.SelectionChanged += JobLogGrid_SelectionChanged;
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        // ─── ウィンドウライフサイクル ────────────────────────────────

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var ws = AppSettings.Instance.WebSocketSettings;
            Title = $"DSPrt - {ws.DisplayName}（{ws.InstanceId}）";
            TxtServerInfo.Text = $"{ws.ServerIpAddress}:{ws.ServerPort}";
            AddLog($"DSPrt 起動: instanceId={ws.InstanceId}, server={ws.ServerIpAddress}:{ws.ServerPort}");

            // バージョン情報をステータスバーに表示
            // Version形式: 1.yy.MMdd.HHmm  例: 1.25.720.1432 → "v1.25 build 2025-07-20 14:32"
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            if (ver != null && TxtVersion != null)
            {
                int year  = 2000 + ver.Minor;
                int month = ver.Build / 100;
                int day   = ver.Build % 100;
                int hour  = ver.Revision / 100;
                int min   = ver.Revision % 100;
                TxtVersion.Text = $"v{ver.Major}.{ver.Minor} build {year}-{month:D2}-{day:D2} {hour:D2}:{min:D2}";
            }

            RefreshLayoutTab();
            InitializeAwardTab();
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

                _client = new DSPrtClient();
                _client.Log.LogOutput += AddLog;
                _client.ConnectionStateChanged += OnConnectionStateChanged;
                _client.DA_MasterReceived += (s, args) => AddLog("DA_Master 受信完了");
                _client.DS_StatusReceived += (s, args) => AddLog("DS_Status 受信完了");
                _client.PrintJobRequested += OnPrintJobRequested;
                _client.PrintJobCancelRequested += OnPrintJobCancelRequested;
                _client.CompetitionSelector = OnSelectCompetitionAsync;

                bool connected = await _client.ConnectAsync();
                if (!connected)
                {
                    AddLog("接続失敗");
                    SetConnectionUi(connected: false);
                    _client?.Dispose();
                    _client = null;
                    return;
                }

                _printService = new PrintService(_client.Log, _client.DataManager);
                _printService.JobStatusChanged += OnJobStatusChanged;
                _printService.PrintDone += OnPrintDone;

                RefreshLayoutTab();
                InitializeAwardTab();

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
            var req = e.Job;
            AddLog($"印刷ジョブ受信: jobId={req.JobId}, layoutId={req.LayoutId}, copies={req.Copies}");

            if (_client != null)
            {
                bool acked = await _client.SendAckAsync(req.JobId);
                AddLog(acked ? $"PR_ACK 送信: {req.JobId}" : $"PR_ACK 送信失敗: {req.JobId}");
            }

            var job = new PrintJob
            {
                JobId = req.JobId,
                LayoutId = req.LayoutId,
                Copies = req.Copies,
                Priority = req.Priority,
                TargetBibNos = new List<string>(req.TargetBibNos),
                Data = req.Data
            };

            try
            {
                if (req.Data != null)
                {
                    var dataStr = req.Data.ToJsonString();
                    try
                    {
                        string diagPath = System.IO.Path.Combine(
                            System.IO.Path.GetTempPath(), $"DSPrt_ReceivedData_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                        System.IO.File.WriteAllText(diagPath, dataStr, System.Text.Encoding.UTF8);
                        AddLog($"[診断] 受信Data保存: {diagPath}");
                    }
                    catch { }
                }
            }
            catch (Exception diagEx)
            {
                AddLog($"[診断] 受信Data 解析エラー: {diagEx.Message}");
            }

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
                var job = e.Job;
                var item = _jobLog.FirstOrDefault(x => x.JobId == job.JobId);
                if (item != null)
                {
                    item.Status = job.Status;
                    item.CompletedAt = job.CompletedAt;
                    item.ErrorMessage = job.ErrorMessage;
                    int idx = _jobLog.IndexOf(item);
                    _jobLog.RemoveAt(idx);
                    _jobLog.Insert(idx, item);
                }
                else
                {
                    _jobLog.Insert(0, new JobLogItem(job));
                    while (_jobLog.Count > JobLogMaxCount)
                        _jobLog.RemoveAt(_jobLog.Count - 1);
                }
                UpdateQueueInfo();
            });
        }

        private async void OnPrintDone(object? sender, PrintDoneEventArgs e)
        {
            AddLog($"印刷{(e.Status == "done" ? "完了" : "エラー")}: jobId={e.JobId}{(e.Message != null ? $", msg={e.Message}" : "")}");

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
            bool canReprint = JobLogGrid.SelectedItems
                .Cast<JobLogItem>()
                .Any(item => item.Status == PrintJobStatus.Done || item.Status == PrintJobStatus.Error);
            BtnReprint.IsEnabled = canReprint;

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
                if (string.Equals(_previewTargetJob.OriginalJob.LayoutId, AwardService.AwardLayoutId, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("賞状ジョブのプレビューは賞状タブで確認してください。",
                        "賞状プレビュー", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

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
                FileName = _lastHtmlPath,
                UseShellExecute = true
            });
        }

        // ─── 帳票設定タブ ────────────────────────────────────────────

        // 配布先ラベル TextBox の配列（index 0〜10 = 司会〜予備2）
        private TextBox[] _roleLabelBoxes = Array.Empty<TextBox>();

        private void InitRoleLabelBoxes()
        {
            _roleLabelBoxes = new[]
            {
                TxtLabel0, TxtLabel1, TxtLabel2, TxtLabel3, TxtLabel4,
                TxtLabel5, TxtLabel6, TxtLabel7, TxtLabel8, TxtLabel9, TxtLabel10
            };
        }

        /// <summary>AppSettings.PrintSettings.RoleLabels の内容を TextBox に反映する</summary>
        private void LoadRoleLabels()
        {
            if (_roleLabelBoxes.Length == 0) return;
            var rl = AppSettings.Instance.PrintSettings.RoleLabels;
            string[] vals = {
                rl.司会, rl.掲示, rl.選手係, rl.本部, rl.ステマネ,
                rl.賞状, rl.保存, rl.GM, rl.レフリー, rl.予備1, rl.予備2
            };
            for (int i = 0; i < _roleLabelBoxes.Length; i++)
                _roleLabelBoxes[i].Text = vals[i];
        }

        /// <summary>TextBox の現在テキストを AppSettings に書き戻す（保存はしない）</summary>
        private void ApplyRoleLabelsToSettings()
        {
            if (_roleLabelBoxes.Length == 0) return;
            var rl = AppSettings.Instance.PrintSettings.RoleLabels;
            rl.司会     = _roleLabelBoxes[0].Text.Trim();
            rl.掲示     = _roleLabelBoxes[1].Text.Trim();
            rl.選手係   = _roleLabelBoxes[2].Text.Trim();
            rl.本部     = _roleLabelBoxes[3].Text.Trim();
            rl.ステマネ = _roleLabelBoxes[4].Text.Trim();
            rl.賞状     = _roleLabelBoxes[5].Text.Trim();
            rl.保存     = _roleLabelBoxes[6].Text.Trim();
            rl.GM       = _roleLabelBoxes[7].Text.Trim();
            rl.レフリー = _roleLabelBoxes[8].Text.Trim();
            rl.予備1    = _roleLabelBoxes[9].Text.Trim();
            rl.予備2    = _roleLabelBoxes[10].Text.Trim();
        }

        // ── ヘッダーTextBox イベント ──

        /// <summary>ダブルクリックで編集モードに入る</summary>
        private void RoleLabelTextBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not TextBox tb) return;
            tb.IsReadOnly = false;
            tb.Background = SystemColors.WindowBrush;
            tb.BorderThickness = new Thickness(1);
            tb.SelectAll();
            tb.Focus();
            e.Handled = true;
        }

        /// <summary>Enter で確定 / Escape でキャンセル</summary>
        private void RoleLabelTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (e.Key == System.Windows.Input.Key.Return)
            {
                CommitRoleLabel(tb);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                // 元の値に戻す
                LoadRoleLabels();
                EndRoleLabelEdit(tb);
                e.Handled = true;
            }
        }

        /// <summary>フォーカスが外れたら確定</summary>
        private void RoleLabelTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && !tb.IsReadOnly)
                CommitRoleLabel(tb);
        }

        private void CommitRoleLabel(TextBox tb)
        {
            ApplyRoleLabelsToSettings();
            EndRoleLabelEdit(tb);
        }

        private static void EndRoleLabelEdit(TextBox tb)
        {
            tb.IsReadOnly = true;
            tb.Background = Brushes.Transparent;
            tb.BorderThickness = new Thickness(0);
        }

        // グリッドバインド用 — PrintCopyRow に IsFirstInGroup を追加したラッパー
        private class CopiesGridRow : PrintCopyRow
        {
            /// <summary>同じ採点方式の先頭行か（グループ区切り線表示用）</summary>
            public bool IsFirstInGroup { get; set; }
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<CopiesGridRow> _copiesRows = new();

        /// <summary>印刷部数設定の全行定義（Excelの印刷部数サンプルと同じ28行）</summary>
        private static readonly (string 採点方式, string ラウンド, string 帳票名, string LayoutId)[] _masterRows =
        {
            ("チェック法", "予選",   "出場者連絡票",          "PLAYER_NOTICE_HORIZONTAL_A4"),
            ("チェック法", "予選",   "出場者ヒート表",        "PLAYER_HEAT_VERTICAL_A4"),
            ("チェック法", "予選",   "得点一覧表_チェック法", "CHECK_SCORE_LIST_A4"),
            ("チェック法", "準決勝", "出場者連絡票",          "PLAYER_NOTICE_HORIZONTAL_A4"),
            ("チェック法", "準決勝", "準決勝進出者名簿",      "SEMI_FINAL_ENTRY_LIST_A4"),
            ("チェック法", "準決勝", "出場者ヒート表",        "PLAYER_HEAT_VERTICAL_A4"),
            ("チェック法", "準決勝", "得点一覧表_チェック法", "CHECK_SCORE_LIST_A4"),
            ("順位法",     "予選",   "出場者連絡票",          "PLAYER_NOTICE_HORIZONTAL_A4"),
            ("順位法",     "予選",   "出場者ヒート表",        "PLAYER_HEAT_VERTICAL_A4"),
            ("順位法",     "予選",   "得点一覧表_順位法",     "RANK_SCORE_LIST_A4"),
            ("順位法",     "準決勝", "出場者連絡票",          "PLAYER_NOTICE_HORIZONTAL_A4"),
            ("順位法",     "準決勝", "出場者ヒート表",        "PLAYER_HEAT_VERTICAL_A4"),
            ("順位法",     "準決勝", "得点一覧表_順位法",     "RANK_SCORE_LIST_A4"),
            ("順位法",     "決勝",   "決勝進出者名簿",        "FINAL_ENTRY_LIST_A4"),
            ("順位法",     "決勝",   "得点一覧表_順位法",     "RANK_SCORE_LIST_A4"),
            ("順位法",     "決勝",   "決勝入賞者名簿",        "FINAL_AWARD_LIST_A4"),
            ("AJS",        "予選",   "出場者連絡票",          "PLAYER_NOTICE_HORIZONTAL_A4"),
            ("AJS",        "予選",   "出場者ヒート表",        "PLAYER_HEAT_VERTICAL_A4"),
            ("AJS",        "予選",   "得点一覧表_AJS",        "AJS_SCORE_LIST_A4"),
            ("AJS",        "準決勝", "出場者連絡票",          "PLAYER_NOTICE_HORIZONTAL_A4"),
            ("AJS",        "準決勝", "準決勝進出者名簿",      "SEMI_FINAL_ENTRY_LIST_A4"),
            ("AJS",        "準決勝", "出場者ヒート表",        "PLAYER_HEAT_VERTICAL_A4"),
            ("AJS",        "準決勝", "得点一覧表_AJS",        "AJS_SCORE_LIST_A4"),
            ("AJS",        "決勝",   "出場者連絡票",          "PLAYER_NOTICE_AJS_FINAL_A4"),
            ("AJS",        "決勝",   "決勝進出者名簿",        "FINAL_ENTRY_LIST_A4"),
            ("AJS",        "決勝",   "出場者ヒート表",        "PLAYER_HEAT_VERTICAL_A4"),
            ("AJS",        "決勝",   "得点一覧表_AJS",        "AJS_SCORE_LIST_A4"),
            ("AJS",        "決勝",   "決勝入賞者名簿",        "FINAL_AWARD_LIST_A4"),
        };

        private void RefreshLayoutTab()
        {
            Dispatcher.Invoke(() =>
            {
                string appDefault = AppSettings.Instance.PrintSettings.DefaultPrinterName;
                TxtDefaultPrinter.Text = !string.IsNullOrWhiteSpace(appDefault)
                    ? appDefault
                    : (_printService?.PrinterController.GetDefaultPrinterName() ?? string.Empty);

                // ヘッダーTextBox が生成済みなら初期化してラベルを反映する
                if (_roleLabelBoxes.Length == 0)
                    InitRoleLabelBoxes();
                LoadRoleLabels();

                RefreshCopiesGrid();
            });
        }

        private void RefreshCopiesGrid()
        {
            _copiesRows.Clear();
            var saved = AppSettings.Instance.PrintCopyRows;
            string? prevSm = null;

            foreach (var (sm, rn, name, lid) in _masterRows)
            {
                var savedRow = saved.FirstOrDefault(r =>
                    r.採点方式 == sm && r.ラウンド == rn &&
                    r.帳票名 == name && r.LayoutId == lid);

                _copiesRows.Add(new CopiesGridRow
                {
                    採点方式       = sm,
                    ラウンド       = rn,
                    帳票名         = name,
                    LayoutId       = lid,
                    IsFirstInGroup = (sm != prevSm),
                    司会     = savedRow?.司会     ?? 0,
                    掲示     = savedRow?.掲示     ?? 0,
                    選手係   = savedRow?.選手係   ?? 0,
                    本部     = savedRow?.本部     ?? 0,
                    ステマネ = savedRow?.ステマネ ?? 0,
                    賞状     = savedRow?.賞状     ?? 0,
                    保存     = savedRow?.保存     ?? 0,
                    GM       = savedRow?.GM       ?? 0,
                    レフリー = savedRow?.レフリー ?? 0,
                    予備1    = savedRow?.予備1    ?? 0,
                    予備2    = savedRow?.予備2    ?? 0,
                });
                prevSm = sm;
            }

            CopiesGrid.ItemsSource = _copiesRows;
        }

        private void CopiesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool hasRow = CopiesGrid.SelectedItem is CopiesGridRow;
            BtnChangeLayoutPrinter.IsEnabled = hasRow;
            BtnTestPrint.IsEnabled = hasRow;
            BtnTestPreview.IsEnabled = hasRow;
            BtnOpenDesigner.IsEnabled = hasRow;
        }

        private void BtnSaveCopies_Click(object sender, RoutedEventArgs e)
        {
            CopiesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

            // ヘッダーラベルを設定に反映してから保存
            ApplyRoleLabelsToSettings();

            AppSettings.Instance.PrintCopyRows = _copiesRows
                .Select(r => new PrintCopyRow
                {
                    採点方式  = r.採点方式,
                    ラウンド  = r.ラウンド,
                    帳票名    = r.帳票名,
                    LayoutId  = r.LayoutId,
                    司会     = r.司会,
                    掲示     = r.掲示,
                    選手係   = r.選手係,
                    本部     = r.本部,
                    ステマネ = r.ステマネ,
                    賞状     = r.賞状,
                    保存     = r.保存,
                    GM       = r.GM,
                    レフリー = r.レフリー,
                    予備1    = r.予備1,
                    予備2    = r.予備2,
                })
                .ToList();

            AppSettings.Save();
            AddLog("印刷部数設定保存完了");
            MessageBox.Show("印刷部数設定を保存しました。", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private LayoutSetting? GetLayoutSettingForSelected()
        {
            if (CopiesGrid.SelectedItem is not CopiesGridRow row) return null;
            var layouts = _printService != null
                ? _printService.Registry.All.ToList()
                : AppSettings.Instance.Layouts;
            return layouts.FirstOrDefault(l =>
                string.Equals(l.LayoutId, row.LayoutId, StringComparison.OrdinalIgnoreCase));
        }

        private void BtnOpenDesigner_Click(object sender, RoutedEventArgs e)
        {
            var layout = GetLayoutSettingForSelected();
            if (layout == null) return;

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
                    FileName = frxPath,
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

        private void BtnChangeDefaultPrinter_Click(object sender, RoutedEventArgs e)
        {
            var selected = ShowPrinterSelectDialog("デフォルトプリンターを選択してください");
            if (selected == null) return;

            AppSettings.Instance.PrintSettings.DefaultPrinterName = selected;
            AppSettings.Save();
            RefreshLayoutTab();
            AddLog($"デフォルトプリンター変更: {selected}");
        }

        private void BtnChangeLayoutPrinter_Click(object sender, RoutedEventArgs e)
        {
            var layout = GetLayoutSettingForSelected();
            if (layout == null) return;

            var selected = ShowPrinterSelectDialog($"「{layout.LayoutId}」のプリンターを選択してください");
            if (selected == null) return;

            _printService?.Registry.UpdatePrinterName(layout.LayoutId, selected);

            var appLayout = AppSettings.Instance.Layouts
                .Find(l => string.Equals(l.LayoutId, layout.LayoutId, StringComparison.OrdinalIgnoreCase));
            if (appLayout != null)
                appLayout.PrinterName = selected;
            AppSettings.Save();

            RefreshLayoutTab();
            AddLog($"プリンター変更: layoutId={layout.LayoutId}, printer={selected}");
        }

        private string? ShowPrinterSelectDialog(string description)
        {
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
                FileName = reportsDir,
                UseShellExecute = true
            });
        }

        // ─── テスト印刷・プレビュー ──────────────────────────────────

        private async void BtnTestPrint_Click(object sender, RoutedEventArgs e)
        {
            var layout = GetLayoutSettingForSelected();
            if (layout == null) return;
            await RunTestJobAsync(layout, previewOnly: false);
        }

        private async void BtnTestPreview_Click(object sender, RoutedEventArgs e)
        {
            var layout = GetLayoutSettingForSelected();
            if (layout == null) return;
            await RunTestJobAsync(layout, previewOnly: true);
        }

        private async Task RunTestJobAsync(LayoutSetting layout, bool previewOnly)
        {
            LOG_C log;
            DSPrt.Data.DataManager dm;
            PrintService svc;

            if (_printService != null && _client != null)
            {
                log = _client.Log;
                dm = _client.DataManager;
                svc = _printService;
            }
            else
            {
                log = new LOG_C();
                log.SetLogLevel(AppSettings.Instance.LogSettings.LogLevel);
                log.CreateFile(AppSettings.Instance.LogSettings.LogPath);
                log.LogOutput += AddLog;
                dm = new DSPrt.Data.DataManager(log);
                svc = new PrintService(log, dm);
            }

            try
            {
                BtnTestPrint.IsEnabled = false;
                BtnTestPreview.IsEnabled = false;

                string testDataDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Reports", "TestData"));

                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = $"テストデータを選択 — {layout.LayoutId}",
                    InitialDirectory = System.IO.Directory.Exists(testDataDir) ? testDataDir : null,
                    Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
                    FileName = $"PR_PRINT_{layout.LayoutId}_テスト.json"
                };

                if (dlg.ShowDialog() != true)
                {
                    AddLog("テスト印刷: キャンセルされました");
                    return;
                }

                string jsonText = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonText);
                var root = jsonDoc.RootElement;

                string jobId = root.TryGetProperty("jobId", out var jid) ? jid.GetString() ?? $"TEST_{DateTime.Now:yyyyMMddHHmmss}" : $"TEST_{DateTime.Now:yyyyMMddHHmmss}";
                string layoutId = root.TryGetProperty("layoutId", out var lid) ? lid.GetString() ?? layout.LayoutId : layout.LayoutId;
                int copies = previewOnly ? 0 : (root.TryGetProperty("copies", out var cpy) ? cpy.GetInt32() : 1);

                System.Text.Json.Nodes.JsonNode? dataNode = null;
                if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    dataNode = System.Text.Json.Nodes.JsonNode.Parse(dataProp.GetRawText());
                }

                bool needsDaMaster = layout.DataType.Contains("DA_Master", StringComparison.OrdinalIgnoreCase);
                bool needsDsStatus = layout.DataType.Contains("DS_Status", StringComparison.OrdinalIgnoreCase);

                if ((needsDaMaster && dm.DA_Master == null) || (needsDsStatus && dm.DS_Status == null))
                {
                    if (needsDaMaster && dm.DA_Master == null)
                    {
                        var daDlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "DA_Master.json を選択してください",
                            InitialDirectory = System.IO.Path.GetDirectoryName(dlg.FileName),
                            Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
                            FileName = "DA_Master.json"
                        };
                        if (daDlg.ShowDialog() == true)
                        {
                            dm.SetDA_Master(System.IO.File.ReadAllText(daDlg.FileName, System.Text.Encoding.UTF8));
                            AddLog($"DA_Master ロード: {daDlg.FileName}");
                        }
                    }

                    if (needsDsStatus && dm.DS_Status == null)
                    {
                        var dsDlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "DS_Status.json を選択してください",
                            InitialDirectory = System.IO.Path.GetDirectoryName(dlg.FileName),
                            Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
                            FileName = "DS_Status.json"
                        };
                        if (dsDlg.ShowDialog() == true)
                        {
                            dm.SetDS_Status(System.IO.File.ReadAllText(dsDlg.FileName, System.Text.Encoding.UTF8));
                            AddLog($"DS_Status ロード: {dsDlg.FileName}");
                        }
                    }
                }

                var job = new PrintJob
                {
                    JobId = jobId,
                    LayoutId = layoutId,
                    Copies = copies,
                    Priority = 1,
                    Data = dataNode
                };

                if (previewOnly)
                {
                    AddLog($"プレビュー生成開始: jobId={jobId}, layoutId={layoutId}");
                    string? htmlPath = await svc.ExportHtmlAsync(job, PreviewSpoolDir);
                    if (htmlPath != null)
                    {
                        string absoluteHtmlPath = System.IO.Path.GetFullPath(htmlPath);
                        _lastHtmlPath = absoluteHtmlPath;
                        BtnOpenHtml.IsEnabled = true;
                        PreviewBrowser.Navigate(new Uri(absoluteHtmlPath, UriKind.Absolute));
                        AddLog($"プレビュー完了: {absoluteHtmlPath}");
                        if (MainTabControl.SelectedIndex != 3)
                            MainTabControl.SelectedIndex = 3;
                    }
                }
                else
                {
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
                bool hasSel = CopiesGrid.SelectedItem is CopiesGridRow;
                BtnTestPrint.IsEnabled = hasSel;
                BtnTestPreview.IsEnabled = hasSel;
                BtnChangeLayoutPrinter.IsEnabled = hasSel;

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

        // ─── 賞状タブ ────────────────────────────────────────────────

        private void InitializeAwardTab()
        {
            _updatingAwardUi = true;
            CmbAwardPaperSize.ItemsSource = Enum.GetValues(typeof(AwardPaperSize)).Cast<AwardPaperSize>().Select(CreateOption).ToList();
            CmbAwardOrientation.ItemsSource = Enum.GetValues(typeof(AwardOrientation)).Cast<AwardOrientation>().Select(CreateOption).ToList();
            CmbAwardNamePrintMode.ItemsSource = Enum.GetValues(typeof(AwardNamePrintMode)).Cast<AwardNamePrintMode>().Select(CreateOption).ToList();
            CmbAwardSemiFinalRankMode.ItemsSource = Enum.GetValues(typeof(AwardSemiFinalRankMode)).Cast<AwardSemiFinalRankMode>().Select(CreateOption).ToList();
            CmbAwardTextAlign.ItemsSource = Enum.GetValues(typeof(AwardTextAlign)).Cast<AwardTextAlign>().Select(CreateOption).ToList();
            CmbAwardOverflowMode.ItemsSource = Enum.GetValues(typeof(AwardOverflowMode)).Cast<AwardOverflowMode>().Select(CreateOption).ToList();
            CmbAwardPaperSize.DisplayMemberPath = nameof(AwardOption<AwardPaperSize>.Label);
            CmbAwardOrientation.DisplayMemberPath = nameof(AwardOption<AwardOrientation>.Label);
            CmbAwardNamePrintMode.DisplayMemberPath = nameof(AwardOption<AwardNamePrintMode>.Label);
            CmbAwardSemiFinalRankMode.DisplayMemberPath = nameof(AwardOption<AwardSemiFinalRankMode>.Label);
            CmbAwardTextAlign.DisplayMemberPath = nameof(AwardOption<AwardTextAlign>.Label);
            CmbAwardOverflowMode.DisplayMemberPath = nameof(AwardOption<AwardOverflowMode>.Label);
            AwardRankTextGrid.ItemsSource = _awardRankRows;
            LoadAwardLayout();
            _updatingAwardUi = false;
        }

        private void LoadAwardLayout(string? path = null)
        {
            var repository = _printService?.AwardService.Repository ?? new AwardLayoutRepository();
            if (!string.IsNullOrWhiteSpace(path))
            {
                _awardLayout = repository.Load(path);
                _awardLayoutPath = path;
            }
            else if (!string.IsNullOrWhiteSpace(AppSettings.Instance.PrintSettings.AwardLayoutPath))
            {
                string resolved = repository.ResolvePath(AppSettings.Instance.PrintSettings.AwardLayoutPath);
                if (System.IO.File.Exists(resolved))
                {
                    _awardLayout = repository.Load(resolved);
                    _awardLayoutPath = resolved;
                }
                else
                {
                    _awardLayout = new AwardLayout();
                    _awardLayoutPath = null;
                }
            }
            else
            {
                _awardLayout = new AwardLayout();
                _awardLayoutPath = null;
            }

            RefreshAwardUi();
        }

        private void RefreshAwardUi()
        {
            _updatingAwardUi = true;
            TxtAwardLayoutName.Text = _awardLayout.LayoutName;
            SelectAwardOption(CmbAwardPaperSize, _awardLayout.PaperSize);
            SelectAwardOption(CmbAwardOrientation, _awardLayout.Orientation);
            TxtAwardMeshSize.Text = _awardLayout.MeshSizeMm.ToString();
            SelectAwardOption(CmbAwardNamePrintMode, _awardLayout.NamePrintMode);
            ChkAwardSurnameSingle.IsChecked = _awardLayout.SurnameSinglePrintEnabled;
            TxtAwardMinimumFontSize.Text = _awardLayout.MinimumAutoFontSizePt.ToString("0.##");
            SelectAwardOption(CmbAwardSemiFinalRankMode, _awardLayout.SemiFinalRankMode);
            TxtAwardSemiFinalText.Text = _awardLayout.SemiFinalText;
            LstAwardItems.ItemsSource = _awardLayout.Items;
            if (LstAwardItems.SelectedItem == null && _awardLayout.Items.Count > 0)
                LstAwardItems.SelectedIndex = 0;
            TxtAwardLayoutPath.Text = _awardLayoutPath ?? "未保存";
            RefreshAwardRankRows();
            UpdateAwardItemEditor();
            RenderAwardPreview();
            _updatingAwardUi = false;
        }

        private void UpdateAwardItemEditor()
        {
            if (LstAwardItems.SelectedItem is not AwardLayoutItem item)
            {
                ChkAwardItemEnabled.IsChecked = false;
                TxtAwardItemText.Text = string.Empty;
                TxtAwardX.Text = string.Empty;
                TxtAwardY.Text = string.Empty;
                TxtAwardWidth.Text = string.Empty;
                TxtAwardFontSize.Text = string.Empty;
                return;
            }

            ChkAwardItemEnabled.IsChecked = item.Enabled;
            ChkAwardItemCentered.IsChecked = item.Centered;
            ChkAwardItemBold.IsChecked = item.Bold;
            SelectAwardOption(CmbAwardTextAlign, item.TextAlign);
            SelectAwardOption(CmbAwardOverflowMode, item.OverflowMode);
            TxtAwardItemText.Text = item.Text;
            TxtAwardX.Text = item.XMm.ToString("0.##");
            TxtAwardY.Text = item.YMm.ToString("0.##");
            TxtAwardWidth.Text = item.WidthMm.ToString("0.##");
            TxtAwardFontFamily.Text = item.FontFamily;
            TxtAwardFontSize.Text = item.FontSizePt.ToString("0.##");
            TxtAwardLineSpacing.Text = item.LineSpacingMm.ToString("0.##");
        }

        private void RenderAwardPreview()
        {
            try
            {
                var service = _printService?.AwardService ?? CreateStandaloneAwardService();
                AwardPreviewHost.Content = service.BuildPreview(_awardLayout);
            }
            catch (Exception ex)
            {
                AwardPreviewHost.Content = new TextBlock
                {
                    Text = $"プレビュー表示エラー: {ex.Message}",
                    Foreground = Brushes.DarkRed,
                    Margin = new Thickness(12),
                    TextWrapping = TextWrapping.Wrap
                };
                AddLog($"賞状プレビューエラー: {ex.Message}");
            }
        }

        private void BtnAwardLoad_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "賞状レイアウトを読み込む",
                Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            LoadAwardLayout(dlg.FileName);
            AddLog($"賞状レイアウト読込: {dlg.FileName}");
        }

        private void BtnAwardSave_Click(object sender, RoutedEventArgs e)
        {
            SaveAwardLayout(_awardLayoutPath);
        }

        private void BtnAwardSaveAs_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "賞状レイアウトを保存",
                Filter = "JSONファイル (*.json)|*.json|すべてのファイル (*.*)|*.*",
                FileName = _awardLayout.LayoutName + ".json"
            };
            if (dlg.ShowDialog() != true) return;
            SaveAwardLayout(dlg.FileName);
        }

        private void SaveAwardLayout(string? path)
        {
            try
            {
                var service = _printService?.AwardService ?? CreateStandaloneAwardService();
                string saved = service.SaveLayout(_awardLayout, path);
                _awardLayoutPath = saved;
                TxtAwardLayoutPath.Text = saved;
                AddLog($"賞状レイアウト保存: {saved}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存に失敗しました。\n{ex.Message}", "賞状保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAwardTestPrint_Click(object sender, RoutedEventArgs e)
        {
            if (_printService == null)
            {
                MessageBox.Show("先に接続してから実行してください。", "未接続", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = ShowPrinterSelectDialog("賞状テスト印刷用のプリンターを選択してください");
            if (selected == null) return;

            try
            {
                _printService.AwardService.PrintTest(_awardLayout, selected);
                AddLog($"賞状テスト印刷完了: printer={selected}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"賞状テスト印刷に失敗しました。\n{ex.Message}", "賞状テスト印刷エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LstAwardItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingAwardUi) return;
            _updatingAwardUi = true;
            UpdateAwardItemEditor();
            _updatingAwardUi = false;
        }

        private void AwardSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingAwardUi) return;
            ApplyAwardUiToModel();
            RenderAwardPreview();
        }

        private void ApplyAwardUiToModel()
        {
            _awardLayout.LayoutName = TxtAwardLayoutName.Text.Trim();
            if (TryGetAwardOptionValue(CmbAwardPaperSize, out AwardPaperSize paperSize))
                _awardLayout.PaperSize = paperSize;
            if (TryGetAwardOptionValue(CmbAwardOrientation, out AwardOrientation orientation))
                _awardLayout.Orientation = orientation;
            _awardLayout.MeshSizeMm = ParseIntOrDefault(TxtAwardMeshSize.Text, _awardLayout.MeshSizeMm);
            if (TryGetAwardOptionValue(CmbAwardNamePrintMode, out AwardNamePrintMode namePrintMode))
                _awardLayout.NamePrintMode = namePrintMode;
            _awardLayout.SurnameSinglePrintEnabled = ChkAwardSurnameSingle.IsChecked == true;
            _awardLayout.MinimumAutoFontSizePt = ParseDoubleOrDefault(TxtAwardMinimumFontSize.Text, _awardLayout.MinimumAutoFontSizePt);
            if (TryGetAwardOptionValue(CmbAwardSemiFinalRankMode, out AwardSemiFinalRankMode semiFinalRankMode))
                _awardLayout.SemiFinalRankMode = semiFinalRankMode;
            _awardLayout.SemiFinalText = TxtAwardSemiFinalText.Text;

            if (LstAwardItems.SelectedItem is not AwardLayoutItem item) return;
            item.Enabled = ChkAwardItemEnabled.IsChecked == true;
            item.Centered = ChkAwardItemCentered.IsChecked == true;
            item.Bold = ChkAwardItemBold.IsChecked == true;
            if (TryGetAwardOptionValue(CmbAwardTextAlign, out AwardTextAlign textAlign))
                item.TextAlign = textAlign;
            if (TryGetAwardOptionValue(CmbAwardOverflowMode, out AwardOverflowMode overflowMode))
                item.OverflowMode = overflowMode;
            item.Text = TxtAwardItemText.Text;
            item.XMm = ParseDoubleOrDefault(TxtAwardX.Text, item.XMm);
            item.YMm = ParseDoubleOrDefault(TxtAwardY.Text, item.YMm);
            item.WidthMm = ParseDoubleOrDefault(TxtAwardWidth.Text, item.WidthMm);
            item.FontFamily = string.IsNullOrWhiteSpace(TxtAwardFontFamily.Text) ? item.FontFamily : TxtAwardFontFamily.Text.Trim();
            item.FontSizePt = ParseDoubleOrDefault(TxtAwardFontSize.Text, item.FontSizePt);
            item.LineSpacingMm = ParseDoubleOrDefault(TxtAwardLineSpacing.Text, item.LineSpacingMm);
        }

        private void RefreshAwardRankRows()
        {
            _awardRankRows.Clear();
            for (int rank = 1; rank <= 16; rank++)
            {
                string key = rank.ToString();
                _awardLayout.RankTexts.TryGetValue(key, out var text);
                _awardRankRows.Add(new AwardRankRow(key, text ?? $"第{rank}位"));
            }
        }

        private static AwardService CreateStandaloneAwardService()
        {
            var log = new LOG_C();
            return new AwardService(log, new DSPrt.Data.DataManager(log), new PrinterController(log));
        }

        private void BtnSelectAwardFont_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FontSelectDialog(TxtAwardFontFamily.Text) { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedFontFamily))
                return;

            TxtAwardFontFamily.Text = dialog.SelectedFontFamily;
            ApplyAwardUiToModel();
            RenderAwardPreview();
        }

        private static AwardOption<T> CreateOption<T>(T value) where T : struct, Enum
        {
            return new AwardOption<T>(value, GetLabel(value));
        }

        private static void SelectAwardOption<T>(ComboBox comboBox, T value) where T : struct, Enum
        {
            comboBox.SelectedItem = comboBox.Items.OfType<AwardOption<T>>().FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, value));
        }

        private static bool TryGetAwardOptionValue<T>(ComboBox comboBox, out T value) where T : struct, Enum
        {
            if (comboBox.SelectedItem is AwardOption<T> option)
            {
                value = option.Value;
                return true;
            }

            value = default;
            return false;
        }

        private void AwardRankTextGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Row.Item is not AwardRankRow row || e.EditingElement is not TextBox textBox) return;
            _awardLayout.RankTexts[row.Rank] = textBox.Text;
            RenderAwardPreview();
        }

        private static double ParseDoubleOrDefault(string text, double defaultValue)
        {
            return double.TryParse(text, out var value) ? value : defaultValue;
        }

        private static int ParseIntOrDefault(string text, int defaultValue)
        {
            return int.TryParse(text, out var value) ? value : defaultValue;
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
                StatusIndicator.Fill = Brushes.Orange;
                TxtConnectionStatus.Text = "接続中...";
                BtnConnect.IsEnabled = false;
                BtnDisconnect.IsEnabled = false;
                return;
            }

            if (connected == true)
            {
                StatusIndicator.Fill = Brushes.LimeGreen;
                TxtConnectionStatus.Text = "接続済み";
                BtnConnect.IsEnabled = false;
                BtnDisconnect.IsEnabled = true;
            }
            else
            {
                StatusIndicator.Fill = Brushes.Gray;
                TxtConnectionStatus.Text = "未接続";
                BtnConnect.IsEnabled = true;
                BtnDisconnect.IsEnabled = false;
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
        public string JobId { get; set; }
        public string LayoutId { get; set; }
        public int Copies { get; set; }
        public PrintJobStatus Status { get; set; }
        public DateTime ReceivedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public string StatusText => Status switch
        {
            PrintJobStatus.Queued => "待機",
            PrintJobStatus.Processing => "印刷中",
            PrintJobStatus.Done => "完了",
            PrintJobStatus.Error => "エラー",
            PrintJobStatus.Cancelled => "キャンセル",
            _ => Status.ToString()
        };

        public PrintJob OriginalJob { get; }

        public JobLogItem(PrintJob job)
        {
            OriginalJob = job;
            JobId = job.JobId;
            LayoutId = job.LayoutId;
            Copies = job.Copies;
            Status = job.Status;
            ReceivedAt = job.ReceivedAt;
            CompletedAt = job.CompletedAt;
            ErrorMessage = job.ErrorMessage;
        }
    }

    public sealed class AwardRankRow : INotifyPropertyChanged
    {
        private string _text;

        public AwardRankRow(string rank, string text)
        {
            Rank = rank;
            _text = text;
        }

        public string Rank { get; }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }


    public sealed class AwardItemTypeLabelConverter : System.Windows.Data.IValueConverter
    {
        public static AwardItemTypeLabelConverter Instance { get; } = new AwardItemTypeLabelConverter();

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value is AwardItemType itemType ? GetLabel(itemType) : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class AwardOption<T> where T : struct, Enum
    {
        public AwardOption(T value, string label)
        {
            Value = value;
            Label = label;
        }

        public T Value { get; }
        public string Label { get; }
    }

}
