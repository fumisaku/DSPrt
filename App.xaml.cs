using System.Threading;
using System.Windows;

namespace DSPrt
{
    /// <summary>
    /// App.xaml の相互作用ロジック
    /// --config &lt;filepath&gt; 引数対応、Mutex による二重起動禁止
    /// </summary>
    public partial class App : Application
    {
        private Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // --config 引数を解析
            string configPath = "DSPrt.json";
            for (int i = 0; i < e.Args.Length - 1; i++)
            {
                if (e.Args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
                {
                    configPath = e.Args[i + 1];
                    break;
                }
            }

            // 設定ファイルを読み込み（Mutex キー取得のため先に実行）
            AppSettings.Load(configPath);

            // Mutex による二重起動禁止
            var instanceId = AppSettings.Instance.WebSocketSettings.InstanceId;
            string mutexName = $"Global\\DSPrt_{instanceId}";

            _mutex = new Mutex(true, mutexName, out bool isNew);
            if (!isNew)
            {
                MessageBox.Show(
                    $"DSPrt インスタンス '{instanceId}' はすでに起動しています。",
                    "DSPrt",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // StartupUri の代わりに MainWindow を明示的に生成・表示する
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
