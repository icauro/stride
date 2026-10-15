using System.Security.Cryptography;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<string, VisualScriptOutputRemovalOperation> visualScriptOutputRemovals = new(StringComparer.OrdinalIgnoreCase);
    private void ClearVisualScriptCutState()
    {
        if (visualScriptOutputRemovals.Values.Any(operation => operation.HasPendingRetention))
            throw new InvalidOperationException("Save or restore removed generated source ownership before closing the session.");
        visualScriptOutputRemovals.Clear();
    }
    private bool CanCutVisualScript(AssetEntry entry)
    {
        var item = workspace!.FindAsset(entry.Id);
        return item.Asset is VisualScriptAsset && item.Package.Container is SolutionProject && IsEditableAssetPackage(item.Package) && item.AlternativePath == null;
    }
    private void CutVisualScriptOutputs(IEnumerable<AssetEntry> entries)
    {
        foreach (var entry in entries)
        {
            var item = workspace!.FindAsset(entry.Id);
            var project = (SolutionProject)item.Package.Container;
            var projectDocument = GetProjectBuildSettingsDocument(project, sessionHistory!);
            projectDocument.ValidateProjectFileBaseline();
            var producer = authoring!.Find(item.Id) ?? throw new InvalidOperationException("The visual script has no authored owner.");
            var path = item.GetGeneratedAbsolutePath().ToOSPath();
            var state = authoring.Find(path);
            if (state == null && File.Exists(path))
            {
                var source = (ScriptSourceDocument)GetCanonicalDocument(path, () => new ScriptSourceDocument(path, true, sessionHistory));
                source.BindGeneratedOutput(item.Id, project.FullPath.ToOSPath(), false, producer);
                state = authoring.Find(path);
            }
            projectDocument.ChangeGeneratedAssetMembership(item, false);
            if (state == null) continue;
            if (state.Document is not ScriptSourceDocument output || output.GeneratedByAssetId != item.Id)
                throw new InvalidOperationException("The generated source has another authored owner.");
            output.CommitEdits(); output.ValidateGeneratedFileBaseline();
            var operation = new VisualScriptOutputRemovalOperation(this, item.Package, state);
            operation.ApplyInitial();
            try { sessionHistory!.PushOperation(operation, state, producer); }
            catch { operation.CancelInitial(); throw; }
        }
    }
    private sealed class VisualScriptOutputRemovalOperation(EditorGame game, Package package, EditorAuthoringState state) : Operation
    {
        private ScriptSourceDocument Source => (ScriptSourceDocument)state.Document;
        private EditorDocumentEntry? view;
        private int viewIndex;
        private byte[]? removedBytes;
        private bool removedFile, frozen;
        private IDisposable? retention;
        internal bool HasPendingRetention => retention != null;
        internal void ApplyInitial() => Exclude();
        internal void CancelInitial() => Include();
        protected override void Rollback() => Include();
        protected override void Rollforward() => Exclude();
        private void Validate()
        {
            if (removedFile)
            {
                if (File.Exists(Source.Path) || Directory.Exists(Source.Path)) throw new IOException("The removed generated path is occupied.");
            }
            else if (Source.DiskContentHash.IsEmpty)
            {
                if (File.Exists(Source.Path) || Directory.Exists(Source.Path)) throw new IOException("The generated path is occupied.");
            }
            else if (!File.Exists(Source.Path) || !SHA256.HashData(File.ReadAllBytes(Source.Path)).AsSpan().SequenceEqual(Source.DiskContentHash.Span))
                throw new IOException("The generated source changed outside the editor.");
        }
        private void Exclude()
        {
            if (!state.IsPublished || game.visualScriptOutputRemovals.ContainsKey(Source.Path)) throw new InvalidOperationException("The generated source is no longer owned.");
            Validate();
            view = game.documents.Find(Source); viewIndex = view == null ? -1 : game.documents.IndexOf(view);
            if (view != null) game.documents.Detach(view);
            try { state.Registry.Unpublish(state); }
            catch { if (view != null) game.documents.Restore(view, viewIndex); throw; }
            retention ??= state.AcquireHistory();
            game.visualScriptOutputRemovals.Add(Source.Path, this);
        }
        private void Include()
        {
            if (state.IsPublished || state.Registry.Find(Source.Path) != null) throw new InvalidOperationException("The generated source path is occupied.");
            Validate();
            var restoredFile = false;
            try
            {
                if (removedFile && removedBytes != null) { WindowsOwnedAssetFileWriter.Write(Source.Path, null, removedBytes); restoredFile = true; }
                state.Registry.Publish(state);
                if (view != null) game.documents.Restore(view, viewIndex);
            }
            catch
            {
                if (state.IsPublished) state.Registry.Unpublish(state);
                if (restoredFile) WindowsOwnedAssetFileDeletion.Delete(Source.Path, SHA256.HashData(removedBytes!));
                throw;
            }
            removedFile = false; game.visualScriptOutputRemovals.Remove(Source.Path);
            retention?.Dispose(); retention = null;
        }
        internal void RemoveSavedFile(Package owner)
        {
            if (!ReferenceEquals(owner, package) || state.IsPublished) throw new InvalidOperationException("The generated source removal has another owner.");
            Validate();
            if (!removedFile && File.Exists(Source.Path))
            {
                removedBytes = File.ReadAllBytes(Source.Path);
                WindowsOwnedAssetFileDeletion.Delete(Source.Path, Source.DiskContentHash.Span); removedFile = true;
            }
            if (frozen) { game.visualScriptOutputRemovals.Remove(Source.Path); retention?.Dispose(); retention = null; }
        }
        protected override void FreezeContent()
        {
            frozen = true;
            if (state.IsPublished || removedFile || Source.DiskContentHash.IsEmpty)
            { game.visualScriptOutputRemovals.Remove(Source.Path); retention?.Dispose(); retention = null; }
            if (view != null && !state.IsPublished) { game.ReleaseScriptEditor(Source); view = null; }
        }
    }
}
