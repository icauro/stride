using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackageMatrixValue(Package package, PackageSettingAccessor accessor, string label,
        bool editable, INodePresenter? presenter)
        => DrawPackageCoordinateValue(package, accessor, typeof(Stride.Core.Mathematics.Matrix), label,
            editable, presenter, 4, "##PackageMatrixCells", false);
}
