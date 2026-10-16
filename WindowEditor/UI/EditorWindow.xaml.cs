using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WindowEditor.Model;
using MRect = WindowEditor.Model.Rect;

namespace WindowEditor.UI
{
    public partial class EditorWindow : Window
    {
        public WindowConfig Config { get; private set; }
        /// <summary>Имя семейства и типа по правилу «имя_ШхВ» (проём) — суффикс размера дописывается сам.</summary>
        public string FamilyName => Mirror.WithSize(FamilyNameBox.Text.Trim(), _geo.OpeningWidth, _geo.OpeningHeight);
        public bool SaveAsNew => SaveAsNewBox.IsChecked == true;

        WindowGeometry _geo;
        readonly List<string> _materials;

        // выделение
        int _selSection;
        Cell _selCell;   // поле, единица рамы (створка по профилю, стена)
        Cell _selMullParent; int _selMullIndex = -1;

        readonly Stack<string> _undo = new Stack<string>();
        bool _loading;

        // перетаскивание импоста
        bool _dragging;
        string _dragStartJson;
        int _dragMullionFlatIndex;
        Point _dragStart;

        // мм -> экран
        double _scale, _ox, _oy, _wx0, _wz0;
        const double Pad = 20;

        // поля вкладки «Проём»
        readonly List<NumField> _openingFields = new List<NumField>();
        CheckBox _extPosBox;
        TextBlock _overlapText;

        static readonly (FillKind kind, string name)[] Fills =
        {
            (FillKind.Fixed, "Глухое"),
            (FillKind.TurnLeft, "Поворотная, петли слева"),
            (FillKind.TurnRight, "Поворотная, петли справа"),
            (FillKind.TiltTurnLeft, "Поворотно-откидная, петли слева"),
            (FillKind.TiltTurnRight, "Поворотно-откидная, петли справа"),
            (FillKind.Tilt, "Откидная (фрамуга)"),
            (FillKind.DoorLeft, "Дверная поворотная, петли слева"),
            (FillKind.DoorRight, "Дверная поворотная, петли справа"),
            (FillKind.DoorTiltLeft, "Дверная поворотно-откидная, петли слева"),
            (FillKind.DoorTiltRight, "Дверная поворотно-откидная, петли справа"),
            (FillKind.Wall, "Стена (вне рамы)"),
        };

        /// <summary>Цвета чертежа: под светлую или тёмную тему TNov (по фону карточки).</summary>
        sealed class Palette
        {
            public Brush Ink, Wall, Frame, Sash, Glass, GlassCustom, Panel, PanelLine, Opening, Quarter,
                OuterSill, InnerSill, CornerPost, Text, Muted, TextBg, Badge, Accent, AccentFill, AccentFillLight, Error;

            public static Palette For(FrameworkElement res)
            {
                Brush R(string key, Brush fallback) => res.TryFindResource(key) as Brush ?? fallback;
                var card = R("CardBrush", Brushes.White) as SolidColorBrush;
                var c = card?.Color ?? Colors.White;
                bool dark = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128;
                var accent = R("AccentBrush", Rgb(0x1B, 0x8A, 0x4A));
                var ac = (accent as SolidColorBrush)?.Color ?? Colors.Green;
                var p = new Palette
                {
                    Accent = accent,
                    AccentFill = new SolidColorBrush(Color.FromArgb(40, ac.R, ac.G, ac.B)),
                    AccentFillLight = new SolidColorBrush(Color.FromArgb(28, ac.R, ac.G, ac.B)),
                    Text = R("PrimaryTextBrush", Brushes.Black),
                    Muted = R("MutedBrush", Brushes.DimGray),
                    Error = R("ElementErrorBrush", Brushes.Firebrick),
                    Badge = Rgb(0x2F, 0x5F, 0x8A),
                };
                if (dark)
                {
                    p.Ink = Rgb(0xD5, 0xE0, 0xDA); p.Wall = Rgb(0x4A, 0x46, 0x3E); p.Frame = Rgb(0x5C, 0x69, 0x63);
                    p.Sash = Rgb(0x4B, 0x57, 0x51); p.Glass = Rgb(0x2E, 0x58, 0x6E); p.GlassCustom = Rgb(0x3A, 0x63, 0x48);
                    p.Panel = Rgb(0x6A, 0x69, 0x62); p.PanelLine = Rgb(0x90, 0x8F, 0x86); p.Opening = Rgb(0x14, 0x20, 0x1A);
                    p.Quarter = Rgb(0xC8, 0xA0, 0x60); p.OuterSill = Rgb(0x7A, 0x82, 0x8C); p.InnerSill = Rgb(0xA0, 0xA8, 0xA4);
                    p.CornerPost = Rgb(0x6E, 0x76, 0x80); p.TextBg = new SolidColorBrush(Color.FromArgb(190, c.R, c.G, c.B));
                }
                else
                {
                    p.Ink = Rgb(0x33, 0x38, 0x40); p.Wall = Rgb(0xE8, 0xE2, 0xD8); p.Frame = Rgb(0xE6, 0xE8, 0xEC);
                    p.Sash = Rgb(0xF2, 0xF3, 0xF5); p.Glass = Rgb(0xD6, 0xEB, 0xF7); p.GlassCustom = Rgb(0xC4, 0xDD, 0xC8);
                    p.Panel = Rgb(0xD9, 0xD9, 0xD4); p.PanelLine = Rgb(0xA0, 0xA0, 0x98); p.Opening = Brushes.White;
                    p.Quarter = Rgb(0x8A, 0x6D, 0x3B); p.OuterSill = Rgb(0x9A, 0xA1, 0xAB); p.InnerSill = Rgb(0x70, 0x70, 0x70);
                    p.CornerPost = Rgb(0xC9, 0xCD, 0xD4); p.TextBg = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
                }
                return p;
            }
        }

        readonly Palette _pal;

        class NumField
        {
            public TextBox Box;
            public Func<double> Get;
            public Action<double> Set;
            public double Min = double.MinValue;
        }

        /// <param name="materials">Имена материалов проекта для выпадающих списков.</param>
        /// <param name="canSaveAsNew">false для нового окна — всегда новое семейство.</param>
        public EditorWindow(WindowConfig cfg, string familyName, bool canSaveAsNew, IEnumerable<string> materials)
        {
            InitializeComponent();
            _pal = Palette.For(this);
            Config = cfg?.Clone() ?? Presets.Create(Presets.IndexOf("v3 12"));
            _materials = (materials ?? Enumerable.Empty<string>()).Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct().OrderBy(m => m).ToList();

            FamilyNameBox.Text = familyName;
            SaveAsNewBox.Visibility = canSaveAsNew ? Visibility.Visible : Visibility.Collapsed;

            _loading = true;
            foreach (var f in Fills) FillBox.Items.Add(f.name);
            // новое окно начинается с выбора шаблона, правка существующего — со схемы
            Tabs.SelectedItem = canSaveAsNew ? SchemeTab : StartTab;
            _loading = false;

            BuildStartTab();
            BuildOpeningTab();
            Relayout();
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (s, e) => Sheet.Focus();
        }

        // =====================================================================
        // Общее
        // =====================================================================

        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        static bool TryNum(string s, out double v) =>
            double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        void PushUndo() => _undo.Push(Config.ToJson());

        void Undo()
        {
            if (_undo.Count == 0) return;
            Config = WindowConfig.FromJson(_undo.Pop());
            ClearSelection();
            Relayout();
        }

        Section SelSection => Config.Sections[Math.Max(0, Math.Min(_selSection, Config.Sections.Count - 1))];

        void ClearSelection() { _selCell = null; _selMullParent = null; _selMullIndex = -1; }

        /// <summary>Пересчитать всё и перерисовать. rebuildTabs=false — правка внутри динамической вкладки.</summary>
        void Relayout(bool rebuildTabs = true)
        {
            _selSection = Math.Max(0, Math.Min(_selSection, Config.Sections.Count - 1));
            _geo = new WindowGeometry(Config);
            Redraw();
            UpdateSchemePanel();
            LoadOpeningFields();
            TitleInfo.Text = FamilyName;
            if (rebuildTabs) RebuildCurrentDynamicTab();
        }

        IEnumerable<MullionInfo> AllMullions => _geo.Sections.SelectMany(s => s.Layout.Mullions);

        MullionInfo SelMullion => _selMullParent == null ? null
            : AllMullions.FirstOrDefault(m => m.Parent == _selMullParent && m.Index == _selMullIndex);
        WindowLayout LayoutOf(Cell c) => c == null ? null : _geo.Sections.Select(s => s.Layout).FirstOrDefault(l => l.Owns(c));
        /// <summary>Выбранное поле (стекло), если выбрано поле.</summary>
        FieldInfo SelField => _selCell == null ? null : LayoutOf(_selCell)?.FindField(_selCell);
        /// <summary>Единица рамы выбранного поля/створки/стены.</summary>
        UnitInfo SelUnit
        {
            get
            {
                var lay = LayoutOf(_selCell);
                if (lay == null) return null;
                var u = lay.UnitOf(_selCell);
                return u == null ? null : lay.FindUnit(u);
            }
        }

        // =====================================================================
        // Вкладка «Схема»
        // =====================================================================

        void UpdateSchemePanel()
        {
            var s = SelSection;
            _loading = true;
            bool empty = Config.EmptyOpening;
            EmptyBox.IsChecked = empty;
            SecWidthLabel.Text = empty ? "Ширина проёма" : "Ширина рамы";
            SecHeightLabel.Text = empty ? "Высота проёма" : "Высота рамы";
            ImpostPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            SecGapBottomBox.IsEnabled = !empty;
            SecGapStepBox.IsEnabled = !empty;
            SectionHeader.Text = Config.Sections.Count > 1
                ? $"Секция {_selSection + 1} из {Config.Sections.Count}{(s.HasDoor ? " (дверная)" : "")}"
                : "Рама";
            SecWidthBox.Text = F(s.Width);
            SecHeightBox.Text = F(s.Height);
            SecRaiseBox.Text = F(s.Raise);
            SecGapBottomBox.Text = F(s.GapBottom);
            SecGapStepBox.Text = F(s.GapStep);
            RemoveSectionBtn.IsEnabled = Config.Sections.Count > 1;

            var unit = SelUnit;
            var field = SelField;
            var mull = SelMullion;
            if (empty) { unit = null; field = null; mull = null; }
            LeafPanel.Visibility = unit != null ? Visibility.Visible : Visibility.Collapsed;
            MullionPanel.Visibility = mull != null ? Visibility.Visible : Visibility.Collapsed;
            RemoveBtn.IsEnabled = mull != null;
            SelHeader.Text = unit != null
                ? (unit.Cell.IsWall ? "Стена в раме" : unit.Cell.IsSash ? (field != null && field.Cell != unit.Cell ? "Поле створки" : "Створка") : "Глухое поле")
                : mull != null ? (mull.InSash ? "Импост створки" : "Импост") : "Выделение: ничего";

            if (unit != null)
            {
                FillBox.SelectedIndex = Array.FindIndex(Fills, f => f.kind == unit.Cell.Fill);
                var pane = field == null ? null : _geo.Panes.FirstOrDefault(p => p.Cell == field.Cell);
                var vis = pane != null ? Visibility.Visible : Visibility.Collapsed;
                GlassLabel.Visibility = vis; GlassMatBox.Visibility = vis;
                InfillLabel.Visibility = vis; InfillBox.Visibility = vis;
                if (pane != null)
                {
                    InfillBox.SelectedIndex = pane.IsPanel ? 1 : 0;
                    GlassLabel.Text = $"Материал {(pane.IsPanel ? "панели" : "стекла")} {pane.Number}";
                    var def = pane.IsPanel ? Config.PanelMaterial : Config.GlassMaterial;
                    FillMaterialCombo(GlassMatBox, string.IsNullOrEmpty(field.Cell.GlassMaterial) ? def : field.Cell.GlassMaterial);
                    LeafSizeText.Text = $"{(unit.Cell.IsSash ? "Створка" : "Поле")}: {unit.R.W:0} × {unit.R.H:0} мм, {(pane.IsPanel ? "панель" : "стекло")} {pane.Glass.W:0} × {pane.Glass.H:0} мм";
                }
                else LeafSizeText.Text = $"{unit.R.W:0} × {unit.R.H:0} мм";
            }
            if (mull != null)
            {
                var sg = _geo.Sections.First(g => g.Layout.Mullions.Contains(mull));
                double axis = mull.Vertical ? mull.Axis - sg.Block.X : mull.Axis - sg.Block.Y;
                MullionAxisLabel.Text = mull.Vertical ? "Ось импоста от левого края рамы, мм" : "Ось импоста от низа рамы, мм";
                MullionAxisBox.Text = F(axis);
            }

            SummaryText.Text =
                $"Код: {_geo.Code()}\n" +
                $"Схема: {Config.SchemeText()}\n" +
                $"Проём: {_geo.OpeningWidth:0} × {_geo.OpeningHeight:0} мм\n" +
                $"Блок: {_geo.BlockBounds.W:0} × {_geo.BlockBounds.H:0} мм\n" +
                $"Створок оконных: {_geo.SashCount}, дверных: {_geo.DoorCount}, глухих: {_geo.FixedCount}\n" +
                $"Площадь остекления: {_geo.GlassArea / 1e6:0.00} м²" +
                (_geo.PanelArea > 0 ? $", панелей: {_geo.PanelArea / 1e6:0.00} м²" : "") + "\n" +
                $"Длина импостов: {_geo.MullionLength / 1000:0.00} м\n" +
                $"Элементов комплектации: {_geo.Boxes.Count}";
            _loading = false;
        }

        void FillMaterialCombo(ComboBox cb, string current)
        {
            cb.Items.Clear();
            var names = _materials.ToList();
            if (!string.IsNullOrEmpty(current) && !names.Contains(current)) names.Insert(0, current);
            foreach (var n in names) cb.Items.Add(n);
            cb.Text = current ?? "";
        }

        void Section_Commit(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (!TryNum(SecWidthBox.Text, out var w) || !TryNum(SecHeightBox.Text, out var h) ||
                !TryNum(SecRaiseBox.Text, out var r) || !TryNum(SecGapBottomBox.Text, out var gb) ||
                !TryNum(SecGapStepBox.Text, out var gs)) { UpdateSchemePanel(); return; }
            var s = SelSection;
            double min = 2 * Config.FrameWidth + WindowConfig.MinCell;
            w = Math.Max(min, w); h = Math.Max(min, h); r = Math.Max(0, r); gb = Math.Max(0, gb); gs = Math.Max(0, gs);
            if (w == s.Width && h == s.Height && r == s.Raise && gb == s.GapBottom && gs == s.GapStep) { UpdateSchemePanel(); return; }
            PushUndo();
            bool facesMayChange = r != s.Raise || h != s.Height || gb != s.GapBottom || gs != s.GapStep;
            s.Width = w; s.Height = h; s.Raise = r; s.GapBottom = gb; s.GapStep = gs;
            Relayout(facesMayChange);
        }

        void AddSection_Click(object sender, RoutedEventArgs e)
        {
            PushUndo();
            var cur = SelSection;
            var s = new Section
            {
                Width = 900, Height = cur.Height, Raise = cur.Raise, GapBottom = cur.GapBottom,
                Root = new Cell { Fill = FillKind.TiltTurnLeft }
            };
            Config.Sections.Insert(_selSection + 1, s);
            _selSection++;
            ClearSelection();
            Relayout();
        }

        void RemoveSection_Click(object sender, RoutedEventArgs e)
        {
            if (Config.Sections.Count < 2) return;
            PushUndo();
            Config.Sections.RemoveAt(_selSection);
            _selSection = Math.Max(0, _selSection - 1);
            ClearSelection();
            Relayout();
        }

        void AddV_Click(object s, RoutedEventArgs e) => AddMullion(SplitKind.Columns, false);
        void AddH_Click(object s, RoutedEventArgs e) => AddMullion(SplitKind.Rows, false);
        void AddSashV_Click(object s, RoutedEventArgs e) => AddMullion(SplitKind.Columns, true);
        void AddSashH_Click(object s, RoutedEventArgs e) => AddMullion(SplitKind.Rows, true);
        void Remove_Click(object s, RoutedEventArgs e) => RemoveSelectedMullion();

        void AddMullion(SplitKind kind, bool inSash)
        {
            var cell = _selCell;
            if (cell == null)
            {
                if (SelSection.Root.IsUnit) cell = SelSection.Root;
                else { Hint("Выберите поле или створку, которую нужно разделить."); return; }
            }
            var layout = LayoutOf(cell);
            var parentBefore = layout.ParentOf(cell);
            var before = Config.ToJson();
            if (!layout.Split(cell, kind, inSash, out var err)) { Hint(err); return; }
            _undo.Push(before);
            ClearSelection();
            Relayout(false);
            // выделяем новый импост, чтобы сразу вписать размер
            var newM = AllMullions.FirstOrDefault(m => m.Parent.Split == kind &&
                ((m.Parent == cell && m.Index == 0) || (m.Parent == parentBefore && m.Parent.Children[m.Index] == cell)));
            if (newM != null) { _selMullParent = newM.Parent; _selMullIndex = newM.Index; }
            Redraw(); UpdateSchemePanel();
        }

        void EmptyBox_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            PushUndo();
            Config.SetEmptyOpening(EmptyBox.IsChecked == true); // размер проёма сохраняется
            ClearSelection();
            Relayout();
        }

        void NameByCode_Click(object sender, RoutedEventArgs e)
        {
            FamilyNameBox.Text = Mirror.WithCode(FamilyNameBox.Text, _geo.Code());
        }

        void Mirror_Click(object sender, RoutedEventArgs e)
        {
            PushUndo();
            Config = Mirror.Apply(Config);
            _selSection = Config.Sections.Count - 1 - _selSection;
            ClearSelection();
            Relayout();
            var n = Mirror.MirrorName(FamilyNameBox.Text);
            if (n != FamilyNameBox.Text)
            {
                FamilyNameBox.Text = n;
                if (SaveAsNewBox.Visibility == Visibility.Visible) SaveAsNewBox.IsChecked = true;
            }
        }

        void RemoveSelectedMullion()
        {
            var m = SelMullion;
            if (m == null) { Hint("Выберите импост."); return; }
            PushUndo();
            LayoutOf(m.Parent).RemoveMullion(m);
            ClearSelection();
            Relayout();
        }

        void FillBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var unit = SelUnit;
            if (_loading || unit == null || FillBox.SelectedIndex < 0) return;
            var f = Fills[FillBox.SelectedIndex].kind;
            if (unit.Cell.Fill == f) return;
            if (f == FillKind.Wall && unit.Cell == SelSection.Root)
            {
                Hint("Вся рама не может быть стеной. «Стена» — для ячейки под окном в Г- и Т-образных блоках.");
                UpdateSchemePanel();
                return;
            }
            PushUndo();
            LayoutOf(unit.Cell).SetFill(unit.Cell, f);
            _selCell = unit.Cell;
            Relayout();
        }

        void GlassMat_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || GlassMatBox.SelectedItem == null) return;
            SetGlassMaterial(GlassMatBox.SelectedItem.ToString());
        }

        void GlassMat_Commit(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SetGlassMaterial(GlassMatBox.Text);
        }

        void SetGlassMaterial(string name)
        {
            var field = SelField;
            if (field == null || string.IsNullOrWhiteSpace(name)) return;
            var def = field.Cell.IsPanel ? Config.PanelMaterial : Config.GlassMaterial;
            var v = name.Trim() == def ? "" : name.Trim();
            if (field.Cell.GlassMaterial == v) return;
            PushUndo();
            field.Cell.GlassMaterial = v;
            Relayout(false);
        }

        void InfillBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var field = SelField;
            if (_loading || field == null || InfillBox.SelectedIndex < 0) return;
            var k = InfillBox.SelectedIndex == 1 ? InfillKind.Panel : InfillKind.Glass;
            if (field.Cell.Infill == k) return;
            PushUndo();
            field.Cell.Infill = k;
            field.Cell.GlassMaterial = ""; // материал по умолчанию для нового вида заполнения
            Relayout();
        }

        // =====================================================================
        // Вкладка «Старт»: шаблоны схем миниатюрами
        // =====================================================================

        void BuildStartTab()
        {
            var host = StartPanel;
            host.Children.Clear();
            host.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 4),
                Text = "Выберите схему — дальше её можно менять на вкладке «Схема». Профиль, проём, материалы и комплектация текущего окна сохраняются."
            });
            string group = null;
            WrapPanel wrap = null;
            for (int i = 0; i < Presets.Names.Length; i++)
            {
                var name = Presets.Names[i];
                var g = name.Contains("Балк") || name.Contains("Дверь") ? "Балконные двери и блоки" : "Окна";
                if (g != group)
                {
                    group = g;
                    host.Children.Add(new TextBlock { Style = (Style)FindResource("Hdr"), Text = g });
                    wrap = new WrapPanel();
                    host.Children.Add(wrap);
                }
                int index = i;
                var tile = new Button { Style = (Style)FindResource("PresetTile"), ToolTip = name };
                var stack = new StackPanel();
                stack.Children.Add(Thumbnail(Presets.Create(index, Config), 100, 86));
                stack.Children.Add(new TextBlock
                {
                    Text = name, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Foreground = _pal.Text,
                    TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                });
                tile.Content = stack;
                tile.Click += (s, e) => ApplyPreset(index);
                wrap.Children.Add(tile);
            }
        }

        void ApplyPreset(int index)
        {
            PushUndo();
            Config = Presets.Create(index, Config);
            _selSection = 0;
            ClearSelection();
            Tabs.SelectedItem = SchemeTab;
            Relayout();
            Sheet.Focus();
        }

        /// <summary>Миниатюра схемы: проём, рамы, створки со знаками открывания.</summary>
        Canvas Thumbnail(WindowConfig cfg, double w, double h)
        {
            var cv = new Canvas { Width = w, Height = h, ClipToBounds = true, IsHitTestVisible = false };
            WindowGeometry geo;
            try { geo = new WindowGeometry(cfg); } catch { return cv; }
            double ow = Math.Max(1, geo.OpeningWidth), oh = Math.Max(1, geo.OpeningHeight);
            double s = Math.Min((w - 8) / ow, (h - 8) / oh);
            double ox = (w - ow * s) / 2, oy = (h + oh * s) / 2;
            Point P(double x, double z) => new Point(ox + x * s, oy - z * s);
            void Rect(MRect r, Brush fill, Brush stroke, double th)
            {
                var e = new Rectangle { Width = Math.Max(0, r.W * s), Height = Math.Max(0, r.H * s), Fill = fill, Stroke = stroke, StrokeThickness = th };
                var p = P(r.X, r.Top); Canvas.SetLeft(e, p.X); Canvas.SetTop(e, p.Y); cv.Children.Add(e);
            }
            void Line2(double x1, double z1, double x2, double z2, Brush b, double th)
            {
                var a = P(x1, z1); var c = P(x2, z2);
                cv.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = c.X, Y2 = c.Y, Stroke = b, StrokeThickness = th });
            }

            var core = new Polygon { Fill = _pal.Opening, Stroke = _pal.Ink, StrokeThickness = 0.8 };
            foreach (var p in WindowGeometry.Polygon(geo.Core)) core.Points.Add(P(p.x, p.z));
            cv.Children.Add(core);
            if (cfg.EmptyOpening) return cv;
            foreach (var sg in geo.Sections)
            {
                var lay = sg.Layout;
                var pg = new PathGeometry { FillRule = FillRule.EvenOdd };
                foreach (var l in lay.FrameLoops)
                {
                    var fig = new PathFigure { StartPoint = P(l[0].x, l[0].z), IsClosed = true, IsFilled = true };
                    for (int k = 1; k < l.Count; k++) fig.Segments.Add(new LineSegment(P(l[k].x, l[k].z), true));
                    pg.Figures.Add(fig);
                }
                cv.Children.Add(new Path { Data = pg, Fill = _pal.Frame, Stroke = _pal.Ink, StrokeThickness = 0.6 });
                foreach (var u in lay.Units.Where(u => u.Cell.IsSash)) Rect(u.R, _pal.Sash, _pal.Ink, 0.5);
                foreach (var f in lay.Fields) Rect(f.R, f.Cell.IsPanel ? _pal.Panel : _pal.Glass, _pal.Ink, 0.4);
                foreach (var u in lay.Units.Where(u => u.Cell.IsSash))
                    OpeningSymbol(u.R.Inset(cfg.SashWidth), u.Cell.Fill, (x1, z1, x2, z2) => Line2(x1, z1, x2, z2, _pal.Ink, 0.6));
            }
            return cv;
        }

        void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void FamilyNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_geo != null) TitleInfo.Text = FamilyName;
        }

        void MullionAxis_Commit(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var m = SelMullion;
            if (m == null || !TryNum(MullionAxisBox.Text, out var axis)) { UpdateSchemePanel(); return; }
            var sg = _geo.Sections.First(g => g.Layout.Mullions.Contains(m));
            double cur = m.Vertical ? m.Axis - sg.Block.X : m.Axis - sg.Block.Y;
            double d = axis - cur;
            if (Math.Abs(d) < 0.01) return;
            PushUndo();
            sg.Layout.MoveMullion(m, d);
            Relayout(false);
        }

        // =====================================================================
        // Вкладка «Проём»
        // =====================================================================

        void BuildOpeningTab()
        {
            var g = OpeningGrid;
            int row = 0;
            void Header(string t)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tb = new TextBlock { Text = t, Style = (Style)FindResource("Hdr") };
                Grid.SetRow(tb, row++); Grid.SetColumnSpan(tb, 2); g.Children.Add(tb);
            }
            void Num(string label, Func<double> get, Action<double> set, double min = 0, string tip = null)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, Style = (Style)FindResource("Lbl"), ToolTip = tip };
                var b = new TextBox();
                var nf = new NumField { Box = b, Get = get, Set = set, Min = min };
                b.LostFocus += (s, e) => CommitOpening(nf);
                b.KeyDown += (s, e) => { if (e.Key == Key.Enter) { CommitOpening(nf); Sheet.Focus(); } };
                Grid.SetRow(l, row); Grid.SetRow(b, row); Grid.SetColumn(b, 1); row++;
                g.Children.Add(l); g.Children.Add(b);
                _openingFields.Add(nf);
            }
            var c = (Func<WindowConfig>)(() => Config);

            Header("Стена");
            Num("Толщина стены", () => c().WallThickness, v => c().WallThickness = v, 50);
            Header("Положение рамы в стене");
            BuildDepthControls(g, ref row);
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Сторону фасада задаёт шаблон WE_Окно (+Y), генерация её не берёт из схемы — флажок скрыт.
            _extPosBox = new CheckBox { Content = "Фасад со стороны +Y семейства", Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
            _extPosBox.Click += (s, e) =>
            {
                if (_loading) return;
                PushUndo();
                Config.ExteriorPositiveY = _extPosBox.IsChecked == true;
                Relayout(false);
            };
            Grid.SetRow(_extPosBox, row++); Grid.SetColumnSpan(_extPosBox, 2); g.Children.Add(_extPosBox);

            Header("Угол здания (наружный, 90°)");
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cornerLbl = new TextBlock { Text = "Угловая сторона", Style = (Style)FindResource("Lbl") };
            _cornerBox = new ComboBox();
            foreach (var n in new[] { "Нет", "Слева (с фасада)", "Справа (с фасада)" }) _cornerBox.Items.Add(n);
            _cornerBox.SelectionChanged += (s, e) =>
            {
                if (_loading || _cornerBox.SelectedIndex < 0) return;
                var k = (CornerSide)_cornerBox.SelectedIndex;
                if (k == Config.Corner) return;
                PushUndo(); Config.Corner = k; Relayout();
            };
            Grid.SetRow(cornerLbl, row); Grid.SetRow(_cornerBox, row); Grid.SetColumn(_cornerBox, 1); row++;
            g.Children.Add(cornerLbl); g.Children.Add(_cornerBox);
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _cornerLeadBox = new CheckBox
            {
                Content = "Ведущее окно угла", Margin = new Thickness(0, 0, 0, 6),
                ToolTip = "У ведущего окна подоконник, отлив и верхние откосы проходят до угла, у второго — примыкают к ним. Ведущим должно быть ровно одно окно из пары."
            };
            _cornerLeadBox.Click += (s, e) => { if (_loading) return; PushUndo(); Config.CornerLead = _cornerLeadBox.IsChecked == true; Relayout(false); };
            Grid.SetRow(_cornerLeadBox, row++); Grid.SetColumnSpan(_cornerLeadBox, 2); g.Children.Add(_cornerLeadBox);
            Num("Угловой соединитель", () => c().CornerPostSize, v => c().CornerPostSize = v, 20, "Сторона квадрата реального углового соединителя, мм");
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _cornerText = new TextBlock { Foreground = _pal.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetRow(_cornerText, row++); Grid.SetColumnSpan(_cornerText, 2); g.Children.Add(_cornerText);

            Header("Монтажные зазоры (низ — у секции)");
            Num("Боковой", () => c().GapSide, v => c().GapSide = v);
            Num("Верхний", () => c().GapTop, v => c().GapTop = v);

            Header("Четверть (0 — без четверти)");
            Num("Боковая", () => c().QuarterSide, v => c().QuarterSide = v);
            Num("Верхняя", () => c().QuarterTop, v => c().QuarterTop = v);
            Num("Глубина", () => c().QuarterDepth, v => c().QuarterDepth = v, 0, "Обычно толщина наружного слоя стены");
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _overlapText = new TextBlock { Foreground = _pal.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            Grid.SetRow(_overlapText, row++); Grid.SetColumnSpan(_overlapText, 2); g.Children.Add(_overlapText);

            Header("Профиль");
            Num("Рама, ширина", () => c().FrameWidth, v => c().FrameWidth = v, 10);
            Num("Рама, глубина", () => c().FrameDepth, v => c().FrameDepth = v, 10);
            Num("Импост, ширина", () => c().MullionWidth, v => c().MullionWidth = v, 10);
            Num("Импост створки", () => c().SashMullionWidth, v => c().SashMullionWidth = v, 10);
            Num("Створка, ширина", () => c().SashWidth, v => c().SashWidth = v, 10);
            Num("Створка, глубина", () => c().SashDepth, v => c().SashDepth = v, 10);
            Num("Стеклопакет", () => c().GlassThickness, v => c().GlassThickness = v, 4);
            Num("Панель (глухая)", () => c().PanelThickness, v => c().PanelThickness = v, 4);
            Num("Соединительный профиль", () => c().ConnectorWidth, v => c().ConnectorWidth = v, 0, "Между секциями блока");
        }

        // ---------- Положение рамы по глубине ----------

        ComboBox _anchorBox, _cornerBox;
        CheckBox _cornerLeadBox;
        TextBlock _cornerText;
        TextBlock _depthInfo;
        Canvas _depthCanvas;
        static readonly string[] AnchorNames = { "От наружной грани стены", "От четверти", "По оси стены", "От внутренней грани стены" };

        void BuildDepthControls(Grid g, ref int rowRef)
        {
            // локальная функция не может захватить ref-параметр: считаем в копии и возвращаем в конце
            int row = rowRef;
            void Row(UIElement label, UIElement ctrl, bool span = false)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (span) { Grid.SetRow(ctrl, row); Grid.SetColumnSpan(ctrl, 2); g.Children.Add(ctrl); row++; return; }
                Grid.SetRow(label, row); Grid.SetRow(ctrl, row); Grid.SetColumn(ctrl, 1); row++;
                g.Children.Add(label); g.Children.Add(ctrl);
            }

            _depthCanvas = new Canvas { Height = 190, Background = Brushes.Transparent, ClipToBounds = true };
            _depthCanvas.SizeChanged += (s, e) => DrawDepth();
            Row(null, new Border { Child = _depthCanvas, Background = (Brush)FindResource("CardBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(2), Margin = new Thickness(0, 0, 0, 8) }, true);

            _anchorBox = new ComboBox();
            foreach (var n in AnchorNames) _anchorBox.Items.Add(n);
            _anchorBox.SelectionChanged += (s, e) =>
            {
                if (_loading || _anchorBox.SelectedIndex < 0) return;
                var a = (FrameAnchor)_anchorBox.SelectedIndex;
                if (a == Config.Anchor) return;
                PushUndo();
                Config.SetAnchor(a); // рама остаётся на месте, меняется только отсчёт
                Relayout(false);
            };
            // названия привязок длинные — список на всю ширину, подпись строкой выше
            Row(null, new TextBlock { Text = "Привязка", Style = (Style)FindResource("Lbl") }, true);
            Row(null, _anchorBox, true);

            // смещение: ◀ наружу, ▶ вглубь
            var box = new TextBox { Margin = new Thickness(4, 0, 4, 0) };
            var nf = new NumField { Box = box, Get = () => Config.FrameOffset, Set = v => Config.FrameOffset = v };
            box.LostFocus += (s, e) => CommitOpening(nf);
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { CommitOpening(nf); Sheet.Focus(); } };
            _openingFields.Add(nf);
            Button Step(string text, int dir, string tip)
            {
                var b = new Button { Content = text, Width = 30, Margin = new Thickness(0), Padding = new Thickness(0), ToolTip = tip };
                b.Click += (s, e) =>
                {
                    double step = Keyboard.Modifiers == ModifierKeys.Shift ? 50 : 10;
                    PushUndo();
                    Config.FrameOffset += dir * step;
                    Relayout(false);
                };
                return b;
            }
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var left = Step("◀", -1, "Наружу на 10 мм (Shift — 50 мм)");
            var right = Step("▶", +1, "Вглубь на 10 мм (Shift — 50 мм)");
            DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(left); dock.Children.Add(right); dock.Children.Add(box);
            Row(new TextBlock { Text = "Смещение: + вглубь, − наружу", Style = (Style)FindResource("Lbl"), TextWrapping = TextWrapping.Wrap }, dock);

            _depthInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
            Row(null, _depthInfo, true);
            rowRef = row;
        }

        /// <summary>Схема: план у откоса. Слева улица, справа помещение, снизу стена, сверху проём.</summary>
        void DrawDepth()
        {
            var cv = _depthCanvas;
            if (cv == null || _geo == null) return;
            cv.Children.Clear();
            double W = cv.ActualWidth, H = cv.ActualHeight;
            if (W < 50) return;
            var c = Config;
            double t = c.WallThickness, B = c.BlockFromExterior, FD = c.FrameDepthEff;
            double pad = 28;
            double s = (W - 2 * pad) / Math.Max(t, 1);
            double yEdge = H - 55; // грань проёма (откос) — всё ниже стена
            double X(double d) => pad + d * s;
            double Y(double across) => yEdge - across * s; // across: от грани проёма внутрь проёма, мм

            void Box(double d0, double d1, double a0, double a1, Brush fill, Brush stroke, bool dashed = false)
            {
                var r = new Rectangle
                {
                    Width = Math.Max(0, Math.Abs(X(d1) - X(d0))), Height = Math.Max(0, Math.Abs(Y(a1) - Y(a0))),
                    Fill = fill, Stroke = stroke, StrokeThickness = 1
                };
                if (dashed) r.StrokeDashArray = new DoubleCollection { 3, 2 };
                Canvas.SetLeft(r, Math.Min(X(d0), X(d1))); Canvas.SetTop(r, Math.Min(Y(a0), Y(a1)));
                cv.Children.Add(r);
            }
            void Text(string txt, double x, double y, Brush b, double size = 10)
            {
                var tb = new TextBlock { Text = txt, FontSize = size, Foreground = b };
                Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); cv.Children.Add(tb);
            }

            var wall = _pal.Wall; var ink = _pal.Ink;
            // стена с четвертью
            Box(0, t, -(H - yEdge) / s, 0, wall, ink);
            if (_geo.HasQuarter && c.QuarterDepth > 0) Box(0, Math.Min(c.QuarterDepth, t), 0, c.QuarterSide, wall, ink);

            var rf = c.RevealFor("Левый");
            double gap = c.GapSideEff;
            if (!c.EmptyOpening)
            {
                // рама и стекло
                Box(B, B + FD, gap, gap + c.FrameWidth, _pal.Frame, ink);
                double mid = B + FD / 2;
                Box(mid - c.GlassThickness / 2, mid + c.GlassThickness / 2, gap + c.FrameWidth, gap + c.FrameWidth + 120, _pal.Glass, ink);
            }
            else
            {
                var l = new Line { X1 = X(B), X2 = X(B), Y1 = Y(0), Y2 = Y(150), Stroke = _pal.Muted, StrokeDashArray = new DoubleCollection { 4, 3 } };
                cv.Children.Add(l);
            }
            if (rf.In.Enabled) Box(B + FD, t, 0, rf.In.Thickness, _pal.Sash, ink);
            if (rf.Out.Enabled)
            {
                double q = _geo.HasQuarter ? c.QuarterSide : 0;
                double dEnd = _geo.HasQuarter && c.QuarterDepth > 0 ? Math.Min(c.QuarterDepth, B) : B;
                Box(0, dEnd, q, q + rf.Out.Thickness, _pal.Panel, ink);
            }

            Text("улица", 6, 4, _pal.Muted); Text("помещение", W - 72, 4, _pal.Muted);
            // размеры: до рамы и после рамы
            double yd = H - 22;
            void Dim(double d0, double d1, string label)
            {
                if (Math.Abs(d1 - d0) < 0.5) return;
                cv.Children.Add(new Line { X1 = X(d0), X2 = X(d1), Y1 = yd, Y2 = yd, Stroke = ink, StrokeThickness = 0.8 });
                cv.Children.Add(new Line { X1 = X(d0), X2 = X(d0), Y1 = yd - 4, Y2 = yd + 4, Stroke = ink });
                cv.Children.Add(new Line { X1 = X(d1), X2 = X(d1), Y1 = yd - 4, Y2 = yd + 4, Stroke = ink });
                Text(label, (X(d0) + X(d1)) / 2 - 12, yd - 15, ink);
            }
            Dim(0, B, F(B));
            if (FD > 0) Dim(B, B + FD, F(FD));
            Dim(B + FD, t, F(t - B - FD));
        }

        void LoadOpeningFields()
        {
            _loading = true;
            foreach (var f in _openingFields) f.Box.Text = F(f.Get());
            _extPosBox.IsChecked = Config.ExteriorPositiveY;
            _anchorBox.SelectedIndex = (int)Config.Anchor;
            _cornerBox.SelectedIndex = (int)Config.Corner;
            _cornerLeadBox.IsChecked = Config.CornerLead;
            _cornerLeadBox.IsEnabled = Config.Corner != CornerSide.None;
            _cornerText.Text = Config.Corner == CornerSide.None ? "" :
                (Config.EmptyOpening
                    ? "Проём доходит до угла здания. Угол стен вырежет команда «Угловое окно»."
                    : $"Рама кончается в {Config.CornerGap:0} мм от угла: {Config.BlockFromExterior:0} (рама от наружной грани) + {Config.CornerPostSize:0} (соединитель). ") +
                " Второе окно у угла — в соседней стене, с той же толщиной стены и положением рамы; «Угловое окно» выровняет оба и поставит узел.";
            _depthInfo.Text = $"От наружной грани до рамы: {Config.BlockFromExterior:0} мм, от рамы до внутренней грани: {Config.BlockToInterior:0} мм" +
                (Config.BlockFromExterior < 0 || Config.BlockToInterior < 0 ? " — рама выходит из стены!" : "");
            _depthInfo.Foreground = Config.BlockFromExterior < 0 || Config.BlockToInterior < 0 ? _pal.Error : _pal.Muted;
            DrawDepth();
            _overlapText.Text = _geo.HasQuarter
                ? $"Заход рамы за четверть: {_geo.QuarterOverlap:0} мм{(_geo.QuarterOverlap < 10 ? " — мало, увеличьте четверть или уменьшите зазор" : "")}"
                : "";
            _loading = false;
        }

        void CommitOpening(NumField f)
        {
            if (_loading) return;
            if (!TryNum(f.Box.Text, out var v)) { f.Box.Text = F(f.Get()); return; }
            v = Math.Max(f.Min, v);
            if (Math.Abs(v - f.Get()) < 1e-9) { f.Box.Text = F(v); return; }
            PushUndo();
            f.Set(v);
            Relayout(false);
        }

        // =====================================================================
        // Вкладки «Комплектация» и «Материалы» — строятся из модели
        // =====================================================================

        void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != Tabs || _geo == null) return;
            RebuildCurrentDynamicTab();
        }

        void RebuildCurrentDynamicTab()
        {
            if (Tabs.SelectedItem == KitTab) BuildKitTab();
            else if (Tabs.SelectedItem == MatTab) BuildMaterialsTab();
        }

        Grid KitGrid(StackPanel host, params string[] headers)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var _ in headers) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < headers.Length; i++)
            {
                var t = new TextBlock { Text = headers[i], Foreground = _pal.Muted, FontSize = 11, Margin = new Thickness(4, 0, 4, 2) };
                Grid.SetColumn(t, i); g.Children.Add(t);
            }
            host.Children.Add(g);
            return g;
        }

        void AddCell(Grid g, int row, int col, UIElement el)
        {
            while (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(el, row); Grid.SetColumn(el, col);
            if (el is FrameworkElement fe) fe.Margin = new Thickness(4, 0, 4, 4);
            g.Children.Add(el);
        }

        TextBox SmallNum(Func<double> get, Action<double> set, double min = 0)
        {
            var b = new TextBox { Width = 50, Text = F(get()), Margin = new Thickness(0) };
            void Commit()
            {
                if (!TryNum(b.Text, out var v)) { b.Text = F(get()); return; }
                v = Math.Max(min, v);
                if (Math.Abs(v - get()) < 1e-9) return;
                PushUndo(); set(v); Relayout(false);
            }
            b.LostFocus += (s, e) => Commit();
            b.KeyDown += (s, e) => { if (e.Key == Key.Enter) Commit(); };
            return b;
        }

        CheckBox Check(Func<bool> get, Action<bool> set, bool rebuild = false)
        {
            var cb = new CheckBox { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center };
            cb.Click += (s, e) =>
            {
                PushUndo(); set(cb.IsChecked == true); Relayout(rebuild);
            };
            return cb;
        }

        void BuildKitTab()
        {
            var host = KitPanel;
            host.Children.Clear();
            var hdr = (Style)FindResource("Hdr");

            for (int i = 0; i < Config.Sections.Count; i++)
            {
                var s = Config.Sections[i];
                host.Children.Add(new TextBlock
                {
                    Style = hdr,
                    Text = Config.Sections.Count > 1 ? $"Секция {i + 1}{(s.HasDoor ? " (дверная)" : "")}" : "Подоконник и отлив"
                });

                var g = KitGrid(host, "Элемент", "Вкл.", "Толщ.", "Вынос", "Под раму", "В стену");
                AddCell(g, 1, 0, new TextBlock { Text = "Подоконник", ToolTip = "Подоконник внутренний", Foreground = _pal.Text, VerticalAlignment = VerticalAlignment.Center });
                AddCell(g, 1, 1, Check(() => s.SillIn.Enabled, v => s.SillIn.Enabled = v));
                AddCell(g, 1, 2, SmallNum(() => s.SillIn.Thickness, v => s.SillIn.Thickness = v, 1));
                AddCell(g, 1, 3, SmallNum(() => s.SillIn.Projection, v => s.SillIn.Projection = v));
                AddCell(g, 1, 4, SmallNum(() => s.SillIn.Inset, v => s.SillIn.Inset = v));
                AddCell(g, 1, 5, SmallNum(() => s.SillIn.Ear, v => s.SillIn.Ear = v));

                // выбор наружного элемента — отдельной строкой на всю ширину: в колонке названий он не помещается
                var kind = new ComboBox { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
                kind.Items.Add("Снаружи: нет"); kind.Items.Add("Снаружи: отлив"); kind.Items.Add("Снаружи: подоконник");
                kind.SelectedIndex = (int)s.OuterKind;
                kind.SelectionChanged += (o, e) =>
                {
                    var k = (OuterSillKind)kind.SelectedIndex;
                    if (k == s.OuterKind) return;
                    PushUndo(); s.OuterKind = k; Relayout(); // вкладка перестроится: поля другого элемента
                };
                AddCell(g, 2, 0, kind);
                Grid.SetColumnSpan(kind, g.ColumnDefinitions.Count);
                var o2 = s.Outer;
                if (o2 != null)
                {
                    bool drip = s.OuterKind == OuterSillKind.Drip;
                    AddCell(g, 3, 0, new TextBlock
                    {
                        Text = drip ? "Отлив" : "Наружный", Foreground = _pal.Text,
                        ToolTip = drip ? "Отлив" : "Подоконник наружный",
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    AddCell(g, 3, 2, SmallNum(() => o2.Thickness, v => o2.Thickness = v, 0.5));
                    AddCell(g, 3, 3, SmallNum(() => o2.Projection, v => o2.Projection = v));
                    AddCell(g, 3, 4, SmallNum(() => o2.Inset, v => o2.Inset = v));
                    AddCell(g, 3, 5, SmallNum(() => o2.Ear, v => o2.Ear = v));
                }
            }

            host.Children.Add(new TextBlock { Style = hdr, Text = "Откосы — каждая грань отдельно" });
            var rg = KitGrid(host, "Грань", "Внутр.", "Толщ.", "Наруж.", "Толщ.");
            int row = 1;
            foreach (var f in _geo.CoreFaces)
            {
                if ((Config.Corner == CornerSide.Left && f.Kind == FaceKind.Left) ||
                    (Config.Corner == CornerSide.Right && f.Kind == FaceKind.Right)) continue; // у угла стены нет — откоса нет
                var rf = Config.RevealFor(f.Name);
                bool hasExt = _geo.ExteriorFaces.Any(x => x.Name == f.Name);
                AddCell(rg, row, 0, new TextBlock { Text = f.Name, Foreground = _pal.Text, VerticalAlignment = VerticalAlignment.Center });
                AddCell(rg, row, 1, Check(() => rf.In.Enabled, v => rf.In.Enabled = v));
                AddCell(rg, row, 2, SmallNum(() => rf.In.Thickness, v => rf.In.Thickness = v, 1));
                if (hasExt)
                {
                    AddCell(rg, row, 3, Check(() => rf.Out.Enabled, v => rf.Out.Enabled = v));
                    AddCell(rg, row, 4, SmallNum(() => rf.Out.Thickness, v => rf.Out.Thickness = v, 1));
                }
                row++;
            }
            host.Children.Add(new TextBlock
            {
                Text = "Размеры, мм. Подоконник и отлив ставятся под каждым участком низа рамы, кроме участков под дверью. Материалы — на вкладке «Материалы». Элементы ставятся в модель отдельными семействами «WE_Элемент окна» (категория «Окна») и обновляются кнопкой «Комплектация».",
                TextWrapping = TextWrapping.Wrap, Foreground = _pal.Muted, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
            });
        }

        void BuildMaterialsTab()
        {
            var host = MatPanel;
            host.Children.Clear();
            host.Children.Add(new TextBlock { Style = (Style)FindResource("Hdr"), Margin = new Thickness(0, 0, 0, 4), Text = "Стекло по умолчанию" });
            host.Children.Add(MaterialCombo(() => Config.GlassMaterial, v => Config.GlassMaterial = v));
            host.Children.Add(new TextBlock { Style = (Style)FindResource("Hdr"), Text = "Панель по умолчанию" });
            host.Children.Add(MaterialCombo(() => Config.PanelMaterial, v => Config.PanelMaterial = v));
            if (Config.Corner != CornerSide.None && !Config.EmptyOpening)
            {
                host.Children.Add(new TextBlock { Style = (Style)FindResource("Hdr"), Text = "Угловой соединитель" });
                host.Children.Add(MaterialCombo(() => Config.CornerPostMaterial, v => Config.CornerPostMaterial = v));
            }

            host.Children.Add(new TextBlock { Style = (Style)FindResource("Hdr"), Text = "Элементы окна" });
            foreach (var slot in _geo.Materials)
            {
                host.Children.Add(new TextBlock { Text = slot.Title, Foreground = _pal.Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 2) });
                host.Children.Add(MaterialCombo(slot.Get, slot.Set));
            }
            host.Children.Add(new TextBlock
            {
                Text = "Материалы ищутся в проекте по имени; если такого нет, создаётся новый. Материалы зашиты в семейство и меняются только здесь, в редакторе.",
                TextWrapping = TextWrapping.Wrap, Foreground = _pal.Muted, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
            });
        }

        ComboBox MaterialCombo(Func<string> get, Action<string> set)
        {
            var cb = new ComboBox { IsEditable = true };
            _loading = true;
            FillMaterialCombo(cb, get());
            _loading = false;
            void Commit(string v)
            {
                if (_loading || string.IsNullOrWhiteSpace(v)) return;
                v = v.Trim();
                if (v == get()) return;
                PushUndo(); set(v); Relayout(false);
            }
            cb.SelectionChanged += (s, e) => { if (cb.SelectedItem != null) Commit(cb.SelectedItem.ToString()); };
            cb.LostFocus += (s, e) => Commit(cb.Text);
            return cb;
        }

        // =====================================================================
        // Отрисовка
        // =====================================================================

        Point ToScreen(double x, double z) => new Point(_ox + (x - _wx0) * _scale, _oy - (z - _wz0) * _scale);
        (double x, double z) ToModel(Point p) => ((p.X - _ox) / _scale + _wx0, (_oy - p.Y) / _scale + _wz0);

        static SolidColorBrush Rgb(byte r, byte g, byte b, byte a = 255) => new SolidColorBrush(Color.FromArgb(a, r, g, b));

        void Redraw()
        {
            Sheet.Children.Clear();
            double cw = Sheet.ActualWidth, ch = Sheet.ActualHeight;
            if (cw < 10 || ch < 10 || _geo == null) return;

            // область: проём + стена вокруг + место под размеры
            const double wallM = 220, dimM = 260;
            _wx0 = -wallM - dimM; double wx1 = _geo.OpeningWidth + wallM;
            _wz0 = -wallM - dimM; double wz1 = _geo.OpeningHeight + wallM;
            _scale = Math.Min((cw - 2 * Pad) / (wx1 - _wx0), (ch - 2 * Pad) / (wz1 - _wz0));
            _ox = Pad + ((cw - 2 * Pad) - (wx1 - _wx0) * _scale) / 2;
            _oy = ch - Pad - ((ch - 2 * Pad) - (wz1 - _wz0) * _scale) / 2;

            var stroke = _pal.Ink;
            var wall = _pal.Wall;
            var frameFill = _pal.Frame;
            var sashFill = _pal.Sash;
            var glass = _pal.Glass;
            var accent = _pal.Accent;

            // стена и проём
            // стена; со стороны угла здания стены нет — дальше видна соседняя стена под углом
            double wallX0 = Config.Corner == CornerSide.Left ? 0 : -wallM;
            double wallX1 = Config.Corner == CornerSide.Right ? _geo.OpeningWidth : _geo.OpeningWidth + wallM;
            AddRect(new MRect(wallX0, -wallM, wallX1 - wallX0, _geo.OpeningHeight + 2 * wallM), wall, null, 0);
            AddPolygon(WindowGeometry.Polygon(_geo.Core), _pal.Opening, stroke, 1, false);
            if (_geo.HasQuarter) AddPolygon(WindowGeometry.Polygon(_geo.Exterior), null, _pal.Quarter, 1.2, true);

            // наружные подоконники/отливы (видны с фасада) и внутренние (пунктиром)
            foreach (var b in _geo.Boxes.Where(b => b.Kind == "Отлив" || b.Kind == "Подоконник наружный"))
                AddRect(new MRect(b.X0, b.Z0 - Math.Max(0, 25 - b.Thickness), b.X1 - b.X0, Math.Max(b.Thickness, 25)), _pal.OuterSill, stroke, 0.6);
            foreach (var b in _geo.Boxes.Where(b => b.Kind == "Подоконник внутренний"))
                AddRect(new MRect(b.X0, b.Z0, b.X1 - b.X0, b.Z1 - b.Z0), null, _pal.InnerSill, 0.8, true);

            foreach (var sg in _geo.Sections)
            {
                var lay = sg.Layout;
                if (Config.EmptyOpening)
                {
                    if (_geo.Sections.Count > 1)
                    {
                        var t0 = ToScreen(sg.Block.X + sg.Block.W / 2, sg.Block.Top);
                        AddText($"С{sg.Index + 1}", new Point(t0.X, t0.Y - 10), 11, stroke, false);
                    }
                    continue; // в пустом проёме только проём и комплектация
                }
                AddLoops(lay.FrameLoops, frameFill, stroke, 1.0);
                foreach (var u in lay.Units)
                {
                    if (u.Cell.IsWall)
                    {
                        var c0 = ToScreen(u.R.X + u.R.W / 2, u.R.Y + u.R.H / 2);
                        AddText("стена", c0, 10, _pal.Muted, false);
                        continue;
                    }
                    if (u.Cell.IsSash) AddRect(u.R, sashFill, stroke, 0.8);
                }
                foreach (var f in lay.Fields)
                {
                    var pane = _geo.Panes.First(p => p.Cell == f.Cell);
                    var gl = f.Cell.IsPanel ? _pal.Panel
                           : string.IsNullOrEmpty(f.Cell.GlassMaterial) ? glass : _pal.GlassCustom;
                    var pr = AddRect(f.R, gl, stroke, 0.6);
                    if (f.Cell.IsPanel) AddRect(f.R.Inset(Math.Min(25, Math.Min(f.R.W, f.R.H) / 4)), null, _pal.PanelLine, 0.6);
                    var c = ToScreen(f.R.X + f.R.W / 2, f.R.Y + f.R.H / 2);
                    AddText($"{f.R.W:0}×{f.R.H:0}", new Point(c.X, c.Y + 10), 10, _pal.Muted, true);
                    AddBadge(pane.Number.ToString(), new Point(c.X, c.Y - 10));
                }
                foreach (var u in lay.Units.Where(u => u.Cell.IsSash))
                    DrawOpeningSymbol(u.R.Inset(Config.SashWidth), u.Cell.Fill, stroke);

                if (_geo.Sections.Count > 1)
                {
                    var top = ToScreen(sg.Block.X + sg.Block.W / 2, sg.Block.Top);
                    AddText($"С{sg.Index + 1}", new Point(top.X, top.Y - 10), 11, stroke, false);
                }
            }

            // угол здания и угловой соединитель
            if (Config.Corner != CornerSide.None)
            {
                double xc = _geo.CornerX;
                AddLine(xc, -wallM, xc, _geo.OpeningHeight + wallM, accent, 2);
                var lbl = ToScreen(xc, _geo.OpeningHeight + wallM * 0.6);
                AddText("угол", lbl, 10, accent, true);
                var post = _geo.CornerPostFacade;
                if (post.HasValue)
                {
                    AddRect(post.Value, _pal.CornerPost, stroke, 1);
                    var pc = ToScreen(post.Value.X + post.Value.W / 2, post.Value.Y + post.Value.H / 2);
                    var tb = AddText($"соединитель {Config.CornerPostSize:0}", pc, 9, stroke, true);
                    tb.RenderTransform = new RotateTransform(-90);
                    tb.RenderTransformOrigin = new Point(0.5, 0.5);
                }
            }

            // выделение
            if (_selCell == null && _selMullParent == null)
                AddRect(_geo.Sections[_selSection].Block, null, accent, 2.5);
            var sf = SelField; var su = SelUnit;
            if (sf != null && (su == null || sf.Cell != su.Cell || !su.Cell.IsSash)) AddRect(sf.R, _pal.AccentFill, accent, 2.5);
            else if (su != null) AddRect(su.R, _pal.AccentFillLight, accent, 2.5);
            var sm = SelMullion;
            if (sm != null) AddRect(sm.R, accent, accent, 1);

            DrawDimensions(stroke);
        }

        void DrawOpeningSymbol(MRect g, FillKind f, Brush stroke) =>
            OpeningSymbol(g, f, (x1, z1, x2, z2) => AddLine(x1, z1, x2, z2, stroke, 0.8));

        static void OpeningSymbol(MRect g, FillKind f, Action<double, double, double, double> line)
        {
            // вершина угла — сторона петель
            void Tri(double ax, double ay, double bx, double by, double cx, double cy)
            {
                line(ax, ay, bx, by);
                line(bx, by, cx, cy);
            }
            bool left = f == FillKind.TurnLeft || f == FillKind.TiltTurnLeft || f == FillKind.DoorLeft || f == FillKind.DoorTiltLeft;
            bool right = f == FillKind.TurnRight || f == FillKind.TiltTurnRight || f == FillKind.DoorRight || f == FillKind.DoorTiltRight;
            bool tilt = f == FillKind.Tilt || f == FillKind.TiltTurnLeft || f == FillKind.TiltTurnRight || f == FillKind.DoorTiltLeft || f == FillKind.DoorTiltRight;
            double midY = g.Y + g.H / 2, midX = g.X + g.W / 2;
            if (left) Tri(g.Right, g.Top, g.X, midY, g.Right, g.Y);
            if (right) Tri(g.X, g.Top, g.Right, midY, g.X, g.Y);
            if (tilt) Tri(g.X, g.Top, midX, g.Y, g.Right, g.Top);
        }

        void DrawDimensions(Brush stroke)
        {
            double y1 = -140, y2 = -230; // ряды размеров под проёмом, мм
            // по рамам секций
            foreach (var sg in _geo.Sections) DimH(sg.Block.X, sg.Block.Right, y1, stroke);
            // проём целиком
            DimH(0, _geo.OpeningWidth, y2, stroke);
            DimV(0, _geo.OpeningHeight, -230, stroke);
            // высота подоконной части стены
            foreach (var iv in _geo.Core.Where(i => i.B > 1))
                DimV(0, iv.B, (iv.X0 + iv.X1) / 2, stroke);
        }

        void DimH(double x0, double x1, double z, Brush stroke)
        {
            var a = ToScreen(x0, z); var b = ToScreen(x1, z);
            Sheet.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = stroke, StrokeThickness = 0.7 });
            Tick(a); Tick(b);
            AddText(F(x1 - x0), new Point((a.X + b.X) / 2, a.Y - 8), 11, stroke, true);
        }

        void DimV(double z0, double z1, double x, Brush stroke)
        {
            var a = ToScreen(x, z0); var b = ToScreen(x, z1);
            Sheet.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = stroke, StrokeThickness = 0.7 });
            Tick(a); Tick(b);
            var tb = AddText(F(z1 - z0), new Point(a.X - 9, (a.Y + b.Y) / 2), 11, stroke, true);
            tb.RenderTransform = new RotateTransform(-90);
            tb.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        void AddLoops(List<List<(double x, double z)>> loops, Brush fill, Brush stroke, double th)
        {
            var geo = new PathGeometry { FillRule = FillRule.EvenOdd };
            foreach (var l in loops)
            {
                var fig = new PathFigure { StartPoint = ToScreen(l[0].x, l[0].z), IsClosed = true, IsFilled = true };
                for (int i = 1; i < l.Count; i++) fig.Segments.Add(new LineSegment(ToScreen(l[i].x, l[i].z), true));
                geo.Figures.Add(fig);
            }
            Sheet.Children.Add(new Path { Data = geo, Fill = fill, Stroke = stroke, StrokeThickness = th, IsHitTestVisible = false });
        }

        void Tick(Point p) => Sheet.Children.Add(new Line { X1 = p.X - 4, Y1 = p.Y + 4, X2 = p.X + 4, Y2 = p.Y - 4, Stroke = _pal.Ink, StrokeThickness = 1 });

        Rectangle AddRect(MRect r, Brush fill, Brush stroke, double th, bool dashed = false)
        {
            var p1 = ToScreen(r.X, r.Top);
            var rect = new Rectangle
            {
                Width = Math.Max(0, r.W * _scale), Height = Math.Max(0, r.H * _scale),
                Fill = fill, Stroke = stroke, StrokeThickness = th, IsHitTestVisible = false
            };
            if (dashed) rect.StrokeDashArray = new DoubleCollection { 4, 3 };
            Canvas.SetLeft(rect, p1.X); Canvas.SetTop(rect, p1.Y);
            Sheet.Children.Add(rect);
            return rect;
        }

        void AddPolygon(List<(double x, double z)> pts, Brush fill, Brush stroke, double th, bool dashed)
        {
            var poly = new Polygon { Fill = fill, Stroke = stroke, StrokeThickness = th, IsHitTestVisible = false };
            foreach (var p in pts) poly.Points.Add(ToScreen(p.x, p.z));
            if (dashed) poly.StrokeDashArray = new DoubleCollection { 6, 3 };
            Sheet.Children.Add(poly);
        }

        void AddLine(double x1, double y1, double x2, double y2, Brush stroke, double th)
        {
            var p = ToScreen(x1, y1); var q = ToScreen(x2, y2);
            Sheet.Children.Add(new Line { X1 = p.X, Y1 = p.Y, X2 = q.X, Y2 = q.Y, Stroke = stroke, StrokeThickness = th, IsHitTestVisible = false });
        }

        TextBlock AddText(string s, Point center, double size, Brush brush, bool bg)
        {
            var tb = new TextBlock { Text = s, FontSize = size, Foreground = brush, IsHitTestVisible = false };
            if (bg) tb.Background = _pal.TextBg;
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, center.X - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, center.Y - tb.DesiredSize.Height / 2);
            Sheet.Children.Add(tb);
            return tb;
        }

        void AddBadge(string s, Point center)
        {
            const double d = 18;
            var e = new Ellipse { Width = d, Height = d, Fill = _pal.Badge, IsHitTestVisible = false };
            Canvas.SetLeft(e, center.X - d / 2); Canvas.SetTop(e, center.Y - d / 2);
            Sheet.Children.Add(e);
            AddText(s, center, 10, Brushes.White, false);
        }

        // =====================================================================
        // Мышь и клавиатура
        // =====================================================================

        void Sheet_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        MullionInfo HitMullion(Point p)
        {
            var (x, z) = ToModel(p);
            double tol = 6 / _scale;
            var all = AllMullions.ToList();
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var m = all[i];
                var r = m.Vertical
                    ? new MRect(m.R.X - tol, m.R.Y, m.R.W + 2 * tol, m.R.H)
                    : new MRect(m.R.X, m.R.Y - tol, m.R.W, m.R.H + 2 * tol);
                if (r.Contains(x, z)) return m;
            }
            return null;
        }

        void Sheet_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Sheet.Focus();
            var p = e.GetPosition(Sheet);
            var (x, z) = ToModel(p);
            var m = Config.EmptyOpening ? null : HitMullion(p);
            ClearSelection();
            if (m != null)
            {
                _selMullParent = m.Parent; _selMullIndex = m.Index;
                _selSection = _geo.Sections.First(s => s.Layout.Mullions.Contains(m)).Index;
                _dragging = true;
                _dragStart = p;
                _dragStartJson = Config.ToJson();
                _dragMullionFlatIndex = AllMullions.ToList().IndexOf(m);
                Sheet.CaptureMouse();
            }
            else
            {
                var sg = _geo.SectionAt(x, z);
                if (sg != null)
                {
                    _selSection = sg.Index;
                    // поле → створка по её профилю или стена → иначе выделена секция
                    var f = sg.Layout.Fields.FirstOrDefault(l => l.R.Contains(x, z));
                    var u = sg.Layout.Units.FirstOrDefault(l => l.R.Contains(x, z));
                    _selCell = Config.EmptyOpening ? null : f?.Cell ?? u?.Cell;
                }
            }
            Redraw(); UpdateSchemePanel();
        }

        void Sheet_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(Sheet);
            if (!_dragging)
            {
                var hm = HitMullion(p);
                Sheet.Cursor = hm == null ? Cursors.Arrow : hm.Vertical ? Cursors.SizeWE : Cursors.SizeNS;
                return;
            }

            // каждый раз от исходного состояния — без накопления ошибки
            var cfg = WindowConfig.FromJson(_dragStartJson);
            var geo = new WindowGeometry(cfg);
            var m = geo.Sections.SelectMany(s => s.Layout.Mullions).ElementAt(_dragMullionFlatIndex);
            double d = m.Vertical ? (p.X - _dragStart.X) / _scale : -(p.Y - _dragStart.Y) / _scale;
            d = Math.Round(d / 5) * 5;
            geo.Sections.First(s => s.Layout.Mullions.Contains(m)).Layout.MoveMullion(m, d);

            Config = cfg;
            _selMullParent = m.Parent; _selMullIndex = m.Index;
            Relayout(false);
        }

        void Sheet_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Sheet.ReleaseMouseCapture();
            if (Config.ToJson() != _dragStartJson) _undo.Push(_dragStartJson);
            RebuildCurrentDynamicTab(); // объекты конфигурации заменены
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
            if (Config.EmptyOpening && e.Key != Key.Z) return;
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
            else if (Tabs.SelectedItem != SchemeTab) return;
            else if (e.Key == Key.V) { AddMullion(SplitKind.Columns, Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; }
            else if (e.Key == Key.H) { AddMullion(SplitKind.Rows, Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; }
            else if (e.Key == Key.Delete) { RemoveSelectedMullion(); e.Handled = true; }
        }

        void Box_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitBox(sender);
            Sheet.Focus();
        }

        void CommitBox(object box)
        {
            if (box == SecWidthBox || box == SecHeightBox || box == SecRaiseBox || box == SecGapBottomBox || box == SecGapStepBox) Section_Commit(box, null);
            else if (box == MullionAxisBox) MullionAxis_Commit(box, null);
        }

        void Hint(string msg) => MessageBox.Show(this, msg, "Редактор окна", MessageBoxButton.OK, MessageBoxImage.Information);

        void Ok_Click(object sender, RoutedEventArgs e)
        {
            // зафиксировать поле, в котором стоит курсор
            Sheet.Focus(); // поле с курсором теряет фокус и фиксирует значение
            if (string.IsNullOrWhiteSpace(Mirror.StripSize(FamilyNameBox.Text)) || FamilyName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                Hint("Введите корректное имя семейства (без символов \\ / : * ? \" < > |).");
                return;
            }
            if (Config.BlockFromExterior < 0 || Config.BlockToInterior < 0)
            {
                Hint("Рама выходит за пределы стены: проверьте толщину стены, привязку и смещение рамы на вкладке «Проём».");
                return;
            }
            FamilyNameBox.Text = FamilyName; // показать итоговое имя с размером
            DialogResult = true;
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
