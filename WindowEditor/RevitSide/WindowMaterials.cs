using System.Linq;
using Autodesk.Revit.DB;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Материалы редактора по имени. Новый материал получает цвет по названию («ПВХ белый» — белый, «антрацит» —
    /// тёмно-серый, стекло — голубое прозрачное…): Material.Create даёт чёрный. Материал, созданный так раньше и
    /// оставшийся чёрным, перекрашивается. Нужна открытая транзакция.
    /// </summary>
    public static class WindowMaterials
    {
        public static ElementId GetOrCreate(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;
            var m = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
            if (m == null) m = (Material)doc.GetElement(Material.Create(doc, name));
            else if (!IsDefaultBlack(m)) return m.Id; // свой цвет материала не трогаем

            if (ColorFor(name, out var color, out int transparency))
            {
                m.Color = color;
                m.Transparency = transparency;
            }
            return m.Id;
        }

        static bool IsDefaultBlack(Material m) =>
            m.Color.IsValid && m.Color.Red == 0 && m.Color.Green == 0 && m.Color.Blue == 0;

        /// <summary>Цвет по ключевым словам названия. false — слов не нашлось (оставляем как есть, кроме чёрного).</summary>
        static bool ColorFor(string name, out Color color, out int transparency)
        {
            var n = name.ToLowerInvariant().Replace('ё', 'е');
            transparency = 0;
            bool Has(params string[] keys) => keys.Any(n.Contains);

            if (Has("воздух", "зона открывания")) { color = new Color(255, 255, 255); transparency = 100; return true; }
            if (Has("стекл", "glass")) { color = new Color(190, 220, 235); transparency = 70; return true; }
            if (Has("черн", "black")) { color = new Color(35, 35, 35); return true; }
            if (Has("антрацит", "anthracite", "7016")) { color = new Color(56, 62, 66); return true; }
            if (Has("коричн", "дуб", "орех", "дерев", "махагон", "wood")) { color = new Color(120, 80, 45); return true; }
            if (Has("оцинк", "алюм", "металл", "сталь")) { color = new Color(185, 190, 195); return true; }
            if (Has("сер", "grey", "gray")) { color = new Color(150, 150, 150); return true; }
            if (Has("штукат")) { color = new Color(225, 220, 210); return true; }
            if (Has("бел", "white", "9016")) { color = new Color(245, 245, 245); return true; }
            color = new Color(200, 200, 200); // неизвестное — светло-серый, только вместо чёрного
            return true;
        }
    }
}
