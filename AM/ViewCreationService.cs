using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace TNovUtilsAR
{
    public sealed class ViewCreationService
    {
        public const string ApartmentParamName = "T_Номер продаваемого помещения";
        public const string ApartmentParamNameCyrillic = "Т_Номер продаваемого помещения";
        private static readonly string[] ApartmentParamNames = { ApartmentParamName, ApartmentParamNameCyrillic };

        private const double CropPadFeet = 1.5;
        private const double LevelMatchToleranceFeet = 0.5;
        private const double RoomBoundaryInflateFeet = 0.3;
        private const double MinAirPadFeet = 0.5;

        // Шаги подбора inflate: начинаем с базового 0.3 ft, при необходимости раздуваем
        // сильнее, пока квартира (тело + лоджия) не склеится в один связный компонент.
        private static readonly double[] InflateStepsFeet = { 0.3, 0.5, 0.75, 1.0, 1.3, 1.6 };

        private readonly Document _doc;

        public ViewCreationService(Document doc) => _doc = doc;

        public ViewCreationResult Run(ViewCreationOptions options)
        {
            if (options.Level == null) throw new ArgumentException("Не выбран уровень.");
            if (string.IsNullOrWhiteSpace(options.FloorLabel)) throw new ArgumentException("Не указан номер этажа.");
            if (options.ViewSets == null || options.ViewSets.Count == 0)
                throw new ArgumentException("Не задан ни один набор видов.");
            foreach (var set in options.ViewSets)
            {
                if (string.IsNullOrWhiteSpace(set.Token))
                    throw new ArgumentException("У набора видов не задан токен (АМ/ПСО).");
                if (string.IsNullOrWhiteSpace(set.PsTemplatePrefix))
                    throw new ArgumentException($"Не задан префикс шаблона ПС для набора '{set.Token}'.");
            }

            var source = ResolveRoomSource(options);
            var rooms = CollectRoomsOnLevel(source);
            var apartments = GroupByApartment(rooms, out var stats);
            if (apartments.Count == 0)
            {
                var paramVariants = string.Join(" / ", ApartmentParamNames);
                throw new InvalidOperationException(
                    $"В источнике '{source.DisplayName}' на уровне '{source.MatchedLevelName}' " +
                    $"не удалось собрать ни одной квартиры по параметру {paramVariants}.\n" +
                    $"Всего комнат на уровне: {stats.Total}; " +
                    $"с заполненным параметром: {stats.WithValue}; " +
                    $"с непарсящимся значением: {stats.UnparseableValues.Count}." +
                    (stats.UnparseableValues.Count > 0
                        ? "\nПримеры значений, которые не удалось распознать: " +
                          string.Join(", ", stats.UnparseableValues.Take(5).Select(v => "'" + v + "'"))
                        : string.Empty));
            }

            var sections = apartments.Keys.Select(k => k.Section).Distinct().ToList();
            if (sections.Count > 1)
                throw new InvalidOperationException($"На уровне найдено несколько секций: {string.Join(", ", sections)}. Ожидалась одна.");

            var sectionStr = apartments.Keys.First().SectionPadded;
            var floor = options.FloorLabel.Trim();
            var floorPlanType = GetFloorPlanType();

            // Имена строятся для каждого набора (АМ/ПСО) — токен меняет только средний сегмент.
            string GeneralName(string token) => $"Р_{sectionStr}_Этаж {floor}_{token}";
            string PkName(ApartmentNumber a, string token) => $"Р_{sectionStr}_Этаж {floor}_{token}_ПК_{a.Raw}";
            string PsName(ApartmentNumber a, string token) => $"Р_{sectionStr}_Этаж {floor}_{token}_ПС_{a.Raw}";

            var plannedNames = new List<string>();
            foreach (var set in options.ViewSets)
            {
                plannedNames.Add(GeneralName(set.Token));
                plannedNames.AddRange(apartments.Keys.Select(a => PkName(a, set.Token)));
                plannedNames.AddRange(apartments.Keys.Select(a => PsName(a, set.Token)));
            }
            EnsureNoNameConflicts(plannedNames);

            // ПС-шаблоны ищем для каждого набора по его префиксу; недостающие собираем все сразу.
            var psLookups = new Dictionary<string, IDictionary<string, View>>();
            var missingPs = new List<string>();
            foreach (var set in options.ViewSets)
            {
                var lookup = BuildPsTemplateLookup(set.PsTemplatePrefix);
                psLookups[set.Token] = lookup;
                missingPs.AddRange(apartments.Keys
                    .Where(a => !lookup.ContainsKey(a.Apartment))
                    .Select(a => set.PsTemplatePrefix + a.Apartment));
            }
            missingPs = missingPs.Distinct().ToList();
            if (missingPs.Any())
                throw new InvalidOperationException("Не найдены шаблоны ПС:\n" + string.Join("\n", missingPs));

            // Контур обрезки ПК — геометрия, общая для всех наборов. Считаем по разу на квартиру.
            var crops = new Dictionary<ApartmentNumber, ApartmentCrop>();
            var cropDiagnostics = new Dictionary<string, CropDiagnostics>();
            foreach (var pair in apartments)
            {
                crops[pair.Key] = ComputeApartmentCrop(pair.Value, source.Transform, options.Level.Elevation, out var cropDiag);
                cropDiagnostics[pair.Key.Raw] = cropDiag;
            }

            var result = new ViewCreationResult();

            using (var tx = new Transaction(_doc, "AM_PSO: Создание видов квартир"))
            {
                tx.Start();

                foreach (var set in options.ViewSets)
                {
                    var psLookup = psLookups[set.Token];

                    var general = ViewPlan.Create(_doc, floorPlanType.Id, options.Level.Id);
                    general.Name = GeneralName(set.Token);
                    ApplyTemplate(general, set.GeneralTemplateId);
                    result.AddGeneral(set.Token, general);

                    foreach (var pair in apartments)
                    {
                        var apt = pair.Key;

                        var pk = ViewPlan.Create(_doc, floorPlanType.Id, options.Level.Id);
                        pk.Name = PkName(apt, set.Token);
                        ApplyTemplate(pk, set.PkTemplateId);
                        ApplyApartmentCrop(pk, crops[apt]);
                        result.Pk.Add(pk);

                        var ps = ViewPlan.Create(_doc, floorPlanType.Id, options.Level.Id);
                        ps.Name = PsName(apt, set.Token);
                        ApplyTemplate(ps, psLookup[apt.Apartment].Id);
                        result.Ps.Add(ps);
                    }
                }

                tx.Commit();
            }

            result.Diagnostics = BuildDiagnostics(source, stats, apartments, cropDiagnostics);
            return result;
        }

        private static string BuildDiagnostics(
            RoomSourceContext source,
            ApartmentGroupingStats stats,
            IDictionary<ApartmentNumber, IList<Room>> apartments,
            IDictionary<string, CropDiagnostics> cropDiag)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Источник: {source.DisplayName}");
            sb.AppendLine($"Уровень источника: {source.MatchedLevelName}");
            sb.AppendLine();
            sb.AppendLine($"Всего комнат на уровне: {stats.Total}");
            sb.AppendLine($"С заполненным параметром: {stats.WithValue}");
            sb.AppendLine($"С непарсящимся значением: {stats.UnparseableValues.Count}");
            if (stats.UnparseableValues.Count > 0)
            {
                sb.AppendLine("  Примеры: " + string.Join(", ",
                    stats.UnparseableValues.Take(10).Select(v => "'" + v + "'")));
            }
            sb.AppendLine();
            sb.AppendLine("Группы квартир (T_Номер → комнат / обрезка):");
            foreach (var pair in apartments)
            {
                cropDiag.TryGetValue(pair.Key.Raw, out var d);
                var cropPart = d != null
                    ? $"extr {d.ExtrusionsCreated}/{d.RoomsConsidered}, union ok {d.UnionSuccesses} fail {d.UnionFailures}, inflate {d.InflateUsedMm:F0} мм, comp {d.Components}{(d.BboxFallback ? " (bbox)" : "")}, shape {(d.CropShapeApplied ? "ok" : "skip")}, loop {d.LoopWidthMm:F0}x{d.LoopHeightMm:F0} мм"
                    : "n/a";
                sb.AppendLine($"  {pair.Key.Raw}: {pair.Value.Count} комн. — {cropPart}");
                foreach (var room in pair.Value)
                {
                    var name = room.LookupParameter("Имя")?.AsString() ?? room.Name;
                    var num = room.Number ?? "?";
                    var areaM2 = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);
                    sb.AppendLine($"     - [{num}] {name}, {areaM2:F1} м²");
                }
            }
            return sb.ToString();
        }

        private RoomSourceContext ResolveRoomSource(ViewCreationOptions options)
        {
            if (options.LinkInstance == null)
            {
                return new RoomSourceContext
                {
                    DisplayName = "Главный документ",
                    Doc = _doc,
                    Transform = Transform.Identity,
                    MatchedLevel = options.Level,
                    MatchedLevelName = options.Level.Name
                };
            }

            var linkDoc = options.LinkInstance.GetLinkDocument();
            if (linkDoc == null)
                throw new InvalidOperationException($"Связанная модель '{options.LinkInstance.Name}' не загружена.");

            var matched = MatchLevelInLink(linkDoc, options.Level);
            return new RoomSourceContext
            {
                DisplayName = options.LinkInstance.Name,
                Doc = linkDoc,
                Transform = options.LinkInstance.GetTotalTransform(),
                MatchedLevel = matched,
                MatchedLevelName = matched.Name
            };
        }

        private static Level MatchLevelInLink(Document linkedDoc, Level hostLevel)
        {
            var linkedLevels = new FilteredElementCollector(linkedDoc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();
            if (!linkedLevels.Any())
                throw new InvalidOperationException("В связанной модели нет уровней.");

            var byName = linkedLevels.FirstOrDefault(l => string.Equals(l.Name, hostLevel.Name, StringComparison.Ordinal));
            if (byName != null) return byName;

            var byElev = linkedLevels
                .Select(l => new { Level = l, Diff = Math.Abs(l.Elevation - hostLevel.Elevation) })
                .OrderBy(p => p.Diff)
                .First();
            if (byElev.Diff <= LevelMatchToleranceFeet) return byElev.Level;

            throw new InvalidOperationException(
                $"В связанной модели не найден уровень, соответствующий '{hostLevel.Name}': " +
                $"ни по имени, ни по отметке (ближайший '{byElev.Level.Name}', Δ {byElev.Diff * 304.8:F0} мм).");
        }

        private static IList<Room> CollectRoomsOnLevel(RoomSourceContext source)
        {
            // Раньше фильтровали по r.LevelId == source.LevelId — пропускали комнаты, у которых
            // базовый уровень это другой уровень того же этажа (например «Чистый пол 03» при matched
            // «03 ... Этаж 3»). Теперь берём по геометрии: комната считается на этаже, если её bbox
            // по Z захватывает «середину» этажа между нашим уровнем и следующим выше.
            var baseElev = source.MatchedLevel.Elevation;
            var nextLevelElev = new FilteredElementCollector(source.Doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Where(l => l.Elevation > baseElev + 0.5)
                .OrderBy(l => l.Elevation)
                .FirstOrDefault()?.Elevation ?? (baseElev + 100.0);
            var floorHeight = nextLevelElev - baseElev;
            var probeZ = baseElev + Math.Min(floorHeight / 2.0, 4.0);

            return new FilteredElementCollector(source.Doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r.Area > 0 && RoomCoversZ(r, probeZ))
                .ToList();
        }

        private static bool RoomCoversZ(Room room, double z)
        {
            var bb = room.get_BoundingBox(null);
            if (bb == null) return false;
            const double tol = 0.05;
            return bb.Min.Z - tol <= z && z <= bb.Max.Z + tol;
        }

        private static IDictionary<ApartmentNumber, IList<Room>> GroupByApartment(
            IEnumerable<Room> rooms, out ApartmentGroupingStats stats)
        {
            stats = new ApartmentGroupingStats();
            var grouped = new SortedDictionary<ApartmentNumber, IList<Room>>(ApartmentSortComparer.Instance);
            foreach (var room in rooms)
            {
                stats.Total++;
                var raw = ReadApartmentRaw(room);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                stats.WithValue++;

                ApartmentNumber apt;
                try { apt = ApartmentNumber.Parse(raw); }
                catch
                {
                    stats.UnparseableValues.Add(raw);
                    continue;
                }

                if (!grouped.TryGetValue(apt, out var list))
                {
                    list = new List<Room>();
                    grouped[apt] = list;
                }
                list.Add(room);
            }
            return grouped;
        }

        private static string ReadApartmentRaw(Room room)
        {
            foreach (var name in ApartmentParamNames)
            {
                var value = room.LookupParameter(name)?.AsString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private ViewFamilyType GetFloorPlanType()
        {
            var type = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
            if (type == null) throw new InvalidOperationException("В проекте нет типа вида FloorPlan.");
            return type;
        }

        private void EnsureNoNameConflicts(IEnumerable<string> plannedNames)
        {
            var existing = new HashSet<string>(
                new FilteredElementCollector(_doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .Select(v => v.Name));

            var conflicts = plannedNames.Where(existing.Contains).ToList();
            if (conflicts.Any())
                throw new InvalidOperationException("Виды с такими именами уже существуют:\n" + string.Join("\n", conflicts));
        }

        private static void ApplyTemplate(View view, ElementId templateId)
        {
            if (templateId == null || templateId == ElementId.InvalidElementId) return;
            view.ViewTemplateId = templateId;
        }

        /// <summary>
        /// Считает контур обрезки ПК по солид-контуру квартиры. Не привязан к виду —
        /// результат применяется к каждому виду набора через <see cref="ApplyApartmentCrop"/>.
        /// </summary>
        private ApartmentCrop ComputeApartmentCrop(IEnumerable<Room> rooms, Transform sourceTransform, double levelElev, out CropDiagnostics diag)
        {
            diag = new CropDiagnostics();
            var crop = new ApartmentCrop();
            var bopt = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Center
            };

            // Базовые петли комнат по центру стен — считаем один раз, переиспользуем на каждом шаге inflate.
            var baseLoops = new List<CurveLoop>();
            foreach (var room in rooms)
            {
                diag.RoomsConsidered++;
                var loops = SafeGetBoundaryLoops(room, bopt);
                if (loops.Count == 0) continue;

                var outerLoop = loops.OrderByDescending(l => Math.Abs(SignedAreaXY(l))).First();
                if (SignedAreaXY(outerLoop) < 0) outerLoop = ReverseLoop(outerLoop);
                baseLoops.Add(outerLoop);
            }
            if (baseLoops.Count == 0) return crop;

            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, levelElev));
            var totalOutset = RoomBoundaryInflateFeet + CropPadFeet;

            // Подбираем минимальный inflate, при котором квартира — один связный компонент.
            // Раздуваем только комнаты этой квартиры (соседские не трогаем вообще), поэтому
            // больший inflate не затянет соседей — он лишь дотягивает тело до лоджии, оторванной
            // наружной стеной. Внешний воздушный отступ ниже компенсирует использованный inflate.
            CurveLoop outer = null;
            var usedInflate = InflateStepsFeet[0];
            List<CurveLoop> lastComponentLoops = null;

            foreach (var inflate in InflateStepsFeet)
            {
                var componentLoops = BuildComponentLoops(baseLoops, sourceTransform, plane, inflate, diag);
                if (componentLoops.Count == 0) continue;
                usedInflate = inflate;
                lastComponentLoops = componentLoops;
                if (componentLoops.Count == 1)
                {
                    outer = componentLoops[0];
                    break;
                }
            }
            diag.InflateUsedMm = usedInflate * 304.8;

            if (outer == null)
            {
                // Даже при максимальном inflate квартира не склеилась (очень толстый разрыв) —
                // последний рубеж: bbox всех кусков, чтобы хотя бы не потерять лоджию.
                if (lastComponentLoops == null || lastComponentLoops.Count == 0) return crop;
                diag.Components = lastComponentLoops.Count;
                diag.ProjectionLoops = lastComponentLoops.Count;
                outer = BuildBboxLoop(lastComponentLoops);
                diag.BboxFallback = true;
            }
            else
            {
                diag.Components = 1;
                diag.ProjectionLoops = 1;
                if (SignedAreaXY(outer) < 0) outer = ReverseLoop(outer);
            }

            // Отступ компенсирует израсходованный inflate, чтобы внешняя граница оставалась
            // на постоянной дистанции от центра стен независимо от того, сколько пришлось раздуть.
            var pad = Math.Max(MinAirPadFeet, totalOutset - usedInflate);
            CurveLoop cropLoop;
            try { cropLoop = CurveLoop.CreateViaOffset(outer, pad, XYZ.BasisZ); }
            catch { cropLoop = outer; }

            // Считаем bbox получившегося crop loop'а и выставляем CropBox по нему,
            // иначе унаследованный от шаблона маленький cropbox обрежет нашу форму.
            double bbMinX = double.PositiveInfinity, bbMinY = double.PositiveInfinity;
            double bbMaxX = double.NegativeInfinity, bbMaxY = double.NegativeInfinity;
            foreach (var c in cropLoop)
            {
                var p = c.GetEndPoint(0);
                if (p.X < bbMinX) bbMinX = p.X;
                if (p.Y < bbMinY) bbMinY = p.Y;
                if (p.X > bbMaxX) bbMaxX = p.X;
                if (p.Y > bbMaxY) bbMaxY = p.Y;
            }
            diag.LoopWidthMm = (bbMaxX - bbMinX) * 304.8;
            diag.LoopHeightMm = (bbMaxY - bbMinY) * 304.8;

            crop.CropLoop = cropLoop;
            crop.FallbackLoop = outer;
            crop.MinX = bbMinX;
            crop.MinY = bbMinY;
            crop.MaxX = bbMaxX;
            crop.MaxY = bbMaxY;
            crop.HasCrop = true;
            diag.CropShapeApplied = true;
            return crop;
        }

        /// <summary>
        /// Применяет рассчитанный контур обрезки к виду. Перед SetCropShape обязательно
        /// выставляем CropBox по bbox контура — иначе унаследованный от шаблона маленький
        /// cropbox клипает форму.
        /// </summary>
        private static void ApplyApartmentCrop(ViewPlan view, ApartmentCrop crop)
        {
            if (crop == null || !crop.HasCrop) return;

            view.CropBoxActive = true;
            var currentBox = view.CropBox;
            var newBox = new BoundingBoxXYZ
            {
                Transform = currentBox.Transform,
                Min = new XYZ(crop.MinX, crop.MinY, currentBox.Min.Z),
                Max = new XYZ(crop.MaxX, crop.MaxY, currentBox.Max.Z),
            };
            try { view.CropBox = newBox; } catch { /* keep default */ }

            try
            {
                view.GetCropRegionShapeManager().SetCropShape(crop.CropLoop);
            }
            catch
            {
                try { view.GetCropRegionShapeManager().SetCropShape(crop.FallbackLoop); }
                catch { /* отдадим вид без shape — лучше, чем сломать всю транзакцию */ }
            }
        }

        /// <summary>
        /// Раздувает базовые петли комнат на <paramref name="inflate"/>, экструдирует, объединяет
        /// и разбивает union на связные компоненты. Возвращает внешнюю петлю каждого компонента
        /// (одна петля = квартира склеилась целиком; больше одной = тело + оторванная лоджия).
        /// </summary>
        private static List<CurveLoop> BuildComponentLoops(
            IEnumerable<CurveLoop> baseLoops, Transform sourceTransform, Plane plane, double inflate, CropDiagnostics diag)
        {
            var extrusions = new List<Solid>();
            foreach (var baseLoop in baseLoops)
            {
                var loop = baseLoop;
                try { loop = CurveLoop.CreateViaOffset(baseLoop, inflate, XYZ.BasisZ); }
                catch { /* offset не сошёлся (тонкая/вогнутая комната) — берём исходную петлю */ }

                Solid extr;
                try
                {
                    extr = GeometryCreationUtilities.CreateExtrusionGeometry(
                        new[] { loop }, XYZ.BasisZ, 1.0);
                }
                catch { continue; }

                if (sourceTransform != null && !sourceTransform.IsIdentity)
                {
                    try { extr = SolidUtils.CreateTransformed(extr, sourceTransform); }
                    catch { continue; }
                }
                extrusions.Add(extr);
            }
            diag.ExtrusionsCreated = extrusions.Count;
            if (extrusions.Count == 0) return new List<CurveLoop>();

            var union = extrusions[0];
            diag.UnionSuccesses = 0;
            diag.UnionFailures = 0;
            for (var i = 1; i < extrusions.Count; i++)
            {
                try
                {
                    union = BooleanOperationsUtils.ExecuteBooleanOperation(
                        union, extrusions[i], BooleanOperationsType.Union);
                    diag.UnionSuccesses++;
                }
                catch
                {
                    diag.UnionFailures++;
                }
            }
            if (union == null || union.Volume <= 0) return new List<CurveLoop>();

            IList<Solid> components;
            try { components = SolidUtils.SplitVolumes(union); }
            catch { components = null; }
            if (components == null || components.Count == 0)
                components = new List<Solid> { union };

            var loops = new List<CurveLoop>();
            foreach (var comp in components)
            {
                var lp = GetOuterProjectionLoop(comp, plane);
                if (lp != null) loops.Add(lp);
            }
            return loops;
        }

        /// <summary>
        /// Проецирует один связный солид на план и возвращает его наибольшую внешнюю петлю
        /// (внутренние петли-дырки игнорируем).
        /// </summary>
        private static CurveLoop GetOuterProjectionLoop(Solid solid, Plane plane)
        {
            PlanarFace face;
            try
            {
                var analyzer = ExtrusionAnalyzer.Create(solid, plane, XYZ.BasisZ);
                face = analyzer.GetExtrusionBase() as PlanarFace;
            }
            catch
            {
                return null;
            }
            if (face == null) return null;

            CurveLoop best = null;
            var bestArea = double.NegativeInfinity;
            foreach (EdgeArray ea in face.EdgeLoops)
            {
                var cl = new CurveLoop();
                foreach (Edge e in ea) cl.Append(e.AsCurveFollowingFace(face));
                var area = Math.Abs(SignedAreaXY(cl));
                if (area > bestArea) { bestArea = area; best = cl; }
            }
            if (best != null && SignedAreaXY(best) < 0) best = ReverseLoop(best);
            return best;
        }

        private static List<CurveLoop> SafeGetBoundaryLoops(Room room, SpatialElementBoundaryOptions opt)
        {
            var result = new List<CurveLoop>();
            IList<IList<BoundarySegment>> segs;
            try { segs = room.GetBoundarySegments(opt); }
            catch { return result; }
            if (segs == null) return result;

            foreach (var loop in segs)
            {
                if (loop == null || loop.Count == 0) continue;
                var cl = new CurveLoop();
                var ok = true;
                foreach (var seg in loop)
                {
                    try { cl.Append(seg.GetCurve()); }
                    catch { ok = false; break; }
                }
                if (ok) result.Add(cl);
            }
            return result;
        }

        private static double SignedAreaXY(CurveLoop loop)
        {
            double a = 0;
            foreach (var c in loop)
            {
                var p = c.GetEndPoint(0);
                var q = c.GetEndPoint(1);
                a += p.X * q.Y - q.X * p.Y;
            }
            return a * 0.5;
        }

        private static CurveLoop ReverseLoop(CurveLoop loop)
        {
            var curves = loop.ToList();
            var reversed = new CurveLoop();
            for (var i = curves.Count - 1; i >= 0; i--)
                reversed.Append(curves[i].CreateReversed());
            return reversed;
        }

        private static CurveLoop BuildBboxLoop(IEnumerable<CurveLoop> loops)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            double z = 0;
            foreach (var loop in loops)
                foreach (var c in loop)
                {
                    var p = c.GetEndPoint(0);
                    z = p.Z;
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
            var p1 = new XYZ(minX, minY, z);
            var p2 = new XYZ(maxX, minY, z);
            var p3 = new XYZ(maxX, maxY, z);
            var p4 = new XYZ(minX, maxY, z);
            var bbox = new CurveLoop();
            bbox.Append(Line.CreateBound(p1, p2));
            bbox.Append(Line.CreateBound(p2, p3));
            bbox.Append(Line.CreateBound(p3, p4));
            bbox.Append(Line.CreateBound(p4, p1));
            return bbox;
        }

        private IDictionary<string, View> BuildPsTemplateLookup(string prefix)
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.Name.StartsWith(prefix))
                .ToDictionary(v => v.Name.Substring(prefix.Length), v => v);
        }

        private sealed class ApartmentGroupingStats
        {
            public int Total { get; set; }
            public int WithValue { get; set; }
            public List<string> UnparseableValues { get; } = new List<string>();
        }

        private sealed class RoomSourceContext
        {
            public string DisplayName { get; set; }
            public Document Doc { get; set; }
            public Transform Transform { get; set; }
            public Level MatchedLevel { get; set; }
            public string MatchedLevelName { get; set; }
        }

        private sealed class ApartmentSortComparer : IComparer<ApartmentNumber>
        {
            public static readonly ApartmentSortComparer Instance = new ApartmentSortComparer();

            public int Compare(ApartmentNumber x, ApartmentNumber y)
            {
                if (x == null && y == null) return 0;
                if (x == null) return -1;
                if (y == null) return 1;
                var s = x.Section.CompareTo(y.Section);
                if (s != 0) return s;
                var f = string.CompareOrdinal(x.Floor, y.Floor);
                if (f != 0) return f;
                if (int.TryParse(x.Apartment, out var xa) && int.TryParse(y.Apartment, out var ya))
                    return xa.CompareTo(ya);
                return string.CompareOrdinal(x.Apartment, y.Apartment);
            }
        }
    }

    public sealed class ViewCreationResult
    {
        public IDictionary<string, View> Generals { get; } = new Dictionary<string, View>();
        public IList<View> Pk { get; } = new List<View>();
        public IList<View> Ps { get; } = new List<View>();
        public string Diagnostics { get; set; }

        public void AddGeneral(string token, View view) => Generals[token] = view;
    }

    internal sealed class ApartmentCrop
    {
        public bool HasCrop { get; set; }
        public CurveLoop CropLoop { get; set; }
        public CurveLoop FallbackLoop { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }

    internal sealed class CropDiagnostics
    {
        public int RoomsConsidered { get; set; }
        public int ExtrusionsCreated { get; set; }
        public int UnionSuccesses { get; set; }
        public int UnionFailures { get; set; }
        public int Components { get; set; }
        public int ProjectionLoops { get; set; }
        public bool BboxFallback { get; set; }
        public bool CropShapeApplied { get; set; }
        public double InflateUsedMm { get; set; }
        public double LoopWidthMm { get; set; }
        public double LoopHeightMm { get; set; }
    }
}
