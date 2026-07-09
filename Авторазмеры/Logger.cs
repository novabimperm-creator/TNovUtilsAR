using System;
using System.IO;
using Autodesk.Revit.DB;

namespace GridDimensionTool
{
    /// <summary>
    /// Логгер по образцу TNovCommon.Logger: пишет в
    /// %UserProfile%/TNovClient/logs/log,&lt;дата&gt;,&lt;юзер&gt;,&lt;документ&gt;,&lt;класс&gt;,&lt;версия&gt;.txt.
    /// Уровни: 0 START, 1 INFO, 2 TECH, 3 BREAK, 4 ERROR, 5 END.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logFilePath;
        private static bool _extendedLogs = true;

        /// <summary>Инициализация: формирует путь к лог-файлу и пишет стартовую строку.</summary>
        public static void Initialize(Document doc, string userName, string className, string version)
        {
            try
            {
                string date = DateTime.Now.ToString().Replace(":", "-").Replace(",", " ");
                string docName = (doc?.Title ?? "нет-документа").Replace(",", " ");
                if (!string.IsNullOrEmpty(userName))
                    docName = docName.Replace("_" + userName, "");
                userName = (userName ?? "нет-юзера").Replace(",", "");

                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "TNovClient", "logs");
                Directory.CreateDirectory(folder);

                _logFilePath = Path.Combine(folder,
                    $"log,{date},{userName},{docName},{className},{version}.txt");

                Log("Логгер инициализирован", 0);
            }
            catch { _logFilePath = null; }
        }

        /// <summary>
        /// Запись сообщения: 0 START, 1 INFO, 2 TECH, 3 BREAK, 4 ERROR, 5 END.
        /// Уровень 2 (TECH) пишется только при включённых расширенных логах.
        /// </summary>
        public static void Log(string message, int level)
        {
            if (_logFilePath == null) return;

            string levelStr;
            switch (level)
            {
                case 0: levelStr = "START"; break;
                case 1: levelStr = "INFO"; break;
                case 2: levelStr = "TECH"; break;
                case 3: levelStr = "BREAK"; break;
                case 4: levelStr = "ERROR"; break;
                case 5: levelStr = "END"; break;
                default: levelStr = "TECH"; break;
            }

            lock (_lock)
            {
                try
                {
                    if (level == 2 && !_extendedLogs) return;
                    string entry = $"{DateTime.Now:HH:mm:ss} [{levelStr}] {message}";
                    File.AppendAllText(_logFilePath, entry + Environment.NewLine);
                }
                catch { /* ошибки записи игнорируем */ }
            }
        }

        public static void Log(string message) => Log(message, 1);
    }
}
