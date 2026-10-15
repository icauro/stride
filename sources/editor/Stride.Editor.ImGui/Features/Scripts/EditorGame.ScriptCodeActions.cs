using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ApplyScriptCodeAction(ScriptCodeActionBatch batch)
    {
        var history = SessionAuthoringHistory ?? throw new InvalidOperationException("The authored session is unavailable.");
        var registry = authoring ?? throw new InvalidOperationException("The authored session is unavailable.");
        if (scriptSemantics?.Epoch != batch.Epoch
            || documents.AuthoredDocuments.OfType<ScriptSourceDocument>().Count(document => document.IsCSharpSource) != batch.Context.Length
            || batch.Context.Any(snapshot => FindAuthoredScript(snapshot.Path) is not { } document || document.Version != snapshot.Version || document.EditorText != snapshot.Text))
            throw new InvalidOperationException("The code-action solution snapshot changed.");
        foreach (var baseline in batch.ProjectFiles ?? [])
            if (!System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(baseline.Path)).AsSpan().SequenceEqual(baseline.Hash))
                throw new InvalidOperationException("The loaded project changed outside the source snapshot.");
        foreach (var project in batch.ProjectStates ?? [])
            if (registry.Find(project.ProjectPath)?.Document is not ProjectBuildSettingsDocument owner || owner.AuthoringRevision != project.Revision)
                throw new InvalidOperationException("The authored project changed after the source action was prepared.");
        var prepared = new List<PreparedScriptSource>();
        var leases = new List<IDisposable>();
        var projectOwners = new Dictionary<string, ProjectBuildSettingsDocument>(StringComparer.OrdinalIgnoreCase);
        var moves = new Dictionary<string, PreparedScriptSourceMove>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ScriptSourceDocument? referenceOrigin = null;
            if (batch.NativeContexts is { Length: > 0 })
            {
                var contexts = documents.AuthoredDocuments.OfType<ScriptSourceDocument>().SelectMany(source => source.CaptureReferenceContexts())
                    .OrderBy(context => context.Order).ToArray();
                if (!contexts.SequenceEqual(batch.ReferenceContextBaseline ?? [])
                    || batch.NativeContexts.Any(context => scriptSemantics?.IsCurrentReferenceContext(context) != true))
                    throw new InvalidOperationException("The native reference context changed after preview.");
                referenceOrigin = batch.OriginPath == null ? null : FindAuthoredScript(batch.OriginPath);
                if (referenceOrigin == null || !referenceOrigin.CanEditText || !ReferenceEquals(referenceOrigin.SessionHistory, history))
                    throw new InvalidOperationException("The native reference action has no current authored source owner.");
                var state = registry.Find(referenceOrigin.Path) ?? throw new InvalidOperationException("The native reference origin is unavailable.");
                leases.Add(state.AcquireHistory());
            }
            foreach (var edit in batch.ProjectEdits ?? [])
            {
                var project = workspace!.Projects.SingleOrDefault(project => SamePath(project.FullPath.ToOSPath(), edit.Path))
                    ?? throw new InvalidOperationException("The compiler reference has no actual loaded project owner.");
                var owner = GetProjectBuildSettingsDocument(project, history);
                owner.ValidateProjectFileBaseline();
                if (owner.CaptureProjectSnapshot().Xml != edit.Before) throw new InvalidOperationException("The compiler reference project changed after preview.");
                var state = registry.Find(edit.Path) ?? throw new InvalidOperationException("The compiler project has no canonical authoring owner.");
                leases.Add(state.AcquireHistory()); projectOwners.Add(edit.Path, owner);
            }
            foreach (var edit in batch.ReferenceEdits ?? [])
            {
                using var file = new FileStream(edit.Change.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!System.Security.Cryptography.SHA256.HashData(file).AsSpan().SequenceEqual(edit.Hash))
                    throw new InvalidOperationException("The compiler reference file changed after preview.");
            }
            foreach (var edit in batch.Edits)
            {
                if (edit.DestinationPath != null) moves.Add(edit.Path, PrepareScriptCodeActionMove(edit));
                if (edit.Kind != ScriptSourceChangeKind.Modified)
                {
                    var topology = PrepareScriptSourceTopology(batch, edit);
                    foreach (var participant in topology.Topology!.Participants) leases.Add(participant.AcquireHistory());
                    prepared.Add(topology); continue;
                }
                var document = GetCanonicalDocument(edit.Path, () => CreateScriptDocument(edit.Path)) as ScriptSourceDocument
                    ?? throw new InvalidOperationException("The code-action source has another authored document owner.");
                var state = registry.Find(edit.Path) ?? throw new InvalidOperationException("The code-action source is unavailable.");
                if (!ReferenceEquals(document.SessionHistory, history) || !ReferenceEquals(state.Document, document) || !state.IsPublished)
                    throw new InvalidOperationException("The code-action document is no longer the current authored source.");
                leases.Add(state.AcquireHistory());
                document.ValidateCodeActionBaseline(edit);
                prepared.Add(new(document, edit, state));
            }
            // Recheck the entire retained batch before applying any authored operation.
            foreach (var item in prepared)
            {
                item.Project?.ValidateProjectFileBaseline();
                if (item.Edit.Kind == ScriptSourceChangeKind.Added)
                {
                    if (registry.Find(item.Edit.Path) != null || File.Exists(item.Edit.Path) || Directory.Exists(item.Edit.Path)) throw new InvalidOperationException("The new source destination is occupied.");
                }
                else
                {
                    if (!ReferenceEquals(registry.Find(item.Edit.Path), item.State)) throw new InvalidOperationException("The code-action source owner changed.");
                    item.Document.ValidateCodeActionBaseline(item.Edit);
                }
            }
            using var capture = history.CaptureOperationsAtomic(() =>
            {
                foreach (var context in batch.NativeContexts ?? []) referenceOrigin!.ApplyReferenceContext(context);
                foreach (var edit in batch.ReferenceEdits ?? [])
                    projectOwners[edit.ProjectPath].ChangeCompilerReference(edit.Change, edit.Configuration, edit.Framework);
                foreach (var edit in batch.ProjectEdits ?? [])
                    if (projectOwners[edit.Path].CaptureProjectSnapshot().Xml != edit.After)
                        throw new InvalidOperationException("The native project reference result differs from its preview.");
                foreach (var item in prepared)
                {
                    if (item.Topology == null)
                    {
                        item.Document.ApplyCodeActionChanges(item.Edit);
                        if (moves.TryGetValue(item.Edit.Path, out var move)) RelocateSourceAsset(move.Entry, move.Url);
                        continue;
                    }
                    if (item.Directories is { } directories)
                    {
                        try { directories.Ensure(Path.GetDirectoryName(item.Edit.Path)!); }
                        catch (Exception failure) { RestoreDirectories(failure); throw; }
                        if (directories.HasDirectories)
                        {
                            try { history.PushOperation(directories, item.State); }
                            catch (Exception failure) { RestoreDirectories(failure); throw; }
                        }
                        void RestoreDirectories(Exception failure)
                        {
                            try { directories.CancelInitial(); }
                            catch (Exception recovery)
                            {
                                history.RetainFailedInitialOperation(directories, item.State);
                                throw new AggregateException("The source directory preparation could not be restored.", failure, recovery);
                            }
                        }
                    }
                    item.Project!.ChangeProjectItem("Compile", item.Item!.GetProjectInclude(), item.Edit.Kind == ScriptSourceChangeKind.Added);
                    using var transaction = history.CreateTransaction(batch.Title);
                    item.Topology.ApplyInitial();
                    try { history.PushOperation(item.Topology, item.Topology.Participants); }
                    catch (Exception failure)
                    {
                        try { item.Topology.CancelInitial(); }
                        catch (Exception recovery)
                        {
                            history.RetainFailedInitialOperation(item.Topology, item.Topology.Participants);
                            throw new AggregateException("The source operation could not be recorded or restored.", failure, recovery);
                        }
                        throw;
                    }
                }
            });
            capture.Publish(history, batch.Title);
            foreach (var path in batch.OpenPaths ?? []) pendingScriptSourceOpenPaths.Enqueue(path);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
    private ScriptSourceDocument? FindAuthoredScript(string path) => (authoring?.Find(path)?.Document ?? documents.Find(path)?.Document) as ScriptSourceDocument;
}
