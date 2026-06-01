using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TNovCommon;

namespace TNovUtilsAR
{
    public class CreateInstancesHandler : IExternalEventHandler
    {
        private List<ItemData> _selectedItems;
        private double _userDepth = 0.5; // значение по умолчанию в футах (152.4 мм)
        private static bool _firstLog = true;

        public List<ItemData> SelectedItems
        {
            set { _selectedItems = value; }
        }

        public double UserDepth
        {
            set { _userDepth = value; }
        }

        public CreateInstancesHandler()
        {
            if (_firstLog)
            {
                _firstLog = false;
            }
        }

        

        public void Execute(UIApplication uiapp)
        {
            
            Document doc = uiapp.ActiveUIDocument.Document;

            //параметры
            Guid adskWidthParamGuid = new Guid("8f2e4f93-9472-4941-a65d-0ac468fd6a5d");//ADSK_Размер_Ширина
            Guid adskHeightParamGuid = new Guid("da753fe3-ecfa-465b-9a2c-02f55d0c2ff1");//ADSK_Размер_Высота
            Guid adskDepthParamGuid = new Guid("14e630a8-bc4f-4556-9094-647e8f323f08");//A_Размер_Глубина

            try
            {
                // Поиск семейства
                FamilySymbol targetSymbol = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.Name == "pmN.Отверстие Стена.ПОФ"
                                        && fs.Category.Id.IntegerValue == (int)BuiltInCategory.OST_GenericModel);
                if (targetSymbol == null)
                {
                    new InfoWindow280("Семейство 'pmN.Отверстие Стена.ПОФ' не загружено.").ShowDialog();
                    return;
                }

                if (!targetSymbol.IsActive)
                {
                    using (Transaction t = new Transaction(doc, "Активировать семейство"))
                    {
                        t.Start();
                        targetSymbol.Activate();
                        t.Commit();
                    }
                }

                // Сбор существующих экземпляров
                List<FamilyInstance> existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol.Id == targetSymbol.Id)
                    .ToList();
                Logger.Log($"Найдено {existing.Count} экземпляров", 1);

                using (Transaction trans = new Transaction(doc, "Обновить проёмы"))
                {
                    trans.Start(); Logger.Log("Открываем транзакцию", 1);

                    // Удаление старых (только с нашим комментарием)
                    List<ElementId> idsToDelete = new List<ElementId>();
                    foreach (var inst in existing)
                    {
                        Parameter comment = inst.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (comment != null && comment.HasValue)
                        {
                            string val = comment.AsString();
                            if (!string.IsNullOrEmpty(val) && val.StartsWith("Исходный элемент ID:"))
                            {
                                idsToDelete.Add(inst.Id);
                            }
                        }
                    }

                    int deleted = 0;
                    foreach (ElementId id in idsToDelete)
                    {
                        doc.Delete(id);
                        deleted++;
                        Logger.Log($"Удален {id}", 2);
                    }

                    // Создание новых
                    int created = 0;
                    if (_selectedItems != null)
                    {
                        foreach (var item in _selectedItems)
                        {
                            try
                            {
                                if (item.Point == null || double.IsNaN(item.Point.X)) continue;

                                // Вычисляем точку на внешней грани основной стены (если есть данные)
                                XYZ placementPoint = item.Point;
                                if (item.OuterNormal != null && item.WallThickness > 1e-6)
                                {
                                    XYZ outwardDirection = item.IsWallFlipped ? -item.OuterNormal : item.OuterNormal;
                                    placementPoint = item.Point + outwardDirection * (item.WallThickness / 2.0);
                                    Logger.Log($"Point adjusted: from {item.Point} to {placementPoint}", 2);
                                }
                                else
                                {
                                    Logger.Log($"WallThickness={item.WallThickness}, OuterNormal={item.OuterNormal} – using original point.",2);
                                }

                                // Создание экземпляра
                                FamilyInstance inst = doc.Create.NewFamilyInstance(
                                    placementPoint, targetSymbol, StructuralType.NonStructural);

                                if (Math.Abs(item.Rotation) > 1e-6)
                                {
                                    Line axis = Line.CreateBound(placementPoint, placementPoint + XYZ.BasisZ);
                                    ElementTransformUtils.RotateElement(doc, inst.Id, axis, item.Rotation);
                                }

                                // Установка параметров
                                SetParameter(inst, adskHeightParamGuid, item.Height);
                                SetParameter(inst, adskWidthParamGuid, item.Width);
                                SetParameter(inst, adskDepthParamGuid, _userDepth);
                                SetParameter(inst, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, $"Исходный элемент ID: {item.OriginalId.IntegerValue}");

                                // Упрощённое соединение со стенами (только по bounding box)
                                JoinWithWallsSimple(doc, inst, placementPoint);

                                created++;
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Error creating {item.OriginalId}: {ex.Message}",4);
                            }
                        }
                    }

                    trans.Commit(); Logger.Log("Закрываем транзакцию", 1);
                    new InfoWindow400($"Удалено: {deleted}\nСоздано: {created}\nГлубина проема: {_userDepth * 304.8:F0} мм").ShowDialog();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"FATAL: {ex}",4);
                new InfoWindow400($"Сбой: {ex.Message}").ShowDialog();
            }
        }

        private void SetParameter(FamilyInstance inst, Guid paramGuid, double value)
        {
            Parameter p = inst.get_Parameter(paramGuid);
            if (p != null && !p.IsReadOnly)
            {
                p.Set(value);
                Logger.Log($"Set {p.Definition.Name} = {value}",2);
            }
            else
            {
                Logger.Log($"Parameter {p.Definition.Name} not found or read-only.",2);
            }
        }

        private void SetParameter(FamilyInstance inst, BuiltInParameter bip, string value)
        {
            Parameter p = inst.get_Parameter(bip);
            if (p != null && !p.IsReadOnly)
            {
                p.Set(value);
                Logger.Log($"Set built-in parameter {bip} = {value}",2);
            }
            else
            {
                Logger.Log($"Built-in parameter {bip} not found or read-only.",2);
            }
        }

        /// <summary>
        /// Упрощённый метод соединения проёма со стенами.
        /// Находит все стены в радиусе 1 метра и пытается соединиться с каждой.
        /// </summary>
        private void JoinWithWallsSimple(Document doc, FamilyInstance instance, XYZ point)
        {
            double tolerance = 3.28; // 1 метр в футах
            Outline outline = new Outline(point - new XYZ(tolerance, tolerance, tolerance),
                                          point + new XYZ(tolerance, tolerance, tolerance));
            BoundingBoxIntersectsFilter filter = new BoundingBoxIntersectsFilter(outline);

            List<Wall> walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WherePasses(filter)
                .Cast<Wall>()
                .ToList();

            Logger.Log($"JoinWithWallsSimple: found {walls.Count} walls near point.",2);

            foreach (Wall wall in walls)
            {
                try
                {
                    if (!JoinGeometryUtils.AreElementsJoined(doc, instance, wall))
                    {
                        JoinGeometryUtils.JoinGeometry(doc, instance, wall);
                        Logger.Log($"Successfully joined with wall {wall.Id}",2);
                    }
                    else
                    {
                        Logger.Log($"Already joined with wall {wall.Id}",2);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"JoinGeometry failed with wall {wall.Id}: {ex.Message}",4);
                }
            }
        }

        public string GetName() => "Обновление проёмов";
    }
}
