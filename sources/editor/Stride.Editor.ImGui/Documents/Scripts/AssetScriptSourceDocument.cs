using System.Text;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Canonical native source asset; tags follow the native in-session source metadata policy.</summary>
internal sealed class AssetScriptSourceDocument : ScriptSourceDocument, IAssetTagDocument
{
    private readonly SourceCodeAsset authoredAsset;
    private string[] savedTags;
    internal AssetScriptSourceDocument(AssetItem item, EditorSessionHistory? history = null) : base(item, history)
    { authoredAsset = (SourceCodeAsset)item.Asset; savedTags = authoredAsset.Tags.ToArray(); }
    internal AssetScriptSourceDocument(string path, SourceCodeAsset asset, string text, Encoding encoding, EditorSessionHistory history)
        : base(path, asset, text, encoding, history)
    { authoredAsset = asset; savedTags = asset.Tags.ToArray(); }
    public Asset AuthoredAsset => authoredAsset;
    public IReadOnlyList<string> SavedTags => savedTags;
    public long EditRevision => SourceEditRevision;
    public Guid? NextUndoOperationId => SourceNextUndoOperationId;
    public Guid? NextRedoOperationId => SourceNextRedoOperationId;
    public override bool IsDirty => base.IsDirty || !authoredAsset.Tags.SequenceEqual(savedTags);
    public PreparedAssetTagsEdit? PrepareTags(IReadOnlyList<string> tags) => PrepareSourceAssetTags(tags);
    public void SetTags(IReadOnlyList<string> tags)
    {
        if (PrepareTags(tags) is not { } edit) return;
        try { edit.Apply(); edit.Commit(); }
        catch { edit.Restore(); throw; }
    }
    // SourceCodeAsset.Save writes native source text only, exactly as in Game Studio.
    protected override void MarkSourceMetadataSaved() => savedTags = authoredAsset.Tags.ToArray();
}
