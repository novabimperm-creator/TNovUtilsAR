using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TNovUtilsAR
{
    public class TNovElement
    {
        public Element elem {  get; set; }
        public string TNovCategory { get; set; }
        public TNovElement(Element elem)
        {
            this.elem = elem;
            
            Type elementType = elem.GetType();
            string tNovCategory = "Default";
            if (elementType != null)
            {
                if (elementType == typeof(Wall)) tNovCategory = "Wall";
                else if (elementType == typeof(Floor)) tNovCategory = "Floor";
                else if (elementType == typeof(Ceiling)) tNovCategory = "Ceiling";
                else if (elementType == typeof(Floor)) tNovCategory = "Floor";
                else if (elementType == typeof(Room)) tNovCategory = "Room";
                else if (elementType == typeof(Stairs)) tNovCategory = "Stairs";
                else if (elementType == typeof(Railing)) tNovCategory = "Railing";
                else if (elementType == typeof(FamilyInstance)) 
                {
#if R2022
                        long catId = elem.Category.Id.IntegerValue;
#else
                    long catId = elem.Category.Id.Value;
#endif
                    switch (catId)
                    {
                        case -2000011: tNovCategory = "FamilyInstance_Wall"; break;
                        case -2000032: tNovCategory = "FamilyInstance_Floor"; break;
                        case -2000038: tNovCategory = "FamilyInstance_Ceiling"; break;
                        case -2000014: tNovCategory = "FamilyInstance_DoorWindow"; break;
                        case -2000023: tNovCategory = "FamilyInstance_DoorWindow"; break;
                        case -2001320:
                            if(elem.Name.Contains("Аэратор")==false) TNovCategory = "FamilyInstance_Beam";
                            else tNovCategory = "FamilyInstance_Other";
                            break;
                        case -2001180: tNovCategory = "FamilyInstance_Parking"; break;
                        case -2000151:
                            FamilyInstance fi = elem as FamilyInstance;
                            string gmvalue = fi.Symbol.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL).AsString();
                            bool isHole = false;
                            if (gmvalue != null)
                            {
                                if (gmvalue.Contains("Отверстие")) { isHole = true; tNovCategory = "FamilyInstance_Hole"; }
                            }
                            if (!isHole) tNovCategory = "FamilyInstance_Other";
                            break;
                        default: tNovCategory = "FamilyInstance_Other"; break;
                    }
                }
            }
            this.TNovCategory = tNovCategory;
        }
    }
}
