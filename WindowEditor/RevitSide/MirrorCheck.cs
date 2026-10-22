using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WindowEditor.Model;
using WindowEditor.UI;

namespace WindowEditor.RevitSide
{
    /// <summary>Отзеркаленный экземпляр окна.</summary>
    public class WindowIssue
    {
        public FamilyInstance Inst;
        public bool InGroup => Inst.GroupId != ElementId.InvalidElementId;
    }

    /// <summary>
    /// Поиск и исправление отзеркаленных окон — только относительно осей самого окна. Сторону стены не проверяем:
    /// стену могли нарисовать «не той» стороной, это не ошибка окна.
    /// Отражение — по Hand/FacingOrientation (FamilyCoords.InstanceFrame): GetTransform() у отражённого экземпляра
    /// отражения не показывает, а флаг Mirrored не сбрасывается стрелками «Отразить».
    /// </summary>
    public static class MirrorCheck
    {
        public static List<WindowIssue> Find(IEnumerable<FamilyInstance> instances) =>
            instances.Where(IsMirrored).Select(i => new WindowIssue { Inst = i }).ToList();

        public static bool IsMirrored(FamilyInstance inst) => FamilyCoords.InstanceFrame(inst).HasReflection;

        public static IEnumerable<FamilyInstance> InstancesOf(Document doc, Family family) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(i => i.Symbol.Family.Id == family.Id);
        public enum Choice { Continue, Fixed, Selected, Cancel }

        /// <summary>
        /// Сообщить о найденных окнах и предложить: исправить (вне групп), выделить, продолжить.
        /// </summary>
        public static Choice Report(UIDocument uidoc, List<WindowIssue> issues, string scope, bool allowContinue)
        {
            if (issues.Count == 0) return Choice.Continue;
            int grouped = issues.Count(i => i.InGroup);
            int fixable = issues.Count(i => !i.InGroup && CanFix(i));

            var lines = new List<string>();
            lines.Add($"• отзеркалено: {issues.Count}");
            if (grouped > 0) lines.Add($"• из них в группах: {grouped} — исправляются только в редакторе группы");

            string text = $"{scope}: найдено окон с нарушениями — {issues.Count}.\n" + string.Join("\n", lines) +
                          "\n\nОкна нельзя зеркалить: схема открывания, марка и спецификация перестают соответствовать модели. " +
                          "Для левого варианта сделайте отдельное семейство кнопкой «Отразить Л/П».";
            var options = new List<(Choice c, string label, string hint)>();
            if (fixable > 0)
                options.Add((Choice.Fixed, $"Исправить ({fixable} шт.)", "Отразить экземпляры обратно стрелками «Отразить». Окна в группах не трогаются."));
            options.Add((Choice.Selected, "Выделить их и выйти", "Чтобы посмотреть и исправить вручную."));
            if (allowContinue) options.Add((Choice.Continue, "Продолжить без исправления", null));
            int pick = NotifyWindow.Choose("Отзеркаленные окна", text,
                options.Select(o => o.label).ToArray(), options.Select(o => o.hint).ToArray());

            switch (pick < 0 ? Choice.Cancel : options[pick].c)
            {
                case Choice.Fixed:
                    int done = Fix(uidoc.Document, issues.Where(i => !i.InGroup));
                    NotifyWindow.Show("Редактор окна", $"Исправлено окон: {done}." +
                        (grouped > 0 ? $" В группах осталось: {grouped} — они выделены." : ""));
                    if (grouped > 0) Select(uidoc, issues.Where(i => i.InGroup));
                    return Choice.Fixed;
                case Choice.Selected:
                    Select(uidoc, issues);
                    return Choice.Selected;
                case Choice.Continue:
                    return Choice.Continue;
                default:
                    return Choice.Cancel;
            }
        }

        static bool CanFix(WindowIssue i) => i.Inst.CanFlipHand || i.Inst.CanFlipFacing;

        /// <summary>
        /// Снять отражение: flipHand (отражение по оси окна), если нельзя — flipFacing.
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
                        if (inst.CanFlipHand) inst.flipHand();
                        else if (inst.CanFlipFacing) inst.flipFacing();
                        else continue;
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
