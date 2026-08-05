using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtilsAR
{
    /// <summary>
    /// Виды витражей: витражи — это навесные стены (Wall с CurtainGrid), а не окна.
    /// На каждое уникальное значение «Маркировка типоразмера» (Вх-1, Вх-2…) создаётся
    /// вид-фасад (инструмент «Фасад», тип «Р_Основной») лицом к витражу, называется по
    /// маркировке, назначается шаблон, ставится марка стены («pmN.Марка_Витраж») и
    /// размеры: габарит/проём (по краям витража) и цепочка по стойкам сетки.
    /// Разрезы и цветовая область — следующим этапом.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoCurtainViewsCommand : IExternalCommand
    {
        private const string BUILD = "виды витражей v1 (виды, марка, размеры)";

        private const string ELEV_TYPE = "Р_Основной";
        private const string VIEW_TEMPLATE = "Д_АР_Фасад_Р_Окна";
        private const string MARK_PARAM = "Маркировка типоразмера";
        // Организация диспетчера: Стадия Р > Витражи.
        private const string ORG_VIEW_PARAM = "Орг.КатегорияВида";
        private const string ORG_VIEW_VALUE = "3. Стадия Р";
        private const string ORG_CONSTR_PARAM = "Орг.КатегорияКонструкц";
        private const string ORG_CONSTR_VALUE = "Витражи";
        // Тип размера обычный и для проёма.
        private const string DIM_TYPE = "Основной_2.5";
        private const string DIM_TYPE_OPENING = "Основной_2.5 (Проем)";
        private const string DIM_OPENING_SUFFIX = "(Проем)";
        // Марка витража: категория «Марки стен», предпочтение семейству с «Витраж».
        private const string TAG_PREFER = "Витраж";

        private const double MARKER_OFFSET_MM = 700.0;
        private const double CROP_MARGIN_MM = 400.0;
        private const double FAR_CLIP_EXTRA_MM = 200.0;
        private const double DIM_CHAIN_MM = 500.0;
        private const double DIM_OPENING_MM = 950.0;
        private const double TAG_ABOVE_MM = 600.0;

        /// <summary>Габаритный прямоугольник витража в координатах вида + грани краёв.</summary>
        private class CwGeom
        {
            public Transform T;
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public Reference Left, Right, Bottom, Top;       // грани краёв витража
            public List<Tuple<double, Reference>> VLines = new List<Tuple<double, Reference>>(); // верт. стойки (X)
            public List<Tuple<double, Reference>> HLines = new List<Tuple<double, Reference>>(); // гор. ригели (Y)
            public double CenX => (MinX + MaxX) / 2;
            public double CenY => (MinY + MaxY) / 2;
            public XYZ Pt(double x, double y) => T.OfPoint(new XYZ(x, y, 0));
            public bool XOk => Left != null && Right != null;
            public bool YOk => Bottom != null && Top != null;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            try
            {
                var report = new StringBuilder();
                report.AppendLine($"[{BUILD}]");
                if (RevitAPI.UiApplication == null) RevitAPI.Initialize(commandData);
                Logger.Initialize("Виды витражей", DateTime.Now, BUILD);

                ViewFamilyType vft = FindElevationType(doc, ELEV_TYPE);
                if (vft == null)
                {
                    PluginReport.Show($"Виды витражей [{BUILD}]",
                        "В проекте нет типа вида «Фасад» — создание невозможно.");
                    return Result.Failed;
                }
                View template = FindTemplate(doc, VIEW_TEMPLATE);
                if (template == null) report.AppendLine($"Шаблон \"{VIEW_TEMPLATE}\" не найден.");

                DimensionType dim = FindDimType(doc, DIM_TYPE);
                DimensionType dimOpening = FindDimType(doc, DIM_TYPE_OPENING);
                FamilySymbol tag = FindWallTag(doc);
                if (tag == null) report.AppendLine("Марка витража (кат. «Марки стен») не найдена — марки не расставлены.");

                // ----- Навесные стены (витражи) по маркировке -----
                var curtains = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall)).Cast<Wall>()
                    .Where(w => w.CurtainGrid != null && w.Location is LocationCurve lc && lc.Curve is Line)
                    .ToList();

                var byMark = new SortedDictionary<string, Wall>(StringComparer.OrdinalIgnoreCase);
                foreach (Wall w in curtains)
                {
                    string mark = MarkOf(w);
                    if (mark.Length == 0) continue;
                    if (!byMark.ContainsKey(mark)) byMark[mark] = w;
                }
                report.AppendLine($"Навесных стен: {curtains.Count}, уникальных маркировок: {byMark.Count}");
                if (byMark.Count == 0)
                {
                    PluginReport.Show($"Виды витражей [{BUILD}]", report.ToString());
                    return Result.Succeeded;
                }

                var viewNames = new HashSet<string>(
                    new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);

                var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(p => !p.IsTemplate && p.GenLevel != null && p.ViewType == ViewType.FloorPlan).ToList();
                ViewPlan fallbackPlan = doc.ActiveView as ViewPlan ?? plans.FirstOrDefault();
                if (fallbackPlan == null)
                {
                    PluginReport.Show($"Виды витражей [{BUILD}]", "Нет плана этажа — маркер разместить негде.");
                    return Result.Failed;
                }

                int created = 0, skipped = 0, failed = 0, tags = 0, dims = 0;
                var failedMarks = new List<string>();
                var diag = new StringBuilder();

                using (Transaction tx = new Transaction(doc, "Виды витражей по маркировке"))
                {
                    tx.Start();
                    foreach (var pair in byMark)
                    {
                        if (viewNames.Contains(pair.Key)) { skipped++; continue; }
                        ViewPlan plan = plans.FirstOrDefault(p => p.GenLevel.Id == pair.Value.LevelId) ?? fallbackPlan;

                        ViewSection view = CreateElevation(doc, vft, template, plan, pair.Value, pair.Key, out CwGeom g);
                        if (view == null) { failed++; failedMarks.Add(pair.Key); continue; }
                        created++;
                        viewNames.Add(pair.Key);

                        if (PlaceTag(doc, view, g, pair.Value, tag)) tags++;
                        int d = PlaceDimensions(doc, view, g, dim, dimOpening);
                        dims += d;
                        diag.AppendLine($"{pair.Key}: габарит {MmBack(g.MaxX - g.MinX)}x{MmBack(g.MaxY - g.MinY)}, " +
                            $"стоек {g.VLines.Count}, ригелей {g.HLines.Count}, размеров {d}");
                    }
                    tx.Commit();
                }

                report.AppendLine($"\nИТОГО: создано видов {created}, уже были {skipped}, не удалось {failed}" +
                    (failedMarks.Count > 0 ? $" ({string.Join(", ", failedMarks)})" : "") +
                    $"; марок {tags}, размеров {dims}");
                if (diag.Length > 0) { report.AppendLine("\nДиагностика:"); report.Append(diag.ToString()); }
                PluginReport.Show($"Виды витражей [{BUILD}]", report.ToString());
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { Logger.Log("Ошибка: " + ex.Message, 4); message = ex.Message; return Result.Failed; }
        }

        // ==================== ВИД ====================

        private static ViewSection CreateElevation(
            Document doc, ViewFamilyType vft, View template, ViewPlan plan, Wall wall, string mark, out CwGeom g)
        {
            g = null;
            try
            {
                LocationCurve lc = (LocationCurve)wall.Location;
                Line line = (Line)lc.Curve;
                XYZ loc = line.Evaluate(0.5, true);

                XYZ f = Flat(wall.Orientation);
                if (f.GetLength() < 1e-6) f = XYZ.BasisY;
                f = f.Normalize();

                double off = Mm(MARKER_OFFSET_MM);
                XYZ markerPt = new XYZ(loc.X + f.X * off, loc.Y + f.Y * off, loc.Z);
                ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, markerPt, 50);
                ViewSection view = marker.CreateElevation(doc, plan.Id, 0);
                doc.Regenerate();

                XYZ cur = Flat(view.ViewDirection).Normalize();
                double angle = Math.Atan2(cur.CrossProduct(f).Z, cur.DotProduct(f));
                if (Math.Abs(angle) > 1e-6)
                {
                    ElementTransformUtils.RotateElement(doc, marker.Id,
                        Line.CreateBound(markerPt, markerPt + XYZ.BasisZ), angle);
                    doc.Regenerate();
                }

                view.Name = mark;
                if (template != null) { try { view.ViewTemplateId = template.Id; } catch { } doc.Regenerate(); }

                g = Recognize(view, wall);
                if (g != null) SetCrop(view, g);
                SetOrgParams(view);
                return view;
            }
            catch { return null; }
        }

        /// <summary>Габарит витража + грани краёв + линии сетки (стойки/ригели) в координатах вида.</summary>
        private static CwGeom Recognize(ViewSection view, Wall wall)
        {
            BoundingBoxXYZ wb = wall.get_BoundingBox(null);
            if (wb == null) return null;
            Transform t = view.CropBox.Transform;
            Transform inv = t.Inverse;

            var g = new CwGeom
            {
                T = t,
                MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue,
                MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue
            };
            foreach (double x in new[] { wb.Min.X, wb.Max.X })
                foreach (double y in new[] { wb.Min.Y, wb.Max.Y })
                    foreach (double z in new[] { wb.Min.Z, wb.Max.Z })
                    {
                        XYZ p = inv.OfPoint(new XYZ(x, y, z));
                        if (p.X < g.MinX) g.MinX = p.X; if (p.X > g.MaxX) g.MaxX = p.X;
                        if (p.Y < g.MinY) g.MinY = p.Y; if (p.Y > g.MaxY) g.MaxY = p.Y;
                        if (p.Z < g.MinZ) g.MinZ = p.Z; if (p.Z > g.MaxZ) g.MaxZ = p.Z;
                    }

            // грани краёв витража: нормаль вдоль стены (Left/Right) и вертикаль (Bottom/Top)
            XYZ ax = t.BasisX;
            var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            double tol = Mm(30);
            foreach (GeometryObject go in wall.get_Geometry(opt) ?? Enumerable.Empty<GeometryObject>())
            {
                if (!(go is Solid s) || s.Faces.IsEmpty) continue;
                foreach (Face face in s.Faces)
                {
                    if (!(face is PlanarFace pf) || pf.Reference == null) continue;
                    BoundingBoxUV uv = pf.GetBoundingBox();
                    XYZ c = inv.OfPoint(pf.Evaluate((uv.Min + uv.Max) / 2));
                    if (Math.Abs(pf.FaceNormal.DotProduct(ax)) > 0.97)
                    {
                        if (Math.Abs(c.X - g.MinX) < tol && g.Left == null) g.Left = pf.Reference;
                        else if (Math.Abs(c.X - g.MaxX) < tol && g.Right == null) g.Right = pf.Reference;
                    }
                    else if (Math.Abs(pf.FaceNormal.DotProduct(XYZ.BasisZ)) > 0.97)
                    {
                        if (Math.Abs(c.Y - g.MinY) < tol && g.Bottom == null) g.Bottom = pf.Reference;
                        else if (Math.Abs(c.Y - g.MaxY) < tol && g.Top == null) g.Top = pf.Reference;
                    }
                }
            }

            // линии сетки витража: вертикальные — позиция по X (стойки), горизонтальные — по Y
            CurtainGrid grid = wall.CurtainGrid;
            var ids = new List<ElementId>();
            try { ids.AddRange(grid.GetUGridLineIds()); } catch { }
            try { ids.AddRange(grid.GetVGridLineIds()); } catch { }
            foreach (ElementId id in ids)
            {
                if (!(view.Document.GetElement(id) is CurtainGridLine gl)) continue;
                Curve fc = gl.FullCurve;
                if (fc == null) continue;
                XYZ dir = (fc.GetEndPoint(1) - fc.GetEndPoint(0)).Normalize();
                XYZ mid = inv.OfPoint(fc.Evaluate(0.5, true));
                Reference r = new Reference(gl);
                if (Math.Abs(dir.DotProduct(XYZ.BasisZ)) > 0.7)
                    g.VLines.Add(Tuple.Create(mid.X, r));   // вертикальная линия → делит ширину
                else
                    g.HLines.Add(Tuple.Create(mid.Y, r));   // горизонтальная → делит высоту
            }
            g.VLines.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            g.HLines.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return g;
        }

        private static void SetCrop(ViewSection view, CwGeom g)
        {
            try
            {
                BoundingBoxXYZ crop = view.CropBox;
                double m = Mm(CROP_MARGIN_MM);
                crop.Min = new XYZ(g.MinX - m, g.MinY - m, crop.Min.Z);
                crop.Max = new XYZ(g.MaxX + m, g.MaxY + m, crop.Max.Z);
                view.CropBoxActive = true;
                view.CropBox = crop;
                view.CropBoxVisible = false;

                double farClip = -g.MinZ + Mm(FAR_CLIP_EXTRA_MM);
                if (farClip > 0)
                {
                    Parameter mode = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                    if (mode != null && !mode.IsReadOnly) mode.Set(1);
                    Parameter offP = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                    if (offP != null && !offP.IsReadOnly) offP.Set(farClip);
                }
            }
            catch { }
        }

        // ==================== МАРКА И РАЗМЕРЫ ====================

        private static bool PlaceTag(Document doc, ViewSection view, CwGeom g, Wall wall, FamilySymbol tag)
        {
            if (tag == null || g == null) return false;
            try
            {
                if (!tag.IsActive) { tag.Activate(); doc.Regenerate(); }
                XYZ pnt = g.Pt(g.CenX, g.MaxY + Mm(TAG_ABOVE_MM));
                return IndependentTag.Create(doc, tag.Id, view.Id, new Reference(wall),
                    false, TagOrientation.Horizontal, pnt) != null;
            }
            catch { return false; }
        }

        private static int PlaceDimensions(Document doc, ViewSection view, CwGeom g,
            DimensionType dim, DimensionType dimOpening)
        {
            int done = 0;
            doc.Regenerate();

            // низ: горизонтальные (ширина). Цепочка: левый край, стойки, правый край
            Line chainH = HLine(g, g.MinY - Mm(DIM_CHAIN_MM));
            Line openH = HLine(g, g.MinY - Mm(DIM_OPENING_MM));
            if (g.XOk)
            {
                var arr = new ReferenceArray();
                arr.Append(g.Left);
                foreach (var v in g.VLines) arr.Append(v.Item2);
                arr.Append(g.Right);
                if (arr.Size >= 3 && TryDim(doc, view, chainH, arr, dim)) done++;
                if (TryDim(doc, view, openH, Pair(g.Left, g.Right), dimOpening ?? dim,
                        dimOpening == null ? DIM_OPENING_SUFFIX : null)) done++;
            }

            // лево: вертикальные (высота). Цепочка: низ, ригели, верх
            Line chainV = VLine(g, g.MinX - Mm(DIM_CHAIN_MM));
            Line openV = VLine(g, g.MinX - Mm(DIM_OPENING_MM));
            if (g.YOk)
            {
                var arr = new ReferenceArray();
                arr.Append(g.Bottom);
                foreach (var h in g.HLines) arr.Append(h.Item2);
                arr.Append(g.Top);
                if (arr.Size >= 3 && TryDim(doc, view, chainV, arr, dim)) done++;
                if (TryDim(doc, view, openV, Pair(g.Bottom, g.Top), dimOpening ?? dim,
                        dimOpening == null ? DIM_OPENING_SUFFIX : null)) done++;
            }
            return done;
        }

        private static Line HLine(CwGeom g, double y) => Line.CreateBound(g.Pt(g.MinX, y), g.Pt(g.MaxX, y));
        private static Line VLine(CwGeom g, double x) => Line.CreateBound(g.Pt(x, g.MinY), g.Pt(x, g.MaxY));

        private static ReferenceArray Pair(Reference a, Reference b)
        {
            var arr = new ReferenceArray(); arr.Append(a); arr.Append(b); return arr;
        }

        private static bool TryDim(Document doc, View view, Line line, ReferenceArray refs,
            DimensionType type, string suffix = null)
        {
            try
            {
                Dimension d = type != null ? doc.Create.NewDimension(view, line, refs, type)
                                           : doc.Create.NewDimension(view, line, refs);
                if (d == null) return false;
                if (suffix != null) try { if (string.IsNullOrEmpty(d.Suffix)) d.Suffix = suffix; } catch { }
                return true;
            }
            catch { return false; }
        }

        // ==================== ОБЩЕЕ ====================

        private static void SetOrgParams(View view)
        {
            SetTextParamByPrefix(view, ORG_VIEW_PARAM, ORG_VIEW_VALUE);
            SetTextParamByPrefix(view, ORG_CONSTR_PARAM, ORG_CONSTR_VALUE);
        }

        private static void SetTextParamByPrefix(Element el, string prefix, string value)
        {
            try
            {
                foreach (Parameter p in el.Parameters)
                {
                    if (p.IsReadOnly || p.StorageType != StorageType.String) continue;
                    if (!(p.Definition?.Name ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Set(value); return;
                }
            }
            catch { }
        }

        private static string MarkOf(Wall w)
        {
            Parameter p = w.LookupParameter(MARK_PARAM) ?? w.WallType?.LookupParameter(MARK_PARAM);
            if (p == null || !p.HasValue) return "";
            string s = p.AsString();
            if (string.IsNullOrEmpty(s)) s = p.AsValueString();
            return (s ?? "").Trim();
        }

        private static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        private static int MmBack(double v) => (int)Math.Round(UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Millimeters));
        private static XYZ Flat(XYZ v) => new XYZ(v.X, v.Y, 0);

        private static View FindTemplate(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && v.Name == name);

        private static DimensionType FindDimType(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .FirstOrDefault(d => d.StyleType == DimensionStyleType.Linear && d.Name == name);

        private static ViewFamilyType FindElevationType(Document doc, string name)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.Elevation).ToList();
            return types.FirstOrDefault(t => t.Name == name) ?? types.FirstOrDefault();
        }

        /// <summary>Марка стены (кат. «Марки стен»), предпочтение семейству с «Витраж».</summary>
        private static FamilySymbol FindWallTag(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_WallTags).Cast<FamilySymbol>()
                .OrderBy(s => (s.Family?.Name ?? "").IndexOf(TAG_PREFER, StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .FirstOrDefault();
    }
}
