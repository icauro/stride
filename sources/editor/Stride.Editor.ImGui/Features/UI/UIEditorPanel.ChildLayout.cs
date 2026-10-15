using Stride.UI;
using Stride.UI.Panels;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private void DrawChildLayoutCommands(UIDocument source, UIElement element)
    {
        if (element.VisualParent is Canvas && host.BeginMenu("Pin origin"))
        {
            Item("Top left", UIChildLayoutCommand.PinTopLeft); Item("Top", UIChildLayoutCommand.PinTop); Item("Top right", UIChildLayoutCommand.PinTopRight);
            Item("Left", UIChildLayoutCommand.PinLeft); Item("Center", UIChildLayoutCommand.PinCenter); Item("Right", UIChildLayoutCommand.PinRight);
            Item("Bottom left", UIChildLayoutCommand.PinBottomLeft); Item("Bottom", UIChildLayoutCommand.PinBottom); Item("Bottom right", UIChildLayoutCommand.PinBottomRight);
            Item("Front", UIChildLayoutCommand.PinFront); Item("Middle", UIChildLayoutCommand.PinMiddle); Item("Back", UIChildLayoutCommand.PinBack);
            Gui.EndMenu();
        }
        if (element.VisualParent is GridBase)
        {
            Group("Row", "Move up", UIChildLayoutCommand.MoveUp, "Move down", UIChildLayoutCommand.MoveDown);
            Group("Column", "Move left", UIChildLayoutCommand.MoveLeft, "Move right", UIChildLayoutCommand.MoveRight);
            Group("Layer", "Move back", UIChildLayoutCommand.MoveBack, "Move front", UIChildLayoutCommand.MoveFront);
        }
        if (element.VisualParent is StackPanel)
        { Item("Move up", UIChildLayoutCommand.MoveUp); Item("Move down", UIChildLayoutCommand.MoveDown); }
        void Item(string label, UIChildLayoutCommand command)
        {
            if (host.DrawMenuItem(label, "", false, source.CanChangeChildLayout(element.Id, command)))
                QueueUICommand(source, [element.Id], true, (asset, ids) => asset.ChangeChildLayout(ids[0], command));
        }
        void Group(string label, string first, UIChildLayoutCommand firstCommand, string second, UIChildLayoutCommand secondCommand)
        {
            if (!host.BeginMenu(label)) return;
            Item(first, firstCommand); Item(second, secondCommand); Gui.EndMenu();
        }
    }
}
