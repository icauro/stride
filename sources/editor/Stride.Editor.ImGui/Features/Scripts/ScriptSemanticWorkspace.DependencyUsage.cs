using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Stride.Core.Assets;
using System.Text;
using NuGet.ProjectModel;

namespace Stride.Editor.ImGui;

internal sealed record ScriptDependencyUsage(string Path, int Line, int Column, string Symbol);
internal sealed record ScriptDependencyUsageResult(long Epoch, ScriptDependencyUsage? Usage, ScriptSourceSnapshot[] Sources,
    ScriptProjectFileBaseline[] Inputs, ScriptProjectMembershipSnapshot[] Membership, string Framework,
    AuthoredProjectSnapshot[] ProjectSnapshots, string[] GeneratedSources, LockFile? ResolvedGraph, PackageDependencyRemovalScope ResolvedScope);

internal sealed partial class ScriptSemanticWorkspace
{
    public async Task<ScriptDependencyUsageResult> FindDependencyUsage(string ownerProjectPath, ScriptSourceSnapshot[] snapshots,
        PackageDependencyRemovalScope scope, CancellationToken cancellation, ProjectDependencyEvaluation? authoredContext = null,
        Func<LockFile, PackageDependencyRemovalScope>? resolvedScope = null, IReadOnlySet<string>? excludedSourcePaths = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WaitForQuiet(token).ConfigureAwait(false);
            var expectedEpoch = Epoch;
            var solution = await PrepareSolution(snapshots, expectedEpoch, token).ConfigureAwait(false);
            var nativeProject = authoredResolution?.Projects.SingleOrDefault(project => string.Equals(Path.GetFullPath(project.ProjectPath), Path.GetFullPath(ownerProjectPath), StringComparison.OrdinalIgnoreCase)
                && project.Framework == framework);
            var nativeGraph = nativeProject == null ? null : new LockFileFormat().Read(nativeProject.AssetsFilePath);
            if (nativeGraph != null) scope = resolvedScope?.Invoke(nativeGraph) ?? throw new InvalidOperationException("The authored dependency graph has no removal-scope evaluator.");
            var owner = solution.Projects.SingleOrDefault(project => project.FilePath != null
                && string.Equals(Path.GetFullPath(project.FilePath), Path.GetFullPath(ownerProjectPath), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The dependency's actual C# project is not loaded.");
            if (authoredContext != null)
            {
                ProjectDependencyEvaluation? diskContext = null;
                if (nativeProject == null)
                {
                    byte[] diskXml;
                    using (var stream = new FileStream(ownerProjectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var memory = new MemoryStream()) { stream.CopyTo(memory); diskXml = memory.ToArray(); }
                    using var disk = new AuthoredProjectProperties(diskXml, ownerProjectPath);
                    diskContext = disk.EvaluateDependencyContexts(configuration).SingleOrDefault(context => context.Framework == framework)
                        ?? throw new InvalidOperationException("The target framework has not been resolved by MSBuild.");
                    if (!AuthoredProjectProperties.SameReferenceAndCompilerContext(authoredContext, diskContext))
                        throw new InvalidOperationException("The project reference or compiler context changed before MSBuild resolution.");
                }
                var desired = authoredContext.CompilePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var removedPaths = (diskContext?.CompilePaths ?? []).Where(path => !desired.Contains(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var document in owner.Documents.Where(document => AuthoredSourcePath(document) is { } sourcePath && (removedPaths.Contains(Path.GetFullPath(sourcePath)) || excludedSourcePaths?.Contains(Path.GetFullPath(sourcePath)) == true)).ToArray())
                    solution = solution.RemoveDocument(document.Id);
                owner = solution.GetProject(owner.Id)!;
                var existing = owner.Documents.Select(AuthoredSourcePath).Where(path => path != null).Select(path => Path.GetFullPath(path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in desired.Where(path => !existing.Contains(path)))
                {
                    var source = snapshots.SingleOrDefault(source => string.Equals(source.Path, path, StringComparison.OrdinalIgnoreCase));
                    var text = source?.Text ?? File.ReadAllText(path);
                    solution = solution.AddDocument(DocumentId.CreateNewId(owner.Id, path), source?.Name ?? Path.GetFileName(path), SourceText.From(text, Encoding.UTF8), source?.Folders, path);
                }
                owner = solution.GetProject(owner.Id)!;
            }
            var compilation = await owner.GetCompilationAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The C# project has no compilation.");
            var sources = new List<ScriptSourceSnapshot>();
            string? SourcePath(SyntaxTree tree)
                => solution.GetDocument(tree) is { } sourceDocument ? AuthoredSourcePath(sourceDocument) : tree.FilePath;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var path = SourcePath(tree);
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || snapshots.Any(source => string.Equals(source.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))))
                    sources.Add(new(Path.GetFullPath(path), 0, (await tree.GetTextAsync(token).ConfigureAwait(false)).ToString()));
            }
            var inputPaths = await ScriptProjectWatchPaths.Read(ownerProjectPath, configuration, framework, token).ConfigureAwait(false);
            var inputs = new List<ScriptProjectFileBaseline>();
            foreach (var path in inputPaths.Concat(loadedProjectFiles.Select(file => file.Path))
                .Concat(compilation.References.OfType<PortableExecutableReference>().Select(reference => reference.FilePath).Where(path => path != null).Cast<string>())
                .Concat(solution.Projects.SelectMany(project => project.AnalyzerReferences.OfType<AnalyzerFileReference>()).Select(reference => reference.FullPath))
                .Concat(solution.Projects.SelectMany(project => project.AdditionalDocuments.Concat(project.AnalyzerConfigDocuments)).Select(document => document.FilePath).Where(path => path != null).Cast<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(path) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(ownerProjectPath), StringComparison.OrdinalIgnoreCase)
                    && (authoredResolution == null || !Path.GetFullPath(path).StartsWith(authoredResolution.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    inputs.Add(new(Path.GetFullPath(path), System.Security.Cryptography.SHA256.HashData(stream)));
                }
            var paths = scope.AssemblyPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var projectPaths = scope.ProjectPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<AssemblyIdentity>();
            foreach (var reference in owner.ProjectReferences)
            {
                var referenced = solution.GetProject(reference.ProjectId);
                if (referenced?.FilePath == null || !projectPaths.Contains(Path.GetFullPath(referenced.FilePath))) continue;
                var dependency = await referenced.GetCompilationAsync(token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A referenced project has no compilation.");
                identities.Add(dependency.Assembly.Identity);
            }
            var removed = compilation.References.Where(reference => reference is PortableExecutableReference file
                && file.FilePath != null && paths.Contains(Path.GetFullPath(file.FilePath))
                || reference is CompilationReference source && identities.Contains(source.Compilation.Assembly.Identity)).ToArray();
            foreach (var reference in removed)
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly) identities.Add(assembly.Identity);
            bool Uses(ISymbol? symbol)
            {
                if (symbol is IAliasSymbol alias) return Uses(alias.Target);
                if (symbol?.ContainingAssembly is { } assembly && identities.Contains(assembly.Identity)) return true;
                if (symbol is IArrayTypeSymbol array) return Uses(array.ElementType);
                if (symbol is IPointerTypeSymbol pointer) return Uses(pointer.PointedAtType);
                if (symbol is INamedTypeSymbol named) return named.TypeArguments.Any(Uses);
                return false;
            }
            ScriptDependencyUsage Describe(SyntaxNode syntax, ISymbol? symbol)
            {
                var line = syntax.GetLocation().GetLineSpan();
                return new(SourcePath(syntax.SyntaxTree) ?? line.Path, line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1, symbol?.ToDisplayString() ?? "");
            }
            var visitedOperations = new HashSet<IOperation>();
            ScriptDependencyUsage? InspectOperation(IOperation? root)
            {
                if (root == null) return null;
                var pending = new Stack<IOperation>(); pending.Push(root);
                while (pending.TryPop(out var operation))
                {
                    if (!visitedOperations.Add(operation)) continue;
                    token.ThrowIfCancellationRequested();
                    ISymbol? member = operation switch
                    {
                        IInvocationOperation invocation => invocation.TargetMethod,
                        IObjectCreationOperation creation => creation.Constructor,
                        IMemberReferenceOperation reference => reference.Member,
                        IConversionOperation conversion => conversion.OperatorMethod,
                        IBinaryOperation binary => binary.OperatorMethod,
                        IUnaryOperation unary => unary.OperatorMethod,
                        IIncrementOrDecrementOperation increment => increment.OperatorMethod,
                        ITypeOfOperation typeOf => typeOf.TypeOperand,
                        ISizeOfOperation sizeOf => sizeOf.TypeOperand,
                        IIsTypeOperation isType => isType.TypeOperand,
                        IDeclarationPatternOperation pattern => pattern.MatchedType,
                        _ => null,
                    };
                    if (Uses(member) || Uses(operation.Type)) return Describe(operation.Syntax, member ?? operation.Type);
                    foreach (var child in operation.ChildOperations) pending.Push(child);
                }
                return null;
            }
            ScriptDependencyUsage? usage = null;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(token).ConfigureAwait(false);
                foreach (var node in root.DescendantNodesAndSelf())
                {
                    token.ThrowIfCancellationRequested();
                    SymbolInfo symbol = default;
                    if (node is ExpressionSyntax expression)
                    {
                        symbol = model.GetSymbolInfo(expression, token);
                        var type = model.GetTypeInfo(expression, token);
                        if (Uses(type.Type) || Uses(type.ConvertedType)) { usage = Describe(node, type.Type ?? type.ConvertedType); break; }
                        if (expression is IdentifierNameSyntax identifier && Uses(model.GetAliasInfo(identifier, token)))
                        { usage = Describe(node, model.GetAliasInfo(identifier, token)); break; }
                    }
                    else if (node is AttributeSyntax attribute) symbol = model.GetSymbolInfo(attribute, token);
                    else if (node is ConstructorInitializerSyntax initializer) symbol = model.GetSymbolInfo(initializer, token);
                    if (Uses(symbol.Symbol) || symbol.CandidateSymbols.Any(Uses)) { usage = Describe(node, symbol.Symbol ?? symbol.CandidateSymbols.First(Uses)); break; }
                    usage = InspectOperation(model.GetOperation(node, token));
                    if (usage != null) break;
                }
                if (usage != null) break;
            }
            if (usage == null && removed.Length > 0)
            {
                // Ask the compiler about implicit/binding requirements that do
                // not expose an explicit source symbol (including forwarding).
                var priorErrors = compilation.GetDiagnostics(token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .GroupBy(DiagnosticIdentity).ToDictionary(group => group.Key, group => group.Count());
                Diagnostic? introduced = null;
                foreach (var diagnostic in compilation.RemoveReferences(removed).GetDiagnostics(token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    var identity = DiagnosticIdentity(diagnostic);
                    if (priorErrors.TryGetValue(identity, out var count) && count > 0) { priorErrors[identity] = count - 1; continue; }
                    introduced = diagnostic; break;
                }
                if (introduced != null)
                {
                    var line = introduced.Location.GetLineSpan();
                    usage = new(introduced.Location.SourceTree is { } diagnosticTree ? SourcePath(diagnosticTree) ?? line.Path : line.Path, line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1, introduced.GetMessage());
                }
            }
            if (Epoch != expectedEpoch) throw new OperationCanceledException("The project source context changed.", token);
            var generated = authoredResolution == null ? [] : sources.Where(source => source.Path.StartsWith(authoredResolution.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Select(source => source.Path).ToArray();
            return new(expectedEpoch, usage, sources.ToArray(), inputs.ToArray(), Volatile.Read(ref authoredMembership), framework,
                Volatile.Read(ref authoredProjects), generated, nativeGraph, scope);
        }
        finally { gate.Release(); }
    }
    private static (string Id, string Path, LocationKind Kind, int Start, int Length, string Additional, string Message) DiagnosticIdentity(Diagnostic diagnostic)
        => (diagnostic.Id, diagnostic.Location.SourceTree?.FilePath ?? "", diagnostic.Location.Kind, diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length,
            string.Join("|", diagnostic.AdditionalLocations.Select(location => (location.SourceTree?.FilePath ?? "") + ":" + location.Kind + ":" + location.SourceSpan.Start + ":" + location.SourceSpan.Length)), diagnostic.GetMessage());
}
