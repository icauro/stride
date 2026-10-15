using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetRepairPanel? ownedAssetRepair;
    private bool? pendingAssetRepair;
    private void RequestAssetRepair(bool sources) => pendingAssetRepair = sources;
    private void DrawAssetRepair()
    {
        if (workspace == null || RecoveryTransitionBusy || projectLoading != null || IsAuthoringPipelineUpdating || IsAssetPreviewUpdating || IsUserProjectBuilding) return;
        if (pendingAssetRepair is { } sources)
        { pendingAssetRepair = null; (ownedAssetRepair ??= new(new AssetRepairHost(this))).Open(sources); }
        ownedAssetRepair?.Draw();
    }
    private void ClearAssetRepair()
    { ownedAssetRepair?.Dispose(); ownedAssetRepair = null; pendingAssetRepair = null; }
    private sealed class AssetRepairHost(EditorGame editor) : IAssetRepairHost
    {
        public IReadOnlyList<AssetRepairContext> AcquireContexts()
        {
            var result = new List<AssetRepairContext>();
            try
            {
                foreach (var entry in editor.workspace!.Assets)
                {
                    if (typeof(SourceCodeAsset).IsAssignableFrom(entry.AssetType) || editor.workspace.FindAsset(entry.Id).Package.IsReadOnly) continue;
                    var document = editor.GetAssetTagDocument(entry, false);
                    var context = document switch
                    {
                        AssetMetadataDocument metadata when metadata.CanSave => metadata.CreateRepairContext(),
                        EntityHierarchyDocument hierarchy when hierarchy.CanSave => hierarchy.CreateRepairContext(),
                        ProceduralAssetDocument procedural when procedural.CanSave => procedural.CreateRepairContext(),
                        _ => null,
                    };
                    if (context != null) result.Add(context);
                }
                return result;
            }
            catch { foreach (var context in result) context.Dispose(); throw; }
        }
        public bool AssetExists(AssetId id) => editor.workspace!.AssetItems.Any(item => item.Id == id);
        public object? BrowseSource(AssetRepairField field)
        {
            var origin = System.IO.Path.GetDirectoryName(field.Context.Owner.Document.Path)!;
            var initial = field.Before is UFile value && !string.IsNullOrEmpty(value.ToString()) ? System.IO.Path.GetFullPath(value.ToOSPath(), origin) : null;
            var chosen = WindowsFileDialogs.Open(editor.Window.NativeWindow.Handle, null!, "All files\0*.*\0\0", initial == null ? origin : System.IO.Path.GetDirectoryName(initial), initial);
            return chosen == null ? null : new UFile(System.IO.Path.GetRelativePath(origin, chosen));
        }
        public void DrawReference(AssetRepairField field, object? value, Action<object?> commit)
            => editor.DrawContentReferenceValue(field.Type, value, true, commit);
        public void DrawSelectableText(string id, string value) => editor.DrawSelectableText(id, value);
        public void Apply(IReadOnlyList<(AssetRepairField Field, object? Value)> changes)
        {
            if (editor.RecoveryTransitionBusy || editor.IsAuthoringPipelineUpdating || editor.IsAssetPreviewUpdating || editor.IsUserProjectBuilding || editor.projectLoading != null)
                throw new InvalidOperationException("Asset authoring is updating.");
            foreach (var change in changes)
                if (change.Field.Source && (change.Value is not UFile source || !File.Exists(System.IO.Path.GetFullPath(source.ToOSPath(), System.IO.Path.GetDirectoryName(change.Field.Context.Owner.Document.Path)!))))
                    throw new FileNotFoundException("The replacement source file no longer exists.");
            foreach (var group in changes.GroupBy(change => change.Field.Context)) group.Key.Validate(group.ToArray());
            using var captured = editor.sessionHistory!.CaptureOperationsAtomic(() =>
            {
                foreach (var group in changes.GroupBy(change => change.Field.Context)) group.Key.Repair(group.ToArray());
            });
            captured.Publish(editor.sessionHistory, "Repair asset references");
        }
        public void ReportFailure(Exception error) { editor.statusMessage = error.Message; editor.RecordAssetPreviewRenderFailure("asset-repair", error); }
    }
}
