using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WindowEditor.Model;
using WindowEditor.UI;
using static WindowEditor.RevitSide.WindowParams;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Угловой узел наружного угла 90°: стойка углового соединителя + пустота, вырезающая угловой квадрат
    /// обеих стен на высоту проёма. Семейство «обобщённая модель», не на основе, «вырезать пустотами при загрузке».
    ///
    /// Система координат семейства: начало — наружный угол здания на отметке низа проёма;
    /// стена A — по +X (занимает Y∈[0; t]), стена B — по +Y (X∈[0; t]).
    /// Стойка: X,Y ∈ [B; B+P] (B — от наружной грани стены до рамы), пустота: X,Y ∈ [−50; t].
    /// Геометрия задаётся размерами, поэтому на каждую комбинацию размеров — своё семейство (имя содержит размеры).
    /// </summary>
    public class CornerSpec
    {
        public double Post;          // сторона стойки, мм (0 — пустой проём, стойки нет)
        public double FrameOffset;   // B, мм
        public double Wall;          // t, мм
        public double CutZ0, CutZ1;  // пустота по высоте от низа проёма, мм
        public double PostZ0, PostZ1;
        public string Material;

        public string FamilyName => Post > 0
            ? $"WE_Угловой узел {Post:0}x{Post:0} рама {FrameOffset:0} стена {Wall:0} h{CutZ1 - CutZ0:0} ст{PostZ0:0}-{PostZ1:0}"
            : $"WE_Угловой узел пустой стена {Wall:0} h{CutZ1 - CutZ0:0}";
    }

    public static class CornerNodeFamily
    {
        public const string Prefix = "WE_Угловой узел";
        const string MatParam = "WE_Материал";

        /// <summary>Типоразмер узла в проекте; при необходимости создаёт и загружает семейство. Нужна открытая транзакция в проекте.</summary>
        public static FamilySymbol Ensure(Application app, Document proj, CornerSpec spec)
        {
            var fam = new FilteredElementCollector(proj).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name == spec.FamilyName);
            if (fam == null)
            {
                var path = Create(app, spec);
                if (!proj.LoadFamily(path, out fam))
                    fam = new FilteredElementCollector(proj).OfClass(typeof(Family)).Cast<Family>().First(f => f.Name == spec.FamilyName);
            }
            var sym = fam.GetFamilySymbolIds().Select(proj.GetElement).OfType<FamilySymbol>().First();
            if (!sym.IsActive) sym.Activate();
            return sym;
        }

        static string Create(Application app, CornerSpec spec)
        {
            var dir = Path.Combine(AppDataDir(), "CornerNodes");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, spec.FamilyName + ".rfa");

            var template = Settings.GetGenericTemplate(app.FamilyTemplatePath)
                ?? throw new InvalidOperationException("Не выбран шаблон «Типовая модель».");
            var doc = app.NewFamilyDocument(template);
            try
            {
                using (var t = new Transaction(doc, "Угловой узел"))
                {
                    t.Start();
                    var fam = doc.OwnerFamily;
                    fam.FamilyCategory = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
                    fam.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS)?.Set(1);
                    fam.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.Set(0);
                    var fm = doc.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType("Узел");
                    EnsureShared(app, doc, ElementShared, true);
                    var mp = EnsureFamilyParam(fm, MatParam, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                    if (!string.IsNullOrWhiteSpace(spec.Material)) fm.Set(mp, MaterialId(doc, spec.Material));

                    var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    if (spec.Post > 0)
                    {
                        var post = Box(doc, sp, spec.FrameOffset, spec.FrameOffset + spec.Post, spec.PostZ0, spec.PostZ1, true);
                        var p = post.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                        if (p != null) fm.AssociateElementParameterToFamilyParameter(p, mp);
                    }
                    Box(doc, sp, -50, spec.Wall, spec.CutZ0, spec.CutZ1, false);
                    t.Commit();
                }
                doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally { doc.Close(false); }
            return path;
        }

        /// <summary>Квадратная призма X,Y ∈ [a; b], Z ∈ [z0; z1] (мм).</summary>
        static Extrusion Box(Document doc, SketchPlane sp, double a, double b, double z0, double z1, bool solid)
        {
            XYZ P(double x, double y) => new XYZ(Ft(x), Ft(y), 0);
            var loop = new CurveArray();
            loop.Append(Line.CreateBound(P(a, a), P(b, a)));
            loop.Append(Line.CreateBound(P(b, a), P(b, b)));
            loop.Append(Line.CreateBound(P(b, b), P(a, b)));
            loop.Append(Line.CreateBound(P(a, b), P(a, a)));
            var prof = new CurveArrArray(); prof.Append(loop);
            var ext = doc.FamilyCreate.NewExtrusion(solid, prof, sp, Ft(z1 - z0));
            ext.StartOffset = Ft(z0);
            ext.EndOffset = Ft(z1);
            return ext;
        }

        static ElementId MaterialId(Document doc, string name) => WindowMaterials.GetOrCreate(doc, name);
    }

    /// <summary>
    /// «Угловое окно»: два окна в стенах, сходящихся наружным углом 90°.
    /// Проверяет окна, выравнивает их к углу, ставит угловой узел и вырезает угол обеих стен.
    /// Повторный запуск для того же угла заменяет узел (после правки окон).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CornerWindowCommand : IExternalCommand
    {
        class Side
        {
            public FamilyInstance Inst;
            public WindowConfig Cfg;
            public WindowGeometry Geo;
            public Wall Wall;
            public XYZ N;        // наружная нормаль стены
            public XYZ FaceO;    // точка на наружной грани
            public XYZ U;        // вдоль стены от угла к окну
            public double ZBottom; // низ проёма, футы, абсолютная отметка
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc.Document;

            var picked = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<FamilyInstance>()
                .Where(EditWindowCommand.IsWindow).Take(2).ToList();
            try
            {
                while (picked.Count < 2)
                {
                    var r = uidoc.Selection.PickObject(ObjectType.Element, new WindowPick(),
                        picked.Count == 0 ? "Угловое окно: выберите первое окно у угла" : "Выберите второе окно у того же угла");
                    var fi = (FamilyInstance)doc.GetElement(r);
                    if (picked.All(p => p.Id != fi.Id)) picked.Add(fi);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            var errors = new List<string>();
            var sides = picked.Select(i => Describe(doc, i, errors)).ToList();
            if (errors.Count > 0) { message = string.Join("\n", errors); return Result.Failed; }
            var a = sides[0]; var b = sides[1];

            if (a.Wall.Id == b.Wall.Id) { message = "Окна стоят в одной стене. Нужны две стены, сходящиеся углом."; return Result.Failed; }
            if (Math.Abs(a.N.DotProduct(b.N)) > 1e-3) { message = "Стены не перпендикулярны: поддерживается только наружный угол 90°."; return Result.Failed; }

            // наружный угол здания: пересечение наружных граней
            var c = Intersect(a.N, a.FaceO, b.N, b.FaceO);
            foreach (var s in sides)
            {
                var tdir = new XYZ(-s.N.Y, s.N.X, 0);
                var loc = ((LocationPoint)s.Inst.Location).Point;
                s.U = tdir.DotProduct(Flat(loc) - c) >= 0 ? tdir : -tdir;
            }
            // наружный угол: стена B уходит от угла в сторону помещения стены A
            if (b.U.DotProduct(-a.N) <= 0 || a.U.DotProduct(-b.N) <= 0)
            {
                message = "Это внутренний угол. Поддерживается только наружный угол 90°.";
                return Result.Failed;
            }

            // сторона угла у каждого окна (как видно с фасада) и согласованность настроек
            foreach (var s in sides)
            {
                // зритель снаружи смотрит по −n; его «право» = (−n) × Z
                var r = new XYZ(-s.N.Y, s.N.X, 0);
                var need = (-s.U).DotProduct(r) > 0 ? CornerSide.Right : CornerSide.Left;
                if (s.Cfg.Corner != need)
                    errors.Add($"«{s.Inst.Symbol.FamilyName}»: угол у этого окна {(need == CornerSide.Right ? "справа" : "слева")} (с фасада), " +
                               $"а в настройках — «{Name(s.Cfg.Corner)}». Откройте редактор → «Проём» → «Угол».");
            }
            if (a.Cfg.CornerLead == b.Cfg.CornerLead)
                errors.Add("Ведущим должно быть ровно одно из двух окон (флажок «Ведущее окно угла»).");
            if (a.Cfg.EmptyOpening != b.Cfg.EmptyOpening)
                errors.Add("Одно из окон — пустой проём, другое — нет.");
            void Same(double x, double y, string what)
            {
                if (Math.Abs(x - y) > 0.5) errors.Add($"Не совпадает {what}: {x:0} и {y:0} мм.");
            }
            Same(a.Cfg.WallThickness, b.Cfg.WallThickness, "толщина стены в настройках окон");
            Same(a.Cfg.BlockFromExterior, b.Cfg.BlockFromExterior, "положение рамы от наружной грани");
            if (!a.Cfg.EmptyOpening) Same(a.Cfg.CornerPostSize, b.Cfg.CornerPostSize, "размер углового соединителя");
            Same(Mm(a.ZBottom), Mm(b.ZBottom), "отметка низа проёма");
            var ia = CornerInterval(a); var ib = CornerInterval(b);
            Same(ia.T, ib.T, "высота проёма у угла");
            foreach (var s in sides)
                Same(Math.Round(Mm(s.Wall.Width)), s.Cfg.HostThickness, $"толщина стены «{s.Wall.Name}» и основы окна «{s.Inst.Symbol.FamilyName}» (без отделки)");
            if (errors.Count > 0) { message = string.Join("\n", errors); return Result.Failed; }

            var lead = a.Cfg.CornerLead ? a : b;
            var spec = new CornerSpec
            {
                Post = lead.Cfg.EmptyOpening ? 0 : lead.Cfg.CornerPostSize,
                FrameOffset = lead.Cfg.BlockFromExterior,
                Wall = lead.Cfg.WallThickness,
                CutZ0 = ia.B, CutZ1 = ia.T,
                Material = lead.Cfg.CornerPostMaterial
            };
            var post = lead.Geo.CornerPostFacade;
            if (post.HasValue) { spec.PostZ0 = post.Value.Y; spec.PostZ1 = post.Value.Top; }

            bool grouped = sides.Any(s => s.Inst.GroupId != ElementId.InvalidElementId);
            using (var t = new Transaction(doc, "Угловое окно"))
            {
                t.Start();

                // окна — к углу: край проёма на угловой стороне = угол здания
                foreach (var s in sides)
                {
                    var loc = ((LocationPoint)s.Inst.Location).Point;
                    double dist = s.U.DotProduct(Flat(loc) - c);
                    double need = Ft(s.Geo.OpeningWidth / 2);
                    if (Math.Abs(dist - need) > Ft(0.5))
                    {
                        if (s.Inst.GroupId != ElementId.InvalidElementId)
                        {
                            t.RollBack();
                            message = $"Окно «{s.Inst.Symbol.FamilyName}» в группе и не стоит вплотную к углу (нужно сдвинуть на {Mm(need - dist):0} мм). Сдвиньте его в редакторе группы.";
                            return Result.Failed;
                        }
                        ElementTransformUtils.MoveElement(doc, s.Inst.Id, s.U * (need - dist));
                    }
                }

                // прежний узел этого угла — заменить
                var old = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol.FamilyName.StartsWith(CornerNodeFamily.Prefix) &&
                                 fi.Location is LocationPoint lp && Flat(lp.Point).DistanceTo(c) < Ft(1))
                    .Select(fi => fi.Id).ToList();
                if (old.Count > 0) doc.Delete(old);

                var sym = CornerNodeFamily.Ensure(uiapp.Application, doc, spec);
                var level = doc.GetElement(lead.Inst.LevelId) as Level ?? doc.GetElement(lead.Wall.LevelId) as Level;
                var node = doc.Create.NewFamilyInstance(new XYZ(c.X, c.Y, level.Elevation), sym, level, StructuralType.NonStructural);
                node.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM)?.Set(lead.ZBottom - level.Elevation);

                // поворот: X семейства → вдоль одной стены, Y → вдоль другой (без отражения)
                var ux = a.U; var uy = b.U;
                if (ux.CrossProduct(uy).Z < 0) { ux = b.U; uy = a.U; }
                double ang = Math.Atan2(ux.Y, ux.X);
                if (Math.Abs(ang) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, node.Id, Line.CreateBound(new XYZ(c.X, c.Y, 0), new XYZ(c.X, c.Y, 1)), ang);

                node.LookupParameter(ElKind)?.Set(spec.Post > 0 ? "Угловой соединитель" : "Угловой узел пустого проёма");
                node.LookupParameter(ElPlace)?.Set($"{a.Inst.Symbol.FamilyName} / {b.Inst.Symbol.FamilyName}");
                node.LookupParameter(ElLength)?.Set(Ft(spec.PostZ1 - spec.PostZ0));
                node.LookupParameter(ElWidth)?.Set(Ft(spec.Post));
                node.LookupParameter(ElThickness)?.Set(Ft(spec.Post));

                // вырезать угловой квадрат обеих стен
                foreach (var s in sides)
                    if (InstanceVoidCutUtils.CanBeCutWithVoid(s.Wall) && !InstanceVoidCutUtils.InstanceVoidCutExists(s.Wall, node))
                        InstanceVoidCutUtils.AddInstanceVoidCut(doc, s.Wall, node);

                t.Commit();
            }

            NotifyWindow.Show("Угловое окно",
                (spec.Post > 0 ? $"Угловой соединитель {spec.Post:0}×{spec.Post:0} мм поставлен, угол стен вырезан." : "Угол пустого проёма вырезан.") +
                (grouped ? "\n\nОкна в группе: добавьте узел в группу вручную («Изменить группу» → «Добавить»), иначе в копиях группы его не будет." : ""));
            return Result.Succeeded;
        }

        static Interval CornerInterval(Side s) =>
            s.Cfg.Corner == CornerSide.Right ? s.Geo.Core[s.Geo.Core.Count - 1] : s.Geo.Core[0];

        static string Name(CornerSide c) => c == CornerSide.Left ? "слева" : c == CornerSide.Right ? "справа" : "нет";

        static Side Describe(Document doc, FamilyInstance inst, List<string> errors)
        {
            var s = new Side { Inst = inst };
            s.Cfg = WindowConfig.FromJson(inst.Symbol.LookupParameter(Config)?.AsString());
            if (s.Cfg == null) { errors.Add($"«{inst.Symbol.FamilyName}» создано не редактором окон."); return s; }
            s.Geo = new WindowGeometry(s.Cfg);
            s.Wall = inst.Host as Wall;
            if (s.Wall == null || !(s.Wall.Location is LocationCurve lc) || !(lc.Curve is Line))
            { errors.Add($"«{inst.Symbol.FamilyName}»: основа — не прямая стена."); return s; }

            var face = HostObjectUtils.GetSideFaces(s.Wall, ShellLayerType.Exterior).FirstOrDefault();
            var pf = face == null ? null : s.Wall.GetGeometryObjectFromReference(face) as PlanarFace;
            s.N = Flat(s.Wall.Orientation).Normalize();
            s.FaceO = pf != null ? Flat(pf.Origin) : Flat(((LocationCurve)s.Wall.Location).Curve.GetEndPoint(0)) + s.N * (s.Wall.Width / 2);

            // «Высота нижнего бруса» отсчитывается до плоскости «Подоконник», а не до начала семейства,
            // поэтому низ проёма = начало семейства в модели + отметка низа проёма в семействе
            double famSill = inst.Symbol.LookupParameter(SillZ)?.AsDouble() ?? 0;
            s.ZBottom = inst.GetTotalTransform().Origin.Z + famSill;
            if (FamilyCoords.InstanceFrame(inst).HasReflection) // у GetTransform отражения не бывает
                errors.Add($"«{inst.Symbol.FamilyName}» отзеркалено — сначала исправьте (кнопка «Проверить окна»).");
            return s;
        }

        static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        /// <summary>Пересечение двух вертикальных плоскостей n·p = n·o в плане.</summary>
        static XYZ Intersect(XYZ n1, XYZ o1, XYZ n2, XYZ o2)
        {
            double c1 = n1.DotProduct(o1), c2 = n2.DotProduct(o2);
            double det = n1.X * n2.Y - n1.Y * n2.X;
            return new XYZ((c1 * n2.Y - n1.Y * c2) / det, (n1.X * c2 - c1 * n2.X) / det, 0);
        }

        class WindowPick : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is FamilyInstance && EditWindowCommand.IsWindow(e);
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
