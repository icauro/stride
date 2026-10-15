using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private static Type PropertyCoordinateTypeForUI(Type declared)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        return type == typeof(UInt4) ? typeof(uint) : type == typeof(Color) ? typeof(byte)
            : type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(Rectangle) ? typeof(int) : typeof(float);
    }
}
