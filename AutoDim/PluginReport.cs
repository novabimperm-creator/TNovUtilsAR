using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Autodesk.Revit.UI;
using TNovCommon;

namespace TNovUtilsAR
{
    /// <summary>
    /// Вывод итогов команд. В пакетном режиме (запуск нескольких функций из панели)
    /// отчёты не показываются по одному, а собираются и выводятся ОДНИМ маленьким
    /// окном итогов. При одиночном запуске команды показывается обычный TaskDialog.
    /// </summary>
    public static class PluginReport
    {
        private static bool _batch;
        private static readonly List<KeyValuePair<string, string>> _items =
            new List<KeyValuePair<string, string>>();

        /// <summary>Начать сбор итогов (перед запуском набора функций).</summary>
        public static void Begin()
        {
            _batch = true;
            _items.Clear();
        }

        /// <summary>Отчёт команды: в пакете — копится, иначе — показывается сразу. Всегда логируется.</summary>
        public static void Show(string title, string body)
        {
            Logger.Log($"{title}\n{body}", 5);
            if (_batch)
                _items.Add(new KeyValuePair<string, string>(title, body ?? ""));
            else
                TaskDialog.Show(title, body ?? "");
        }

        /// <summary>Завершить сбор и показать одно маленькое окно итогов.</summary>
        public static void End(IntPtr owner)
        {
            _batch = false;
            if (_items.Count == 0) return;

            var win = new SummaryWindow(_items);
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(win) { Owner = owner };
            win.ShowDialog();
            _items.Clear();
        }

        /// <summary>Строки «ИТОГО…» из тела отчёта (или последняя непустая строка).</summary>
        internal static IEnumerable<string> Totals(string body)
        {
            var lines = (body ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var totals = lines.Where(l => l.TrimStart().StartsWith("ИТОГО", StringComparison.OrdinalIgnoreCase)).ToList();
            if (totals.Count > 0) return totals.Select(l => l.Trim());
            var last = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            return last != null ? new[] { last.Trim() } : Enumerable.Empty<string>();
        }
    }

    /// <summary>Маленькое окно итогов в теме плагина.</summary>
    internal class SummaryWindow : Window
    {
        private static readonly Brush Bg     = Hex("#14201A");
        private static readonly Brush Card   = Hex("#1E2E26");
        private static readonly Brush Accent = Hex("#37C871");
        private static readonly Brush Muted  = Hex("#8FB3A2");

        public SummaryWindow(List<KeyValuePair<string, string>> items)
        {
            Title = "Итоги";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 640;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Bg;

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock
            {
                Text = "ИТОГИ",
                Foreground = Accent,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var list = new StackPanel();
            foreach (var it in items)
            {
                var card = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                card.Children.Add(new TextBlock
                {
                    Text = CleanTitle(it.Key),
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold
                });
                foreach (var line in PluginReport.Totals(it.Value))
                    card.Children.Add(new TextBlock
                    {
                        Text = line,
                        Foreground = Muted,
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });

                var border = new Border
                {
                    Background = Card,
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = card
                };
                list.Children.Add(border);
            }

            root.Children.Add(new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480
            });

            var close = new Button
            {
                Content = "Закрыть",
                Foreground = Brushes.White,
                Background = Card,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = HoverTemplate
            };
            close.Click += (s, e) => Close();
            root.Children.Add(close);

            Content = root;
        }

        // скруглённая кнопка: фон из Background, при наведении — зелёная подсветка
        private static readonly ControlTemplate HoverTemplate =
            (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "  <Border x:Name='b' CornerRadius='10' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'>" +
                "    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                "  </Border>" +
                "  <ControlTemplate.Triggers>" +
                "    <Trigger Property='IsMouseOver' Value='True'>" +
                "      <Setter TargetName='b' Property='Background' Value='#2A9D5A'/>" +
                "    </Trigger>" +
                "  </ControlTemplate.Triggers>" +
                "</ControlTemplate>");

        /// <summary>Название функции без версии: обрезает всё от « [».</summary>
        private static string CleanTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            int i = title.IndexOf(" [", StringComparison.Ordinal);
            return i > 0 ? title.Substring(0, i) : title;
        }

        private static SolidColorBrush Hex(string hex)
        {
            var c = (System.Windows.Media.Color)
                System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
