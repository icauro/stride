namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static FlagBulkAction? DrawFlagBulkActions() => FlagEnumPropertyControl.DrawBulkActions();
    private static Enum ApplyFlagBulkAction(Enum value, FlagBulkAction action) => FlagEnumSelection.Apply(value, action);
}
