using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal interface IAssetTagDocument : IEditorDocument
{
    Asset AuthoredAsset { get; }
    IReadOnlyList<string> SavedTags { get; }
    long EditRevision { get; }
    Guid? NextUndoOperationId { get; }
    Guid? NextRedoOperationId { get; }
    PreparedAssetTagsEdit? PrepareTags(IReadOnlyList<string> tags);
    void SetTags(IReadOnlyList<string> tags);
}
