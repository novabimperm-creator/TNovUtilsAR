using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace TNovUtilsAR
{
    /// <summary>
    /// Приложение плагина: добавляет на ленту вкладку «Авторазмеры» с кнопкой,
    /// открывающей WPF-панель выбора функций (PluginPanelCommand).
    /// </summary>
    public class AppRibbon : IExternalApplication
    {
        private const string TAB = "Авторазмеры";
        private const string PANEL = "Инструменты";

        public Result OnStartup(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TAB); } catch { /* вкладка уже есть */ }

            RibbonPanel panel = app.CreateRibbonPanel(TAB, PANEL);
            string asm = Assembly.GetExecutingAssembly().Location;

            var data = new PushButtonData(
                "AvtPanel", "Панель\nфункций", asm, "TNovUtilsAR.PluginPanelCommand")
            {
                ToolTip = "Открыть панель плагина: авторазмеры и марки (помещения, квартиры, окна, двери)."
            };
            panel.AddItem(data);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
