using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;
using Stride.Core.Assets;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Document = Microsoft.CodeAnalysis.Document;

namespace Stride.Editor.ImGui;

internal sealed record ScriptCodeActionItem(string Title, Document Document, ScriptSourceSnapshot Source, CodeAction Action, long Epoch, ScriptSourceSnapshot[] Context, TextSpan Selection, ScriptFixAllRequest? FixAll = null);
internal enum ScriptSourceChangeKind { Modified, Added, Removed }
internal sealed record ScriptCodeActionSourceEdit(string Path, string Before, TextChange[] Changes, long? Version, byte[]? DiskHash,
    ScriptSourceChangeKind Kind = ScriptSourceChangeKind.Modified, string? ProjectPath = null, string? Name = null, string[]? Folders = null,
    string? DestinationPath = null);
internal sealed record ScriptCodeActionReferenceEdit(string ProjectPath, string Configuration, string Framework,
    ProjectCompilerReferenceChange Change, byte[] Hash);
internal sealed record ScriptCodeActionProjectEdit(string Path, string Before, string After);
internal sealed record ScriptCodeActionBatch(string Title, ScriptCodeActionSourceEdit[] Edits, ScriptSourceSnapshot[] Context, long Epoch, ScriptDiagnosticReport[]? Failures = null, string[]? OpenPaths = null, string? OriginPath = null,
    ScriptProjectFileBaseline[]? ProjectFiles = null, ScriptProjectMembershipSnapshot[]? ProjectStates = null,
    ScriptCodeActionReferenceEdit[]? ReferenceEdits = null, ScriptCodeActionProjectEdit[]? ProjectEdits = null,
    ScriptNativeReferenceContext[]? NativeContexts = null, ScriptNativeReferenceContext[]? ReferenceContextBaseline = null) : IDisposable
{
    public void Dispose() { foreach (var context in NativeContexts ?? []) context.Dispose(); }
}

internal sealed partial class ScriptSemanticWorkspace
{
    private ScriptCodeActionProviders? codeActionProviders;
    private async Task<(ScriptCodeActionItem[] Actions, ScriptDiagnosticReport[] Failures)> CodeActions(Document document, ScriptSourceSnapshot source, TextSpan selection, long expectedEpoch, ScriptSourceSnapshot[] snapshots, CancellationToken cancellation)
    {
        var host = codeActionProviders ?? throw new InvalidOperationException("The code-action providers are unavailable.");
        using var analyzers = CreateAnalyzerRun(document.Project, cancellation);
        var model = await document.GetSemanticModelAsync(cancellation).ConfigureAwait(false) ?? throw new InvalidOperationException("The source semantic model is unavailable.");
        var diagnostics = model.GetDiagnostics(cancellationToken: cancellation).Concat(await analyzers.Run(model.Compilation, cancellation).ConfigureAwait(false))
            .Where(diagnostic => !diagnostic.IsSuppressed && diagnostic.Location.SourceTree == model.SyntaxTree
                && diagnostic.Location.SourceSpan.Start <= selection.End && diagnostic.Location.SourceSpan.End >= selection.Start).ToArray();
        var actions = new ConcurrentQueue<(CodeAction Action, CodeFixProvider? Provider, string[] Ids)>(); var failures = new List<ScriptDiagnosticReport>();
        void Failed(Exception error) => failures.Add(new("", DiagnosticSeverity.Warning, error.Message, null, 0, 0));
        foreach (var provider in host.Fixes(document.Project.Language, source.Path, Failed))
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var diagnostic in diagnostics.Where(diagnostic => provider.FixableDiagnosticIds.Contains(diagnostic.Id)))
                try
                {
                    await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, registered) => actions.Enqueue((action, provider, registered.Select(value => value.Id).Distinct().ToArray())), cancellation)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; } catch (Exception error) { Failed(error); }
        }
        foreach (var provider in host.Refactorings(document.Project.Language, Failed))
            try { await provider.ComputeRefactoringsAsync(new CodeRefactoringContext(document, selection, action => actions.Enqueue((action, null, [])), cancellation)).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; } catch (Exception error) { Failed(error); }
        var flattened = new List<ScriptCodeActionItem>();
        var fixAllKeys = new HashSet<(Type Provider, string Key, FixAllScope Scope, string Ids)>();
        void Add(CodeAction action, string prefix, CodeFixProvider? provider, string[] ids)
        {
            var title = prefix.Length == 0 ? action.Title : prefix + " > " + action.Title;
            if (action.NestedActions.IsDefaultOrEmpty)
            {
                flattened.Add(new(title, document, source, action, expectedEpoch, snapshots, selection));
                if (provider != null && action.EquivalenceKey is { } key)
                    try
                    {
                        var fixAll = provider.GetFixAllProvider();
                        if (fixAll != null)
                        {
                            var supportedIds = fixAll.GetSupportedFixAllDiagnosticIds(provider).Intersect(ids).ToArray();
                            if (supportedIds.Length > 0)
                                foreach (var scope in fixAll.GetSupportedFixAllScopes())
                                {
                                    var scopeName = scope switch { FixAllScope.Document => "Document", FixAllScope.Project => "Project", FixAllScope.Solution => "Solution",
                                        FixAllScope.ContainingMember => "Containing member", FixAllScope.ContainingType => "Containing type", _ => null };
                                    if (scopeName != null && fixAllKeys.Add((provider.GetType(), key, scope, string.Join(";", supportedIds.OrderBy(id => id, StringComparer.Ordinal))))) flattened.Add(new(title + " > Fix all in " + scopeName, document, source, action, expectedEpoch, snapshots, selection,
                                        new(provider, fixAll, scope, key, supportedIds)));
                                }
                        }
                    }
                    catch (OperationCanceledException) { throw; } catch (Exception error) { Failed(error); }
            }
            else foreach (var nested in action.NestedActions) Add(nested, title, provider, ids);
        }
        foreach (var entry in actions.OrderByDescending(entry => entry.Action.Priority)) Add(entry.Action, "", entry.Provider, entry.Ids);
        return (flattened.ToArray(), failures.Concat(analyzers.Failures).Distinct().ToArray());
    }

    private async Task<ScriptCodeActionBatch> CodeActionChanges(ScriptCodeActionItem item, ScriptSourceSnapshot[] snapshots, CancellationToken cancellation)
    {
        if (item.Context.Length != snapshots.Length || item.Context.Any(previous => !snapshots.Any(current =>
            string.Equals(previous.Path, current.Path, StringComparison.OrdinalIgnoreCase) && previous.Version == current.Version && previous.Text == current.Text)))
            throw new OperationCanceledException("The code-action solution snapshot changed.", cancellation);
        var action = item.Action;
        ScriptFixAllDiagnostics? fixAllDiagnostics = null;
        if (item.FixAll is { } fixAll)
        {
            fixAllDiagnostics = new(fixAll.DiagnosticIds, cancellation, (project, token) => CreateAnalyzerRun(project, token));
            var context = new FixAllContext(item.Document, item.Selection, fixAll.Provider, fixAll.Scope, fixAll.EquivalenceKey,
                fixAll.DiagnosticIds, fixAllDiagnostics, cancellation);
            action = await fixAll.FixAll.GetFixAsync(context).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The provider has no fix-all action for this source snapshot.");
        }
        var operations = await action.GetOperationsAsync(cancellation).ConfigureAwait(false);
        var applyOperations = operations.OfType<ApplyChangesOperation>().ToArray();
        if (applyOperations.Length != 1 || operations.Any(operation => operation is not ApplyChangesOperation and not OpenDocumentOperation))
            throw new InvalidOperationException("This code action requires operations that are not supported by the source editor.");
        var apply = applyOperations[0];
        var solutionChanges = apply.ChangedSolution.GetChanges(item.Document.Project.Solution);
        var addedIds = solutionChanges.GetProjectChanges().SelectMany(project => project.GetAddedDocuments()).ToHashSet();
        if (!Equals(apply.ChangedSolution.Options, item.Document.Project.Solution.Options))
            throw new InvalidOperationException("This code action changes solution options.");
        var changes = solutionChanges;
        if (changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any()) throw new InvalidOperationException("This code action changes solution projects.");
        var projects = changes.GetProjectChanges().ToArray();
        var edits = new Dictionary<string, ScriptCodeActionSourceEdit>(StringComparer.OrdinalIgnoreCase);
        var referenceEdits = new List<ScriptCodeActionReferenceEdit>();
        var referenceProjects = new List<(Project Before, Project After)>();
        var physicalMoves = new HashSet<DocumentId>();
        foreach (var project in projects)
        {
            if (project.GetAddedAdditionalDocuments().Any() || project.GetRemovedAdditionalDocuments().Any()
                || project.GetChangedAdditionalDocuments().Any() || project.GetAddedAnalyzerConfigDocuments().Any() || project.GetRemovedAnalyzerConfigDocuments().Any()
                || project.GetChangedAnalyzerConfigDocuments().Any() || project.GetAddedProjectReferences().Any() || project.GetRemovedProjectReferences().Any())
                throw new InvalidOperationException("This code action changes project inputs.");
            var before = project.OldProject; var after = project.NewProject;
            if (project.GetRemovedMetadataReferences().Any() || project.GetAddedMetadataReferences().Any()
                || project.GetRemovedAnalyzerReferences().Any() || project.GetAddedAnalyzerReferences().Any())
                referenceProjects.Add((before, after));
            if (!Equals(before.ParseOptions, after.ParseOptions) || !Equals(before.CompilationOptions, after.CompilationOptions)
                || before.Name != after.Name || before.AssemblyName != after.AssemblyName || before.FilePath != after.FilePath || before.OutputFilePath != after.OutputFilePath
                || before.OutputRefFilePath != after.OutputRefFilePath || before.DefaultNamespace != after.DefaultNamespace || !Equals(before.CompilationOutputInfo, after.CompilationOutputInfo))
                throw new InvalidOperationException("This code action changes project compilation options.");
            foreach (var id in project.GetChangedDocuments())
            {
                var original = before.GetDocument(id)!;
                var document = after.GetDocument(id)!;
                var authoredPath = AuthoredSourcePath(original);
                if (authoredPath == null || !Path.GetExtension(authoredPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This code action changes a source without an editable C# file.");
                var path = Path.GetFullPath(authoredPath);
                string? destination = null;
                if (!ScriptNativeDocumentInfo.Same(original, document))
                {
                    destination = snapshots.Any(source => source.CanRelocate && string.Equals(source.Path, path, StringComparison.OrdinalIgnoreCase))
                        ? ResolvePhysicalDocumentInfoMove(original, document, item.Document) : null;
                    if (destination != null && !string.Equals(destination, path, StringComparison.OrdinalIgnoreCase)
                        && (File.Exists(destination) || Directory.Exists(destination))) throw new InvalidOperationException("The source move destination is occupied.");
                    if (destination != null) physicalMoves.Add(id);
                    else if (!referenceProjects.Any(project => project.Before.Id == before.Id)) referenceProjects.Add((before, after));
                }
                var text = (await original.GetTextAsync(cancellation).ConfigureAwait(false)).ToString();
                var textChanges = (await document.GetTextChangesAsync(original, cancellation).ConfigureAwait(false)).ToArray();
                if (textChanges.Length == 0 && destination == null) continue;
                var snapshot = snapshots.FirstOrDefault(value => string.Equals(value.Path, path, StringComparison.OrdinalIgnoreCase));
                if (snapshot != null && snapshot.Text != text) throw new OperationCanceledException("The code-action source snapshot changed.", cancellation);
                var hash = File.Exists(path) ? System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false)) : null;
                if (hash == null && snapshot == null) throw new IOException("The source file is no longer available.");
                var edit = new ScriptCodeActionSourceEdit(path, text, textChanges, snapshot?.Version, hash, ProjectPath: before.FilePath,
                    Name: document.Name, Folders: document.Folders.ToArray(), DestinationPath: destination);
                if (edits.TryGetValue(path, out var linked))
                {
                    if (linked.Before != text || linked.DestinationPath != destination || SourceText.From(linked.Before).WithChanges(linked.Changes).ToString() != SourceText.From(text).WithChanges(textChanges).ToString())
                        throw new InvalidOperationException("This code action produces conflicting edits for a linked source file.");
                }
                else edits.Add(path, edit);
            }
            foreach (var id in project.GetAddedDocuments())
            {
                var added = after.GetDocument(id)!;
                if (before.FilePath == null || added.SourceCodeKind != SourceCodeKind.Regular)
                    throw new InvalidOperationException("The new source has no physical project destination.");
                var path = ResolveAddedSourcePath(added, item.Document);
                if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) || File.Exists(path) || Directory.Exists(path))
                    throw new InvalidOperationException("The new C# source destination is occupied.");
                var text = (await added.GetTextAsync(cancellation).ConfigureAwait(false)).ToString();
                if (!edits.TryAdd(path, new(path, "", [new TextChange(new TextSpan(0, 0), text)], null, null,
                    ScriptSourceChangeKind.Added, before.FilePath, added.Name, added.Folders.ToArray())))
                    throw new InvalidOperationException("The code action changes a source destination more than once.");
            }
            foreach (var id in project.GetRemovedDocuments())
            {
                var removed = before.GetDocument(id)!;
                if (AuthoredSourcePath(removed) is not { } removedPath || before.FilePath == null || removed.SourceCodeKind != SourceCodeKind.Regular)
                    throw new InvalidOperationException("The removed source has no physical project owner.");
                var path = Path.GetFullPath(removedPath);
                var text = (await removed.GetTextAsync(cancellation).ConfigureAwait(false)).ToString();
                var snapshot = snapshots.FirstOrDefault(value => string.Equals(value.Path, path, StringComparison.OrdinalIgnoreCase));
                var hash = File.Exists(path) ? System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false)) : null;
                if (!edits.TryAdd(path, new(path, text, [new TextChange(new TextSpan(0, text.Length), "")], snapshot?.Version, hash,
                    ScriptSourceChangeKind.Removed, before.FilePath, removed.Name, removed.Folders.ToArray())))
                    throw new InvalidOperationException("The code action changes a source destination more than once.");
            }
        }
        foreach (var edit in edits.Values)
        {
            if (edit.DestinationPath is { } moved && edits.Values.Any(other => !ReferenceEquals(other, edit)
                && (string.Equals(other.Path, moved, StringComparison.OrdinalIgnoreCase) || string.Equals(other.DestinationPath, moved, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("The code action produces overlapping source move destinations.");
            if (edit.DestinationPath != null)
                foreach (var linked in apply.ChangedSolution.Projects.SelectMany(project => project.Documents)
                    .Where(document => document.FilePath != null && string.Equals(Path.GetFullPath(document.FilePath), edit.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    var previous = item.Document.Project.Solution.GetDocument(linked.Id);
                    if (previous == null || linked.Name == previous.Name && linked.Folders.SequenceEqual(previous.Folders)
                        || !string.Equals(ResolveAddedSourcePath(linked, item.Document, ignorePhysicalPath: true), edit.DestinationPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The source move leaves a linked project at its previous physical path.");
                }
            if (edit.Kind == ScriptSourceChangeKind.Removed)
            {
                if (apply.ChangedSolution.Projects.SelectMany(project => project.Documents).Any(document => document.FilePath != null && string.Equals(Path.GetFullPath(document.FilePath), edit.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The removed source is still included by a linked project.");
                continue;
            }
            var expected = SourceText.From(edit.Before).WithChanges(edit.Changes).ToString();
            var linkedDocuments = apply.ChangedSolution.Projects.SelectMany(project => project.Documents)
                .Where(document => AuthoredSourcePath(document) is { } source && string.Equals(Path.GetFullPath(source), edit.Path, StringComparison.OrdinalIgnoreCase)
                    || document.FilePath == null && addedIds.Contains(document.Id)
                        && string.Equals(ResolveAddedSourcePath(document, item.Document), edit.Path, StringComparison.OrdinalIgnoreCase));
            foreach (var linked in linkedDocuments)
                if ((await linked.GetTextAsync(cancellation).ConfigureAwait(false)).ToString() != expected)
                    throw new InvalidOperationException("This code action produces conflicting edits for a linked source file.");
        }
        var openPaths = operations.OfType<OpenDocumentOperation>().Select(operation =>
        {
            var document = apply.ChangedSolution.GetDocument(operation.DocumentId) ?? throw new InvalidOperationException("The provider's source navigation has no source document.");
            var previous = item.Document.Project.Solution.GetDocument(operation.DocumentId);
            if (previous == null) return ResolveAddedSourcePath(document, item.Document);
            return (physicalMoves.Contains(document.Id) ? ResolvePhysicalDocumentInfoMove(previous, document, item.Document) : null)
                ?? AuthoredSourcePath(previous) ?? throw new InvalidOperationException("The provider's navigation has no authored source owner.");
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var projectEdits = new List<ScriptCodeActionProjectEdit>();
        foreach (var group in referenceEdits.GroupBy(edit => edit.ProjectPath, StringComparer.OrdinalIgnoreCase))
        {
            var retained = Volatile.Read(ref authoredProjects).SingleOrDefault(snapshot => string.Equals(snapshot.Path, group.Key, StringComparison.OrdinalIgnoreCase));
            using var properties = new AuthoredProjectProperties(retained == null ? await File.ReadAllBytesAsync(group.Key, cancellation).ConfigureAwait(false)
                : System.Text.Encoding.UTF8.GetBytes(retained.Xml), group.Key);
            var before = properties.Snapshot;
            foreach (var edit in group) properties.ChangeCompilerReference(edit.Change, edit.Configuration, edit.Framework);
            projectEdits.Add(new(group.Key, before, properties.Snapshot));
        }
        return new(item.Title, edits.Values.ToArray(), snapshots, item.Epoch, fixAllDiagnostics?.Failures, openPaths, item.Source.Path,
            loadedProjectFiles, Volatile.Read(ref authoredMembership), referenceEdits.ToArray(), projectEdits.ToArray(),
            referenceProjects.Select(project => CaptureReferenceContext(project.Before, project.After, physicalMoves)).ToArray(), CaptureReferenceContextBaseline());
    }
    private string? ResolvePhysicalDocumentInfoMove(Document before, Document after, Document origin)
    {
        if (before.SourceCodeKind != after.SourceCodeKind || before.FilePath == null || after.FilePath == null || before.FilePath != AuthoredSourcePath(before)
            || after.Project.FilePath == null) return null;
        string destination;
        try
        {
            destination = before.FilePath != after.FilePath && after.FilePath != null && Path.IsPathFullyQualified(after.FilePath)
                ? Path.GetFullPath(after.FilePath) : ResolveAddedSourcePath(after, origin, ignorePhysicalPath: true);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var projectRoot = Path.GetDirectoryName(Path.GetFullPath(after.Project.FilePath))!;
        var relative = Path.GetRelativePath(projectRoot, destination);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !Path.GetExtension(destination).Equals(".cs", StringComparison.OrdinalIgnoreCase)) return null;
        return string.Equals(destination, AuthoredSourcePath(before), StringComparison.Ordinal) ? null : destination;
    }
    private async Task<ScriptCodeActionReferenceEdit> PrepareCompilerReference(Project project, MetadataReference reference, bool included, CancellationToken cancellation)
    {
        if (reference is not PortableExecutableReference { FilePath: { Length: > 0 } path } portableReference || reference.Properties.Kind != MetadataImageKind.Assembly)
            throw new InvalidOperationException("The compiler reference has no physical assembly owner.");
        if (portableReference.GetMetadata() is not AssemblyMetadata metadata) throw new InvalidOperationException("The provider reference is not native assembly metadata.");
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var pe = new PEReader(input))
        {
            var actual = pe.GetMetadataReader(); var expected = metadata.GetModules()[0].GetMetadataReader();
            if (actual.GetGuid(actual.GetModuleDefinition().Mvid) != expected.GetGuid(expected.GetModuleDefinition().Mvid))
                throw new InvalidOperationException("The provider assembly reference changed before preview.");
        }
        var identity = System.Reflection.AssemblyName.GetAssemblyName(path).FullName
            ?? throw new InvalidOperationException("The compiler reference has no native assembly identity.");
        return await PrepareReference(project, new("Reference", Path.GetFullPath(path), included, identity,
            reference.Properties.Aliases.ToArray(), reference.Properties.EmbedInteropTypes), cancellation).ConfigureAwait(false);
    }
    private Task<ScriptCodeActionReferenceEdit> PrepareAnalyzerReference(Project project, Microsoft.CodeAnalysis.Diagnostics.AnalyzerReference reference, bool included, CancellationToken cancellation)
    {
        if (reference.FullPath is not { Length: > 0 } path) throw new InvalidOperationException("The analyzer reference has no physical assembly owner.");
        return PrepareReference(project, new("Analyzer", Path.GetFullPath(path), included, "", [], false), cancellation);
    }
    private async Task<ScriptCodeActionReferenceEdit> PrepareReference(Project project, ProjectCompilerReferenceChange change, CancellationToken cancellation)
    {
        if (project.FilePath == null) throw new InvalidOperationException("The compiler reference has no native project owner.");
        using var file = new FileStream(change.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false);
        return new(Path.GetFullPath(project.FilePath), configuration, framework, change, hash);
    }
    private static string ResolveAddedSourcePath(Document document, Document origin, bool ignorePhysicalPath = false)
    {
        if (!ignorePhysicalPath && document.FilePath != null) return Path.GetFullPath(document.FilePath);
        if (document.Project.FilePath == null || string.IsNullOrWhiteSpace(document.Name) || document.Name != Path.GetFileName(document.Name)
            || document.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Path.GetExtension(document.Name).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The new source has no unambiguous physical destination.");
        if (document.Folders.Any(folder => folder is "" or "." or ".." || folder != Path.GetFileName(folder)
            || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidOperationException("The source has an invalid logical folder.");
        var projectRoot = Path.GetDirectoryName(document.Project.FilePath)!;
        var known = origin.Project.Solution.GetProject(document.Project.Id)?.Documents.Where(source => source.FilePath != null).ToArray() ?? [];
        var exact = known.Where(source => source.Folders.SequenceEqual(document.Folders))
            .Select(source => Path.GetDirectoryName(Path.GetFullPath(source.FilePath!))!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (exact.Length == 1) return Path.Combine(exact[0], document.Name);
        if (exact.Length > 1) throw new InvalidOperationException("The logical source folder has conflicting physical mappings.");
        // Only use the native project-relative convention when an actual source
        // proves it; a linked/virtual logical folder is not a disk destination.
        var conventional = known.Any(source =>
        {
            var relative = Path.GetRelativePath(projectRoot, Path.GetDirectoryName(source.FilePath!)!);
            string[] folders = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return source.Folders.SequenceEqual(folders);
        });
        if (!conventional) throw new InvalidOperationException("The logical source folder has no physical project mapping.");
        var path = Path.Combine(document.Folders.Prepend(projectRoot).Append(document.Name).ToArray());
        return Path.GetFullPath(path);
    }
}
