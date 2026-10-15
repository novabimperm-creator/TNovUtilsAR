using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace WindowEditor.RevitSide
{
    /// <summary>Настройки надстройки: %AppData%\WindowEditor\settings.json</summary>
    public static class Settings
    {
        class Data
        {
            /// <summary>Шаблон окна WE_Окно.rfa (до шаблонов-семейств здесь был .rft — такой путь игнорируется).</summary>
            public string WindowTemplate { get; set; }
            public string GenericTemplate { get; set; }
        }

        static string FilePath => Path.Combine(WindowParams.AppDataDir(), "settings.json");

        static Data Load()
        {
            try { return File.Exists(FilePath) ? JsonConvert.DeserializeObject<Data>(File.ReadAllText(FilePath)) ?? new Data() : new Data(); }
            catch { return new Data(); }
        }

        static void Save(Data d) =>
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(d, Formatting.Indented));

        /// <summary>
        /// Шаблон окна WE_Окно.rfa (см. TEMPLATE.md). Если не задан — берётся %AppData%\WindowEditor\WE_Окно.rfa,
        /// иначе спрашивает и запоминает.
        /// </summary>
        public static string GetTemplate()
        {
            var d = Load();
            if (IsFamily(d.WindowTemplate) && File.Exists(d.WindowTemplate)) return d.WindowTemplate;

            var local = Path.Combine(WindowParams.AppDataDir(), "WE_Окно.rfa");
            var path = File.Exists(local)
                ? local
                : Ask("Шаблон окна WindowEditor (WE_Окно.rfa)", WindowParams.AppDataDir(), "Шаблон окна (*.rfa)|*.rfa");
            if (path == null) return null;
            d.WindowTemplate = path; Save(d);
            return path;
        }

        static bool IsFamily(string path) =>
            !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase);

        public static void ResetTemplate()
        {
            var d = Load(); d.WindowTemplate = null; Save(d);
        }

        /// <summary>Шаблон «Типовая модель» для вложенного семейства. Ищется сам, иначе спрашивает.</summary>
        public static string GetGenericTemplate(string defaultDir)
        {
            var d = Load();
            if (!string.IsNullOrEmpty(d.GenericTemplate) && File.Exists(d.GenericTemplate)) return d.GenericTemplate;

            string found = null;
            try
            {
                if (Directory.Exists(defaultDir))
                {
                    string[] bad = { "грань", "face", "стен", "wall", "лини", "line", "перекрыт", "floor", "ceiling", "потол",
                                     "крыш", "roof", "адаптив", "adaptive", "узор", "pattern", "двух", "two", "вращ", "rotat" };
                    found = Directory.GetFiles(defaultDir, "*.rft", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                            return (n.Contains("типовая модель") || n.Contains("generic model")) && !bad.Any(n.Contains);
                        })
                        .OrderBy(f => Path.GetFileName(f).Length)
                        .FirstOrDefault();
                }
            }
            catch { /* нет доступа к папке — спросим */ }

            found = found ?? Ask("Шаблон «Типовая модель» (например, «Метрическая система, типовая модель.rft»)", defaultDir);
            if (found == null) return null;
            d.GenericTemplate = found; Save(d);
            return found;
        }

        static string Ask(string title, string defaultDir, string filter = "Шаблон семейства (*.rft)|*.rft")
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                InitialDirectory = Directory.Exists(defaultDir) ? defaultDir : ""
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }
    }
}
