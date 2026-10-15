using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WindowEditor.Model;
using WindowEditor.UI;

namespace WindowEditor.RevitSide
{
    /// <summary>Редактировать окно: выбранный экземпляр → его семейство.</summary>
    [Transaction(TransactionMode.Manual)]
    public class EditWindowCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc.Document;

            var inst = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<FamilyInstance>()
                .FirstOrDefault(IsWindow);
            if (inst == null)
            {
                try
                {
                    var r = uidoc.Selection.PickObject(ObjectType.Element, new WindowFilter(), "Выберите окно");
                    inst = doc.GetElement(r) as FamilyInstance;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }

            var symbol = inst.Symbol;
            var family = symbol.Family;
            if (!family.IsEditable) { message = "Семейство не редактируется (системное или защищённое)."; return Result.Failed; }

            var cfg = WindowConfig.FromJson(symbol.LookupParameter(WindowParams.Config)?.AsString());
            bool legacy = cfg == null;
            if (legacy)
            {
                var td = new TaskDialog("Редактор окна")
                {
                    MainInstruction = "Окно создано не редактором",
                    MainContent = "Семейство будет пересоздано из шаблона WE_Окно по схеме редактора, своя геометрия семейства пропадёт. " +
                                  "Лучше создать новое окно командой «Новое окно» и заменить им старое.\n\nПродолжить?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };
                if (td.Show() != TaskDialogResult.Yes) return Result.Cancelled;
                cfg = Presets.Create(0);
                cfg.Sections[0].Width = WindowParams.SymbolDim(symbol, true) - 2 * cfg.GapSide;
                cfg.Sections[0].Height = WindowParams.SymbolDim(symbol, false) - cfg.GapTop - cfg.Sections[0].GapBottom;
            }

            // отзеркаленные экземпляры этого семейства
            bool? extPos = legacy ? (bool?)null : cfg.ExteriorPositiveY;
            var issues = MirrorCheck.Find(doc, MirrorCheck.InstancesOf(doc, family), _ => extPos);
            var choice = MirrorCheck.Report(uidoc, issues, $"Семейство «{family.Name}»", allowContinue: true);
            if (choice == MirrorCheck.Choice.Selected || choice == MirrorCheck.Choice.Cancel) return Result.Cancelled;

            SyncWithHostWall(inst, cfg, legacy);
            ReadMaterialsBack(doc, symbol, cfg);

            if (family.GetFamilySymbolIds().Count > 1)
                TaskDialog.Show("Редактор окна",
                    $"В семействе «{family.Name}» несколько типов. Редактор ведёт правило «одно окно — одно семейство — один тип» " +
                    "(имя «имя_ШхВ»): в семействе останется только тип выбранного окна, геометрия изменится у всех его типов. " +
                    "Для отдельного окна включите «Сохранить как новое семейство».");

            var win = new EditorWindow(cfg, family.Name, canSaveAsNew: true, materials: ProjectMaterials(doc));
            new WindowInteropHelper(win).Owner = data.Application.MainWindowHandle;
            if (win.ShowDialog() != true) return Result.Cancelled;

            // имя семейства и типа — «имя_ШхВ»: смена размера переименовывает это же семейство,
            // новое семейство — только если изменилось само имя или включено «Сохранить как новое»
            string name = win.FamilyName;
            bool asNew = win.SaveAsNew || Mirror.StripSize(name) != Mirror.StripSize(family.Name);
            var taken = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name == name && f.Id != family.Id);
            if (taken != null)
            {
                message = $"Семейство «{name}» уже есть в проекте — окно с таким именем и размером уже создано. Переименуйте окно в редакторе.";
                return Result.Failed;
            }
            try
            {
                var app = data.Application.Application;
                var famDoc = doc.EditFamily(family);
                string saveAs = asNew ? name : null;
                if (!asNew && symbol.Name != name)
                {
                    // типы при перезагрузке сопоставляются по имени — переименовываем тип заранее,
                    // иначе экземпляры остались бы на старом типе со старыми значениями
                    using (var t = new Transaction(doc, "Имя типа окна"))
                    {
                        t.Start();
                        symbol.Name = name;
                        t.Commit();
                    }
                }
                if (TemplateBuilder.Missing(famDoc).Count > 0)
                {
                    // семейство не из шаблона WE_Окно (старая генерация или чужое) — пересоздаём из шаблона с тем же именем
                    famDoc.Close(false);
                    var template = Settings.GetTemplate();
                    if (template == null) return Result.Cancelled;
                    famDoc = TemplateBuilder.OpenCopy(app, template);
                    saveAs = saveAs ?? family.Name;
                }
                var newSymbol = FamilyLoader.BuildAndLoad(app, doc, famDoc, win.Config, name, saveAs);

                if (newSymbol == null) throw new InvalidOperationException($"После загрузки не найден тип «{name}».");
                if (asNew) TrySwapType(doc, inst, newSymbol);
                // семейство после перезагрузки — новый объект: берём его у типа, а не прежний family
                var fam = newSymbol.Family;
                if (!asNew && fam.Name != name)
                {
                    using (var t = new Transaction(doc, "Имя семейства окна"))
                    {
                        t.Start();
                        fam.Name = name;
                        t.Commit();
                    }
                }

                // комплектация в модели — под новую схему у всех экземпляров семейства
                var report = Accessories.Update(doc, Accessories.WindowsOf(doc, fam), removeOrphans: false);
                if (report.Problems.Count > 0) TaskDialog.Show("Комплектация окон", report.ToString());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Толщина стены и сторона фасада — по стене, в которой стоит окно.</summary>
        static void SyncWithHostWall(FamilyInstance inst, WindowConfig cfg, bool legacy)
        {
            if (!(inst.Host is Wall wall)) return;
            double t = Math.Round(WindowParams.Mm(wall.Width));
            if (Math.Abs(t - cfg.WallThickness) > 0.5)
            {
                var td = new TaskDialog("Редактор окна")
                {
                    MainInstruction = "Толщина стены отличается",
                    MainContent = $"Стена: {t:0} мм, в настройках окна: {cfg.WallThickness:0} мм. Взять толщину стены?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };
                if (td.Show() == TaskDialogResult.Yes) cfg.WallThickness = t;
            }

            if (legacy)
            {
                // Для нового окна сторону фасада определяем по стене; у окон редактора не трогаем — её мог поправить пользователь.
                cfg.ExteriorPositiveY = inst.FacingOrientation.DotProduct(wall.Orientation) > 0;

                // Четверть по умолчанию — толщина наружного слоя, если слоёв больше одного
                var cs = wall.WallType.GetCompoundStructure();
                if (cs != null && cs.LayerCount > 1)
                {
                    double outer = WindowParams.Mm(cs.GetLayerWidth(0));
                    if (outer >= 50 && outer < t / 2) { cfg.QuarterDepth = Math.Round(outer); cfg.QuarterSide = 65; cfg.QuarterTop = 65; }
                }
            }
        }

        /// <summary>Материалы, изменённые в проекте через параметры типа WE_Мат.*, возвращаем в конфигурацию.</summary>
        static void ReadMaterialsBack(Document doc, FamilySymbol symbol, WindowConfig cfg)
        {
            var geo = new WindowGeometry(cfg);
            foreach (var slot in geo.Materials)
            {
                var p = symbol.LookupParameter(slot.Param);
                if (p == null || p.StorageType != StorageType.ElementId) continue;
                if (doc.GetElement(p.AsElementId()) is Material m && m.Name != slot.Get()) slot.Set(m.Name);
            }
        }

        internal static List<string> ProjectMaterials(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Material)).Select(m => m.Name).Distinct().OrderBy(n => n).ToList();

        static void TrySwapType(Document doc, FamilyInstance inst, FamilySymbol newSymbol)
        {
            if (inst.GroupId != ElementId.InvalidElementId)
            {
                TaskDialog.Show("Редактор окна",
                    $"Создано семейство «{newSymbol.FamilyName}». Выбранное окно входит в группу, поэтому его тип не заменён — " +
                    "замените его в режиме редактирования группы (изменение применится ко всем экземплярам группы).");
                return;
            }
            using (var t = new Transaction(doc, "Замена типа окна"))
            {
                t.Start();
                if (!newSymbol.IsActive) newSymbol.Activate();
                inst.Symbol = newSymbol;
                t.Commit();
            }
        }

        /// <summary>Окно (категория «Окна»), но не элемент комплектации «WE_Элемент окна» — он той же категории.</summary>
        internal static bool IsWindow(Element e) =>
            e?.Category != null && e.Category.Id.IntegerValueCompat() == (long)BuiltInCategory.OST_Windows
            && !(e is FamilyInstance fi && fi.Symbol.FamilyName == AccessoryFamily.Name);
    }

    /// <summary>Проверка всей модели: отзеркаленные окна и окна наружной стороной внутрь.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CheckWindowsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc.Document;
            var windows = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(EditWindowCommand.IsWindow).ToList();
            var issues = MirrorCheck.Find(doc, windows, MirrorCheck.ExteriorResolver());
            if (issues.Count == 0)
            {
                TaskDialog.Show("Редактор окна", $"Проверено окон: {windows.Count}. Отзеркаленных и развёрнутых нет.");
                return Result.Succeeded;
            }
            MirrorCheck.Report(uidoc, issues, "Модель", allowContinue: false);
            return Result.Succeeded;
        }
    }

    /// <summary>Новое окно из шаблона семейства окна.</summary>
    [Transaction(TransactionMode.Manual)]
    public class NewWindowCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var app = uiapp.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            string template = Settings.GetTemplate();
            if (template == null) return Result.Cancelled;

            var win = new EditorWindow(Presets.Create(Presets.IndexOf("v3 12")), "Окно 12", canSaveAsNew: false, materials: EditWindowCommand.ProjectMaterials(doc));
            new WindowInteropHelper(win).Owner = uiapp.MainWindowHandle;
            if (win.ShowDialog() != true) return Result.Cancelled;

            FamilySymbol symbol;
            try
            {
                Document famDoc;
                try { famDoc = TemplateBuilder.OpenCopy(app, template); }
                catch (InvalidOperationException)
                {
                    Settings.ResetTemplate(); // в следующий раз спросим другой файл
                    throw;
                }
                // одно семейство — один тип, имя типа = имя семейства «имя_ШхВ»
                var name = win.FamilyName;
                if (new FilteredElementCollector(doc).OfClass(typeof(Family)).Any(f => f.Name == name))
                {
                    famDoc.Close(false);
                    message = $"Семейство «{name}» уже есть в проекте. Измените имя окна или отредактируйте существующее.";
                    return Result.Failed;
                }
                symbol = FamilyLoader.BuildAndLoad(app, doc, famDoc, win.Config, name, name);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            // Сразу предлагаем разместить окно в стене
            if (symbol != null)
            {
                try { uidoc.PostRequestForElementTypePlacement(symbol); }
                catch { /* разместить можно вручную из Диспетчера проекта */ }
            }
            return Result.Succeeded;
        }
    }

    /// <summary>Генерация + загрузка в проект с перезаписью. Экземпляры в группах обновляются автоматически.</summary>
    public static class FamilyLoader
    {
        /// <param name="saveAsName">null — перезаписать то же семейство; иначе — сохранить под новым именем.</param>
        public static FamilySymbol BuildAndLoad(Autodesk.Revit.ApplicationServices.Application app, Document projectDoc,
            Document famDoc, WindowConfig cfg, string typeName, string saveAsName)
        {
            Family loaded;
            try
            {
                // семейство от шаблона первой версии (база пустот 10 м) — на ближнюю базу; иначе ничего не делает
                TemplateBuilder.Rebase(famDoc);
                // комплектация — отдельными элементами в модели (Accessories), не вложенными семействами
                new WindowFamilyBuilder(app, famDoc, cfg).Build(typeName);

                if (saveAsName == null)
                {
                    loaded = famDoc.LoadFamily(projectDoc, new OverwriteLoadOptions());
                }
                else
                {
                    var dir = Path.Combine(Path.GetTempPath(), "WindowEditor");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, saveAsName + ".rfa");
                    famDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                    famDoc.Close(false);
                    famDoc = null;
                    using (var t = new Transaction(projectDoc, "Загрузка окна"))
                    {
                        t.Start();
                        projectDoc.LoadFamily(path, new OverwriteLoadOptions(), out loaded);
                        t.Commit();
                    }
                }
            }
            finally
            {
                if (famDoc != null && famDoc.IsValidObject) famDoc.Close(false);
            }

            if (loaded == null) return null;
            string familyName = loaded.Name; // после перезагрузки в WarmUpHostCut объект loaded недействителен
            WarmUpHostCut(projectDoc, familyName);
            return new FilteredElementCollector(projectDoc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.FamilyName == familyName && s.Name == typeName);
        }

        /// <summary>
        /// Особенность Revit (проверено в 2022): пустоты, вырезанные «Вырезать» в семействе на основе стены, режут
        /// стену-основу в проекте только после того, как семейство перезагрузили, пока в проекте стоял хотя бы один
        /// его экземпляр. Без этого первое окно в уже существующей стене стену не режет. Поэтому после загрузки ставим
        /// временное окно во временную стену, перезагружаем семейство и всё временное удаляем — дальше любые
        /// экземпляры режут любые стены. Вызывать вне транзакций.
        /// </summary>
        static void WarmUpHostCut(Document doc, string familyName)
        {
            Family Fam() => new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().First(f => f.Name == familyName);

            using (var tg = new TransactionGroup(doc, "Окно: подготовка выреза стены"))
            {
                tg.Start();
                ElementId wallId, instId;
                using (var t = new Transaction(doc, "Временное окно"))
                {
                    t.Start();
                    var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
                    var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(w => w.Kind == WallKind.Basic);
                    // далеко от модели, с запасом под любое окно
                    var p0 = new XYZ(WindowParams.Ft(-500000), WindowParams.Ft(-500000), level.Elevation);
                    var wall = Wall.Create(doc, Line.CreateBound(p0, p0 + XYZ.BasisX * WindowParams.Ft(20000)), wallType.Id, level.Id,
                        WindowParams.Ft(10000), 0, false, false);
                    doc.Regenerate();
                    var sym = (FamilySymbol)doc.GetElement(Fam().GetFamilySymbolIds().First());
                    if (!sym.IsActive) sym.Activate();
                    var inst = doc.Create.NewFamilyInstance(p0 + XYZ.BasisX * WindowParams.Ft(10000), sym, wall, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    wallId = wall.Id; instId = inst.Id;
                    t.Commit();
                }

                // перезагрузка «из редактора семейств»: документ помечаем изменённым, иначе Revit может её пропустить
                var famDoc = doc.EditFamily(Fam());
                try
                {
                    using (var t = new Transaction(famDoc, "Перезагрузка"))
                    {
                        t.Start();
                        var fm = famDoc.FamilyManager;
                        var p = fm.get_Parameter(WindowParams.GenVoids);
                        if (p != null) { fm.Set(p, "1"); fm.Set(p, ""); }
                        t.Commit();
                    }
                    famDoc.LoadFamily(doc, new OverwriteLoadOptions());
                }
                finally
                {
                    famDoc.Close(false);
                }

                using (var t = new Transaction(doc, "Удалить временное окно"))
                {
                    t.Start();
                    doc.Delete(new List<ElementId> { instId, wallId });
                    t.Commit();
                }
                tg.Assimilate();
            }
        }
    }

    public class OverwriteLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true; // значения типа задал редактор
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;  // вложенный «WE_Элемент окна» — из семейства окна
            overwriteParameterValues = true;
            return true;
        }
    }

    class WindowFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is FamilyInstance && EditWindowCommand.IsWindow(e);
        public bool AllowReference(Reference r, XYZ p) => false;
    }

    static class IdCompat
    {
        // ElementId.IntegerValue устарел в 2024+, Value появился в 2024.
        public static long IntegerValueCompat(this ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId ToElementId(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }
}
