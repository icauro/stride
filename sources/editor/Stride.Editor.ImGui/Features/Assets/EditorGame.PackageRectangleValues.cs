using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool IsPackageRectangleType(Type type) => type == typeof(Rectangle) || type == typeof(RectangleF);

    private void DrawPackageRectangleValue(Package package, PackageSettingAccessor accessor, Type type,
        string label, bool editable, INodePresenter? presenter)
        => DrawPackageCoordinateValue(package, accessor, type, label, editable, presenter,
            4, "##PackageRectangleCoordinates", false, axis =>
            {
                Gui.TextUnformatted("XYWH"[axis].ToString());
                Gui.SameLine();
            });
}
