using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Interop;
using TNovCommon;

namespace TNovUtilsAR
{
    [Transaction(TransactionMode.Manual)]
    public class CopyWindows : IExternalCommand
    {
        // Списки исключаемых подстрок (регистронезависимые)
        private static readonly string[] ExcludedWindowSubstrings =
            { "Подоконник", "Отлив", "Створка", "Откос" };
        private static readonly string[] ExcludedDoorSubstrings =
            { "полотно", "ручка" };
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            
            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Проемщик";
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
            string docName = doc.Title.ToString(); docName = docName.Replace(",", " ");
            string userName = rvtApp.Username; userName = userName.Replace(",", "");
            string docNameUserName = "_" + userName; docName = docName.Replace(docNameUserName, "");
            docName = docName.Replace(",", "");
            #endregion

            TNovConfig config = TNovConfigLoad.LoadConfig(DBCommandName, TNovVersion);

            #region Настройки логов
            // создание log - файла
            Logger.Initialize(DBCommandName, dateTime, TNovVersion);

            var viewModel0 = new AppVersionViewModel();

            string jsonpath0 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TNovClient/TNovSettings.json");
            viewModel0 = JsonConvert.DeserializeObject<AppVersionViewModel>(File.ReadAllText(jsonpath0));
            if (viewModel0.extendedLogs)

            {
                var qViewModel = new QuestionWindowViewModel();
                qViewModel.headtxt = "Включены расширенные логи. " +
                    "Плагин будет работать медленнее, но соберет больше данных. " +
                    "Выключить расширенные логи для ускорения работы?";
                var qwpfview = new QuestionWindow280(qViewModel);
                qViewModel.CloseRequest += (s, e) => qwpfview.Close();
                bool? qok = qwpfview.ShowDialog();
                if (qok != null && qok == true) { Logger.TurnOffExtendedLogs(); } else Logger.Log("Расширенные логи вкл", 2);
            }
            #endregion


            //параметры
            Guid adskWidthParamGuid = new Guid("8f2e4f93-9472-4941-a65d-0ac468fd6a5d");//ADSK_Размер_Ширина
            Guid adskHeightParamGuid = new Guid("da753fe3-ecfa-465b-9a2c-02f55d0c2ff1");//ADSK_Размер_Высота
            Guid NBottomGapParamGuid = new Guid("dad2af7b-d5ca-4d85-9edb-40949bfe968e");//N_Зазор.Снизу

            #region Сбор элементов
            Logger.Log("Сбор элементов", 1);

            // 1. Собираем все связанные модели
            List<RevitLinkInstance> linkInstances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            // 2. Отбираем те, в имени файла которых есть "_АР"
            var targetLinks = new List<RevitLinkInstance>();
            foreach (RevitLinkInstance link in linkInstances)
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc != null)
                {
                    string path = linkDoc.PathName;
                    if (string.IsNullOrEmpty(path)) continue;
                    string fileName = Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrEmpty(fileName) && fileName.Contains("_АР"))
                    {
                        targetLinks.Add(link);
                    }
                }
            }

            if (targetLinks.Count == 0)
            {
                Logger.Log("Связанная модель АР не найдена. Завершение работы.", 3);
                new InfoWindow280("Не найдено ни одной связанной модели с именем, содержащим '_АР'.").ShowDialog();
                return Result.Failed;
            }

            Logger.Log("Сбор данных", 1);
            // 3. Собираем данные из всех таких связей
            List<ItemData> allItems = new List<ItemData>();

            foreach (RevitLinkInstance link in targetLinks)
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;

                Logger.Log($"Модель {linkDoc.Title}", 1);

                // Окна
                FilteredElementCollector windows = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType();

                // Двери
                FilteredElementCollector doors = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType();

                List<Element> openings = windows.Cast<Element>().Concat(doors.Cast<Element>()).ToList();

                foreach (Element elem in openings)
                {
                    LocationPoint loc = elem.Location as LocationPoint;
                    if (loc == null) continue;

                    string category = elem.Category.Name;
                    Element type = elem.Document.GetElement(elem.GetTypeId());
                    string typeName = type?.Name ?? "Без типа";

                    

                    // Определяем, какой набор исключений применять в зависимости от категории
                    bool exclude = false;
                    if (category == "Окна")
                    {
                        exclude = ExcludedWindowSubstrings.Any(s =>
                            typeName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    else if (category == "Двери")
                    {
                        exclude = ExcludedDoorSubstrings.Any(s =>
                            typeName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    if (exclude) continue; // пропускаем нежелательные типы

                    XYZ point = loc.Point;

                    double height = 0, width = 0;


                    if (type != null)
                    {
                        //для дверей поднимаем точку расположения на зазор снизу
                        if (category == "Двери")
                        {
                            double bottomGap = Param.GetDoubleParamValue(doc, NBottomGapParamGuid, elem);
                            double newZ = point.Z + bottomGap;
                            XYZ newPoint = new XYZ(point.X, point.Y, newZ);
                            point = newPoint;
                        }

                        //получаем ширину без четвертей
                        
                        width = Param.GetDoubleParamValue(doc, adskWidthParamGuid, elem);
                        double leftW = 0;
                        if (Param.ParamExist("Четверть.Слева", type))
                        {
                            leftW = type.LookupParameter("Четверть.Слева").AsDouble();
                        }
                        double leftR = 0;
                        if (Param.ParamExist("Четверть.Справа", type))
                        {
                            leftR = type.LookupParameter("Четверть.Справа").AsDouble();
                        }
                        width = width - leftW - leftR;
                        

                        //получаем высоту без четвертей
                        
                        height = Param.GetDoubleParamValue(doc, adskHeightParamGuid, elem);

                        double leftW1 = 0;
                        if (Param.ParamExist("Четверть.Сверху", type))
                        {
                            leftW1 = type.LookupParameter("Четверть.Сверху").AsDouble();
                        }
                        double leftR1 = 0;
                        if (Param.ParamExist("Четверть.Снизу", type))
                        {
                            leftR1 = type.LookupParameter("Четверть.Снизу").AsDouble();
                        }
                        height = height - leftW1 - leftR1;
                        
                    }

                    


                    // Определение толщины стены-хозяина, нормали и признака переворота
                    double wallThickness = 0;
                    XYZ outerNormal = null;
                    bool isFlipped = false;

                    if (elem is FamilyInstance fi && fi.Host != null && fi.Host is Wall hostWall)
                    {
                        Parameter thicknessParam = hostWall.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
                        if (thicknessParam != null && thicknessParam.HasValue)
                            wallThickness = thicknessParam.AsDouble();

                        outerNormal = hostWall.Orientation;
                        isFlipped = hostWall.Flipped;
                    }

                    allItems.Add(new ItemData
                    {
                        OriginalId = elem.Id,
                        Category = category,
                        TypeName = typeName,
                        Point = point,
                        Rotation = loc.Rotation,
                        Height = height,
                        Width = width,
                        WallThickness = wallThickness,
                        OuterNormal = outerNormal,
                        IsWallFlipped = isFlipped,
                        LinkDocumentPath = linkDoc.PathName
                    });
                    Logger.Log("   Добавлен элемент "+elem.Id.IntegerValue.ToString(), 2);
                }
            }

            if (allItems.Count == 0)
            {
                Logger.Log("В связанных моделях не найдено ни одного окна или двери, удовлетворяющих фильтру. Завершение работы.", 3);
                new InfoWindow280("В связанных моделях не найдено ни одного окна или двери, удовлетворяющих фильтру.").ShowDialog();
                return Result.Failed;
            }

            #endregion

            // Создаём обработчик и внешнее событие
            CreateInstancesHandler handler = new CreateInstancesHandler();
            ExternalEvent exEvent = ExternalEvent.Create(handler);

            Logger.Log("Открываем окно", 1);
            // Открываем немодальное окно
            CopyWindowsWPF window = new CopyWindowsWPF(allItems, uiApp, handler, exEvent);
            IntPtr revitHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            WindowInteropHelper helper = new WindowInteropHelper(window);
            helper.Owner = revitHandle;
            window.Show();

            Logger.Log("Завершение работы.", 5);
            return Result.Succeeded;
        }
    }
    public class ItemData
    {
        public ElementId OriginalId { get; set; }
        public string Category { get; set; }
        public string TypeName { get; set; }
        public XYZ Point { get; set; }
        public double Rotation { get; set; }
        public double Height { get; set; }
        public double Width { get; set; }
        public double WallThickness { get; set; }
        public XYZ OuterNormal { get; set; }   // новое поле – вектор наружу
        public string LinkDocumentPath { get; set; }

        // ... существующие поля ...
        public bool IsWallFlipped { get; set; } // true, если стена перевернута (внешняя сторона – противоположна Orientation)

    }
}
