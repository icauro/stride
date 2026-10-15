using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal static class AssetSettingNumericMetadata
{
    internal static bool Supports(Type type) => type == typeof(float) || type == typeof(double) || type == typeof(decimal)
        || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
    internal static PropertyNumericRange? Read(IMemberNode node)
    {
        if (!Supports(node.Type)) return null;
        var declared = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DataMemberRangeAttribute>(node.MemberDescriptor.MemberInfo);
        var integral = node.Type != typeof(float) && node.Type != typeof(double) && node.Type != typeof(decimal);
        var byteSized = node.Type == typeof(byte) || node.Type == typeof(sbyte);
        // Original NumericValueNodeUpdater supplies type bounds and integral/byte
        // metadata; NumericTextBox supplies absent steps (1 and 10) and no implicit
        // fractional rounding. Read the exact native bounds, not a copied table.
        return new(new DataMemberRangeAttribute(
            declared?.Minimum ?? Convert.ToDouble(NumericValueNodeUpdater.GetMinimum(node.Type)),
            declared?.Maximum ?? Convert.ToDouble(NumericValueNodeUpdater.GetMaximum(node.Type)),
            declared?.SmallStep ?? 1, declared?.LargeStep ?? (byteSized ? 5 : 10),
            declared?.DecimalPlaces ?? 0), node.Type, integral || declared?.DecimalPlaces != null);
    }
}
