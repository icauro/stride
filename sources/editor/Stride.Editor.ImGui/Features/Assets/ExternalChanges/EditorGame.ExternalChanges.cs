using System.Security.Cryptography;
using Stride.Core.Assets;
using Stride.Core.Assets.Serializers;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetExternalChangeMonitor? externalAssetMonitor;
    private AssetExternalChangesPanel? externalChangesPanel;
    private IReadOnlyList<AssetEntry>? observedExternalEntries;
    private void TickExternalAssetChanges()
    {
        if (workspace == null || RecoveryTransitionBusy || projectLoading != null || IsAuthoringPipelineUpdating || IsAssetPreviewUpdating || IsUserProjectBuilding
            || sessionHistory?.IsCapturingOperations == true || sessionHistory?.IsCompensating == true
            || sessionHistory?.Stack.TransactionInProgress == true || sessionHistory?.Stack.RollInProgress == true) return;
        externalAssetMonitor ??= new(ExternalDocumentBaseline);
        if (!ReferenceEquals(observedExternalEntries, workspace.Assets)) { externalAssetMonitor.Configure(workspace.Assets); observedExternalEntries = workspace.Assets; }
        externalAssetMonitor.Tick();
    }
    private byte[]? ExternalDocumentBaseline(string path) => authoring?.Find(path)?.Document switch
    {
        IRelocatableEditorDocument owner => owner.DiskContentHash.ToArray(),
        _ => null,
    };
    private void DrawExternalAssetChanges()
    {
        if (externalAssetMonitor == null || RecoveryTransitionBusy || projectLoading != null || IsAuthoringPipelineUpdating || IsAssetPreviewUpdating || IsUserProjectBuilding) return;
        externalChangesPanel ??= new(new ExternalChangesHost(this));
        externalChangesPanel.WorkflowControlRecorder = Controls.Enabled ? Controls.Record : null;
        externalChangesPanel.Draw(externalAssetMonitor);
    }
    private void ClearExternalAssetChanges()
    { externalAssetMonitor?.Dispose(); externalAssetMonitor = null; externalChangesPanel = null; observedExternalEntries = null; }
    private sealed class ExternalChangesHost(EditorGame editor) : IAssetExternalChangesHost
    {
        public bool IsDirty(string path) => editor.authoring?.Find(path)?.Document.IsDirty == true;
        public void Reload(ExternalAssetChange change)
        {
            if (change.Missing || editor.workspace == null || !editor.IsCurrentEditableAssetPackage(editor.workspace.FindAsset(change.Asset.Id).Package) || editor.RecoveryTransitionBusy || editor.IsAuthoringPipelineUpdating || editor.IsAssetPreviewUpdating || editor.IsUserProjectBuilding)
                throw new InvalidOperationException("The external asset cannot be reloaded while authoring is updating.");
            var item = editor.workspace.FindAsset(change.Asset.Id);
            if (!SamePath(item.FullPath.ToOSPath(), change.Asset.Path)) throw new InvalidOperationException("The changed file no longer owns this asset.");
            var bytes = File.ReadAllBytes(change.Asset.Path); var hash = SHA256.HashData(bytes);
            if (!hash.AsSpan().SequenceEqual(change.ContentHash)) throw new IOException("The asset changed again before reload. Refresh the external changes list.");
            if (item.Asset is not SourceCodeAsset)
            {
                using var stream = new MemoryStream(bytes, false); var log = new LoggerResult();
                var loaded = AssetFileSerializer.Load<Asset>(stream, new UFile(change.Asset.Path), log, item.Package.Container?.AssetNamespace);
                if (log.HasErrors || !ExternalAssetTypeIdentity.Matches(item.Asset, loaded.Asset))
                    throw new InvalidDataException(log.HasErrors ? log.ToText() : "The external file changed the actual asset identity or type.");
            }
            var entry = editor.documents.Find(change.Asset.Path);
            if (entry == null)
            {
                IEditorDocument document = item.Asset is SourceCodeAsset
                    ? editor.GetCanonicalDocument(change.Asset.Path, () => editor.CreateScriptDocument(change.Asset.Path))
                    : (IEditorDocument)editor.GetAssetTagDocument(change.Asset, false);
                entry = editor.documents.Find(document) ?? editor.documents.Add(document);
            }
            var current = entry.Document;
            // A dirty owner is retained in the actual undoable generation swap; disk content is never overwritten here.
            editor.ReplaceAuthoredDocument(entry, () => ExternalReloadDocuments.Create(current, bytes, hash, item.Package.Container?.AssetNamespace), "Reload external " + entry.Name);
            editor.InvalidateCompiledTexture(change.Asset.Path);
            if (ReferenceEquals(editor.documents.Active, entry)) editor.ActivateDocument(entry);
        }
        public void DrawSelectableText(string id, string value) => editor.DrawSelectableText(id, value);
        public void ReportFailure(Exception error) { editor.statusMessage = error.Message; editor.RecordAssetPreviewRenderFailure("external-asset", error); }
    }
}
