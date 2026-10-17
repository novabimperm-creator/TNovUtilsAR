using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using static WindowEditor.RevitSide.WindowParams;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Заготовка шаблона окна WE_Окно.rfa из «Метрическая система, окно.rft» (см. TEMPLATE.md).
    /// Делает всё, что умеет API: плоскости, параметры, размеры с метками, 6 пустот с гранями на плоскостях.
    /// Вручную после неё: «Вырезать» стену каждой из 6 пустот (связь пустота → стена-основа API не создаёт).
    ///
    /// Кромки пустот отсчитываются от базовых плоскостей, сдвинутых на WE_Вырез.База (10 мм) влево от «Слева»
    /// (левой кромки проёма) и вниз от «Подоконник»: размер с меткой не может быть нулевым и менять знак,
    /// а кромки пустот не выходят левее и ниже проёма — значения всегда положительные. Далеко базу не выносим:
    /// плоскости попадают в рамку выделения окна в проекте.
    /// Глубина: О* — от «Четверть» до «Внутренние.Запас», Н* — от «Наружный.Запас» до «Четверть».
    /// </summary>
    public static class TemplateBuilder
    {
        // ---- контракт шаблона (его проверяет и заполняет генерация окна) ----
        public const string PlaneExterior = "Наружный";
        public const string PlaneInterior = "Внутренние";
        public const string PlaneQuarter = "Четверть";
        public const string PlaneExtMargin = "Наружный.Запас";
        public const string PlaneIntMargin = "Внутренние.Запас";
        public const string PlaneBaseX = "WE_База.X";
        public const string PlaneBaseZ = "WE_База.Z";
        public const string PlaneSill = "Подоконник";
        public const string PlaneLeft = "Слева";
        const string PlaneCenterX = "По центру (Влево/Вправо)";

        public const string PWallThickness = "WE_Стена.Толщина";
        public const string PQuarterDepth = "WE_Четверть.Глубина";
        public const string PMargin = "WE_Вырез.Запас";
        public const string PBase = "WE_Вырез.База";

        public static readonly string[] CoreVoids = { "О1", "О2", "О3" };
        public static readonly string[] ExtVoids = { "Н1", "Н2", "Н3" };
        public static IEnumerable<string> AllVoids => CoreVoids.Concat(ExtVoids);

        /// <summary>Параметры кромок пустоты: расстояния от базовых плоскостей до левой/правой/нижней/верхней кромки.</summary>
        public static string PX0(string v) => $"WE_Вырез.{v}.X0";
        public static string PX1(string v) => $"WE_Вырез.{v}.X1";
        public static string PZ0(string v) => $"WE_Вырез.{v}.Z0";
        public static string PZ1(string v) => $"WE_Вырез.{v}.Z1";

        public const double BaseMm = 10;
        const double MarginMm = 50;
        const double QuarterMm = 120;

        /// <summary>Создать заготовку и сохранить в outPath. Вызывать вне транзакций. Возвращает отчёт.</summary>
        public static string Build(Application app, string rftPath, string outPath)
        {
            var log = new StringBuilder();
            var doc = app.NewFamilyDocument(rftPath);
            try
            {
                using (var t = new Transaction(doc, "Шаблон окна WindowEditor"))
                {
                    t.Start();
                    new Impl(doc, log).Run();
                    t.Commit();
                }
                log.AppendLine(Flex(doc));
                doc.SaveAs(outPath, new SaveAsOptions { OverwriteExistingFile = true });
                log.AppendLine("Сохранено: " + outPath);
            }
            finally
            {
                doc.Close(false);
            }
            return log.ToString();
        }

        class Impl
        {
            readonly Document _doc;
            readonly StringBuilder _log;
            readonly FamilyManager _fm;
            readonly View _plan, _front;
            readonly Dictionary<string, ReferencePlane> _planes = new Dictionary<string, ReferencePlane>();
            Wall _wall;
            int _dimSlot;

            public Impl(Document doc, StringBuilder log)
            {
                _doc = doc; _log = log; _fm = doc.FamilyManager;
                _plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .First(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
                // фасадный вид «Наружный»: смотрим на стену снаружи (+Y)
                _front = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .First(v => !v.IsTemplate && v.ViewType == ViewType.Elevation && v.ViewDirection.IsAlmostEqualTo(XYZ.BasisY));
            }

            public void Run()
            {
                if (_fm.CurrentType == null || string.IsNullOrWhiteSpace(_fm.CurrentType.Name)) _fm.NewType("WE_Шаблон");

                _wall = new FilteredElementCollector(_doc).OfClass(typeof(Wall)).Cast<Wall>().First();
                foreach (ReferencePlane rp in new FilteredElementCollector(_doc).OfClass(typeof(ReferencePlane))) _planes[rp.Name] = rp;
                Need(PlaneExterior); Need(PlaneInterior); Need(PlaneSill); Need(PlaneLeft);

                // проём шаблона заменяют пустоты
                var openings = new FilteredElementCollector(_doc).OfClass(typeof(Opening)).ToElementIds();
                if (openings.Count > 0) _doc.Delete(openings);

                _doc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS)?.Set(1);

                double t2 = _wall.Width / 2; // «Наружный» = +t/2, «Внутренние» = −t/2

                // толщина стены — параметр-отчёт
                var pWall = Param(PWallThickness, true);
                _fm.MakeReporting(pWall);
                Dim(_plan, PlaneExterior, PlaneInterior, pWall, alongX: false);

                var pQ = Param(PQuarterDepth, false);
                var pM = Param(PMargin, false);
                var pB = Param(PBase, false);
                _fm.Set(pQ, Ft(QuarterMm)); _fm.Set(pM, Ft(MarginMm)); _fm.Set(pB, Ft(BaseMm));

                PlaneY(PlaneQuarter, t2 - Ft(QuarterMm));
                PlaneY(PlaneExtMargin, t2 + Ft(MarginMm));
                PlaneY(PlaneIntMargin, -t2 - Ft(MarginMm));
                Dim(_plan, PlaneExterior, PlaneQuarter, pQ, alongX: false);
                Dim(_plan, PlaneExterior, PlaneExtMargin, pM, alongX: false);
                Dim(_plan, PlaneInterior, PlaneIntMargin, pM, alongX: false);

                double sill = _planes[PlaneSill].GetPlane().Origin.Z;
                double left = _planes[PlaneLeft].GetPlane().Origin.X;
                PlaneX(PlaneBaseX, left - Ft(BaseMm));
                PlaneZ(PlaneBaseZ, sill - Ft(BaseMm));
                Dim(_plan, PlaneBaseX, PlaneLeft, pB, alongX: true);
                Dim(_front, PlaneSill, PlaneBaseZ, pB, alongX: false);

                // проём по умолчанию из шаблона: Ширина × Высота от «Подоконник»
                double w = _fm.CurrentType.AsDouble(FamilyWidth(_fm)) ?? Ft(1000);
                double h = _fm.CurrentType.AsDouble(FamilyHeight(_fm)) ?? Ft(1500);
                // Пустоты разложены рядом, не внахлёст: иначе при «Вырезать» выбирается одна и та же верхняя,
                // и остальные остаются невырезанными. О — три полосы по ширине, Н — три полосы по высоте.
                double gap = Ft(50), qs = Ft(65);
                double cw = (w - 2 * gap) / 3;
                for (int i = 0; i < CoreVoids.Length; i++)
                {
                    double x0 = -w / 2 + i * (cw + gap);
                    MakeVoid(CoreVoids[i], x0, x0 + cw, sill, sill + h, PlaneQuarter, PlaneIntMargin);
                }
                double eh = (h - qs - 2 * gap) / 3;
                for (int i = 0; i < ExtVoids.Length; i++)
                {
                    double z0 = sill + i * (eh + gap);
                    MakeVoid(ExtVoids[i], -w / 2 + qs, w / 2 - qs, z0, z0 + eh, PlaneExtMargin, PlaneQuarter);
                }

                _log.AppendLine($"Создано: плоскостей {_planes.Count}, пустот 6, параметров {_fm.Parameters.Size}.");
            }

            void Need(string name)
            {
                if (!_planes.ContainsKey(name)) throw new InvalidOperationException($"В шаблоне нет опорной плоскости «{name}».");
            }

            FamilyParameter Param(string name, bool instance) =>
                EnsureFamilyParam(_fm, name, GroupTypeId.Constraints, SpecTypeId.Length, instance);

            // ---- опорные плоскости ----

            /// <summary>Плоскость y = const (параллельна стене), рисуется на плане.</summary>
            ReferencePlane PlaneY(string name, double y) =>
                Add(name, _doc.FamilyCreate.NewReferencePlane(new XYZ(-Ft(1500), y, 0), new XYZ(Ft(1500), y, 0), XYZ.BasisZ, _plan));

            /// <summary>Плоскость x = const, рисуется на плане.</summary>
            ReferencePlane PlaneX(string name, double x) =>
                Add(name, _doc.FamilyCreate.NewReferencePlane(new XYZ(x, -Ft(800), 0), new XYZ(x, Ft(800), 0), XYZ.BasisZ, _plan));

            /// <summary>Плоскость z = const, рисуется на фасаде.</summary>
            ReferencePlane PlaneZ(string name, double z) =>
                Add(name, _doc.FamilyCreate.NewReferencePlane(new XYZ(-Ft(1500), 0, z), new XYZ(Ft(1500), 0, z), XYZ.BasisY, _front));

            ReferencePlane Add(string name, ReferencePlane rp)
            {
                rp.Name = name;
                _planes[name] = rp;
                return rp;
            }

            // ---- размеры с метками ----

            /// <summary>
            /// Размер между параллельными плоскостями. alongX — плоскости x = const (размерная линия вдоль X);
            /// иначе на плане — плоскости y = const, на фасаде — z = const.
            /// </summary>
            void Dim(View view, string a, string b, FamilyParameter label, bool alongX)
            {
                _doc.Regenerate(); // у только что созданной плоскости ссылка для размера готова только после регенерации
                var pa = _planes[a].GetPlane().Origin;
                var pb = _planes[b].GetPlane().Origin;
                double off = Ft(1800 + 150 * _dimSlot++);
                Line line;
                if (alongX) line = view == _plan
                    ? Line.CreateBound(new XYZ(pa.X, -off, 0), new XYZ(pb.X, -off, 0))
                    : Line.CreateBound(new XYZ(pa.X, 0, -off), new XYZ(pb.X, 0, -off));
                else if (view == _plan) line = Line.CreateBound(new XYZ(-off, pa.Y, 0), new XYZ(-off, pb.Y, 0));
                else line = Line.CreateBound(new XYZ(-off, 0, pa.Z), new XYZ(-off, 0, pb.Z));

                var refs = new ReferenceArray();
                refs.Append(_planes[a].GetReference());
                refs.Append(_planes[b].GetReference());
                Dimension d;
                try { d = _doc.FamilyCreate.NewLinearDimension(view, line, refs); }
                catch (Exception ex) { throw new InvalidOperationException($"Размер «{a}» — «{b}» на виде «{view.Name}»: {ex.Message}", ex); }
                try { d.FamilyLabel = label; }
                catch (Exception ex) { throw new InvalidOperationException($"Метка {label.Definition.Name} на размере «{a}» — «{b}»: {ex.Message}", ex); }
            }

            // ---- пустоты ----

            void MakeVoid(string v, double x0, double x1, double z0, double z1, string fromPlane, string toPlane)
            {
                // плоскости кромок и их размеры от базовых плоскостей
                double bx = _planes[PlaneBaseX].GetPlane().Origin.X, bz = _planes[PlaneBaseZ].GetPlane().Origin.Z;
                var px0 = Param(PX0(v), false); var px1 = Param(PX1(v), false);
                var pz0 = Param(PZ0(v), false); var pz1 = Param(PZ1(v), false);
                _fm.Set(px0, x0 - bx); _fm.Set(px1, x1 - bx); _fm.Set(pz0, z0 - bz); _fm.Set(pz1, z1 - bz);

                string nL = $"WE_{v}.Л", nP = $"WE_{v}.П", nN = $"WE_{v}.Н", nV = $"WE_{v}.В";
                PlaneX(nL, x0); PlaneX(nP, x1); PlaneZ(nN, z0); PlaneZ(nV, z1);
                Dim(_plan, PlaneBaseX, nL, px0, alongX: true);
                Dim(_plan, PlaneBaseX, nP, px1, alongX: true);
                Dim(_front, PlaneBaseZ, nN, pz0, alongX: false);
                Dim(_front, PlaneBaseZ, nV, pz1, alongX: false);

                // выдавливание: эскиз на плоскости «fromPlane», глубина до «toPlane»
                double y0 = _planes[fromPlane].GetPlane().Origin.Y, y1 = _planes[toPlane].GetPlane().Origin.Y;
                var sp = SketchPlane.Create(_doc, _planes[fromPlane].Id);
                var loop = new CurveArray();
                var pts = new[] { new XYZ(x0, y0, z0), new XYZ(x1, y0, z0), new XYZ(x1, y0, z1), new XYZ(x0, y0, z1) };
                for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                var prof = new CurveArrArray(); prof.Append(loop);
                var ext = _doc.FamilyCreate.NewExtrusion(false, prof, sp, Math.Abs(y1 - y0));
                // глубина отсчитывается по нормали эскиза, а она у плоскости может смотреть в любую сторону
                double dist = (y1 - y0) / sp.GetPlane().Normal.Y;
                ext.EndOffset = Math.Max(0, dist);
                ext.StartOffset = Math.Min(0, dist);
                _doc.Regenerate();

                // грани пустоты — на плоскости, с замками
                var faces = Faces(ext);
                Lock(_plan, faces, -XYZ.BasisX, nL); Lock(_plan, faces, XYZ.BasisX, nP);
                Lock(_front, faces, -XYZ.BasisZ, nN); Lock(_front, faces, XYZ.BasisZ, nV);
                var yFrom = y0 > y1 ? XYZ.BasisY : -XYZ.BasisY;
                Lock(_plan, faces, yFrom, fromPlane); Lock(_plan, faces, yFrom.Negate(), toPlane);
            }

            List<PlanarFace> Faces(Extrusion ext)
            {
                var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
                var res = new List<PlanarFace>();
                foreach (var go in ext.get_Geometry(opt))
                    if (go is Solid s && s.Faces.Size > 0)
                        foreach (Face f in s.Faces) if (f is PlanarFace pf && pf.Reference != null) res.Add(pf);
                if (res.Count < 6) throw new InvalidOperationException($"У пустоты {ext.Id} не найдены грани ({res.Count}).");
                return res;
            }

            void Lock(View view, List<PlanarFace> faces, XYZ normal, string plane)
            {
                // у пустоты нормали граней смотрят внутрь тела — грань ищем по совпадению с плоскостью
                var pl = _planes[plane].GetPlane();
                var f = faces.FirstOrDefault(x => x.FaceNormal.CrossProduct(pl.Normal).GetLength() < 1e-6
                                                  && Math.Abs(pl.Normal.DotProduct(x.Origin - pl.Origin)) < 1e-6)
                        ?? throw new InvalidOperationException($"Нет грани пустоты на плоскости «{plane}».");
                try
                {
                    var al = _doc.FamilyCreate.NewAlignment(view, _planes[plane].GetReference(), f.Reference);
                    al.IsLocked = true;
                }
                catch (Exception ex)
                {
                    var o = _planes[plane].GetPlane().Origin;
                    var fo = f.Origin;
                    var all = string.Join("; ", faces.Select(x => $"n={x.FaceNormal} o=({Mm(x.Origin.X):0},{Mm(x.Origin.Y):0},{Mm(x.Origin.Z):0})"));
                    throw new InvalidOperationException(
                        $"Грань {normal} → «{plane}» (плоскость в ({Mm(o.X):0},{Mm(o.Y):0},{Mm(o.Z):0}), грань в ({Mm(fo.X):0},{Mm(fo.Y):0},{Mm(fo.Z):0}), вид «{view.Name}»): {ex.Message}\nГрани: {all}", ex);
                }
            }
        }

        // =====================================================================
        // Использование шаблона при генерации окна
        // =====================================================================

        public const string PlaneCenterY = "По центру (Вперед/Назад)";

        /// <summary>Чего не хватает в документе семейства, чтобы считать его шаблоном WindowEditor. Пусто — шаблон.</summary>
        public static List<string> Missing(Document doc)
        {
            var res = new List<string>();
            if (!doc.IsFamilyDocument) { res.Add("это не семейство"); return res; }
            if (doc.OwnerFamily.FamilyCategory?.Id.IntegerValueCompat() != (long)BuiltInCategory.OST_Windows) res.Add("категория не «Окна»");
            var planes = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Select(e => e.Name));
            foreach (var p in new[] { PlaneExterior, PlaneInterior, PlaneQuarter, PlaneExtMargin, PlaneIntMargin, PlaneBaseX, PlaneBaseZ, PlaneSill, PlaneLeft, PlaneCenterY })
                if (!planes.Contains(p)) res.Add("плоскость «" + p + "»");
            var fm = doc.FamilyManager;
            var names = new List<string> { PQuarterDepth, PBase };
            foreach (var v in AllVoids) names.AddRange(new[] { PX0(v), PX1(v), PZ0(v), PZ1(v) });
            foreach (var n in names) if (fm.get_Parameter(n) == null) res.Add("параметр «" + n + "»");
            return res;
        }

        /// <summary>Открыть копию шаблона (сам файл шаблона не трогаем) и проверить её. Вызывать вне транзакций.</summary>
        public static Document OpenCopy(Application app, string templatePath)
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WindowEditor");
            System.IO.Directory.CreateDirectory(dir);
            foreach (var old in System.IO.Directory.GetFiles(dir, "template_*.rfa"))
                try { System.IO.File.Delete(old); } catch { /* занят — удалим в другой раз */ }
            var copy = System.IO.Path.Combine(dir, $"template_{Guid.NewGuid():N}.rfa");
            System.IO.File.Copy(templatePath, copy, true);

            var doc = app.OpenDocumentFile(copy);
            var missing = Missing(doc);
            if (missing.Count > 0)
            {
                doc.Close(false);
                throw new InvalidOperationException(
                    $"Файл «{templatePath}» не подходит как шаблон окна WindowEditor. Нет: {string.Join(", ", missing)}.\n" +
                    "Сделайте шаблон по WindowEditor/TEMPLATE.md.");
            }
            Rebase(doc);
            return doc;
        }

        /// <summary>
        /// Шаблон первой версии: база по X — от центра, WE_Вырез.База = 10 м (огромная рамка выделения окна).
        /// Переводит на базу от «Слева» в 10 мм, пустоты остаются на месте (их вырезы стены сохраняются).
        /// Возвращает true, если шаблон пришлось перевести. Вызывать вне транзакций.
        /// </summary>
        public static bool Rebase(Document doc)
        {
            var fm = doc.FamilyManager;
            var pB = fm.get_Parameter(PBase);
            var planes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToDictionary(r => r.Name, r => r);
            var oldDim = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().FirstOrDefault(d =>
            {
                FamilyParameter lab = null;
                try { lab = d.FamilyLabel; } catch { /* размер из нескольких сегментов */ }
                if (lab == null || lab.Id != pB.Id) return false;
                var names = d.References.Cast<Reference>().Select(r => doc.GetElement(r)?.Name).ToList();
                return names.Contains(PlaneBaseX) && names.Contains(PlaneCenterX);
            });
            if (oldDim == null) return false;

            using (var t = new Transaction(doc, "Перевод шаблона на ближнюю базу"))
            {
                t.Start();
                double bOld = fm.CurrentType.AsDouble(pB) ?? 0, bNew = Ft(BaseMm);
                double left = planes[PlaneLeft].GetPlane().Origin.X;          // −Ширина/2
                double sill = planes[PlaneSill].GetPlane().Origin.Z;
                // кромки сейчас: X = −bOld + X0, Z = (sill − bOld) + Z0; после: X = left − bNew + X0', Z = sill − bNew + Z0'
                double dx = -bOld - (left - bNew), dz = -bOld + bNew;
                var values = AllVoids.SelectMany(v => new[] { PX0(v), PX1(v), PZ0(v), PZ1(v) })
                    .ToDictionary(n => n, n => fm.CurrentType.AsDouble(fm.get_Parameter(n)) ?? 0);

                var plan = oldDim.View;
                doc.Delete(oldDim.Id);
                var refs = new ReferenceArray();
                refs.Append(planes[PlaneBaseX].GetReference());
                refs.Append(planes[PlaneLeft].GetReference());
                double y = -Ft(1800);
                var line = Line.CreateBound(new XYZ(planes[PlaneBaseX].GetPlane().Origin.X, y, 0), new XYZ(left, y, 0));
                var dim = doc.FamilyCreate.NewLinearDimension(plan, line, refs);
                // метку ставим, когда параметр равен текущему размеру, — тогда ничего не сдвинется
                fm.Set(pB, dim.Value ?? bOld);
                dim.FamilyLabel = pB;

                fm.Set(pB, bNew);
                // уменьшаем: сначала нижние/левые кромки, потом верхние/правые — ширина пустоты не уходит в минус
                foreach (var v in AllVoids)
                {
                    fm.Set(fm.get_Parameter(PX0(v)), values[PX0(v)] + dx);
                    fm.Set(fm.get_Parameter(PX1(v)), values[PX1(v)] + dx);
                    fm.Set(fm.get_Parameter(PZ0(v)), values[PZ0(v)] + dz);
                    fm.Set(fm.get_Parameter(PZ1(v)), values[PZ1(v)] + dz);
                }
                t.Commit();
            }
            return true;
        }

        /// <summary>
        /// Записать в параметры шаблона пустоты по контуру проёма и глубину четверти. Нужна открытая транзакция.
        /// Контур — в координатах схемы (мм: x от левого края проёма с фасада, z от низа проёма); фасад шаблона — +Y,
        /// поэтому x схемы идёт по −X семейства.
        /// </summary>
        public static void SetVoids(Document doc, Model.WindowGeometry geo, Model.WindowConfig cfg)
        {
            var fm = doc.FamilyManager;
            double b = BaseMm, w = geo.OpeningWidth;

            var core = Model.WindowGeometry.CoverRects(geo.Core, CoreVoids.Length)
                       ?? throw new InvalidOperationException($"Контур проёма слишком сложный: нужно больше {CoreVoids.Length} прямоугольников.");
            var ext = geo.HasQuarter
                ? Model.WindowGeometry.CoverRects(geo.Exterior, ExtVoids.Length)
                  ?? throw new InvalidOperationException($"Контур четверти слишком сложный: нужно больше {ExtVoids.Length} прямоугольников.")
                : core;

            void Set(string[] voids, List<Model.Rect> rects)
            {
                for (int i = 0; i < voids.Length; i++)
                {
                    var r = rects[Math.Min(i, rects.Count - 1)]; // лишние пустоты совпадают с последней
                    // «Слева» — кромка проёма с меньшим X семейства, это правый край с фасада: X семейства = w/2 − x
                    fm.Set(fm.get_Parameter(PX0(voids[i])), Ft(b + w - r.Right));
                    fm.Set(fm.get_Parameter(PX1(voids[i])), Ft(b + w - r.X));
                    fm.Set(fm.get_Parameter(PZ0(voids[i])), Ft(b + r.Y));
                    fm.Set(fm.get_Parameter(PZ1(voids[i])), Ft(b + r.Top));
                }
            }
            Set(CoreVoids, core);
            Set(ExtVoids, ext);

            // После смены толщины стены-основы (AdjustHostWall) Revit в редакторе семейств двигает «Наружный/
            // Внутренние», но не перерешает зависящие от них плоскости (четверть, запас) и даже переписывает их
            // параметры под фактическое положение (запас становится 0). Пустоты тогда режут не всю стену.
            // Помогает запись заведомо другого значения и обратно, с регенерацией между ними.
            void Resolve(string param, double mm)
            {
                var p = fm.get_Parameter(param);
                fm.Set(p, Ft(mm + 1));
                doc.Regenerate(); // без регенерации между записями Revit считает, что ничего не изменилось
                fm.Set(p, Ft(mm));
            }
            // При большой смене толщины решатель может поставить плоскость «по другую сторону» грани
            // (запас 50 мм внутрь стены вместо наружу). Поэтому сначала сами ставим зависимые плоскости на место.
            var planes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
            double PlaneOf(string name) => planes.First(r => r.Name == name).GetPlane().Origin.Y;
            void Place(string name, double y)
            {
                var rp = planes.FirstOrDefault(r => r.Name == name);
                double d = rp == null ? 0 : y - rp.GetPlane().Origin.Y;
                if (Math.Abs(d) > 1e-6) ElementTransformUtils.MoveElement(doc, rp.Id, new XYZ(0, d, 0));
            }
            double yExt = PlaneOf(PlaneExterior), yInt = PlaneOf(PlaneInterior);
            Place(PlaneExtMargin, yExt + Ft(MarginMm));
            Place(PlaneIntMargin, yInt - Ft(MarginMm));
            Place(PlaneQuarter, yExt - Ft(QuarterPlaneMm(cfg)));
            doc.Regenerate();

            // плоскость четверти нужна всегда (от неё идут обе пустоты); без четверти пустоты Н = О
            Resolve(PQuarterDepth, QuarterPlaneMm(cfg));
            Resolve(PMargin, MarginMm);
            doc.Regenerate();
        }

        /// <summary>Глубина плоскости «Четверть» от наружной грани, мм: не меньше 1 мм и меньше толщины стены.</summary>
        public static double QuarterPlaneMm(Model.WindowConfig cfg) =>
            Math.Max(1, Math.Min(cfg.QuarterDepth, cfg.WallThickness - 1));

        /// <summary>Проверка гибкости: меняем параметры и смотрим, что пустоты пошли за ними. Откатывается.</summary>
        static string Flex(Document doc)
        {
            var fm = doc.FamilyManager;
            using (var t = new Transaction(doc, "Проверка шаблона"))
            {
                t.Start();
                double b = Ft(BaseMm);
                // проём шаблона 1000: «Слева» на −500, О2 → −300..300
                fm.Set(fm.get_Parameter(PX0("О2")), b + Ft(200));
                fm.Set(fm.get_Parameter(PX1("О2")), b + Ft(800));
                fm.Set(fm.get_Parameter(PZ0("О2")), b + Ft(100));
                fm.Set(fm.get_Parameter(PZ1("О2")), b + Ft(2100));
                fm.Set(fm.get_Parameter(PQuarterDepth), Ft(80));
                doc.Regenerate();
                double sill = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .First(r => r.Name == PlaneSill).GetPlane().Origin.Z;
                var wall = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().First();
                double t2 = Mm(wall.Width / 2);
                var boxes = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().Where(e => !e.IsSolid)
                    .Select(e => e.get_BoundingBox(null))
                    .Select(bb => (x0: Mm(bb.Min.X), x1: Mm(bb.Max.X), z0: Mm(bb.Min.Z - sill), z1: Mm(bb.Max.Z - sill), y0: Mm(bb.Min.Y), y1: Mm(bb.Max.Y)))
                    .ToList();
                bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
                bool moved = boxes.Any(b => Near(b.x0, -300) && Near(b.x1, 300) && Near(b.z0, 100) && Near(b.z1, 2100)
                                            && Near(b.y0, -t2 - MarginMm) && Near(b.y1, t2 - 80));
                bool quarter = boxes.Count(b => Near(b.y0, t2 - 80) && Near(b.y1, t2 + MarginMm)) == 3;
                t.RollBack();
                string res = $"Флекс: пустот {boxes.Count}; О2 по параметрам {(moved ? "встала" : "НЕ встала")} в −300..300 × 100..2100; " +
                             $"пустоты Н за четвертью 80 мм {(quarter ? "пошли" : "НЕ пошли")} за глубиной.\n" +
                             string.Join("\n", boxes.Select(b => $"  X {b.x0:0}..{b.x1:0}  Z {b.z0:0}..{b.z1:0}  Y {b.y0:0}..{b.y1:0}"));
                if (!moved || !quarter) throw new InvalidOperationException("Шаблон не перестраивается по параметрам.\n" + res);
                return res;
            }
        }
    }
}
