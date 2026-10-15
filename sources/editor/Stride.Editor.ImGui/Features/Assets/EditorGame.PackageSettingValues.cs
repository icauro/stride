using Stride.Core.Assets;
using Stride.Core.Settings;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawPackageSettingValue(Package package, PackageSettingAccessor accessor, Type declared, string label,
        object? value, string path, bool allowsNull, bool canEdit, Action<object?> set, INodePresenter? commandTarget = null)
    {
        var editable = canEdit && !package.IsReadOnly && package.State >= PackageState.AssetsReady
            && !IsAuthoringPipelineUpdating && SessionAuthoringHistory != null
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && !PackagePresenterReadOnly(commandTarget);
        var extensions = SessionAuthoringHistory?.Extensions;
        if (extensions?.HasPrimitiveEditor(declared) == true)
        {
            Gui.BeginDisabled(!editable);
            try
            {
                extensions.TryDrawPrimitive(new(declared, value, label, editable, next => Execute(() =>
                {
                    if (next == null && !allowsNull) throw new ArgumentException("This registered setting does not allow null.");
                    set(next);
                }), () => { }, commandTarget == null ? null : (name, parameter) =>
                    QueuePackageSettingCommand(package, accessor, commandTarget, name, parameter)));
            }
            finally { Gui.EndDisabled(); }
            return true;
        }
        if (path.Length == 0 && TryDrawPackageAcceptableValues(package, accessor, label, value, editable, allowsNull, set, commandTarget)) return true;
        if (TryDrawNullablePackageValue(package, accessor, declared, label, value, path, allowsNull, editable, set, commandTarget)) return true;
        if ((Nullable.GetUnderlyingType(declared) ?? declared) == typeof(bool))
        {
            DrawPackageBooleanValue(package, accessor, label, editable, set, commandTarget);
            return true;
        }
        if (Nullable.GetUnderlyingType(declared) is { } numeric && IsStockPackageNumeric(numeric))
        {
            DrawPackageSettingScalar(package, accessor, numeric, label.Length == 0 ? "##Numeric" : label,
                value, path, set, commandTarget, editable);
            return true;
        }
        if ((Nullable.GetUnderlyingType(declared) ?? declared).IsEnum)
        {
            DrawPackageEnumValue(package, accessor, declared, label, value, set, commandTarget, editable);
            return true;
        }
        if (declared == typeof(Quaternion))
        {
            DrawPackageRotationValue(package, accessor, label, path, editable, set, commandTarget);
            return true;
        }
        if (IsPackageVectorType(declared))
        {
            DrawPackageVectorValue(package, accessor, declared, label, editable, commandTarget);
            return true;
        }
        if (IsPackageColorType(declared))
        {
            DrawPackageColorValue(package, accessor, declared, label, editable, set, commandTarget);
            return true;
        }
        if (declared == typeof(Matrix))
        {
            DrawPackageMatrixValue(package, accessor, label, editable, commandTarget);
            return true;
        }
        if (IsPackageRectangleType(declared))
        {
            DrawPackageRectangleValue(package, accessor, declared, label, editable, commandTarget);
            return true;
        }
        if (ContentAssetReference.Supports(declared))
        {
            using var referencePhase = BeginPackageReferenceCommitPhase(package, accessor);
            Gui.TextUnformatted(label);
            DrawContentReferenceValue(declared, value, allowsNull, next => referencePhase.Submit(() => Execute(() =>
            {
                if (!editable) throw new InvalidOperationException("This registered package setting cannot be edited.");
                ContentAssetReference.Validate(declared, next, allowsNull); set(next);
            })), canEdit: editable);
            return true;
        }
        if (declared == typeof(UFile) || declared == typeof(UDirectory))
        {
            DrawPackagePathValue(package, accessor, declared, label, value, path, editable, set, commandTarget);
            return true;
        }
        if (declared == typeof(AngleSingle))
        {
            DrawPackageAngleValue(package, accessor, label, editable, set, commandTarget);
            return true;
        }
        if (declared == typeof(TimeSpan))
        {
            DrawPackageTimeSpanValue(package, accessor, label, editable, set, commandTarget);
            return true;
        }
        if (declared == typeof(DateTime))
        {
            DrawPackageDateTimeValue(package, accessor, label, editable, set, commandTarget);
            return true;
        }
        if (declared == typeof(Guid))
        {
            DrawPackageGuidValue(package, accessor, label, editable, set, commandTarget);
            return true;
        }
        if ((Nullable.GetUnderlyingType(declared) ?? declared) == typeof(char))
        {
            DrawPackageCharacterValue(package, accessor, declared, label, editable, allowsNull, set, commandTarget);
            return true;
        }
        return false;
    }
}
