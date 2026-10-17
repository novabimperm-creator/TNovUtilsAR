using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using WindowEditor.Model;
using MRect = WindowEditor.Model.Rect;
using static WindowEditor.RevitSide.WindowParams;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Перестраивает окно в документе семейства, сделанном из шаблона WE_Окно.rfa (см. TemplateBuilder), по WindowConfig:
    /// рамы, импосты, створки, стёкла, соединители, обозначения открывания, размеры пустот проёма с четвертями
    /// и параметры для спецификаций. Комплектация — отдельными элементами модели (Accessories).
    ///
    /// Тела строятся на опорной плоскости привязки рамы («Наружный», «Четверть», ось, «Внутренние»), а плоскости
    /// шаблона закреплены на гранях стены — поэтому рама держит отступ от своей грани в стене любой толщины.
    ///
    /// «Своя» геометрия помечена: тела — подкатегориями WE_*, линии — стилями WE_*. При повторной правке удаляется только она.
    /// </summary>
    public class WindowFamilyBuilder
    {
        const string SubFrame = "WE_Рама";
        const string SubSash = "WE_Створка";
        const string SubGlass = "WE_Стекло";
        const string SubPanel = "WE_Панель";
        const string SubSymbol = "WE_Схема открывания";
        const string SubZone = "WE_Зона открывания";
        const string ZoneParam = "WE_Зона открывания";      // видимость зон открывания (да/нет, тип)
        const string ZoneMaterial = "WE_Зона открывания";   // прозрачный, как «N Воздух» в каталоге
        const double TiltDeg = 10;                          // откидывание в разрезе

        readonly Document _doc;
        readonly Application _app;
        readonly WindowConfig _cfg;
        WindowGeometry _geo;
        double _sillZ;
        SketchPlane _anchor;   // эскизная плоскость на опорной плоскости привязки рамы
        double _anchorY;       // её положение по Y в семействе
        double _anchorDepth;   // её глубина от наружной грани стены по схеме, мм
        readonly Dictionary<string, Category> _subcats = new Dictionary<string, Category>();
        readonly Dictionary<string, ElementId> _mats = new Dictionary<string, ElementId>();

        public WindowFamilyBuilder(Application app, Document familyDoc, WindowConfig cfg)
        {
            if (!familyDoc.IsFamilyDocument) throw new ArgumentException("Нужен документ семейства.");
            _app = app; _doc = familyDoc; _cfg = cfg;
            var missing = TemplateBuilder.Missing(familyDoc);
            if (missing.Count > 0)
                throw new InvalidOperationException("Семейство сделано не из шаблона WE_Окно. Нет: " + string.Join(", ", missing) + ".");
            _cfg.ExteriorPositiveY = true; // фасад шаблона — со стороны +Y
        }

        public void Build(string typeName)
        {
            using (var t = new Transaction(_doc, "Генерация окна"))
            {
                // предупреждения Revit при генерации пользователю не показываем
                var opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new HideWarnings());
                t.SetFailureHandlingOptions(opts);
                t.Start();
                var fm = _doc.FamilyManager;
                _geo = new WindowGeometry(_cfg);

                var type = fm.Types.Cast<FamilyType>().FirstOrDefault(x => x.Name == typeName);
                if (type == null)
                {
                    if (fm.CurrentType == null || fm.Types.Size == 0) type = fm.NewType(typeName);
                    else { fm.RenameCurrentType(typeName); type = fm.CurrentType; }
                }
                fm.CurrentType = type;

                // одно семейство — один тип (имя типа = имя семейства): остальные типы удаляем
                foreach (var other in fm.Types.Cast<FamilyType>().Where(x => x.Name != typeName).ToList())
                {
                    fm.CurrentType = other;
                    fm.DeleteCurrentType();
                }
                fm.CurrentType = type;
                // размеры закреплены формулами прошлой генерации — снимаем, чтобы записать новые
                foreach (var p in Lockable(fm).Where(p => !string.IsNullOrEmpty(p.Formula)))
                    fm.SetFormula(p, null);

                // «При загрузке вырезать с полостями» — окно должны резать пустоты (угловой узел и т.п.)
                _doc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS)?.Set(1);

                EnsureFamilyParam(fm, Config, GroupTypeId.Data, SpecTypeId.String.Text, false);
                EnsureFamilyParam(fm, GenVoids, GroupTypeId.Data, SpecTypeId.String.Text, false);
                EnsureShared(_app, _doc, WindowShared, false);

                // Ширина/Высота семейства = проём (для марок и спецификаций проёмов)
                SetDouble(fm, FamilyWidth(fm), Ft(_geo.OpeningWidth), "Ширина");
                SetDouble(fm, FamilyHeight(fm), Ft(_geo.OpeningHeight), "Высота");
                _doc.Regenerate();

                PrepareSubcategories();
                DeleteGenerated(fm);
                _sillZ = FindSillZ();
                fm.Set(EnsureFamilyParam(fm, SillZ, GroupTypeId.Data, SpecTypeId.Length, false), _sillZ);

                // стена-основа семейства — толщиной из схемы: тогда неподвижная геометрия (2D-линии открывания)
                // совпадает с рамой в стене этой толщины, и Revit одинаково ставит экземпляры при любой загрузке
                AdjustHostWall();
                // пустоты шаблона — по контуру проёма; плоскость «Четверть» встаёт до выбора привязки
                // (запись параметров заодно заставляет Revit перерешить плоскости после смены толщины стены)
                TemplateBuilder.SetVoids(_doc, _geo, _cfg);
                fm.Set(fm.get_Parameter(GenVoids), "");
                _doc.Regenerate();
                SetupAnchor();

                SetupMaterials(fm);
                if (!_cfg.EmptyOpening) BuildFrames();
                WriteResults(fm);
                LockDimensions(fm);

                t.Commit();
            }
        }

        // =====================================================================
        // Координаты
        // =====================================================================

        FamilyCoords C => new FamilyCoords(_cfg, _geo, _sillZ);
        double X(double x) => C.X(x);
        double Z(double z) => C.Z(z);
        XYZ P(double x, double z) => new XYZ(X(x), _anchorY, Z(z));

        /// <summary>Толщина стены-основы в семействе = толщине основы из схемы, без отделки (меняем самый толстый слой).</summary>
        void AdjustHostWall()
        {
            try
            {
                var wall = new FilteredElementCollector(_doc).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                var cs = wall?.WallType.GetCompoundStructure();
                if (cs == null) return;
                var layers = cs.GetLayers();
                double total = layers.Sum(l => l.Width), target = Ft(_cfg.HostThickness);
                if (Math.Abs(total - target) < 1e-6) return;
                int idx = Enumerable.Range(0, layers.Count).OrderByDescending(i => layers[i].Width).First();
                double w = layers[idx].Width + (target - total);
                if (w <= Ft(1)) return;
                cs.SetLayerWidth(idx, w);
                wall.WallType.SetCompoundStructure(cs);
                _doc.Regenerate();
            }
            catch (Exception) { /* не критично: 2D-линии открывания лягут по толщине стены шаблона */ }
        }

        /// <summary>
        /// Опорная плоскость шаблона, от которой отсчитывается рама, — по привязке схемы. Глубины схемы (от наружной
        /// грани всей стены, с отделкой) пересчитываются от неё; плоскости шаблона стоят на гранях стены-основы, а отделка —
        /// отдельные стены снаружи и изнутри основы, поэтому глубина плоскости = отделка + положение в основе.
        /// </summary>
        void SetupAnchor()
        {
            string name; double depth;
            switch (_cfg.Anchor)
            {
                case FrameAnchor.Quarter: name = TemplateBuilder.PlaneQuarter; depth = _cfg.FinishExterior + TemplateBuilder.QuarterPlaneMm(_cfg); break;
                case FrameAnchor.Center: name = TemplateBuilder.PlaneCenterY; depth = _cfg.FinishExterior + _cfg.HostThickness / 2; break;
                case FrameAnchor.Interior: name = TemplateBuilder.PlaneInterior; depth = _cfg.WallThickness - _cfg.FinishInterior; break;
                default: name = TemplateBuilder.PlaneExterior; depth = _cfg.FinishExterior; break;
            }
            var rp = new FilteredElementCollector(_doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().First(r => r.Name == name);
            _anchor = SketchPlane.Create(_doc, rp.Id);
            _anchorY = rp.GetPlane().Origin.Y;
            _anchorDepth = depth;
        }

        // =====================================================================
        // Рамы, створки, стёкла
        // =====================================================================

        void BuildFrames()
        {
            var c = _cfg;
            double d0 = c.BlockFromExterior, d1 = d0 + c.FrameDepth, mid = (d0 + d1) / 2;
            var frameMat = _mats["WE_Мат. Рама"];
            _mats.TryGetValue("WE_Мат. Створки", out var sashMat);

            const double rebate = 12; // заход стекла в фальц
            foreach (var sg in _geo.Sections)
            {
                var lay = sg.Layout;
                // рама вместе с импостами — одно тело по контуру (в т.ч. Г- и Т-образному)
                SolidPoly(lay.FrameLoops, d0, d1, SubFrame, frameMat);

                foreach (var u in lay.Units.Where(u => u.Cell.IsSash))
                {
                    var inner = u.R.Inset(c.SashWidth);
                    var sash = Solid(new[] { u.R, inner }, mid - c.SashDepth / 2, mid + c.SashDepth / 2, SubSash, sashMat != null && sashMat != ElementId.InvalidElementId ? sashMat : frameMat);
                    OpeningGraphics(sash, u.R, inner, u.Cell.Fill, mid + c.SashDepth / 2);
                }
                // импосты внутри створок
                foreach (var m in lay.Mullions.Where(m => m.InSash))
                    Solid(new[] { m.R }, mid - c.SashDepth / 2, mid + c.SashDepth / 2, SubSash, sashMat != null && sashMat != ElementId.InvalidElementId ? sashMat : frameMat);

                foreach (var f in lay.Fields)
                {
                    var pane = _geo.Panes.First(p => p.Cell == f.Cell);
                    var mat = _mats[pane.MaterialParam];
                    double th = pane.IsPanel ? c.PanelThickness : c.GlassThickness;
                    Solid(new[] { f.R.Inset(-rebate) }, mid - th / 2, mid + th / 2, pane.IsPanel ? SubPanel : SubGlass, mat);
                }
            }

            // соединительный профиль между секциями
            for (int i = 0; i + 1 < _geo.Sections.Count; i++)
            {
                var a = _geo.Sections[i].Block; var b = _geo.Sections[i + 1].Block;
                double z0 = Math.Max(a.Y, b.Y), z1 = Math.Min(a.Top, b.Top);
                if (b.X - a.Right > 0.5 && z1 - z0 > 1)
                    Solid(new[] { new MRect(a.Right, z0, b.X - a.Right, z1 - z0) }, d0, d1, SubFrame, frameMat);
            }
        }

        /// <summary>Выдавливание по профилю на фасадной плоскости. loops[0] — внешний контур, остальные — отверстия.</summary>
        Extrusion Solid(MRect[] loops, double dFrom, double dTo, string subcat, ElementId mat)
        {
            var profile = new CurveArrArray();
            foreach (var r in loops)
                if (r.W >= 1 && r.H >= 1)
                    profile.Append(Loop(new List<(double x, double z)> { (r.X, r.Y), (r.Right, r.Y), (r.Right, r.Top), (r.X, r.Top) }));
            if (profile.Size == 0) return null;
            var ext = Extrude(profile, dFrom, dTo, true);
            ext.Subcategory = _subcats[subcat];
            Associate(ext, mat);
            return ext;
        }

        /// <summary>Выдавливание по многоугольникам (внешние контуры и отверстия) — для рамы сложной формы.</summary>
        Extrusion SolidPoly(List<List<(double x, double z)>> loops, double dFrom, double dTo, string subcat, ElementId mat)
        {
            var profile = new CurveArrArray();
            foreach (var l in loops) if (l.Count >= 3) profile.Append(Loop(l));
            if (profile.Size == 0) return null;
            var ext = Extrude(profile, dFrom, dTo, true);
            ext.Subcategory = _subcats[subcat];
            Associate(ext, mat);
            return ext;
        }

        /// <summary>Выдавливание от глубины dFrom до dTo (мм от наружной грани по схеме) — смещениями от плоскости привязки.</summary>
        Extrusion Extrude(CurveArrArray profile, double dFrom, double dTo, bool solid)
        {
            // вглубь стены — это −Y семейства; смещения выдавливания идут по нормали эскиза
            double ny = _anchor.GetPlane().Normal.Y;
            double o0 = -Ft(dFrom - _anchorDepth) / ny, o1 = -Ft(dTo - _anchorDepth) / ny;
            var ext = _doc.FamilyCreate.NewExtrusion(solid, profile, _anchor, Math.Abs(o1 - o0));
            ext.EndOffset = Math.Max(o0, o1);
            ext.StartOffset = Math.Min(o0, o1);
            return ext;
        }

        CurveArray Loop(List<(double x, double z)> pts)
        {
            var arr = new CurveArray();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                arr.Append(Line.CreateBound(P(a.x, a.z), P(b.x, b.z)));
            }
            return arr;
        }

        void Associate(Element e, ElementId mat)
        {
            if (mat == null || mat == ElementId.InvalidElementId) return;
            e.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(mat);
        }

        // =====================================================================
        // Открывание: 3D-линии, зона открывания, 2D в плане и в разрезе (как в каталоге pmN.Окно v3)
        // =====================================================================

        /// <summary>
        /// Для открывающейся створки. outer — створка, inner — её заполнение (по нему треугольники), dIn — глубина
        /// внутренней грани створки от наружной грани стены (мм, по схеме). Вершина треугольника — сторона петель.
        ///  • 3D-линии — модельные, на эскизе, привязанном к внутренней грани тела створки: идут за створкой при
        ///    любой толщине стены; видны в 3D и на фасадах, в плане и сбоку скрыты.
        ///  • Зона открывания — тело поворота створки на 90° внутрь вокруг петель (поворотные и дверные), прозрачная,
        ///    только в 3D; видимость — параметр «WE_Зона открывания».
        ///  • План — линия створки и дуга (как у дверей каталога): грубо — створка под 30° без дуги, средне/высоко —
        ///    под 90° с дугой. Разрез (виды «Слева/Справа») — откидывание на 10°.
        ///    2D-линии плана и разреза нельзя привязать к плоскостям шаблона — они от внутренней грани стены по толщине из схемы.
        /// </summary>
        void OpeningGraphics(Extrusion sash, MRect outer, MRect inner, FillKind f, double dIn)
        {
            bool left = f == FillKind.TurnLeft || f == FillKind.TiltTurnLeft || f == FillKind.DoorLeft || f == FillKind.DoorTiltLeft;
            bool right = f == FillKind.TurnRight || f == FillKind.TiltTurnRight || f == FillKind.DoorRight || f == FillKind.DoorTiltRight;
            bool tilt = f == FillKind.Tilt || f == FillKind.TiltTurnLeft || f == FillKind.TiltTurnRight || f == FillKind.DoorTiltLeft || f == FillKind.DoorTiltRight;
            if (!left && !right && !tilt) return;

            var face = sash == null ? null : InteriorFace(sash);
            var style = _subcats[SubSymbol].GetGraphicsStyle(GraphicsStyleType.Projection);

            // ---- 3D-линии открывания ----
            if (face != null)
            {
                var sp = SketchPlane.Create(_doc, face.Reference);
                double fy = face.Origin.Y;
                XYZ F(double x, double z) => new XYZ(X(x), fy, Z(z));
                var vis = new FamilyElementVisibility(FamilyElementVisibilityType.Model)
                {
                    IsShownInFrontBack = true, IsShownInLeftRight = false, IsShownInTopBottom = false,
                    IsShownInCoarse = true, IsShownInMedium = true, IsShownInFine = true
                };
                void Seg(double x1, double z1, double x2, double z2)
                {
                    var mc = _doc.FamilyCreate.NewModelCurve(Line.CreateBound(F(x1, z1), F(x2, z2)), sp);
                    mc.LineStyle = style;
                    mc.SetVisibility(vis);
                }
                void Tri(double ax, double az, double bx, double bz, double cx, double cz) { Seg(ax, az, bx, bz); Seg(bx, bz, cx, cz); }
                var g = inner;
                double midZ = g.Y + g.H / 2, midX = g.X + g.W / 2;
                if (left) Tri(g.Right, g.Top, g.X, midZ, g.Right, g.Y);
                if (right) Tri(g.X, g.Top, g.Right, midZ, g.X, g.Y);
                if (tilt) Tri(g.X, g.Top, midX, g.Y, g.Right, g.Top);

                // ---- зона открывания ----
                if (left || right) OpeningZone(sp, fy, outer, left);
            }

            // ---- 2D: план и разрез ----
            // Символьные линии не привязать к плоскостям шаблона: они неподвижны в координатах семейства. Стена-основа
            // семейства подогнана под толщину из схемы (AdjustHostWall), поэтому при такой стене в проекте линии точно
            // на створке; в стене другой толщины сдвинутся на половину разницы.
            double yIn = _anchorY - Ft(dIn - _anchorDepth); // в помещение — −Y
            if (left || right) PlanSwing(outer, left, yIn, style);
            if (tilt) SectionTilt(outer, yIn, style);
        }

        /// <summary>Внутренняя (со стороны помещения, −Y) грань тела створки — с ссылкой для эскиза.</summary>
        PlanarFace InteriorFace(Extrusion ext)
        {
            _doc.Regenerate();
            var opt = new Options { ComputeReferences = true };
            PlanarFace best = null;
            foreach (var go in ext.get_Geometry(opt))
                if (go is Solid s)
                    foreach (Autodesk.Revit.DB.Face fc in s.Faces)
                        if (fc is PlanarFace pf && pf.Reference != null && Math.Abs(Math.Abs(pf.FaceNormal.Y) - 1) < 1e-6
                            && (best == null || pf.Origin.Y < best.Origin.Y))
                            best = pf;
            return best;
        }

        void OpeningZone(SketchPlane sp, double fy, MRect r, bool hingeLeft)
        {
            XYZ F(double x, double z) => new XYZ(X(x), fy, Z(z));
            var loop = new CurveArray();
            loop.Append(Line.CreateBound(F(r.X, r.Y), F(r.Right, r.Y)));
            loop.Append(Line.CreateBound(F(r.Right, r.Y), F(r.Right, r.Top)));
            loop.Append(Line.CreateBound(F(r.Right, r.Top), F(r.X, r.Top)));
            loop.Append(Line.CreateBound(F(r.X, r.Top), F(r.X, r.Y)));
            var prof = new CurveArrArray(); prof.Append(loop);
            double hx = hingeLeft ? r.X : r.Right;
            var axis = Line.CreateBound(F(hx, r.Y), F(hx, r.Top)); // вверх, +Z
            // Направление поворота у Revit зависит от ориентации эскиза — строим четверть оборота, проверяем,
            // что зона ушла в помещение (−Y), иначе берём другую четверть.
            Revolution rev = null;
            foreach (var (a0, a1) in new[] { (0.0, 0.5 * Math.PI), (1.5 * Math.PI, 2 * Math.PI) })
            {
                Revolution r0;
                try { r0 = _doc.FamilyCreate.NewRevolution(true, prof, sp, axis, a0, a1); }
                catch (Exception) { continue; }
                _doc.Regenerate();
                var bb = r0.get_BoundingBox(null);
                if (bb != null && bb.Max.Y <= fy + Ft(1)) { rev = r0; break; }
                _doc.Delete(r0.Id);
            }
            if (rev == null) return; // зона — вспомогательная, без неё окно всё равно строится
            rev.Subcategory = _subcats[SubZone];
            Associate(rev, WindowMaterials.GetOrCreate(_doc, ZoneMaterial));
            rev.SetVisibility(new FamilyElementVisibility(FamilyElementVisibilityType.Model)
            {
                IsShownInFrontBack = false, IsShownInLeftRight = false, IsShownInTopBottom = false,
                IsShownInCoarse = true, IsShownInMedium = true, IsShownInFine = true
            });
            var fm = _doc.FamilyManager;
            var vp = fm.get_Parameter(ZoneParam);
            if (vp == null)
            {
                vp = fm.AddParameter(ZoneParam, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, false);
                fm.Set(vp, 1);
            }
            var ip = rev.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            if (ip != null) fm.AssociateElementParameterToFamilyParameter(ip, vp);
        }

        SketchPlane _planSp, _sideSp;

        static FamilyElementVisibility ViewVis(bool coarse, bool medium, bool fine) =>
            new FamilyElementVisibility(FamilyElementVisibilityType.ViewSpecific)
            { IsShownInCoarse = coarse, IsShownInMedium = medium, IsShownInFine = fine };

        void PlanSwing(MRect r, bool hingeLeft, double yIn, GraphicsStyle style)
        {
            if (_planSp == null)
            {
                var sill = new FilteredElementCollector(_doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .First(p => p.Name == TemplateBuilder.PlaneSill);
                _planSp = SketchPlane.Create(_doc, sill.Id);
            }
            double z = _planSp.GetPlane().Origin.Z;
            double hx = X(hingeLeft ? r.X : r.Right), fx = X(hingeLeft ? r.Right : r.X);
            double len = Math.Abs(fx - hx), sx = Math.Sign(fx - hx);
            var hinge = new XYZ(hx, yIn, z);
            var closed = new XYZ(fx, yIn, z);
            var open90 = new XYZ(hx, yIn - len, z);
            double c30 = Math.Cos(Math.PI / 6), s30 = Math.Sin(Math.PI / 6);
            var open30 = new XYZ(hx + sx * len * c30, yIn - len * s30, z);
            var mid45 = new XYZ(hx + sx * len * Math.Cos(Math.PI / 4), yIn - len * Math.Sin(Math.PI / 4), z);

            void Add(Curve c, FamilyElementVisibility v)
            {
                var sc = _doc.FamilyCreate.NewSymbolicCurve(c, _planSp);
                sc.LineStyle = style;
                sc.SetVisibility(v);
            }
            Add(Line.CreateBound(hinge, open30), ViewVis(true, false, false));
            Add(Line.CreateBound(hinge, open90), ViewVis(false, true, true));
            Add(Arc.Create(open90, closed, mid45), ViewVis(false, true, true));
        }

        readonly HashSet<string> _sectionDone = new HashSet<string>();

        void SectionTilt(MRect r, double yIn, GraphicsStyle style)
        {
            // все створки рисуются в одной боковой плоскости — одинаковые по высоте совпали бы
            if (!_sectionDone.Add($"{Math.Round(r.Y)}|{Math.Round(r.H)}")) return;
            if (_sideSp == null)
                _sideSp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero));
            double z0 = Z(r.Y), h = Ft(r.H), a = TiltDeg * Math.PI / 180;
            var hinge = new XYZ(0, yIn, z0);
            var closedTop = new XYZ(0, yIn, z0 + h);
            var openTop = new XYZ(0, yIn - h * Math.Sin(a), z0 + h * Math.Cos(a));
            var mid = new XYZ(0, yIn - h * Math.Sin(a / 2), z0 + h * Math.Cos(a / 2));
            void Add(Curve c, FamilyElementVisibility v)
            {
                var sc = _doc.FamilyCreate.NewSymbolicCurve(c, _sideSp);
                sc.LineStyle = style;
                sc.SetVisibility(v);
            }
            Add(Line.CreateBound(hinge, openTop), ViewVis(true, true, true));
            Add(Arc.Create(closedTop, openTop, mid), ViewVis(false, true, true));
        }

        // =====================================================================
        // Материалы: назначаются телам напрямую, без параметров типа — менять только в редакторе
        // =====================================================================

        void SetupMaterials(FamilyManager fm)
        {
            // параметры материалов прошлых версий (WE_Мат. …, v1) убираем: материал меняется только в редакторе
            foreach (var p in fm.Parameters.Cast<FamilyParameter>().ToList())
            {
                var n = p.Definition.Name;
                if (n.StartsWith(MatPrefix) || n == "WE_Материал рамы" || n == "WE_Материал стекла")
                    fm.RemoveParameter(p);
            }
            foreach (var slot in _geo.Materials)
                _mats[slot.Param] = WindowMaterials.GetOrCreate(_doc, slot.Get());
        }

        // =====================================================================
        // Служебное
        // =====================================================================

        void PrepareSubcategories()
        {
            var parent = _doc.OwnerFamily.FamilyCategory;
            foreach (var name in new[] { SubFrame, SubSash, SubGlass, SubPanel, SubSymbol, SubZone })
            {
                Category sub = null;
                foreach (Category c in parent.SubCategories) if (c.Name == name) { sub = c; break; }
                _subcats[name] = sub ?? _doc.Settings.Categories.NewSubcategory(parent, name);
            }
            // зона открывания — как в каталоге: прозрачный материал, белые линии
            var zone = _subcats[SubZone];
            zone.Material = (Material)_doc.GetElement(WindowMaterials.GetOrCreate(_doc, ZoneMaterial));
            zone.LineColor = new Color(254, 254, 254);
        }

        void DeleteGenerated(FamilyManager fm)
        {
            var ids = new List<ElementId>();
            foreach (var f in new FilteredElementCollector(_doc).OfClass(typeof(GenericForm)).Cast<GenericForm>())
                if (f.IsSolid && f.Subcategory != null && f.Subcategory.Name.StartsWith("WE_")) ids.Add(f.Id);
            // свои линии — символьные (план, разрез) и модельные (3D-открывание)
            foreach (var c in new FilteredElementCollector(_doc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                if ((c is SymbolicCurve || c is ModelCurve) && c.LineStyle != null && c.LineStyle.Name.StartsWith("WE_")) ids.Add(c.Id);
            foreach (var fi in new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                if (fi.Symbol.Family.Name == AccessoryFamily.Name) ids.Add(fi.Id);
            // прямоугольный проём из шаблона заменяется пустотами
            ids.AddRange(new FilteredElementCollector(_doc).OfClass(typeof(Opening)).ToElementIds());
            // свои пустоты прошлой генерации
            var gp = fm.get_Parameter(GenVoids);
            var prev = gp != null ? fm.CurrentType.AsString(gp) : null;
            if (!string.IsNullOrEmpty(prev))
                foreach (var uid in prev.Split(';'))
                {
                    var e = _doc.GetElement(uid);
                    if (e != null) ids.Add(e.Id);
                }
            if (ids.Count > 0) _doc.Delete(ids.Distinct().ToList());
        }

        /// <summary>Отметка низа проёма: опорная плоскость «Подоконник»/«Sill», иначе 0.</summary>
        double FindSillZ()
        {
            var names = new[] { "Sill", "Подоконник", "Низ", "Низ проема", "Низ проёма" };
            foreach (var rp in new FilteredElementCollector(_doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
            {
                if (!names.Any(n => string.Equals(n, rp.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var pl = rp.GetPlane();
                if (Math.Abs(Math.Abs(pl.Normal.Z) - 1) < 1e-6) return pl.Origin.Z;
            }
            return 0;
        }

        void WriteResults(FamilyManager fm)
        {
            void Set(string name, Action<FamilyParameter> setter)
            {
                var p = fm.get_Parameter(name);
                if (p != null) setter(p);
            }
            Set(Config, p => fm.Set(p, _cfg.ToJson()));
            Set(Scheme, p => fm.Set(p, _cfg.SchemeText()));
            Set(Sashes, p => fm.Set(p, _geo.SashCount));
            Set(Doors, p => fm.Set(p, _geo.DoorCount));
            Set(Fixeds, p => fm.Set(p, _geo.FixedCount));
            Set(GlassArea, p => fm.Set(p, FtSq(_geo.GlassArea)));
            Set(PanelArea, p => fm.Set(p, FtSq(_geo.PanelArea)));
            Set(WindowArea, p => fm.Set(p, FtSq(_geo.Sections.Sum(s => s.Block.Area))));
            Set(MullionLen, p => fm.Set(p, Ft(_geo.MullionLength)));
            Set(BlockWidth, p => fm.Set(p, Ft(_geo.BlockBounds.W)));
            Set(BlockHeight, p => fm.Set(p, Ft(_geo.BlockBounds.H)));
            Set(OpenWidth, p => fm.Set(p, Ft(_geo.OpeningWidth)));
            Set(OpenHeight, p => fm.Set(p, Ft(_geo.OpeningHeight)));
        }

        // =====================================================================
        // Размеры — только из редактора
        // =====================================================================

        /// <summary>
        /// Размерные параметры типа, которые задаёт редактор: длины и площади WE_* и «Ширина»/«Высота».
        /// Их закрепляем формулами-константами, чтобы в проекте их нельзя было поменять мимо редактора.
        /// </summary>
        IEnumerable<FamilyParameter> Lockable(FamilyManager fm)
        {
            var w = FamilyWidth(fm); var h = FamilyHeight(fm);
            return fm.GetParameters().Where(p =>
                !p.IsInstance && !p.IsReporting && p.StorageType == StorageType.Double
                && (p.Definition.GetDataType() == SpecTypeId.Length || p.Definition.GetDataType() == SpecTypeId.Area)
                && (p.Definition.Name.StartsWith("WE_") || p.Id == w?.Id || p.Id == h?.Id)).ToList();
        }

        // Как записать константу в формулу — зависит от языка Revit («мм»/«mm», «м²»/«m²»): подбираем по факту.
        static int _lengthStyle = -1, _areaStyle = -1;

        void LockDimensions(FamilyManager fm)
        {
            var values = Lockable(fm).Select(p => (p, v: fm.CurrentType.AsDouble(p) ?? 0, area: p.Definition.GetDataType() == SpecTypeId.Area)).ToList();

            string[] Styles(double v, bool area)
            {
                double shown = area ? UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.SquareMeters) : Mm(v);
                string n = Math.Round(shown, area ? 6 : 3).ToString(area ? "0.######" : "0.###", System.Globalization.CultureInfo.InvariantCulture);
                return area
                    ? new[] { n + " м²", n + " m²", n, n.Replace('.', ',') + " м²" }
                    : new[] { n + " мм", n + " mm", n, n.Replace('.', ',') + " мм" };
            }

            bool Holds((FamilyParameter p, double v, bool area) x) =>
                !string.IsNullOrEmpty(x.p.Formula) && Math.Abs((fm.CurrentType.AsDouble(x.p) ?? double.NaN) - x.v) < 1e-6;

            // формат подбираем на первом параметре каждого вида (длина, площадь), если он ещё не известен
            foreach (bool area in new[] { false, true })
            {
                if ((area ? _areaStyle : _lengthStyle) >= 0) continue;
                var first = values.FirstOrDefault(x => x.area == area);
                if (first.p == null) continue;
                var styles = Styles(first.v, area);
                for (int i = 0; i < styles.Length; i++)
                    if (TryFormula(fm, first.p, styles[i], first.v))
                    {
                        if (area) _areaStyle = i; else _lengthStyle = i;
                        break;
                    }
            }

            // известным форматом — все разом, одна регенерация
            foreach (var (p, v, area) in values)
            {
                int known = area ? _areaStyle : _lengthStyle;
                if (known >= 0) try { fm.SetFormula(p, Styles(v, area)[known]); } catch (Exception) { /* проверим ниже */ }
            }
            _doc.Regenerate();

            // где формула не встала — оставляем просто значение (и в следующий раз подберём формат заново)
            foreach (var x in values.Where(x => !Holds(x)).ToList())
            {
                if (x.area) _areaStyle = -1; else _lengthStyle = -1;
                fm.SetFormula(x.p, null);
                fm.Set(x.p, x.v);
            }
        }

        bool TryFormula(FamilyManager fm, FamilyParameter p, string formula, double expected)
        {
            try
            {
                fm.SetFormula(p, formula);
                _doc.Regenerate();
                return Math.Abs((fm.CurrentType.AsDouble(p) ?? double.NaN) - expected) < 1e-6;
            }
            catch (Exception) { return false; }
        }

        class HideWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                fa.DeleteAllWarnings();
                return FailureProcessingResult.Continue;
            }
        }

        static void SetDouble(FamilyManager fm, FamilyParameter p, double v, string label)
        {
            if (p == null) throw new InvalidOperationException($"В семействе не найден параметр «{label}». Используйте шаблон окна.");
            if (!string.IsNullOrEmpty(p.Formula))
                throw new InvalidOperationException($"Параметр «{label}» задан формулой — уберите формулу в семействе.");
            fm.Set(p, v);
        }
    }
}
