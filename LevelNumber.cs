using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Threading;
using TNovCommon;

namespace TNovUtilsAR
{
    [Transaction(TransactionMode.Manual)]
    public class LevelNumber : IExternalCommand
    {
        
        
        private TNovProgressBar levnumProgressBar;
        private void ThreadStartingPoint()
        {
            this.levnumProgressBar = new TNovProgressBar();
            this.levnumProgressBar.Show();
            Dispatcher.Run();
        }
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            
            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Эт.Номер";
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


            //имя и роль пользователя
            string userDepartment = "-"; string userDepRole = "-";
            string[] rolesFile = File.ReadAllLines(config.ServerPath+"roles.txt");
            foreach (string role in rolesFile)
            {
                if (role.Contains(userName))
                {
                    string[] line = role.Split(','); userDepartment = line[1]; userDepRole = line[2]; break;
                }

            }

            Guid NLevelNumberParamGuid = new Guid("4d2aa1b8-727c-43a1-8b1e-8c22dd484e11"); //N_Эт.Номер

            #region Сбор элементов
            Logger.Log("Сбор элементов",1);
            List<Level> levels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Levels)   //фильтр по категории Уровни
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<Level>()                     //элементы категории Уровни
                                                                         .ToList();                         //формируем список
            int ec = 0; // счетчик неправильных имен уровней (ec = error counter)
            List<string> wrongnames = new List<string>();

            foreach (Level level in levels)
            {
                string name0 = level.Name.Replace("_", " "); //получаем имя уровня
                int i = 0, count = 0;
                var s = " ";
                while ((i = name0.IndexOf(s, i)) != -1) { ++count; i += s.Length; } //ищем сколько пробелов в имени уровня
                if (count < 2) 
                { 
                    ec = ++ec; //счетчик неправильных имен уровней
                    wrongnames.Add(level.Name);
                } 
            }


            List<Wall> walls = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls)   //фильтр по категории Стены
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .OfClass(typeof(Wall))         //отсеиваем модели в контексте
                                                                         .Cast<Wall>()                     //элементы категории Стены
                                                                         .ToList();                         //формируем список

            List<FamilyInstance> wallsFI = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls)   //Стены семействами
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(FamilyInstance))
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();

            List<Autodesk.Revit.DB.Floor> floors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors)   //фильтр по категории Перекрытия
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(Autodesk.Revit.DB.Floor))
                                                                         .Cast<Autodesk.Revit.DB.Floor>()                     
                                                                         .ToList();

            List<FamilyInstance> floorsFI = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors)   //Плиты (полы) семействами
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(FamilyInstance))
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();

            List<Ceiling> ceilings = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Ceilings)   //фильтр по категории Потолки
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(Ceiling))
                                                                         .Cast<Ceiling>()
                                                                         .ToList();

            List<FamilyInstance> ceilingsFI = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Ceilings)   //Потолки семействами
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(FamilyInstance))
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();

            List<FamilyInstance> windows = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows)   //фильтр по категории Окна
                                                                         .WhereElementIsNotElementType()
                                                                         .Cast<FamilyInstance>()
                                                                         //.Where(it => it.Symbol.get_Parameter(gm).AsString() == "Окно") //только род семейства
                                                                         .ToList();

            List<FamilyInstance> doors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors)   //фильтр по категории Двери
                                                                         .WhereElementIsNotElementType()
                                                                         .Cast<FamilyInstance>()
                                                                         //.Where(it => it.Symbol.get_Parameter(gm).AsString() == "Дверь") //только род семейства
                                                                         .ToList();
            
            List<FamilyInstance> beams = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming)   //фильтр по категории Каркас несущий
                                                                         .WhereElementIsNotElementType()
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();

            List<Room> rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)   //фильтр по категории Помещения
                                                                         .WhereElementIsNotElementType()
                                                                         .Cast<Room>()
                                                                         .ToList();

            List<FamilyInstance> parks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Parking)   //фильтр по категории Парковка
                                                                         .WhereElementIsNotElementType()
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();

            List<FamilyInstance> fur = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Furniture)   //фильтр по категории Мебель
                                                                         .WhereElementIsNotElementType()
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();

            List<FamilyInstance> GMs = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_GenericModel)   //фильтр по категории Об модели
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(FamilyInstance))
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();

            List<Element> obor = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            List<Element> sobor = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_SpecialityEquipment)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            List<Element> Santeh = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            List<Autodesk.Revit.DB.Architecture.Stairs> stairs = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Stairs)   //Лестницы
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(Autodesk.Revit.DB.Architecture.Stairs))  //отсеиваем модели в контексте
                                                                         .Cast<Autodesk.Revit.DB.Architecture.Stairs>()
                                                                         .ToList();
            List<FamilyInstance> stairs2 = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Stairs)   //Лестницы семействами
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(Autodesk.Revit.DB.FamilyInstance))
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();
            List<Autodesk.Revit.DB.Architecture.Railing> railings = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StairsRailing)   //Ограждения
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(Autodesk.Revit.DB.Architecture.Railing)) //отсеиваем модели в контексте
                                                                         .Cast<Autodesk.Revit.DB.Architecture.Railing>()
                                                                         .ToList();
            List<FamilyInstance> railings2 = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StairsRailing)   //Ограждения семействами
                                                                         .WhereElementIsNotElementType()
                                                                         .OfClass(typeof(Autodesk.Revit.DB.FamilyInstance))
                                                                         .Cast<FamilyInstance>()
                                                                         .ToList();

            Logger.Log("Элементы собраны. Создаем списки для работы",1);

            List<FamilyInstance> windowsdoors = new List<FamilyInstance>(windows.Count + doors.Count); //общий список окна двери
            windowsdoors.AddRange(windows);
            windowsdoors.AddRange(doors);

            List<FamilyInstance> beams1 = new List<FamilyInstance>(); //перемычки
            foreach (FamilyInstance elem in beams)
            {
                bool air = elem.Name.Contains("Аэратор");
                if (air == false) { beams1.Add(elem); }
            }

            List<FamilyInstance> GMs1 = new List<FamilyInstance>();
            List<FamilyInstance> holes = new List<FamilyInstance>();
            foreach (FamilyInstance elem in GMs)
            {
                string gmvalue = elem.Symbol.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString();
                bool isHole = false;
                if (gmvalue != null)
                {
                    if (gmvalue.Contains("Отверстие")) { isHole = true; holes.Add(elem); }
                }
                if(!isHole) GMs1.Add(elem);
            }
            #endregion

            #region Диалог
            Logger.Log("Списки собраны. Диалоговое окно",1);
            var viewModel = new LevelNumberViewModel();
            // Десериализация
            bool forProject = true;
            json js = new json(in DBCommandName, in forProject, out bool canserialize, out string jsonpath);
            if (canserialize)
            {
                viewModel = JsonConvert.DeserializeObject<LevelNumberViewModel>(File.ReadAllText(jsonpath));
                Logger.Log("Десериализация прошла успешно",1);
            }
            //Проверка отдела пользователя
            switch (userDepartment)
            {
                case "ST":
                    viewModel.checkBox8islocked = false;
                    break;
                case "BIM":
                    viewModel.checkBox8islocked = false; viewModel.beams = false; viewModel.holes = false;
                    break;
                default:
                    viewModel.checkBox8islocked = true; viewModel.beams = false; viewModel.holes = false;
                    break;
            }
            //Окно
            var wpfview = new LevelNumberWPF(viewModel);
            viewModel.CloseRequest += (s, e) => wpfview.Close();
            bool? ok = wpfview.ShowDialog();
            if (ok != null && ok == true) { }
            else { Logger.Log("Запуск отменен пользователем. Завершение работы.", 3); return Result.Cancelled; }
            //Сериализация
            try
            {
                File.WriteAllText(jsonpath, JsonConvert.SerializeObject(viewModel));
                Logger.Log("Сериализация прошла успешно",1);
            }
            catch (Exception ex) { Logger.Log("Ошибка при сериализации: " + ex.Message,4); }

            bool runWalls = viewModel.walls; bool runFloors = viewModel.floors; bool runCeilings = viewModel.ceilings;
            bool runInstances = viewModel.instances; bool runRooms = viewModel.rooms; bool runPark = viewModel.park; bool runOther = viewModel.other;
            string section = viewModel.section; bool runBeams = viewModel.beams; bool runHoles = viewModel.holes;

            int failscount = 0;
            List<string> failed = new List<string>(); //пустой список id элементов с недоступным параметром Закрепить
            string categories = ":";
            int allcount = 0;
            if (runWalls) { categories = categories + " стены"; allcount += walls.Count+wallsFI.Count; }; 
            if (runFloors) { categories = categories + " перекрытия"; allcount += floors.Count+floorsFI.Count; }; 
            if (runCeilings) { categories = categories + " потолки"; allcount += ceilings.Count+ceilingsFI.Count; }; 
            if (runInstances) { categories = categories + " окна двери"; allcount += windowsdoors.Count; };
            if (runBeams) { categories = categories + " перемычки"; allcount += beams1.Count; };
            if (runRooms) { categories = categories + " помещения"; allcount += rooms.Count; };
            if (runPark) { categories = categories + " паркинг"; allcount += parks.Count; };
            if (runOther) { categories = categories + " прочее"; allcount += fur.Count+ obor.Count+ sobor.Count+ Santeh.Count+ stairs.Count+ stairs2.Count+ railings.Count+ railings2.Count+GMs1.Count; };
            if (runHoles) { categories = categories + " отверстия"; allcount += holes.Count; };
            #endregion

            Logger.Log("Выбор сделан:"+ categories,1);

            #region Возможные неправильные имена уровней
            if (ec > 0)
            {
                string wn = "";
                int i = 0;
                foreach (string wname in wrongnames)
                {
                    if (i == 0) { wn = wn + wname; }
                    else { wn = wn + ", " + wname; }
                    i++;
                }
                //сообщение об ошибке
                string info2txt = "Уровни " + wn + " названы не по регламенту!\r\n" +
                    "Структура наименования имеет вид(с пробелами без нижних подчеркиваний):\r\n" +
                    "АА ББ ВВ, где\r\nАА – код уровня в цифровом формате(-01, 01, 02…);\r\n" +
                    "ББ – отметка уровня от 0.000(например, -3.200 или + 1.500);\r\n" +
                    "ВВ – название уровня(например, Автостоянка, Подвал, Этаж 7, Покрытие).\r\nПример наименования уровня:\r\n" +
                    "\t - 01 - 3.200 Подвал\r\n" +
                    "\t05 + 12.850 Этаж 5\r\n";
                var info2 = new InfoWindow400(info2txt); info2.ShowDialog();
                return Result.Failed;
            }
            #endregion

            bool unhandledError = false;
            #region Основной код
            using (Transaction transaction = new Transaction(doc))
            {
                try
                {
                    transaction.Start("TNov - заполнить Эт.Номер");
                    Logger.Log("Открываем транзакцию", 1);

                    Thread thread = new Thread(new ThreadStart(this.ThreadStartingPoint));
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.IsBackground = true;
                    thread.Start();
                    Thread.Sleep(100);

                    int PBCount = 0;
                    this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Minimum = (double)PBCount));
                    this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));
                    this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Maximum = (double)allcount));
                    this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.maxvalue.Text = allcount.ToString()));

                    if (runWalls && walls.Count > 0)
                    {
                        Logger.Log("Стены:", 1);
                        foreach (var elem in walls) //СТЕНЫ
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                            if (param0 != null)
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runWalls && wallsFI.Count > 0)
                    {
                        Logger.Log("Стены (семейства):", 1);
                        foreach (var elem in wallsFI) //СТЕНЫ семействами
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runFloors && floors.Count > 0)
                    {
                        Logger.Log("Перекрытия:", 1);
                        foreach (var elem in floors) //ПЛИТЫ
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                            if (param0 != null)
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runFloors && floorsFI.Count > 0)
                    {
                        Logger.Log("Перекрытия (семейства):", 1);
                        foreach (var elem in floorsFI) //ПЛИТЫ семействами
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runCeilings && ceilings.Count > 0)
                    {
                        Logger.Log("Перекрытия:", 1);
                        foreach (var elem in ceilings) //ПОТОЛКИ
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                            if (param0 != null)
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runCeilings && ceilingsFI.Count > 0)
                    {
                        Logger.Log("Перекрытия (семейства):", 1);
                        foreach (var elem in ceilingsFI) //ПОТОЛКИ семействами
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runInstances && windowsdoors.Count > 0)
                    {
                        Logger.Log("Окна двери:", 1);
                        foreach (var elem in windowsdoors) //ОКНА ДВЕРИ
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runBeams && beams1.Count > 0)
                    {
                        Logger.Log("Перемычки:", 1);
                        foreach (var elem in beams1) //ПЕРЕМЫЧКИ
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runHoles && holes.Count > 0)
                    {
                        Logger.Log("Отверстия:", 1);
                        foreach (var elem in holes) //ОТВЕРСТИЯ
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runRooms && rooms.Count > 0)
                    {
                        Logger.Log("Помещения:", 1);
                        foreach (var elem in rooms) //ПОМЕЩЕНИЯ
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            double area = elem.get_Parameter(BuiltInParameter.ROOM_AREA).AsDouble();
                            if (area == 0) continue; //не размещено, 06.26

                            Autodesk.Revit.DB.Parameter param0 = elem.get_Parameter(BuiltInParameter.LEVEL_NAME);//получаем параметр "Уровень"
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runPark && parks.Count > 0)
                    {
                        Logger.Log("Парковка:", 1);
                        foreach (var elem in parks) //ПАРКИНГ
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && fur.Count > 0)
                    {
                        Logger.Log("Мебель:", 1);
                        foreach (var elem in fur) //Мебель
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && obor.Count > 0)
                    {
                        Logger.Log("Оборудование:", 1);
                        foreach (var elem in obor) //Оборудование
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && sobor.Count > 0)
                    {
                        Logger.Log("Спецоборудование:", 1);
                        foreach (var elem in sobor) //Спецоборудование
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && Santeh.Count > 0)
                    {
                        Logger.Log("Сантехника:", 1);
                        foreach (var elem in Santeh) //Сантехника 
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && stairs.Count > 0)
                    {
                        Logger.Log("Лестницы:", 1);
                        foreach (var elem in stairs) //Лестницы
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM);//получаем параметр "Нижний уровень"
                            if (param0 != null)
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && stairs2.Count > 0)
                    {
                        Logger.Log("Лестницы семействами:", 1);
                        foreach (var elem in stairs2) //Лестницы семействами
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && railings.Count > 0)
                    {
                        Logger.Log("Ограждения:", 1);
                        foreach (var elem in railings) //Ограждения
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.get_Parameter(BuiltInParameter.STAIRS_RAILING_BASE_LEVEL_PARAM);//получаем параметр "Базовый уровень"
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else if (elem.HasHost)
                            {
                                SetLevelParamByHost(elem, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && railings2.Count > 0)
                    {
                        Logger.Log("Ограждения семействами:", 1);
                        foreach (var elem in railings2) //Ограждения семействами
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    if (runOther && GMs1.Count > 0)
                    {
                        Logger.Log("Обобщенные модели:", 1);
                        foreach (var elem in GMs1) //Обобщенные модели
                        {
                            PBCount++;
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                            this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));

                            Autodesk.Revit.DB.Parameter param0 = elem.LookupParameter("Уровень");
                            if (param0 != null && param0.AsValueString() != "")
                            {
                                SetLevelParam(elem.Id, param0, NLevelNumberParamGuid, out bool success);
                                if (!success) { failed.Add(elem.Id.ToString()); failscount++; }
                            }
                            else { failed.Add(elem.Id.ToString()); failscount++; Logger.Log("      " + elem.Id.ToString() + " ошибка", 4); }
                        }
                    }
                    Logger.Log("Элементы обработаны.", 1);


                    transaction.Commit();
                    Logger.Log("Закрываем транзакцию.", 1);
                }
                catch (Exception ex)
                {
                    Logger.Log("Ошибка: " + ex.Message, 4);
                    new InfoWindow280("Ошибка: " + ex.Message).ShowDialog();
                    unhandledError = true;
                }
                finally
                {
                    CloseProgressBarSafely();
                }
                if (failscount != 0)
                {
                    Logger.Log("Открываем окно с ID проблемных элементов: " + String.Join(",", failed), 1);
                    // Диалоговое окно
                    ElementsTreeWindow window = new ElementsTreeWindow(uiApp, String.Join(",", failed), DBCommandName,dateTime, TNovVersion);
                    window.Show();
                    /*
                    var viewModel2 = new InfoWindowTextFieldViewModel();
                    viewModel2.headtxt = "Один или несколько элементов не изменены:";
                    viewModel2.ids = String.Join(",", failed);
                    viewModel2.lowtxt = "Проверьте их вручную или посмотрите ошибки в лог-файле.";
                    var wpfview2 = new InfoWindowTextField(viewModel2);
                    viewModel2.CloseRequest += (s, e) => wpfview2.Close();
                    bool? ok2 = wpfview2.ShowDialog();*/
                }
            }
            #endregion
            if (unhandledError)
            {
                Logger.Log("Завершение работы с ошибками.", 4);
                return Result.Succeeded;
            }
            Logger.Log("Завершение работы.",5);
            return Result.Succeeded;
        }
        private void CloseProgressBarSafely()
        {
            if (levnumProgressBar != null &&
                levnumProgressBar.Dispatcher != null &&
                !levnumProgressBar.Dispatcher.HasShutdownStarted)
            {
                levnumProgressBar.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (levnumProgressBar.IsLoaded)
                        levnumProgressBar.Close();
                    // Завершаем цикл сообщений диспетчера, чтобы поток завершился
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }));
            }
        }

        private void SetLevelParam(ElementId elemid, in Parameter param0, in Guid param1, out bool success)
        {

            string eid = elemid.ToString();
            Element elem = RevitAPI.Document.GetElement(elemid);
            Logger.Log("   Элемент " + eid + ":", 2);
            string level = param0.AsValueString(); //получаем значение исходного параметра
            level = level.Replace("_", " ");
            string[] parts = level.Split(new char[] { ' ' }); //делим имя пробелами
            level = parts[0];
            if (level.Contains('.'))
            {
                string[] parts2 = level.Split('.');
                level = parts2[0];
            }
            double num = 0;
            Double.TryParse(level, out num);
            num = num / 0.3048 / 0.3048;

            success = false;

            if (Param.ParamExistByGuid(param1, elem))
            {
                try
                {
                    elem.get_Parameter(param1)?.Set(num);
                    success = true;
                    Logger.Log("      назначено " + num.ToString(), 2);
                }
                catch (Exception ex)
                {
                    Logger.Log("   Элемент " + eid + " Ошибка:" + ex.Message, 4);
                }
            }


        }
        private void SetLevelParamByHost(Railing elem, in Guid param1, out bool success)
        {
            Logger.Log("   Элемент " + elem.Id + ":", 2);
            //получаем хост
            Element host = RevitAPI.Document.GetElement(elem.HostId);
            Parameter param0 = null;
            if (host.Category.Id.IntegerValue == -2000011)
            {
                param0 = host.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
            }
            else if (host.Category.Id.IntegerValue == -2000120)
            {
                param0 = host.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM);
            }
            if (param0 != null)
            {
                SetLevelParam(elem.Id, param0, param1, out success);
            }
            else success = false;
        }
    }
    
}
