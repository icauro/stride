using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record RecoveryMissingGeneratedProducerReceipt(string ProducerPayloadHash, string DirtyCompilerText,
    string DirtyCompilerTextSha256, string SavedCompilerText, string ProducerDiskSha256, bool ActualNativeProducerEdit);
internal sealed record RecoveryMissingGeneratedState(Guid ProducerGeneration, Guid GeneratedGeneration,
    bool ProducerDirty, bool GeneratedDirty, int PropertyCount, string CompiledSource, EditorHistorySnapshot History,
    bool GeneratedDiskAbsent, bool ActualNativeProducerLease, bool GeneratedCanSave, bool GeneratedCanEditText);

internal sealed partial class EditorGame
{
    internal Guid RecoveryMissingGeneratedProducerEntry(RecoveryGeneratedReadOnlySourcesReceipt expected)
    {
        RequireRecoveryGeneratedReadOnlyHost();
        return documents.Find(expected.ProducerPath)?.Id ?? throw new InvalidOperationException("Actual saved visual-script producer tab is absent.");
    }
    internal RecoveryMissingGeneratedState InspectRecoveryMissingGeneratedState(RecoveryGeneratedReadOnlySourcesReceipt expected, bool requireMissing)
    {
        RequireRecoveryGeneratedReadOnlyHost();
        if (!args.Contains("--recovery-missing-generated-dirty-producer"))
            throw new InvalidOperationException("Missing generated verification requires its exact private variant.");
        var current = workspace ?? throw new InvalidOperationException("The native generated project is absent.");
        var assetId = new AssetId(Guid.Parse(expected.ProducerAssetId));
        var item = current.Session.FindAsset(assetId) ?? throw new InvalidOperationException("The saved native visual-script producer asset is absent.");
        var producer = authoring?.Find(assetId) ?? throw new InvalidOperationException("The current canonical visual-script producer state is absent.");
        var output = authoring?.Find(expected.GeneratedPath) ?? throw new InvalidOperationException("The canonical generated projection is absent.");
        if (item.Asset is not VisualScriptAsset || producer.Document is not VisualScriptDocument visual
            || output.Document is not ScriptSourceDocument source || source.GeneratedByAssetId != assetId
            || !producer.IsPublished || producer.IsDisposed || !output.IsPublished || output.IsDisposed
            || !source.IsGeneratedOutputOwnedBy(producer) || source.CanEditText || source.ProjectPath == null
            || !SamePath(item.FullPath.ToOSPath(), expected.ProducerPath)
            || !SamePath(item.GetGeneratedAbsolutePath().ToOSPath(), expected.GeneratedPath)
            || item.Package?.Container is not SolutionProject project || !current.Session.Projects.Contains(project)
            || !SamePath(source.ProjectPath, project.FullPath.ToOSPath())
            || !ReferenceEquals(source.SessionHistory, sessionHistory) || !ReferenceEquals(visual.SessionHistory, sessionHistory)
            || documents.Entries.Count(entry => SamePath(entry.Path, expected.GeneratedPath)) != 1)
            throw new InvalidOperationException("Missing generated view has incorrect current canonical producer/project/source identity or duplicate/editable ownership.");
        var absent = !File.Exists(expected.GeneratedPath) && !Directory.Exists(expected.GeneratedPath);
        if (requireMissing && !absent) throw new InvalidOperationException("Workspace/payload/Undo/Redo must not materialize the missing compiler artifact on disk.");
        if (RecoveryProtectedHash(expected.ProducerPath) != expected.ProducerSha256)
            throw new InvalidOperationException("Projection recovery must not save or overwrite the actual saved visual-script producer file.");
        var compiled = visual.CaptureCompilation(item).Result.GeneratedSource
            ?? throw new InvalidOperationException("The real current native visual-script compiler returned no projection.");
        if (source.Text != compiled)
            throw new InvalidOperationException("Derived generated buffer differs from the actual compiler output of its current canonical producer.");
        VerifyRecoveryExternalReadOnlySource(current, expected);
        var readOnly = documents.Find(expected.ReadOnlyPath)?.Document as ScriptSourceDocument;
        if (readOnly == null || readOnly.CanEditText || readOnly.CanSave || readOnly.IsDirty || readOnly.Text != expected.ReadOnlyText
            || RecoveryProtectedHash(expected.ReadOnlyPath) != expected.ReadOnlySha256
            || (File.GetAttributes(expected.ReadOnlyPath) & FileAttributes.ReadOnly) == 0)
            throw new InvalidOperationException("Missing generated recovery changed the independent actual read-only source.");
        return new(producer.Generation, output.Generation, visual.IsDirty, source.IsDirty, visual.Asset.Properties.Count,
            compiled, visual.History, absent, true, source.CanSave, source.CanEditText);
    }
    internal object CaptureRecoveryMissingGeneratedPreRestoreOwner()
    {
        RequireRecoveryGeneratedReadOnlyHost();
        var current = workspace ?? throw new InvalidOperationException("The actual pre-restore workspace owner is absent.");
        return new { workspacePath = current.OpenPath, nativeSessionProjectCount = current.Session.Projects.Count,
            actualCanonicalDocumentIds = documents.Entries.Select(entry => new { entry.Id, entry.Path, generation = entry.State?.Generation }).ToArray(),
            history = sessionHistory!.History };
    }
}
