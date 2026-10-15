using Microsoft.CodeAnalysis;

namespace Stride.Editor.ImGui;

internal sealed partial class ScriptSemanticWorkspace
{
    private ScriptNativeContextGeneration? nativeContextGeneration;
    private long referenceContextOrder;
    private long referenceOnlyChanges;
    private readonly object referenceContextGate = new();
    private ScriptNativeReferenceContext[] retainedReferenceContexts = [];
    private IDisposable[] referenceContextLeases = [];

    public void SetReferenceContexts(ScriptNativeReferenceContext[] contexts)
    {
        lock (referenceContextGate)
        {
            if (disposed || retainedReferenceContexts.SequenceEqual(contexts)) return;
            var next = new List<IDisposable>();
            try { foreach (var context in contexts.Where(context => context.Generation == nativeContextGeneration?.Id)) next.Add(context.Acquire()); }
            catch { foreach (var lease in next) lease.Dispose(); throw; }
            var previous = referenceContextLeases;
            retainedReferenceContexts = contexts.ToArray(); referenceContextLeases = next.ToArray();
            referenceOnlyChanges++;
            Interlocked.Increment(ref epoch);
            foreach (var lease in previous) lease.Dispose();
        }
    }
    private void RefreshReferenceContextLeases()
    {
        lock (referenceContextGate)
        {
            var next = new List<IDisposable>();
            try { foreach (var context in retainedReferenceContexts.Where(context => context.Generation == nativeContextGeneration?.Id)) next.Add(context.Acquire()); }
            catch { foreach (var lease in next) lease.Dispose(); throw; }
            var previous = referenceContextLeases; referenceContextLeases = next.ToArray();
            foreach (var lease in previous) lease.Dispose();
        }
    }
    private long NativeContextVersion { get { lock (referenceContextGate) return Interlocked.Read(ref epoch) - referenceOnlyChanges; } }
    public bool IsCurrentReferenceContext(ScriptNativeReferenceContext context)
        => nativeContextGeneration?.Id == context.Generation;

    private Solution ApplyReferenceContexts(Solution solution)
    {
        lock (referenceContextGate)
            foreach (var context in retainedReferenceContexts.Where(context => context.Generation == nativeContextGeneration?.Id)
                .GroupBy(context => context.ProjectId).Select(group => group.MaxBy(context => context.Order)!))
            {
                if (solution.GetProject(context.ProjectId) == null) throw new InvalidOperationException("The retained native reference project is no longer loaded.");
                solution = solution.WithProjectMetadataReferences(context.ProjectId, context.Metadata)
                    .WithProjectAnalyzerReferences(context.ProjectId, context.Analyzers);
                foreach (var document in context.Documents)
                    if (solution.GetDocument(document.Id) != null)
                        solution = solution.WithDocumentName(document.Id, document.Name).WithDocumentFolders(document.Id, document.Folders)
                            .WithDocumentFilePath(document.Id, document.FilePath).WithDocumentSourceCodeKind(document.Id, document.Kind);
            }
        return solution;
    }
    private void ReleaseReferenceContexts()
    {
        lock (referenceContextGate)
        {
            foreach (var lease in referenceContextLeases) lease.Dispose();
            referenceContextLeases = []; retainedReferenceContexts = [];
        }
    }
    private ScriptNativeReferenceContext CaptureReferenceContext(Project before, Project after, IReadOnlySet<DocumentId>? physicalMoves = null)
    {
        var generation = nativeContextGeneration ?? throw new InvalidOperationException("The native semantic generation is unavailable.");
        return new(generation, ++referenceContextOrder, before, after, baseline?.GetProject(before.Id), physicalMoves);
    }
    private string? AuthoredSourcePath(Microsoft.CodeAnalysis.Document document)
        => baseline?.GetDocument(document.Id)?.FilePath ?? document.FilePath;
    private ScriptDiagnosticReport DescribeAuthoredDiagnostic(Diagnostic diagnostic, Solution solution)
    {
        var report = ScriptAnalyzerRun.Describe(diagnostic);
        var document = diagnostic.Location.SourceTree is { } tree ? solution.GetDocument(tree) : null;
        return document == null ? report : report with { Path = AuthoredSourcePath(document) };
    }
    private ScriptNativeReferenceContext[] CaptureReferenceContextBaseline()
    { lock (referenceContextGate) return retainedReferenceContexts.ToArray(); }
    private static void DisposeNativeContext(Action release)
    {
        try { release(); }
        catch (Exception error) { Console.Error.WriteLine("Native semantic generation cleanup failed: " + error); }
    }
}
