using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.UI.Panels;

namespace Stride.Editor.ImGui;

internal enum UIChildLayoutCommand
{
    MoveBack, MoveDown, MoveFront, MoveLeft, MoveRight, MoveUp,
    PinTopLeft, PinTop, PinTopRight, PinLeft, PinCenter, PinRight,
    PinBottomLeft, PinBottom, PinBottomRight, PinFront, PinMiddle, PinBack,
}

internal sealed partial class UIDocument
{
    private static (PropertyKey<int> Key, int Offset) GridLayoutCommand(UIChildLayoutCommand command) => command switch
    {
        UIChildLayoutCommand.MoveUp => (GridBase.RowPropertyKey, -1),
        UIChildLayoutCommand.MoveDown => (GridBase.RowPropertyKey, 1),
        UIChildLayoutCommand.MoveLeft => (GridBase.ColumnPropertyKey, -1),
        UIChildLayoutCommand.MoveRight => (GridBase.ColumnPropertyKey, 1),
        UIChildLayoutCommand.MoveBack => (GridBase.LayerPropertyKey, -1),
        UIChildLayoutCommand.MoveFront => (GridBase.LayerPropertyKey, 1),
        _ => throw new ArgumentException("This command is not supported by the parent layout.", nameof(command)),
    };
    internal bool CanChangeChildLayout(Guid id, UIChildLayoutCommand command)
    {
        var element = Find(id);
        if (element?.VisualParent is Canvas) return (int)command >= (int)UIChildLayoutCommand.PinTopLeft && CanChangeDependencyProperty(id, Canvas.PinOriginPropertyKey);
        if (element?.VisualParent is GridBase) return (int)command < (int)UIChildLayoutCommand.PinTopLeft && CanChangeDependencyProperty(id, GridLayoutCommand(command).Key);
        if (element?.VisualParent is not StackPanel stack || command is not UIChildLayoutCommand.MoveUp and not UIChildLayoutCommand.MoveDown) return false;
        var children = (Stride.Core.Assets.Quantum.IAssetObjectNode)ObjectNode(stack)[nameof(Panel.Children)].Target!;
        var index = new Stride.Core.Quantum.NodeIndex(stack.Children.IndexOf(element));
        return CanEditAssetSettingNode(children) && CanEditAssetSettingItem(children, index);
    }
    internal void ChangeChildLayout(Guid id, UIChildLayoutCommand command)
    {
        EndEdit();
        if (!CanChangeChildLayout(id, command)) throw new InvalidOperationException("The child layout property cannot be edited.");
        var element = Require(id);
        if (element.VisualParent is Canvas)
        {
            var origin = element.DependencyProperties.Get(Canvas.PinOriginPropertyKey);
            switch (command)
            {
                case UIChildLayoutCommand.PinTopLeft: origin.X = 0; origin.Y = 0; break;
                case UIChildLayoutCommand.PinTop: origin.X = 0.5f; origin.Y = 0; break;
                case UIChildLayoutCommand.PinTopRight: origin.X = 1; origin.Y = 0; break;
                case UIChildLayoutCommand.PinLeft: origin.X = 0; origin.Y = 0.5f; break;
                case UIChildLayoutCommand.PinCenter: origin.X = 0.5f; origin.Y = 0.5f; break;
                case UIChildLayoutCommand.PinRight: origin.X = 1; origin.Y = 0.5f; break;
                case UIChildLayoutCommand.PinBottomLeft: origin.X = 0; origin.Y = 1; break;
                case UIChildLayoutCommand.PinBottom: origin.X = 0.5f; origin.Y = 1; break;
                case UIChildLayoutCommand.PinBottomRight: origin.X = 1; origin.Y = 1; break;
                case UIChildLayoutCommand.PinFront: origin.Z = 1; break;
                case UIChildLayoutCommand.PinMiddle: origin.Z = 0.5f; break;
                case UIChildLayoutCommand.PinBack: origin.Z = 0; break;
                default: throw new InvalidOperationException("The pin origin command is unsupported.");
            }
            SetDependencyProperties([id], Canvas.PinOriginPropertyKey, origin);
        }
        else if (element.VisualParent is GridBase)
        {
            var (key, offset) = GridLayoutCommand(command);
            var current = element.DependencyProperties.Get(key);
            SetDependencyProperties([id], key, (int)Math.Clamp((long)current + offset, 0, int.MaxValue));
        }
        else if (element.VisualParent is StackPanel stack)
        {
            var index = stack.Children.IndexOf(element);
            if (index < 0) throw new InvalidOperationException("The UI element is not a child of this panel.");
            var target = index + (command == UIChildLayoutCommand.MoveUp ? -1 : 1);
            if (target < 0 || target >= stack.Children.Count) return;
            var children = (IAssetObjectNode)ObjectNode(stack)[nameof(Panel.Children)].Target!;
            // Native PanelViewModel changes only the actual child collection. The same
            // hierarchy design, base links and descendants remain published throughout.
            Mutate(() =>
            {
                children.Remove(element, new NodeIndex(index));
                children.Add(element, new NodeIndex(target));
            }, "Move " + (element.Name ?? element.GetType().Name));
        }
    }
}
