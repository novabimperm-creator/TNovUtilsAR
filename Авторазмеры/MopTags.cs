using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace GridDimensionTool
{
    /// <summary>
    /// Марки помещений МОП: марка pmN.Марка_Помещение, тип «Номер_ВКруге», ставится
    /// по центру только тех помещений, у которых «Назначение» = «МОП» или
    /// «Техническое». Номер помещения марка читает из параметра «Номер».
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoMopTagsCommand : IExternalCommand
    {
        // Метка сборки. Если её нет в заголовке окна — Revit грузит старый DLL.
        private const string BUILD = "МОП v1";

        // Семейство и тип марки.
        private const string TAG_FAMILY = "pmN.Марка_Помещение";
        private const string TAG_TYPE = "Номер_ВКруге";
        // Параметр назначения помещения.
        private const string PURPOSE_PARAM = "Назначение";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                View view = doc.ActiveView;
                var report = new StringBuilder();
                report.AppendLine($"[{BUILD}]  Вид: {view.Name}, масштаб 1:{view.Scale}");
                Logger.Initialize(doc, commandData.Application.Application.Username, "Марки МОП", BUILD);

                if (!(view is ViewPlan))
                {
                    PluginReport.Show($"Марки МОП [{BUILD}]", "Команда работает только на планах.");
                    return Result.Failed;
                }

                // ----- Тип марки -----
                RoomTagType tagType = FindTagType(doc, TAG_FAMILY, TAG_TYPE);
                if (tagType == null)
                {
                    PluginReport.Show($"Марки МОП [{BUILD}]",
                        $"Не найдено семейство марки \"{TAG_FAMILY}\" с типом \"{TAG_TYPE}\".\n" +
                        "Загрузите семейство/тип в проект и повторите.");
                    return Result.Failed;
                }

                // ----- Помещения МОП/Технические в виде -----
                var rooms = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .OfType<Room>()
                    .Where(r => r.Area > 1e-6 && r.Location is LocationPoint)
                    .Where(IsMop)
                    .ToList();
                report.AppendLine($"Помещений МОП/технических в виде: {rooms.Count}");
                if (rooms.Count == 0)
                {
                    PluginReport.Show($"Марки МОП [{BUILD}]", report.ToString());
                    return Result.Succeeded;
                }

                // ----- Уже помеченные этим типом (не дублируем) -----
                var tagged = new HashSet<ElementId>();
                foreach (RoomTag t in new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().OfType<RoomTag>())
                {
                    if (t.Room != null && t.RoomTagType != null && t.RoomTagType.Id == tagType.Id)
                        tagged.Add(t.Room.Id);
                }

                int placed = 0, failed = 0, skipped = 0;

                using (Transaction tx = new Transaction(doc, "Марки МОП"))
                {
                    tx.Start();

                    foreach (Room room in rooms)
                    {
                        if (tagged.Contains(room.Id)) { skipped++; continue; }
                        if (PlaceTag(doc, view, room, tagType)) placed++;
                        else failed++;
                    }

                    tx.Commit();
                }

                report.AppendLine($"\nИТОГО: марок МОП {placed}, не удалось {failed}, " +
                    $"пропущено (уже есть) {skipped}");
                PluginReport.Show($"Марки МОП [{BUILD}]", report.ToString());
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

        /// <summary>Назначение помещения = «МОП» или «Техническое».</summary>
        private static bool IsMop(Room room)
        {
            string p = (ParamText(room.LookupParameter(PURPOSE_PARAM)) ?? "").Trim();
            if (p.Length == 0) return false;
            return p.Equals("МОП", StringComparison.OrdinalIgnoreCase)
                || p.IndexOf("техническ", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Ставит марку по центру помещения (точка вставки, иначе центр габарита).</summary>
        private static bool PlaceTag(Document doc, View view, Room room, RoomTagType type)
        {
            try
            {
                XYZ lp = ((LocationPoint)room.Location).Point;
                double z = lp.Z + 0.05;
                XYZ p = new XYZ(lp.X, lp.Y, z);
                if (!room.IsPointInRoom(p))
                {
                    BoundingBoxXYZ bb = room.get_BoundingBox(view);
                    if (bb != null) p = new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, z);
                }

                RoomTag tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(p.X, p.Y), view.Id);
                if (tag == null) return false;
                if (tag.GetTypeId() != type.Id) tag.ChangeTypeId(type.Id);
                return true;
            }
            catch { return false; }
        }

        private static RoomTagType FindTagType(Document doc, string family, string type)
        {
            var syms = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .OfType<RoomTagType>()
                .Where(t => t.FamilyName == family)
                .ToList();
            return syms.FirstOrDefault(t => t.Name == type) ?? syms.FirstOrDefault();
        }

        private static string ParamText(Parameter p)
        {
            if (p == null || !p.HasValue) return "";
            string s = p.AsString();
            if (string.IsNullOrEmpty(s)) s = p.AsValueString();
            return s ?? "";
        }
    }
}
