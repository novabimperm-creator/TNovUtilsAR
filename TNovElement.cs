using Autodesk.Revit.DB;
using TNovCommon;

namespace TNovUtilsAR
{
    public class TNovElement
    {
        public Element elem {  get; set; }
        public string TNovCategory { get; set; }
        public TNovElement(Element elem)
        {
            this.elem = elem;
            this.TNovCategory = LevelNumberElements.GetKind(elem); //общая классификация с автопроверкой N_Эт.Номер
        }
    }
}
