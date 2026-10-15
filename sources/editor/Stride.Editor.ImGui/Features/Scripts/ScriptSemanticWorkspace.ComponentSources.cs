using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stride.Editor.ImGui;

internal sealed record ScriptComponentSourceResult(long Epoch, string ProjectPath, string AssemblyIdentity, string[] MetadataNames);

internal sealed partial class ScriptSemanticWorkspace
{
    internal async Task<ScriptComponentSourceResult> DiscoverSourceComponents(ScriptSourceSnapshot[] sources, string path, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        var token = linked.Token;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WaitForQuiet(token).ConfigureAwait(false);
            var expected = Epoch;
            var (document, _) = await PrepareDocument(sources, path, expected, token).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The C# project returned no semantic model.");
            var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The C# project returned no syntax root.");
            var component = model.Compilation.GetTypeByMetadataName("Stride.Engine.EntityComponent");
            var names = new List<string>();
            foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                token.ThrowIfCancellationRequested();
                if (model.GetDeclaredSymbol(declaration, token) is not INamedTypeSymbol { TypeKind: TypeKind.Class } type) continue;
                for (var parent = type.BaseType; parent != null; parent = parent.BaseType)
                    if (component != null && SymbolEqualityComparer.Default.Equals(parent, component))
                    { names.Add(MetadataName(type)); break; }
            }
            if (Epoch != expected) throw new OperationCanceledException("The project source context changed.", token);
            return new(expected, Path.GetFullPath(document.Project.FilePath!), model.Compilation.Assembly.Identity.GetDisplayName(), names.Distinct(StringComparer.Ordinal).ToArray());
        }
        finally { gate.Release(); }
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType != null) return MetadataName(type.ContainingType) + "+" + type.MetadataName;
        var space = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        return string.IsNullOrEmpty(space) ? type.MetadataName : space + "." + type.MetadataName;
    }
}
