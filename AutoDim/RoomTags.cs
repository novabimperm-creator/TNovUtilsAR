using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtilsAR
{
    /// <summary>
    /// Метки помещений: марка имени (по центру помещения) и марка площади
    /// (в правом нижнем углу). Обе марки ищут свободное место, не пересекая
    /// мебель, сантехнику, двери, другие марки и размеры на плане.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoRoomTagsCommand : IExternalCommand
    {
        // Метка сборки. Если её нет в заголовке окна — Revit грузит старый DLL.
        private const string BUILD = "метки v4 (лоджии с коэффициентом)";

        // Семейство марки и имена типов
        private const string TAG_FAMILY = "pmN.Марка_Помещение";
        private const string TAG_TYPE_NAME = "Имя";
        private const string TAG_TYPE_AREA = "Площадь";
        // Тип площади для лоджий/балконов (площадь с коэффициентом).
        private const string TAG_TYPE_AREA_COEF = "Площадь / Площадь с коэффициентом";
        private static readonly string[] LOGGIA_KEYS = { "лоджи", "балкон" };
        // Помещения с этим назначением метятся отдельной маркой «Номер_ВКруге»,
        // имя и площадь им не ставятся.
        private const string PURPOSE_PARAM = "Назначение";
        // Параметр площади (для оценки ширины текста марки)
        private const string AREA_PARAM = "N_Площадь.ОкруглСКоэффициентом";

        // Оценка габарита текста марки в МИЛЛИМЕТРАХ НА БУМАГЕ (умножается на масштаб вида)
        private const double TEXT_H_PAPER_MM = 3.5;    // высота строки текста
        private const double CHAR_W_FACTOR = 0.70;     // ширина символа ≈ 0.7 высоты
        private const double TAG_PAD_PAPER_MM = 1.0;   // запас вокруг текста

        // Поиск свободного места
        private const double CORNER_MARGIN_PAPER_MM = 2.0; // отступ от границ помещения
        private const double SEARCH_STEP_PAPER_MM = 2.0;   // шаг сетки поиска
        private const int SEARCH_MAX_RINGS = 30;           // максимум колец поиска (марка имени)
        // Марка площади не должна «улетать» из угла: поиск чистого места ограничен
        // этим числом колец, дальше марка ставится у угла даже с наложением.
        private const int CORNER_MAX_RINGS = 8;

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
                Logger.Initialize("Метки помещений", DateTime.Now, BUILD);

                if (!(view is ViewPlan))
                {
                    PluginReport.Show($"Метки помещений [{BUILD}]", "Команда работает только на планах.");
                    return Result.Failed;
                }

                // ----- Типы марок -----
                RoomTagType nameType = FindTagType(doc, TAG_FAMILY, TAG_TYPE_NAME);
                RoomTagType areaType = FindTagType(doc, TAG_FAMILY, TAG_TYPE_AREA);
                // площадь с коэффициентом для лоджий (если нет — обычная площадь)
                RoomTagType loggiaAreaType = FindTagType(doc, TAG_FAMILY, TAG_TYPE_AREA_COEF) ?? areaType;
                if (nameType == null || areaType == null)
                {
                    PluginReport.Show($"Метки помещений [{BUILD}]",
                        $"Не найдено семейство марки \"{TAG_FAMILY}\" с типами \"{TAG_TYPE_NAME}\" и \"{TAG_TYPE_AREA}\".\n" +
                        "Загрузите семейство в проект и повторите.");
                    return Result.Failed;
                }

                // ----- Помещения в виде -----
                var rooms = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .OfType<Room>()
                    .Where(r => r.Area > 1e-6 && r.Location is LocationPoint)
                    .ToList();
                report.AppendLine($"Помещений в виде: {rooms.Count}");
                if (rooms.Count == 0)
                {
                    PluginReport.Show($"Метки помещений [{BUILD}]", report.ToString());
                    return Result.Succeeded;
                }

                // ----- Препятствия: габариты элементов вида (2D) -----
                List<Rect2> obstacles = CollectObstacles(doc, view, report);

                // ----- Существующие марки этих типов (не дублируем) -----
                var tagged = new HashSet<(ElementId room, ElementId type)>();
                foreach (RoomTag t in new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().OfType<RoomTag>())
                {
                    if (t.Room != null && t.RoomTagType != null)
                        tagged.Add((t.Room.Id, t.RoomTagType.Id));
                }

                int namePlaced = 0, areaPlaced = 0, nameOverlap = 0, areaOverlap = 0, skipped = 0, mopSkipped = 0;

                using (Transaction tx = new Transaction(doc, "Метки помещений"))
                {
                    tx.Start();

                    foreach (Room room in rooms)
                    {
                        // МОП/технические метятся отдельной маркой «Номер_ВКруге»
                        if (IsMop(room)) { mopSkipped++; continue; }

                        double z = ((LocationPoint)room.Location).Point.Z + 0.05;
                        BoundingBoxXYZ bb = room.get_BoundingBox(view);
                        if (bb == null) { skipped++; continue; }

                        string roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";

                        // --- марка ИМЕНИ: по центру помещения ---
                        if (!tagged.Contains((room.Id, nameType.Id)))
                        {
                            Rect2 size = TagSize(view, roomName.Length);
                            XYZ centroid = RoomCentroid(room, bb, z);
                            bool clean;
                            XYZ pt = FindSpotSpiral(room, centroid, size, z, view, obstacles, out clean);
                            RoomTag tag = PlaceTag(doc, view, room, pt, nameType);
                            if (tag != null)
                            {
                                namePlaced++;
                                if (!clean) nameOverlap++;
                                obstacles.Add(Rect2.Around(pt, size));
                            }
                        }
                        else skipped++;

                        // --- марка ПЛОЩАДИ: правый нижний угол ---
                        // лоджии/балконы — площадь с коэффициентом, остальные — обычная
                        RoomTagType useAreaType = IsLoggia(room) ? loggiaAreaType : areaType;
                        if (!tagged.Contains((room.Id, useAreaType.Id)))
                        {
                            string areaText = AreaText(room);
                            Rect2 size = TagSize(view, Math.Max(areaText.Length, 3));
                            bool clean;
                            XYZ pt = FindSpotCorner(room, bb, size, z, view, obstacles, out clean);
                            RoomTag tag = PlaceTag(doc, view, room, pt, useAreaType);
                            if (tag != null)
                            {
                                areaPlaced++;
                                if (!clean) areaOverlap++;
                                obstacles.Add(Rect2.Around(pt, size));
                            }
                        }
                        else skipped++;
                    }

                    tx.Commit();
                }

                report.AppendLine($"\nИТОГО: марок имени {namePlaced} (с наложением {nameOverlap}), " +
                    $"марок площади {areaPlaced} (с наложением {areaOverlap}), пропущено (уже есть) {skipped}, " +
                    $"МОП/технических пропущено {mopSkipped}");
                PluginReport.Show($"Метки помещений [{BUILD}]", report.ToString());
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

        // =====================================================================
        //  РАЗМЕЩЕНИЕ
        // =====================================================================

        private static RoomTag PlaceTag(Document doc, View view, Room room, XYZ pt, RoomTagType type)
        {
            try
            {
                RoomTag tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(pt.X, pt.Y), view.Id);
                if (tag != null && tag.GetTypeId() != type.Id) tag.ChangeTypeId(type.Id);
                return tag;
            }
            catch { return null; }
        }

        /// <summary>
        /// Поиск места по спирали от центра: первая точка, где прямоугольник марки
        /// целиком в помещении и не пересекает препятствия. Если чистого места нет —
        /// первая точка внутри помещения (clean=false), иначе стартовая точка.
        /// </summary>
        private static XYZ FindSpotSpiral(
            Room room, XYZ start, Rect2 size, double z, View view,
            List<Rect2> obstacles, out bool clean)
        {
            double step = MM(view, SEARCH_STEP_PAPER_MM);
            XYZ firstInside = null;

            for (int ring = 0; ring <= SEARCH_MAX_RINGS; ring++)
            {
                foreach (var (dx, dy) in RingOffsets(ring))
                {
                    XYZ p = new XYZ(start.X + dx * step, start.Y + dy * step, z);
                    Rect2 r = Rect2.Around(p, size);
                    if (!RectInRoom(room, r, z)) continue;
                    if (firstInside == null) firstInside = p;
                    if (!r.IntersectsAny(obstacles)) { clean = true; return p; }
                }
            }
            clean = false;
            return firstInside ?? start;
        }

        /// <summary>
        /// Поиск места от правого нижнего угла габарита помещения: кольца кандидатов
        /// влево/вверх от угла, ближайший к углу приоритетнее.
        /// </summary>
        private static XYZ FindSpotCorner(
            Room room, BoundingBoxXYZ bb, Rect2 size, double z, View view,
            List<Rect2> obstacles, out bool clean)
        {
            double step = MM(view, SEARCH_STEP_PAPER_MM);
            double margin = MM(view, CORNER_MARGIN_PAPER_MM);
            // стартовый центр марки — в углу с отступом
            double x0 = bb.Max.X - margin - size.W / 2;
            double y0 = bb.Min.Y + margin + size.H / 2;
            XYZ firstInside = null;

            for (int ring = 0; ring <= CORNER_MAX_RINGS; ring++)
            {
                // только влево (dx≥0) и вверх (dy≥0) от угла
                for (int dx = 0; dx <= ring; dx++)
                {
                    int dy = ring - dx;
                    XYZ p = new XYZ(x0 - dx * step, y0 + dy * step, z);
                    Rect2 r = Rect2.Around(p, size);
                    if (!RectInRoom(room, r, z)) continue;
                    if (firstInside == null) firstInside = p;
                    if (!r.IntersectsAny(obstacles)) { clean = true; return p; }
                }
            }
            clean = false;
            return firstInside ?? new XYZ(x0, y0, z);
        }

        /// <summary>Смещения кольца ring по периметру квадрата (для спирали от центра).</summary>
        private static IEnumerable<(int dx, int dy)> RingOffsets(int ring)
        {
            if (ring == 0) { yield return (0, 0); yield break; }
            for (int dx = -ring; dx <= ring; dx++)
                for (int dy = -ring; dy <= ring; dy++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        yield return (dx, dy);
        }

        /// <summary>Прямоугольник марки целиком внутри помещения (углы + центр).</summary>
        private static bool RectInRoom(Room room, Rect2 r, double z)
        {
            return room.IsPointInRoom(new XYZ(r.MinX, r.MinY, z))
                && room.IsPointInRoom(new XYZ(r.MaxX, r.MinY, z))
                && room.IsPointInRoom(new XYZ(r.MinX, r.MaxY, z))
                && room.IsPointInRoom(new XYZ(r.MaxX, r.MaxY, z))
                && room.IsPointInRoom(new XYZ((r.MinX + r.MaxX) / 2, (r.MinY + r.MaxY) / 2, z));
        }

        /// <summary>Центр помещения: точка вставки, если внутри, иначе центр габарита.</summary>
        private static XYZ RoomCentroid(Room room, BoundingBoxXYZ bb, double z)
        {
            XYZ lp = ((LocationPoint)room.Location).Point;
            XYZ p = new XYZ(lp.X, lp.Y, z);
            if (room.IsPointInRoom(p)) return p;
            return new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, z);
        }

        // =====================================================================
        //  ГАБАРИТЫ И ПРЕПЯТСТВИЯ
        // =====================================================================

        private struct Rect2
        {
            public double MinX, MinY, MaxX, MaxY;
            public double W => MaxX - MinX;
            public double H => MaxY - MinY;

            public static Rect2 Around(XYZ center, Rect2 size) => new Rect2
            {
                MinX = center.X - size.W / 2,
                MaxX = center.X + size.W / 2,
                MinY = center.Y - size.H / 2,
                MaxY = center.Y + size.H / 2
            };

            public bool Intersects(Rect2 o) =>
                MinX < o.MaxX && MaxX > o.MinX && MinY < o.MaxY && MaxY > o.MinY;

            public bool IntersectsAny(List<Rect2> list)
            {
                foreach (var o in list) if (Intersects(o)) return true;
                return false;
            }
        }

        /// <summary>Оценка габарита текста марки в модели (по числу символов и масштабу вида).</summary>
        private static Rect2 TagSize(View view, int chars)
        {
            double w = MM(view, chars * TEXT_H_PAPER_MM * CHAR_W_FACTOR + 2 * TAG_PAD_PAPER_MM);
            double h = MM(view, TEXT_H_PAPER_MM + 2 * TAG_PAD_PAPER_MM);
            return new Rect2 { MinX = 0, MinY = 0, MaxX = w, MaxY = h };
        }

        /// <summary>Текст марки площади (для оценки ширины): значение параметра или площадь м².</summary>
        private static string AreaText(Room room)
        {
            Parameter p = room.LookupParameter(AREA_PARAM);
            if (p != null && p.HasValue)
            {
                string s = p.AsValueString();
                if (string.IsNullOrEmpty(s))
                {
                    if (p.StorageType == StorageType.Double) s = p.AsDouble().ToString("0.0");
                    else if (p.StorageType == StorageType.String) s = p.AsString();
                }
                if (!string.IsNullOrEmpty(s)) return s;
            }
            double m2 = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
            return m2.ToString("0.0");
        }

        /// <summary>
        /// 2D-габариты элементов вида, которые марки не должны перекрывать:
        /// мебель, сантехника, оборудование, колонны, двери, существующие марки, размеры.
        /// </summary>
        private static List<Rect2> CollectObstacles(Document doc, View view, StringBuilder report)
        {
            var cats = new HashSet<ElementId>(new[]
            {
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_FurnitureSystems,
                BuiltInCategory.OST_PlumbingFixtures,
                BuiltInCategory.OST_Casework,
                BuiltInCategory.OST_SpecialityEquipment,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_ElectricalEquipment,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_LightingFixtures,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_RoomTags,
                BuiltInCategory.OST_Dimensions,
            }.Select(c => new ElementId(c)));

            var result = new List<Rect2>();
            foreach (Element el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (el.Category == null || !cats.Contains(el.Category.Id)) continue;
                BoundingBoxXYZ bb = el.get_BoundingBox(view);
                if (bb == null) continue;
                result.Add(new Rect2 { MinX = bb.Min.X, MinY = bb.Min.Y, MaxX = bb.Max.X, MaxY = bb.Max.Y });
            }
            report.AppendLine($"Препятствий (габаритов элементов): {result.Count}");
            return result;
        }

        // =====================================================================
        //  ОБЩЕЕ
        // =====================================================================

        private static RoomTagType FindTagType(Document doc, string family, string typeName)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .OfType<RoomTagType>()
                .FirstOrDefault(t => t.FamilyName == family && t.Name == typeName);
        }

        /// <summary>Назначение помещения = «МОП» или «Техническое» (метятся отдельно).</summary>
        private static bool IsMop(Room room)
        {
            Parameter p = room.LookupParameter(PURPOSE_PARAM);
            string s = (p != null && p.HasValue ? (p.AsString() ?? p.AsValueString()) : "") ?? "";
            s = s.Trim();
            if (s.Length == 0) return false;
            return s.Equals("МОП", StringComparison.OrdinalIgnoreCase)
                || s.IndexOf("техническ", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Помещение — лоджия/балкон (по имени): площадь с коэффициентом.</summary>
        private static bool IsLoggia(Room room)
        {
            string n = (room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "")
                .ToLowerInvariant();
            if (n.Length == 0) return false;
            foreach (var k in LOGGIA_KEYS)
                if (n.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>Миллиметры на бумаге → координаты модели (с учётом масштаба вида).</summary>
        private static double MM(View view, double paperMM)
        {
            double scale = view.Scale <= 0 ? 100 : view.Scale;
            return UnitUtils.ConvertToInternalUnits(paperMM * scale, UnitTypeId.Millimeters);
        }
    }
}
