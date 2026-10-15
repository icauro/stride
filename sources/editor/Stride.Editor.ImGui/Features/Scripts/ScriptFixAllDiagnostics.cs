using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;

namespace Stride.Editor.ImGui;

internal sealed record ScriptFixAllRequest(CodeFixProvider Provider, FixAllProvider FixAll, FixAllScope Scope, string EquivalenceKey, string[] DiagnosticIds);

internal sealed class ScriptFixAllDiagnostics(string[] ids, CancellationToken lifetime,
    Func<Project, CancellationToken, ScriptAnalyzerRun>? analyzerFactory = null) : FixAllContext.DiagnosticProvider
{
    private readonly HashSet<string> diagnosticIds = ids.ToHashSet(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ProjectId, Lazy<Task<Diagnostic[]>>> projects = new();
    private readonly ConcurrentQueue<ScriptDiagnosticReport> failures = new();
    public ScriptDiagnosticReport[] Failures => failures.ToArray();
    private Task<Diagnostic[]> Get(Project project) => projects.GetOrAdd(project.Id,
        _ => new Lazy<Task<Diagnostic[]>>(() => Analyze(project))).Value;
    private async Task<Diagnostic[]> Analyze(Project project)
    {
        lifetime.ThrowIfCancellationRequested();
        var compilation = await project.GetCompilationAsync(lifetime).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The fix-all project compilation is unavailable.");
        using var analyzers = analyzerFactory?.Invoke(project, lifetime) ?? new ScriptAnalyzerRun(project, lifetime);
        var result = compilation.GetDiagnostics(lifetime).Concat(await analyzers.Run(compilation, lifetime).ConfigureAwait(false))
            .Where(diagnostic => !diagnostic.IsSuppressed && diagnosticIds.Contains(diagnostic.Id)).ToArray();
        foreach (var failure in analyzers.Failures) failures.Enqueue(failure);
        return result;
    }
    public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = await Get(document.Project).WaitAsync(cancellationToken).ConfigureAwait(false);
        return diagnostics.Where(diagnostic => diagnostic.Location.IsInSource && diagnostic.Location.SourceTree == tree).ToArray();
    }
    public override async Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
        => (await Get(project).WaitAsync(cancellationToken).ConfigureAwait(false)).Where(diagnostic => !diagnostic.Location.IsInSource).ToArray();
    public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
        => await Get(project).WaitAsync(cancellationToken).ConfigureAwait(false);
}
