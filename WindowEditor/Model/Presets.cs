using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowEditor.Model
{
    /// <summary>
    /// Шаблоны схем. Названия — по каталогу «pmN.Окно v3»: цифры = код схемы (ряды снизу вверх).
    /// Левые варианты получаются кнопкой «Отразить».
    /// </summary>
    public static class Presets
    {
        static Cell L(FillKind f, double size = 0) => new Cell { Fill = f, Size = size };
        static Cell G(double size = 0) => new Cell { Fill = FillKind.Fixed, Size = size };
        static Cell Wall(double size) => new Cell { Fill = FillKind.Wall, Size = size };
        static Cell Cols(double size, params Cell[] kids) => new Cell { Size = size, Split = SplitKind.Columns, Children = kids.ToList() };
        /// <summary>Ряды перечисляются снизу вверх.</summary>
        static Cell Rows(double size, params Cell[] kids) => new Cell { Size = size, Split = SplitKind.Rows, Children = kids.ToList() };
        /// <summary>Створка с полями снизу вверх (например, дверь: нижняя панель + стекло).</summary>
        static Cell SashRows(FillKind f, double size, params double[] fieldSizes)
        {
            var c = new Cell { Fill = f, Size = size, Split = SplitKind.Rows };
            foreach (var s in fieldSizes) c.Children.Add(new Cell { Size = s });
            c.Children.Add(new Cell());
            return c;
        }

        /// <summary>Дверь: глухая панель снизу, стекло сверху.</summary>
        static Cell DoorPanel(FillKind f, double size, double panel)
        {
            var d = SashRows(f, size, panel);
            d.Children[0].Infill = InfillKind.Panel;
            return d;
        }

        static Section Sec(double w, double h, Cell root, double gapBottom = 40) =>
            new Section { Width = w, Height = h, Root = root, GapBottom = gapBottom };

        static readonly (string name, Func<List<Section>> make)[] Items =
        {
            ("Глухое", () => new List<Section> { Sec(1200, 1400, G()) }),
            ("Пустой проём", () => new List<Section> { Sec(1240, 1460, G()) }),

            // ---- окна ----
            ("v3 1",    () => new List<Section> { Sec(900, 1400, L(FillKind.TiltTurnLeft)) }),
            ("v3 11",   () => new List<Section> { Sec(900, 1700, Rows(0, G(1100), L(FillKind.Tilt))) }),
            ("v3 111",  () => new List<Section> { Sec(900, 1700, Rows(0, G(400), G(500), G())) }),
            ("v3 2",    () => new List<Section> { Sec(1300, 1400, Cols(0, G(560), L(FillKind.TiltTurnRight))) }),
            ("v3 12",   () => new List<Section> { Sec(1300, 1700, Rows(0, G(300), Cols(1180, L(FillKind.TiltTurnLeft, 560), L(FillKind.TiltTurnRight)))) }),
            ("v3 21",   () => new List<Section> { Sec(1300, 1700, Rows(0, Cols(1100, L(FillKind.TurnLeft, 560), L(FillKind.TiltTurnRight)), G())) }),
            ("v3 22",   () => new List<Section> { Sec(1300, 1700, Rows(0, Cols(300, G(560), G()), Cols(0, L(FillKind.TiltTurnLeft, 560), L(FillKind.TiltTurnRight)))) }),
            ("v3 121",  () => new List<Section> { Sec(1300, 1800, Rows(0, G(400), Cols(700, G(560), G()), G())) }),
            ("v3 3",    () => new List<Section> { Sec(1800, 1500, Cols(0, G(520), L(FillKind.TiltTurnLeft, 580), G())) }),
            ("v3 31",   () => new List<Section> { Sec(1800, 1800, Rows(0, Cols(1200, G(520), L(FillKind.TiltTurnLeft, 580), G()), G())) }),
            ("v3 4",    () => new List<Section> { Sec(2400, 1500, Cols(0, G(480), L(FillKind.TiltTurnLeft, 600), L(FillKind.TiltTurnRight, 600), G())) }),
            ("v3 41",   () => new List<Section> { Sec(2400, 1900, Rows(0, Cols(1300, G(480), L(FillKind.TiltTurnLeft, 600), L(FillKind.TiltTurnRight, 600), G()), G())) }),
            ("v3 44",   () => new List<Section> { Sec(2400, 1900, Rows(0, Cols(1300, G(480), L(FillKind.TiltTurnLeft, 600), L(FillKind.TiltTurnRight, 600), G()), Cols(0, G(480), G(600), G(600), G()))) }),
            ("v3 24",   () => new List<Section> { Sec(2400, 1900, Rows(0, Cols(350, G(1100), G()), Cols(0, G(480), L(FillKind.TiltTurnLeft, 600), L(FillKind.TiltTurnRight, 600), G()))) }),
            ("v3 212п", () => new List<Section> { Sec(1500, 2000, Rows(0,
                            Cols(1450, SashRows(FillKind.TiltTurnLeft, 600, 500), Rows(0, G(500), G())),
                            Cols(0, G(600), G()))) }),

            // ---- балконные, прямоугольные ----
            ("Балк 1",  () => new List<Section> { Sec(800, 2200, SashRows(FillKind.DoorTiltLeft, 0, 600), 10) }),
            ("Балк 1 — низ панель", () => new List<Section> { Sec(800, 2200, DoorPanel(FillKind.DoorTiltLeft, 0, 600), 10) }),
            ("Балк 2",  () => new List<Section> { Sec(1500, 2200, Cols(0, SashRows(FillKind.DoorTiltLeft, 750, 600), G()), 10) }),
            ("Балк 21", () => new List<Section> { Sec(1500, 2200, Rows(0, Cols(1600, L(FillKind.DoorTiltLeft, 750), G()), G()), 10) }),
            ("Балк 32", () => new List<Section> { Sec(2300, 2200, Cols(0,
                            Rows(600, G(600), G()),
                            SashRows(FillKind.DoorTiltLeft, 800, 600),
                            Rows(0, G(600), G())), 10) }),

            // ---- балконные блоки: одна Г- или Т-образная рама с общей фрамугой ----
            ("121 Балк Правое", () => new List<Section> { Sec(2000, 2200, Rows(0,
                            Cols(1600, SashRows(FillKind.DoorTiltLeft, 750, 600), Rows(0, Wall(700), Cols(0, G(500), L(FillKind.TiltTurnRight)))),
                            G()), 10) }),
            ("1111 Балк Правое", () => new List<Section> { Sec(2000, 2200, Rows(0,
                            Cols(1600, SashRows(FillKind.DoorTiltLeft, 750, 600), Rows(0, Wall(700), G(250), G())),
                            G()), 10) }),
            ("1112 Балк Правое", () => new List<Section> { Sec(2000, 2200, Rows(0,
                            Cols(1600, SashRows(FillKind.DoorTiltLeft, 750, 600), Rows(0, Wall(700), G(250), G())),
                            Cols(0, G(750), G())), 10) }),
            ("122 Балк Правое", () => new List<Section> { Sec(2000, 2200, Rows(0,
                            Cols(1600, SashRows(FillKind.DoorTiltLeft, 750, 600), Rows(0, Wall(700), Cols(0, G(500), L(FillKind.TiltTurnRight)))),
                            Cols(0, G(750), G())), 10) }),
            ("Балк Центр 1223", () => new List<Section> { Sec(2700, 2200, Rows(0,
                            Cols(1600,
                                Rows(900, Wall(700), G(400), G()),
                                SashRows(FillKind.DoorTiltLeft, 800, 600),
                                Rows(0, Wall(700), G(400), L(FillKind.TiltTurnRight))),
                            Cols(0, G(900), G(800), G())), 10) }),

            // ---- балконный блок из двух отдельных рам ----
            ("Дверь + окно (две рамы)", () =>
            {
                var door = Sec(800, 2200, SashRows(FillKind.DoorTiltLeft, 0, 600), 10);
                var win = Sec(1200, 1370, Cols(0, G(500), L(FillKind.TiltTurnRight)));
                win.Raise = 800;
                return new List<Section> { door, win };
            }),
        };

        public static readonly string[] Names = Items.Select(i => i.name).ToArray();

        /// <summary>Новая схема по шаблону. Профиль, проём, материалы и откосы берутся из profileFrom.</summary>
        public static WindowConfig Create(int index, WindowConfig profileFrom = null)
        {
            var c = profileFrom?.Clone() ?? new WindowConfig();
            var item = Items[Math.Max(0, Math.Min(index, Items.Length - 1))];
            c.Sections = item.make();
            c.EmptyOpening = item.name == "Пустой проём";
            c.Reveals.Clear(); // грани изменились — настройки откосов по умолчанию
            return c;
        }

        public static int IndexOf(string name) => Array.IndexOf(Names, name);
    }
}
