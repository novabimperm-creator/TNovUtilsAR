using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WindowEditor.Model;

namespace WindowEditor.RevitSide
{
    /// <summary>Экземпляр окна, который нельзя оставлять: отзеркален или стоит наружной стороной внутрь.</summary>
    public class WindowIssue
    {
        public FamilyInstance Inst;
        public bool Mirrored;   // в преобразовании есть отражение — петли и схема с фасада зеркальны
        public bool WrongSide;  // наружная сторона семейства смотрит в помещение
        public bool InGroup => Inst.GroupId != ElementId.InvalidElementId;
    }

    /// <summary>
    /// Поиск и исправление отзеркаленных окон.
    /// Отражение определяется по матрице экземпляра (HasReflection) — так ловятся и «Зеркально», и стрелки «Отразить».
    /// Сторона — по оси +Y семейства и наружной стороне стены (Wall.Orientation); нужна конфигурация окна,
    /// чтобы знать, где у семейства фасад.
    /// </summary>
    public static class MirrorCheck
    {
        public static List<WindowIssue> Find(Document doc, IEnumerable<FamilyInstance> instances, Func<FamilyInstance, bool?> exteriorPositiveY)
        {
            var res = new List<WindowIssue>();
            foreach (var inst in instances)
            {
                var issue = Check(inst, exteriorPositiveY(inst));
                if (issue.Mirrored || issue.WrongSide) res.Add(issue);
            }
            return res;
        }

        static WindowIssue Check(FamilyInstance inst, bool? extPosY)
        {
            // GetTransform() у отражённого экземпляра без отражения, а флаг Mirrored не сбрасывается стрелками
            // «Отразить» — отражение и сторону фасада смотрим по Hand/FacingOrientation (FamilyCoords.InstanceFrame).
            var t = FamilyCoords.InstanceFrame(inst);
            var issue = new WindowIssue { Inst = inst, Mirrored = t.HasReflection };
            if (extPosY.HasValue && inst.Host is Wall wall)
            {
                var famExterior = extPosY.Value ? t.BasisY : -t.BasisY;
                issue.WrongSide = famExterior.DotProduct(wall.Orientation) < 0;
            }
            return issue;
        }

        public static IEnumerable<FamilyInstance> InstancesOf(Document doc, Family family) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(i => i.Symbol.Family.Id == family.Id);

        /// <summary>Конфигурация окна по экземпляру (с кэшем по типу).</summary>
        public static Func<FamilyInstance, bool?> ExteriorResolver()
        {
            var cache = new Dictionary<ElementId, bool?>();
            return inst =>
            {
                var sid = inst.Symbol.Id;
                if (!cache.TryGetValue(sid, out var v))
                {
                    var cfg = WindowConfig.FromJson(inst.Symbol.LookupParameter(WindowParams.Config)?.AsString());
                    v = cfg?.ExteriorPositiveY;
                    cache[sid] = v;
                }
                return v;
            };
        }

        public enum Choice { Continue, Fixed, Selected, Cancel }

        /// <summary>
        /// Сообщить о найденных окнах и предложить: исправить (вне групп), выделить, продолжить.
        /// </summary>
        public static Choice Report(UIDocument uidoc, List<WindowIssue> issues, string scope, bool allowContinue)
        {
            if (issues.Count == 0) return Choice.Continue;
            int mirrored = issues.Count(i => i.Mirrored);
            int wrong = issues.Count(i => i.WrongSide);
            int grouped = issues.Count(i => i.InGroup);
            int fixable = issues.Count(i => !i.InGroup && CanFix(i));

            var lines = new List<string>();
            if (mirrored > 0) lines.Add($"• отзеркалено: {mirrored}");
            if (wrong > 0) lines.Add($"• наружной стороной в помещение: {wrong}");
            if (grouped > 0) lines.Add($"• из них в группах: {grouped} — исправляются только в редакторе группы");

            var td = new TaskDialog("Редактор окна")
            {
                MainInstruction = $"{scope}: найдено окон с нарушениями — {issues.Count}",
                MainContent = string.Join("\n", lines) +
                              "\n\nОкна нельзя зеркалить: схема открывания, марка и спецификация перестают соответствовать модели. " +
                              "Для левого варианта сделайте отдельное семейство кнопкой «Отразить Л/П».",
                AllowCancellation = true
            };
            if (fixable > 0)
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Исправить ({fixable} шт.)",
                    "Отразить экземпляры обратно стрелками «Отразить». Окна в группах не трогаются.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Выделить их и выйти",
                "Чтобы посмотреть и исправить вручную.");
            if (allowContinue)
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Продолжить без исправления");

            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1:
                    int done = Fix(uidoc.Document, issues.Where(i => !i.InGroup));
                    TaskDialog.Show("Редактор окна", $"Исправлено окон: {done}." +
                        (grouped > 0 ? $" В группах осталось: {grouped} — они выделены." : ""));
                    if (grouped > 0) Select(uidoc, issues.Where(i => i.InGroup));
                    return Choice.Fixed;
                case TaskDialogResult.CommandLink2:
                    Select(uidoc, issues);
                    return Choice.Selected;
                case TaskDialogResult.CommandLink3:
                    return Choice.Continue;
                default:
                    return Choice.Cancel;
            }
        }

        static bool CanFix(WindowIssue i) =>
            (!i.Mirrored || i.Inst.CanFlipHand || i.Inst.CanFlipFacing) && (!i.WrongSide || i.Inst.CanFlipFacing);

        /// <summary>
        /// Снять отражение и развернуть фасадом наружу.
        /// flipFacing меняет и сторону, и чётность отражения; flipHand — только отражение.
        /// </summary>
        static int Fix(Document doc, IEnumerable<WindowIssue> issues)
        {
            int n = 0;
            using (var t = new Transaction(doc, "Исправить отзеркаленные окна"))
            {
                t.Start();
                foreach (var i in issues)
                {
                    try
                    {
                        var inst = i.Inst;
                        if (i.WrongSide && inst.CanFlipFacing) inst.flipFacing(); // сторона исправлена, отражение переключилось
                        doc.Regenerate();
                        if (FamilyCoords.InstanceFrame(inst).HasReflection)
                        {
                            if (inst.CanFlipHand) inst.flipHand();
                            else continue;
                        }
                        n++;
                    }
                    catch (Exception) { /* окно, которое нельзя отразить, оставляем как есть */ }
                }
                t.Commit();
            }
            return n;
        }

        static void Select(UIDocument uidoc, IEnumerable<WindowIssue> issues) =>
            uidoc.Selection.SetElementIds(issues.Select(i => i.Inst.Id).ToList());
    }
}
