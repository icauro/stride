using Stride.Assets.Sprite;
using Stride.UI.Assets;
using Stride.Core.Assets;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record SpecializedNameWorkflowState(string? AssetId, string? Path, string? Name, string? ItemIdentity,
    int Count, bool Dirty, bool CanUndo, bool CanRedo);

internal sealed partial class EditorGame
{
    internal string? InspectActiveAuthoredDocumentTabControl()
        => documents.Active is { } entry ? "document-tab/" + entry.Id : null;

    internal SpecializedNameWorkflowState InspectSpriteAuthoredName()
    {
        var owner = documents.Active?.Document as AssetMetadataDocument;
        var sheet = owner?.AuthoredAsset as SpriteSheetAsset;
        var identity = sheet?.Sprites.Count > 0 && CollectionItemIdHelper.TryGetCollectionItemIds(sheet.Sprites, out var ids) ? ids[0].ToString() : null;
        return new(sheet?.Id.ToString(), owner?.Path, sheet?.Sprites.FirstOrDefault()?.Name, identity, sheet?.Sprites.Count ?? 0,
            owner?.IsDirty ?? false, owner?.CanUndo ?? false, owner?.CanRedo ?? false);
    }
    internal SpecializedNameWorkflowState InspectUiAuthoredName()
    {
        var owner = documents.Active?.Document as UIDocument;
        var element = owner?.Find(UiWorkflowTest.AuthoredUiButtonId);
        return new(owner?.Asset.Id.ToString(), owner?.Path, element?.Name, element?.Id.ToString(), owner?.Asset.Hierarchy.Parts.Count ?? 0,
            owner?.IsDirty ?? false, owner?.CanUndo ?? false, owner?.CanRedo ?? false);
    }
}
