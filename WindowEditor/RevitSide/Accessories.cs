using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using WindowEditor.Model;
using static WindowEditor.RevitSide.WindowParams;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Комплектация окон (подоконники, отливы, откосы) отдельными элементами модели — экземплярами
    /// «WE_Элемент окна», не вложенными в семейство окна. Связь с окном — WE_ID окна (ElementId строкой);
    /// деталь окна узнаётся по WE_Вид элемента + WE_Расположение. Обновление — кнопкой: существующие
    /// элементы двигаются и меняют размеры (Id сохраняются), недостающие создаются, лишние удаляются.
    /// Элементы окон из групп создаются вне групп.
    /// </summary>
    public static class Accessories
    {
        public class Report
        {
            public int Windows, Created, Updated, Deleted, Orphans;
            public readonly List<string> Problems = new List<string>();

            public override string ToString()
            {
                var s = $"Окон: {Windows}. Элементов комплектации: создано {Created}, обновлено {Updated}, удалено {Deleted}";
                if (Orphans > 0) s += $" (из них без окна {Orphans})";
                s += ".";
                if (Problems.Count > 0) s += "\n\nНе обработано:\n" + string.Join("\n", Problems.Take(15)) + (Problems.Count > 15 ? $"\n… и ещё {Problems.Count - 15}" : "");
                return s;
            }
        }

        /// <summary>Окна редактора (с WE_Config в типе).</summary>
        public static bool IsEditorWindow(Element e) =>
            e is FamilyInstance fi && EditWindowCommand.IsWindow(fi) && fi.Symbol.LookupParameter(Config)?.AsString() is string s && s.Length > 0;

        public static List<FamilyInstance> WindowsOf(Document doc, Family family) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(i => i.Symbol.Family.Id == family.Id).ToList();

        public static List<FamilyInstance> AllWindows(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(IsEditorWindow).ToList();

        /// <summary>
        /// Обновить комплектацию окон. removeOrphans — удалить элементы, чьих окон (редактора) в модели нет;
        /// имеет смысл при обработке всей модели. Вызывать вне транзакций.
        /// </summary>
        public static Report Update(Document doc, IEnumerable<FamilyInstance> windows, bool removeOrphans)
        {
            var report = new Report();
            string path = AccessoryFamily.Ensure(doc.Application);
            var list = windows.Where(IsEditorWindow).ToList();

            using (var t = new Transaction(doc, "Комплектация окон"))
            {
                t.Start();
                var opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new HideWarnings());
                t.SetFailureHandlingOptions(opts);

                var symbol = AccessoryFamily.LoadIntoProject(doc, path, out bool replaced);
                // старое семейство другой категории удалено вместе с элементами — пересоздаём у всех окон
                if (replaced) list = AllWindows(doc);

                // существующие элементы: Id окна → элементы
                var byWindow = new Dictionary<long, List<FamilyInstance>>();
                var unlinked = new List<FamilyInstance>();
                foreach (var a in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                             .Where(i => i.Symbol.Family.Name == AccessoryFamily.Name))
                {
                    if (long.TryParse(a.LookupParameter(ElWindowId)?.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                    {
                        if (!byWindow.TryGetValue(id, out var l)) byWindow[id] = l = new List<FamilyInstance>();
                        l.Add(a);
                    }
                    else unlinked.Add(a);
                }

                foreach (var w in list)
                {
                    long wid = w.Id.IntegerValueCompat();
                    byWindow.TryGetValue(wid, out var existing);
                    try
                    {
                        UpdateWindow(doc, w, symbol, existing ?? new List<FamilyInstance>(), report);
                        report.Windows++;
                    }
                    catch (Exception ex)
                    {
                        report.Problems.Add($"Окно {wid} ({w.Symbol.FamilyName}): {ex.Message}");
                    }
                }

                if (removeOrphans)
                {
                    var alive = new HashSet<long>(AllWindows(doc).Select(w => w.Id.IntegerValueCompat()));
                    var orphans = unlinked.Concat(byWindow.Where(kv => !alive.Contains(kv.Key)).SelectMany(kv => kv.Value))
                        .Select(a => a.Id).ToList();
                    if (orphans.Count > 0)
                    {
                        doc.Delete(orphans);
                        report.Orphans = orphans.Count;
                        report.Deleted += orphans.Count;
                    }
                }

                t.Commit();
            }
            return report;
        }

        static void UpdateWindow(Document doc, FamilyInstance w, FamilySymbol symbol, List<FamilyInstance> existing, Report report)
        {
            var cfg = WindowConfig.FromJson(w.Symbol.LookupParameter(Config)?.AsString())
                      ?? throw new InvalidOperationException("не читается WE_Config");
            if (!(w.Host is Wall wall)) throw new InvalidOperationException("окно не в стене");

            // геометрия — по фактической стене, а не по толщине из схемы
            cfg.WallThickness = Mm(wall.Width);
            var geo = new WindowGeometry(cfg);
            double sillZ = w.Symbol.LookupParameter(SillZ)?.AsDouble() ?? 0;
            var c = new FamilyCoords(cfg, geo, sillZ);
            var tr = FamilyCoords.InstanceFrame(w);
            var level = doc.GetElement(w.LevelId) as Level;
            var wanted = Place(geo, c, tr);

            // существующие — по ключу детали; дубли ключа лишние
            var pool = new Dictionary<string, FamilyInstance>();
            var extra = new List<ElementId>();
            foreach (var a in existing)
            {
                var key = Key(a.LookupParameter(ElKind)?.AsString(), a.LookupParameter(ElPlace)?.AsString());
                if (pool.ContainsKey(key)) extra.Add(a.Id); else pool[key] = a;
            }

            var used = new HashSet<string>();
            var created = new List<(FamilyInstance a, Placement p)>();
            foreach (var p in wanted)
            {
                var key = Key(p.Box.Kind, p.Box.Label);
                if (!used.Add(key)) continue; // в схеме не бывает двух одинаковых деталей; на всякий случай
                if (pool.TryGetValue(key, out var a))
                {
                    Apply(doc, a, p, w);
                    report.Updated++;
                }
                else
                {
                    a = level != null
                        ? doc.Create.NewFamilyInstance(p.Corner, symbol, level, StructuralType.NonStructural)
                        : doc.Create.NewFamilyInstance(p.Corner, symbol, StructuralType.NonStructural);
                    created.Add((a, p));
                    report.Created++;
                }
            }
            // До регенерации LocationPoint нового экземпляра читается как (0,0,0) —
            // без неё Apply «доставил» бы его повторно и сдвинул вдвое.
            if (created.Count > 0)
            {
                doc.Regenerate();
                foreach (var (a, p) in created) Apply(doc, a, p, w);
            }

            extra.AddRange(pool.Where(kv => !used.Contains(kv.Key)).Select(kv => kv.Value.Id));
            if (extra.Count > 0) { doc.Delete(extra); report.Deleted += extra.Count; }
        }

        static string Key(string kind, string place) => (kind ?? "") + "|" + (place ?? "");

        class Placement
        {
            public Box Box;
            public XYZ Corner;      // угол бруска в модели (начало семейства элемента)
            public double Angle;    // поворот вокруг Z
            public double Lx, Ly, Lz;
        }

        /// <summary>
        /// Бруски схемы → модель. Брусок задаём углом и осями семейства элемента (X вдоль стены, Y поперёк).
        /// Оси берём правыми (без отражения) — так отражённые окна получают обычные, не зеркальные элементы.
        /// </summary>
        static List<Placement> Place(WindowGeometry geo, FamilyCoords c, Transform tr)
        {
            var dirX = new XYZ(tr.BasisX.X, tr.BasisX.Y, 0).Normalize();
            var dirY = XYZ.BasisZ.CrossProduct(dirX);
            var res = new List<Placement>();
            foreach (var b in geo.Boxes)
            {
                double xa = c.X(b.X0), xb = c.X(b.X1), ya = c.Y(b.D0), yb = c.Y(b.D1);
                var pts = new[] { new XYZ(xa, ya, 0), new XYZ(xb, ya, 0), new XYZ(xa, yb, 0), new XYZ(xb, yb, 0) }
                    .Select(tr.OfPoint).ToList();
                double u0 = pts.Min(p => p.DotProduct(dirX)), u1 = pts.Max(p => p.DotProduct(dirX));
                double v0 = pts.Min(p => p.DotProduct(dirY)), v1 = pts.Max(p => p.DotProduct(dirY));
                double z = tr.OfPoint(new XYZ(0, 0, c.Z(b.Z0))).Z;
                res.Add(new Placement
                {
                    Box = b,
                    Corner = dirX * u0 + dirY * v0 + new XYZ(0, 0, z),
                    Angle = Math.Atan2(dirX.Y, dirX.X),
                    Lx = u1 - u0,
                    Ly = v1 - v0,
                    Lz = Ft(b.Z1 - b.Z0),
                });
            }
            return res;
        }

        static void Apply(Document doc, FamilyInstance a, Placement p, FamilyInstance window)
        {
            SetLen(a, AccessoryFamily.PX, p.Lx);
            SetLen(a, AccessoryFamily.PY, p.Ly);
            SetLen(a, AccessoryFamily.PZ, p.Lz);
            SetText(a, ElKind, p.Box.Kind);
            SetText(a, ElPlace, p.Box.Label);
            SetText(a, ElWindowId, window.Id.IntegerValueCompat().ToString(CultureInfo.InvariantCulture));
            SetLen(a, ElLength, Ft(p.Box.Length));
            SetLen(a, ElWidth, Ft(p.Box.Width));
            SetLen(a, ElThickness, Ft(p.Box.Thickness));

            var mat = MaterialFor(doc, window.Symbol, p.Box);
            var mp = a.LookupParameter(AccessoryFamily.PMat);
            if (mat != null && mp != null && !mp.IsReadOnly) mp.Set(mat);

            // рабочий набор и стадия — как у окна
            CopyParam(window, a, BuiltInParameter.ELEM_PARTITION_PARAM);
            if (a.CreatedPhaseId != window.CreatedPhaseId) try { a.CreatedPhaseId = window.CreatedPhaseId; } catch { }

            if (a.Location is LocationPoint lp)
            {
                if (!lp.Point.IsAlmostEqualTo(p.Corner)) lp.Point = p.Corner;
                double d = Normalize(p.Angle - lp.Rotation);
                if (Math.Abs(d) > 1e-9) lp.Rotate(Line.CreateUnbound(p.Corner, XYZ.BasisZ), d);
            }
        }

        /// <summary>Материал детали — по имени из схемы окна (параметров материала у окна нет: меняется только в редакторе).</summary>
        static ElementId MaterialFor(Document doc, FamilySymbol windowType, Box b) =>
            string.IsNullOrEmpty(b.Material) ? null : WindowMaterials.GetOrCreate(doc, b.Material);

        static double Normalize(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        static void SetLen(Element e, string name, double v)
        {
            var p = e.LookupParameter(name);
            if (p != null && !p.IsReadOnly && Math.Abs(p.AsDouble() - v) > 1e-9) p.Set(v);
        }

        static void SetText(Element e, string name, string v)
        {
            var p = e.LookupParameter(name)
                    ?? throw new InvalidOperationException($"у семейства «{AccessoryFamily.Name}» нет параметра «{name}»");
            if (!p.IsReadOnly && p.AsString() != v) p.Set(v ?? "");
        }

        static void CopyParam(Element from, Element to, BuiltInParameter bip)
        {
            var s = from.get_Parameter(bip);
            var d = to.get_Parameter(bip);
            if (s == null || d == null || d.IsReadOnly || s.AsInteger() == d.AsInteger()) return;
            try { d.Set(s.AsInteger()); } catch { }
        }

        /// <summary>Предупреждения Revit при массовом обновлении не показываем.</summary>
        class HideWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var f in fa.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }
    }

    /// <summary>Комплектация: выбранные окна или вся модель (с удалением элементов без окна).</summary>
    [Transaction(TransactionMode.Manual)]
    public class UpdateAccessoriesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc.Document;

            var selected = uidoc.Selection.GetElementIds().Select(doc.GetElement).Where(Accessories.IsEditorWindow)
                .Cast<FamilyInstance>().ToList();
            bool whole = selected.Count == 0;
            if (whole)
            {
                var td = new TaskDialog("Комплектация окон")
                {
                    MainInstruction = "Обновить комплектацию всех окон редактора в модели?",
                    MainContent = "Подоконники, отливы и откосы будут подогнаны под окна и стены. Элементы, чьих окон в модели нет, будут удалены.\n\n" +
                                  "Чтобы обработать только часть окон, выделите их и запустите команду снова.",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel
                };
                if (td.Show() != TaskDialogResult.Ok) return Result.Cancelled;
            }

            try
            {
                var report = Accessories.Update(doc, whole ? Accessories.AllWindows(doc) : selected, removeOrphans: whole);
                TaskDialog.Show("Комплектация окон", report.ToString());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
