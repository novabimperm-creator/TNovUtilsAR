using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;
using Parameter = Autodesk.Revit.DB.Parameter;

namespace TNovUtilsAR
{
    /// <summary>
    /// Марки дверей: на каждую дверь (внутреннюю и наружную) ставится марка
    /// pmN.Марка_Дверь, тип «Маркировка типоразмера_План», по центру двери.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoDoorTags : IExternalCommand
    {
        // Метка сборки. Если её нет в заголовке окна — Revit грузит старый DLL.
        private const string BUILD = "двери v2 (без проёмов)";

        // Семейство и тип марки двери.
        private const string TAG_FAMILY = "pmN.Марка_Дверь";
        private const string TAG_TYPE = "Маркировка типоразмера_План";
        // Исключаемые семейства (дверные проёмы): по подстроке в имени семейства.
        private const string EXCLUDE_FAMILY_SUBSTR = "проем";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                View view = doc.ActiveView;
                var report = new StringBuilder();
                report.AppendLine($"[{BUILD}]  Вид: {view.Name}, масштаб 1:{view.Scale}");
                if (RevitAPI.UiApplication == null) RevitAPI.Initialize(commandData);
                string _ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
                TNovConfigLoad.LoadConfig("Марки дверей", _ver);
                Logger.Initialize("Марки дверей", DateTime.Now, _ver);

                if (!(view is ViewPlan))
                {
                    TaskDialog.Show($"Марки дверей [{BUILD}]", "Команда работает только на планах.");
                    return Result.Failed;
                }

                // ----- Тип марки двери -----
                FamilySymbol tagType = FindTagType(doc, TAG_FAMILY, TAG_TYPE);
                if (tagType == null)
                {
                    TaskDialog.Show($"Марки дверей [{BUILD}]",
                        $"Не найдено семейство марки двери \"{TAG_FAMILY}\".\n" +
                        "Загрузите семейство в проект и повторите.");
                    return Result.Failed;
                }

                // ----- Двери в виде: только родительские (без SuperComponent),
                //       и внутренние, и наружные -----
                var all = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(fi => fi.Location is LocationPoint)
                    .ToList();
                var doors = all
                    .Where(fi => fi.SuperComponent == null)
                    .Where(fi => (fi.Symbol?.FamilyName ?? "")
                        .IndexOf(EXCLUDE_FAMILY_SUBSTR, StringComparison.OrdinalIgnoreCase) < 0)
                    .ToList();
                report.AppendLine($"Дверей в виде: {all.Count}, из них родительских (без проёмов): {doors.Count}");
                if (doors.Count == 0)
                {
                    TaskDialog.Show($"Марки дверей [{BUILD}]", report.ToString());
                    return Result.Succeeded;
                }

                // ----- Уже помеченные двери (не дублируем) -----
                var tagged = ExistingTaggedDoors(doc, view);

                int placed = 0, failed = 0, skipped = 0;

                using (Transaction tx = new Transaction(doc, "Марки дверей"))
                {
                    tx.Start();
                    if (!tagType.IsActive) tagType.Activate();

                    foreach (FamilyInstance dr in doors)
                    {
                        if (tagged.Contains(dr.Id)) { skipped++; continue; }
                        if (PlaceDoorTag(doc, view, tagType, dr)) placed++;
                        else failed++;
                    }

                    tx.Commit();
                }

                report.AppendLine($"\nИТОГО: марок дверей {placed}, не удалось {failed}, " +
                    $"пропущено (уже есть) {skipped}");
                TaskDialog.Show($"Марки дверей [{BUILD}]", report.ToString());
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Logger.Log("Ошибка: " + ex.Message, 4);
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Ставит марку двери по центру двери (без выноски, горизонтально).</summary>
        private static bool PlaceDoorTag(Document doc, View view, FamilySymbol tagType, FamilyInstance dr)
        {
            try
            {
                XYZ loc = ((LocationPoint)dr.Location).Point;
                XYZ head = new XYZ(loc.X, loc.Y, loc.Z);

                IndependentTag tag = IndependentTag.Create(
                    doc, tagType.Id, view.Id, new Reference(dr),
                    false, TagOrientation.Horizontal, head);
                if (tag == null) return false;
                try { tag.TagHeadPosition = head; } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Id дверей, у которых уже есть марка двери в этом виде.</summary>
        private static HashSet<ElementId> ExistingTaggedDoors(Document doc, View view)
        {
            var set = new HashSet<ElementId>();
            foreach (IndependentTag t in new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_DoorTags)
                .WhereElementIsNotElementType()
                .OfType<IndependentTag>())
            {
                foreach (ElementId id in t.GetTaggedLocalElementIds())
                    set.Add(id);
            }
            return set;
        }

        private static FamilySymbol FindTagType(Document doc, string family, string type)
        {
            var syms = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_DoorTags)
                .OfType<FamilySymbol>()
                .Where(t => t.FamilyName == family)
                .ToList();
            // точный тип, иначе — любой тип этого семейства
            return syms.FirstOrDefault(t => t.Name == type) ?? syms.FirstOrDefault();
        }
    }
}
