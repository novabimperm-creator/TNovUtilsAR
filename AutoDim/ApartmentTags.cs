using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using TNovCommon;
using Parameter = Autodesk.Revit.DB.Parameter;

namespace TNovUtilsAR
{
    /// <summary>
    /// Марки квартир для квартирографии: одна обычная марка (IndependentTag) на
    /// квартиру — на «главное» (наибольшее) помещение группы, объединённой общим
    /// значением параметра «Номер квартиры». Марки выносятся к верхней/нижней
    /// границе здания и выстраиваются в горизонтальные ряды (общая координата Y).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoApartmentTags : IExternalCommand
    {
        // Метка сборки. Если её нет в заголовке окна — Revit грузит старый DLL.
        private const string BUILD = "квартиры v14 (офис — на самое большое)";

        // Семейство марки квартиры. Тип — первый, если TAG_TYPE не найден.
        private const string TAG_FAMILY = "pmN.Марка_Квартира_Талан";
        private const string TAG_TYPE = "pmN.Марка_Квартира_Талан";

        // Семейство/тип марки ОФИСА: та же группировка, но марка ставится ВНУТРИ
        // помещения (без выноса и выноски).
        private const string OFFICE_TAG_FAMILY = "pmN.Марка_Офис_Талан";
        private const string OFFICE_TAG_TYPE = "ПоУмолчанию";
        // Параметр назначения; группа с этим назначением считается офисом.
        private const string PURPOSE_PARAM = "Назначение";
        private const string OFFICE_PURPOSE_KEY = "офис";
        // Санузлы (по имени помещения) в офисе маркой не помечаются.
        private static readonly string[] SANITARY_KEYS =
            { "санузел", "с/у", "c/у", "туалет", "уборная", "ванн", "душев", "постироч" };

        // Основной параметр помещения с номером квартиры на этаже (по нему группируем).
        private const string PRIMARY_APARTMENT_PARAM = "N_Кв.НомерНаЭтаже";
        // Запасное ключевое слово: если основного параметра нет, ищем любой параметр,
        // чьё имя содержит «квартир», даёт >1 группы и не относится к площади.
        private const string APARTMENT_KEY = "квартир";

        // Вынос ряда марок за габарит здания — РЕАЛЬНОЕ расстояние в модели (мм).
        private const double EDGE_OFFSET_MM_MODEL = 3000.0;
        // Зазор ряда за габаритом марок осей (мм в модели).
        private const double GRID_MARGIN_MM_MODEL = 2000.0;
        // Мин. интервал между марками в ряду — в МИЛЛИМЕТРАХ НА БУМАГЕ (× масштаб вида).
        private const double MARK_SPACING_PAPER_MM = 18.0;

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
                TNovConfigLoad.LoadConfig("Марки квартир", _ver);
                Logger.Initialize("Марки квартир", DateTime.Now, _ver);

                if (!(view is ViewPlan))
                {
                    TaskDialog.Show($"Марки квартир [{BUILD}]", "Команда работает только на планах.");
                    return Result.Failed;
                }

                // ----- Тип марки -----
                FamilySymbol tagType = FindTagType(doc, TAG_FAMILY, TAG_TYPE);
                if (tagType == null)
                {
                    TaskDialog.Show($"Марки квартир [{BUILD}]",
                        $"Не найдено семейство марки \"{TAG_FAMILY}\".\n" +
                        "Загрузите его в проект и повторите.");
                    return Result.Failed;
                }

                // ----- Помещения; офисы обрабатываются ОТДЕЛЬНО от квартир -----
                var rooms = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .OfType<Room>()
                    .Where(r => r.Area > 1e-6 && r.Location is LocationPoint)
                    .ToList();

                // офисы — только основные помещения (санузлы офиса маркой не помечаются)
                var officeRooms = rooms.Where(IsOffice).Where(r => !IsSanitary(r)).ToList();
                var aptRooms = rooms.Where(r => !IsOffice(r)).ToList();

                string usedParam, diag;
                var groups = GroupByApartment(aptRooms, out usedParam, out diag);
                var officeGroups = GroupOffices(officeRooms);

                report.AppendLine($"Параметр квартиры: {usedParam ?? "не найден"}");
                if (!string.IsNullOrEmpty(diag))
                    report.AppendLine($"Кандидаты [имя=групп]: {diag}");
                report.AppendLine($"Помещений: {rooms.Count}, квартир: {groups.Count}, офисов: {officeGroups.Count}");
                if (groups.Count == 0 && officeGroups.Count == 0)
                {
                    report.AppendLine();
                    report.AppendLine("Параметры первого помещения (имя=значение):");
                    report.AppendLine(DumpRoomParams(rooms.FirstOrDefault()));
                    TaskDialog.Show($"Марки квартир [{BUILD}]", report.ToString());
                    return Result.Succeeded;
                }

                // ----- Типы марок и существующие марки (не дублируем) -----
                FamilySymbol officeType = FindTagType(doc, OFFICE_TAG_FAMILY, OFFICE_TAG_TYPE);
                var taggedRooms = ExistingTaggedRooms(doc, view, tagType.Id);
                var officeTagged = officeType != null
                    ? ExistingTaggedRooms(doc, view, officeType.Id)
                    : new HashSet<ElementId>();

                // ----- Габарит здания и уровни рядов (нужны только для квартир) -----
                double midY = 0, topY = 0, botY = 0;
                if (groups.Count > 0)
                {
                    double minX, maxX, minY, maxY;
                    if (!BuildingExtent(doc, view, rooms, out minX, out maxX, out minY, out maxY))
                    {
                        TaskDialog.Show($"Марки квартир [{BUILD}]", "Не удалось определить габарит здания.");
                        return Result.Failed;
                    }
                    midY = (minY + maxY) / 2;
                    double edgeOff = UnitUtils.ConvertToInternalUnits(EDGE_OFFSET_MM_MODEL, UnitTypeId.Millimeters);
                    topY = maxY + edgeOff;
                    botY = minY - edgeOff;

                    // ряд не должен наезжать на марки осей: отодвигаем за габарит осей
                    double gMin, gMax;
                    if (GridExtentY(doc, view, out gMin, out gMax))
                    {
                        double gridMargin = UnitUtils.ConvertToInternalUnits(GRID_MARGIN_MM_MODEL, UnitTypeId.Millimeters);
                        topY = Math.Max(topY, gMax + gridMargin);
                        botY = Math.Min(botY, gMin - gridMargin);
                    }
                }

                // ----- Кандидаты квартир (вынос в ряд к границе здания) -----
                var top = new List<Cand>();
                var bot = new List<Cand>();
                int skipped = 0;
                foreach (var grp in groups)
                {
                    Room main = grp.OrderByDescending(r => r.Area).First();
                    if (grp.Any(r => taggedRooms.Contains(r.Id))) { skipped++; continue; }
                    string key = ParamText(main.LookupParameter(usedParam));
                    XYZ c = ApartmentCentroid(grp);
                    var cand = new Cand { Main = main, Key = key, Rooms = grp.Count,
                        DesiredX = c.X, CentroidY = c.Y, Z = c.Z + 0.05 };
                    if (c.Y >= midY) top.Add(cand); else bot.Add(cand);
                }

                // ----- Кандидаты офисов (марка ВНУТРИ помещения) -----
                var offices = new List<Cand>();
                int officeSkipped = 0;
                foreach (var grp in officeGroups)
                {
                    if (officeType == null) break;
                    Room main = grp.OrderByDescending(r => r.Area).First();
                    if (grp.Any(r => officeTagged.Contains(r.Id))) { officeSkipped++; continue; }
                    XYZ mp = ((LocationPoint)main.Location).Point;
                    offices.Add(new Cand { Main = main, Key = OfficeGroupKey(main), Rooms = grp.Count,
                        DesiredX = mp.X, CentroidY = mp.Y, Z = mp.Z + 0.05 });
                }

                using (Transaction tx = new Transaction(doc, "Марки квартир"))
                {
                    tx.Start();
                    if (!tagType.IsActive) tagType.Activate();
                    if (officeType != null && !officeType.IsActive) officeType.Activate();

                    PlaceRow(doc, view, tagType, top, topY);
                    PlaceRow(doc, view, tagType, bot, botY);

                    // офисы — марка внутри помещения, без выноски
                    foreach (var oc in offices)
                        oc.Ok = PlaceInsideTag(doc, view, officeType, oc.Main,
                            new XYZ(oc.DesiredX, oc.CentroidY, oc.Z));

                    tx.Commit();
                }

                var all = top.Concat(bot).ToList();
                int placed = all.Count(c => c.Ok);
                int failed = all.Count(c => !c.Ok);
                int officePlaced = offices.Count(c => c.Ok);
                int officeFailed = offices.Count(c => !c.Ok);

                report.AppendLine($"\nИТОГО квартир: {all.Count}, марок {placed}, " +
                    $"не удалось {failed}, пропущено (уже есть) {skipped}");
                report.AppendLine($"ИТОГО офисов: {offices.Count}, марок {officePlaced}, " +
                    $"не удалось {officeFailed}, пропущено {officeSkipped}" +
                    (officeType == null ? " (тип марки офиса не найден)" : ""));
                report.AppendLine($"Ряды квартир: верх {top.Count}, низ {bot.Count}");
                report.AppendLine("\nПо объектам (номер: комнат, статус):");
                foreach (var c in all.Concat(offices).OrderBy(c => c.Key))
                    report.AppendLine($"  {c.Key}: комнат {c.Rooms}, {(c.Ok ? "OK" : "НЕ УДАЛОСЬ")}");
                TaskDialog.Show($"Марки квартир [{BUILD}]", report.ToString());
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

        private class Cand
        {
            public Room Main;
            public string Key;
            public int Rooms;
            public double DesiredX;
            public double CentroidY;   // Y центра квартиры — куда ведёт выноска
            public double Z;
            public bool Ok;
        }

        /// <summary>
        /// Ставит ряд марок на общей координате rowY: сортировка по X, интервал не
        /// меньше MARK_SPACING (чтобы марки не наезжали), выравнивание в одну линию.
        /// </summary>
        private static void PlaceRow(
            Document doc, View view, FamilySymbol tagType,
            List<Cand> cands, double rowY)
        {
            if (cands.Count == 0) return;
            double spacing = MM(view, MARK_SPACING_PAPER_MM);

            cands.Sort((a, b) => a.DesiredX.CompareTo(b.DesiredX));
            double prevX = double.NegativeInfinity;
            foreach (var cand in cands)
            {
                double x = cand.DesiredX;
                if (x < prevX + spacing) x = prevX + spacing;
                prevX = x;

                XYZ pt = new XYZ(x, rowY, cand.Z);
                cand.Ok = PlaceTag(doc, view, tagType, cand.Main, pt, cand.CentroidY);
            }
        }

        /// <summary>
        /// Создаёт обычную марку (IndependentTag) на помещение в точке head с прямой
        /// ВЕРТИКАЛЬНОЙ выноской до уровня центра квартиры (targetY). Конец выноски
        /// ставится под ЦЕНТРОМ рамки марки (по её bounding box), а не под точкой
        /// вставки — иначе выноска крепится сбоку и идёт под наклоном.
        /// </summary>
        private static bool PlaceTag(
            Document doc, View view, FamilySymbol tagType, Room room, XYZ head, double targetY)
        {
            try
            {
                Reference rf = new Reference(room);
                // без выноски — чтобы bounding box был только по рамке марки
                IndependentTag tag = IndependentTag.Create(
                    doc, tagType.Id, view.Id, rf, false, TagOrientation.Horizontal, head);
                if (tag == null) return false;
                try { tag.TagHeadPosition = head; } catch { }

                // центр рамки марки по X (после регенерации bbox актуален)
                doc.Regenerate();
                double cx = head.X;
                try
                {
                    BoundingBoxXYZ bb = tag.get_BoundingBox(view);
                    if (bb != null) cx = (bb.Min.X + bb.Max.X) / 2.0;
                }
                catch { }

                // прямая вертикальная выноска: конец под центром рамки, на уровне квартиры.
                // SetLeaderEnd принимает именно ссылку, что хранит марка (GetTaggedReferences).
                try
                {
                    tag.HasLeader = true;
                    Reference lr = rf;
                    try
                    {
                        var refs = tag.GetTaggedReferences();
                        if (refs != null && refs.Count > 0) lr = refs[0];
                    }
                    catch { }

                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                    tag.SetLeaderEnd(lr, new XYZ(cx, targetY, head.Z));
                }
                catch { }
                return true;
            }
            catch { return false; }
        }

        // =====================================================================
        //  ГРУППИРОВКА И ГЕОМЕТРИЯ
        // =====================================================================

        /// <summary>
        /// Находит параметр квартиры автоматически и группирует по нему помещения.
        /// Кандидаты — параметры, чьё имя содержит «квартир» и которые дают >1 группы.
        /// Приоритет: имя с «номер»/«№» и не про площадь; diag — список кандидатов.
        /// </summary>
        private static List<List<Room>> GroupByApartment(
            List<Room> rooms, out string usedParam, out string diag)
        {
            usedParam = null; diag = "";

            // 1) основной параметр номера квартиры на этаже
            if (rooms.Any(r => !string.IsNullOrWhiteSpace(ParamText(r.LookupParameter(PRIMARY_APARTMENT_PARAM)))))
            {
                usedParam = PRIMARY_APARTMENT_PARAM;
                diag = $"[{usedParam}={DistinctCount(rooms, usedParam)}] (основной)";
                return GroupByKey(rooms, usedParam);
            }

            // 2) запасная эвристика — все имена параметров, содержащие ключевое слово
            var names = new HashSet<string>();
            foreach (var r in rooms)
                foreach (Parameter p in r.Parameters)
                {
                    string n = p.Definition?.Name;
                    if (!string.IsNullOrEmpty(n) &&
                        n.IndexOf(APARTMENT_KEY, StringComparison.OrdinalIgnoreCase) >= 0)
                        names.Add(n);
                }

            // число различных непустых значений по каждому кандидату
            var scored = names
                .Select(n => new { n, lower = n.ToLowerInvariant(), distinct = DistinctCount(rooms, n) })
                .Where(x => x.distinct > 1)
                .ToList();
            diag = string.Join(" ", scored.OrderByDescending(x => x.distinct)
                .Select(x => $"[{x.n}={x.distinct}]"));

            usedParam =
                scored.Where(x => (x.lower.Contains("номер") || x.n.Contains("№")) && !x.lower.Contains("площад"))
                      .OrderByDescending(x => x.distinct).Select(x => x.n).FirstOrDefault()
                ?? scored.Where(x => !x.lower.Contains("площад"))
                      .OrderByDescending(x => x.distinct).Select(x => x.n).FirstOrDefault()
                ?? scored.OrderByDescending(x => x.distinct).Select(x => x.n).FirstOrDefault();

            if (usedParam == null) return new List<List<Room>>();
            return GroupByKey(rooms, usedParam);
        }

        /// <summary>Группирует помещения по непустому строковому значению параметра.</summary>
        private static List<List<Room>> GroupByKey(List<Room> rooms, string param)
        {
            return rooms
                .Select(r => new { r, key = ParamText(r.LookupParameter(param)) })
                .Where(x => !string.IsNullOrWhiteSpace(x.key))
                .GroupBy(x => x.key)
                .Select(g => g.Select(x => x.r).ToList())
                .ToList();
        }

        /// <summary>Число различных непустых значений параметра name по помещениям.</summary>
        private static int DistinctCount(List<Room> rooms, string name)
        {
            return rooms.Select(r => ParamText(r.LookupParameter(name)))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct()
                .Count();
        }

        /// <summary>Список параметров помещения со значениями (для диагностики).</summary>
        private static string DumpRoomParams(Room room)
        {
            if (room == null) return "(нет помещений)";
            var lines = new List<string>();
            foreach (Parameter p in room.Parameters)
            {
                string n = p.Definition?.Name;
                if (string.IsNullOrEmpty(n)) continue;
                string v = ParamText(p);
                if (!string.IsNullOrWhiteSpace(v)) lines.Add($"{n} = {v}");
            }
            lines.Sort();
            return string.Join("\n", lines);
        }

        /// <summary>Назначение помещения содержит «офис».</summary>
        private static bool IsOffice(Room room)
        {
            string p = (ParamText(room.LookupParameter(PURPOSE_PARAM)) ?? "").Trim();
            return p.IndexOf(OFFICE_PURPOSE_KEY, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Помещение — санузел/влажная зона (по имени): маркой офиса не метится.</summary>
        private static bool IsSanitary(Room room)
        {
            string n = (room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "")
                .ToLowerInvariant();
            if (n.Length == 0) return false;
            foreach (var k in SANITARY_KEYS)
                if (n.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Группирует помещения одного офиса вместе, чтобы марка встала ОДИН раз на
        /// самое большое помещение. Ключ — «Комментарии» (там «Офис (№…)», одинаковое
        /// у всех помещений офиса), иначе «N_Кв.Номер»/«N_Кв.НомерНаЭтаже»,
        /// иначе помещение отдельно.
        /// </summary>
        private static List<List<Room>> GroupOffices(List<Room> officeRooms)
        {
            return officeRooms
                .GroupBy(OfficeGroupKey)
                .Select(g => g.ToList())
                .ToList();
        }

        private static string OfficeGroupKey(Room r)
        {
            string k = ParamText(r.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS));
            if (string.IsNullOrWhiteSpace(k)) k = ParamText(r.LookupParameter("N_Кв.Номер"));
            if (string.IsNullOrWhiteSpace(k)) k = ParamText(r.LookupParameter("N_Кв.НомерНаЭтаже"));
            if (string.IsNullOrWhiteSpace(k)) k = "офис_" + r.Id.ToString();
            return k;
        }

        /// <summary>Ставит обычную марку по элементу ВНУТРИ помещения, без выноски.</summary>
        private static bool PlaceInsideTag(
            Document doc, View view, FamilySymbol tagType, Room room, XYZ pt)
        {
            try
            {
                IndependentTag tag = IndependentTag.Create(
                    doc, tagType.Id, view.Id, new Reference(room),
                    false, TagOrientation.Horizontal, pt);
                if (tag == null) return false;
                try { tag.TagHeadPosition = pt; } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Центр квартиры: среднее точек вставки помещений группы.</summary>
        private static XYZ ApartmentCentroid(List<Room> group)
        {
            double sx = 0, sy = 0, sz = 0; int n = 0;
            foreach (var r in group)
            {
                XYZ p = ((LocationPoint)r.Location).Point;
                sx += p.X; sy += p.Y; sz += p.Z; n++;
            }
            return n > 0 ? new XYZ(sx / n, sy / n, sz / n) : XYZ.Zero;
        }

        /// <summary>
        /// Габарит здания по 2D-обёрткам НАРУЖНЫХ стен (контур здания). Если стен в
        /// виде нет — по помещениям. От этого габарита откладывается вынос рядов.
        /// </summary>
        private static bool BuildingExtent(
            Document doc, View view, List<Room> rooms,
            out double minX, out double maxX, out double minY, out double maxY)
        {
            minX = minY = double.MaxValue; maxX = maxY = double.MinValue;
            bool any = false;

            // контур здания — по стенам (учитывает толщину наружных стен)
            foreach (Wall w in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Wall)).Cast<Wall>())
            {
                BoundingBoxXYZ bb = w.get_BoundingBox(view);
                if (bb == null) continue;
                minX = Math.Min(minX, bb.Min.X); maxX = Math.Max(maxX, bb.Max.X);
                minY = Math.Min(minY, bb.Min.Y); maxY = Math.Max(maxY, bb.Max.Y);
                any = true;
            }
            if (any) return true;

            // запас — по помещениям
            foreach (var r in rooms)
            {
                BoundingBoxXYZ bb = r.get_BoundingBox(view);
                if (bb == null) continue;
                minX = Math.Min(minX, bb.Min.X); maxX = Math.Max(maxX, bb.Max.X);
                minY = Math.Min(minY, bb.Min.Y); maxY = Math.Max(maxY, bb.Max.Y);
                any = true;
            }
            return any;
        }

        /// <summary>Вертикальный габарит марок осей (Grid) в виде: gMin..gMax по Y.</summary>
        private static bool GridExtentY(Document doc, View view, out double gMin, out double gMax)
        {
            gMin = double.MaxValue; gMax = double.MinValue;
            bool any = false;
            foreach (Grid g in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Grid)).Cast<Grid>())
            {
                BoundingBoxXYZ bb = g.get_BoundingBox(view);
                if (bb == null) continue;
                gMin = Math.Min(gMin, bb.Min.Y); gMax = Math.Max(gMax, bb.Max.Y);
                any = true;
            }
            return any;
        }

        /// <summary>Id помещений, уже помеченных маркой этого типа в виде.</summary>
        private static HashSet<ElementId> ExistingTaggedRooms(Document doc, View view, ElementId typeId)
        {
            var set = new HashSet<ElementId>();
            foreach (IndependentTag t in new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                if (t.GetTypeId() != typeId) continue;
                foreach (ElementId id in t.GetTaggedLocalElementIds())
                    set.Add(id);
            }
            return set;
        }

        // =====================================================================
        //  ОБЩЕЕ
        // =====================================================================

        private static FamilySymbol FindTagType(Document doc, string family, string typeName)
        {
            var syms = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family != null && s.Family.Name == family)
                .ToList();
            return syms.FirstOrDefault(s => s.Name == typeName) ?? syms.FirstOrDefault();
        }

        private static string ParamText(Parameter p)
        {
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.String: return p.AsString();
                case StorageType.Integer: return p.AsInteger().ToString();
                case StorageType.Double: return p.AsValueString() ?? p.AsDouble().ToString("0.###");
                default: return p.AsValueString();
            }
        }

        /// <summary>Миллиметры на бумаге → координаты модели (с учётом масштаба вида).</summary>
        private static double MM(View view, double paperMM)
        {
            double scale = view.Scale <= 0 ? 100 : view.Scale;
            return UnitUtils.ConvertToInternalUnits(paperMM * scale, UnitTypeId.Millimeters);
        }
    }
}
