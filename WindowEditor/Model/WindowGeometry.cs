using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowEditor.Model
{
    // Система координат расчёта (мм):
    //   x — от левого края проёма, как видно с фасада;
    //   z — от самого низкого низа проёма вверх;
    //   d — глубина от наружной грани стены (0) внутрь помещения (WallThickness).

    /// <summary>Вертикальная полоса проёма: на участке [X0, X1] проём открыт от B до T.</summary>
    public class Interval
    {
        public double X0, X1, B, T;
        public Interval Copy() => (Interval)MemberwiseClone();
    }

    public class SectionGeo
    {
        public int Index;
        public Section S;
        public WindowLayout Layout;
        public Rect Block;          // габарит рамы
        public List<Rect> CoreRects = new List<Rect>(); // проём этой секции (с зазорами) полосами
        public double OpenBottom;   // низ проёма секции (самый низкий)
        public double ShareX0, ShareX1; // участок X, который «принадлежит» секции (для подоконников)
    }

    public enum FaceKind { Left, Right, Top, StepBottom, StepTop }

    /// <summary>Грань проёма, на которую ставится откос.</summary>
    public class Face
    {
        public string Name;
        public FaceKind Kind;
        public bool Vertical => Kind != FaceKind.Top;
        public double X, Z0, Z1;    // вертикальная грань
        public double X0, X1, Z;    // верхняя грань
        public int Dir;             // вертикальная: в какую сторону от грани лежит проём (+1 вправо, −1 влево)
        public bool Quartered;      // грань четверти (наружный контур)
    }

    /// <summary>Подоконник, отлив, откос — прямоугольный элемент (вложенное семейство).</summary>
    public class Box
    {
        public string Kind;          // «Подоконник внутренний», «Отлив», «Откос внутренний»…
        public string Label;         // секция или грань
        public double X0, X1, D0, D1, Z0, Z1;
        public double Length, Width, Thickness; // для спецификации
        public string MaterialParam;
        public string Material;
        public bool VerticalReveal;  // боковой откос (по высоте проёма)
    }

    public class Pane
    {
        public int Number;
        public int Section;
        public Cell Cell;
        public Cell Unit;
        public Rect Glass;      // видимая часть заполнения (стекла или панели)
        public bool IsPanel => Cell.IsPanel;
        public string MaterialParam => $"WE_Мат. {(IsPanel ? "Панель" : "Стекло")} {Number}";
    }

    public class MaterialSlot
    {
        public string Param;
        public string Title;
        public Func<string> Get;
        public Action<string> Set;
    }

    public class SillRun
    {
        public SectionGeo S;
        public double X0, X1;
        public bool WallLeft, WallRight;
        public bool HasIn, HasOut;
        public double OpenBottom, BlockBottom;
    }

    /// <summary>Полный расчёт окна: секции, контур проёма, грани, комплектация, стёкла, материалы.</summary>
    public class WindowGeometry
    {
        const double Eps = 1e-6;

        public readonly WindowConfig Cfg;
        public readonly List<SectionGeo> Sections = new List<SectionGeo>();
        public List<Interval> Core;
        public List<Interval> Exterior;
        public List<Face> CoreFaces;
        public List<Face> ExteriorFaces;
        public readonly List<SillRun> SillRuns = new List<SillRun>();
        public readonly List<Box> Boxes = new List<Box>();
        public readonly List<Pane> Panes = new List<Pane>();
        public readonly List<MaterialSlot> Materials = new List<MaterialSlot>();
        public double OpeningWidth, OpeningHeight;
        public Rect BlockBounds;

        public bool HasQuarter => Cfg.QuarterSide > 0 || Cfg.QuarterTop > 0;

        public WindowGeometry(WindowConfig cfg)
        {
            Cfg = cfg;
            if (cfg.Sections.Count == 0) cfg.Sections.Add(new Section());
            PlaceSections();
            BuildContours();
            CoreFaces = BuildFaces(Core, false);
            ExteriorFaces = BuildFaces(Exterior, HasQuarter);
            BuildSillRuns();
            BuildBoxes();
            AdjustCornerJoints();
            if (!cfg.EmptyOpening) BuildPanes();
            BuildMaterialSlots();
        }

        // ---------------- Секции и контур проёма ----------------

        void PlaceSections()
        {
            var c = Cfg;
            double minRaise = c.Sections.Min(s => s.Raise);
            double x = c.GapLeftEff;
            for (int i = 0; i < c.Sections.Count; i++)
            {
                var s = c.Sections[i];
                double ob = s.Raise - minRaise;
                var block = new Rect(x, ob + (c.EmptyOpening ? 0 : s.GapBottom), s.Width, s.Height);
                Sections.Add(new SectionGeo
                {
                    Index = i, S = s, Block = block, OpenBottom = ob,
                    Layout = new WindowLayout(c, s, block)
                });
                x += s.Width + c.ConnectorEff;
            }
            var last = Sections[Sections.Count - 1];
            OpeningWidth = last.Block.Right + c.GapRightEff;

            for (int i = 0; i < Sections.Count; i++)
            {
                var sg = Sections[i];
                var prev = i > 0 ? Sections[i - 1] : null;
                var next = i < Sections.Count - 1 ? Sections[i + 1] : null;
                var sil = sg.Layout.Silhouette;
                for (int k = 0; k < sil.Count; k++)
                {
                    var st = sil[k];
                    bool lowest = Math.Abs(st.Y - sg.Block.Y) < Eps;
                    double gb = c.EmptyOpening ? 0 : lowest ? sg.S.GapBottom : sg.S.GapStep;
                    // крайние полосы: со стороны угла проём доходит до угла здания
                    double left = st.X - (i == 0 && k == 0 ? c.GapLeftEff : c.GapSideEff);
                    // без разрыва с соседней секцией, если соединитель шире двух зазоров
                    if (k == 0 && prev != null) left = Math.Min(left, prev.Block.Right + c.GapSideEff);
                    double right = st.Right + (i == Sections.Count - 1 && k == sil.Count - 1 ? c.GapRightEff : c.GapSideEff);
                    double bottom = st.Y - gb;
                    double top = st.Top + c.GapTopEff;
                    sg.CoreRects.Add(new Rect(left, bottom, right - left, top - bottom));
                }
                sg.ShareX0 = prev == null ? 0 : (prev.Block.Right + sg.Block.X) / 2;
                sg.ShareX1 = next == null ? OpeningWidth : (sg.Block.Right + next.Block.X) / 2;
            }

            double bx0 = Sections.Min(s => s.Block.X), bx1 = Sections.Max(s => s.Block.Right);
            double bz0 = Sections.Min(s => s.Block.Y), bz1 = Sections.Max(s => s.Block.Top);
            BlockBounds = new Rect(bx0, bz0, bx1 - bx0, bz1 - bz0);
        }

        void BuildContours()
        {
            Core = Union(Sections.SelectMany(s => s.CoreRects).ToList());

            OpeningHeight = Core.Max(i => i.T);

            Exterior = Core.Select(i => i.Copy()).ToList();
            if (HasQuarter)
            {
                // со стороны угла стены нет — нет и четверти
                if (Cfg.Corner != CornerSide.Left) Exterior[0].X0 += Cfg.QuarterSide;
                if (Cfg.Corner != CornerSide.Right) Exterior[Exterior.Count - 1].X1 -= Cfg.QuarterSide;
                foreach (var iv in Exterior) iv.T -= Cfg.QuarterTop;
                Exterior.RemoveAll(iv => iv.X1 - iv.X0 < Eps || iv.T - iv.B < Eps);
            }
        }

        /// <summary>Объединение прямоугольников, стоящих в ряд, в «силуэт» из вертикальных полос.</summary>
        public static List<Interval> Union(IList<Rect> rects)
        {
            var xs = rects.SelectMany(r => new[] { r.X, r.Right }).OrderBy(v => v).ToList();
            var res = new List<Interval>();
            for (int k = 0; k < xs.Count - 1; k++)
            {
                double a = xs[k], b = xs[k + 1];
                if (b - a < Eps) continue;
                double m = (a + b) / 2;
                var cov = rects.Where(r => r.X <= m && m <= r.Right).ToList();
                if (cov.Count == 0) continue;
                double B = cov.Min(r => r.Y), T = cov.Max(r => r.Top);
                var last = res.LastOrDefault();
                if (last != null && Math.Abs(last.X1 - a) < Eps && Math.Abs(last.B - B) < Eps && Math.Abs(last.T - T) < Eps)
                    last.X1 = b;
                else
                    res.Add(new Interval { X0 = a, X1 = b, B = B, T = T });
            }
            return res;
        }

        /// <summary>
        /// Покрыть контур (полосы) не более чем maxCount прямоугольниками, каждый внутри контура; перекрываться
        /// они могут. Кандидаты — полосы a..b подряд с общей по высоте частью; перебор наименьшего набора,
        /// который закрывает каждую полосу по всей высоте. null — не удалось.
        /// </summary>
        public static List<Rect> CoverRects(List<Interval> iv, int maxCount)
        {
            int n = iv.Count;
            if (n == 0) return new List<Rect>();
            var cand = new List<(int a, int b, double z0, double z1)>();
            for (int a = 0; a < n; a++)
                for (int b = a; b < n; b++)
                {
                    if (b > a && Math.Abs(iv[b].X0 - iv[b - 1].X1) > Eps) break; // полосы не подряд — разрыв
                    double z0 = iv.Skip(a).Take(b - a + 1).Max(i => i.B), z1 = iv.Skip(a).Take(b - a + 1).Min(i => i.T);
                    if (z1 - z0 > Eps) cand.Add((a, b, z0, z1));
                }

            bool Covers(IList<(int a, int b, double z0, double z1)> set)
            {
                for (int k = 0; k < n; k++)
                {
                    var spans = set.Where(c => c.a <= k && k <= c.b).Select(c => (c.z0, c.z1)).OrderBy(s => s.z0).ToList();
                    double reach = iv[k].B;
                    foreach (var s in spans)
                    {
                        if (s.z0 > reach + Eps) break;
                        reach = Math.Max(reach, s.z1);
                    }
                    if (reach < iv[k].T - Eps) return false;
                }
                return true;
            }

            IEnumerable<List<int>> Combos(int k, int start)
            {
                if (k == 0) { yield return new List<int>(); yield break; }
                for (int i = start; i < cand.Count; i++)
                    foreach (var rest in Combos(k - 1, i + 1)) { rest.Insert(0, i); yield return rest; }
            }

            for (int k = 1; k <= maxCount; k++)
                foreach (var combo in Combos(k, 0))
                {
                    var set = combo.Select(i => cand[i]).ToList();
                    if (Covers(set))
                        return set.Select(c => new Rect(iv[c.a].X0, c.z0, iv[c.b].X1 - iv[c.a].X0, c.z1 - c.z0)).ToList();
                }
            return null;
        }

        /// <summary>Контур проёма как многоугольник (против часовой стрелки), для пустот.</summary>
        public static List<(double x, double z)> Polygon(List<Interval> iv)
        {
            var pts = new List<(double x, double z)>();
            // низ слева направо
            foreach (var i in iv) { pts.Add((i.X0, i.B)); pts.Add((i.X1, i.B)); }
            // верх справа налево
            for (int k = iv.Count - 1; k >= 0; k--) { pts.Add((iv[k].X1, iv[k].T)); pts.Add((iv[k].X0, iv[k].T)); }
            // убираем совпадающие и лежащие на одной прямой точки
            var clean = new List<(double x, double z)>();
            foreach (var p in pts)
                if (clean.Count == 0 || Math.Abs(clean[clean.Count - 1].x - p.x) > Eps || Math.Abs(clean[clean.Count - 1].z - p.z) > Eps)
                    clean.Add(p);
            if (clean.Count > 1 && Math.Abs(clean[0].x - clean[clean.Count - 1].x) < Eps && Math.Abs(clean[0].z - clean[clean.Count - 1].z) < Eps)
                clean.RemoveAt(clean.Count - 1);
            bool changed = true;
            while (changed && clean.Count > 3)
            {
                changed = false;
                for (int k = 0; k < clean.Count; k++)
                {
                    var p0 = clean[(k - 1 + clean.Count) % clean.Count];
                    var p1 = clean[k];
                    var p2 = clean[(k + 1) % clean.Count];
                    double cross = (p1.x - p0.x) * (p2.z - p1.z) - (p1.z - p0.z) * (p2.x - p1.x);
                    if (Math.Abs(cross) < Eps) { clean.RemoveAt(k); changed = true; break; }
                }
            }
            return clean;
        }

        // ---------------- Грани для откосов ----------------

        List<Face> BuildFaces(List<Interval> iv, bool quartered)
        {
            var faces = new List<Face>();
            if (iv.Count == 0) return faces;
            var first = iv[0]; var last = iv[iv.Count - 1];
            faces.Add(new Face { Name = "Левый", Kind = FaceKind.Left, X = first.X0, Z0 = first.B, Z1 = first.T, Dir = +1, Quartered = quartered });
            faces.Add(new Face { Name = "Правый", Kind = FaceKind.Right, X = last.X1, Z0 = last.B, Z1 = last.T, Dir = -1, Quartered = quartered });

            var stepsB = new List<Face>(); var stepsT = new List<Face>();
            for (int k = 0; k < iv.Count - 1; k++)
            {
                var a = iv[k]; var b = iv[k + 1];
                if (Math.Abs(a.B - b.B) > Eps)
                    stepsB.Add(new Face { Kind = FaceKind.StepBottom, X = a.X1, Z0 = Math.Min(a.B, b.B), Z1 = Math.Max(a.B, b.B), Dir = b.B > a.B ? -1 : +1 });
                if (Math.Abs(a.T - b.T) > Eps)
                    stepsT.Add(new Face { Kind = FaceKind.StepTop, X = a.X1, Z0 = Math.Min(a.T, b.T), Z1 = Math.Max(a.T, b.T), Dir = b.T < a.T ? -1 : +1 });
            }
            Name(stepsB, "Ступень низ"); Name(stepsT, "Ступень верх");

            var tops = new List<Face>();
            foreach (var i in iv)
            {
                var t = tops.LastOrDefault();
                if (t != null && Math.Abs(t.Z - i.T) < Eps && Math.Abs(t.X1 - i.X0) < Eps) t.X1 = i.X1;
                else tops.Add(new Face { Kind = FaceKind.Top, X0 = i.X0, X1 = i.X1, Z = i.T, Quartered = quartered });
            }
            Name(tops, "Верх");

            faces.AddRange(tops); faces.AddRange(stepsB); faces.AddRange(stepsT);
            return faces;
        }

        static void Name(List<Face> list, string baseName)
        {
            for (int i = 0; i < list.Count; i++) list[i].Name = list.Count == 1 ? baseName : $"{baseName} {i + 1}";
        }

        // ---------------- Подоконники и отливы ----------------

        void BuildSillRuns()
        {
            foreach (var sg in Sections)
            {
                // участки проёма в пределах секции с одинаковым низом
                var parts = Core
                    .Select(i => new Interval { X0 = Math.Max(i.X0, sg.ShareX0), X1 = Math.Min(i.X1, sg.ShareX1), B = i.B, T = i.T })
                    .Where(i => i.X1 - i.X0 > Eps)
                    .ToList();
                var runs = new List<Interval>();
                foreach (var p in parts)
                {
                    var last = runs.LastOrDefault();
                    if (last != null && Math.Abs(last.X1 - p.X0) < Eps && Math.Abs(last.B - p.B) < Eps) last.X1 = p.X1;
                    else runs.Add(p.Copy());
                }

                foreach (var r in runs)
                {
                    // низ рамы над этим участком
                    double mid = (r.X0 + r.X1) / 2;
                    var st = sg.Layout.Silhouette.Where(x => x.X - c_gap() <= mid && mid <= x.Right + c_gap())
                        .OrderBy(x => Math.Abs(x.Y - r.B)).FirstOrDefault();
                    if (st.W <= 0) continue;
                    double blockBottom = st.Y;
                    if (blockBottom - r.B > 500 || blockBottom < r.B) continue; // не под рамой этой секции

                    // подоконник и отлив — на весь участок низа, в том числе под дверью: у окна «в пол» они идут на всю
                    // ширину проёма, у двери с поднятой оконной частью — по низу двери (оконная часть — своим участком)
                    SillRuns.Add(new SillRun
                    {
                        S = sg, X0 = r.X0, X1 = r.X1,
                        OpenBottom = r.B, BlockBottom = blockBottom,
                        WallLeft = IsWallBeside(r.X0 - 1, r.B) && !(Cfg.Corner == CornerSide.Left && r.X0 <= Eps),
                        WallRight = IsWallBeside(r.X1 + 1, r.B) && !(Cfg.Corner == CornerSide.Right && r.X1 >= OpeningWidth - Eps),
                        HasIn = sg.S.SillIn.Enabled,
                        HasOut = sg.S.OuterKind != OuterSillKind.None
                    });
                }
            }
        }

        double c_gap() => Cfg.GapSide + 1;

        /// <summary>Есть ли стена в точке x на отметке чуть выше z (т.е. проём там выше или его нет).</summary>
        bool IsWallBeside(double x, double z)
        {
            var iv = Core.FirstOrDefault(i => i.X0 - Eps <= x && x <= i.X1 + Eps);
            if (iv == null) return true;          // за пределами проёма — стена
            return iv.B > z + Eps;                // проём там начинается выше — стена (подоконная часть)
        }

        double SillTopAt(double x, double zBottom, bool interior)
        {
            foreach (var r in SillRuns)
            {
                if (Math.Abs(r.OpenBottom - zBottom) > Eps) continue;
                if (x < r.X0 - 1 || x > r.X1 + 1) continue;
                if (!(interior ? r.HasIn : r.HasOut)) continue;
                if (!Cfg.EmptyOpening) return r.BlockBottom;
                // в пустом проёме доска/отлив лежат на низе проёма — откос начинается над ними
                var s = r.S.S;
                return r.BlockBottom + (interior ? s.SillIn.Thickness : s.Outer.Thickness);
            }
            return zBottom;
        }

        // ---------------- Элементы комплектации ----------------

        void BuildBoxes()
        {
            var c = Cfg;
            double t = c.WallThickness, B = c.BlockFromExterior, FD = c.FrameDepthEff;

            foreach (var r in SillRuns)
            {
                var s = r.S.S;
                int n = r.S.Index + 1;
                // несколько участков в секции (например, по бокам двери) — у каждого своя метка: по ней элементы
                // комплектации сопоставляются с моделью, одинаковые метки затирали бы друг друга
                var same = SillRuns.Where(x => x.S == r.S && (x.HasIn || x.HasOut)).ToList();
                string label = same.Count > 1 ? $"Секция {n}, участок {same.IndexOf(r) + 1}" : $"Секция {n}";
                if (r.HasIn)
                    AddSill(r, s.SillIn, "Подоконник внутренний", label, $"WE_Мат. Подоконник вн. С{n}",
                        B + FD - (c.EmptyOpening ? 0 : s.SillIn.Inset), t + s.SillIn.Projection, false);
                if (r.HasOut)
                {
                    var o = s.Outer;
                    bool drip = s.OuterKind == OuterSillKind.Drip;
                    AddSill(r, o, drip ? "Отлив" : "Подоконник наружный", label,
                        drip ? $"WE_Мат. Отлив С{n}" : $"WE_Мат. Подоконник нар. С{n}",
                        -o.Projection, B + (c.EmptyOpening ? 0 : o.Inset), true); // в пустом проёме доска и отлив стыкуются по плоскости рамы
                }
            }

            foreach (var f in CoreFaces)
            {
                if (IsCornerFace(f)) continue; // на угловой стороне стены нет — откоса нет
                var rf = c.RevealFor(f.Name);
                if (rf.In.Enabled) AddReveal(f, rf.In, true, B + FD, t);
            }
            foreach (var f in ExteriorFaces)
            {
                if (IsCornerFace(f)) continue;
                var rf = c.RevealFor(f.Name);
                double dEnd = f.Quartered && c.QuarterDepth > 0 ? Math.Min(c.QuarterDepth, B) : B;
                if (rf.Out.Enabled) AddReveal(f, rf.Out, false, 0, dEnd);
            }
            TrimRevealCorners();
        }

        /// <summary>
        /// Верхний откос идёт во всю ширину проёма, боковые — под ним: иначе в верхних углах бруски
        /// накладывались бы друг на друга на квадрат толщина × толщина.
        /// </summary>
        void TrimRevealCorners()
        {
            var reveals = Boxes.Where(b => b.Kind.StartsWith("Откос")).ToList();
            foreach (var v in reveals.Where(b => b.VerticalReveal))
            {
                var top = reveals.FirstOrDefault(h => !h.VerticalReveal && h.Kind == v.Kind &&
                    Math.Abs(h.Z1 - v.Z1) < 1 && h.X0 - 1 <= v.X0 && v.X1 <= h.X1 + 1);
                if (top == null) continue;
                v.Z1 = top.Z0;
                v.Length = v.Z1 - v.Z0;
            }
            Boxes.RemoveAll(b => b.VerticalReveal && b.Length < 1);
        }

        bool IsCornerFace(Face f) =>
            (Cfg.Corner == CornerSide.Left && f.Kind == FaceKind.Left) ||
            (Cfg.Corner == CornerSide.Right && f.Kind == FaceKind.Right);

        /// <summary>
        /// Стыки в углу двух окон с одинаковыми толщиной стены и положением рамы.
        /// Элемент с глубиной [D0; D1] от наружной грани своей стены в соседней стене занимает
        /// полосу «угол − D1 … угол − D0». Поэтому у ведущего окна элемент идёт до «угол − D0»
        /// (перекрывает угловой квадрат), у второго — обрывается на «угол − D1» (примыкает к первому).
        /// </summary>
        void AdjustCornerJoints()
        {
            if (Cfg.Corner == CornerSide.None) return;
            bool right = Cfg.Corner == CornerSide.Right;
            double xc = right ? OpeningWidth : 0;
            foreach (var b in Boxes)
            {
                bool horizontal = b.Kind.StartsWith("Подоконник") || b.Kind == "Отлив" ||
                                  (b.Kind.StartsWith("Откос") && b.Label.StartsWith("Верх"));
                if (!horizontal) continue;
                const double reach = 1; // элемент доходит до угла (уха на угловой стороне нет)
                double depth = Cfg.CornerLead ? b.D0 : b.D1;
                if (right)
                {
                    if (Math.Abs(b.X1 - xc) > reach) continue;
                    b.X1 = xc - depth;
                }
                else
                {
                    if (Math.Abs(b.X0 - xc) > reach) continue;
                    b.X0 = xc + depth;
                }
                b.Length = b.X1 - b.X0;
            }
            Boxes.RemoveAll(b => b.X1 - b.X0 < 1);
        }

        void AddSill(SillRun r, SillSettings s, string kind, string label, string param, double d0, double d1, bool exterior)
        {
            double rx0 = r.X0, rx1 = r.X1;
            // снаружи на краях проёма отлив режется по четверти, а не по проёму за ней
            if (exterior && HasQuarter && Exterior.Count > 0)
            {
                if (rx0 <= Core[0].X0 + Eps) rx0 = Exterior[0].X0;
                if (rx1 >= Core[Core.Count - 1].X1 - Eps) rx1 = Exterior[Exterior.Count - 1].X1;
            }
            double x0 = rx0 - (r.WallLeft ? s.Ear : 0);
            double x1 = rx1 + (r.WallRight ? s.Ear : 0);
            var b = new Box
            {
                Kind = kind, Label = label,
                X0 = x0, X1 = x1, D0 = d0, D1 = d1,
                // у окна — под рамой; в пустом проёме — на низе проёма
                Z0 = Cfg.EmptyOpening ? r.BlockBottom : r.BlockBottom - s.Thickness,
                Z1 = Cfg.EmptyOpening ? r.BlockBottom + s.Thickness : r.BlockBottom,
                Length = x1 - x0, Width = d1 - d0, Thickness = s.Thickness,
                MaterialParam = param, Material = s.Material
            };
            Boxes.Add(b);
            AddSlot(param, $"{kind}, {label.ToLower()}", () => s.Material, v => s.Material = v);
        }

        void AddReveal(Face f, RevealSide side, bool interior, double d0, double d1)
        {
            if (d1 - d0 < 1) return;
            double th = side.Thickness;
            var b = new Box
            {
                Kind = interior ? "Откос внутренний" : "Откос наружный",
                Label = f.Name, D0 = d0, D1 = d1, Thickness = th, Material = side.Material,
                MaterialParam = $"WE_Мат. Откос {(interior ? "вн." : "нар.")} {f.Name}"
            };
            if (f.Vertical)
            {
                b.VerticalReveal = true;
                if (f.Dir > 0) { b.X0 = f.X; b.X1 = f.X + th; } else { b.X0 = f.X - th; b.X1 = f.X; }
                b.Z0 = f.Kind == FaceKind.StepTop ? f.Z0 : SillTopAt(f.X, f.Z0, interior);
                b.Z1 = f.Z1;
                b.Length = b.Z1 - b.Z0;
            }
            else
            {
                b.X0 = f.X0; b.X1 = f.X1; b.Z0 = f.Z - th; b.Z1 = f.Z;
                b.Length = b.X1 - b.X0;
            }
            b.Width = d1 - d0;
            if (b.Length < 1) return;
            Boxes.Add(b);
            AddSlot(b.MaterialParam, $"{b.Kind}, {f.Name.ToLower()}", () => side.Material, v => side.Material = v);
        }

        // ---------------- Стёкла и материалы ----------------

        void BuildPanes()
        {
            int n = 0;
            foreach (var sg in Sections)
                foreach (var f in sg.Layout.Fields)
                    Panes.Add(new Pane { Number = ++n, Section = sg.Index, Cell = f.Cell, Unit = f.Unit, Glass = f.R });
        }

        void BuildMaterialSlots()
        {
            var c = Cfg;
            // рама и створки — в начало списка
            if (c.EmptyOpening) return; // в пустом проёме только элементы комплектации
            Materials.Insert(0, new MaterialSlot { Param = "WE_Мат. Рама", Title = "Рама и импосты", Get = () => c.FrameMaterial, Set = v => c.FrameMaterial = v });
            int k = 1;
            if (Panes.Any(p => p.Unit.IsSash))
                Materials.Insert(k++, new MaterialSlot { Param = "WE_Мат. Створки", Title = "Створки", Get = () => c.SashMaterial, Set = v => c.SashMaterial = v });
            foreach (var p in Panes)
            {
                var cell = p.Cell;
                Func<string> def = () => cell.IsPanel ? c.PanelMaterial : c.GlassMaterial;
                Materials.Insert(k++, new MaterialSlot
                {
                    Param = p.MaterialParam,
                    Title = $"{(p.IsPanel ? "Панель" : "Стекло")} {p.Number}",
                    Get = () => string.IsNullOrEmpty(cell.GlassMaterial) ? def() : cell.GlassMaterial,
                    Set = v => cell.GlassMaterial = v == def() ? "" : v
                });
            }
        }

        void AddSlot(string param, string title, Func<string> get, Action<string> set)
        {
            if (Materials.Any(m => m.Param == param)) return;
            Materials.Add(new MaterialSlot { Param = param, Title = title, Get = get, Set = set });
        }

        // ---------------- Итоги ----------------

        public int SashCount => Cfg.EmptyOpening ? 0 : Sections.Sum(s => s.Layout.SashCount);
        public int DoorCount => Cfg.EmptyOpening ? 0 : Sections.Sum(s => s.Layout.DoorCount);
        public int FixedCount => Cfg.EmptyOpening ? 0 : Sections.Sum(s => s.Layout.FixedCount);
        public double GlassArea => Panes.Where(p => !p.IsPanel).Sum(p => p.Glass.Area);
        public double PanelArea => Panes.Where(p => p.IsPanel).Sum(p => p.Glass.Area);
        public double MullionLength => Cfg.EmptyOpening ? 0 : Sections.Sum(s => s.Layout.MullionLength);

        public SectionGeo SectionAt(double x, double z) =>
            Sections.FirstOrDefault(s => s.Block.Contains(x, z));

        /// <summary>
        /// Код схемы для имени: ряды снизу вверх, в каждом — число единиц рамы (створок и глухих полей),
        /// у которых низ в этом ряду. Створка с полями считается одной единицей. Пример: 121, 1223.
        /// </summary>
        public string Code()
        {
            if (Cfg.EmptyOpening) return "";
            var bottoms = Sections.SelectMany(s => s.Layout.Units.Where(u => !u.Cell.IsWall))
                .Select(u => Math.Round(u.R.Y)).ToList();
            if (bottoms.Count == 0) return "";
            var groups = bottoms.GroupBy(b => b).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
            bool wide = groups.Any(n => n > 9);
            return string.Join(wide ? "," : "", groups);
        }

        /// <summary>Заход рамы за четверть (по боку), мм. Отрицательный — рама не перекрывает четверть.</summary>
        public double QuarterOverlap => Cfg.QuarterSide - Cfg.GapSide;

        /// <summary>Положение угла здания по X (для углового окна), иначе NaN.</summary>
        public double CornerX => Cfg.Corner == CornerSide.Right ? OpeningWidth : Cfg.Corner == CornerSide.Left ? 0 : double.NaN;

        /// <summary>Стойка углового соединителя, как видна с фасада: X-диапазон, или null.</summary>
        public Rect? CornerPostFacade
        {
            get
            {
                if (Cfg.Corner == CornerSide.None || Cfg.EmptyOpening) return null;
                var sg = Cfg.Corner == CornerSide.Right ? Sections[Sections.Count - 1] : Sections[0];
                var st = Cfg.Corner == CornerSide.Right ? sg.Layout.Silhouette.Last() : sg.Layout.Silhouette.First();
                double p = Cfg.CornerPostSize, b = Cfg.BlockFromExterior;
                double x0 = Cfg.Corner == CornerSide.Right ? OpeningWidth - b - p : b;
                return new Rect(x0, st.Y, p, st.H);
            }
        }
    }
}
