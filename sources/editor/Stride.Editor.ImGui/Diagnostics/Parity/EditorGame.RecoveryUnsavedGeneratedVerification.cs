using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record RecoveryUnsavedGeneratedProducer(string AssetId, string Path, string GeneratedPath,
    string CompilerText, string CompilerSha256, EditorRecoveryOpenDocument ProducerDescriptor,
    EditorRecoveryOpenDocument GeneratedDescriptor);
internal sealed record RecoveryUnsavedGeneratedState(Guid ProducerGeneration, Guid GeneratedGeneration,
    string CompiledSource, bool ProducerDirty, bool GeneratedDirty, bool CurrentNativeProducerLease,
    EditorHistorySnapshot History, Guid ProducerEntry, Guid GeneratedEntry);

internal sealed partial class EditorGame
{
    private void RequireUnsavedGeneratedVerification()
    {
        if (!args.Contains("--ui-test") || !args.Contains("--recovery-unsaved-generated-producer"))
            throw new InvalidOperationException("Unsaved generated recovery requires its explicit disposable capability fixture.");
        var current = workspace ?? throw new InvalidOperationException("The native recovery project is not loaded.");
        WorkspaceRecoveryFixtureGuard.Resolve(current.OpenPath, args);
    }

    // Fixture arrangement uses the normal creation operation and native compiler.
    // No disk input/output is fabricated, saved or deleted to simulate a new asset.
    internal RecoveryUnsavedGeneratedProducer PrepareRecoveryUnsavedGeneratedProducer()
    {
        RequireUnsavedGeneratedVerification();
        var current = workspace!;
        var activePackage = current.Package ?? throw new InvalidOperationException("The native project has no active package.");
        var package = current.Session.LocalPackages.Single(candidate => candidate.Container is SolutionProject
            && SamePath(candidate.FullPath.ToOSPath(), activePackage.FullPath.ToOSPath()));
        var project = (SolutionProject)package.Container!;
        var asset = new VisualScriptAsset { Namespace = "EditorParity" };
        var item = new AssetItem(project.Qualify(new UFile("Scripts/RecoveryUnsavedVisualScript")), asset)
        { SourceFolder = package.RootDirectory };
        // AssetItem has no package until actual creation publishes it; use the same
        // native factory path calculation rather than evaluating an unowned item.
        var path = UPath.Combine(package.RootDirectory, new UFile("Scripts/RecoveryUnsavedVisualScript.sdvs")).ToOSPath();
        if (File.Exists(path) || authoring?.Find(path) != null)
            throw new InvalidOperationException("The genuine new producer destination is already occupied.");
        CreatePreparedAsset(package, item, path, "Create recovery visual script");
        var producerState = authoring!.Find(item.Id) ?? throw new InvalidOperationException("Native creation did not publish its producer.");
        if (producerState.Document is not VisualScriptDocument producer || !producer.IsDirty || !producer.DiskContentHash.IsEmpty)
            throw new InvalidOperationException("Native creation must retain an unsaved producer with an empty real disk baseline.");
        documents.Add(producer);
        var generatedPath = item.GetGeneratedAbsolutePath().ToOSPath();
        if (authoring.Find(generatedPath)?.Document is not ScriptSourceDocument generated)
            throw new InvalidOperationException("Native creation did not publish its actual compiler projection.");
        var generatedEntry = documents.Add(generated);
        // Normal Draw synchronizes newly added entries and acknowledges the
        // activation request; the probe then clicks the actual native tab.
        ActivateDocument(generatedEntry);
        var state = CaptureRecoveryWorkspace();
        var compiled = producer.CaptureCompilation(item).Result.GeneratedSource
            ?? throw new InvalidOperationException("The actual native compiler returned no new-producer source.");
        var result = new RecoveryUnsavedGeneratedProducer(item.Id.ToString(), path, generatedPath, compiled,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compiled))),
            state.Documents.Single(document => SamePath(document.Path, path)),
            state.Documents.Single(document => SamePath(document.Path, generatedPath)));
        InspectRecoveryUnsavedGeneratedProducer(result, requireGeneratedView: true);
        return result;
    }

    internal RecoveryUnsavedGeneratedState InspectRecoveryUnsavedGeneratedProducer(RecoveryUnsavedGeneratedProducer expected,
        bool requireGeneratedView)
    {
        RequireUnsavedGeneratedVerification();
        var current = workspace!;
        var id = new AssetId(Guid.Parse(expected.AssetId));
        var item = current.Session.FindAsset(id) ?? throw new InvalidOperationException("The native unsaved producer is absent.");
        var state = authoring?.Find(id) ?? throw new InvalidOperationException("The canonical unsaved producer is absent.");
        if (item.Asset is not VisualScriptAsset || state.Document is not VisualScriptDocument producer
            || !state.IsPublished || state.IsDisposed || !producer.IsDirty || !producer.DiskContentHash.IsEmpty
            || item.Package?.Container is not SolutionProject project || !current.Session.Projects.Contains(project)
            || !SamePath(item.FullPath.ToOSPath(), expected.Path) || !SamePath(item.GetGeneratedAbsolutePath().ToOSPath(), expected.GeneratedPath)
            || item.Package.Assets.Count(candidate => candidate.Id == id) != 1
            || documents.Entries.Count(entry => SamePath(entry.Path, expected.Path)) != 1)
            throw new InvalidOperationException("New-producer recovery lost exact native identity, unsaved baseline or canonical ownership.");
        if (File.Exists(expected.Path) || Directory.Exists(expected.Path) || File.Exists(expected.GeneratedPath) || Directory.Exists(expected.GeneratedPath))
            throw new InvalidOperationException("New-producer recovery must not implicitly write its input or generated output.");
        var compiled = producer.CaptureCompilation(item).Result.GeneratedSource
            ?? throw new InvalidOperationException("Actual restored native compiler returned no source.");
        if (compiled != expected.CompilerText)
            throw new InvalidOperationException("Restored new producer differs from its durable compiler source.");
        var outputState = authoring!.Find(expected.GeneratedPath);
        var output = outputState?.Document as ScriptSourceDocument;
        var outputEntry = documents.Find(expected.GeneratedPath);
        if (requireGeneratedView && (outputState == null || output == null || outputEntry == null
            || !outputState.IsPublished || outputState.IsDisposed || output.CanEditText || output.GeneratedByAssetId != id
            || !output.IsGeneratedOutputOwnedBy(state) || output.Text != compiled || output.ProjectPath == null
            || !SamePath(output.ProjectPath, project.FullPath.ToOSPath())
            || documents.Entries.Count(entry => SamePath(entry.Path, expected.GeneratedPath)) != 1))
            throw new InvalidOperationException("Restored generated view lacks its exact current native producer lease/compiler/project authority.");
        return new(state.Generation, outputState?.Generation ?? Guid.Empty, compiled, producer.IsDirty,
            output?.IsDirty ?? false, output?.IsGeneratedOutputOwnedBy(state) == true,
            producer.History, documents.Find(producer)!.Id, outputEntry?.Id ?? Guid.Empty);
    }

    internal string CaptureRecoveryUnsavedDependencyBoundary(RecoveryUnsavedGeneratedProducer expected)
    {
        RequireUnsavedGeneratedVerification();
        var current = workspace!;
        return JsonSerializer.Serialize(new
        {
            current.OpenPath, history = sessionHistory!.History,
            entries = documents.Entries.Select(entry => new { entry.Id, entry.Path, generation = entry.State?.Generation, entry.Document.IsDirty }),
            groups = CaptureRecoveryWorkspace().Groups,
            nativeProducer = current.Session.FindAsset(new AssetId(Guid.Parse(expected.AssetId)))?.Id.ToString(),
            producerExists = File.Exists(expected.Path), generatedExists = File.Exists(expected.GeneratedPath),
            pending = IsRecoveryWorkspaceRestorePending
        });
    }

    internal string? InspectRecoveryUnsavedDependencyFailure(EditorRecoveryWorkspaceState state)
    { RequireUnsavedGeneratedVerification(); return GetRecoveryWorkspaceDependencyFailure(state); }

    internal RecoveryWorkspaceDiskReceipt[] CaptureRecoveryUnsavedOriginalDisks()
    {
        RequireUnsavedGeneratedVerification();
        var current = workspace!;
        var projects = current.Session.Projects.OfType<SolutionProject>().ToArray();
        if (current.Package?.Container is not SolutionProject activeProject
            || !projects.Any(project => ReferenceEquals(project, activeProject))
            || projects.Any(project => !ReferenceEquals(project.Package.Container, project)
                || !ReferenceEquals(project.Package.Session, current.Session)))
            throw new InvalidOperationException("Original project bytes require actual native solution-project/session ownership.");
        return current.Session.LocalPackages.Select(package => package.FullPath.ToOSPath())
            .Concat(projects.Select(project => project.FullPath.ToOSPath()))
            .Concat(documents.Entries.Select(entry => entry.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists).Select(path => new RecoveryWorkspaceDiskReceipt(path,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
    }
}
