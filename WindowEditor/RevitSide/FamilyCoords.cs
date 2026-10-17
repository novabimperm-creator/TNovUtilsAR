using Autodesk.Revit.DB;
using WindowEditor.Model;
using static WindowEditor.RevitSide.WindowParams;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Схема (мм: x по фасаду от левого края проёма, d вглубь от наружной грани стены, z от низа проёма)
    /// → координаты семейства окна (футы). Общая для генерации семейства и комплектации в модели.
    /// </summary>
    public readonly struct FamilyCoords
    {
        readonly double _signX, _signY, _halfWidth, _halfWall, _sillZ;

        public FamilyCoords(WindowConfig cfg, WindowGeometry geo, double sillZ)
        {
            // Вид с фасада: если фасад со стороны +Y, то «право» на фасаде — это −X семейства.
            _signX = cfg.ExteriorPositiveY ? -1 : 1;
            _signY = cfg.ExteriorPositiveY ? 1 : -1;
            _halfWidth = geo.OpeningWidth / 2;
            _halfWall = cfg.FinishExterior + cfg.HostThickness / 2; // начало семейства — ось стены-основы
            _sillZ = sillZ;
        }

        public double X(double x) => _signX * Ft(x - _halfWidth);
        public double Y(double d) => _signY * Ft(_halfWall - d);
        public double Z(double z) => Ft(z) + _sillZ;

        /// <summary>
        /// Координаты семейства → модель для экземпляра, с учётом отражения. У отражённого экземпляра
        /// (инструмент «Зеркально») GetTotalTransform() — чистый поворот без отражения: Revit «зашивает»
        /// отражение в геометрию. Верные оси — HandOrientation (X семейства) и FacingOrientation (Y).
        /// </summary>
        public static Transform InstanceFrame(FamilyInstance fi)
        {
            var tr = fi.GetTotalTransform();
            var f = Transform.Identity;
            f.Origin = tr.Origin;
            f.BasisX = fi.HandOrientation;
            f.BasisY = fi.FacingOrientation;
            f.BasisZ = tr.BasisZ;
            return f;
        }
    }
}
