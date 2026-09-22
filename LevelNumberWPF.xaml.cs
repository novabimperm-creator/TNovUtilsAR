using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

using TNovCommon;

namespace TNovUtilsAR
{
    /// <summary>
    /// Логика взаимодействия для LevelNumberWPF.xaml
    /// </summary>
    public partial class LevelNumberWPF : Window
    {
        public LevelNumberWPF(LevelNumberViewModel viewModel)
        {
            InitializeComponent();
            this.SizeToContent = SizeToContent.Height;
            DataContext = viewModel;
        }
        private void acceptButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            this.Close(); // закрытие окна
        }

        private void escButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close(); // закрытие окна
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            HelpLinks.ShowHelp("Эт.Номер");
        }
    }
    public class InverseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => !(bool)value; // Инвертирует значение

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
