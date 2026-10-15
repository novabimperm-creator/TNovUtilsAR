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

        readonly Document _doc;
        readonly Application _app;
        readonly WindowConfig _cfg;
        WindowGeometry _geo;
        double _sillZ;
        SketchPlane _anchor;   // эскизная плоскость на опорной плоскости привязки рамы
        double _anchorY;       // её положение по Y в семействе
        double _anchorDepth;   // её глубина от наружной грани стены по схеме, мм
        readonly Dictionary<string, Category> _subcats = new Dictionary<string, Category>();
        readonly Dictionary<string, FamilyParameter> _matParams = new Dictionary<string, FamilyParameter>();

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

                // пустоты шаблона — по контуру проёма; плоскость «Четверть» встаёт до выбора привязки
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

        /// <summary>
        /// Опорная плоскость шаблона, от которой отсчитывается рама, — по привязке схемы. Глубины схемы (от наружной
        /// грани, по толщине стены из схемы) пересчитываются от неё, а в проекте она стоит на своей грани фактической стены.
        /// </summary>
        void SetupAnchor()
        {
            string name; double depth;
            switch (_cfg.Anchor)
            {
                case FrameAnchor.Quarter: name = TemplateBuilder.PlaneQuarter; depth = TemplateBuilder.QuarterPlaneMm(_cfg); break;
                case FrameAnchor.Center: name = TemplateBuilder.PlaneCenterY; depth = _cfg.WallThickness / 2; break;
                case FrameAnchor.Interior: name = TemplateBuilder.PlaneInterior; depth = _cfg.WallThickness; break;
                default: name = TemplateBuilder.PlaneExterior; depth = 0; break;
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
            var frameMat = _matParams["WE_Мат. Рама"];
            _matParams.TryGetValue("WE_Мат. Створки", out var sashMat);

            const double rebate = 12; // заход стекла в фальц
            foreach (var sg in _geo.Sections)
            {
                var lay = sg.Layout;
                // рама вместе с импостами — одно тело по контуру (в т.ч. Г- и Т-образному)
                SolidPoly(lay.FrameLoops, d0, d1, SubFrame, frameMat);

                foreach (var u in lay.Units.Where(u => u.Cell.IsSash))
                {
                    var inner = u.R.Inset(c.SashWidth);
                    Solid(new[] { u.R, inner }, mid - c.SashDepth / 2, mid + c.SashDepth / 2, SubSash, sashMat ?? frameMat);
                    OpeningSymbol(inner, u.Cell.Fill);
                }
                // импосты внутри створок
                foreach (var m in lay.Mullions.Where(m => m.InSash))
                    Solid(new[] { m.R }, mid - c.SashDepth / 2, mid + c.SashDepth / 2, SubSash, sashMat ?? frameMat);

                foreach (var f in lay.Fields)
                {
                    var pane = _geo.Panes.First(p => p.Cell == f.Cell);
                    var mat = _matParams[pane.MaterialParam];
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
        Extrusion Solid(MRect[] loops, double dFrom, double dTo, string subcat, FamilyParameter mat)
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
        Extrusion SolidPoly(List<List<(double x, double z)>> loops, double dFrom, double dTo, string subcat, FamilyParameter mat)
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

        void Associate(Element e, FamilyParameter mat)
        {
            if (mat == null) return;
            var p = e.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (p != null) _doc.FamilyManager.AssociateElementParameterToFamilyParameter(p, mat);
        }

        void OpeningSymbol(MRect g, FillKind f)
        {
            var style = _subcats[SubSymbol].GetGraphicsStyle(GraphicsStyleType.Projection);
            void Seg(double x1, double z1, double x2, double z2)
            {
                var c = _doc.FamilyCreate.NewSymbolicCurve(Line.CreateBound(P(x1, z1), P(x2, z2)), _anchor);
                c.LineStyle = style;
            }
            void Tri(double ax, double az, double bx, double bz, double cx, double cz) { Seg(ax, az, bx, bz); Seg(bx, bz, cx, cz); }

            bool left = f == FillKind.TurnLeft || f == FillKind.TiltTurnLeft || f == FillKind.DoorLeft || f == FillKind.DoorTiltLeft;
            bool right = f == FillKind.TurnRight || f == FillKind.TiltTurnRight || f == FillKind.DoorRight || f == FillKind.DoorTiltRight;
            bool tilt = f == FillKind.Tilt || f == FillKind.TiltTurnLeft || f == FillKind.TiltTurnRight || f == FillKind.DoorTiltLeft || f == FillKind.DoorTiltRight;
            double midZ = g.Y + g.H / 2, midX = g.X + g.W / 2;
            if (left) Tri(g.Right, g.Top, g.X, midZ, g.Right, g.Y);
            if (right) Tri(g.X, g.Top, g.Right, midZ, g.X, g.Y);
            if (tilt) Tri(g.X, g.Top, midX, g.Y, g.Right, g.Top);
        }

        // =====================================================================
        // Материалы: параметр типа на каждый элемент и каждое стекло
        // =====================================================================

        void SetupMaterials(FamilyManager fm)
        {
            var wanted = new HashSet<string>(_geo.Materials.Select(m => m.Param));
            // убрать параметры исчезнувших элементов (и старые параметры v1)
            foreach (var p in fm.Parameters.Cast<FamilyParameter>().ToList())
            {
                var n = p.Definition.Name;
                if ((n.StartsWith(MatPrefix) && !wanted.Contains(n)) || n == "WE_Материал рамы" || n == "WE_Материал стекла")
                    fm.RemoveParameter(p);
            }
            foreach (var slot in _geo.Materials)
            {
                var fp = EnsureFamilyParam(fm, slot.Param, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                var id = MaterialId(slot.Get());
                if (id != ElementId.InvalidElementId) fm.Set(fp, id);
                _matParams[slot.Param] = fp;
            }
        }

        /// <summary>Материал по имени; если в семействе нет — создаётся. В проекте при загрузке приоритет у одноимённого материала проекта.</summary>
        ElementId MaterialId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;
            var m = new FilteredElementCollector(_doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
            if (m != null) return m.Id;
            var id = Material.Create(_doc, name);
            var mat = (Material)_doc.GetElement(id);
            var low = name.ToLowerInvariant();
            if (low.Contains("стекл") || low.Contains("glass"))
            {
                mat.Color = new Color(190, 220, 235);
                mat.Transparency = 70;
            }
            return id;
        }

        // =====================================================================
        // Служебное
        // =====================================================================

        void PrepareSubcategories()
        {
            var parent = _doc.OwnerFamily.FamilyCategory;
            foreach (var name in new[] { SubFrame, SubSash, SubGlass, SubPanel, SubSymbol })
            {
                Category sub = null;
                foreach (Category c in parent.SubCategories) if (c.Name == name) { sub = c; break; }
                _subcats[name] = sub ?? _doc.Settings.Categories.NewSubcategory(parent, name);
            }
        }

        void DeleteGenerated(FamilyManager fm)
        {
            var ids = new List<ElementId>();
            foreach (var f in new FilteredElementCollector(_doc).OfClass(typeof(GenericForm)).Cast<GenericForm>())
                if (f.IsSolid && f.Subcategory != null && f.Subcategory.Name.StartsWith("WE_")) ids.Add(f.Id);
            foreach (var c in new FilteredElementCollector(_doc).OfClass(typeof(CurveElement)).OfType<SymbolicCurve>())
                if (c.LineStyle != null && c.LineStyle.Name.StartsWith("WE_")) ids.Add(c.Id);
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

        static void SetDouble(FamilyManager fm, FamilyParameter p, double v, string label)
        {
            if (p == null) throw new InvalidOperationException($"В семействе не найден параметр «{label}». Используйте шаблон окна.");
            if (!string.IsNullOrEmpty(p.Formula))
                throw new InvalidOperationException($"Параметр «{label}» задан формулой — уберите формулу в семействе.");
            fm.Set(p, v);
        }
    }
}
