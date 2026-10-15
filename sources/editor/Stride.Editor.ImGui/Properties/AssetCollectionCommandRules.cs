using Stride.Core.Assets;
using System;
using Stride.Core.Reflection;
using Stride.Core.Serialization;

namespace Stride.Editor.ImGui;

/// <summary>Shared eligibility of the original native collection commands.</summary>
internal static class AssetCollectionCommandRules
{
    public static bool CanConstruct(Type type) => !type.IsClass || type.GetConstructor(Type.EmptyTypes) != null || type == typeof(string);
    public static bool IsReferenceType(Type type) => AssetRegistry.IsExactContentType(type)
        || typeof(AssetReference).IsAssignableFrom(type) || UrlReferenceBase.IsUrlReferenceType(type);
    public static bool CanAdd(Type type) => CanConstruct(type) || type.IsAbstract || type.IsNullable() || IsReferenceType(type);
    public static object? CreateItemInstance(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsAbstract ? null : type == typeof(string) ? "" : ObjectFactoryRegistry.NewInstance(type);
    }
    public static object? CreateDictionaryItem(Type type)
    {
        var instance = CreateItemInstance(type);
        return IsReferenceType(type) ? type.Default() : instance;
    }
}
