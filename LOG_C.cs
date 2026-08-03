using System;
using System.IO;

namespace DSPrt
{
    /// <summary>
    /// ログ出力クラス（DSDsp から流用）
    /// </summary>
    public class LOG_C
    {
        private string ON_OFF_Flag;
        private string LOG_Path;
        private string LOG_Filename;
        private int LOG_Level;

        // ログレベル定数
        public int ERR = 1;
        public int WARNING = 2;
        public int INFO = 3;
        public int DEBUG = 4;
        public int DEB_Detail = 5;

        // ログ出力イベント（UIに表示する場合に使用）
        public event Action<string>? LogOutput;

        public LOG_C()
        {
            LOG_Level = 3;
            ON_OFF_Flag = "OFF";
            LOG_Path = string.Empty;
            LOG_Filename = string.Empty;
        }

        public void SetLogLevel(int Level)
        {
            LOG_Level = Level;
        }

        public string CreateFile(string? logPath = null)
        {
            ON_OFF_Flag = "ON";

            if (string.IsNullOrEmpty(logPath))
            {
                LOG_Path = Directory.GetCurrentDirectory();
            }
            else
            {
                LOG_Path = logPath;
                if (!Directory.Exists(LOG_Path))
                    Directory.CreateDirectory(LOG_Path);
            }

            LOG_Filename = Path.Combine(LOG_Path, $"LOG{DateTime.Now:yyyyMMddHHmmss}.log");
            LogAdd("=== ログファイル作成 ===", INFO);
            return LOG_Filename;
        }

        public void LogAdd(string cmt, int Level)
        {
            if (ON_OFF_Flag != "ON") return;
            if (Level > LOG_Level) return;

            try
            {
                string levelStr = Level switch
                {
                    1 => "ERR",
                    2 => "WARN",
                    3 => "INFO",
                    4 => "DEBUG",
                    5 => "DETAIL",
                    _ => "UNKNOWN"
                };

                string logMessage = $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} [{levelStr}] {cmt}";

                using (var writer = new StreamWriter(LOG_Filename, true, System.Text.Encoding.UTF8))
                    writer.WriteLine(logMessage);

                string shortMessage = $"{DateTime.Now:HH:mm:ss} {cmt}";
                LogOutput?.Invoke(shortMessage);

                System.Diagnostics.Debug.WriteLine(logMessage);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ログ書き込みエラー: {ex.Message}");
            }
        }

        public void Set_ON() => ON_OFF_Flag = "ON";
        public void Set_OFF() => ON_OFF_Flag = "OFF";
        public string GetLogFilePath() => LOG_Filename;
    }
}
