using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Documents;
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
            string[] rolesFile = TNovCommon.Server.ServerData.ReadAllLines("roles.txt");
            foreach (string role in rolesFile)
            {
                if (role.Contains(userName))
                {
                    string[] line = role.Split(','); userDepartment = line[1]; userDepRole = line[2]; break;
                }

            }

            Guid NLevelNumberParamGuid = LevelNumberParam.Guid; //N_Эт.Номер

            
            #region Возможные неправильные имена уровней
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

            #region Сбор элементов
            Logger.Log("Сбор элементов", 1);
            List<Element> elems = LevelNumberElements.Collect(doc);

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

            #endregion

            #region Выборка
            List<ElementId> selectedIds = new List<ElementId>();
            //анализ текущей выборки
            Logger.Log("Анализ текущей выборки", 1);
            Autodesk.Revit.UI.Selection.Selection selection = commandData.Application.ActiveUIDocument.Selection;
            ICollection<ElementId> preselectedIds = selection.GetElementIds();
            if (preselectedIds.Count > 0)
            {
                foreach (ElementId id in preselectedIds) { selectedIds.Add(id); }
            }
            else  //запускаем выбор элементов если ничего не выбрано
            {
                if (viewModel.selected)
                {
                    Selection elemselection = uidoc.Selection;

                    List<Element> selectedElements = null;


                    try
                    {
                        selectedElements = elemselection.PickElementsByRectangle("Выберите элементы при помощи рамки (Esc - отмена)").ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException e)
                    {
                        Logger.Log("Запуск отменен пользователем. Завершение работы: " + e.Message, 3);
                        return Result.Cancelled;
                    }
                    foreach (Element element in selectedElements) selectedIds.Add(element.Id);
                }
            }
            //итоговый список
            if (viewModel.selected)
            {
                List<Element> elems1 = new List<Element>();
                foreach(var id in selectedIds) 
                {
                    Element el = doc.GetElement(id); if(el != null) elems1.Add(el);
                }
                elems = elems1;
            }
            List<TNovElement> elemsToWork = new List<TNovElement>(); int allcount = 0;
            foreach (var elem in elems)
            {
                TNovElement tNovElem = new TNovElement(elem);
                if (tNovElem.TNovCategory == "Default") continue;
                switch (tNovElem.TNovCategory)
                {
                    case "Wall":
                        if (viewModel.walls) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "FamilyInstance_Wall":
                        if (viewModel.walls) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "Floor":
                        if (viewModel.floors) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "FamilyInstance_Floor":
                        if (viewModel.floors) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "Ceiling":
                        if (viewModel.ceilings) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "FamilyInstance_Ceiling":
                        if (viewModel.ceilings) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "FamilyInstance_DoorWindow":
                        if(viewModel.instances) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "Room":
                        if (viewModel.rooms) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "FamilyInstance_Parking":
                        if (viewModel.park) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "FamilyInstance_Other":
                    case "Stairs":
                    case "Railing":
                        if (viewModel.other) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "FamilyInstance_Hole":
                        if (viewModel.holes) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                    case "FamilyInstance_Beam":
                        if (viewModel.beams) { allcount++; elemsToWork.Add(tNovElem); }
                        ; break;
                }
                
            }
            #endregion


            int failscount = 0;
            List<string> failed = new List<string>(); //id элементов, которым не удалось назначить параметр
            List<string> byGeometry = new List<string>(); //id элементов, уровень которых определен по отметке
            LevelResolver resolver = new LevelResolver(doc, new LevelNumberSettings
            {
                useGeometry = viewModel.useGeometry,
                nearTolerance = viewModel.nearTolerance,
                maxOffset = viewModel.maxOffset,
                offsetCheckFromFloor = viewModel.offsetCheckFromFloor
            });


            bool unhandledError = false;
            #region Основной код
            using (Transaction transaction = new Transaction(doc))
            {
                try
                {
                    transaction.Start("TNov - Эт.Номер");
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

                    foreach(var tNovElem in elemsToWork) //новое: универсальный цикл по всем элементам
                    {
                        Element elem = doc.GetElement(tNovElem.elem.Id);
#if R2022
                    long idint =  elem.Id.IntegerValue;
#else
                        long idint = elem.Id.Value;
#endif
                        Logger.Log(idint.ToString(), 2);

                        //определение уровня и заполнение параметра
                        LevelResolveResult resolved = resolver.Resolve(elem);
                        if (resolved.Level != null)
                        {
                            if (resolved.ByGeometry)
                            {
                                byGeometry.Add(elem.Id.ToString());
                                Logger.Log(elem.Id.ToString() + " - уровень определен по отметке: " + resolved.Info, 3);
                            }
                            else if (resolved.Source == LevelSource.Fallback)
                                Logger.Log(elem.Id.ToString() + " - уровень из запасного параметра: " + resolved.Info, 2);

                            SetLevelParam(elem.Id, resolved.Level, NLevelNumberParamGuid, out bool success);
                            if (!success)
                            {
                                failed.Add(elem.Id.ToString()); failscount++;
                            }
                        }
                        else
                        {
                            failed.Add(elem.Id.ToString()); failscount++;
                            Logger.Log(elem.Id.ToString() + " - " + resolved.Info, 4);
                        }

                        PBCount++;
                        this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<double>((Func<double>)(() => this.levnumProgressBar.TNov_ProgressBar.Value = (double)PBCount));
                        this.levnumProgressBar.TNov_ProgressBar.Dispatcher.Invoke<string>((Func<string>)(() => this.levnumProgressBar.value.Text = PBCount.ToString()));
                    }
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
                if (byGeometry.Count != 0)
                {
                    Logger.Log("Открываем окно с ID элементов, уровень которых определен по отметке: " + String.Join(",", byGeometry), 1);
                    ElementsTreeWindow window = new ElementsTreeWindow(uiApp, String.Join(",", byGeometry), DBCommandName, dateTime, TNovVersion,
                        "Уровень определен по отметке");
                    window.Show();
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

        private void SetLevelParam(ElementId elemid, Level level, in Guid param1, out bool success)
        {

            string eid = elemid.ToString();
            Element elem = RevitAPI.Document.GetElement(elemid);
            Logger.Log("   Элемент " + eid + ":", 2);
            double num = LevelNumberParam.Encode(LevelResolver.ParseLevelNumber(level.Name));

            success = false;

            if (Param.ParamExistByGuid(param1, elem))
            {
                try
                {
                    elem.get_Parameter(param1)?.Set(num);
                    success = true;
                    Logger.Log("   назначено " + num.ToString(), 2);
                }
                catch (Exception ex)
                {
                    Logger.Log("Элемент " + eid + " Ошибка:" + ex.Message, 4);
                }
            }


        }
    }
    
}
