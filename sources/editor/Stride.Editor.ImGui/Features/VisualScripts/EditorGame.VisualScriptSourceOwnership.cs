using Stride.Core.Assets;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void InitializeVisualScriptDocument(VisualScriptDocument document)
    {
        document.UpdateGeneratedOutput = UpdateVisualScriptGeneratedOutput;
        document.PrepareGeneratedSave = PrepareVisualScriptGeneratedSave;
        document.SaveGeneratedOutput = SaveVisualScriptGeneratedOutput;
    }

    private (AssetItem Item, EditorAuthoringState Producer, string Path, string Project) GetVisualScriptCompilerOwner(VisualScriptDocument document)
    {
        if (workspace == null || authoring == null || sessionHistory == null) throw new InvalidOperationException("The authored project is unavailable.");
        var producer = authoring.Find(document.Asset.Id);
        if (producer == null || !ReferenceEquals(producer.Document, document)) throw new InvalidOperationException("The compiler requires the current canonical visual script.");
        var item = workspace.Session.FindAsset(document.Asset.Id) ?? throw new InvalidOperationException("The visual script has no package item.");
        var project = item.Package.Container as SolutionProject ?? throw new InvalidOperationException("The visual script has no actual source project.");
        return (item, producer, item.GetGeneratedAbsolutePath().ToOSPath(), project.FullPath.ToOSPath());
    }

    private ScriptSourceDocument UpdateVisualScriptGeneratedOutputCore(VisualScriptDocument document)
    {
        var owner = GetVisualScriptCompilerOwner(document);
        if (!document.CanSave || owner.Item.Package.IsReadOnly) throw new InvalidOperationException("The generated source belongs to a read-only project.");
        var snapshot = document.CaptureCompilation(owner.Item);
        var source = snapshot.Result.GeneratedSource ?? throw new InvalidDataException("The visual-script compiler returned no source.");
        var registry = authoring!; var history = sessionHistory!;
        var state = registry.Find(owner.Path);
        if (state == null)
            state = registry.RetainedStates.FirstOrDefault(candidate => candidate.Document is ScriptSourceDocument retained
                && retained.GeneratedByAssetId == document.Asset.Id && SamePath(retained.Path, owner.Path));
        if (state == null)
        {
            var generated = File.Exists(owner.Path) ? new ScriptSourceDocument(owner.Path, readOnly: true, history: history)
                : new ScriptSourceDocument(owner.Path, document.Asset.Id, owner.Project, history);
            try
            {
                generated.BindGeneratedOutput(document.Asset.Id, owner.Project, false, owner.Producer);
                state = registry.RegisterUnpublished(generated);
            }
            catch { if (!registry.Owns(generated)) generated.Dispose(); throw; }
        }
        if (state.Document is not ScriptSourceDocument output) throw new InvalidOperationException("The generated path has another canonical document kind.");
        using var lease = state.AcquireView();
        output.BindGeneratedOutput(document.Asset.Id, owner.Project, false, owner.Producer);
        output.VerifyGeneratedOutput = VerifyVisualScriptGeneratedOutput;
        output.ValidateGeneratedFileBaseline();
        // Atomic capture supplies rollback; a real transaction also supplies the
        // authored operation boundary for publication and its generated text.
        using var transaction = history.CreateTransaction("Generate visual script");
        if (!state.IsPublished)
        {
            var operation = new VisualScriptOutputPublicationOperation(state, output.DiskContentHash.IsEmpty);
            registry.Publish(state);
            try { history.PushOperation(operation, state, owner.Producer); }
            catch { registry.Unpublish(state); throw; }
        }
        output.UpdateGeneratedText(document.Asset.Id, source, owner.Producer);
        document.GeneratedOutputInitialized = true;
        return output;
    }

    private void UpdateVisualScriptGeneratedOutput(VisualScriptDocument document)
    {
        if (sessionHistory!.IsCapturingOperations) { UpdateVisualScriptGeneratedOutputCore(document); return; }
        using var capture = sessionHistory.CaptureOperationsAtomic(() => UpdateVisualScriptGeneratedOutputCore(document));
        if (capture.Entries.Count != 0) capture.Publish(sessionHistory, "Generate visual script");
    }
    private void PrepareVisualScriptGeneratedSave(VisualScriptDocument document) => UpdateVisualScriptGeneratedOutput(document);
    private void SaveVisualScriptGeneratedOutput(VisualScriptDocument document)
    {
        var owner = GetVisualScriptCompilerOwner(document);
        if (authoring!.Find(owner.Path)?.Document is not ScriptSourceDocument generated || generated.GeneratedByAssetId != document.Asset.Id)
            throw new InvalidOperationException("The generated source is not owned by the saved visual script.");
        generated.Save();
    }
    private void VerifyVisualScriptGeneratedOutput(ScriptSourceDocument source)
    {
        if (source.GeneratedByAssetId is not { } id || authoring?.Find(id)?.Document is not VisualScriptDocument document)
            throw new InvalidOperationException("The generated source has no current visual-script producer.");
        if (document.IsDirty) throw new InvalidOperationException("Save the visual script before saving its generated source.");
        var owner = GetVisualScriptCompilerOwner(document);
        if (!SamePath(source.Path, owner.Path) || !SamePath(source.ProjectPath!, owner.Project))
            throw new InvalidOperationException("The generated source path no longer matches its actual compiler output.");
        var current = document.CaptureCompilation(owner.Item);
        if (!string.Equals(current.Result.GeneratedSource, source.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("The generated source does not match the current serialized visual script.");
        source.ValidateGeneratedFileBaseline();
    }
    private void ClearVisualScriptGeneratedOwnership()
    {
        if (authoring == null) return;
        foreach (var state in authoring.RetainedStates)
        {
            if (state.Document is ScriptSourceDocument source) source.ReleaseGeneratedOutputOwnership();
            if (state.Document is VisualScriptDocument visual)
            { visual.UpdateGeneratedOutput = null; visual.PrepareGeneratedSave = null; visual.SaveGeneratedOutput = null; }
        }
    }
    private sealed class VisualScriptOutputPublicationOperation(EditorAuthoringState state, bool ownsNewFile = false) : Operation
    {
        private byte[]? savedBytes;
        private ScriptSourceDocument Source => (ScriptSourceDocument)state.Document;
        protected override void Rollback()
        {
            byte[]? removed = null;
            if (ownsNewFile)
            {
                if (File.Exists(Source.Path))
                {
                    removed = File.ReadAllBytes(Source.Path);
                    if (Source.DiskContentHash.IsEmpty || !System.Security.Cryptography.SHA256.HashData(removed).AsSpan().SequenceEqual(Source.DiskContentHash.Span))
                        throw new IOException("The generated source changed outside its owner.");
                    WindowsOwnedAssetFileDeletion.Delete(Source.Path, Source.DiskContentHash.Span);
                }
                else if (Directory.Exists(Source.Path) || !Source.DiskContentHash.IsEmpty)
                    throw new IOException("The generated source baseline is no longer available.");
            }
            try { state.Registry.Unpublish(state); savedBytes = removed; }
            catch { if (removed != null) WindowsOwnedAssetFileWriter.Write(Source.Path, null, removed); throw; }
        }
        protected override void Rollforward()
        {
            var restored = false;
            try
            {
                if (ownsNewFile && savedBytes != null) { WindowsOwnedAssetFileWriter.Write(Source.Path, null, savedBytes); restored = true; }
                state.Registry.Publish(state);
            }
            catch
            {
                if (restored) WindowsOwnedAssetFileDeletion.Delete(Source.Path, System.Security.Cryptography.SHA256.HashData(savedBytes!));
                throw;
            }
        }
    }
}
