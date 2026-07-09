using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
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

    /// <summary>
    /// Панель плагина: карточки функций работают как переключатели (клик — выбрать/
    /// снять). Кнопка «Оформить» запускает все выбранные функции по порядку.
    /// </summary>
    internal class PluginPanelWindow : Window
    {
        private readonly ExternalCommandData _data;
        private readonly ElementSet _elements;
        private readonly List<Item> _items = new List<Item>();

        // тёмно-зелёная тема плагина
        private static readonly Brush Bg       = Hex("#14201A");
        private static readonly Brush Card     = Hex("#1E2E26");
        private static readonly Brush Selected = Hex("#2A9D5A");
        private static readonly Brush Accent   = Hex("#37C871");
        private static readonly Brush Muted    = Hex("#8FB3A2");
        private static readonly Brush Dark     = Hex("#14201A");

        private class Item
        {
            public Button Btn;
            public Func<IExternalCommand> Make;
            public bool On;
        }

        public PluginPanelWindow(ExternalCommandData data, ElementSet elements)
        {
            _data = data; _elements = elements;

            Title = "Авторазмеры";
            Width = 360;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Bg;

            var root = new StackPanel { Margin = new Thickness(18) };

            root.Children.Add(new TextBlock
            {
                Text = "АВТОРАЗМЕРЫ",
                Foreground = Accent,
                FontSize = 22,
                FontWeight = FontWeights.Bold
            });
            root.Children.Add(new TextBlock
            {
                Text = "Выберите функции и нажмите «Оформить»",
                Foreground = Muted,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 16)
            });

            root.Children.Add(Toggle("Авторазмеры",
                "Оси, общий размер, цепочки проёмов по фасадам",
                () => new AutoDimensionGridsCommand()));
            root.Children.Add(Toggle("Метки помещений",
                "Имя и площадь; лоджии — с коэффициентом",
                () => new AutoRoomTagsCommand()));
            root.Children.Add(Toggle("Марки МОП",
                "Номер в круге для МОП и технических",
                () => new AutoMopTagsCommand()));
            root.Children.Add(Toggle("Марки квартир",
                "Одна на квартиру/офис, вынос за оси, выноски",
                () => new AutoApartmentTagsCommand()));
            root.Children.Add(Toggle("Марки окон",
                "Окна «Ок-…» и витражи, зазор 700 мм, поворот по стене",
                () => new AutoWindowTagsCommand()));
            root.Children.Add(Toggle("Марки дверей",
                "По центру двери, без дверных проёмов",
                () => new AutoDoorTagsCommand()));

            // «Оформить» — запускает все выбранные функции
            var run = ActionButton("Оформить", Accent, Dark, RunSelected);
            run.Margin = new Thickness(0, 6, 0, 10);
            root.Children.Add(run);

            root.Children.Add(ActionButton("Закрыть", Card, Brushes.White, Close));

            Content = root;
        }

        /// <summary>Запускает по порядку все выбранные функции, затем снимает выбор.</summary>
        private void RunSelected()
        {
            var chosen = _items.Where(i => i.On).ToList();
            if (chosen.Count == 0)
            {
                TaskDialog.Show("Авторазмеры", "Выберите хотя бы одну функцию.");
                return;
            }

            // пакетный режим: отчёты не показываются по одному, а собираются в одно
            // маленькое окно итогов
            PluginReport.Begin();
            foreach (var it in chosen) Run(it.Make());
            foreach (var it in chosen) SetOn(it, false);
            PluginReport.End(_data.Application.MainWindowHandle);
        }

        private void Run(IExternalCommand cmd)
        {
            string msg = "";
            try { cmd.Execute(_data, ref msg, _elements); }
            catch (Exception ex) { TaskDialog.Show("Ошибка", ex.Message); }
        }

        /// <summary>Карточка-переключатель: клик выделяет/снимает выделение.</summary>
        private Button Toggle(string title, string subtitle, Func<IExternalCommand> make)
        {
            var btn = MakeCard(title, subtitle, HorizontalAlignment.Left);
            btn.Background = Card;
            var item = new Item { Btn = btn, Make = make, On = false };
            _items.Add(item);
            btn.Click += (s, e) => SetOn(item, !item.On);
            return btn;
        }

        private void SetOn(Item item, bool on)
        {
            item.On = on;
            item.Btn.Background = on ? Selected : Card;
        }

        /// <summary>Обычная кнопка действия (по центру, заданный цвет).</summary>
        private Button ActionButton(string title, Brush bg, Brush fg, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg
            };
            var btn = new Button
            {
                Content = tb,
                Background = bg,
                Margin = new Thickness(0, 0, 0, 0),
                Padding = new Thickness(14, 12, 14, 12),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = ButtonTemplate
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        /// <summary>Скруглённая карточка с заголовком и подписью.</summary>
        private Button MakeCard(string title, string subtitle, HorizontalAlignment align)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            });
            if (!string.IsNullOrEmpty(subtitle))
                content.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11,
                    Foreground = Muted,
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });

            return new Button
            {
                Content = content,
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(14, 11, 14, 11),
                HorizontalContentAlignment = align,
                Cursor = System.Windows.Input.Cursors.Hand,
                Template = ButtonTemplate
            };
        }

        // скруглённый шаблон: фон берётся из Background кнопки, ховер слегка затемняет
        private static readonly ControlTemplate ButtonTemplate =
            (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
                " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "  <Border x:Name='b' CornerRadius='10' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}'>" +
                "    <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>" +
                "  </Border>" +
                "  <ControlTemplate.Triggers>" +
                "    <Trigger Property='IsMouseOver' Value='True'>" +
                "      <Setter TargetName='b' Property='Opacity' Value='0.85'/>" +
                "    </Trigger>" +
                "  </ControlTemplate.Triggers>" +
                "</ControlTemplate>");

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
