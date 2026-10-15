using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    internal bool CanCopyGameSettingItem(GameSettingItem item)
        => gameSettingItemMembers.Contains(item) && ReferenceEquals(item.Context.Node, item.Collection)
            && (item.Id is { } id ? item.Collection.HasId(id) : (item.Collection.Indices ?? []).Contains(item.Index));

    internal string CopyGameSettingItem(GameSettingItem item)
    {
        CommitEdits();
        return CanCopyGameSettingItem(item) ? CopyAssetPropertyAccessor(new(item.Collection, item.Index)) : "";
    }

    internal bool CanPasteGameSettingItem(GameSettingItem item, string text)
        => CanCopyGameSettingItem(item) && CanEditPresentedGameSettingItem(item)
            && CanPasteAssetAccessors([new NodeAccessor(item.Collection, item.Index)], text);

    internal void PasteGameSettingItem(GameSettingItem item, string text, bool replace)
    {
        CommitEdits();
        if (!CanPasteGameSettingItem(item, text)) throw new InvalidOperationException("The clipboard cannot be pasted into this collection item.");
        PasteAssetAccessors([new NodeAccessor(item.Collection, item.Index)], text, replace);
    }
}
