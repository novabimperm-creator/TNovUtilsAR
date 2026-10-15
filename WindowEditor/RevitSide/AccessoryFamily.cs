using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using static WindowEditor.RevitSide.WindowParams;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Семейство «WE_Элемент окна» — параметрический брусок категории «Окна» (без основы), с площадью = длина × ширина.
    /// Подоконники, отливы и откосы — его экземпляры в модели (см. Accessories), связанные с окном по WE_ID окна.
    ///
    /// Соглашение (его должно соблюдать и своё семейство, если вы подмените файл):
    ///   начало координат — в углу; брусок занимает X∈[0; WE_Размер X], Y∈[0; WE_Размер Y], Z∈[0; WE_Размер Z];
    ///   параметры экземпляра: WE_Размер X/Y/Z (длина), WE_Материал (материал), общие WE_Вид элемента и др.
    /// Файл создаётся один раз: %AppData%\WindowEditor\WE_Элемент окна.rfa — его можно заменить своим.
    /// </summary>
    public static class AccessoryFamily
    {
        public const string Name = "WE_Элемент окна";
        public const string PX = "WE_Размер X";
        public const string PY = "WE_Размер Y";
        public const string PZ = "WE_Размер Z";
        public const string PMat = "WE_Материал";

        public static string FilePath => Path.Combine(AppDataDir(), Name + ".rfa");

        /// <summary>Путь к файлу семейства; при первом вызове создаёт его. Вызывать вне транзакций.</summary>
        public static string Ensure(Application app)
        {
            if (File.Exists(FilePath)) { Upgrade(app); return FilePath; }

            var template = Settings.GetGenericTemplate(app.FamilyTemplatePath);
            if (template == null)
                throw new InvalidOperationException("Не выбран шаблон «Типовая модель» — элементы комплектации создать нельзя.");

            var doc = app.NewFamilyDocument(template);
            try
            {
                using (var t = new Transaction(doc, "Элемент окна"))
                {
                    t.Start();
                    Build(app, doc);
                    t.Commit();
                }
                CheckFlex(doc);
                doc.SaveAs(FilePath, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally
            {
                doc.Close(false);
            }
            return FilePath;
        }

        static void Build(Application app, Document doc)
        {
            var fam = doc.OwnerFamily;
            fam.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            fam.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.Set(0);

            var fm = doc.FamilyManager;
            if (fm.CurrentType == null) fm.NewType("Элемент");

            var px = EnsureFamilyParam(fm, PX, GroupTypeId.Geometry, SpecTypeId.Length, true);
            var py = EnsureFamilyParam(fm, PY, GroupTypeId.Geometry, SpecTypeId.Length, true);
            var pz = EnsureFamilyParam(fm, PZ, GroupTypeId.Geometry, SpecTypeId.Length, true);
            var pm = EnsureFamilyParam(fm, PMat, GroupTypeId.Materials, SpecTypeId.Reference.Material, true);
            Conform(app, doc);

            double L = Ft(1000), W = Ft(300), H = Ft(20);
            fm.Set(px, L); fm.Set(py, W); fm.Set(pz, H);

            var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .First(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);

            var rps = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
            ReferencePlane Origin(XYZ normal) => rps.FirstOrDefault(r =>
            {
                var pl = r.GetPlane();
                return pl.Normal.CrossProduct(normal).GetLength() < 1e-6 && Math.Abs(pl.Normal.DotProduct(pl.Origin)) < 1e-6;
            });
            var rx0 = Origin(XYZ.BasisX) ?? throw new InvalidOperationException("В шаблоне нет опорной плоскости «Центр (лево/право)».");
            var ry0 = Origin(XYZ.BasisY) ?? throw new InvalidOperationException("В шаблоне нет опорной плоскости «Центр (спереди/сзади)».");

            var rx1 = doc.FamilyCreate.NewReferencePlane(new XYZ(L, 0, 0), new XYZ(L, 1, 0), XYZ.BasisZ, plan);
            rx1.Name = "WE_X";
            var ry1 = doc.FamilyCreate.NewReferencePlane(new XYZ(0, W, 0), new XYZ(1, W, 0), XYZ.BasisZ, plan);
            ry1.Name = "WE_Y";

            // брусок
            var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
            var loop = new CurveArray();
            XYZ a = XYZ.Zero, b = new XYZ(L, 0, 0), c = new XYZ(L, W, 0), d = new XYZ(0, W, 0);
            loop.Append(Line.CreateBound(a, b)); loop.Append(Line.CreateBound(b, c));
            loop.Append(Line.CreateBound(c, d)); loop.Append(Line.CreateBound(d, a));
            var profile = new CurveArrArray(); profile.Append(loop);
            var ext = doc.FamilyCreate.NewExtrusion(true, profile, sp, H);
            ext.StartOffset = 0;
            ext.EndOffset = H;
            doc.Regenerate();

            // грани бруска выравниваем по опорным плоскостям и замыкаем
            var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false };
            foreach (var go in ext.get_Geometry(opt))
            {
                if (!(go is Solid s) || s.Volume <= 0) continue;
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf)) continue;
                    var n = pf.FaceNormal;
                    ReferencePlane target = null;
                    if (n.IsAlmostEqualTo(-XYZ.BasisX)) target = rx0;
                    else if (n.IsAlmostEqualTo(XYZ.BasisX)) target = rx1;
                    else if (n.IsAlmostEqualTo(-XYZ.BasisY)) target = ry0;
                    else if (n.IsAlmostEqualTo(XYZ.BasisY)) target = ry1;
                    if (target == null) continue;
                    var al = doc.FamilyCreate.NewAlignment(plan, target.GetReference(), pf.Reference);
                    al.IsLocked = true;
                }
            }

            // размеры с метками
            var rax = new ReferenceArray(); rax.Append(rx0.GetReference()); rax.Append(rx1.GetReference());
            var dx = doc.FamilyCreate.NewLinearDimension(plan, Line.CreateBound(new XYZ(0, -Ft(300), 0), new XYZ(L, -Ft(300), 0)), rax);
            dx.FamilyLabel = px;
            var ray = new ReferenceArray(); ray.Append(ry0.GetReference()); ray.Append(ry1.GetReference());
            var dy = doc.FamilyCreate.NewLinearDimension(plan, Line.CreateBound(new XYZ(-Ft(300), 0, 0), new XYZ(-Ft(300), W, 0)), ray);
            dy.FamilyLabel = py;

            // высота и материал — напрямую через параметры выдавливания
            fm.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), pz);
            fm.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), pm);
        }

        /// <summary>Проверяем, что брусок действительно тянется параметрами.</summary>
        static void CheckFlex(Document doc)
        {
            using (var t = new Transaction(doc, "Проверка"))
            {
                t.Start();
                var fm = doc.FamilyManager;
                fm.Set(fm.get_Parameter(PX), Ft(1234));
                fm.Set(fm.get_Parameter(PY), Ft(321));
                fm.Set(fm.get_Parameter(PZ), Ft(45));
                doc.Regenerate();
                var ext = new FilteredElementCollector(doc).OfClass(typeof(Extrusion)).Cast<Extrusion>().First();
                var bb = ext.get_BoundingBox(null);
                var size = bb.Max - bb.Min;
                bool ok = Math.Abs(Mm(size.X) - 1234) < 0.5 && Math.Abs(Mm(size.Y) - 321) < 0.5 && Math.Abs(Mm(size.Z) - 45) < 0.5
                          && Math.Abs(Mm(bb.Min.X)) < 0.5 && Math.Abs(Mm(bb.Min.Y)) < 0.5;
                t.RollBack();
                if (!ok) throw new InvalidOperationException(
                    "Сгенерированное семейство «WE_Элемент окна» не перестраивается по параметрам. " +
                    $"Сделайте его вручную по соглашению из README и положите в {FilePath}.");
            }
        }

        static bool _upgraded;

        const string AreaFormula = ElLength + " * " + ElWidth;

        /// <summary>
        /// Категория «Окна» (как у pmN.Откос v3 / Отлив v3 — комплектация идёт в спецификации окон), общие параметры
        /// элемента и формула площади. Нужна открытая транзакция.
        /// </summary>
        static void Conform(Application app, Document doc)
        {
            var win = Category.GetCategory(doc, BuiltInCategory.OST_Windows);
            if (doc.OwnerFamily.FamilyCategory?.Id != win.Id) doc.OwnerFamily.FamilyCategory = win;
            EnsureShared(app, doc, ElementShared, true);
            var fm = doc.FamilyManager;
            var area = fm.get_Parameter(ElArea);
            if (area != null && area.Formula != AreaFormula) fm.SetFormula(area, AreaFormula);
        }

        static bool Conforms(Document doc)
        {
            var fm = doc.FamilyManager;
            return doc.OwnerFamily.FamilyCategory?.Id.IntegerValueCompat() == (long)BuiltInCategory.OST_Windows
                   && ElementShared.All(d => fm.get_Parameter(d.Name) != null)
                   && fm.get_Parameter(ElArea).Formula == AreaFormula;
        }

        /// <summary>Файл прошлых версий (или свой): обобщённая модель, нет WE_ID окна или площади — приводим к текущему виду.</summary>
        static void Upgrade(Application app)
        {
            if (_upgraded) return;
            var doc = app.OpenDocumentFile(FilePath);
            try
            {
                if (!Conforms(doc))
                {
                    using (var t = new Transaction(doc, "Элемент окна: категория и параметры"))
                    {
                        t.Start();
                        Conform(app, doc);
                        t.Commit();
                    }
                    doc.Save();
                }
            }
            finally
            {
                doc.Close(false);
            }
            _upgraded = true;
        }

        /// <summary>
        /// Загрузить (перезагрузить, если файл обновился) семейство в проект и вернуть активный типоразмер.
        /// Нужна открытая транзакция.
        /// </summary>
        public static FamilySymbol LoadIntoProject(Document doc, string path) => LoadIntoProject(doc, path, out _);

        /// <param name="replaced">
        /// В проекте было семейство с этим именем другой категории (обобщённая модель прошлых версий): Revit не
        /// перезагружает его поверх, поэтому оно удалено вместе с экземплярами — комплектацию надо пересоздать.
        /// </param>
        public static FamilySymbol LoadIntoProject(Document doc, string path, out bool replaced)
        {
            replaced = false;
            var old = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == Name);
            if (old != null && old.FamilyCategory?.Id.IntegerValueCompat() != (long)BuiltInCategory.OST_Windows)
            {
                doc.Delete(old.Id);
                replaced = true;
            }
            doc.LoadFamily(path, new OverwriteLoadOptions(), out Family fam);
            fam = fam ?? new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == Name);
            if (fam == null) throw new InvalidOperationException($"Не удалось загрузить {path}.");
            var symbol = fam.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().First();
            if (!symbol.IsActive) symbol.Activate();
            return symbol;
        }

    }
}
