using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace WindowEditor.Model
{
    /// <summary>Как ячейка поделена импостами.</summary>
    public enum SplitKind
    {
        None,     // лист: заполнение
        Columns,  // вертикальные импосты, дети слева направо
        Rows      // горизонтальные импосты, дети снизу вверх
    }

    /// <summary>Заполнение ячейки. Сторона петель — как видно в редакторе (вид с фасада).</summary>
    public enum FillKind
    {
        Fixed,
        TurnLeft, TurnRight,
        TiltTurnLeft, TiltTurnRight,
        Tilt,
        DoorLeft, DoorRight,             // дверная створка, поворотная
        DoorTiltLeft, DoorTiltRight,     // дверная створка, поворотно-откидная
        Wall                             // стена: ячейка вне рамы (Г- и Т-образные блоки)
    }

    public enum OuterSillKind { None, Drip, Sill }

    /// <summary>Сторона окна (как видно с фасада), выходящая на наружный угол здания 90°.</summary>
    public enum CornerSide { None, Left, Right }

    /// <summary>От чего отсчитывается положение рамы по глубине стены.</summary>
    public enum FrameAnchor
    {
        Exterior,   // наружная грань рамы — по наружной грани стены
        Quarter,    // наружная грань рамы — по плоскости четверти
        Center,     // ось рамы — по оси стены
        Interior    // внутренняя грань рамы — по внутренней грани стены
    }

    /// <summary>Заполнение поля: стеклопакет или непрозрачная панель (сэндвич).</summary>
    public enum InfillKind { Glass, Panel }

    public class Cell
    {
        /// <summary>Размер вдоль деления родителя, мм. У последнего ребёнка не используется — он забирает остаток.</summary>
        public double Size { get; set; }
        public SplitKind Split { get; set; } = SplitKind.None;
        public FillKind Fill { get; set; } = FillKind.Fixed;
        /// <summary>Материал заполнения этого поля. Пусто — материал по умолчанию (стекла или панели).</summary>
        public string GlassMaterial { get; set; } = "";
        /// <summary>Стекло или глухая панель.</summary>
        public InfillKind Infill { get; set; } = InfillKind.Glass;
        [JsonIgnore] public bool IsPanel => Infill == InfillKind.Panel;
        public List<Cell> Children { get; set; } = new List<Cell>();

        [JsonIgnore] public bool IsLeaf => Split == SplitKind.None || Children.Count == 0;
        /// <summary>Створка (в т.ч. с полями внутри: тогда Split != None, а дети — поля створки).</summary>
        [JsonIgnore] public bool IsSash => Fill != FillKind.Fixed && Fill != FillKind.Wall;
        [JsonIgnore] public bool IsDoor => Fill >= FillKind.DoorLeft && Fill <= FillKind.DoorTiltRight;
        [JsonIgnore] public bool IsWall => Fill == FillKind.Wall;
        /// <summary>Единица рамы: глухое поле, створка целиком или стена.</summary>
        [JsonIgnore] public bool IsUnit => IsLeaf || IsSash;

        /// <summary>Единицы рамы в поддереве (поля внутри створок не выдаются).</summary>
        public IEnumerable<Cell> Units()
        {
            if (IsUnit) { yield return this; yield break; }
            foreach (var c in Children) foreach (var u in c.Units()) yield return u;
        }

        public IEnumerable<Cell> Leaves()
        {
            if (IsLeaf) { yield return this; yield break; }
            foreach (var c in Children) foreach (var l in c.Leaves()) yield return l;
        }

        public Cell DeepCopy() => new Cell
        {
            Size = Size, Split = Split, Fill = Fill, GlassMaterial = GlassMaterial, Infill = Infill,
            Children = Children.Select(c => c.DeepCopy()).ToList()
        };
    }

    /// <summary>Подоконник, отлив: один элемент вдоль низа проёма.</summary>
    public class SillSettings
    {
        public bool Enabled { get; set; } = true;
        public double Thickness { get; set; } = 20;
        /// <summary>Вынос за грань стены, мм.</summary>
        public double Projection { get; set; } = 50;
        /// <summary>Заход под раму, мм.</summary>
        public double Inset { get; set; } = 20;
        /// <summary>Заход в стену по торцам («уши»), мм. Только там, где сбоку стена.</summary>
        public double Ear { get; set; } = 50;
        public string Material { get; set; } = "";

        public SillSettings Copy() => (SillSettings)MemberwiseClone();
    }

    /// <summary>
    /// Секция блока: отдельная рама со своим деревом ячеек. Балконный блок = дверная секция + оконная секция.
    /// </summary>
    public class Section
    {
        /// <summary>Размер рамы (изделия), мм.</summary>
        public double Width { get; set; } = 1500;
        public double Height { get; set; } = 1500;
        /// <summary>Отметка низа проёма этой секции над самым низким низом проёма, мм. У балконной двери 0, у окна рядом — высота подоконной части стены.</summary>
        public double Raise { get; set; } = 0;
        /// <summary>Монтажный зазор снизу (вместе с подставочным профилем), мм.</summary>
        public double GapBottom { get; set; } = 40;
        /// <summary>Зазор под частью рамы, стоящей на подоконной части стены (Г-/Т-образная рама), мм.</summary>
        public double GapStep { get; set; } = 40;
        public Cell Root { get; set; } = new Cell();

        public SillSettings SillIn { get; set; } = new SillSettings { Thickness = 20, Projection = 50, Inset = 20, Ear = 50, Material = "Подоконник ПВХ белый" };
        public OuterSillKind OuterKind { get; set; } = OuterSillKind.Drip;
        public SillSettings Drip { get; set; } = new SillSettings { Thickness = 2, Projection = 40, Inset = 20, Ear = 20, Material = "Отлив оцинкованный" };
        public SillSettings SillOut { get; set; } = new SillSettings { Thickness = 30, Projection = 50, Inset = 0, Ear = 50, Material = "Подоконник наружный" };

        [JsonIgnore] public bool HasDoor => Root.Units().Any(l => l.IsDoor);
        [JsonIgnore] public SillSettings Outer => OuterKind == OuterSillKind.Sill ? SillOut : OuterKind == OuterSillKind.Drip ? Drip : null;
    }

    public class RevealSide
    {
        public bool Enabled { get; set; }
        public double Thickness { get; set; } = 10;
        public string Material { get; set; } = "";
        public RevealSide Copy() => (RevealSide)MemberwiseClone();
    }

    /// <summary>Настройки откосов одной грани проёма: внутренний и наружный независимо.</summary>
    public class RevealFace
    {
        public string Face { get; set; }
        public RevealSide In { get; set; }
        public RevealSide Out { get; set; }
    }

    /// <summary>Конфигурация окна. Хранится как JSON в текстовом параметре типа WE_Config.</summary>
    public class WindowConfig
    {
        public int Version { get; set; } = 2;

        public List<Section> Sections { get; set; } = new List<Section>();

        /// <summary>
        /// Пустой проём: без рамы, створок и стёкол. Размеры секций — размеры проёма,
        /// зазоры и соединитель не действуют; откосы, подоконные доски и отливы — как у окна.
        /// </summary>
        public bool EmptyOpening { get; set; }

        // зазоры с учётом режима пустого проёма
        [JsonIgnore] public double GapSideEff => EmptyOpening ? 0 : GapSide;
        [JsonIgnore] public double GapTopEff => EmptyOpening ? 0 : GapTop;
        [JsonIgnore] public double ConnectorEff => EmptyOpening ? 0 : ConnectorWidth;
        [JsonIgnore] public double FrameDepthEff => EmptyOpening ? 0 : FrameDepth;

        // ---- Профиль, мм ----
        public double FrameWidth { get; set; } = 60;
        public double FrameDepth { get; set; } = 70;
        public double MullionWidth { get; set; } = 80;
        public double SashWidth { get; set; } = 50;
        public double SashDepth { get; set; } = 78;
        /// <summary>Импост внутри створки (между стеклом и нижним полем двери и т.п.), мм.</summary>
        public double SashMullionWidth { get; set; } = 60;
        public double GlassThickness { get; set; } = 32;
        /// <summary>Толщина глухой панели (сэндвич), мм.</summary>
        public double PanelThickness { get; set; } = 24;
        /// <summary>Соединительный профиль между секциями, мм.</summary>
        public double ConnectorWidth { get; set; } = 10;

        // ---- Проём и стена, мм ----
        public double WallThickness { get; set; } = 400;
        /// <summary>true — фасад со стороны +Y семейства.</summary>
        public bool ExteriorPositiveY { get; set; } = true;
        /// <summary>Привязка рамы по глубине.</summary>
        public FrameAnchor Anchor { get; set; } = FrameAnchor.Exterior;
        /// <summary>Смещение рамы от привязки: + вглубь (к помещению), − наружу, мм.</summary>
        public double FrameOffset { get; set; } = 120;

        /// <summary>Положение привязки: расстояние от наружной грани стены до наружной грани рамы при нулевом смещении.</summary>
        public double AnchorBase(FrameAnchor a)
        {
            switch (a)
            {
                case FrameAnchor.Quarter: return QuarterDepth;
                case FrameAnchor.Center: return WallThickness / 2 - FrameDepthEff / 2;
                case FrameAnchor.Interior: return WallThickness - FrameDepthEff;
                default: return 0;
            }
        }

        /// <summary>Итог: от наружной грани стены до наружной грани рамы, мм.</summary>
        [JsonIgnore] public double BlockFromExterior => AnchorBase(Anchor) + FrameOffset;
        /// <summary>От внутренней грани рамы до внутренней грани стены, мм.</summary>
        [JsonIgnore] public double BlockToInterior => WallThickness - BlockFromExterior - FrameDepthEff;

        /// <summary>Сменить привязку, не сдвигая раму.</summary>
        public void SetAnchor(FrameAnchor a)
        {
            double b = BlockFromExterior;
            Anchor = a;
            FrameOffset = b - AnchorBase(a);
        }

        // формат до привязок: только расстояние от наружной грани
        [JsonProperty("BlockFromExterior", NullValueHandling = NullValueHandling.Ignore)]
        public double? LegacyBlockFromExterior { get; set; }
        public double GapSide { get; set; } = 20;
        public double GapTop { get; set; } = 20;
        /// <summary>Четверть: 0 — без четверти.</summary>
        public double QuarterSide { get; set; } = 0;
        public double QuarterTop { get; set; } = 0;
        public double QuarterDepth { get; set; } = 0;

        // ---- Угловое окно (наружный угол 90°) ----
        /// <summary>Сторона, выходящая на угол. Проём на ней доходит до угла здания, зазора, четверти и откоса нет.</summary>
        public CornerSide Corner { get; set; } = CornerSide.None;
        /// <summary>Ведущее окно угла: его доски, отливы и верхние откосы проходят до угла, у второго — примыкают.</summary>
        public bool CornerLead { get; set; } = true;
        /// <summary>Угловой соединитель (стойка) — сторона квадрата, мм. Реальный размер профиля.</summary>
        public double CornerPostSize { get; set; } = 80;
        public string CornerPostMaterial { get; set; } = "ПВХ белый";

        /// <summary>От рамы до наружного угла здания по фасаду: глубина рамы в стене + стойка. В пустом проёме — 0.</summary>
        [JsonIgnore] public double CornerGap => EmptyOpening ? 0 : BlockFromExterior + CornerPostSize;
        [JsonIgnore] public double GapLeftEff => Corner == CornerSide.Left ? CornerGap : GapSideEff;
        [JsonIgnore] public double GapRightEff => Corner == CornerSide.Right ? CornerGap : GapSideEff;

        // ---- Материалы ----
        public string FrameMaterial { get; set; } = "ПВХ белый";
        public string SashMaterial { get; set; } = "ПВХ белый";
        public string GlassMaterial { get; set; } = "Стекло";
        public string PanelMaterial { get; set; } = "Сэндвич-панель белая";

        // ---- Откосы ----
        public RevealSide RevealInDefault { get; set; } = new RevealSide { Enabled = true, Thickness = 10, Material = "Откос ПВХ белый" };
        public RevealSide RevealOutDefault { get; set; } = new RevealSide { Enabled = false, Thickness = 20, Material = "Штукатурка фасадная" };
        public List<RevealFace> Reveals { get; set; } = new List<RevealFace>();

        // ---- Поля формата v1, только для чтения старых окон ----
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public double? Width { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public double? Height { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public Cell Root { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public double? OffsetY { get; set; }

        public const double MinCell = 150;

        /// <summary>Настройки граней с этим именем (создаются по умолчанию при первом обращении).</summary>
        public RevealFace RevealFor(string face)
        {
            var r = Reveals.FirstOrDefault(x => x.Face == face);
            if (r == null)
            {
                r = new RevealFace { Face = face, In = RevealInDefault.Copy(), Out = RevealOutDefault.Copy() };
                Reveals.Add(r);
            }
            return r;
        }

        // Newtonsoft (как во всём TNov): System.Text.Json в Revit 2022 не подгружается.
        // Replace — иначе списки, заполненные в инициализаторах, дополнялись бы при чтении.
        static readonly JsonSerializerSettings Opts = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            Converters = { new StringEnumConverter() }
        };

        public string ToJson() => JsonConvert.SerializeObject(this, Opts);

        public static WindowConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            WindowConfig c;
            try { c = JsonConvert.DeserializeObject<WindowConfig>(json, Opts); }
            catch (JsonException) { return null; }
            if (c == null) return null;

            // Миграция v1: одна рама, размеры на верхнем уровне
            if (c.Sections.Count == 0)
            {
                c.Sections.Add(new Section
                {
                    Width = c.Width ?? 1500,
                    Height = c.Height ?? 1500,
                    Root = c.Root ?? new Cell()
                });
            }
            if (c.LegacyBlockFromExterior.HasValue)
            {
                c.Anchor = FrameAnchor.Exterior;
                c.FrameOffset = c.LegacyBlockFromExterior.Value;
                c.LegacyBlockFromExterior = null;
            }
            c.Width = null; c.Height = null; c.Root = null; c.OffsetY = null;
            c.Version = 2;
            return c;
        }

        public WindowConfig Clone() => FromJson(ToJson());

        /// <summary>Короткая схема для спецификаций: "Д-ПО-Л + Г | ПО-П".</summary>
        public string SchemeText() => EmptyOpening
            ? "Пустой проём"
            : string.Join(" + ", Sections.Select(s => Strip(SchemeOf(s.Root), s.Root)));

        /// <summary>
        /// Включить/выключить режим пустого проёма, сохранив размер проёма:
        /// рама = проём − зазоры и наоборот.
        /// </summary>
        public void SetEmptyOpening(bool on)
        {
            if (on == EmptyOpening) return;
            int k = on ? 1 : -1;
            foreach (var s in Sections)
            {
                s.Width += k * 2 * GapSide;
                s.Height += k * (GapTop + s.GapBottom);
            }
            EmptyOpening = on;
        }

        static string Strip(string s, Cell root) => root.IsUnit ? s : s.Substring(1, s.Length - 2);

        static string SchemeOf(Cell c)
        {
            if (c.IsUnit)
            {
                string fields = c.IsLeaf ? "" : $"[{c.Leaves().Count()}]";
                switch (c.Fill)
                {
                    case FillKind.TurnLeft: return "П-Л" + fields;
                    case FillKind.TurnRight: return "П-П" + fields;
                    case FillKind.TiltTurnLeft: return "ПО-Л" + fields;
                    case FillKind.TiltTurnRight: return "ПО-П" + fields;
                    case FillKind.Tilt: return "О" + fields;
                    case FillKind.DoorLeft: return "Д-П-Л" + fields;
                    case FillKind.DoorRight: return "Д-П-П" + fields;
                    case FillKind.DoorTiltLeft: return "Д-ПО-Л" + fields;
                    case FillKind.DoorTiltRight: return "Д-ПО-П" + fields;
                    case FillKind.Wall: return "Ст";
                    default: return "Г";
                }
            }
            var parts = c.Children.Select(SchemeOf).ToList();
            if (c.Split == SplitKind.Rows) parts.Reverse(); // сверху вниз, как читают чертёж
            return "(" + string.Join(c.Split == SplitKind.Columns ? " | " : " / ", parts) + ")";
        }
    }
}
