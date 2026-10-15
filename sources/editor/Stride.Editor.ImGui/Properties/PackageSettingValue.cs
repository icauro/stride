using Stride.Core.Annotations;

namespace Stride.Editor.ImGui;

// The original settings wrapper presents a typed member, allowing native
// member commands and math accessors to replace the staged key value.
internal interface IPackageSettingValue { }

internal sealed class PackageSettingValue<T> : IPackageSettingValue
{
    public PackageSettingValue(object? value) => TypedValue = (T)value!;
    [InlineProperty]
    [NotNull]
    public T TypedValue { get; set; }
}
