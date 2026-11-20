#if !BROILER_UI_HOSTED_RICH_EDIT_MENU
using System;
using Broiler.Graphics.Geometry;
using Broiler.UI;
using Broiler.UI.Menu;
using Broiler.UI.Menu.Standard;
using Broiler.UI.RichEdit;
using Broiler.UI.RichEdit.Standard;

namespace Broiler.Writer;

internal static partial class WriterPageLayout
{
    internal sealed partial class PageLayoutContent
    {
        // Remove when the minimum Broiler.UI package can host the shared RichEdit popup.
        private StandardMenu? _legacyContextMenu;

        partial void InitializeLegacyPageContextMenu()
        {
            _legacyContextMenu = new StandardMenu
            {
                PresentationMode = UiMenuPresentationMode.ContextMenu,
                PopupBackground = WriterPalette.MenuPopup,
            };
            _legacyContextMenu.ItemInvoked += (_, e) =>
            {
                if (_body is StandardRichEdit editor && Enum.TryParse(e.Item.Id, out RichEditCommand command))
                    editor.InvokeContextMenuCommand(command);
            };
            AddChild(_legacyContextMenu);
        }

        private bool OpenLegacyPageContextMenu(StandardRichEdit editor, BPoint point)
        {
            var menu = _legacyContextMenu!;
            menu.SetItems([
                Item(RichEditCommand.Cut, "Cut", "Ctrl+X"),
                Item(RichEditCommand.Copy, "Copy", "Ctrl+C"),
                Item(RichEditCommand.Paste, "Paste", "Ctrl+V"),
                new UiMenuItem("separator", "") { IsSeparator = true },
                Item(RichEditCommand.SelectAll, "Select All", "Ctrl+A"),
            ]);
            var anchor = new BRect(point.X, point.Y, 0, 0);
            menu.PopupAnchorBounds = anchor;
            menu.Arrange(anchor);
            return menu.OpenPopup();

            UiMenuItem Item(RichEditCommand command, string title, string shortcut)
            {
                bool enabled = editor.GetCommandState(command).IsEnabled;
                if (enabled && command == RichEditCommand.Paste)
                    enabled = Session?.Host is IUiClipboardHost clipboard &&
                        (!clipboard.CanQueryTextSynchronously || (clipboard.TryGetText(out string value) && !string.IsNullOrEmpty(value)));
                return new UiMenuItem(command.ToString(), title) { IsEnabled = enabled, Accelerator = shortcut };
            }
        }
    }
}
#endif
