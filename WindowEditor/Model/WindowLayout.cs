using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowEditor.Model
{
    /// <summary>Прямоугольник в мм. Y вверх.</summary>
    public struct Rect
    {
        public double X, Y, W, H;
        public Rect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
        public double Right => X + W;
        public double Top => Y + H;
        public double Area => W * H;
        public Rect Inset(double d) => new Rect(X + d, Y + d, Math.Max(0, W - 2 * d), Math.Max(0, H - 2 * d));
        public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Top;
        public bool ContainsStrict(double x, double y) => x > X + 1e-6 && x < Right - 1e-6 && y > Y + 1e-6 && y < Top - 1e-6;
    }

    /// <summary>Единица рамы: глухое поле, створка целиком или «стена».</summary>
    public class UnitInfo
    {
        public Cell Cell;
        public Rect R;
    }

    /// <summary>Поле заполнения (стекло): глухое поле, створка без деления или поле внутри створки.</summary>
    public class FieldInfo
    {
        public Cell Cell;
        public Rect R;      // видимая часть заполнения
        public Cell Unit;   // единица рамы, к которой относится поле
    }

    public class MullionInfo
    {
        public Cell Parent;     // ячейка, которую делит импост
        public int Index;       // импост между Children[Index] и Children[Index+1]
        public Rect R;
        public bool InSash;     // импост створки, а не рамы
        public bool Vertical => Parent.Split == SplitKind.Columns;
        /// <summary>Ось импоста: X (вертикальный) или Z (горизонтальный), мм в координатах блока.</summary>
        public double Axis => Vertical ? R.X + R.W / 2 : R.Y + R.H / 2;
    }

    /// <summary>Раскладка одной секции (рамы). Пересчитывается после каждой правки.</summary>
    public class WindowLayout
    {
        public Rect Outer;
        public Rect Inner;
        public readonly Section Section;
        public readonly List<UnitInfo> Units = new List<UnitInfo>();
        public readonly List<FieldInfo> Fields = new List<FieldInfo>();
        public readonly List<MullionInfo> Mullions = new List<MullionInfo>();
        /// <summary>Контуры сплошной части рамы (рама + импосты): внешние и отверстия.</summary>
        public List<List<(double x, double z)>> FrameLoops = new List<List<(double x, double z)>>();
        /// <summary>Силуэт рамы вертикальными полосами (без «стен»).</summary>
        public List<Rect> Silhouette = new List<Rect>();

        readonly Dictionary<Cell, Rect> _rects = new Dictionary<Cell, Rect>();
        readonly Dictionary<Cell, Cell> _parents = new Dictionary<Cell, Cell>();
        readonly Dictionary<Cell, Cell> _unitOf = new Dictionary<Cell, Cell>();
        readonly WindowConfig _cfg;

        public const double MinField = 100;

        public WindowLayout(WindowConfig cfg, Section section, Rect outer)
        {
            _cfg = cfg;
            Section = section;
            Outer = outer;
            Inner = Outer.Inset(cfg.FrameWidth);
            Visit(section.Root, Inner, null);
            BuildFrameRegion();
        }

        public Rect RectOf(Cell c) => _rects[c];
        public Cell ParentOf(Cell c) => _parents.TryGetValue(c, out var p) ? p : null;
        public Cell UnitOf(Cell c) => _unitOf.TryGetValue(c, out var u) ? u : null;
        public bool Owns(Cell c) => _rects.ContainsKey(c);

        // ---------------- Раскладка ----------------

        void Visit(Cell cell, Rect r, Cell sash)
        {
            _rects[cell] = r;
            if (sash == null)
            {
                if (cell.IsUnit)
                {
                    Units.Add(new UnitInfo { Cell = cell, R = r });
                    _unitOf[cell] = cell;
                    if (cell.IsWall) return;
                    if (!cell.IsSash) { Fields.Add(new FieldInfo { Cell = cell, R = r, Unit = cell }); return; }
                    var inner = r.Inset(_cfg.SashWidth);
                    if (cell.IsLeaf) Fields.Add(new FieldInfo { Cell = cell, R = inner, Unit = cell });
                    else VisitSplit(cell, inner, cell);
                    return;
                }
                VisitSplit(cell, r, null);
            }
            else
            {
                _unitOf[cell] = sash;
                if (cell.IsLeaf) { Fields.Add(new FieldInfo { Cell = cell, R = r, Unit = sash }); return; }
                VisitSplit(cell, r, sash);
            }
        }

        void VisitSplit(Cell cell, Rect r, Cell sash)
        {
            var kids = cell.Children;
            int n = kids.Count;
            bool cols = cell.Split == SplitKind.Columns;
            double m = sash == null ? _cfg.MullionWidth : _cfg.SashMullionWidth;
            double min = sash == null ? WindowConfig.MinCell : MinField;
            double avail = (cols ? r.W : r.H) - (n - 1) * m;

            // Самовосстановление: если размеры не помещаются, делим поровну.
            double fixedSum = kids.Take(n - 1).Sum(k => k.Size);
            if (kids.Take(n - 1).Any(k => k.Size < min) || avail - fixedSum < min)
                for (int i = 0; i < n - 1; i++) kids[i].Size = Math.Round(avail / n);

            double pos = cols ? r.X : r.Y, used = 0;
            for (int i = 0; i < n; i++)
            {
                double s = i < n - 1 ? kids[i].Size : avail - used;
                var rc = cols ? new Rect(pos, r.Y, s, r.H) : new Rect(r.X, pos, r.W, s);
                _parents[kids[i]] = cell;
                Visit(kids[i], rc, sash);
                pos += s; used += s;
                if (i < n - 1)
                {
                    var mr = cols ? new Rect(pos, r.Y, m, r.H) : new Rect(r.X, pos, r.W, m);
                    Mullions.Add(new MullionInfo { Parent = cell, Index = i, R = mr, InSash = sash != null });
                    pos += m;
                }
            }
        }

        // ---------------- Контур рамы с учётом «стен» ----------------

        /// <summary>Прямоугольник, который «стена» вырезает из рамы: сама ячейка + рама до наружного края; импост рядом ужимается до ширины рамы.</summary>
        Rect WallCut(Rect r)
        {
            const double eps = 1e-6;
            double f = _cfg.FrameWidth, extra = Math.Max(0, _cfg.MullionWidth - _cfg.FrameWidth);
            double x0 = Math.Abs(r.X - Inner.X) < eps ? Outer.X : r.X - extra;
            double x1 = Math.Abs(r.Right - Inner.Right) < eps ? Outer.Right : r.Right + extra;
            double z0 = Math.Abs(r.Y - Inner.Y) < eps ? Outer.Y : r.Y - extra;
            double z1 = Math.Abs(r.Top - Inner.Top) < eps ? Outer.Top : r.Top + extra;
            return new Rect(x0, z0, x1 - x0, z1 - z0);
        }

        void BuildFrameRegion()
        {
            var cuts = Units.Where(u => u.Cell.IsWall).Select(u => WallCut(u.R)).ToList();
            var solids = Units.Where(u => !u.Cell.IsWall).Select(u => u.R).ToList();

            var xs = new SortedSet<double> { Outer.X, Outer.Right };
            var zs = new SortedSet<double> { Outer.Y, Outer.Top };
            foreach (var r in cuts.Concat(solids)) { xs.Add(r.X); xs.Add(r.Right); zs.Add(r.Y); zs.Add(r.Top); }
            var X = xs.Where(v => v >= Outer.X - 1e-6 && v <= Outer.Right + 1e-6).ToList();
            var Z = zs.Where(v => v >= Outer.Y - 1e-6 && v <= Outer.Top + 1e-6).ToList();
            int nx = X.Count - 1, nz = Z.Count - 1;

            var inside = new bool[nx, nz]; // в силуэте рамы
            var frame = new bool[nx, nz];  // сплошная рама
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                {
                    double cx = (X[i] + X[i + 1]) / 2, cz = (Z[j] + Z[j + 1]) / 2;
                    inside[i, j] = !cuts.Any(c => c.ContainsStrict(cx, cz));
                    frame[i, j] = inside[i, j] && !solids.Any(s => s.ContainsStrict(cx, cz));
                }

            // силуэт: по каждой вертикальной полосе — от нижней до верхней ячейки внутри
            for (int i = 0; i < nx; i++)
            {
                int lo = -1, hi = -1;
                for (int j = 0; j < nz; j++) if (inside[i, j]) { if (lo < 0) lo = j; hi = j; }
                if (lo < 0) continue;
                var r = new Rect(X[i], Z[lo], X[i + 1] - X[i], Z[hi + 1] - Z[lo]);
                var last = Silhouette.Count > 0 ? Silhouette[Silhouette.Count - 1] : (Rect?)null;
                if (last.HasValue && Math.Abs(last.Value.Right - r.X) < 1e-6 && Math.Abs(last.Value.Y - r.Y) < 1e-6 && Math.Abs(last.Value.Top - r.Top) < 1e-6)
                    Silhouette[Silhouette.Count - 1] = new Rect(last.Value.X, r.Y, r.Right - last.Value.X, r.H);
                else
                    Silhouette.Add(r);
            }

            FrameLoops = TraceLoops(frame, X, Z);
        }

        /// <summary>Границы области из клеток сетки → замкнутые контуры без лишних вершин.</summary>
        static List<List<(double x, double z)>> TraceLoops(bool[,] cells, List<double> X, List<double> Z)
        {
            int nx = cells.GetLength(0), nz = cells.GetLength(1);
            var edges = new HashSet<(int, int, int, int)>();
            void Add(int a, int b, int c, int d)
            {
                if (!edges.Remove((c, d, a, b))) edges.Add((a, b, c, d));
            }
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < nz; j++)
                    if (cells[i, j])
                    {
                        Add(i, j, i + 1, j); Add(i + 1, j, i + 1, j + 1);
                        Add(i + 1, j + 1, i, j + 1); Add(i, j + 1, i, j);
                    }

            var outgoing = new Dictionary<(int, int), List<(int, int)>>();
            foreach (var e in edges)
            {
                var k = (e.Item1, e.Item2);
                if (!outgoing.TryGetValue(k, out var l)) outgoing[k] = l = new List<(int, int)>();
                l.Add((e.Item3, e.Item4));
            }

            var loops = new List<List<(double x, double z)>>();
            while (outgoing.Count > 0)
            {
                var start = outgoing.Keys.First();
                var cur = start;
                (int, int) prevDir = (0, 0);
                var pts = new List<(int, int)>();
                while (true)
                {
                    pts.Add(cur);
                    var outs = outgoing[cur];
                    // в «щипке» (две исходящие) берём самый правый поворот — контуры не слипаются
                    var next = outs.Count == 1 ? outs[0] : outs.OrderBy(o => TurnRank(prevDir, (o.Item1 - cur.Item1, o.Item2 - cur.Item2))).First();
                    outs.Remove(next);
                    if (outs.Count == 0) outgoing.Remove(cur);
                    prevDir = (Math.Sign(next.Item1 - cur.Item1), Math.Sign(next.Item2 - cur.Item2));
                    cur = next;
                    if (cur.Equals(start)) break;
                    if (!outgoing.ContainsKey(cur)) break; // защитный выход
                }

                // в координаты и без точек на прямой
                var poly = pts.Select(p => (x: X[p.Item1], z: Z[p.Item2])).ToList();
                bool changed = true;
                while (changed && poly.Count > 3)
                {
                    changed = false;
                    for (int k = 0; k < poly.Count; k++)
                    {
                        var a = poly[(k - 1 + poly.Count) % poly.Count]; var b = poly[k]; var c = poly[(k + 1) % poly.Count];
                        if (Math.Abs((b.x - a.x) * (c.z - b.z) - (b.z - a.z) * (c.x - b.x)) < 1e-9) { poly.RemoveAt(k); changed = true; break; }
                    }
                }
                if (poly.Count >= 3) loops.Add(poly);
            }
            return loops;
        }

        static int TurnRank((int x, int z) inDir, (int x, int z) outDir)
        {
            outDir = (Math.Sign(outDir.x), Math.Sign(outDir.z));
            if (inDir == (0, 0)) return 0;
            int cross = inDir.x * outDir.z - inDir.z * outDir.x; // >0 — налево
            int dot = inDir.x * outDir.x + inDir.z * outDir.z;
            if (cross < 0) return 0;      // направо
            if (dot > 0) return 1;        // прямо
            if (cross > 0) return 2;      // налево
            return 3;                     // назад
        }

        double SizeAlong(Cell parent, Cell child)
        {
            var r = _rects[child];
            return parent.Split == SplitKind.Columns ? r.W : r.H;
        }

        bool InSash(Cell c) => UnitOf(c) is Cell u && u != c && u.IsSash;

        // ---------------- Операции редактора ----------------

        /// <summary>
        /// Добавить импост, поделив ячейку пополам.
        /// inSash=true — импост внутри створки (поле створки делится, створка остаётся одна).
        /// Для поля внутри створки импост всегда внутренний.
        /// </summary>
        public bool Split(Cell c, SplitKind kind, bool inSash, out string error)
        {
            error = null;
            bool insideSash = InSash(c);
            if (c.IsWall) { error = "Стену делить нельзя — смените заполнение."; return false; }
            if (inSash && !insideSash && !c.IsSash) { error = "Импост в створке ставится в створку. Выберите створку."; return false; }

            if (insideSash || inSash)
            {
                // делим поле створки (или саму створку на поля)
                var rect = insideSash ? _rects[c] : Fields.First(f => f.Cell == c).R;
                double m = _cfg.SashMullionWidth;
                double half = Math.Round(((kind == SplitKind.Columns ? rect.W : rect.H) - m) / 2);
                if (half < MinField) { error = $"Поле слишком мало: после деления {half:0} мм (минимум {MinField:0})."; return false; }
                if (!insideSash)
                {
                    // створка без полей → створка с двумя полями
                    c.Split = kind;
                    c.Children = new List<Cell>
                    {
                        new Cell { Size = half, GlassMaterial = c.GlassMaterial, Infill = c.Infill },
                        new Cell { GlassMaterial = c.GlassMaterial, Infill = c.Infill }
                    };
                    return true;
                }
                return SplitGeneric(c, kind, half, false);
            }

            // импост рамы
            var r = _rects[c];
            double hm = Math.Round(((kind == SplitKind.Columns ? r.W : r.H) - _cfg.MullionWidth) / 2);
            if (hm < WindowConfig.MinCell)
            {
                error = $"Ячейка слишком мала: после деления {hm:0} мм (минимум {WindowConfig.MinCell:0}).";
                return false;
            }
            return SplitGeneric(c, kind, hm, true);
        }

        bool SplitGeneric(Cell c, SplitKind kind, double half, bool frameLevel)
        {
            var parent = ParentOf(c);
            // у створки с полями «родитель-створка» — не узел рамы
            bool parentUsable = parent != null && parent.Split == kind && !(frameLevel && parent.IsSash);
            // рядом со створкой с полями ставится простая створка; поле наследует материал и вид заполнения
            Cell Twin() => new Cell { Fill = c.Fill, GlassMaterial = c.GlassMaterial, Infill = c.Infill };

            if (parentUsable)
            {
                int idx = parent.Children.IndexOf(c);
                bool wasLast = idx == parent.Children.Count - 1;
                var twin = Twin();
                twin.Size = wasLast ? 0 : half;
                c.Size = half;
                parent.Children.Insert(idx + 1, twin);
            }
            else
            {
                var a = new Cell { Size = half, Split = c.Split, Fill = c.Fill, GlassMaterial = c.GlassMaterial, Infill = c.Infill, Children = c.Children };
                var b = Twin();
                c.Split = kind;
                if (frameLevel) c.Fill = FillKind.Fixed; // узел рамы
                c.GlassMaterial = "";
                c.Children = new List<Cell> { a, b };
            }
            return true;
        }

        /// <summary>Удалить импост, объединив две соседние ячейки. Остаётся левая/нижняя.</summary>
        public void RemoveMullion(MullionInfo mi)
        {
            var p = mi.Parent;
            var a = p.Children[mi.Index];
            var b = p.Children[mi.Index + 1];
            double merged = SizeAlong(p, a) + SizeAlong(p, b) + (mi.InSash ? _cfg.SashMullionWidth : _cfg.MullionWidth);
            bool bWasLast = mi.Index + 1 == p.Children.Count - 1;
            if (!mi.InSash && a.IsUnit && b.IsSash && !a.IsSash && !a.IsWall) a.Fill = b.Fill; // створка «побеждает» глухое
            p.Children.RemoveAt(mi.Index + 1);
            a.Size = bWasLast ? 0 : merged;

            if (p.Children.Count == 1)
            {
                var only = p.Children[0];
                p.Split = only.Split;
                p.Children = only.Children;
                p.GlassMaterial = only.GlassMaterial;
                p.Infill = only.Infill;
                if (!p.IsSash || only.IsSash) p.Fill = only.Fill; // створка с полями остаётся створкой
            }
        }

        /// <summary>Сдвинуть импост на delta мм (вправо/вверх). Возвращает фактический сдвиг.</summary>
        public double MoveMullion(MullionInfo mi, double delta)
        {
            var p = mi.Parent;
            var a = p.Children[mi.Index];
            var b = p.Children[mi.Index + 1];
            double sa = SizeAlong(p, a), sb = SizeAlong(p, b);
            double min = mi.InSash ? MinField : WindowConfig.MinCell;
            delta = Math.Max(min - sa, Math.Min(sb - min, delta));
            a.Size = sa + delta;
            if (mi.Index + 1 < p.Children.Count - 1) b.Size = sb - delta;
            return delta;
        }

        /// <summary>Сменить заполнение единицы рамы.</summary>
        public void SetFill(Cell unit, FillKind f)
        {
            if (f == FillKind.Wall || f == FillKind.Fixed)
            {
                // у глухого поля и стены нет полей створки: створку с полями превращаем в узел рамы из полей
                if (!unit.IsLeaf)
                {
                    if (f == FillKind.Wall) { unit.Split = SplitKind.None; unit.Children = new List<Cell>(); }
                    unit.Fill = f == FillKind.Wall ? f : FillKind.Fixed;
                    return;
                }
            }
            unit.Fill = f;
        }

        // ---------------- Итоги ----------------

        public int SashCount => Units.Count(u => u.Cell.IsSash && !u.Cell.IsDoor);
        public int DoorCount => Units.Count(u => u.Cell.IsDoor);
        public int FixedCount => Units.Count(u => !u.Cell.IsSash && !u.Cell.IsWall);
        public double GlassArea => Fields.Where(f => !f.Cell.IsPanel).Sum(f => f.R.Area);
        public double PanelArea => Fields.Where(f => f.Cell.IsPanel).Sum(f => f.R.Area);
        public double MullionLength => Mullions.Where(m => !m.InSash).Sum(mi => mi.Vertical ? mi.R.H : mi.R.W);

        // ---------------- Поиск ----------------

        public MullionInfo FindMullion(Cell parent, int index) =>
            Mullions.FirstOrDefault(x => x.Parent == parent && x.Index == index);
        public FieldInfo FindField(Cell c) => Fields.FirstOrDefault(f => f.Cell == c);
        public UnitInfo FindUnit(Cell c) => Units.FirstOrDefault(u => u.Cell == c);
    }
}
