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
    /// Марки окон: на каждое окно ставится марка pmN.Марка_Окно (показывает параметр
    /// «Маркировка типоразмера»), с зазором 500 мм от окна наружу и поворотом под
    /// ориентацию окна (текст идёт вдоль стены).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoWindowTags : IExternalCommand
    {
        // Метка сборки. Если её нет в заголовке окна — Revit грузит старый DLL.
        private const string BUILD = "окна v7 (витраж только с маркой)";

        // Семейство и тип марки окна.
        private const string TAG_FAMILY = "pmN.Марка_Окно";
        private const string TAG_TYPE = "Маркировка типоразмера";
        // Параметр маркировки типоразмера и требуемая подстрока (только окна «Ок-…»).
        private const string MARK_PARAM = "Маркировка типоразмера";
        private const string MARK_MUST_CONTAIN = "ок";
        // Исключаемые семейства (проёмы): по подстроке в имени семейства.
        private const string EXCLUDE_FAMILY_SUBSTR = "проем";
        // Витражи: марка pmN.Марка_Витраж, отбираются по «витраж» в имени семейства.
        private const string VITRAZH_TAG_FAMILY = "pmN.Марка_Витраж";
        private const string VITRAZH_TAG_TYPE = "Маркировка типоразмера - Витраж.Марка";
        private const string VITRAZH_FAMILY_SUBSTR = "витраж";
        // Витраж без заполненного этого параметра маркой не помечается.
        private const string VITRAZH_MARK_PARAM = "N_Витраж.Марка";
        // Зазор от окна/витража до марки — РЕАЛЬНОЕ расстояние в модели (мм).
        private const double OFFSET_MM_MODEL = 700.0;

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
                TNovConfigLoad.LoadConfig("Марки окон", _ver);
                Logger.Initialize("Марки окон", DateTime.Now, _ver);

                if (!(view is ViewPlan))
                {
                    TaskDialog.Show($"Марки окон [{BUILD}]", "Команда работает только на планах.");
                    return Result.Failed;
                }

                // ----- Типы марок окна и витража (хотя бы один должен найтись) -----
                FamilySymbol tagType = FindTagType(doc, TAG_FAMILY, TAG_TYPE);
                FamilySymbol vitType = FindTagType(doc, VITRAZH_TAG_FAMILY, VITRAZH_TAG_TYPE);
                if (tagType == null && vitType == null)
                {
                    TaskDialog.Show($"Марки окон [{BUILD}]",
                        $"Не найдено ни \"{TAG_FAMILY}\", ни \"{VITRAZH_TAG_FAMILY}\".\n" +
                        "Загрузите семейства марок в проект и повторите.");
                    return Result.Failed;
                }

                // ----- Экземпляры «Окна»: родительские, с точкой вставки -----
                var all = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(fi => fi.Location is LocationPoint)
                    .Where(fi => fi.SuperComponent == null)
                    .ToList();

                // окна «Ок-…» (не проёмы)
                var windows = all
                    .Where(fi => !FamilyContains(fi, EXCLUDE_FAMILY_SUBSTR))
                    .Where(fi => MarkOf(fi).IndexOf(MARK_MUST_CONTAIN, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                // витражи — витражные стены (curtain wall); если есть с «витраж» в имени
                // типа, берём только их, иначе все витражные стены
                var curtain = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .OfType<Wall>()
                    .Where(w => w.CurtainGrid != null && w.Location is LocationCurve)
                    .ToList();
                var named = curtain.Where(w => NameContains(w, VITRAZH_FAMILY_SUBSTR)).ToList();
                // только витражи с заполненным «N_Витраж.Марка»
                var vitrazhi = (named.Count > 0 ? named : curtain)
                    .Where(w => !string.IsNullOrWhiteSpace(VitrazhMark(w)))
                    .ToList();

                report.AppendLine($"Окон-экземпляров: {all.Count}, «Ок»: {windows.Count}; " +
                    $"витражных стен: {curtain.Count}, с маркой: {vitrazhi.Count}");
                if (windows.Count == 0 && vitrazhi.Count == 0)
                {
                    TaskDialog.Show($"Марки окон [{BUILD}]", report.ToString());
                    return Result.Succeeded;
                }

                // ----- Уже помеченные (не дублируем), по типу марки -----
                var tagged = tagType != null ? ExistingTaggedByType(doc, view, tagType.Id) : new HashSet<ElementId>();
                var vTagged = vitType != null ? ExistingTaggedByType(doc, view, vitType.Id) : new HashSet<ElementId>();

                double off = UnitUtils.ConvertToInternalUnits(OFFSET_MM_MODEL, UnitTypeId.Millimeters);
                int placed = 0, failed = 0, skipped = 0;
                int vPlaced = 0, vFailed = 0, vSkipped = 0;

                using (Transaction tx = new Transaction(doc, "Марки окон и витражей"))
                {
                    tx.Start();
                    if (tagType != null && !tagType.IsActive) tagType.Activate();
                    if (vitType != null && !vitType.IsActive) vitType.Activate();

                    if (tagType != null)
                        foreach (FamilyInstance win in windows)
                        {
                            if (tagged.Contains(win.Id)) { skipped++; continue; }
                            if (PlaceWindowTag(doc, view, tagType, win, off)) placed++;
                            else failed++;
                        }

                    XYZ center = BuildingCenter(doc, view);
                    if (vitType != null)
                        foreach (Wall v in vitrazhi)
                        {
                            if (vTagged.Contains(v.Id)) { vSkipped++; continue; }
                            if (PlaceCurtainWallTag(doc, view, vitType, v, off, center)) vPlaced++;
                            else vFailed++;
                        }

                    tx.Commit();
                }

                report.AppendLine($"\nИТОГО окон: марок {placed}, не удалось {failed}, пропущено {skipped}" +
                    (tagType == null ? " (тип марки окна не найден)" : ""));
                report.AppendLine($"ИТОГО витражей: марок {vPlaced}, не удалось {vFailed}, пропущено {vSkipped}" +
                    (vitType == null ? " (тип марки витража не найден)" : ""));
                TaskDialog.Show($"Марки окон [{BUILD}]", report.ToString());
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

        /// <summary>
        /// Ставит марку окна с зазором off наружу (по нормали окна) и ориентацией
        /// вдоль стены (горизонтальная/вертикальная — по направлению стены).
        /// </summary>
        private static bool PlaceWindowTag(
            Document doc, View view, FamilySymbol tagType, FamilyInstance win, double off)
        {
            try
            {
                XYZ loc = ((LocationPoint)win.Location).Point;

                // нормаль окна в плоскости плана (наружу); запас — нормаль стены
                XYZ f = Flat(win.FacingOrientation);
                if (f.GetLength() < 1e-6 && win.Host is Wall w) f = Flat(w.Orientation);
                if (f.GetLength() < 1e-6) f = XYZ.BasisY;
                f = f.Normalize();

                XYZ head = new XYZ(loc.X + f.X * off, loc.Y + f.Y * off, loc.Z);

                // направление стены = нормаль, повёрнутая на 90°; ориентация текста по нему
                XYZ wallDir = new XYZ(-f.Y, f.X, 0);
                TagOrientation ori = Math.Abs(wallDir.X) >= Math.Abs(wallDir.Y)
                    ? TagOrientation.Horizontal
                    : TagOrientation.Vertical;

                IndependentTag tag = IndependentTag.Create(
                    doc, tagType.Id, view.Id, new Reference(win), false, ori, head);
                if (tag == null) return false;
                try { tag.TagHeadPosition = head; } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Ставит марку витража (витражной стены) с зазором off наружу от середины
        /// стены и ориентацией вдоль стены (горизонтальная/вертикальная).
        /// </summary>
        private static bool PlaceCurtainWallTag(
            Document doc, View view, FamilySymbol tagType, Wall wall, double off, XYZ center)
        {
            try
            {
                Curve c = ((LocationCurve)wall.Location).Curve;
                XYZ mid = c.Evaluate(0.5, true);

                XYZ dir = Flat(c.GetEndPoint(1) - c.GetEndPoint(0));
                dir = dir.GetLength() < 1e-9 ? XYZ.BasisX : dir.Normalize();

                // нормаль стены; запас — перпендикуляр к направлению
                XYZ f = Flat(wall.Orientation);
                if (f.GetLength() < 1e-6) f = new XYZ(-dir.Y, dir.X, 0);
                f = f.Normalize();
                // развернуть НАРУЖУ здания (от центра): у стены Orientation может
                // смотреть внутрь в зависимости от направления отрисовки
                XYZ outward = Flat(mid - center);
                if (outward.GetLength() > 1e-6 && f.DotProduct(outward) < 0) f = f.Negate();

                XYZ head = new XYZ(mid.X + f.X * off, mid.Y + f.Y * off, mid.Z);
                TagOrientation ori = Math.Abs(dir.X) >= Math.Abs(dir.Y)
                    ? TagOrientation.Horizontal
                    : TagOrientation.Vertical;

                IndependentTag tag = IndependentTag.Create(
                    doc, tagType.Id, view.Id, new Reference(wall), false, ori, head);
                if (tag == null) return false;
                try { tag.TagHeadPosition = head; } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Центр здания в плане — среднее середин всех стен вида.</summary>
        private static XYZ BuildingCenter(Document doc, View view)
        {
            double sx = 0, sy = 0; int n = 0;
            foreach (Wall w in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!(w.Location is LocationCurve lc)) continue;
                XYZ m = lc.Curve.Evaluate(0.5, true);
                sx += m.X; sy += m.Y; n++;
            }
            return n > 0 ? new XYZ(sx / n, sy / n, 0) : XYZ.Zero;
        }

        /// <summary>Проекция вектора на плоскость плана (Z = 0).</summary>
        private static XYZ Flat(XYZ v) => new XYZ(v.X, v.Y, 0);

        /// <summary>Имя семейства экземпляра содержит подстроку (без учёта регистра).</summary>
        private static bool FamilyContains(FamilyInstance fi, string sub) =>
            (fi.Symbol?.FamilyName ?? "").IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Имя типа стены содержит подстроку (без учёта регистра).</summary>
        private static bool NameContains(Wall w, string sub) =>
            (w.WallType?.Name ?? "").IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Значение «N_Витраж.Марка» стены (экземпляр или тип); "" если нет.</summary>
        private static string VitrazhMark(Wall w)
        {
            Parameter p = w.LookupParameter(VITRAZH_MARK_PARAM)
                ?? w.WallType?.LookupParameter(VITRAZH_MARK_PARAM);
            if (p == null || !p.HasValue) return "";
            string s = p.AsString();
            if (string.IsNullOrEmpty(s)) s = p.AsValueString();
            return s ?? "";
        }

        /// <summary>Значение «Маркировка типоразмера» окна (экземпляр или тип).</summary>
        private static string MarkOf(FamilyInstance fi)
        {
            Parameter p = fi.LookupParameter(MARK_PARAM) ?? fi.Symbol?.LookupParameter(MARK_PARAM);
            if (p == null || !p.HasValue) return "";
            string s = p.AsString();
            if (string.IsNullOrEmpty(s)) s = p.AsValueString();
            return s ?? "";
        }

        /// <summary>Id элементов, уже помеченных маркой указанного типа в этом виде.</summary>
        private static HashSet<ElementId> ExistingTaggedByType(Document doc, View view, ElementId tagTypeId)
        {
            var set = new HashSet<ElementId>();
            foreach (IndependentTag t in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                if (t.GetTypeId() != tagTypeId) continue;
                foreach (ElementId id in t.GetTaggedLocalElementIds())
                    set.Add(id);
            }
            return set;
        }

        private static FamilySymbol FindTagType(Document doc, string family, string type)
        {
            // ищем по имени семейства в любой категории (марка витража может быть
            // не в OST_WindowTags)
            var syms = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfType<FamilySymbol>()
                .Where(t => t.Family != null && t.Family.Name == family)
                .ToList();
            // точный тип, иначе — любой тип этого семейства
            return syms.FirstOrDefault(t => t.Name == type) ?? syms.FirstOrDefault();
        }
    }
}
