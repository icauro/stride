using System.ComponentModel;

namespace Stride.Editor.ImGui;

/// <summary>The original dictionary property command's registered TypeConverter path.</summary>
internal static class NativeDictionaryKey
{
    internal static bool SupportsText(Type keyType) => keyType == typeof(string) || TypeDescriptor.GetConverter(keyType).CanConvertFrom(typeof(string));
    // Original AssetCollectionCommandRules.CanConstruct and AddPrimitiveKeyCommand default-key branch.
    internal static bool CanAdd(Type keyType) => !keyType.IsClass || keyType.GetConstructor(Type.EmptyTypes) != null || keyType == typeof(string);
    internal static object CreateForAdd(Type keyType, string? text)
    {
        if (!CanAdd(keyType)) throw new InvalidOperationException("The dictionary key cannot be constructed.");
        if (SupportsText(keyType)) return Parse(keyType, text ?? "");
        return Activator.CreateInstance(keyType) ?? throw new InvalidOperationException("The dictionary key constructor returned null.");
    }
    internal static object Parse(Type keyType, string text)
    {
        if (!SupportsText(keyType)) throw new InvalidOperationException("The dictionary key has no registered text converter.");
        var value = keyType == typeof(string) ? text : TypeDescriptor.GetConverter(keyType).ConvertFrom(text);
        if (value == null || !keyType.IsInstanceOfType(value)) throw new ArgumentException("The dictionary key converter returned an incompatible value.");
        return value;
    }
}
