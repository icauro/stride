using System.Composition;
using System.Composition.Hosting;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;

namespace Stride.Editor.ImGui;

/// <summary>Actual MEF code-fix/refactoring exports shared with the project's workspace host.</summary>
internal sealed class ScriptCodeActionProviders : IDisposable
{
    private readonly CompositionHost composition;
    public MefHostServices Host { get; }
    internal object NativeCompositionEvidence { get; }
    internal object GetNativeService(Type contract) => composition.GetExport(contract);
    public ScriptCodeActionProviders()
    {
        var assemblies = MefHostServices.DefaultAssemblies.Concat([typeof(CompletionService).Assembly, Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")]).Distinct();
        var parts = assemblies.SelectMany(assembly => assembly.DefinedTypes).Select(type => type.AsType()).Distinct().ToArray();
        const string pythiaContractName = "Microsoft.CodeAnalysis.ExternalAccess.Pythia.Api.IPythiaSignatureHelpProviderImplementation";
        const string pythiaProviderName = "Microsoft.CodeAnalysis.ExternalAccess.Pythia.PythiaSignatureHelpProvider";
        var pythiaContract = parts.SingleOrDefault(type => type.FullName == pythiaContractName);
        var pythiaImplementationAvailable = pythiaContract != null && parts.Any(type =>
            type.GetCustomAttributes<ExportAttribute>().Any(export => export.ContractType == pythiaContract));
        var pythiaProviderPresent = parts.Any(type => type.FullName == pythiaProviderName);
        // This optional Visual Studio integration requires an external implementation
        // that the standalone editor does not ship. Keep the ordinary Roslyn providers.
        if (!pythiaImplementationAvailable)
            parts = parts.Where(type => type.FullName != pythiaProviderName).ToArray();
        NativeCompositionEvidence = new { pythiaContract = pythiaContractName, pythiaImplementationAvailable,
            pythiaProviderPresent, pythiaProviderExcluded = pythiaProviderPresent && !pythiaImplementationAvailable,
            ordinaryInvocationSignatureProviderPresent = parts.Any(type =>
                type.FullName == "Microsoft.CodeAnalysis.CSharp.SignatureHelp.InvocationExpressionSignatureHelpProvider") };
        var hasRefresher = parts.Any(type => type.Name != "NullDiagnosticsRefresher" && type.IsDefined(typeof(ExportAttribute))
            && type.GetInterfaces().Any(contract => contract.Name == "IDiagnosticsRefresher"));
        if (hasRefresher) parts = parts.Where(type => type.Name != "NullDiagnosticsRefresher").ToArray();
        composition = new ContainerConfiguration().WithParts(parts).CreateContainer();
        Host = MefHostServices.Create(composition);
    }
    public IEnumerable<CodeFixProvider> Fixes(string language, string path, Action<Exception> failed)
    {
        foreach (var export in composition.GetExports<Lazy<CodeFixProvider>>())
        {
            CodeFixProvider provider;
            try { provider = export.Value; } catch (Exception error) { failed(error); continue; }
            if (provider.GetType().GetCustomAttributes<ExportCodeFixProviderAttribute>().Any(attribute => attribute.Languages.Contains(language)
                && (attribute.DocumentKinds == null || attribute.DocumentKinds.Contains(nameof(TextDocumentKind.Document)))
                && (attribute.DocumentExtensions == null || attribute.DocumentExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))) yield return provider;
        }
    }
    public IEnumerable<CodeRefactoringProvider> Refactorings(string language, Action<Exception> failed)
    {
        foreach (var export in composition.GetExports<Lazy<CodeRefactoringProvider>>())
        {
            CodeRefactoringProvider provider;
            try { provider = export.Value; } catch (Exception error) { failed(error); continue; }
            if (provider.GetType().GetCustomAttributes<ExportCodeRefactoringProviderAttribute>().Any(attribute => attribute.Languages.Contains(language))) yield return provider;
        }
    }
    public void Dispose() => composition.Dispose();
}
