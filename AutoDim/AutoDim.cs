using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TNovCommon;
using Parameter = Autodesk.Revit.DB.Parameter;

namespace TNovUtilsAR
{
    /// <summary>
    /// Авторазмеры: цепочки размеров по осям (+ общий размер) и цепочки проёмов
    /// по наружным фасадным стенам на планах, фасадах и разрезах.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoDim : IExternalCommand
    {
        // Метка сборки. Если её нет в заголовке окна — Revit грузит старый DLL.
        private const string BUILD = "build v18 (диагностика 3)";

        // Отступы цепочек в МИЛЛИМЕТРАХ НА БУМАГЕ (умножаются на масштаб вида).
        private const double OPENING_CHAIN_PAPER_MM = 8.0;   // цепочка проёмов от стены
        private const double GRID_CHAIN_PAPER_MM    = 8.0;   // цепочка осей от концов осей
        private const double OVERALL_GAP_PAPER_MM   = 8.0;   // вынос общего размера дальше цепочки осей

        // Засечки ближе этого расстояния (в МОДЕЛИ, мм) считаются одной — убирает
        // нулевые сегменты от почти совпадающих граней/ссылок.
        private const double OPENING_MERGE_MM = 10.0;
        // Стена, утопленная глубже этого от наружной фасадной плоскости, считается
        // «внутри лоджии» и не размеряется.
        private const double LOGGIA_RECESS_MM = 200.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Авторазмеры";
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
            try
            {
                viewModel0 = JsonConvert.DeserializeObject<AppVersionViewModel>(File.ReadAllText(jsonpath0));
            }
            catch (Exception) { }
            #endregion

            try
            {
                View view = doc.ActiveView;

                var report = new StringBuilder();
                report.AppendLine($"[{BUILD}]  Вид: {view.Name} ({view.GetType().Name}), масштаб 1:{view.Scale}");

                if (!(view is ViewPlan) && !(view is ViewSection))
                {
                    TaskDialog.Show($"Авторазмеры [{BUILD}]", "Команда работает только на планах, фасадах и разрезах.");
                    return Result.Failed;
                }

                // Оси видимые в виде (если пусто — по всему проекту)
                List<Grid> allGrids = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(Grid)).Cast<Grid>().ToList();
                if (allGrids.Count == 0)
                    allGrids = new FilteredElementCollector(doc)
                        .OfClass(typeof(Grid)).Cast<Grid>().ToList();
                report.AppendLine($"Осей: {allGrids.Count}");

                int gridDims = 0, overallDims = 0, openingDims = 0;

                using (Transaction tx = new Transaction(doc, "Авторазмеры"))
                {
                    tx.Start();

                    // ----- ОСИ + ОБЩИЙ РАЗМЕР -----
                    if (allGrids.Count >= 2)
                    {
                        var groups = GroupGridsByDirection(allGrids);
                        report.AppendLine($"Групп параллельных осей: {groups.Count}");
                        for (int i = 0; i < groups.Count; i++)
                        {
                            if (groups[i].Count < 2) continue;
                            string st;
                            bool chain, overall;
                            CreateGridChain(doc, view, groups[i], out chain, out overall, out st);
                            if (chain) gridDims++;
                            if (overall) overallDims++;
                            report.AppendLine($"  Группа {i + 1} ({groups[i].Count} осей): {st}");
                        }
                    }

                    // ----- ПРОЁМЫ -----
                    openingDims = CreateOpeningDimensions(doc, view, allGrids, report);

                    tx.Commit();
                }

                report.AppendLine($"\nИТОГО: цепочек осей {gridDims}, общих размеров {overallDims}, цепочек проёмов {openingDims}");
                Logger.Log($"Вид: {view.Name}, цепочек осей {gridDims}, общих размеров {overallDims}, цепочек проёмов {openingDims}", 1);
                TaskDialog.Show($"Авторазмеры [{BUILD}]", report.ToString());
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Logger.Log("Ошибка: " + ex.Message, 1);
                message = ex.Message;
                return Result.Failed;
            }
        }

        // =====================================================================
        //  ОСИ
        // =====================================================================

        private static List<List<Grid>> GroupGridsByDirection(List<Grid> grids)
        {
            var groups = new List<List<Grid>>();
            var remaining = new List<Grid>(grids);

            while (remaining.Count > 0)
            {
                Grid first = remaining[0];
                XYZ dir1 = GetGridDirection(first);
                if (dir1 == null) { remaining.RemoveAt(0); continue; }

                var group = new List<Grid> { first };
                for (int i = remaining.Count - 1; i >= 1; i--)
                {
                    XYZ dir2 = GetGridDirection(remaining[i]);
                    if (dir2 != null && Math.Abs(dir1.DotProduct(dir2)) > 0.9999)
                    {
                        group.Add(remaining[i]);
                        remaining.RemoveAt(i);
                    }
                }
                remaining.RemoveAt(0);
                groups.Add(group);
            }
            return groups;
        }

        private static XYZ GetGridDirection(Grid grid)
        {
            Curve curve = grid.Curve;
            if (curve is Line line) return line.Direction.Normalize();
            return null;
        }

        /// <summary>
        /// Создаёт цепочку размеров между осями группы и общий размер
        /// (первая↔последняя ось). Совпадающие оси дедуплицируются.
        /// </summary>
        private static void CreateGridChain(
            Document doc, View view, List<Grid> grids,
            out bool chainCreated, out bool overallCreated, out string status)
        {
            chainCreated = false;
            overallCreated = false;

            XYZ gridDir = GetGridDirection(grids[0]);
            if (gridDir == null) { status = "оси не прямые."; return; }

            XYZ perp = new XYZ(-gridDir.Y, gridDir.X, 0);
            if (perp.GetLength() < 1e-9) { status = "вырожденный перпендикуляр."; return; }
            perp = perp.Normalize();

            Options opt = GeomOptions(view);
            var data = new List<(Grid grid, Reference reference, XYZ point, double s)>();
            foreach (Grid g in grids)
            {
                Curve c = g.Curve;
                if (c == null) continue;
                XYZ p = c.GetEndPoint(0);
                Reference r = GetGridReference(g, view, opt);
                if (r == null) continue;
                data.Add((g, r, p, perp.DotProduct(p)));
            }
            if (data.Count < 2) { status = $"валидных ссылок {data.Count}."; return; }

            // Сортировка и дедупликация совпадающих осей
            data.Sort((a, b) => a.s.CompareTo(b.s));
            double tol = doc.Application.ShortCurveTolerance;
            var uniq = new List<(Grid grid, Reference reference, XYZ point, double s)>();
            foreach (var d in data)
                if (uniq.Count == 0 || Math.Abs(d.s - uniq[uniq.Count - 1].s) > tol)
                    uniq.Add(d);
            if (uniq.Count < 2) { status = "после дедупликации осталось <2 уникальных осей."; return; }

            double minS = uniq.First().s, maxS = uniq.Last().s;

            // Габариты вдоль направления осей
            double minD = double.MaxValue, maxD = double.MinValue;
            foreach (var d in uniq)
            {
                Curve c = d.grid.Curve;
                double d0 = gridDir.DotProduct(c.GetEndPoint(0));
                double d1 = gridDir.DotProduct(c.GetEndPoint(1));
                minD = Math.Min(minD, Math.Min(d0, d1));
                maxD = Math.Max(maxD, Math.Max(d0, d1));
            }

            // Сторона: ниже (вертикальные оси) / левее (горизонтальные) в координатах вида
            XYZ vRight = view.RightDirection, vUp = view.UpDirection;
            bool vertical = Math.Abs(gridDir.DotProduct(vUp)) >= Math.Abs(gridDir.DotProduct(vRight));

            double chainOff = OffsetModel(view, GRID_CHAIN_PAPER_MM);
            double overallGap = OffsetModel(view, OVERALL_GAP_PAPER_MM);

            // Пробуем оба варианта и выбираем нижний/левый
            XYZ bp = uniq[0].point;
            double bpS = perp.DotProduct(bp), bpD = gridDir.DotProduct(bp);
            double dMin = minD - chainOff, dMax = maxD + chainOff;
            XYZ pMin = bp + (minS - bpS) * perp + (dMin - bpD) * gridDir;
            XYZ pMax = bp + (minS - bpS) * perp + (dMax - bpD) * gridDir;

            bool useMin = vertical
                ? pMin.DotProduct(vUp) <= pMax.DotProduct(vUp)
                : pMin.DotProduct(vRight) <= pMax.DotProduct(vRight);

            double dimD = useMin ? dMin : dMax;
            double overallD = useMin ? (dMin - overallGap) : (dMax + overallGap);

            if (maxS - minS < tol) { status = "оси совпадают."; return; }

            // --- основная цепочка ---
            chainCreated = TryCreateDim(doc, view, perp, gridDir, bp, minS, maxS, dimD,
                uniq.Select(u => u.reference));

            // --- общий размер (первая↔последняя) ---
            var ends = new[] { uniq.First().reference, uniq.Last().reference };
            overallCreated = TryCreateDim(doc, view, perp, gridDir, bp, minS, maxS, overallD, ends);

            status = $"уникальных осей {uniq.Count}, цепочка {(chainCreated ? "OK" : "нет")}, " +
                     $"общий {(overallCreated ? "OK" : "нет")}, сторона {(useMin ? "min" : "max")}.";
        }

        /// <summary>
        /// Строит размерную линию ВДОЛЬ perp (поперёк осей) на координате dimD
        /// по направлению gridDir и создаёт размер по переданным ссылкам.
        /// </summary>
        private static bool TryCreateDim(
            Document doc, View view, XYZ perp, XYZ gridDir, XYZ basePoint,
            double minS, double maxS, double dimD, IEnumerable<Reference> refs)
        {
            double baseS = perp.DotProduct(basePoint);
            double baseD = gridDir.DotProduct(basePoint);
            XYZ start = basePoint + (minS - baseS) * perp + (dimD - baseD) * gridDir;
            XYZ end   = basePoint + (maxS - baseS) * perp + (dimD - baseD) * gridDir;

            if (start.DistanceTo(end) < doc.Application.ShortCurveTolerance) return false;

            Line line = Line.CreateBound(start, end);
            ReferenceArray ra = new ReferenceArray();
            foreach (var r in refs) ra.Append(r);
            if (ra.Size < 2) return false;

            try
            {
                Dimension dim = doc.Create.NewDimension(view, line, ra);
                return dim != null;
            }
            catch { return false; }
        }

        // =====================================================================
        //  ПРОЁМЫ
        // =====================================================================

        private class WallInfo
        {
            public Wall Wall;
            public XYZ Dir;
            public XYZ Origin;
            public XYZ Exterior;
            public List<FamilyInstance> Windows;
        }

        /// <summary>
        /// Строит цепочку проёмов (ширины окон и простенков по откосам) единой линией
        /// на весь фасад: коллинеарные наружные стены объединяются, цепочка не
        /// прерывается на лоджиях и разрывах между сегментами стены.
        /// </summary>
        private static int CreateOpeningDimensions(
            Document doc, View view, List<Grid> allGrids, StringBuilder report)
        {
            int created = 0;
            Options opt = GeomOptions(view);
            double tol = doc.Application.ShortCurveTolerance;

            ElementId windowsCatId = new ElementId(BuiltInCategory.OST_Windows);
            ElementId doorsCatId = new ElementId(BuiltInCategory.OST_Doors);
            var openings = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Category != null &&
                    (fi.Category.Id == windowsCatId || fi.Category.Id == doorsCatId))
                .Where(fi => fi.Host is Wall)
                .ToList();

            report.AppendLine($"Окон в виде: {openings.Count}");

            // стены с окнами и прямой осью
            var walls = new List<WallInfo>();
            foreach (var grp in openings.GroupBy(o => o.Host.Id))
            {
                Wall wall = doc.GetElement(grp.Key) as Wall;
                if (wall == null) continue;
                if (!((wall.Location as LocationCurve)?.Curve is Line wl)) continue;
                walls.Add(new WallInfo
                {
                    Wall = wall,
                    Dir = wl.Direction.Normalize(),
                    Origin = wl.GetEndPoint(0),
                    Exterior = wall.Orientation,
                    Windows = grp.ToList()
                });
            }

            // объединяем коллинеарные стены (один фасад) в группы
            double collinearTol = UnitUtils.ConvertToInternalUnits(200.0, UnitTypeId.Millimeters);
            var groups = new List<List<WallInfo>>();
            var used = new bool[walls.Count];
            for (int i = 0; i < walls.Count; i++)
            {
                if (used[i]) continue;
                var group = new List<WallInfo> { walls[i] };
                used[i] = true;
                XYZ dir = walls[i].Dir, org = walls[i].Origin;

                for (int j = i + 1; j < walls.Count; j++)
                {
                    if (used[j]) continue;
                    if (Math.Abs(dir.DotProduct(walls[j].Dir)) < 0.999) continue; // не параллельна
                    XYZ delta = walls[j].Origin - org;
                    double perp = (delta - dir * delta.DotProduct(dir)).GetLength();
                    if (perp > collinearTol) continue;                            // другая плоскость (лоджия/иной фасад)
                    group.Add(walls[j]);
                    used[j] = true;
                }
                groups.Add(group);
            }

            // центр здания — по серединам стен-хостов проёмов; относительно него
            // определяем, какая из параллельных стен «наружная» (без опоры на Orientation)
            XYZ centroid = XYZ.Zero;
            int cn = 0;
            foreach (var wi in walls)
                if ((wi.Wall.Location as LocationCurve)?.Curve is Curve wc)
                {
                    centroid = centroid.Add(wc.GetEndPoint(0).Add(wc.GetEndPoint(1)).Multiply(0.5));
                    cn++;
                }
            if (cn > 0) centroid = centroid.Divide(cn);

            // размеряем только наружные фасадные группы; стены, утопленные внутрь
            // лоджии (за наружной плоскостью фасада), игнорируем
            for (int i = 0; i < groups.Count; i++)
            {
                if (cn > 0 && IsRecessedInsideLoggia(groups[i], groups, centroid, tol))
                {
                    report.AppendLine($"  Группа {i + 1}: стена внутри лоджии — пропущена.");
                    continue;
                }
                var g0 = groups[i][0];
                report.AppendLine($"  Группа {i + 1} ({groups[i].Count} стен):");
                if (BuildFacadeChain(doc, view, opt, g0.Dir, g0.Origin, g0.Exterior, groups[i], tol, report))
                    created++;
            }

            return created;
        }

        /// <summary>
        /// true, если группа стен утоплена внутрь: на ТОЙ ЖЕ стороне от центра здания
        /// есть параллельная группа, расположенная дальше НАРУЖУ и перекрывающая эту
        /// по длине (например, остекление лоджии позади фасада). Такие не размеряем.
        /// </summary>
        private static bool IsRecessedInsideLoggia(
            List<WallInfo> group, List<List<WallInfo>> allGroups, XYZ centroid, double tol)
        {
            XYZ dir = group[0].Dir;
            XYZ perp = new XYZ(-dir.Y, dir.X, 0);
            if (perp.GetLength() < 1e-9) return false;
            perp = perp.Normalize();

            double cPos = perp.DotProduct(centroid);
            double myPos = GroupPerpPos(group, perp);
            double myOut = Math.Abs(myPos - cPos);                          // удаление наружу от центра
            GroupSpan(group, dir, out double myMin, out double myMax);
            double recessTol = UnitUtils.ConvertToInternalUnits(LOGGIA_RECESS_MM, UnitTypeId.Millimeters);

            foreach (var other in allGroups)
            {
                if (ReferenceEquals(other, group)) continue;
                if (Math.Abs(dir.DotProduct(other[0].Dir)) < 0.999) continue;   // не параллельна
                double otherPos = GroupPerpPos(other, perp);
                if ((myPos - cPos) * (otherPos - cPos) <= 0) continue;          // на другой стороне от центра
                double otherOut = Math.Abs(otherPos - cPos);
                if (otherOut <= myOut + recessTol) continue;                    // не дальше наружу
                GroupSpan(other, dir, out double oMin, out double oMax);
                if (oMax < myMin - tol || oMin > myMax + tol) continue;         // не перекрывает по длине
                return true;
            }
            return false;
        }

        /// <summary>Средняя проекция группы на перпендикуляр perp.</summary>
        private static double GroupPerpPos(List<WallInfo> group, XYZ perp)
        {
            double sum = 0; int n = 0;
            foreach (var wi in group) { sum += perp.DotProduct(wi.Origin); n++; }
            return n > 0 ? sum / n : 0;
        }

        /// <summary>Габарит группы вдоль направления стен dir (проекции концов).</summary>
        private static void GroupSpan(List<WallInfo> group, XYZ dir, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            foreach (var wi in group)
            {
                if (!((wi.Wall.Location as LocationCurve)?.Curve is Curve c)) continue;
                double a = dir.DotProduct(c.GetEndPoint(0));
                double b = dir.DotProduct(c.GetEndPoint(1));
                min = Math.Min(min, Math.Min(a, b));
                max = Math.Max(max, Math.Max(a, b));
            }
        }

        /// <summary>
        /// Единая размерная цепочка по группе коллинеарных наружных стен:
        /// торцы стен + откосы окон, спроецированные на общую линию фасада.
        /// </summary>
        private static bool BuildFacadeChain(
            Document doc, View view, Options opt,
            XYZ dir, XYZ origin, XYZ exterior, List<WallInfo> group, double tol,
            StringBuilder report)
        {
            var items = new List<(Reference r, double t)>();     // основной: грани блока (Ширина)
            var itemsFb = new List<(Reference r, double t)>();   // запас: Left/Right (номинал)

            // торцы фасада — крайние продольные грани всей группы (границы цепочки)
            Reference minRef = null, maxRef = null;
            double minT = double.MaxValue, maxT = double.MinValue;

            foreach (var wi in group)
            {
                var faces = GetWallLongitudinalFaces(wi.Wall, opt, dir, origin).ToList();

                foreach (var f in faces)
                {
                    if (f.t < minT) { minT = f.t; minRef = f.r; }
                    if (f.t > maxT) { maxT = f.t; maxRef = f.r; }
                }

                foreach (var op in wi.Windows)
                {
                    XYZ c = (op.Location as LocationPoint)?.Point;
                    if (c == null) continue;
                    double tc = dir.DotProduct(c - origin);
                    double halfW = GetOpeningWidth(op) / 2.0;

                    // запас: ссылки Left/Right семейства (обычно номинал проёма)
                    Reference lFb = null, rFb = null;
                    IList<Reference> ll = op.GetReferences(FamilyInstanceReferenceType.Left);
                    IList<Reference> rr = op.GetReferences(FamilyInstanceReferenceType.Right);
                    if (ll != null && ll.Count > 0) lFb = ll[0];
                    if (rr != null && rr.Count > 0) rFb = rr[0];
                    if (lFb != null) itemsFb.Add((lFb, tc - halfW));
                    if (rFb != null) itemsFb.Add((rFb, tc + halfW));

                    // основной: грани блока, ближайшие к ±«Ширина.Наружная»/2 —
                    // размер = наружная ширина проёма (с четвертями)
                    double targetHalf = GetOpeningOuterWidth(op) / 2.0;
                    if (targetHalf <= 0) targetHalf = halfW;
                    GetOpeningSideRefs(op, dir, origin, tc, targetHalf,
                        out Reference lref, out double lt, out Reference rref, out double rt);
                    if (lref != null && rref != null)
                    {
                        items.Add((lref, lt));
                        items.Add((rref, rt));
                    }
                    else // граней с ссылками нет — используем Left/Right и в основном списке
                    {
                        if (lFb != null) items.Add((lFb, tc - halfW));
                        if (rFb != null) items.Add((rFb, tc + halfW));
                    }

                    // ДИАГНОСТИКА: все боковые грани блока и стены (мм от центра проёма)
                    int tgt = (int)Math.Round(UnitUtils.ConvertFromInternalUnits(targetHalf, UnitTypeId.Millimeters));
                    var famF = ListOpeningSideFaces(op, dir, origin, tc);
                    var wallF = faces.Select(f => f.t - tc)
                        .Where(d => Math.Abs(d) < UnitUtils.ConvertToInternalUnits(1600, UnitTypeId.Millimeters))
                        .OrderBy(d => d)
                        .Select(d => (int)Math.Round(UnitUtils.ConvertFromInternalUnits(d, UnitTypeId.Millimeters)));
                    report.AppendLine($"    проём(цель ±{tgt}): сем[{string.Join(" ", famF)}] стена[{string.Join(" ", wallF)}]");
                }
            }

            if (minRef != null) { items.Add((minRef, minT)); itemsFb.Add((minRef, minT)); }
            if (maxRef != null) { items.Add((maxRef, maxT)); itemsFb.Add((maxRef, maxT)); }

            // основной вариант, при неудаче — запасной
            if (TryCreateChain(doc, view, dir, origin, exterior, tol, items)) return true;
            return TryCreateChain(doc, view, dir, origin, exterior, tol, itemsFb);
        }

        /// <summary>Создаёт цепочку по списку (ссылка, позиция) со слиянием близких засечек.</summary>
        private static bool TryCreateChain(
            Document doc, View view, XYZ dir, XYZ origin, XYZ exterior, double tol,
            List<(Reference r, double t)> items)
        {
            if (items.Count < 2) return false;

            // слияние засечек ближе OPENING_MERGE_MM — убирает нулевые сегменты
            double mergeTol = UnitUtils.ConvertToInternalUnits(OPENING_MERGE_MM, UnitTypeId.Millimeters);
            items.Sort((a, b) => a.t.CompareTo(b.t));
            var uniq = new List<(Reference r, double t)>();
            foreach (var it in items)
                if (uniq.Count == 0 || Math.Abs(it.t - uniq[uniq.Count - 1].t) > mergeTol)
                    uniq.Add(it);
            if (uniq.Count < 2) return false;

            double off = OffsetModel(view, OPENING_CHAIN_PAPER_MM);
            XYZ basePt = origin + exterior * off;
            XYZ start = basePt + uniq.First().t * dir;
            XYZ end   = basePt + uniq.Last().t  * dir;
            if (start.DistanceTo(end) < tol) return false;

            Line line = Line.CreateBound(start, end);
            ReferenceArray ra = new ReferenceArray();
            foreach (var u in uniq) ra.Append(u.r);
            if (ra.Size < 2) return false;

            try { return doc.Create.NewDimension(view, line, ra) != null; }
            catch { return false; }
        }

        /// <summary>
        /// Наружная ширина проёма (параметр «Ширина.Наружная» и аналоги); 0 если нет.
        /// </summary>
        private static double GetOpeningOuterWidth(FamilyInstance fi)
        {
            string[] names = { "Ширина.Наружная", "Ширина наружная", "Width.Exterior" };
            foreach (var n in names)
            {
                Parameter p = fi.LookupParameter(n) ?? fi.Symbol?.LookupParameter(n);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                    return p.AsDouble();
            }
            return 0;
        }

        /// <summary>
        /// Ссылки на боковые грани геометрии оконного блока (нормаль вдоль стены),
        /// БЛИЖАЙШИЕ к целевой полуширине targetHalf от центра — даёт размер по
        /// параметру «Ширина.Наружная» (наружный проём с четвертями).
        /// Ссылки берутся из геометрии символа (в геометрии экземпляра они null).
        /// </summary>
        private static void GetOpeningSideRefs(
            FamilyInstance op, XYZ dir, XYZ origin, double tc, double targetHalf,
            out Reference lref, out double lt, out Reference rref, out double rt)
        {
            lref = null; rref = null;
            lt = 0; rt = 0;
            // грань засчитывается, если отстоит от целевой позиции не дальше этого
            double accept = UnitUtils.ConvertToInternalUnits(120.0, UnitTypeId.Millimeters);

            var geOpt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            GeometryElement ge = op.get_Geometry(geOpt);
            if (ge == null) return;

            double bestLd = double.MaxValue, bestRd = double.MaxValue;
            double bestLt = 0, bestRt = 0;
            foreach (GeometryObject go in ge)
            {
                if (go is Solid s0)
                    ScanSideFaces(s0, Transform.Identity, dir, origin, tc, targetHalf,
                        ref lref, ref bestLd, ref bestLt, ref rref, ref bestRd, ref bestRt);
                else if (go is GeometryInstance gi)
                {
                    GeometryElement sym = gi.GetSymbolGeometry();
                    if (sym == null) continue;
                    foreach (GeometryObject g2 in sym)
                        if (g2 is Solid s)
                            ScanSideFaces(s, gi.Transform, dir, origin, tc, targetHalf,
                                ref lref, ref bestLd, ref bestLt, ref rref, ref bestRd, ref bestRt);
                }
            }
            if (lref == null || rref == null || bestLd > accept || bestRd > accept)
            {
                lref = null; rref = null;
                return;
            }
            lt = bestLt; rt = bestRt;
        }

        /// <summary>ДИАГНОСТИКА: позиции всех боковых граней блока с ссылками (мм от центра).</summary>
        private static List<int> ListOpeningSideFaces(FamilyInstance op, XYZ dir, XYZ origin, double tc)
        {
            var result = new List<int>();
            var geOpt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            GeometryElement ge = op.get_Geometry(geOpt);
            if (ge == null) return result;

            void Scan(Solid s, Transform tr)
            {
                if (s == null || s.Faces.Size == 0) return;
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.Reference == null) continue;
                    XYZ n = tr.OfVector(pf.FaceNormal);
                    if (Math.Abs(n.Normalize().DotProduct(dir)) < 0.99) continue;
                    double t = dir.DotProduct(tr.OfPoint(pf.Origin) - origin);
                    result.Add((int)Math.Round(UnitUtils.ConvertFromInternalUnits(t - tc, UnitTypeId.Millimeters)));
                }
            }

            foreach (GeometryObject go in ge)
            {
                if (go is Solid s0) Scan(s0, Transform.Identity);
                else if (go is GeometryInstance gi)
                {
                    GeometryElement sym = gi.GetSymbolGeometry();
                    if (sym == null) continue;
                    foreach (GeometryObject g2 in sym)
                        if (g2 is Solid s) Scan(s, gi.Transform);
                }
            }
            result.Sort();
            return result;
        }

        private static void ScanSideFaces(
            Solid s, Transform tr, XYZ dir, XYZ origin, double tc, double targetHalf,
            ref Reference lref, ref double bestLd, ref double bestLt,
            ref Reference rref, ref double bestRd, ref double bestRt)
        {
            if (s == null || s.Faces.Size == 0) return;
            foreach (Face f in s.Faces)
            {
                if (!(f is PlanarFace pf) || pf.Reference == null) continue;
                XYZ n = tr.OfVector(pf.FaceNormal);
                if (Math.Abs(n.Normalize().DotProduct(dir)) < 0.99) continue;
                double t = dir.DotProduct(tr.OfPoint(pf.Origin) - origin);
                if (t < tc)
                {
                    double d = Math.Abs((tc - t) - targetHalf);
                    if (d < bestLd) { bestLd = d; bestLt = t; lref = pf.Reference; }
                }
                else
                {
                    double d = Math.Abs((t - tc) - targetHalf);
                    if (d < bestRd) { bestRd = d; bestRt = t; rref = pf.Reference; }
                }
            }
        }

        /// <summary>Ширина проёма в координатах модели (для сортировки откосов вдоль стены).</summary>
        private static double GetOpeningWidth(FamilyInstance fi)
        {
            string[] names = { "Width", "Ширина", "Rough Width", "Ширина проёма" };
            foreach (var n in names)
            {
                Parameter p = fi.LookupParameter(n) ?? fi.Symbol?.LookupParameter(n);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                    return p.AsDouble();
            }
            // запасной вариант — габарит bounding box (для фасадов/планов с осевым направлением стены)
            BoundingBoxXYZ bb = fi.get_BoundingBox(null);
            if (bb != null)
                return Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
            return 0;
        }

        /// <summary>
        /// Ссылки на ВСЕ продольные планарные грани стены (нормаль вдоль стены):
        /// торцы стены по краям + откосы вырезанных в стене проёмов (лоджия и т.п.).
        /// Позиция t — проекция грани на направление стены. Ранее брались только две
        /// крайние грани (торцы), из-за чего откосы проёма лоджии терялись.
        /// </summary>
        private static IEnumerable<(Reference r, double t)> GetWallLongitudinalFaces(
            Wall wall, Options opt, XYZ wallDir, XYZ wallStart)
        {
            var result = new List<(Reference, double)>();

            GeometryElement geom = wall.get_Geometry(opt);
            if (geom == null) return result;

            foreach (GeometryObject go in geom)
            {
                if (!(go is Solid s) || s.Faces.Size == 0) continue;
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.Reference == null) continue;
                    if (Math.Abs(pf.FaceNormal.Normalize().DotProduct(wallDir)) < 0.99) continue;
                    double t = wallDir.DotProduct(pf.Origin - wallStart);
                    result.Add((pf.Reference, t));
                }
            }
            return result;
        }

        // =====================================================================
        //  ОБЩЕЕ
        // =====================================================================

        private static Options GeomOptions(View view) => new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = false,
            View = view
        };

        private static Reference GetGridReference(Grid grid, View view, Options opt)
        {
            GeometryElement geom = grid.get_Geometry(opt);
            if (geom != null)
                foreach (GeometryObject go in geom)
                    if (go is Line ln && ln.Reference != null)
                        return ln.Reference;
            return new Reference(grid);
        }

        /// <summary>Отступ в координатах модели из миллиметров на бумаге (учитывает масштаб вида).</summary>
        private static double OffsetModel(View view, double paperMM)
        {
            double scale = view.Scale <= 0 ? 100 : view.Scale;
            return UnitUtils.ConvertToInternalUnits(paperMM * scale, UnitTypeId.Millimeters);
        }
    }
}
