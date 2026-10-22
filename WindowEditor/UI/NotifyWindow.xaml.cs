using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace WindowEditor.UI
{
    /// <summary>Сообщение или выбор действия в стиле TNov — вместо TaskDialog.</summary>
    public partial class NotifyWindow : Window
    {
        /// <summary>Номер нажатой кнопки; −1 — окно закрыто крестиком.</summary>
        public int Choice { get; private set; } = -1;

        NotifyWindow(string title, string text, string[] options, string[] hints)
        {
            InitializeComponent();
            TitleText.Text = (title ?? "").ToUpperInvariant();
            MessageText.Text = text ?? "";
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var b = new Button
                {
                    Style = (Style)FindResource(i == 0 ? "RunButtonStyle" : "SecondaryButtonStyle"),
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = options[i], TextWrapping = TextWrapping.Wrap });
                if (hints != null && i < hints.Length && !string.IsNullOrEmpty(hints[i]))
                    content.Children.Add(new TextBlock { Text = hints[i], FontSize = 11, FontWeight = FontWeights.Normal, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                b.Content = content;
                b.Click += (s, e) => { Choice = index; DialogResult = true; };
                Buttons.Children.Add(b);
            }
        }

        void Title_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        static int Run(string title, string text, string[] options, string[] hints = null)
        {
            var w = new NotifyWindow(title, text, options, hints);
            try
            {
                var owner = Process.GetCurrentProcess().MainWindowHandle;
                if (owner != IntPtr.Zero) new WindowInteropHelper(w).Owner = owner;
                else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            catch { w.WindowStartupLocation = WindowStartupLocation.CenterScreen; }
            w.ShowDialog();
            return w.Choice;
        }

        /// <summary>Сообщение с кнопкой «Понятно».</summary>
        public static void Show(string title, string text) => Run(title, text, new[] { "Понятно" });

        /// <summary>Вопрос да/нет: true — первая кнопка.</summary>
        public static bool Ask(string title, string text, string yes = "Да", string no = "Нет") =>
            Run(title, text, new[] { yes, no }) == 0;

        /// <summary>Выбор из вариантов (с пояснениями под кнопками): номер варианта или −1.</summary>
        public static int Choose(string title, string text, string[] options, string[] hints = null) =>
            Run(title, text, options, hints);
    }
}
