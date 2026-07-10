using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WpfToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using TNovCommon;

namespace TNovUtilsAR
{
    /// <summary>
    /// Панель плагина: карточки функций работают как переключатели (клик — выбрать/
    /// снять). Кнопка «Оформить» запускает все выбранные функции по порядку.
    /// </summary>
    public partial class PluginPanelWindow : Window
    {
        private readonly ExternalCommandData _data;
        private readonly ElementSet _elements;
        private readonly List<(WpfToggleButton Toggle, Func<IExternalCommand> Make)> _items;

        public PluginPanelWindow(ExternalCommandData data, ElementSet elements)
        {
            InitializeComponent();

            _data = data;
            _elements = elements;
            _items = new List<(WpfToggleButton, Func<IExternalCommand>)>
            {
                (AutoDimToggle, () => new AutoDimensionGridsCommand()),
                (RoomTagsToggle, () => new AutoRoomTagsCommand()),
                (MopTagsToggle, () => new AutoMopTagsCommand()),
                (ApartmentTagsToggle, () => new AutoApartmentTagsCommand()),
                (WindowTagsToggle, () => new AutoWindowTagsCommand()),
                (DoorTagsToggle, () => new AutoDoorTagsCommand())
            };
        }

        private void OnRunClick(object sender, RoutedEventArgs e) => RunSelected();

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

        /// <summary>Запускает по порядку все выбранные функции, затем снимает выбор.</summary>
        private void RunSelected()
        {
            var chosen = _items.Where(i => i.Toggle.IsChecked == true).ToList();
            if (chosen.Count == 0)
            {
                TaskDialog.Show("Авторазмеры", "Выберите хотя бы одну функцию.");
                return;
            }

            PluginReport.Begin();
            foreach (var it in chosen) Run(it.Make());
            foreach (var it in chosen) it.Toggle.IsChecked = false;
            PluginReport.End(_data.Application.MainWindowHandle);
        }

        private void Run(IExternalCommand cmd)
        {
            string msg = "";
            try { cmd.Execute(_data, ref msg, _elements); }
            catch (Exception ex) { TaskDialog.Show("Ошибка", ex.Message); }
        }
    }
}
