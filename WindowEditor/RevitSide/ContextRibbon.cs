using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using adWin = Autodesk.Windows;

namespace WindowEditor.RevitSide
{
    /// <summary>
    /// Контекстные кнопки «Редактировать» и «Комплектация» на вкладке «Изменить», когда выбрано окно редактора
    /// (как у ModPlus). Revit API этого не умеет — панель добавляется через AdWindows, а кнопки запускают
    /// обычные команды TNov через PostCommand по Id их кнопок в ленте.
    /// </summary>
    public static class ContextRibbon
    {
        const string PanelId = "TNov_WindowEditor_Context";
        static adWin.RibbonPanel _panel;
        static string _editId, _kitId;
        static ImageSource _image;
        static string _lastSelection = "";
        static bool _failed;

        /// <summary>Вызвать после создания ленты TNov (кнопки «Редактор окон» уже добавлены).</summary>
        public static void Init(UIControlledApplication app)
        {
            app.Idling += OnIdling;
        }

        static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_failed || !(sender is UIApplication uiapp)) return;
            try
            {
                var uidoc = uiapp.ActiveUIDocument;
                var ids = uidoc?.Selection.GetElementIds();
                string key = ids == null ? "" : ids.Count + ":" + string.Join(",", ids.Take(50).Select(i => i.ToString()));
                if (key == _lastSelection) return; // выбор не менялся — ничего не делаем
                _lastSelection = key;

                bool show = ids != null && ids.Count > 0 && ids.Count <= 500 &&
                            ids.Select(uidoc.Document.GetElement).Any(Accessories.IsEditorWindow);
                if (show && !EnsurePanel(uiapp)) return;
                if (_panel != null && _panel.IsVisible != show) _panel.IsVisible = show;
            }
            catch (Exception)
            {
                _failed = true; // неподдерживаемое API ленты могло измениться — просто без контекстных кнопок
            }
        }

        static bool EnsurePanel(UIApplication uiapp)
        {
            if (_panel != null) return true;
            var ribbon = adWin.ComponentManager.Ribbon;
            var modify = ribbon?.Tabs.FirstOrDefault(t => t.Id == "Modify");
            if (modify == null) return false;

            // Id кнопок TNov в ленте: по ним команды запускаются через PostCommand
            foreach (var item in ribbon.Tabs.SelectMany(t => t.Panels).SelectMany(p => Flatten(p.Source.Items)))
            {
                if (item.Id == null) continue;
                if (item.Id.EndsWith("%" + nameof(EditWindowCommand))) _editId = item.Id;
                else if (item.Id.EndsWith("%" + nameof(UpdateAccessoriesCommand))) _kitId = item.Id;
                else if (item.Id.EndsWith("%WindowEditor") && _image == null) _image = item.LargeImage;
            }
            if (_editId == null && _kitId == null) return false;

            var src = new adWin.RibbonPanelSource { Id = PanelId, Title = "Редактор окон" };
            if (_editId != null) src.Items.Add(Button("Редактировать\nокно", "Открыть схему выбранного окна в редакторе", _editId, uiapp));
            if (_kitId != null) src.Items.Add(Button("Комплектация", "Подогнать подоконники, отливы и откосы выбранных окон", _kitId, uiapp));
            _panel = new adWin.RibbonPanel { Source = src, IsVisible = false };
            modify.Panels.Add(_panel);
            return true;
        }

        static adWin.RibbonButton Button(string text, string tip, string commandId, UIApplication uiapp) => new adWin.RibbonButton
        {
            Id = PanelId + "_" + commandId.GetHashCode(),
            Text = text,
            ShowText = true,
            ShowImage = _image != null,
            LargeImage = _image,
            Image = _image,
            Size = adWin.RibbonItemSize.Large,
            Orientation = System.Windows.Controls.Orientation.Vertical,
            ToolTip = tip,
            CommandHandler = new PostCommand(uiapp, commandId)
        };

        static IEnumerable<adWin.RibbonItem> Flatten(IEnumerable<adWin.RibbonItem> items)
        {
            foreach (var i in items)
            {
                yield return i;
                if (i is adWin.RibbonListButton lb)
                    foreach (var c in Flatten(lb.Items)) yield return c;
                else if (i is adWin.RibbonRowPanel rp)
                    foreach (var c in Flatten(rp.Items)) yield return c;
            }
        }

        sealed class PostCommand : ICommand
        {
            readonly UIApplication _app;
            readonly string _id;
            public PostCommand(UIApplication app, string id) { _app = app; _id = id; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter)
            {
                var cmd = RevitCommandId.LookupCommandId(_id);
                if (cmd != null && _app.CanPostCommand(cmd)) _app.PostCommand(cmd);
            }
        }
    }
}
