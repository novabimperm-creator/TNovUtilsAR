using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Параметры семейств. Те, что нужны в спецификациях, — общие (shared) с фиксированными GUID.
    /// WE_Config, служебные и материалы — обычные параметры семейства.
    /// </summary>
    public static class WindowParams
    {
        // ---- служебные параметры окна ----
        public const string Config = "WE_Config";
        public const string GenVoids = "WE_Служебн. пустоты";
        /// <summary>Отметка низа проёма в координатах семейства (для углового узла).</summary>
        public const string SillZ = "WE_Служебн. низ проёма";
        public const string MatPrefix = "WE_Мат. ";

        // ---- общие параметры окна (тип) ----
        public const string Scheme = "WE_Схема открывания";
        public const string Sashes = "WE_Количество створок";
        public const string Doors = "WE_Количество дверных створок";
        public const string Fixeds = "WE_Количество глухих";
        public const string GlassArea = "WE_Площадь остекления";
        public const string PanelArea = "WE_Площадь панелей";
        public const string WindowArea = "WE_Площадь окна";
        public const string MullionLen = "WE_Длина импостов";
        public const string BlockWidth = "WE_Ширина блока";
        public const string BlockHeight = "WE_Высота блока";
        public const string OpenWidth = "WE_Ширина проёма";
        public const string OpenHeight = "WE_Высота проёма";

        // ---- общие параметры элемента комплектации (экземпляр) ----
        public const string ElKind = "WE_Вид элемента";
        public const string ElPlace = "WE_Расположение";
        public const string ElLength = "WE_Длина элемента";
        public const string ElWidth = "WE_Ширина элемента";
        public const string ElThickness = "WE_Толщина элемента";
        /// <summary>Id окна (ElementId строкой), к которому относится элемент комплектации в модели.</summary>
        public const string ElWindowId = "WE_ID окна";
        /// <summary>Площадь элемента = длина × ширина (подоконник/отлив — в плане, откос — лицевая грань). Формула в семействе.</summary>
        public const string ElArea = "WE_Площадь элемента";

        public class SharedDef
        {
            public string Name;
            public Guid Guid;
            public Func<ForgeTypeId> Spec;
            public SharedDef(string n, string g, Func<ForgeTypeId> s) { Name = n; Guid = new Guid(g); Spec = s; }
        }

        // GUID менять нельзя: по ним Revit сопоставляет параметры в спецификациях.
        public static readonly SharedDef[] WindowShared =
        {
            new SharedDef(Scheme,      "6b3e1c52-8f0a-4d7e-9c21-3a5f7d10e4a1", () => SpecTypeId.String.Text),
            new SharedDef(Sashes,      "a1d74e09-2c6b-4f38-b5e2-81c09f3d6a72", () => SpecTypeId.Int.Integer),
            new SharedDef(Doors,       "5b0e7c31-9a24-4d8f-a6e1-0c3f8b72d915", () => SpecTypeId.Int.Integer),
            new SharedDef(Fixeds,      "c9f2a7d4-0e51-4b6a-8d3c-5e7b19a2f083", () => SpecTypeId.Int.Integer),
            new SharedDef(GlassArea,   "3e8b5f16-d24c-49a0-a7f1-62d9c0b4e5a4", () => SpecTypeId.Area),
            new SharedDef(WindowArea,  "f05c6d28-71b3-4e9a-bc4d-9a1e3f7c2b65", () => SpecTypeId.Area),
            new SharedDef(PanelArea,   "2f8d6a13-c5e7-4b90-a1d2-7e4b9c3f0a58", () => SpecTypeId.Area),
            new SharedDef(MullionLen,  "8d4a2b7e-5f69-40c1-9e38-b7f6a1d0c396", () => SpecTypeId.Length),
            new SharedDef(BlockWidth,  "e2a91f47-6c38-4b05-8d7e-14f9c6a3b081", () => SpecTypeId.Length),
            new SharedDef(BlockHeight, "71c4d8a2-3e9f-4a16-b0d5-9f2e7c8a4b36", () => SpecTypeId.Length),
            new SharedDef(OpenWidth,   "a8f3c1e5-2d74-4b9a-9e06-6b1d0f5c3e72", () => SpecTypeId.Length),
            new SharedDef(OpenHeight,  "0d6b9e24-f81a-4c37-a5e9-3c7f2b8d1a04", () => SpecTypeId.Length),
        };

        public static readonly SharedDef[] ElementShared =
        {
            new SharedDef(ElKind,      "4c2f8a61-b7d3-4e90-8a15-e6d9c3b2f708", () => SpecTypeId.String.Text),
            new SharedDef(ElPlace,     "9e7a3d05-1f6c-48b2-bd34-a0c5e8f61d93", () => SpecTypeId.String.Text),
            new SharedDef(ElLength,    "b3d6f0a8-5e21-4c7b-9f48-27a1e9c0d5b6", () => SpecTypeId.Length),
            new SharedDef(ElWidth,     "16e9b4c7-a053-4f2d-8c6a-d8b3f5e7a291", () => SpecTypeId.Length),
            new SharedDef(ElThickness, "d5a0e3f9-7b16-4d84-a2c9-5f8e1b6d3c47", () => SpecTypeId.Length),
            new SharedDef(ElWindowId,  "7a1c5e93-4b2d-4f86-9e0a-c3d81f6b2e47", () => SpecTypeId.String.Text),
            new SharedDef(ElArea,      "3d9b6f20-8e4c-4a71-b5d2-e1c07a9f4b68", () => SpecTypeId.Area),
        };

        /// <summary>Добавить недостающие общие параметры. Нужна открытая транзакция в famDoc.</summary>
        public static void EnsureShared(Application app, Document famDoc, IEnumerable<SharedDef> defs, bool isInstance)
        {
            var fm = famDoc.FamilyManager;
            var missing = defs.Where(d => fm.get_Parameter(d.Name) == null).ToList();
            if (missing.Count == 0) return;

            // Временно переключаемся на свой ФОП, потом возвращаем пользовательский.
            string original = app.SharedParametersFilename;
            string path = SharedFilePath();
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, "");
                app.SharedParametersFilename = path;
                var file = app.OpenSharedParameterFile();
                var group = file.Groups.get_Item("WindowEditor") ?? file.Groups.Create("WindowEditor");
                foreach (var d in missing)
                {
                    var def = group.Definitions.get_Item(d.Name) as ExternalDefinition;
                    if (def == null)
                    {
                        var opt = new ExternalDefinitionCreationOptions(d.Name, d.Spec()) { GUID = d.Guid, Visible = true };
                        def = (ExternalDefinition)group.Definitions.Create(opt);
                    }
                    fm.AddParameter(def, GroupTypeId.Data, isInstance);
                }
            }
            finally
            {
                app.SharedParametersFilename = original ?? "";
            }
        }

        public static FamilyParameter EnsureFamilyParam(FamilyManager fm, string name, ForgeTypeId group, ForgeTypeId spec, bool isInstance)
            => fm.get_Parameter(name) ?? fm.AddParameter(name, group, spec, isInstance);

        /// <summary>
        /// Общая папка библиотеки редактора: шаблон окна WE_Окно.rfa и семейство комплектации «WE_Элемент окна».
        /// Прописана здесь, не в TNovConfig: файлы из неё важнее локальных и сохранённых в настройках.
        /// </summary>
        public const string LibraryDir = @"\\fs-nova\nova\04_БИБЛИОТЕКА\BIM\АР_Семейства\Окна\template";

        /// <summary>Файл из общей папки библиотеки, если он доступен; иначе null.</summary>
        public static string LibraryFile(string fileName)
        {
            try
            {
                var p = Path.Combine(LibraryDir, fileName);
                return File.Exists(p) ? p : null;
            }
            catch (Exception) { return null; } // нет сети/доступа — работаем с локальными файлами
        }

        public static string SharedFilePath() => Path.Combine(AppDataDir(), "WindowEditor_SharedParams.txt");

        public static string AppDataDir()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowEditor");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ---------- Ширина / высота окна ----------

        static readonly BuiltInParameter[] WidthBips = { BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM };
        static readonly BuiltInParameter[] HeightBips = { BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM };
        static readonly string[] WidthNames = { "Ширина", "Width" };
        static readonly string[] HeightNames = { "Высота", "Height" };

        public static FamilyParameter FamilyWidth(FamilyManager fm) => Find(fm, WidthBips, WidthNames);
        public static FamilyParameter FamilyHeight(FamilyManager fm) => Find(fm, HeightBips, HeightNames);

        static FamilyParameter Find(FamilyManager fm, BuiltInParameter[] bips, string[] names)
        {
            foreach (var b in bips)
            {
                try { var p = fm.get_Parameter(b); if (p != null) return p; } catch { }
            }
            foreach (var n in names) { var p = fm.get_Parameter(n); if (p != null) return p; }
            return null;
        }

        /// <summary>Ширина/высота типа в проекте, мм (для окон, созданных не редактором).</summary>
        public static double SymbolDim(FamilySymbol s, bool width)
        {
            foreach (var b in width ? WidthBips : HeightBips)
            {
                var p = s.get_Parameter(b);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double) return Mm(p.AsDouble());
            }
            foreach (var n in width ? WidthNames : HeightNames)
            {
                var p = s.LookupParameter(n);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double) return Mm(p.AsDouble());
            }
            return 1500;
        }

        public static double Ft(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        public static double Mm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
        public static double FtSq(double mm2) => UnitUtils.ConvertToInternalUnits(mm2 / 1e6, UnitTypeId.SquareMeters);
    }
}
