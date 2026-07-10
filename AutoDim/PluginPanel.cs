using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TNovUtilsAR
{
    /// <summary>
    /// Открывает WPF-панель плагина с выбором функций. Панель показывается модально
    /// в контексте команды, поэтому функции запускаются в валидном контексте Revit API.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PluginPanelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var win = new PluginPanelWindow(commandData, elements);
            new WindowInteropHelper(win) { Owner = commandData.Application.MainWindowHandle };
            win.ShowDialog();
            return Result.Succeeded;
        }
    }
}
