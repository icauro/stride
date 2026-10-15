using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Reflection;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawNullablePackageValue(Package package, PackageSettingAccessor accessor, Type declared,
        string label, object? value, string path, bool allowsNull, bool editable, Action<object?> set, INodePresenter? presenter)
    {
        var underlying = Nullable.GetUnderlyingType(declared);
        if (underlying?.IsStruct() != true || !IsPackageSettingScalar(underlying) && underlying != typeof(Quaternion) && underlying != typeof(Matrix) && underlying != typeof(AngleSingle) && !IsPackageVectorType(underlying) && !IsPackageColorType(underlying) && !IsPackageRectangleType(underlying)) return false;
        var entries = PackageFactoryEntries(presenter, underlying, allowsNull);
        var entry = value == null ? entries.OfType<AbstractNodeType>().FirstOrDefault(candidate => candidate.Type == underlying)
            : entries.FirstOrDefault(candidate => candidate is AbstractNodeValue { Value: null });
        using var row = NativeNullableRow.Begin("##NullableSetting", true);
        Gui.BeginDisabled(!editable || entry == null);
        if (DrawNativeNullablePresence(value != null, out _, allowsNull) && entry != null)
            Execute(() => SelectPackageFactoryEntry(package, accessor, presenter, entries, entry, value,
                next => { set(next); CommitPackageSettingEdits(package, accessor, retainPresentation: true); }, "CreateNewInstance"));
        Gui.EndDisabled();
        row.ValueColumn();
        if (value == null)
        {
            Gui.TextUnformatted(EditorLocalization.Text("(null)"));
            return true;
        }
        if (!TryDrawPackageSettingValue(package, accessor, underlying, label, value, path, false, editable, set, presenter))
            DrawPackageSettingScalar(package, accessor, underlying, label, value, path, set, presenter, editable);
        return true;
    }
}
