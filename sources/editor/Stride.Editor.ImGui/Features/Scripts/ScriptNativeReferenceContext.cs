using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Stride.Editor.ImGui;

/// <summary>An actual native solution generation retained by semantic-context history.</summary>
internal sealed class ScriptNativeContextGeneration(Action release) : IDisposable
{
    private readonly object gate = new();
    private int references = 1;
    private bool initialReleased;
    public Guid Id { get; } = Guid.NewGuid();
    public bool IsRetired { get { lock (gate) return initialReleased; } }
    public IDisposable Acquire()
    {
        lock (gate)
        {
            if (references == 0) throw new ObjectDisposedException(nameof(ScriptNativeContextGeneration));
            references++; return new Lease(this);
        }
    }
    public void Dispose()
    {
        lock (gate) { if (initialReleased) return; initialReleased = true; }
        Release();
    }
    private void Release()
    {
        lock (gate) { if (--references != 0) return; }
        release();
    }
    private sealed class Lease(ScriptNativeContextGeneration owner) : IDisposable
    {
        private ScriptNativeContextGeneration? retained = owner;
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.Release();
    }
}

/// <summary>Immutable real provider reference state; no project XML or source file is implied.</summary>
internal sealed class ScriptNativeReferenceContext : IDisposable
{
    private readonly object gate = new();
    private IDisposable? generationLease;
    private int references = 1;
    private bool preparationReleased;
    private readonly ScriptNativeContextGeneration nativeGeneration;
    public ScriptNativeReferenceContext(ScriptNativeContextGeneration generation, long order, Project before, Project after,
        Project? baseline, IReadOnlySet<DocumentId>? physicalMoves = null)
    {
        nativeGeneration = generation;
        Generation = generation.Id; Order = order; ProjectId = before.Id;
        ProjectPath = before.FilePath; ProjectName = before.Name;
        BeforeMetadata = before.MetadataReferences.ToImmutableArray(); Metadata = after.MetadataReferences.ToImmutableArray();
        BeforeAnalyzers = before.AnalyzerReferences.ToImmutableArray(); Analyzers = after.AnalyzerReferences.ToImmutableArray();
        Documents = after.Documents.Where(document => baseline?.GetDocument(document.Id) is { } original
            && physicalMoves?.Contains(document.Id) != true && (!ScriptNativeDocumentInfo.Same(original, document)
                || before.GetDocument(document.Id) is { } previous && !ScriptNativeDocumentInfo.Same(original, previous)))
            .Select(ScriptNativeDocumentInfo.Capture).ToImmutableArray();
        BeforeDocuments = Documents.Select(document => ScriptNativeDocumentInfo.Capture(before.GetDocument(document.Id)!)).ToImmutableArray();
        foreach (var reference in BeforeMetadata.Concat(Metadata).OfType<PortableExecutableReference>().Distinct())
            try { reference.GetMetadata(); }
            catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException)
            { /* Retain the actual unresolved/invalid reference; native compiler diagnostics remain authoritative. */ }
        generationLease = generation.Acquire();
    }
    public Guid Generation { get; }
    public long Order { get; }
    public bool IsRetired => nativeGeneration.IsRetired;
    public bool MayPrune { get { lock (gate) return preparationReleased && references == 1 && nativeGeneration.IsRetired; } }
    public ProjectId ProjectId { get; }
    public string? ProjectPath { get; }
    public string ProjectName { get; }
    public ImmutableArray<MetadataReference> BeforeMetadata { get; }
    public ImmutableArray<MetadataReference> Metadata { get; }
    public ImmutableArray<AnalyzerReference> BeforeAnalyzers { get; }
    public ImmutableArray<AnalyzerReference> Analyzers { get; }
    public ImmutableArray<ScriptNativeDocumentInfo> BeforeDocuments { get; }
    public ImmutableArray<ScriptNativeDocumentInfo> Documents { get; }
    internal static string Describe(MetadataReference reference)
    {
        var name = reference.Display;
        if (name == null && reference is CompilationReference compilation) name = compilation.Compilation.Assembly.Identity.ToString();
        if (name == null && reference is PortableExecutableReference pe)
        {
            try
            {
                var metadata = pe.GetMetadata();
                if (metadata is AssemblyMetadata assembly)
                {
                    var reader = assembly.GetModules()[0].GetMetadataReader();
                    name = reader.GetString(reader.GetAssemblyDefinition().Name);
                }
                else if (metadata is ModuleMetadata module)
                {
                    var reader = module.GetMetadataReader(); name = reader.GetString(reader.GetModuleDefinition().Name);
                }
            }
            catch (Exception error) when (error is IOException or BadImageFormatException or UnauthorizedAccessException) { name = pe.FilePath; }
        }
        return (reference.Properties.Aliases.Length == 0 ? "" : string.Join(',', reference.Properties.Aliases) + " = ")
            + (name ?? reference.Properties.Kind.ToString()) + (reference.Properties.Kind == MetadataImageKind.Module ? " [Module]" : "")
            + (reference.Properties.EmbedInteropTypes ? " [EmbedInteropTypes]" : "");
    }
    internal static string Describe(AnalyzerReference reference) => reference.Display ?? reference.FullPath ?? reference.GetType().FullName ?? reference.GetType().Name;
    public IDisposable Acquire()
    {
        lock (gate)
        {
            if (references == 0) throw new ObjectDisposedException(nameof(ScriptNativeReferenceContext));
            references++; return new Lease(this);
        }
    }
    public void Dispose()
    {
        lock (gate) { if (preparationReleased) return; preparationReleased = true; }
        Release(); GC.SuppressFinalize(this);
    }
    ~ScriptNativeReferenceContext() { try { Dispose(); } catch { } }
    private void Release()
    {
        IDisposable? lease;
        lock (gate) { if (--references != 0) return; lease = generationLease; generationLease = null; }
        lease?.Dispose();
    }
    private sealed class Lease(ScriptNativeReferenceContext owner) : IDisposable
    {
        private ScriptNativeReferenceContext? retained = owner;
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.Release();
    }
}

internal sealed record ScriptNativeDocumentInfo(DocumentId Id, string Name, ImmutableArray<string> Folders, string? FilePath, SourceCodeKind Kind)
{
    internal static ScriptNativeDocumentInfo Capture(Document document)
        => new(document.Id, document.Name, document.Folders.ToImmutableArray(), document.FilePath, document.SourceCodeKind);
    internal static bool Same(Document left, Document right) => left.Name == right.Name && left.FilePath == right.FilePath
        && left.SourceCodeKind == right.SourceCodeKind && left.Folders.SequenceEqual(right.Folders);
    internal bool Same(ScriptNativeDocumentInfo other) => Name == other.Name && FilePath == other.FilePath && Kind == other.Kind && Folders.SequenceEqual(other.Folders);
    internal string[] Describe() => ["Name: " + System.Text.Json.JsonSerializer.Serialize(Name),
        "Folders: " + System.Text.Json.JsonSerializer.Serialize(Folders),
        "FilePath: " + System.Text.Json.JsonSerializer.Serialize(FilePath), "SourceCodeKind: " + Kind];
}
