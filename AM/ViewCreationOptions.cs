using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TNovUtilsAR
{
    /// <summary>
    /// Один набор видов (дисциплина): токен в имени (АМ/ПСО) + три шаблона.
    /// Геометрия (комнаты, квартиры, контур обрезки ПК) общая для всех наборов —
    /// различаются только токен имени и шаблоны.
    /// </summary>
    public sealed class ViewSetOptions
    {
        public string Token { get; set; }
        public ElementId GeneralTemplateId { get; set; }
        public ElementId PkTemplateId { get; set; }
        public string PsTemplatePrefix { get; set; }
    }

    public sealed class ViewCreationOptions
    {
        public Level Level { get; set; }
        public string FloorLabel { get; set; }
        public RevitLinkInstance LinkInstance { get; set; }
        public IList<ViewSetOptions> ViewSets { get; set; } = new List<ViewSetOptions>();
    }
}
