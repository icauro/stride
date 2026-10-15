using Stride.Core.Extensions;

namespace Stride.Editor.ImGui;

internal enum FlagBulkAction { All, None, Invert }

internal static class FlagEnumSelection
{
    // Original commands invert declared individual flags, independently per owner.
    internal static Enum Apply(Enum value, FlagBulkAction action)
    {
        var type = value.GetType();
        if (action == FlagBulkAction.None) return (Enum)Enum.ToObject(type, 0);
        var all = EditorFlagEnums.GetIndividualFlags(type);
        return EditorFlagEnums.GetEnum(type, action == FlagBulkAction.All
            ? all : all.Except(EditorFlagEnums.GetIndividualFlags(value)));
    }
}
