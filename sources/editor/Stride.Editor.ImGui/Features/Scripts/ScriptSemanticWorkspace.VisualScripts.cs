using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stride.Assets.Scripts;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed record VisualScriptIssue(DiagnosticSeverity Severity, string Message, Guid? Block, Guid? Link,
    string? Path = null, int Line = 1, int Column = 1, string Code = "");
internal sealed record VisualScriptGeneratedSlots(Guid Method, Guid Block, Slot[] Slots);
internal sealed record VisualScriptVariableReference(Guid Method, string Kind, string Name, string Type);
internal sealed record VisualScriptSemanticResult(long Revision, long Epoch, VisualScriptIssue[] Issues,
    VisualScriptGeneratedSlots[] Slots, IMethodSymbol[] Overrides, Compilation Compilation, VisualScriptVariableReference[] VariableReferences);

internal sealed partial class ScriptSemanticWorkspace
{
    public async Task<VisualScriptSemanticResult> AnalyzeVisualScript(VisualScriptCompilationSnapshot snapshot,
        ScriptSourceSnapshot[] sources, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token; await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WaitForQuiet(token).ConfigureAwait(false); var expected = Epoch;
            var solution = await PrepareSolution(sources, expected, token).ConfigureAwait(false);
            var path = Path.GetFullPath(snapshot.ProjectPath);
            var candidates = solution.Projects.Where(project => project.FilePath != null
                && string.Equals(Path.GetFullPath(project.FilePath), path, StringComparison.OrdinalIgnoreCase)).ToArray();
            var distances = ProjectDistances(solution);
            var selected = candidates.OrderBy(project => distances.GetValueOrDefault(project.Id, int.MaxValue)).FirstOrDefault();
            if (selected == null) throw new InvalidOperationException("The visual-script project is absent from the actual loaded compiler context.");
            var compilation = await selected.GetCompilationAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The visual-script project returned no actual compilation.");
            var generatedPath = Path.GetFullPath(snapshot.GeneratedPath);
            var annotated = snapshot.Result.SyntaxTree ?? throw new InvalidDataException("The visual-script compiler returned no annotated syntax tree.");
            var tree = annotated.WithRootAndOptions(await annotated.GetRootAsync(token).ConfigureAwait(false), selected.ParseOptions!).WithFilePath(generatedPath);
            var oldTrees = compilation.SyntaxTrees.Where(candidate => !string.IsNullOrEmpty(candidate.FilePath)
                && string.Equals(Path.GetFullPath(candidate.FilePath), generatedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            compilation = compilation.RemoveSyntaxTrees(oldTrees).AddSyntaxTrees(tree);
            using var analyzers = CreateAnalyzerRun(selected, token, liveAnalysis: true);
            var diagnostics = compilation.GetDiagnostics(token).Concat(await analyzers.Run(compilation, token).ConfigureAwait(false));
            var root = await tree.GetRootAsync(token).ConfigureAwait(false);
            var issues = new List<VisualScriptIssue>();
            foreach (var message in snapshot.Result.Messages.Where(message => message.Type >= Stride.Core.Diagnostics.LogMessageType.Warning))
                issues.Add(new(message.Type >= Stride.Core.Diagnostics.LogMessageType.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, message.Text, null, null));
            foreach (var diagnostic in diagnostics.Where(diagnostic => diagnostic.Location.SourceTree == tree && !diagnostic.IsSuppressed && diagnostic.Severity != DiagnosticSeverity.Hidden))
            {
                Guid? block = null, link = null;
                var span = diagnostic.Location.SourceSpan;
                var node = root.FindNode(span, getInnermostNodeForTie: true);
                for (var current = node; current != null; current = current.Parent)
                {
                    block ??= FindVisualAnnotation(current, "Block"); link ??= FindVisualAnnotation(current, "Link");
                }
                var location = diagnostic.Location.GetLineSpan();
                issues.Add(new(diagnostic.Severity, diagnostic.GetMessage(), block, link, location.Path,
                    location.StartLinePosition.Line + 1, location.StartLinePosition.Character + 1, diagnostic.Id));
            }
            var slots = new List<VisualScriptGeneratedSlots>();
            foreach (var method in ((VisualScriptAsset)snapshot.Item.Asset).Methods)
                foreach (var block in method.Blocks.Values)
                {
                    token.ThrowIfCancellationRequested();
                    var generated = new List<Slot>(); block.GenerateSlots(generated, new SlotGeneratorContext(compilation));
                    slots.Add(new(method.Id, block.Id, generated.ToArray()));
                }
            var model = compilation.GetSemanticModel(tree);
            var variables = new List<VisualScriptVariableReference>();
            foreach (var declaration in root.DescendantNodesAndSelf().OfType<MethodDeclarationSyntax>())
            {
                token.ThrowIfCancellationRequested();
                if (FindVisualAnnotation(declaration, "Method") is not { } methodId || declaration.Body?.Statements.FirstOrDefault() is not { } first) continue;
                foreach (var symbol in model.LookupSymbols(first.SpanStart))
                {
                    var type = symbol switch { IFieldSymbol field => field.Type, IPropertySymbol property => property.Type, IParameterSymbol parameter => parameter.Type, _ => null };
                    if (type != null) variables.Add(new(methodId, symbol.Kind.ToString(), symbol.Name, type.ToDisplayString()));
                }
            }
            var actualType = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Select(node => model.GetDeclaredSymbol(node, token)).OfType<INamedTypeSymbol>().FirstOrDefault();
            var overrides = new List<IMethodSymbol>();
            if (actualType != null)
            {
                for (var type = actualType.BaseType; type != null; type = type.BaseType)
                    overrides.AddRange(type.GetMembers().OfType<IMethodSymbol>().Where(method => method.MethodKind == MethodKind.Ordinary
                        && !method.IsSealed && !method.IsStatic && (method.IsVirtual || method.IsAbstract || method.IsOverride)
                        && compilation.IsSymbolAccessibleWithin(method, actualType)
                        && !actualType.GetMembers(method.Name).OfType<IMethodSymbol>().Any(own => SymbolEqualityComparer.Default.Equals(own.OverriddenMethod, method))));
                foreach (var type in actualType.AllInterfaces)
                    overrides.AddRange(type.GetMembers().OfType<IMethodSymbol>().Where(method => method.MethodKind == MethodKind.Ordinary
                        && actualType.FindImplementationForInterfaceMember(method) == null));
            }
            token.ThrowIfCancellationRequested();
            if (Epoch != expected) throw new OperationCanceledException("The visual-script compiler context changed.", token);
            return new(snapshot.Revision, expected, issues.ToArray(), slots.ToArray(), overrides.Distinct(SymbolEqualityComparer.Default).Cast<IMethodSymbol>().ToArray(), compilation, variables.ToArray());
        }
        finally { gate.Release(); }
    }
    private static Guid? FindVisualAnnotation(SyntaxNode node, string kind)
    {
        foreach (var annotation in node.GetAnnotations(kind)) if (Guid.TryParse(annotation.Data, out var id)) return id;
        return null;
    }
}
