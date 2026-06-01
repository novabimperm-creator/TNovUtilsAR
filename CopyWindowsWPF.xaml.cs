using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TNovUtilsAR
{
    public partial class CopyWindowsWPF : Window
    {
        private List<ItemViewModel> _viewModels;
        private UIApplication _uiapp;
        private CreateInstancesHandler _handler;
        private ExternalEvent _exEvent;

        public CopyWindowsWPF(List<ItemData> items, UIApplication uiapp,
                          CreateInstancesHandler handler, ExternalEvent exEvent)
        {
            InitializeComponent();
            _uiapp = uiapp;
            _handler = handler;
            _exEvent = exEvent;

            _viewModels = items.Select(item => new ItemViewModel
            {
                OriginalId = item.OriginalId,
                Category = item.Category,
                TypeName = item.TypeName,
                Height = item.Height,
                Width = item.Width,
                Point = item.Point,
                Rotation = item.Rotation,
                IsSelected = false
            }).ToList();

            // Создаём представление с группировкой по категории
            ICollectionView view = CollectionViewSource.GetDefaultView(_viewModels);
            view.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
            ItemsListView.ItemsSource = view;

            // Устанавливаем выбранный элемент ComboBox (это вызовет событие, но ItemsListView уже инициализирован)
            CategoryFilterCombo.SelectedIndex = 0;
        }

        private double GetUserDepthInFeet()
        {
            if (double.TryParse(DepthTextBox.Text, out double mm) && mm > 0)
                return mm / 304.8;
            else
                return 0.5; // 152.4 мм
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (ItemsListView.ItemsSource is ICollectionView view)
            {
                foreach (ItemViewModel vm in view.Cast<ItemViewModel>())
                {
                    vm.IsSelected = true;
                }
            }
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            if (ItemsListView.ItemsSource is ICollectionView view)
            {
                foreach (ItemViewModel vm in view.Cast<ItemViewModel>())
                {
                    vm.IsSelected = false;
                }
            }
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = _viewModels.Where(vm => vm.IsSelected)
                    .Select(vm => new ItemData
                    {
                        OriginalId = vm.OriginalId,
                        Category = vm.Category,
                        TypeName = vm.TypeName,
                        Point = vm.Point,
                        Rotation = vm.Rotation,
                        Height = vm.Height,
                        Width = vm.Width,
                        WallThickness = 0,
                        OuterNormal = null,
                        IsWallFlipped = false,
                        LinkDocumentPath = null
                    }).ToList();

                if (selected.Count == 0)
                {
                    MessageBox.Show("Не выбрано ни одного элемента.");
                    return;
                }

                _handler.UserDepth = GetUserDepthInFeet();
                _handler.SelectedItems = selected;
                _exEvent.Raise();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}");
            }
        }

        private void CategoryFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Защита от вызова до полной инициализации
            if (ItemsListView?.ItemsSource is ICollectionView view)
            {
                ComboBoxItem selectedItem = CategoryFilterCombo.SelectedItem as ComboBoxItem;
                if (selectedItem == null) return;

                string filter = selectedItem.Content.ToString();
                if (filter == "Все")
                {
                    view.Filter = null;
                }
                else if (filter == "Окна")
                {
                    view.Filter = obj => ((ItemViewModel)obj).Category == "Окна";
                }
                else if (filter == "Двери")
                {
                    view.Filter = obj => ((ItemViewModel)obj).Category == "Двери";
                }
            }
        }

        private void Border_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {

        }

        private void escButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close(); // закрытие окна
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string commandText = @"https://portal.talan.group/knowledge/proektirovanie/";
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = commandText;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }
    }

    public class ItemViewModel : INotifyPropertyChanged
    {
        public ElementId OriginalId { get; set; }
        public string Category { get; set; }
        public string TypeName { get; set; }
        public XYZ Point { get; set; }
        public double Rotation { get; set; }
        public double Height { get; set; }
        public double Width { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
        }

        public double HeightMm => Height * 304.8;
        public double WidthMm => Width * 304.8;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}