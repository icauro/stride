using Stride.Core.Assets.Quantum;
using Stride.Assets.Presentation.NodePresenters.Rules;
using Stride.Navigation;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record GameSettingItem(GameSettingCollection Context, IAssetObjectNode Collection, NodeIndex OriginalIndex, ItemId? Id, IReadOnlyList<GameSettingField> Children)
{
    public GameSettingField Field => Context.Origin;
    public NodeIndex Index => Id is { } id ? Collection.IdToIndex(id) : OriginalIndex;
    public object? Value => Collection.Retrieve(Index);
    public Type DeclaredType => Context.ElementType!;
    public Type Type => Nullable.GetUnderlyingType(DeclaredType) ?? DeclaredType;
    public bool AllowsNull => Context.CollectionRules?.NotNullItems != true && (!DeclaredType.IsValueType || Nullable.GetUnderlyingType(DeclaredType) != null);
    public string DisplayName => Value is NavigationMeshGroup group ? NavigationNodeRules.GroupName(group) : Index.ToString();
    public string Key => Context.Key + "/" + (Id?.ToString() ?? OriginalIndex.ToString());
}
