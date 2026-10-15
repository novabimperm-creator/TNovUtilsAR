using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WindowEditor.Model
{
    /// <summary>Зеркальное отражение схемы (левое ↔ правое).</summary>
    public static class Mirror
    {
        public static WindowConfig Apply(WindowConfig src)
        {
            var oldGeo = new WindowGeometry(src);
            double w = oldGeo.OpeningWidth;

            var c = src.Clone();
            var cloneGeo = new WindowGeometry(c);
            // фиксируем фактические размеры, чтобы после разворота «остаток» ушёл в правильную ячейку
            foreach (var sg in cloneGeo.Sections) FreezeSizes(sg.Layout, sg.S.Root);
            foreach (var s in c.Sections) Flip(s.Root);
            c.Sections.Reverse();

            // настройки откосов переносим на отражённые грани
            var newGeo = new WindowGeometry(c);
            var reveals = new List<RevealFace>();
            foreach (var f in oldGeo.CoreFaces)
            {
                var rf = src.Reveals.FirstOrDefault(x => x.Face == f.Name);
                if (rf == null) continue;
                var nf = newGeo.CoreFaces.FirstOrDefault(n => Same(f, n, w));
                if (nf != null) reveals.Add(new RevealFace { Face = nf.Name, In = rf.In.Copy(), Out = rf.Out.Copy() });
            }
            c.Reveals = reveals;
            return c;
        }

        static bool Same(Face a, Face b, double w)
        {
            const double e = 0.5;
            FaceKind k = a.Kind == FaceKind.Left ? FaceKind.Right : a.Kind == FaceKind.Right ? FaceKind.Left : a.Kind;
            if (b.Kind != k) return false;
            if (a.Vertical) return Math.Abs(b.X - (w - a.X)) < e && Math.Abs(b.Z0 - a.Z0) < e && Math.Abs(b.Z1 - a.Z1) < e;
            return Math.Abs(b.X0 - (w - a.X1)) < e && Math.Abs(b.X1 - (w - a.X0)) < e && Math.Abs(b.Z - a.Z) < e;
        }

        static void FreezeSizes(WindowLayout lay, Cell cell)
        {
            if (cell.IsLeaf) return;
            foreach (var ch in cell.Children)
            {
                var r = lay.RectOf(ch);
                ch.Size = cell.Split == SplitKind.Columns ? r.W : r.H;
                FreezeSizes(lay, ch);
            }
        }

        static void Flip(Cell cell)
        {
            switch (cell.Fill)
            {
                case FillKind.TurnLeft: cell.Fill = FillKind.TurnRight; break;
                case FillKind.TurnRight: cell.Fill = FillKind.TurnLeft; break;
                case FillKind.TiltTurnLeft: cell.Fill = FillKind.TiltTurnRight; break;
                case FillKind.TiltTurnRight: cell.Fill = FillKind.TiltTurnLeft; break;
                case FillKind.DoorLeft: cell.Fill = FillKind.DoorRight; break;
                case FillKind.DoorRight: cell.Fill = FillKind.DoorLeft; break;
                case FillKind.DoorTiltLeft: cell.Fill = FillKind.DoorTiltRight; break;
                case FillKind.DoorTiltRight: cell.Fill = FillKind.DoorTiltLeft; break;
            }
            if (cell.Split == SplitKind.Columns) cell.Children.Reverse();
            foreach (var ch in cell.Children) Flip(ch);
        }

        /// <summary>Имя зеркального варианта: Левое ↔ Правое, 212л ↔ 212п.</summary>
        public static string MirrorName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var r = Regex.Replace(name, "Левое|Правое|левое|правое", m =>
                m.Value == "Левое" ? "Правое" : m.Value == "Правое" ? "Левое" : m.Value == "левое" ? "правое" : "левое");
            r = Regex.Replace(r, @"(\d)([лп])(?=\b|$|\s)", m => m.Groups[1].Value + (m.Groups[2].Value == "л" ? "п" : "л"));
            return r;
        }

        static readonly Regex SizeSuffix = new Regex(@"_\d+(?:[.,]\d+)?[хxХX]\d+(?:[.,]\d+)?\s*$");

        /// <summary>Имя без суффикса размера «_ШхВ».</summary>
        public static string StripSize(string name) => SizeSuffix.Replace(name ?? "", "").TrimEnd();

        /// <summary>
        /// Имя семейства (и его единственного типа) по правилу «имя_ШхВ», Ш×В — проём, мм. Прежний суффикс
        /// размера заменяется: одно семейство — один тип — один размер.
        /// </summary>
        public static string WithSize(string name, double width, double height) =>
            $"{StripSize(name)}_{Math.Round(width):0}х{Math.Round(height):0}";

        /// <summary>Подставить код схемы в имя: заменяет первое число (с запятыми) или дописывает в конец.</summary>
        public static string WithCode(string name, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return name;
            name = name ?? "";
            var rx = new Regex(@"(?<![\w.])\d+(?:,\d+)*(?=[^\d,]|$)");
            // версия вида «v3» числом не считается: перед числом не должно быть буквы
            var m = rx.Matches(name).Cast<Match>().FirstOrDefault(x => x.Index == 0 || !char.IsLetter(name[x.Index - 1]));
            if (m == null) return (name.TrimEnd() + " " + code).Trim();
            return name.Substring(0, m.Index) + code + name.Substring(m.Index + m.Length);
        }
    }
}
