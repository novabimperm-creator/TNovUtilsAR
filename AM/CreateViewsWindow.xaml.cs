using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.DB;
using TNovCommon;

namespace TNovUtilsAR
{
    public partial class CreateViewsWindow : Window
    {
        private static readonly Regex TrailingDigits = new Regex(@"^(.*?)(\d+)$", RegexOptions.Compiled);

        public ViewCreationOptions Options { get; private set; }

        public CreateViewsWindow(Document doc)
        {
            InitializeComponent();

            var sources = new List<RoomSourceItem> { new RoomSourceItem(null) };
            sources.AddRange(
                new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Where(li => li.GetLinkDocument() != null)
                    .OrderBy(li => li.Name)
                    .Select(li => new RoomSourceItem(li)));
            SourceComboBox.ItemsSource = sources;
            SourceComboBox.SelectedIndex = sources.Count > 1 ? 1 : 0;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
            LevelComboBox.ItemsSource = levels;
            if (levels.Count > 0) LevelComboBox.SelectedIndex = 0;

            var templates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                .OrderBy(v => v.Name)
                .ToList();
            var psSeries = templates.Where(t => t.Name.EndsWith("-1")).ToList();

            GeneralTemplateComboBox.ItemsSource = templates;
            PkTemplateComboBox.ItemsSource = templates;
            PsTemplateComboBox.ItemsSource = templates;
            PsoGeneralTemplateComboBox.ItemsSource = templates;
            PsoPkTemplateComboBox.ItemsSource = templates;
            PsoPsTemplateComboBox.ItemsSource = templates;

            SelectByName(GeneralTemplateComboBox, templates, "Д_План_Р_АМ");
            SelectByName(PkTemplateComboBox, templates, "Д_План_Р_АМ_Квартира_М75");
            SelectByName(PsTemplateComboBox, psSeries, null);

            SelectByName(PsoGeneralTemplateComboBox, templates, "Д_План_Р_ПСО");
            SelectByName(PsoPkTemplateComboBox, templates, "Д_План_Р_ПСО_Квартира_М75");
            SelectByName(PsoPsTemplateComboBox, psSeries, null);
        }

        private static void SelectByName(System.Windows.Controls.ComboBox combo, IList<View> source, string exactName)
        {
            if (source == null || source.Count == 0) return;
            if (!string.IsNullOrEmpty(exactName))
            {
                var match = source.FirstOrDefault(v => v.Name == exactName);
                if (match != null) { combo.SelectedItem = match; return; }
            }
            combo.SelectedItem = source.First();
        }

        private void OnCreateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var level = LevelComboBox.SelectedItem as Level
                    ?? throw new InvalidOperationException("Выберите уровень.");
                var floor = FloorTextBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(floor))
                    throw new InvalidOperationException("Введите номер этажа.");

                var amSet = BuildViewSet("АМ", GeneralTemplateComboBox, PkTemplateComboBox, PsTemplateComboBox);
                var psoSet = BuildViewSet("ПСО", PsoGeneralTemplateComboBox, PsoPkTemplateComboBox, PsoPsTemplateComboBox);

                var source = SourceComboBox.SelectedItem as RoomSourceItem;
                Options = new ViewCreationOptions
                {
                    Level = level,
                    FloorLabel = floor,
                    LinkInstance = source?.Instance,
                    ViewSets = new List<ViewSetOptions> { amSet, psoSet }
                };
                DialogResult = true;
            }
            catch (Exception ex)
            {
                var info = new InfoWindow280($"Ошибка: {ex.Message}") { Owner = this };
                info.ShowDialog();
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static ViewSetOptions BuildViewSet(
            string token,
            System.Windows.Controls.ComboBox generalCombo,
            System.Windows.Controls.ComboBox pkCombo,
            System.Windows.Controls.ComboBox psCombo)
        {
            var psTemplate = psCombo.SelectedItem as View
                ?? throw new InvalidOperationException($"Выберите шаблон ПС для набора {token}.");
            var match = TrailingDigits.Match(psTemplate.Name);
            if (!match.Success)
                throw new InvalidOperationException(
                    $"Имя шаблона ПС '{psTemplate.Name}' (набор {token}) должно оканчиваться цифрой (например, '… Х-Х-1').");

            return new ViewSetOptions
            {
                Token = token,
                GeneralTemplateId = (generalCombo.SelectedItem as View)?.Id ?? ElementId.InvalidElementId,
                PkTemplateId = (pkCombo.SelectedItem as View)?.Id ?? ElementId.InvalidElementId,
                PsTemplatePrefix = match.Groups[1].Value
            };
        }

        private sealed class RoomSourceItem
        {
            public RevitLinkInstance Instance { get; }
            public string DisplayName { get; }

            public RoomSourceItem(RevitLinkInstance instance)
            {
                Instance = instance;
                DisplayName = instance == null ? "Главный документ" : (instance.Name ?? "<без имени>");
            }
        }
    }
}
