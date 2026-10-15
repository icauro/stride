using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal interface IHierarchyCollectionValue
{
    ComponentField Field { get; }
    IAssetObjectNode Collection { get; }
    NodeIndex Index { get; }
    object? Value { get; }
    Type DeclaredType { get; }
    Type Type { get; }
    bool AllowsNull { get; }
    bool IsSceneReference { get; }
    bool IsContentReference { get; }
    bool SupportsSimpleEditing { get; }
    IReadOnlyList<ComponentEnumOption> EnumOptions { get; }
    IReadOnlyList<PropertyNumericRange?> AxisNumericRanges { get; }
    string IdentityKey { get; }
}
