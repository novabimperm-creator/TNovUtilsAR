using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtilsAR
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateApartmentViewsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "Нет активного документа.";
                return Result.Failed;
            }

            try
            {
                var window = new CreateViewsWindow(doc);
                new WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
                if (window.ShowDialog() != true) return Result.Cancelled;

                var result = new ViewCreationService(doc).Run(window.Options);
                var generalTokens = string.Join(", ", result.Generals.Keys);
                var summary = $"Создано видов:\n• общих: {result.Generals.Count} ({generalTokens})\n• ПК: {result.Pk.Count}\n• ПС: {result.Ps.Count}";
                if (!string.IsNullOrEmpty(result.Diagnostics))
                {
                    summary += "\n\n--- Диагностика ---\n" + result.Diagnostics;
                }
                new InfoWindow400(summary).ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                new InfoWindow280($"Ошибка: {ex.Message}").ShowDialog();
                return Result.Failed;
            }
        }

        
    }
}
