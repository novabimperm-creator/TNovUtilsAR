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
    /// Виды окон: на каждое уникальное значение «Маркировка типоразмера» (Ок-1, Ок-2…)
    /// создаётся вид-фасад (инструмент «Фасад», тип «Р_Основной»), который смотрит на
    /// окно снаружи. Вид называется по маркировке, ему назначается шаблон
    /// «Д_АР_Фасад_Р_Окна». Рабочая геометрия строится в три шага:
    ///   1) рама окна — по крупным граням геометрии самого окна (мелочь вроде отлива
    ///      и торцов стеклопакетов отсекается фильтром по площади);
    ///   2) проём — по граням отверстия в стене возле краёв рамы и по именованным
    ///      опорным семейства (стабильны при регенерации);
    ///   3) от проёма (или рамы, если проём не найден) — обрезка, цветовая область,
    ///      центр разрезов и размерные линии.
    /// Оформление (Decorate): область-рамка 160 мм со стилями линий контуров,
    /// разрезы «Nа-Nа»/«Nб-Nб», размеры: цепочка проём/габарит/ось импоста, габарит
    /// по номинальным «Ширина»/«Высота», проём с суффиксом «(Проем)».
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AutoWindowViewsCommand : IExternalCommand
    {
        // Метка сборки. Если её нет в заголовке окна — Revit грузит старый DLL.
        private const string BUILD = "виды окон v27 (тип марки — Маркировка типоразмера)";

        // Тип фасада (ViewFamilyType семейства «Фасад») и шаблон вида.
        private const string ELEV_TYPE = "Р_Основной";
        private const string VIEW_TEMPLATE = "Д_АР_Фасад_Р_Окна";
        // Тип разреза и шаблон разрезов окна.
        private const string SECTION_TYPE = "Р_Витражи";
        private const string SECTION_TEMPLATE = "Д_АР_Разрез_Р_Окна";
        // Семейство линии обрыва (детальный компонент) по краям разрезов: по подстроке.
        private const string BREAKLINE_SUBSTR = "обрыв";
        private const double BREAKLINE_LEN_MM = 1000.0;   // длина линии обрыва
        // Цветовая область вокруг проёма и её толщина (мм).
        private const string REGION_TYPE = "Диагональ_1.5мм_схема окон";
        private const double REGION_WIDTH_MM = 160.0;
        // Стили линий контуров области: наружный и внутренний.
        private const string REGION_LINE_OUTER = "Невидимые линии";
        private const string REGION_LINE_INNER = "Скрыто";
        // Типы размеров: обычный и для проёма (+ суффикс на размере проёма).
        private const string DIM_TYPE = "Основной_2.5";
        private const string DIM_TYPE_OPENING = "Основной_2.5 (Проем)";
        private const string DIM_OPENING_SUFFIX = "(Проем)";
        // Отступы размерных линий от проёма (мм): цепочка, габарит, проём.
        private const double DIM_CHAIN_MM = 500.0;
        private const double DIM_TOTAL_MM = 950.0;
        private const double DIM_OPENING_MM = 1400.0;
        // Слияние близких граней в один пункт цепочки (мм): у импоста две грани.
        private const double DIM_CLUSTER_MM = 100.0;
        // Осевая семейства заменяет точку цепочки, если лежит в этой зоне от её середины (мм).
        private const double CENTER_SNAP_MM = 40.0;
        // Минимальная площадь грани окна для рамы/цепочки (м²): отсекает отлив,
        // торцы стеклопакетов и прочую мелочь.
        private const double MIN_FACE_AREA_M2 = 0.03;
        // Откосы/верх/низ проёма ищутся не дальше этого от краёв рамы (мм).
        private const double OPENING_NEAR_MM = 250.0;
        // Максимальная глубина поиска пола/плиты вниз от проёма (мм): только свой этаж.
        private const double FLOOR_MAX_DEPTH_MM = 1500.0;
        // Параметры наружного проёма (наружные габариты окна с учётом четверти).
        private static readonly string[] OUTER_WIDTH_PARAMS = { "Ширина.Наружная", "Ширина наружная", "Наружная ширина" };
        private static readonly string[] OUTER_HEIGHT_PARAMS = { "Высота.Наружная", "Высота наружная", "Наружная высота" };
        // Параметр маркировки типоразмера и требуемая подстрока (только окна «Ок-…»).
        private const string MARK_PARAM = "Маркировка типоразмера";
        private const string MARK_MUST_CONTAIN = "ок";
        // Исключаемые семейства (проёмы): по подстроке в имени семейства.
        private const string EXCLUDE_FAMILY_SUBSTR = "проем";
        // Параметры организации диспетчера проекта (Стадия Р > Окна).
        private const string ORG_VIEW_PARAM = "Орг.КатегорияВида";
        private const string ORG_VIEW_VALUE = "3. Стадия Р";
        private const string ORG_CONSTR_PARAM = "Орг.КатегорияКонструкц";   // по началу имени
        private const string ORG_CONSTR_VALUE = "Окна";
        // Отступ маркера фасада от окна наружу (мм) и запас границы обрезки (мм).
        private const double MARKER_OFFSET_MM = 500.0;
        private const double CROP_MARGIN_MM = 300.0;
        // Запас дальней подрезки за самой дальней точкой окна (мм).
        private const double FAR_CLIP_EXTRA_MM = 200.0;
        // Разрезы: запас обрезки от проёма (мм), глубина вида (мм), запас поперёк
        // стены за её гранями (мм) и отступ первой размерной линии от грани стены (мм).
        private const double SEC_MARGIN_MM = 300.0;
        private const double SEC_DEPTH_MM = 600.0;
        // Захват поперёк стены в помещение (мм): 450 = было 150 + 300, чтобы левая
        // линия обрыва (в 300 мм от стены) попадала в область видимости.
        private const double SEC_ACROSS_EXTRA_MM = 450.0;
        // Отступ первой размерной линии от грани стены (мм): 650 = было 350 + 300,
        // чтобы размеры не налезали на линию обрыва.
        private const double SEC_DIM_OFFSET_MM = 650.0;
        private const double SEC_DIM_STEP_MM = 300.0;

        /// <summary>Оформлять созданные виды (область, разрезы, размеры). Ставится панелью.</summary>
        public bool Decorate { get; set; } = true;

        /// <summary>
        /// Рабочий прямоугольник в координатах вида (X — вдоль стены, Y — вверх,
        /// Z — глубина): проём, а без него — рама окна.
        /// </summary>
        private class ViewBox
        {
            public Transform T;                       // координаты вида -> модель
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public double CenX => (MinX + MaxX) / 2;
            public double CenY => (MinY + MaxY) / 2;
            public double CenZ => (MinZ + MaxZ) / 2;
            public XYZ Pt(double x, double y) => T.OfPoint(new XYZ(x, y, 0));
        }

        /// <summary>Грань окна (или опорная) для размеров: позиция вдоль оси и ссылка.</summary>
        private class FacePt
        {
            public double Pos;        // позиция выбранной грани/опорной
            public double Mid;        // середина группы слитых граней (центр импоста)
            public Reference Ref;
        }

        /// <summary>
        /// Грани проёма в стене-основе и его прямоугольник в координатах вида.
        /// Inner (Left/Right/Bottom/Top) — внутренний (широкий) проём; Out — наружный,
        /// с четвертью, если стена её моделирует (второй ряд граней ближе к оси окна).
        /// </summary>
        private class OpeningInfo
        {
            public Reference Left, Right, Bottom, Top;
            public double MinX, MaxX, MinY, MaxY;
            public Reference LeftOut, RightOut, BottomOut, TopOut;
            public double MinXOut, MaxXOut, MinYOut, MaxYOut;
            public int CandL, CandR, CandB, CandT;   // граней-кандидатов по сторонам (диагностика)
            public bool XOk => Left != null && Right != null;
            public bool YOk => Bottom != null && Top != null;
            public bool XOutOk => LeftOut != null && RightOut != null;
            public bool YOutOk => BottomOut != null && TopOut != null;
        }

        /// <summary>Вся геометрия одного окна, найденная для оформления.</summary>
        private class WinGeom
        {
            public ViewBox Box;                                  // рабочий прямоугольник
            public OpeningInfo Opening;                          // может быть null
            public List<FacePt> FacesX;                          // вертикальные грани окна
            public List<FacePt> FacesY;                          // горизонтальные грани окна
            public double FrameMinX, FrameMaxX, FrameMinY, FrameMaxY;   // рама
            public int SnappedX, SnappedY;                       // точек посажено на осевые
            public string HostInfo = "?";                        // основа окна (диагностика)
            public List<ElementId> DimIds = new List<ElementId>();      // созданные размеры
            public bool NomX, NomY;                              // габарит посажен на номинал
            public List<Tuple<double, Reference>> FloorPts;      // пол/плита ниже проёма (Y возр.)
            public bool FloorOk => FloorPts != null && FloorPts.Count > 0;
            public double FloorMinY => FloorPts[0].Item1;        // самая нижняя точка (для обрезки)
            public Reference CeilingBot;                         // низ плиты перекрытия над окном
            public double CeilingBotY;
            public bool CeilingOk => CeilingBot != null;
        }

        /// <summary>Найденные один раз элементы оформления (типы, шаблоны).</summary>
        private class DecorSet
        {
            public FilledRegionType Region;
            public ViewFamilyType SectionVft;
            public View SectionTpl;
            public DimensionType Dim;
            public DimensionType DimOpening;
            public FamilySymbol BreakLine;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var report = new StringBuilder();
                report.AppendLine($"[{BUILD}]  Оформление: {(Decorate ? "да" : "нет")}");
                if (RevitAPI.UiApplication == null) RevitAPI.Initialize(commandData);
                Logger.Initialize("Виды окон", DateTime.Now, BUILD);

                // ----- Тип фасада «Р_Основной» (иначе — любой тип фасада) -----
                ViewFamilyType vft = FindElevationType(doc, ELEV_TYPE, out bool exactType);
                if (vft == null)
                {
                    PluginReport.Show($"Виды окон [{BUILD}]",
                        "В проекте нет ни одного типа вида «Фасад». Создание видов невозможно.");
                    return Result.Failed;
                }
                if (!exactType)
                    report.AppendLine($"Тип фасада \"{ELEV_TYPE}\" не найден, использован \"{vft.Name}\".");

                // ----- Шаблон вида (не обязателен, но сообщаем) -----
                View template = FindTemplate(doc, VIEW_TEMPLATE);
                if (template == null)
                    report.AppendLine($"Шаблон \"{VIEW_TEMPLATE}\" не найден — виды созданы без шаблона.");

                // ----- Набор для оформления -----
                DecorSet decor = null;
                if (Decorate)
                {
                    decor = new DecorSet
                    {
                        Region = new FilteredElementCollector(doc)
                            .OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                            .FirstOrDefault(t => t.Name == REGION_TYPE),
                        SectionVft = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .Where(t => t.ViewFamily == ViewFamily.Section)
                            .OrderBy(t => t.Name == SECTION_TYPE ? 0 : 1)
                            .FirstOrDefault(),
                        SectionTpl = FindTemplate(doc, SECTION_TEMPLATE),
                        Dim = FindDimType(doc, DIM_TYPE),
                        DimOpening = FindDimType(doc, DIM_TYPE_OPENING),
                        BreakLine = FindBreakLine(doc, BREAKLINE_SUBSTR)
                    };
                    if (decor.Region == null)
                        report.AppendLine($"Тип области \"{REGION_TYPE}\" не найден — области пропущены.");
                    if (decor.SectionVft == null)
                        report.AppendLine("Типы разрезов в проекте не найдены — разрезы пропущены.");
                    else if (decor.SectionVft.Name != SECTION_TYPE)
                        report.AppendLine($"Тип разреза \"{SECTION_TYPE}\" не найден, использован \"{decor.SectionVft.Name}\".");
                    if (decor.SectionTpl == null)
                        report.AppendLine($"Шаблон \"{SECTION_TEMPLATE}\" не найден — разрезы без шаблона.");
                    if (decor.Dim == null)
                        report.AppendLine($"Тип размера \"{DIM_TYPE}\" не найден — размеры типом по умолчанию.");
                    if (decor.DimOpening == null)
                        report.AppendLine($"Тип размера \"{DIM_TYPE_OPENING}\" не найден — размер проёма типом по умолчанию.");
                    if (decor.BreakLine == null)
                        report.AppendLine($"Семейство линии обрыва (\"{BREAKLINE_SUBSTR}\") не найдено — линии обрыва пропущены.");
                }

                // ----- Окна проекта: по одному экземпляру на маркировку -----
                var windows = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(fi => fi.Location is LocationPoint)
                    .Where(fi => fi.SuperComponent == null)
                    .Where(fi => !FamilyContains(fi, EXCLUDE_FAMILY_SUBSTR))
                    .ToList();

                var byMark = new SortedDictionary<string, FamilyInstance>(StringComparer.OrdinalIgnoreCase);
                foreach (FamilyInstance fi in windows)
                {
                    string mark = MarkOf(fi);
                    if (mark.IndexOf(MARK_MUST_CONTAIN, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!byMark.ContainsKey(mark)) byMark[mark] = fi;
                }

                report.AppendLine($"Окон в проекте: {windows.Count}, уникальных маркировок «Ок»: {byMark.Count}");
                if (byMark.Count == 0)
                {
                    PluginReport.Show($"Виды окон [{BUILD}]", report.ToString());
                    return Result.Succeeded;
                }

                // ----- Существующие имена видов (виды не дублируем) -----
                var viewNames = new HashSet<string>(
                    new FilteredElementCollector(doc).OfClass(typeof(View))
                        .Cast<View>().Where(v => !v.IsTemplate).Select(v => v.Name),
                    StringComparer.OrdinalIgnoreCase);

                // ----- Планы этажей для размещения маркеров -----
                var plans = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .Where(p => !p.IsTemplate && p.GenLevel != null &&
                                p.ViewType == ViewType.FloorPlan)
                    .ToList();
                ViewPlan fallbackPlan = doc.ActiveView as ViewPlan ?? plans.FirstOrDefault();
                if (fallbackPlan == null)
                {
                    PluginReport.Show($"Виды окон [{BUILD}]",
                        "В проекте нет ни одного плана этажа — маркер фасада разместить негде.");
                    return Result.Failed;
                }

                // ----- Марка окна (тег «pmN.Марка_Окно») для расстановки на видах -----
                FamilySymbol windowTag = FindWindowTag(doc);
                if (windowTag == null)
                    report.AppendLine("Семейство марки окна (кат. «Марки окон») не найдено — марки не расставлены.");

                int created = 0, skipped = 0, failed = 0, tags = 0;
                int regions = 0, sections = 0, dims = 0;
                var failedMarks = new List<string>();
                var results = new List<Tuple<string, WinGeom, int, int, int>>();

                using (Transaction tx = new Transaction(doc, "Виды окон по маркировке"))
                {
                    tx.Start();
                    foreach (var pair in byMark)
                    {
                        if (viewNames.Contains(pair.Key)) { skipped++; continue; }

                        ViewPlan plan = plans.FirstOrDefault(p => p.GenLevel.Id == pair.Value.LevelId)
                            ?? fallbackPlan;
                        ViewSection view = CreateWindowElevation(
                            doc, vft, template, plan, pair.Value, pair.Key,
                            out WinGeom g, out XYZ facing);
                        if (view == null)
                        {
                            failed++;
                            failedMarks.Add(pair.Key);
                            continue;
                        }
                        created++;
                        viewNames.Add(pair.Key);
                        if (PlaceWindowTag(doc, view, g, pair.Value, windowTag)) tags++;

                        int r = 0, s = 0, d = 0;
                        if (decor != null && g != null)
                        {
                            if (PlaceRegion(doc, view, g.Box, decor)) r = 1;
                            d = PlaceDimensions(doc, view, g, pair.Value, decor);
                            s = PlaceSections(doc, g, facing, pair.Key, pair.Value,
                                decor, viewNames, out int sd);
                            d += sd;
                            regions += r; sections += s; dims += d;
                        }
                        results.Add(Tuple.Create(pair.Key, g, r, s, d));
                    }
                    tx.Commit();
                }

                // после коммита: считаем размеры, пережившие регенерацию (размеры на
                // нестабильных опорных Revit удаляет молча)
                int dimsAlive = 0;
                var diag = new StringBuilder();
                foreach (var t in results)
                {
                    int alive = t.Item2?.DimIds.Count(id => doc.GetElement(id) is Dimension) ?? 0;
                    dimsAlive += alive;
                    diag.AppendLine(DiagLine(t.Item1, t.Item2, t.Item3, t.Item4, t.Item5, alive));
                }

                report.AppendLine($"\nИТОГО: создано видов {created}, уже были {skipped}, не удалось {failed}" +
                    (failedMarks.Count > 0 ? $" ({string.Join(", ", failedMarks)})" : "") +
                    $"; марок окон {tags}");
                if (decor != null)
                    report.AppendLine($"Оформление: областей {regions}, разрезов {sections}, " +
                        $"размеров {dims} (выжило {dimsAlive})");
                if (diag.Length > 0)
                {
                    report.AppendLine("\nДиагностика по окнам:");
                    report.Append(diag.ToString());
                }
                PluginReport.Show($"Виды окон [{BUILD}]", report.ToString());
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

        /// <summary>Строка диагностики по окну: рама, проём, число граней, оформление.</summary>
        private static string DiagLine(string mark, WinGeom g, int regions, int sections, int dims, int alive)
        {
            if (g == null) return $"{mark}: геометрия не распознана";
            string frame = $"{MmBack(g.FrameMaxX - g.FrameMinX)}x{MmBack(g.FrameMaxY - g.FrameMinY)}";
            string open = "не искался", outer = "нет";
            if (g.Opening != null)
            {
                string w = g.Opening.XOk ? MmBack(g.Opening.MaxX - g.Opening.MinX).ToString() : "?";
                string h = g.Opening.YOk ? MmBack(g.Opening.MaxY - g.Opening.MinY).ToString() : "?";
                open = $"{w}x{h} (гр. {g.Opening.CandL}/{g.Opening.CandR}/{g.Opening.CandB}/{g.Opening.CandT})";
                string wo = g.Opening.XOutOk ? MmBack(g.Opening.MaxXOut - g.Opening.MinXOut).ToString() : "?";
                string ho = g.Opening.YOutOk ? MmBack(g.Opening.MaxYOut - g.Opening.MinYOut).ToString() : "?";
                if (g.Opening.XOutOk || g.Opening.YOutOk) outer = $"{wo}x{ho}";
            }
            return $"{mark}: основа {g.HostInfo}, рама {frame}, проём {open}, нар.проём {outer}, " +
                   $"плита {(g.FloorOk ? g.FloorPts.Count.ToString() : "-")}, " +
                   $"граней X:{g.FacesX.Count} Y:{g.FacesY.Count}, на осевых X:{g.SnappedX} Y:{g.SnappedY}, " +
                   $"номинал X:{(g.NomX ? "+" : "-")} Y:{(g.NomY ? "+" : "-")}, " +
                   $"обл {regions}, разр {sections}, разм {dims} (выжило {alive})";
        }

        // ==================== СОЗДАНИЕ ВИДА-ФАСАДА ====================

        /// <summary>
        /// Создаёт фасад на окно: маркер снаружи окна, вид повёрнут лицом к окну,
        /// имя = маркировка, шаблон. Затем распознаётся геометрия окна (рама → проём)
        /// и по ней ставится обрезка вида.
        /// </summary>
        private static ViewSection CreateWindowElevation(
            Document doc, ViewFamilyType vft, View template, ViewPlan plan,
            FamilyInstance win, string mark, out WinGeom g, out XYZ facing)
        {
            g = null; facing = null;
            try
            {
                XYZ loc = ((LocationPoint)win.Location).Point;

                // нормаль окна в плане (наружу); запас — нормаль стены-основы
                XYZ f = Flat(win.FacingOrientation);
                if (f.GetLength() < 1e-6 && win.Host is Wall w) f = Flat(w.Orientation);
                if (f.GetLength() < 1e-6) f = XYZ.BasisY;
                f = f.Normalize();
                facing = f;

                double off = Mm(MARKER_OFFSET_MM);
                XYZ markerPt = new XYZ(loc.X + f.X * off, loc.Y + f.Y * off, loc.Z);

                ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, vft.Id, markerPt, 50);
                ViewSection view = marker.CreateElevation(doc, plan.Id, 0);
                doc.Regenerate();

                // повернуть маркер так, чтобы вид смотрел на окно снаружи:
                // ViewDirection (из экрана на зрителя) должен совпасть с нормалью наружу
                XYZ cur = Flat(view.ViewDirection).Normalize();
                double angle = Math.Atan2(cur.CrossProduct(f).Z, cur.DotProduct(f));
                if (Math.Abs(angle) > 1e-6)
                {
                    Line axis = Line.CreateBound(markerPt, markerPt + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, marker.Id, axis, angle);
                    doc.Regenerate();
                }

                view.Name = mark;
                if (template != null)
                {
                    try { view.ViewTemplateId = template.Id; } catch { }
                    doc.Regenerate();
                }

                g = RecognizeGeometry(view, win);
                if (g != null)
                {
                    SetCrop(view, g.Box);
                    SetOrgParams(view);
                }
                return view;
            }
            catch { return null; }
        }

        /// <summary>
        /// Распознаёт геометрию окна: габаритный бокс → рама по крупным граням окна →
        /// проём по граням стены возле рамы. Рабочий прямоугольник = проём, без него
        /// рама, без неё — бокс.
        /// </summary>
        private static WinGeom RecognizeGeometry(ViewSection view, FamilyInstance win)
        {
            ViewBox box = ComputeViewBox(view, win);
            if (box == null) return null;
            var g = new WinGeom { Box = box };

            // 1) горизонтальные грани окна (низ/верх рамы, горизонтальные импосты):
            //    фильтр поперёк — по всему боксу
            g.FacesY = WindowFaceRefs(win, box, false, box.MinX, box.MaxX);
            g.FrameMinY = g.FacesY.Count >= 2 ? g.FacesY.First().Pos : box.MinY;
            g.FrameMaxY = g.FacesY.Count >= 2 ? g.FacesY.Last().Pos : box.MaxY;

            // 2) вертикальные грани окна (бока рамы, импосты): фильтр поперёк — только
            //    в полосе рамы по высоте, чтобы отлив/фартук ниже рамы не попадали
            double t10 = Mm(10);
            g.FacesX = WindowFaceRefs(win, box, true, g.FrameMinY - t10, g.FrameMaxY + t10);
            g.FrameMinX = g.FacesX.Count >= 2 ? g.FacesX.First().Pos : box.MinX;
            g.FrameMaxX = g.FacesX.Count >= 2 ? g.FacesX.Last().Pos : box.MaxX;

            // рабочий прямоугольник = рама
            box.MinX = g.FrameMinX; box.MaxX = g.FrameMaxX;
            box.MinY = g.FrameMinY; box.MaxY = g.FrameMaxY;

            // 3) проём в стене-основе — грани возле краёв рамы; составная стена
            //    разворачивается на дочерние
            var hostWalls = new List<Wall>();
            if (win.Host is Wall host)
            {
                if (host.IsStackedWall)
                {
                    foreach (ElementId id in host.GetStackedWallMemberIds())
                        if (view.Document.GetElement(id) is Wall mw) hostWalls.Add(mw);
                }
                else hostWalls.Add(host);
            }
            g.HostInfo = win.Host == null ? "нет основы"
                : win.Host.GetType().Name + (hostWalls.Count > 1 ? $" x{hostWalls.Count}" : "");

            if (hostWalls.Count > 0)
                g.Opening = ScanOpening(hostWalls, box);

            // заменить опорные проёма на именованные опорные семейства (Лево/Право/
            // Верх/Низ): грани стены нестабильны — размеры на них Revit удаляет при
            // регенерации, а опорные семейства живут
            RefineOpeningByFamilyRefs(view.Document, view, win, g);

            // наружный проём — по параметрам «Ширина.Наружная»/«Высота.Наружная»:
            // ищем в семействе пару опорных ровно на это расстояние (стабильны)
            RefineOuterOpeningByParams(view.Document, view, win, g);

            if (g.Opening != null)
            {
                if (g.Opening.XOk) { box.MinX = g.Opening.MinX; box.MaxX = g.Opening.MaxX; }
                if (g.Opening.YOk) { box.MinY = g.Opening.MinY; box.MaxY = g.Opening.MaxY; }
            }

            // плита/пол под окном — для размеров «до пола» и «до плиты» на разрезе;
            // плита перекрытия над окном — для размера «до плиты» сверху
            ScanFloorBelow(view.Document, win, g);
            ScanCeilingAbove(view.Document, win, g);
            return g;
        }

        /// <summary>
        /// Ищет плиту перекрытия НАД окном: ближайшее перекрытие (Floor), чей низ выше
        /// верха окна, но не дальше одного этажа. Берётся его нижняя грань (низ плиты).
        /// Пробная точка — в помещении (сторона -f), как и для пола снизу.
        /// </summary>
        private static void ScanCeilingAbove(Document doc, FamilyInstance win, WinGeom g)
        {
            try
            {
                if (!(win.Location is LocationPoint lp)) return;
                XYZ loc = lp.Point;
                Transform inv = g.Box.T.Inverse;
                double t50 = Mm(50);

                XYZ f = Flat(win.FacingOrientation);
                if (f.GetLength() < 1e-6 && win.Host is Wall w0) f = Flat(w0.Orientation);
                f = f.GetLength() < 1e-6 ? XYZ.BasisY : f.Normalize();
                double wallHalf = win.Host is Wall wh ? wh.Width / 2 : Mm(200);
                XYZ probe = loc - f * (wallHalf + Mm(300));

                // верх проёма в координатах вида; над ним ищем низ плиты, но в пределах этажа
                double openTopY = g.Opening != null && g.Opening.YOk ? g.Opening.MaxY : g.Box.MaxY;

                var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                PlanarFace best = null; double bestY = double.MaxValue;
                foreach (Floor fl in new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Floors)
                            .WhereElementIsNotElementType().OfClass(typeof(Floor)).Cast<Floor>())
                {
                    BoundingBoxXYZ bb = fl.get_BoundingBox(null);
                    if (bb == null) continue;
                    if (probe.X < bb.Min.X - t50 || probe.X > bb.Max.X + t50) continue;
                    if (probe.Y < bb.Min.Y - t50 || probe.Y > bb.Max.Y + t50) continue;
                    if (bb.Min.Z < loc.Z - t50) continue;              // не ниже окна
                    foreach (GeometryObject go in fl.get_Geometry(opt) ?? Enumerable.Empty<GeometryObject>())
                    {
                        if (!(go is Solid s) || s.Faces.IsEmpty) continue;
                        foreach (Face face in s.Faces)
                        {
                            if (!(face is PlanarFace pf) || pf.Reference == null) continue;
                            if (pf.FaceNormal.DotProduct(XYZ.BasisZ) > -0.97) continue;   // низ плиты
                            double y = inv.OfPoint(pf.Origin).Y;
                            // выше проёма, но не дальше одного этажа; берём самую нижнюю
                            if (y > openTopY + Mm(5) && y < openTopY + Mm(FLOOR_MAX_DEPTH_MM) && y < bestY)
                            { bestY = y; best = pf; }
                        }
                    }
                }
                if (best != null) { g.CeilingBot = best.Reference; g.CeilingBotY = bestY; }
            }
            catch { }
        }

        /// <summary>
        /// Ищет пол и плиту под окном. Пробная точка берётся НЕ в самом окне (оно
        /// внутри стены — стяжка/пол под стену не заходит), а сдвинута в помещение
        /// (сторона -f). Собираются горизонтальные грани всех перекрытий, накрывающих
        /// эту точку и лежащих ниже проёма; позиции по Y (координаты вида) дедуплятся.
        /// Даёт точки: пол (стяжка), верх плиты, низ плиты.
        /// </summary>
        private static void ScanFloorBelow(Document doc, FamilyInstance win, WinGeom g)
        {
            try
            {
                if (!(win.Location is LocationPoint lp)) return;
                XYZ loc = lp.Point;
                Transform inv = g.Box.T.Inverse;
                double t50 = Mm(50);

                // сдвиг пробной точки в помещение по нормали окна
                XYZ f = Flat(win.FacingOrientation);
                if (f.GetLength() < 1e-6 && win.Host is Wall w0) f = Flat(w0.Orientation);
                f = f.GetLength() < 1e-6 ? XYZ.BasisY : f.Normalize();
                double wallHalf = win.Host is Wall wh ? wh.Width / 2 : Mm(200);
                XYZ probe = loc - f * (wallHalf + Mm(300));

                // низ проёма в координатах вида — ниже него собираем точки пола/плиты
                double openBotY = g.Opening != null && g.Opening.YOk ? g.Opening.MinY : g.Box.MinY;

                var raw = new List<Tuple<double, Reference>>();
                var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                foreach (Floor fl in new FilteredElementCollector(doc)
                            .OfCategory(BuiltInCategory.OST_Floors)
                            .WhereElementIsNotElementType().OfClass(typeof(Floor)).Cast<Floor>())
                {
                    BoundingBoxXYZ bb = fl.get_BoundingBox(null);
                    if (bb == null) continue;
                    if (probe.X < bb.Min.X - t50 || probe.X > bb.Max.X + t50) continue;
                    if (probe.Y < bb.Min.Y - t50 || probe.Y > bb.Max.Y + t50) continue;
                    if (bb.Max.Z > loc.Z + t50) continue;              // не выше окна
                    foreach (GeometryObject go in fl.get_Geometry(opt) ?? Enumerable.Empty<GeometryObject>())
                    {
                        if (!(go is Solid s) || s.Faces.IsEmpty) continue;
                        foreach (Face face in s.Faces)
                        {
                            if (!(face is PlanarFace pf) || pf.Reference == null) continue;
                            if (Math.Abs(pf.FaceNormal.DotProduct(XYZ.BasisZ)) < 0.97) continue;
                            double y = inv.OfPoint(pf.Origin).Y;
                            // только свой этаж: ниже проёма, но не глубже одного этажа
                            if (y < openBotY - Mm(5) && y > openBotY - Mm(FLOOR_MAX_DEPTH_MM))
                                raw.Add(Tuple.Create(y, pf.Reference));
                        }
                    }
                }
                if (raw.Count == 0) return;

                // дедуп по Y (20 мм), по возрастанию
                raw.Sort((p, q) => p.Item1.CompareTo(q.Item1));
                var res = new List<Tuple<double, Reference>>();
                double last = double.NaN;
                foreach (var p in raw)
                    if (double.IsNaN(last) || Math.Abs(p.Item1 - last) > Mm(20))
                    { res.Add(p); last = p.Item1; }
                g.FloorPts = res;
            }
            catch { }
        }

        /// <summary>
        /// Подменяет опорные проёма на именованные опорные семейства окна, если те
        /// лежат в зоне проёма (2 мм — OPENING_NEAR_MM от края рамы наружу). Положение
        /// каждой меряется пробным размером от первой грани рамы.
        /// </summary>
        private static void RefineOpeningByFamilyRefs(
            Document doc, ViewSection view, FamilyInstance win, WinGeom g)
        {
            try
            {
                if (g.FacesX.Count == 0 || g.FacesY.Count == 0) return;
                Line lineH = HLine(g.Box, g.Box.MinY - Mm(DIM_CHAIN_MM));
                Line lineV = VLine(g.Box, g.Box.MinX - Mm(DIM_CHAIN_MM));
                Reference baseX = g.FacesX[0].Ref; double posX = g.FacesX[0].Pos;
                Reference baseY = g.FacesY[0].Ref; double posY = g.FacesY[0].Pos;
                double near = Mm(OPENING_NEAR_MM), t2 = Mm(2);

                if (FamilySide(doc, view, win, FamilyInstanceReferenceType.Left, lineH,
                        baseX, posX, g.FrameMinX - near, g.FrameMinX - t2, -1, out Reference lr, out double lp))
                { Op(g).Left = lr; Op(g).MinX = lp; }
                if (FamilySide(doc, view, win, FamilyInstanceReferenceType.Right, lineH,
                        baseX, posX, g.FrameMaxX + t2, g.FrameMaxX + near, +1, out Reference rr, out double rp))
                { Op(g).Right = rr; Op(g).MaxX = rp; }
                if (FamilySide(doc, view, win, FamilyInstanceReferenceType.Bottom, lineV,
                        baseY, posY, g.FrameMinY - near, g.FrameMinY - t2, -1, out Reference br, out double bp))
                { Op(g).Bottom = br; Op(g).MinY = bp; }
                if (FamilySide(doc, view, win, FamilyInstanceReferenceType.Top, lineV,
                        baseY, posY, g.FrameMaxY + t2, g.FrameMaxY + near, +1, out Reference tr, out double tp))
                { Op(g).Top = tr; Op(g).MaxY = tp; }
            }
            catch { }
        }

        private static OpeningInfo Op(WinGeom g) => g.Opening ?? (g.Opening = new OpeningInfo());

        /// <summary>
        /// Наружный проём по параметрам окна «Ширина.Наружная»/«Высота.Наружная»:
        /// в семействе ищется пара опорных ровно на это расстояние (около центра
        /// окна). Такие опорные стабильны — размеры на них переживают регенерацию.
        /// </summary>
        private static void RefineOuterOpeningByParams(
            Document doc, ViewSection view, FamilyInstance win, WinGeom g)
        {
            try
            {
                if (g.FacesX.Count == 0 || g.FacesY.Count == 0) return;
                double cx = (g.FrameMinX + g.FrameMaxX) / 2;
                double cy = (g.FrameMinY + g.FrameMaxY) / 2;
                Line lineH = HLine(g.Box, g.Box.MinY - Mm(DIM_CHAIN_MM));
                Line lineV = VLine(g.Box, g.Box.MinX - Mm(DIM_CHAIN_MM));

                double? wOut = OuterSize(win, true);
                if (wOut != null && wOut.Value > Mm(100) &&
                    FindFamilyPair(doc, view, win, true, lineH, g.FacesX[0].Ref, g.FacesX[0].Pos,
                        wOut.Value, cx, out Reference lo, out double plo, out Reference hi, out double phi))
                {
                    Op(g).LeftOut = lo; Op(g).MinXOut = plo;
                    Op(g).RightOut = hi; Op(g).MaxXOut = phi;
                }

                double? hOut = OuterSize(win, false);
                if (hOut != null && hOut.Value > Mm(100) &&
                    FindFamilyPair(doc, view, win, false, lineV, g.FacesY[0].Ref, g.FacesY[0].Pos,
                        hOut.Value, cy, out Reference bo, out double pbo, out Reference to, out double pto))
                {
                    Op(g).BottomOut = bo; Op(g).MinYOut = pbo;
                    Op(g).TopOut = to; Op(g).MaxYOut = pto;
                }
            }
            catch { }
        }

        /// <summary>Наружный габарит окна: параметр «*.Наружная» (экземпляр или тип).</summary>
        private static double? OuterSize(FamilyInstance win, bool horizontal)
        {
            foreach (string name in horizontal ? OUTER_WIDTH_PARAMS : OUTER_HEIGHT_PARAMS)
            {
                Parameter p = win.LookupParameter(name) ?? win.Symbol?.LookupParameter(name);
                if (p != null && p.StorageType == StorageType.Double && p.HasValue)
                    return p.AsDouble();
            }
            return null;
        }

        /// <summary>
        /// Ищет в семействе окна пару опорных, отстоящих ровно на dist (допуск 4 мм) и
        /// расположенных максимально симметрично относительно center. Позиции опорных
        /// меряются пробными размерами от baseRef. Возвращает нижнюю/левую и верхнюю/
        /// правую опорные с их позициями.
        /// </summary>
        private static bool FindFamilyPair(
            Document doc, ViewSection view, FamilyInstance win, bool horizontal, Line line,
            Reference baseRef, double basePos, double dist, double center,
            out Reference rLo, out double pLo, out Reference rHi, out double pHi)
        {
            rLo = null; rHi = null; pLo = 0; pHi = 0;
            try
            {
                var types = horizontal
                    ? new[] { FamilyInstanceReferenceType.Left, FamilyInstanceReferenceType.Right,
                              FamilyInstanceReferenceType.StrongReference, FamilyInstanceReferenceType.WeakReference }
                    : new[] { FamilyInstanceReferenceType.Bottom, FamilyInstanceReferenceType.Top,
                              FamilyInstanceReferenceType.StrongReference, FamilyInstanceReferenceType.WeakReference };
                var cands = new List<Reference>();
                foreach (FamilyInstanceReferenceType t in types)
                    try { cands.AddRange(win.GetReferences(t) ?? new List<Reference>()); } catch { }

                var pts = new List<Tuple<double, Reference>>();
                foreach (Reference r in cands)
                {
                    double? v = TempDimValue(doc, view, baseRef, r, line);
                    if (v == null) continue;
                    foreach (int sign in new[] { -1, 1 })
                    {
                        pts.Add(Tuple.Create(basePos + sign * v.Value, r));
                        if (v.Value < Mm(1)) break;
                    }
                }

                double bestCenterErr = Mm(250);
                for (int i = 0; i < pts.Count; i++)
                    for (int j = 0; j < pts.Count; j++)
                    {
                        if (pts[j].Item1 <= pts[i].Item1) continue;
                        if (Math.Abs((pts[j].Item1 - pts[i].Item1) - dist) > Mm(4)) continue;
                        double centerErr = Math.Abs((pts[i].Item1 + pts[j].Item1) / 2 - center);
                        if (centerErr >= bestCenterErr) continue;
                        bestCenterErr = centerErr;
                        rLo = pts[i].Item2; pLo = pts[i].Item1;
                        rHi = pts[j].Item2; pHi = pts[j].Item1;
                    }
                return rLo != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Именованная опорная семейства данного типа, лежащая в интервале [lo, hi]
        /// (позиция = базовая ± пробный размер, sign задаёт сторону от базы).
        /// </summary>
        private static bool FamilySide(
            Document doc, ViewSection view, FamilyInstance win, FamilyInstanceReferenceType t,
            Line line, Reference baseRef, double basePos, double lo, double hi, int sign,
            out Reference r, out double pos)
        {
            r = null; pos = 0;
            try
            {
                foreach (Reference cand in win.GetReferences(t) ?? new List<Reference>())
                {
                    double? v = TempDimValue(doc, view, baseRef, cand, line);
                    if (v == null) continue;
                    double p = basePos + sign * v.Value;
                    if (p < lo || p > hi) continue;
                    r = cand; pos = p;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Габаритный бокс окна в координатах обрезки вида.</summary>
        private static ViewBox ComputeViewBox(ViewSection view, FamilyInstance win)
        {
            BoundingBoxXYZ wb = win.get_BoundingBox(null);
            if (wb == null) return null;

            Transform t = view.CropBox.Transform;
            Transform inv = t.Inverse;
            var b = new ViewBox
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
                        if (p.X < b.MinX) b.MinX = p.X;
                        if (p.X > b.MaxX) b.MaxX = p.X;
                        if (p.Y < b.MinY) b.MinY = p.Y;
                        if (p.Y > b.MaxY) b.MaxY = p.Y;
                        if (p.Z < b.MinZ) b.MinZ = p.Z;
                        if (p.Z > b.MaxZ) b.MaxZ = p.Z;
                    }
            return b;
        }

        /// <summary>
        /// Ищет грани проёма в стене-основе НЕ ДАЛЬШЕ OPENING_NEAR_MM от краёв рамы:
        /// откосы (нормаль вдоль стены) слева/справа от рамы, перемычка/подоконная
        /// грань (вертикальная нормаль) над/под рамой. Из кандидатов берётся крайняя
        /// грань. Торцы стены и грани других проёмов так не попадают.
        /// </summary>
        private static OpeningInfo ScanOpening(IList<Wall> walls, ViewBox b)
        {
            try
            {
                Transform inv = b.T.Inverse;
                double near = Mm(OPENING_NEAR_MM);
                XYZ ax = b.T.BasisX;            // вдоль стены в модели

                var left = new List<Tuple<double, Reference>>();
                var right = new List<Tuple<double, Reference>>();
                var below = new List<Tuple<double, Reference>>();
                var above = new List<Tuple<double, Reference>>();

                var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                foreach (Wall wall in walls)
                foreach (GeometryObject go in wall.get_Geometry(opt) ?? Enumerable.Empty<GeometryObject>())
                {
                    if (!(go is Solid solid) || solid.Faces.IsEmpty) continue;
                    foreach (Face face in solid.Faces)
                    {
                        if (!(face is PlanarFace pf) || pf.Reference == null) continue;

                        BoundingBoxUV uv = pf.GetBoundingBox();
                        XYZ c = inv.OfPoint(pf.Evaluate((uv.Min + uv.Max) / 2));

                        if (Math.Abs(pf.FaceNormal.DotProduct(ax)) > 0.97)
                        {
                            // откос: по высоте — в полосе проёма, по X — вплотную к краю рамы
                            if (c.Y < b.MinY - near || c.Y > b.MaxY + near) continue;
                            if (c.X >= b.MinX - near && c.X <= b.MinX + near / 2)
                                left.Add(Tuple.Create(c.X, pf.Reference));
                            else if (c.X >= b.MaxX - near / 2 && c.X <= b.MaxX + near)
                                right.Add(Tuple.Create(c.X, pf.Reference));
                        }
                        else if (Math.Abs(pf.FaceNormal.DotProduct(XYZ.BasisZ)) > 0.97)
                        {
                            // верх/низ проёма: по ширине — в полосе рамы, по Y — у края
                            if (c.X < b.MinX - near || c.X > b.MaxX + near) continue;
                            if (c.Y >= b.MinY - near && c.Y <= b.MinY + near / 2)
                                below.Add(Tuple.Create(c.Y, pf.Reference));
                            else if (c.Y >= b.MaxY - near / 2 && c.Y <= b.MaxY + near)
                                above.Add(Tuple.Create(c.Y, pf.Reference));
                        }
                    }
                }

                // грани каждой стороны группируются в кластеры по глубине: крайний
                // кластер — внутренний (широкий) проём, следующий внутрь — наружный
                // (четверть). extremeIsMax=true, если внутренняя грань имеет макс. позицию.
                var cl = ClusterFaces(left, false);   // левый откос: внутренний = min X
                var cr = ClusterFaces(right, true);    // правый:      внутренний = max X
                var cb = ClusterFaces(below, false);   // низ:         внутренний = min Y
                var ca = ClusterFaces(above, true);    // верх:        внутренний = max Y

                var info = new OpeningInfo
                {
                    CandL = cl.Count, CandR = cr.Count,
                    CandB = cb.Count, CandT = ca.Count
                };
                if (cl.Count > 0 && cr.Count > 0)
                {
                    info.Left = cl[0].Item2; info.MinX = cl[0].Item1;
                    info.Right = cr[0].Item2; info.MaxX = cr[0].Item1;
                    if (cl.Count > 1 && cr.Count > 1)
                    {
                        info.LeftOut = cl[1].Item2; info.MinXOut = cl[1].Item1;
                        info.RightOut = cr[1].Item2; info.MaxXOut = cr[1].Item1;
                    }
                }
                if (cb.Count > 0 && ca.Count > 0)
                {
                    info.Bottom = cb[0].Item2; info.MinY = cb[0].Item1;
                    info.Top = ca[0].Item2; info.MaxY = ca[0].Item1;
                    if (cb.Count > 1 && ca.Count > 1)
                    {
                        info.BottomOut = cb[1].Item2; info.MinYOut = cb[1].Item1;
                        info.TopOut = ca[1].Item2; info.MaxYOut = ca[1].Item1;
                    }
                }
                return info;
            }
            catch { return null; }
        }

        /// <summary>
        /// Группирует грани откоса по позиции (кластеры ближе 20 мм — одна плоскость),
        /// возвращает представителей кластеров так, что [0] — крайний (внутренний
        /// проём), [1] — следующий внутрь (наружная четверть). extremeIsMax задаёт,
        /// что «крайний» = наибольшая позиция.
        /// </summary>
        private static List<Tuple<double, Reference>> ClusterFaces(
            List<Tuple<double, Reference>> list, bool extremeIsMax)
        {
            var sorted = list.OrderBy(p => p.Item1).ToList();
            var clusters = new List<Tuple<double, Reference>>();
            double tol = Mm(20);
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1].Item1 - sorted[j].Item1 < tol) j++;
                double avg = (sorted[i].Item1 + sorted[j].Item1) / 2;
                clusters.Add(Tuple.Create(avg, sorted[(i + j) / 2].Item2));
                i = j + 1;
            }
            if (extremeIsMax) clusters.Reverse();   // [0] = наибольшая позиция
            return clusters;                         // иначе [0] = наименьшая
        }

        /// <summary>
        /// Обрезка вида по рабочему прямоугольнику с запасом CROP_MARGIN_MM и дальняя
        /// подрезка сразу за самой дальней точкой окна.
        /// </summary>
        private static void SetCrop(ViewSection view, ViewBox b)
        {
            try
            {
                if (b == null) return;
                BoundingBoxXYZ crop = view.CropBox;
                double m = Mm(CROP_MARGIN_MM);
                crop.Min = new XYZ(b.MinX - m, b.MinY - m, crop.Min.Z);
                crop.Max = new XYZ(b.MaxX + m, b.MaxY + m, crop.Max.Z);

                view.CropBoxActive = true;
                view.CropBox = crop;
                view.CropBoxVisible = false;

                double farClip = -b.MinZ + Mm(FAR_CLIP_EXTRA_MM);
                if (farClip <= 0) return;
                Parameter mode = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING);
                if (mode != null && !mode.IsReadOnly) mode.Set(1); // подрезка без линии
                Parameter offP = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                if (offP != null && !offP.IsReadOnly) offP.Set(farClip);
            }
            catch { }
        }

        // ==================== ОФОРМЛЕНИЕ: ОБЛАСТЬ ====================

        /// <summary>
        /// Цветовая область-рамка REGION_WIDTH_MM вокруг проёма. Линиям контуров
        /// назначаются стили: наружному — «Невидимые линии», внутреннему — «Скрыто».
        /// </summary>
        private static bool PlaceRegion(Document doc, ViewSection view, ViewBox b, DecorSet decor)
        {
            if (decor.Region == null) return false;
            try
            {
                double w = Mm(REGION_WIDTH_MM);
                var loops = new List<CurveLoop>
                {
                    RectLoop(b, b.MinX - w, b.MinY - w, b.MaxX + w, b.MaxY + w),
                    RectLoop(b, b.MinX, b.MinY, b.MaxX, b.MaxY)
                };
                FilledRegion fr = FilledRegion.Create(doc, decor.Region.Id, view.Id, loops);
                if (fr == null) return false;
                SetRegionLineStyles(doc, fr, b, w);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Назначает стили линиям контуров области: линия, чья середина лежит на
        /// внутреннем прямоугольнике (проём), получает REGION_LINE_INNER, остальные —
        /// REGION_LINE_OUTER. Стили ищутся по имени без учёта скобок «&lt;…&gt;».
        /// </summary>
        private static void SetRegionLineStyles(Document doc, FilledRegion fr, ViewBox b, double w)
        {
            try
            {
                GraphicsStyle outer = FindLineStyle(doc, REGION_LINE_OUTER);
                GraphicsStyle inner = FindLineStyle(doc, REGION_LINE_INNER);
                if (outer == null && inner == null) return;

                doc.Regenerate();
                Transform inv = b.T.Inverse;
                double tol = w / 2;
                foreach (ElementId id in fr.GetDependentElements(new ElementClassFilter(typeof(CurveElement))))
                {
                    if (!(doc.GetElement(id) is CurveElement ce) || ce.GeometryCurve == null) continue;
                    XYZ p = inv.OfPoint(ce.GeometryCurve.Evaluate(0.5, true));
                    bool onInner =
                        (Math.Abs(p.X - b.MinX) < tol || Math.Abs(p.X - b.MaxX) < tol ||
                         Math.Abs(p.Y - b.MinY) < tol || Math.Abs(p.Y - b.MaxY) < tol) &&
                        p.X > b.MinX - tol && p.X < b.MaxX + tol &&
                        p.Y > b.MinY - tol && p.Y < b.MaxY + tol;
                    GraphicsStyle gs = onInner ? inner : outer;
                    if (gs != null)
                        try { ce.LineStyle = gs; } catch { }
                }
            }
            catch { }
        }

        /// <summary>Стиль линий по имени; встроенные вида «&lt;Скрыто&gt;» находятся без скобок.</summary>
        private static GraphicsStyle FindLineStyle(Document doc, string name)
        {
            string Clean(string s) => (s ?? "").Trim().Trim('<', '>').Trim();
            return new FilteredElementCollector(doc)
                .OfClass(typeof(GraphicsStyle))
                .Cast<GraphicsStyle>()
                .Where(gs => gs.GraphicsStyleType == GraphicsStyleType.Projection)
                .FirstOrDefault(gs => string.Equals(Clean(gs.Name), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Прямоугольный контур в плоскости вида.</summary>
        private static CurveLoop RectLoop(ViewBox b, double x0, double y0, double x1, double y1)
        {
            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(b.Pt(x0, y0), b.Pt(x1, y0)));
            loop.Append(Line.CreateBound(b.Pt(x1, y0), b.Pt(x1, y1)));
            loop.Append(Line.CreateBound(b.Pt(x1, y1), b.Pt(x0, y1)));
            loop.Append(Line.CreateBound(b.Pt(x0, y1), b.Pt(x0, y0)));
            return loop;
        }

        // ==================== ОФОРМЛЕНИЕ: РАЗРЕЗЫ ====================

        /// <summary>
        /// Два разреза по окну: «Nа-Nа» — горизонтальный (взгляд вниз) и «Nб-Nб» —
        /// вертикальный (вдоль стены), где N — число из маркировки (Ок-1 → 1).
        /// Разрезы подрезаются под окно (по толщине стены-основы с запасом) и
        /// оформляются размерами: цепочка, габарит, проём — с наружной стороны.
        /// </summary>
        private static int PlaceSections(
            Document doc, WinGeom g, XYZ f, string mark, FamilyInstance win,
            DecorSet decor, HashSet<string> viewNames, out int secDims)
        {
            secDims = 0;
            ViewBox b = g.Box;
            if (decor.SectionVft == null || b == null) return 0;
            int done = 0;
            string n = new string(mark.Where(char.IsDigit).ToArray());
            if (n.Length == 0) n = mark;

            XYZ center = b.T.OfPoint(new XYZ(b.CenX, b.CenY, b.CenZ));
            XYZ wallDir = XYZ.BasisZ.CrossProduct(f).Normalize();
            double halfW = (b.MaxX - b.MinX) / 2 + Mm(SEC_MARGIN_MM);
            double halfH = (b.MaxY - b.MinY) / 2 + Mm(SEC_MARGIN_MM);
            Wall hw = win.Host as Wall;
            double wallHalf = hw != null ? hw.Width / 2 : Mm(250);
            // внутрь — тесно по стене; наружу — за плоскость вида-фасада (маркер в
            // MARKER_OFFSET_MM), иначе линия разреза не отображается на виде окна
            double inSide = wallHalf + Mm(SEC_ACROSS_EXTRA_MM);
            double outSide = Mm(MARKER_OFFSET_MM + 100);
            double depth = Mm(SEC_DEPTH_MM);

            // горизонтальный разрез «Nа-Nа»: секущая плоскость горизонтальна, взгляд вниз
            string nameA = $"{n}а-{n}а";
            if (!viewNames.Contains(nameA))
            {
                Transform ta = Transform.Identity;
                ta.Origin = center;
                ta.BasisX = wallDir;
                ta.BasisY = f;
                ta.BasisZ = -XYZ.BasisZ;
                ViewSection s = CreateSectionView(doc, decor, nameA, $"{n}а", ta,
                    -halfW, halfW, -inSide, outSide, depth);
                if (s != null)
                {
                    viewNames.Add(nameA);
                    done++;
                    secDims += SectionDims(doc, s, g, decor, center, wallDir, f, halfW, wallHalf, true);
                    // линии обрыва по левому/правому краю, зеркальны друг другу (хвосты
                    // наружу), поперёк — глубина, длина BREAKLINE_LEN_MM. На «Nа-Nа»
                    // развёрнуты на 180° относительно «Nб-Nб».
                    double bl = Mm(BREAKLINE_LEN_MM / 2);
                    PlaceBreakLine(doc, s, decor.BreakLine, center + wallDir * (halfW - Mm(20)), -f, bl);
                    PlaceBreakLine(doc, s, decor.BreakLine, center - wallDir * (halfW - Mm(20)), f, bl);
                }
            }

            // вертикальный разрез «Nб-Nб»: секущая плоскость поперёк окна, взгляд вдоль
            // стены. Секущую ставим не по центру (там импост), а по середине правой
            // створки — правее импоста; при отсутствии импоста — по центру окна.
            string nameB = $"{n}б-{n}б";
            if (!viewNames.Contains(nameB))
            {
                double sashX = g.FacesX.Count >= 3
                    ? (g.FacesX[g.FacesX.Count - 2].Pos + g.FacesX[g.FacesX.Count - 1].Pos) / 2
                    : b.CenX;
                XYZ centerB = b.T.OfPoint(new XYZ(sashX, b.CenY, b.CenZ));
                XYZ w = wallDir.Negate();
                Transform tb = Transform.Identity;
                tb.Origin = centerB;
                tb.BasisX = XYZ.BasisZ.CrossProduct(w);
                tb.BasisY = XYZ.BasisZ;
                tb.BasisZ = w;
                // низ разреза опускаем до плиты под окном, верх — до плиты перекрытия
                // сверху (для размеров до пола/плиты)
                double botY = -halfH;
                if (g.FloorOk)
                    botY = Math.Min(botY, (g.FloorMinY - b.CenY) - Mm(150));
                double topY = halfH;
                if (g.CeilingOk)
                    topY = Math.Max(topY, (g.CeilingBotY - b.CenY) + Mm(150));
                ViewSection s = CreateSectionView(doc, decor, nameB, $"{n}б", tb,
                    -inSide, outSide, botY, topY, depth);
                if (s != null)
                {
                    viewNames.Add(nameB);
                    done++;
                    // наружу в плоскости вида = BasisX вертикального разреза (= f)
                    secDims += SectionDims(doc, s, g, decor, center, XYZ.BasisZ, tb.BasisX, halfH, wallHalf, false);
                    // линии обрыва по верхнему/нижнему краю, зеркальны друг другу (хвосты
                    // наружу), поперёк — глубина, длина BREAKLINE_LEN_MM
                    double bl = Mm(BREAKLINE_LEN_MM / 2);
                    PlaceBreakLine(doc, s, decor.BreakLine, centerB + XYZ.BasisZ * (topY - Mm(20)), tb.BasisX, bl);
                    PlaceBreakLine(doc, s, decor.BreakLine, centerB + XYZ.BasisZ * (botY + Mm(20)), -tb.BasisX, bl);
                    // линия обрыва слева, вертикальная (вдоль высоты), отодвинута на
                    // 300 мм от стены в сторону ОТ окна (-f)
                    double midY = (topY + botY) / 2;
                    double halfLenV = (topY - botY) / 2 - Mm(20);
                    PlaceBreakLine(doc, s, decor.BreakLine,
                        centerB - tb.BasisX * (wallHalf + Mm(300)) + XYZ.BasisZ * midY,
                        XYZ.BasisZ, halfLenV);
                }
            }
            return done;
        }

        /// <summary>
        /// Ставит семейство линии обрыва в разрез: для линейного детального компонента —
        /// отрезком вдоль spanDir; для точечного — в точке mid с поворотом к spanDir и
        /// установкой параметра «Длина». halfLen — полудлина линии.
        /// </summary>
        private static bool PlaceBreakLine(
            Document doc, View view, FamilySymbol sym, XYZ mid, XYZ spanDir, double halfLen)
        {
            if (sym == null) return false;
            try
            {
                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                XYZ dir = spanDir.Normalize();
                FamilyInstance fi;
                if (sym.Family.FamilyPlacementType == FamilyPlacementType.CurveBasedDetail)
                {
                    Line ln = Line.CreateBound(mid - dir * halfLen, mid + dir * halfLen);
                    fi = doc.Create.NewFamilyInstance(ln, sym, view);
                }
                else
                {
                    fi = doc.Create.NewFamilyInstance(mid, sym, view);
                    doc.Regenerate();
                    double ang = SignedAngle(view.RightDirection, dir, view.ViewDirection);
                    if (Math.Abs(ang) > 1e-6)
                        ElementTransformUtils.RotateElement(doc, fi.Id,
                            Line.CreateBound(mid, mid + view.ViewDirection), ang);
                    Parameter lp = fi.LookupParameter("Длина") ?? fi.LookupParameter("Length");
                    if (lp != null && !lp.IsReadOnly && lp.StorageType == StorageType.Double)
                        lp.Set(2 * halfLen);
                }
                return fi != null;
            }
            catch { return false; }
        }

        /// <summary>Знаковый угол от from к to вокруг оси axis (в плоскости, перпендикулярной axis).</summary>
        private static double SignedAngle(XYZ from, XYZ to, XYZ axis)
        {
            double a = from.AngleTo(to);
            return from.CrossProduct(to).DotProduct(axis) < 0 ? -a : a;
        }

        /// <summary>Первый типоразмер семейства детального компонента с подстрокой в имени семейства.</summary>
        private static FamilySymbol FindBreakLine(Document doc, string sub) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => (s.Family?.Name ?? "").IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(s => s.Name.IndexOf("хвост", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .FirstOrDefault();

        /// <summary>
        /// Типоразмер марки окна (категория «Марки окон»). Предпочтение — семейству
        /// «Марка_Окно» без «Дверь» (чтобы не взять «Марка_ДверьОкно_КР»); затем любое
        /// с «Окно» без «Дверь»; затем любое.
        /// </summary>
        private static FamilySymbol FindWindowTag(Document doc)
        {
            int Rank(string name)
            {
                name = name ?? "";
                bool door = name.IndexOf("Дверь", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!door && name.IndexOf("Марка_Окно", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
                if (!door && name.IndexOf("Окно", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                if (name.IndexOf("Окно", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
                return 3;
            }
            // приоритет типа: имя типоразмера с «типоразмер» (нужен тип
            // «Маркировка типоразмера», а не «Марка»)
            int TypeRank(string name) =>
                (name ?? "").IndexOf("типоразмер", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;

            return new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_WindowTags).Cast<FamilySymbol>()
                .OrderBy(s => Rank(s.Family?.Name))
                .ThenBy(s => TypeRank(s.Name))
                .FirstOrDefault();
        }

        /// <summary>Ставит марку окна над проёмом на виде-фасаде, тегом на само окно.</summary>
        private static bool PlaceWindowTag(
            Document doc, ViewSection view, WinGeom g, FamilyInstance win, FamilySymbol tag)
        {
            if (tag == null || g == null) return false;
            try
            {
                if (!tag.IsActive) { tag.Activate(); doc.Regenerate(); }
                XYZ pnt = g.Box.Pt(g.Box.CenX, g.Box.MaxY + Mm(500));
                IndependentTag t = IndependentTag.Create(
                    doc, tag.Id, view.Id, new Reference(win), false,
                    TagOrientation.Horizontal, pnt);
                return t != null;
            }
            catch { return false; }
        }

        /// <summary>Создаёт разрез по рамке (Transform + границы + глубина), с шаблоном.</summary>
        private static ViewSection CreateSectionView(
            Document doc, DecorSet decor, string name, string detailNumber, Transform frame,
            double minX, double maxX, double minY, double maxY, double depth)
        {
            try
            {
                var bb = new BoundingBoxXYZ
                {
                    Transform = frame,
                    Min = new XYZ(minX, minY, 0),
                    Max = new XYZ(maxX, maxY, depth)
                };
                ViewSection s = ViewSection.CreateSection(doc, decor.SectionVft.Id, bb);
                if (s == null) return null;
                try { s.Name = name; } catch { }
                if (decor.SectionTpl != null)
                    try { s.ViewTemplateId = decor.SectionTpl.Id; } catch { }
                // граница обрезки активна (подрезка под окно), но не показывается
                try { s.CropBoxActive = true; s.CropBoxVisible = false; } catch { }
                try
                {
                    Parameter dn = s.get_Parameter(BuiltInParameter.VIEWER_DETAIL_NUMBER);
                    if (dn != null && !dn.IsReadOnly) dn.Set(detailNumber);
                }
                catch { }
                SetOrgParams(s);
                return s;
            }
            catch { return null; }
        }

        /// <summary>
        /// Размеры на разрезе окна с наружной стороны стены: цепочка (проём/габарит/
        /// ось импоста), номинальный габарит и проём — те же опорные, что на фасаде.
        /// ux — направление размерной линии (вдоль стены или вертикаль), uOut —
        /// наружу от стены в плоскости вида.
        /// </summary>
        private static int SectionDims(
            Document doc, ViewSection sec, WinGeom g, DecorSet decor,
            XYZ center, XYZ ux, XYZ uOut, double half, double wallHalf, bool horizontal)
        {
            int done = 0;
            try
            {
                doc.Regenerate();
                OpeningInfo o = g.Opening;
                List<FacePt> faces = horizontal ? g.FacesX : g.FacesY;
                bool ok = horizontal ? (o != null && o.XOk) : (o != null && o.YOk);

                // внутренний проём — на противоположную сторону от наружного:
                // внутренний уходит вправо, наружный — влево
                uOut = uOut.Negate();

                Line L(double off) => Line.CreateBound(
                    center + uOut * off - ux * half,
                    center + uOut * off + ux * half);

                double d1 = wallHalf + Mm(SEC_DIM_OFFSET_MM);
                double d2 = d1 + Mm(SEC_DIM_STEP_MM);
                double d3 = d2 + Mm(SEC_DIM_STEP_MM);

                var chain = new ReferenceArray();
                // вертикальная цепочка продлевается вниз (низ плиты → пол → низ проёма)
                // и вверх (… → верх проёма → низ плиты перекрытия сверху)
                if (!horizontal && g.FloorOk)
                    foreach (var fp in g.FloorPts) chain.Append(fp.Item2);
                if (ok) chain.Append(horizontal ? o.Left : o.Bottom);
                foreach (FacePt fp in faces) chain.Append(fp.Ref);
                if (ok) chain.Append(horizontal ? o.Right : o.Top);
                if (!horizontal && g.CeilingOk) chain.Append(g.CeilingBot);
                if (chain.Size >= 3)
                {
                    Dimension cd = MakeDim(doc, sec, L(d1), chain, decor.Dim, g.DimIds);
                    if (cd != null)
                    {
                        done++;
                        // на вертикальном разрезе тесные сегменты выносим наружу выноской
                        if (!horizontal) PullSmallSegments(cd, uOut, Mm(250), Mm(SEC_DIM_STEP_MM));
                    }
                }

                // на месте габарита рамы — размер проёма (габарит рамы на разрезе убран)
                if (ok && TryDim(doc, sec, L(d2),
                        Pair(horizontal ? o.Left : o.Bottom, horizontal ? o.Right : o.Top),
                        decor.DimOpening ?? decor.Dim, g.DimIds,
                        decor.DimOpening == null ? DIM_OPENING_SUFFIX : null)) done++;

                // ----- наружный проём + четверти ОДНОЙ цепочкой с внешней стороны -----
                // точки: внутр.низ, нар.низ, нар.верх, внутр.верх — по позиции, без
                // дублей ближе 3 мм. Сегменты: четверть | наружный проём | четверть.
                bool outOk = horizontal ? (o != null && o.XOutOk) : (o != null && o.YOutOk);
                if (outOk)
                {
                    var pts = new List<Tuple<double, Reference>>
                    {
                        Tuple.Create(horizontal ? o.MinX    : o.MinY,    horizontal ? o.Left     : o.Bottom),
                        Tuple.Create(horizontal ? o.MinXOut : o.MinYOut, horizontal ? o.LeftOut  : o.BottomOut),
                        Tuple.Create(horizontal ? o.MaxXOut : o.MaxYOut, horizontal ? o.RightOut : o.TopOut),
                        Tuple.Create(horizontal ? o.MaxX    : o.MaxY,    horizontal ? o.Right    : o.Top),
                    };
                    pts.Sort((p, q) => p.Item1.CompareTo(q.Item1));
                    var outChain = new ReferenceArray();
                    double last = double.NaN;
                    foreach (var pt in pts)
                        if (double.IsNaN(last) || Math.Abs(pt.Item1 - last) > Mm(3))
                        { outChain.Append(pt.Item2); last = pt.Item1; }
                    if (outChain.Size >= 2) { if (TryDim(doc, sec, L(-d1), outChain, decor.Dim, g.DimIds)) done++; }
                }
            }
            catch { }
            return done;
        }

        // ==================== ОФОРМЛЕНИЕ: РАЗМЕРЫ ====================

        /// <summary>
        /// Размеры на виде окна. Снизу: цепочка проём/габарит/ось импоста, габарит по
        /// номинальным «Ширина»/«Высота», проём. Слева: то же по вертикали. Проём —
        /// по опорным семейства (стабильны), с суффиксом «(Проем)» при отсутствии
        /// специального типа.
        /// </summary>
        private static int PlaceDimensions(
            Document doc, ViewSection view, WinGeom g, FamilyInstance win, DecorSet decor)
        {
            int done = 0;
            doc.Regenerate();
            ViewBox b = g.Box;
            OpeningInfo opening = g.Opening;

            // --- низ: горизонтальные размеры ---
            Line lineChainH = HLine(b, b.MinY - Mm(DIM_CHAIN_MM));
            Line lineTotalH = HLine(b, b.MinY - Mm(DIM_TOTAL_MM));
            Line lineOpenH = HLine(b, b.MinY - Mm(DIM_OPENING_MM));

            // крайние точки — на плоскости номинального габарита («Ширина» типа),
            // промежуточные — на осевые семейства (середина импоста)
            g.NomX = NominalizeEnds(doc, view, win, g, horizontal: true, line: lineChainH);
            g.SnappedX = SnapToCenterlines(doc, view, win: g, horizontal: true, line: lineChainH);

            if (g.FacesX.Count >= 2 &&
                TryDim(doc, view, lineTotalH, Pair(g.FacesX.First().Ref, g.FacesX.Last().Ref), decor.Dim, g.DimIds))
                done++;

            var chainH = new ReferenceArray();
            if (opening != null && opening.XOk) chainH.Append(opening.Left);
            foreach (var fr in g.FacesX) chainH.Append(fr.Ref);
            if (opening != null && opening.XOk) chainH.Append(opening.Right);
            if (chainH.Size >= 3 && TryDim(doc, view, lineChainH, chainH, decor.Dim, g.DimIds)) done++;

            // суффикс вручную — только если специального типа нет (тип сам даёт «(Проем)»)
            if (opening != null && opening.XOk &&
                TryDim(doc, view, lineOpenH, Pair(opening.Left, opening.Right),
                    decor.DimOpening ?? decor.Dim, g.DimIds,
                    decor.DimOpening == null ? DIM_OPENING_SUFFIX : null)) done++;

            // --- лево: вертикальные размеры ---
            Line lineChainV = VLine(b, b.MinX - Mm(DIM_CHAIN_MM));
            Line lineTotalV = VLine(b, b.MinX - Mm(DIM_TOTAL_MM));
            Line lineOpenV = VLine(b, b.MinX - Mm(DIM_OPENING_MM));

            g.NomY = NominalizeEnds(doc, view, win, g, horizontal: false, line: lineChainV);
            g.SnappedY = SnapToCenterlines(doc, view, win: g, horizontal: false, line: lineChainV);

            if (g.FacesY.Count >= 2 &&
                TryDim(doc, view, lineTotalV, Pair(g.FacesY.First().Ref, g.FacesY.Last().Ref), decor.Dim, g.DimIds))
                done++;

            var chainV = new ReferenceArray();
            if (opening != null && opening.YOk) chainV.Append(opening.Bottom);
            foreach (var fr in g.FacesY) chainV.Append(fr.Ref);
            if (opening != null && opening.YOk) chainV.Append(opening.Top);
            if (chainV.Size >= 3 && TryDim(doc, view, lineChainV, chainV, decor.Dim, g.DimIds)) done++;

            if (opening != null && opening.YOk &&
                TryDim(doc, view, lineOpenV, Pair(opening.Bottom, opening.Top),
                    decor.DimOpening ?? decor.Dim, g.DimIds,
                    decor.DimOpening == null ? DIM_OPENING_SUFFIX : null)) done++;

            return done;
        }

        /// <summary>
        /// Сажает крайние точки цепочки и габаритного размера на плоскости номинального
        /// габарита окна — параметры «Ширина»/«Высота» типа (например 1760x1780 при
        /// геометрической раме 1660x1680 и проёме 1800x1850). Среди опорных семейства
        /// ищется пара на расстоянии ровно W: позиции меряются пробными размерами.
        /// </summary>
        private static bool NominalizeEnds(
            Document doc, ViewSection view, FamilyInstance win, WinGeom g, bool horizontal, Line line)
        {
            try
            {
                List<FacePt> faces = horizontal ? g.FacesX : g.FacesY;
                if (faces.Count < 2) return false;
                double? nom = NominalSize(win, horizontal);
                if (nom == null || nom.Value < Mm(100)) return false;
                double W = nom.Value;

                double frameLo = faces[0].Pos, frameHi = faces[faces.Count - 1].Pos;
                if (Math.Abs((frameHi - frameLo) - W) < Mm(3)) return true;   // уже номинал

                // зоны поиска: от проёма (с запасом 5 мм) до 60 мм внутрь рамы
                OpeningInfo o = g.Opening;
                double z = Mm(5), inner = Mm(60), near = Mm(OPENING_NEAR_MM);
                bool ok = horizontal ? (o != null && o.XOk) : (o != null && o.YOk);
                double lo0 = ok ? (horizontal ? o.MinX : o.MinY) - z : frameLo - near;
                double hi1 = ok ? (horizontal ? o.MaxX : o.MaxY) + z : frameHi + near;

                var types = horizontal
                    ? new[] { FamilyInstanceReferenceType.Left, FamilyInstanceReferenceType.Right,
                              FamilyInstanceReferenceType.StrongReference, FamilyInstanceReferenceType.WeakReference }
                    : new[] { FamilyInstanceReferenceType.Bottom, FamilyInstanceReferenceType.Top,
                              FamilyInstanceReferenceType.StrongReference, FamilyInstanceReferenceType.WeakReference };
                var cands = new List<Reference>();
                foreach (FamilyInstanceReferenceType t in types)
                    try { cands.AddRange(win.GetReferences(t) ?? new List<Reference>()); } catch { }
                if (cands.Count == 0) return false;

                // позиции кандидатов; знак неизвестен — рассматриваем обе стороны от базы
                Reference baseRef = faces[0].Ref;
                double basePos = faces[0].Pos;
                var loSet = new List<Tuple<double, Reference>>();
                var hiSet = new List<Tuple<double, Reference>>();
                foreach (Reference r in cands)
                {
                    double? v = TempDimValue(doc, view, baseRef, r, line);
                    if (v == null) continue;
                    foreach (int sign in new[] { -1, 1 })
                    {
                        double p = basePos + sign * v.Value;
                        if (p >= lo0 && p <= frameLo + inner) loSet.Add(Tuple.Create(p, r));
                        if (p >= frameHi - inner && p <= hi1) hiSet.Add(Tuple.Create(p, r));
                        if (v.Value < Mm(1)) break;
                    }
                }

                // лучшая пара: расстояние совпадает с номиналом (допуск 3 мм)
                Tuple<double, Reference> bestLo = null, bestHi = null;
                double bestErr = Mm(3);
                foreach (var l in loSet)
                    foreach (var h in hiSet)
                    {
                        double err = Math.Abs((h.Item1 - l.Item1) - W);
                        if (err < bestErr) { bestErr = err; bestLo = l; bestHi = h; }
                    }
                if (bestLo == null) return false;

                faces[0] = new FacePt { Pos = bestLo.Item1, Mid = bestLo.Item1, Ref = bestLo.Item2 };
                faces[faces.Count - 1] = new FacePt { Pos = bestHi.Item1, Mid = bestHi.Item1, Ref = bestHi.Item2 };
                return true;
            }
            catch { return false; }
        }

        /// <summary>Номинальный габарит окна: параметр «Ширина»/«Высота» (экземпляр, тип, встроенные).</summary>
        private static double? NominalSize(FamilyInstance win, bool horizontal)
        {
            string name = horizontal ? "Ширина" : "Высота";
            var candidates = new[]
            {
                win.LookupParameter(name),
                win.Symbol?.LookupParameter(name),
                win.Symbol?.get_Parameter(horizontal
                    ? BuiltInParameter.WINDOW_WIDTH : BuiltInParameter.WINDOW_HEIGHT),
                win.Symbol?.get_Parameter(horizontal
                    ? BuiltInParameter.FAMILY_WIDTH_PARAM : BuiltInParameter.FAMILY_HEIGHT_PARAM),
                win.Symbol?.get_Parameter(horizontal
                    ? BuiltInParameter.GENERIC_WIDTH : BuiltInParameter.GENERIC_HEIGHT)
            };
            foreach (Parameter p in candidates)
                if (p != null && p.StorageType == StorageType.Double && p.HasValue)
                    return p.AsDouble();
            return null;
        }

        /// <summary>
        /// Сажает промежуточные точки цепочки (импосты) на осевые опорные семейства.
        /// Кандидаты — осевые (Center L/R или Center Elevation), сильные и слабые
        /// опорные экземпляра. Положение каждого кандидата меряется пробным размером
        /// от первой грани рамы; если кандидат лежит в пределах CENTER_SNAP_MM от
        /// середины группы граней (центра импоста) — он заменяет грань в цепочке.
        /// Крайние точки (рама) не трогаются. Возвращает число заменённых точек.
        /// </summary>
        private static int SnapToCenterlines(Document doc, ViewSection view, WinGeom win, bool horizontal, Line line)
        {
            List<FacePt> faces = horizontal ? win.FacesX : win.FacesY;
            if (faces.Count < 3) return 0;

            FamilyInstance fi = null;
            try { fi = doc.GetElement(faces[0].Ref.ElementId) as FamilyInstance; } catch { }
            if (fi == null) return 0;

            var cands = new List<Reference>();
            foreach (FamilyInstanceReferenceType t in new[]
                     {
                         horizontal ? FamilyInstanceReferenceType.CenterLeftRight
                                    : FamilyInstanceReferenceType.CenterElevation,
                         FamilyInstanceReferenceType.StrongReference,
                         FamilyInstanceReferenceType.WeakReference
                     })
                try { cands.AddRange(fi.GetReferences(t) ?? new List<Reference>()); } catch { }
            if (cands.Count == 0) return 0;

            Reference baseRef = faces[0].Ref;
            double basePos = faces[0].Pos;
            double snap = Mm(CENTER_SNAP_MM);
            var bestDist = new double[faces.Count];
            for (int i = 0; i < faces.Count; i++) bestDist[i] = double.MaxValue;
            int snapped = 0;

            foreach (Reference r in cands)
            {
                double? v = TempDimValue(doc, view, baseRef, r, line);
                if (v == null || v.Value < Mm(5)) continue;
                double pos = basePos + v.Value;

                // только промежуточные точки: крайние — это рама
                for (int i = 1; i < faces.Count - 1; i++)
                {
                    double dist = Math.Abs(pos - faces[i].Mid);
                    if (dist > snap || dist >= bestDist[i]) continue;
                    if (bestDist[i] == double.MaxValue) snapped++;
                    bestDist[i] = dist;
                    faces[i] = new FacePt { Pos = pos, Mid = faces[i].Mid, Ref = r };
                    break;
                }
            }
            return snapped;
        }

        /// <summary>Значение пробного размера между двумя опорными (сам размер удаляется).</summary>
        private static double? TempDimValue(Document doc, View view, Reference a, Reference bRef, Line line)
        {
            try
            {
                Dimension d = doc.Create.NewDimension(view, line, Pair(a, bRef));
                if (d == null) return null;
                double? v = d.Value;
                doc.Delete(d.Id);
                return v;
            }
            catch { return null; }
        }

        /// <summary>
        /// Грани геометрии окна для размеров: horizontal — грани с нормалью вдоль стены
        /// (бока рамы, импосты; позиции по X), иначе — с вертикальной нормалью (низ/верх
        /// рамы, горизонтальные импосты; позиции по Y). Мелкие грани (отлив, торцы
        /// стеклопакетов) отсекаются по площади, поперечный фильтр [perpMin, perpMax]
        /// отсекает грани вне полосы рамы. Близкие грани сливаются в одну.
        /// </summary>
        private static List<FacePt> WindowFaceRefs(
            FamilyInstance win, ViewBox b, bool horizontal, double perpMin, double perpMax)
        {
            var raw = new List<FacePt>();
            try
            {
                Transform inv = b.T.Inverse;
                XYZ ax = horizontal ? b.T.BasisX : XYZ.BasisZ;
                double minArea = UnitUtils.ConvertToInternalUnits(MIN_FACE_AREA_M2, UnitTypeId.SquareMeters);
                double tol = Mm(20);

                var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                foreach (GeometryObject go in win.get_Geometry(opt) ?? Enumerable.Empty<GeometryObject>())
                {
                    if (go is Solid s0)
                        CollectFaces(s0, Transform.Identity, ax, inv, b, tol, minArea, horizontal, perpMin, perpMax, raw);
                    else if (go is GeometryInstance gi)
                    {
                        // опорные валидны только у геометрии символа; координаты — через
                        // трансформацию экземпляра
                        Transform tf = gi.Transform;
                        foreach (GeometryObject sub in gi.GetSymbolGeometry() ?? Enumerable.Empty<GeometryObject>())
                            if (sub is Solid s1)
                                CollectFaces(s1, tf, ax, inv, b, tol, minArea, horizontal, perpMin, perpMax, raw);
                    }
                }
            }
            catch { }

            // сортировка и слияние граней ближе DIM_CLUSTER_MM (импост — две грани);
            // Mid группы запоминается — на него потом сажаются осевые семейства
            raw.Sort((p, q) => p.Pos.CompareTo(q.Pos));
            var result = new List<FacePt>();
            int i = 0;
            double cluster = Mm(DIM_CLUSTER_MM);
            while (i < raw.Count)
            {
                int j = i;
                while (j + 1 < raw.Count && raw[j + 1].Pos - raw[j].Pos < cluster) j++;
                double mid = (raw[i].Pos + raw[j].Pos) / 2;
                FacePt best = raw[i];
                for (int k = i; k <= j; k++)
                    if (Math.Abs(raw[k].Pos - mid) < Math.Abs(best.Pos - mid)) best = raw[k];
                result.Add(new FacePt { Pos = best.Pos, Mid = mid, Ref = best.Ref });
                i = j + 1;
            }
            return result;
        }

        /// <summary>Собирает крупные плоские грани солида с нормалью вдоль ax в полосе окна.</summary>
        private static void CollectFaces(
            Solid solid, Transform tf, XYZ ax, Transform inv, ViewBox b, double tol, double minArea,
            bool horizontal, double perpMin, double perpMax, List<FacePt> outList)
        {
            if (solid == null || solid.Faces.IsEmpty) return;
            foreach (Face face in solid.Faces)
            {
                if (!(face is PlanarFace pf) || pf.Reference == null) continue;
                if (pf.Area < minArea) continue;
                XYZ n = tf.OfVector(pf.FaceNormal);
                if (Math.Abs(n.DotProduct(ax)) < 0.99) continue;

                BoundingBoxUV uv = pf.GetBoundingBox();
                XYZ c = inv.OfPoint(tf.OfPoint(pf.Evaluate((uv.Min + uv.Max) / 2)));
                double pos = horizontal ? c.X : c.Y;
                double perp = horizontal ? c.Y : c.X;
                // грань в пределах бокса вдоль оси и в заданной полосе поперёк
                if (pos < (horizontal ? b.MinX : b.MinY) - tol) continue;
                if (pos > (horizontal ? b.MaxX : b.MaxY) + tol) continue;
                if (perp < perpMin || perp > perpMax) continue;

                outList.Add(new FacePt { Pos = pos, Mid = pos, Ref = pf.Reference });
            }
        }

        /// <summary>Горизонтальная размерная линия в плоскости вида на высоте y.</summary>
        private static Line HLine(ViewBox b, double y) =>
            Line.CreateBound(b.Pt(b.MinX, y), b.Pt(b.MaxX, y));

        /// <summary>Вертикальная размерная линия в плоскости вида на отступе x.</summary>
        private static Line VLine(ViewBox b, double x) =>
            Line.CreateBound(b.Pt(x, b.MinY), b.Pt(x, b.MaxY));

        private static ReferenceArray Pair(Reference a, Reference bRef)
        {
            var arr = new ReferenceArray();
            arr.Append(a);
            arr.Append(bRef);
            return arr;
        }

        /// <summary>
        /// Создаёт размер; тип и суффикс применяются, если заданы. Id созданного
        /// размера пишется в ids — после регенерации по нему проверяется выживание.
        /// </summary>
        private static bool TryDim(
            Document doc, View view, Line line, ReferenceArray refs,
            DimensionType type, List<ElementId> ids, string suffix = null) =>
            MakeDim(doc, view, line, refs, type, ids, suffix) != null;

        /// <summary>Создаёт размер и возвращает его (или null). Id пишется в ids.</summary>
        private static Dimension MakeDim(
            Document doc, View view, Line line, ReferenceArray refs,
            DimensionType type, List<ElementId> ids, string suffix = null)
        {
            try
            {
                Dimension d = type != null
                    ? doc.Create.NewDimension(view, line, refs, type)
                    : doc.Create.NewDimension(view, line, refs);
                if (d == null) return null;
                if (suffix != null)
                    try { if (string.IsNullOrEmpty(d.Suffix)) d.Suffix = suffix; } catch { }
                ids?.Add(d.Id);
                return d;
            }
            catch { return null; }
        }

        /// <summary>
        /// У мелких сегментов цепочки (значение меньше maxVal) отодвигает текст наружу
        /// (вдоль uOut на offset) — Revit сам рисует выноску. Так тесные размеры
        /// (30, 40, 120, 180, 200 …) выносятся за пределы цепочки и читаются.
        /// </summary>
        private static void PullSmallSegments(Dimension dim, XYZ uOut, double maxVal, double offset)
        {
            try
            {
                DimensionSegmentArray segs = dim.Segments;
                if (segs == null || segs.Size == 0) return;
                foreach (DimensionSegment seg in segs)
                {
                    if (!seg.Value.HasValue || seg.Value.Value >= maxVal) continue;
                    try
                    {
                        XYZ tp = seg.TextPosition;
                        if (tp != null) seg.TextPosition = tp + uOut * offset;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ==================== ОБЩЕЕ ====================

        /// <summary>Параметры организации диспетчера: Стадия Р > Окна (если параметры есть).</summary>
        private static void SetOrgParams(View view)
        {
            SetTextParamByPrefix(view, ORG_VIEW_PARAM, ORG_VIEW_VALUE);
            SetTextParamByPrefix(view, ORG_CONSTR_PARAM, ORG_CONSTR_VALUE);
        }

        /// <summary>Ставит текстовый параметр, имя которого начинается с prefix.</summary>
        private static void SetTextParamByPrefix(Element el, string prefix, string value)
        {
            try
            {
                foreach (Parameter p in el.Parameters)
                {
                    if (p.IsReadOnly || p.StorageType != StorageType.String) continue;
                    string name = p.Definition?.Name ?? "";
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Set(value);
                    return;
                }
            }
            catch { }
        }

        /// <summary>Миллиметры во внутренние единицы.</summary>
        private static double Mm(double mm) =>
            UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

        /// <summary>Внутренние единицы в целые миллиметры (для отчёта).</summary>
        private static int MmBack(double v) =>
            (int)Math.Round(UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Millimeters));

        /// <summary>Проекция вектора на плоскость плана (Z = 0).</summary>
        private static XYZ Flat(XYZ v) => new XYZ(v.X, v.Y, 0);

        /// <summary>Имя семейства экземпляра содержит подстроку (без учёта регистра).</summary>
        private static bool FamilyContains(FamilyInstance fi, string sub) =>
            (fi.Symbol?.FamilyName ?? "").IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Значение «Маркировка типоразмера» окна (экземпляр или тип).</summary>
        private static string MarkOf(FamilyInstance fi)
        {
            Parameter p = fi.LookupParameter(MARK_PARAM) ?? fi.Symbol?.LookupParameter(MARK_PARAM);
            if (p == null || !p.HasValue) return "";
            string s = p.AsString();
            if (string.IsNullOrEmpty(s)) s = p.AsValueString();
            return (s ?? "").Trim();
        }

        /// <summary>Шаблон вида по точному имени; null, если нет.</summary>
        private static View FindTemplate(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && v.Name == name);

        /// <summary>Линейный тип размера по имени; null, если нет.</summary>
        private static DimensionType FindDimType(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .FirstOrDefault(d => d.StyleType == DimensionStyleType.Linear && d.Name == name);

        /// <summary>Тип вида «Фасад» по имени; иначе — первый попавшийся тип фасада.</summary>
        private static ViewFamilyType FindElevationType(Document doc, string name, out bool exact)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.Elevation)
                .ToList();
            ViewFamilyType byName = types.FirstOrDefault(t => t.Name == name);
            exact = byName != null;
            return byName ?? types.FirstOrDefault();
        }
    }
}
