using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;

namespace Stride.Editor.ImGui;

internal sealed partial class VisualScriptDocument : AssetMetadataDocument
{
    internal VisualScriptDocument(string path, EditorSessionHistory history, bool readOnly = false) : base(path, history, readOnly)
    {
        if (AuthoredAsset is not VisualScriptAsset) throw new InvalidDataException("The file is not a visual-script asset.");
        Stride.Assets.Quantum.VisualScriptLegacyReferenceNormalizer.Normalize(Asset);
        SelectedMethodId = Asset.Methods.FirstOrDefault()?.Id;
    }
    internal VisualScriptDocument(string path, AssetLoadResult<Asset> loaded, byte[] baseline, EditorSessionHistory? history, bool readOnly)
        : base(path, loaded, baseline, history, readOnly)
    {
        if (AuthoredAsset is not VisualScriptAsset) throw new InvalidDataException("The migrated file is not a visual-script asset.");
        Stride.Assets.Quantum.VisualScriptLegacyReferenceNormalizer.Normalize(Asset);
        SelectedMethodId = Asset.Methods.FirstOrDefault()?.Id;
    }
    internal VisualScriptAsset Asset => (VisualScriptAsset)AuthoredAsset;
    internal Guid SymbolDragIdentity { get; } = Guid.NewGuid();
    internal Guid? SelectedMethodId { get; set; }
    internal Action<VisualScriptDocument>? UpdateGeneratedOutput { get; set; }
    internal Action<VisualScriptDocument>? SaveGeneratedOutput { get; set; }
    internal bool GeneratedOutputInitialized { get; set; }
    internal Method? SelectedMethod => Asset.Methods.FirstOrDefault(method => method.Id == SelectedMethodId);
    internal IObjectNode ObjectNode(object owner) => AuthoringGraph.Container.NodeContainer.GetOrCreateNode(owner)!;
    internal IAssetObjectNode CollectionNode(object owner, string member) => (IAssetObjectNode)ObjectNode(owner)[member].Target!;
    internal bool CanEditMember(object owner, string member)
    {
        var node = ObjectNode(owner).TryGetChild(member);
        return node != null && CanEditAssetSettingNode(node) && CanAssignAuthoringNode(node);
    }
    internal void SetMember(object owner, string member, object? value)
    {
        if (!CanEditMember(owner, member)) throw new InvalidOperationException("This visual-script member is not editable.");
        var node = ObjectNode(owner)[member];
        if (Equals(node.Retrieve(), value)) return;
        ApplyVisualMutation(() =>
        {
            node.Update(value);
        }, "Edit " + member);
    }
    private void MutateCollection(IAssetObjectNode node, Action edit, string title)
    {
        if (!CanEditAssetSettingNode(node)) throw new InvalidOperationException("This visual-script collection is not editable.");
        ApplyVisualMutation(edit, title);
    }
    private void ApplyVisualMutation(Action edit, string title)
        => ApplyInheritedAssetMutation(edit, title);
    private void RequireMethod(Method method)
    {
        if (!Asset.Methods.Contains(method)) throw new InvalidOperationException("The function is not owned by this visual script.");
    }
    protected override AssetMetadataDocument CreateLoadedGeneration(string path, AssetLoadResult<Asset> restored, byte[] baseline,
        EditorSessionHistory? history, bool readOnly)
    {
        var next = new VisualScriptDocument(path, restored, baseline, history, readOnly);
        next.SelectedMethodId = SelectedMethodId; return next;
    }
    internal Action<VisualScriptDocument>? PrepareGeneratedSave { get; set; }
    public override void Save() { PrepareGeneratedSave?.Invoke(this); base.Save(); SaveGeneratedOutput?.Invoke(this); }
}
