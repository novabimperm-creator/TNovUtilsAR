using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TNovCommon;
using Parameter = Autodesk.Revit.DB.Parameter;

namespace TNovUtilsAR
{
    [Transaction(TransactionMode.Manual)]
    public class Mirror : IExternalCommand
    {
        
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            
            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Антизеркало";
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

            #region Сбор элементов

            Logger.Log("Сбор элементов",1);

            BuiltInParameter gm = BuiltInParameter.ALL_MODEL_MODEL; //параметр Группа модели

            BuiltInParameter mrk = BuiltInParameter.ALL_MODEL_MARK; //параметр Марка

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
            
            List<FamilyInstance> elems = new List<FamilyInstance>(); 

            foreach(FamilyInstance f in windows)
            {
                string fvalue = f.Symbol.get_Parameter(gm).AsString();
                Element element = (Element)f;
                if (fvalue != null)
                {
                    if (fvalue.Contains(".")) { }
                    else if (fvalue.Contains("Окно")) { elems.Add(f); }
                }
                if (element.Name.Contains("Витраж")) { elems.Add(f); }
            }
            foreach (FamilyInstance f in doors)
            {
                string fvalue = f.Symbol.get_Parameter(gm).AsString();
                Element element = (Element)f;
                if (fvalue != null)
                {
                    if (fvalue=="Дверь") { elems.Add(f); }
                }
                if (element.Name.Contains("Витраж")) { elems.Add(f); }
            }
            #endregion

            int failscount = 0;
            List<string> failed = new List<string>(); //пустой список id элементов отзеркаленных

            bool unhandledError = false;

            #region Основной код
            using (Transaction transaction = new Transaction(doc))
            {
                try { 
                Logger.Log("Открываем транзакцию",1); 
                transaction.Start("TNov - Антизеркало");
                
                foreach (var elem in elems) 
                {
                    string eid = elem.Id.ToString();
                    bool m = elem.Mirrored; 
                    if (m) {Logger.Log("Элемент " + eid+" отзеркален",1); }
                    Parameter elmrk = elem.get_Parameter(mrk);
                    if (m) { failed.Add(eid); failscount++; elmrk.Set("зеркальный"); }
                    else
                    {
                        string mrkvalue = elem.get_Parameter(mrk).AsValueString();
                        if (mrkvalue != null) 
                        {
                            mrkvalue = mrkvalue.Replace("зеркальный", "");
                            elmrk.Set(mrkvalue);
                        }
                    }
                }
                
                if (failscount == 0) 
                {
                    string info1txt = "Отлично! Отзеркаленные элементы отсутствуют.";
                    var info1 = new InfoWindow400(info1txt); info1.ShowDialog();
                }
                else
                {
                    Logger.Log("Открываем окно с ID проблемных элементов", 1);
                    // Диалоговое окно
                    var viewModel = new InfoWindowTextFieldViewModel();
                    viewModel.headtxt = "Один или несколько элементов отзеркалены:";
                    viewModel.ids = String.Join(",", failed);
                    viewModel.lowtxt = "Необходимо исправить такие элементы в модели.";
                    var wpfview = new InfoWindowTextField(viewModel);
                    bool? ok = wpfview.ShowDialog();
                    Logger.Log(viewModel.ids, 1);
                    Logger.Log("Выделяем проблемные элементы в модели", 1);
                    uidoc.Selection.SetElementIds(failed.Select(s => new ElementId(int.Parse(s))).ToArray());
                }
   
                transaction.Commit();
                Logger.Log("Закрываем транзакцию",1);
                }
                catch (Exception ex)
                {
                    Logger.Log("Ошибка: " + ex.Message, 4);
                    new InfoWindow280("Ошибка: " + ex.Message).ShowDialog();
                    unhandledError = true;
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
    }
    
}
