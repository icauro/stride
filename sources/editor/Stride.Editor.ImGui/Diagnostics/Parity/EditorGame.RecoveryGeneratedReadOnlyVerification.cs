using System.Security.Cryptography;
using System.Text.Json;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record RecoveryGeneratedReadOnlySourcesReceipt(string ProducerAssetId, string ProducerPath,
    string ProducerSha256, string GeneratedPath, string GeneratedSha256, string GeneratedText,
    string ReadOnlyPath, string ReadOnlySha256, string ReadOnlyText,
    EditorRecoveryOpenDocument ProducerDescriptor, EditorRecoveryOpenDocument GeneratedDescriptor,
    EditorRecoveryOpenDocument ReadOnlyDescriptor, bool ActualCompilerOwned, bool ReadOnlyVerified,
    string ExternalReadOnlyPath, string ExternalReadOnlySha256, string ExternalReadOnlyText,
    EditorRecoveryOpenDocument ExternalReadOnlyDescriptor);

internal sealed partial class EditorGame
{
    // Arrangement only: a native serialized visual-script input. No authored C# output is fabricated.
    internal void PrepareRecoveryGeneratedVisualScriptFixture()
    {
        if (!args.Contains("--ui-test") || !args.Contains("--recovery-generated-readonly-sources") || workspace != null)
            throw new InvalidOperationException("Generated recovery preparation requires its explicit unloaded disposable fixture.");
        var packagePath = WorkspaceRecoveryFixtureGuard.Resolve(projectPathInput, args);
        var path = Path.Combine(Path.GetDirectoryName(packagePath)!, "Assets", "RecoveryVisualScript.sdvs");
        if (File.Exists(path)) throw new IOException("The independently owned generated recovery producer path already exists.");
        var input = new VisualScriptAsset { Namespace = "EditorParity" };
        AssetFileSerializer.Save(path, input, null, assetNamespace: "EditorParity");
        var orphan = Path.Combine(Path.GetDirectoryName(packagePath)!, "Scripts", "RecoveryOrphan.sdvs.cs");
        if (File.Exists(orphan) || File.Exists(Path.ChangeExtension(orphan, null)))
            throw new IOException("The independently owned ordinary suffix fixture must have no source or paired native generator.");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllText(orphan, "namespace EditorParity; internal sealed class RecoveryOrdinarySuffixSource { }\n", new System.Text.UTF8Encoding(false));
    }

    // Service fixture construction is not passing proof of UI authoring. Subsequent workspace
    // Restore, native source focus and attempted typing are the capability assertions.
    internal RecoveryGeneratedReadOnlySourcesReceipt PrepareRecoveryGeneratedReadOnlySources()
    {
        RequireRecoveryGeneratedReadOnlyHost();
        var currentWorkspace = workspace ?? throw new InvalidOperationException("The generated recovery project is no longer loaded.");
        var previous = documents.Active;
        var packageRoot = Path.GetDirectoryName(currentWorkspace.OpenPath)!;
        var producerPath = Path.Combine(packageRoot, "Assets", "RecoveryVisualScript.sdvs");
        var item = currentWorkspace.AssetItems.Single(candidate => SamePath(candidate.FullPath.ToOSPath(), producerPath));
        var producerPackage = item.Package ?? throw new InvalidOperationException("The native generated producer has no owning package.");
        if (item.Asset is not VisualScriptAsset || producerPackage.Container is not SolutionProject project)
            throw new InvalidOperationException("The actual generated recovery producer is not a native visual-script project asset.");
        var producer = (VisualScriptDocument)GetCanonicalDocument(producerPath,
            () => new VisualScriptDocument(producerPath, sessionHistory!, producerPackage.IsReadOnly));
        var producerEntry = documents.Find(producer) ?? documents.Add(producer);
        InitializeVisualScriptDocument(producer);
        producer.Save(); // Real native compiler, canonical producer/lease, real generated file save.
        var generatedPath = item.GetGeneratedAbsolutePath().ToOSPath();
        if (authoring!.Find(generatedPath)?.Document is not ScriptSourceDocument generated
            || generated.GeneratedByAssetId != item.Id || generated.CanEditText || generated.IsDirty
            || !SamePath(generated.ProjectPath!, project.FullPath.ToOSPath()))
            throw new InvalidOperationException("Native visual-script save did not publish its genuine clean compiler-owned source.");
        VerifyVisualScriptGeneratedOutput(generated);
        documents.Add(generated);
        using var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(packageRoot, ".workspace-recovery-fixture.json")));
        var readOnlyPath = marker.RootElement.GetProperty("readOnlySourcePath").GetString()!;
        if ((File.GetAttributes(readOnlyPath) & FileAttributes.ReadOnly) == 0)
            throw new InvalidOperationException("The actual read-only source fixture file must already have its native disk attribute.");
        OpenScriptDocument(readOnlyPath);
        var readOnlyEntry = documents.Find(readOnlyPath)!;
        var readOnly = readOnlyEntry.Document as ScriptSourceDocument ?? throw new InvalidOperationException("Read-only source did not open its actual native document.");
        if (readOnly.CanEditText || readOnly.CanSave || readOnly.IsDirty || readOnly.GeneratedByAssetId != null
            || !SamePath(readOnly.ProjectPath!, project.FullPath.ToOSPath()))
            throw new InvalidOperationException("The actual read-only project source has incorrect native authority.");
        var externalPath = marker.RootElement.GetProperty("externalReadOnlySourcePath").GetString()!;
        var externalRequested = new EditorRecoveryOpenDocument(externalPath, typeof(ScriptSourceDocument).FullName!,
            null, null, null, null, null, ReadOnlyPresentation: true);
        // Arrangement uses the actual supported external read-only presentation route.
        // The passing proof is later workspace recovery and focused native typing.
        var externalEntry = OpenRecoverySourcePresentation(externalRequested);
        var external = externalEntry.Document as ScriptSourceDocument
            ?? throw new InvalidOperationException("Actual external read-only presentation did not acquire its canonical native source.");
        var state = CaptureRecoveryWorkspace();
        var result = new RecoveryGeneratedReadOnlySourcesReceipt(item.Id.ToString(), producerPath, RecoveryProtectedHash(producerPath),
            generatedPath, RecoveryProtectedHash(generatedPath), generated.Text, readOnlyPath, RecoveryProtectedHash(readOnlyPath), readOnly.Text,
            state.Documents.Single(document => SamePath(document.Path, producerPath)),
            state.Documents.Single(document => SamePath(document.Path, generatedPath)),
            state.Documents.Single(document => SamePath(document.Path, readOnlyPath)), true, true,
            externalPath, RecoveryProtectedHash(externalPath), external.Text,
            state.Documents.Single(document => SamePath(document.Path, externalPath)));
        VerifyRecoveryGeneratedReadOnlySources(result);
        if (previous != null) { ActivateDocument(previous); DocumentGroupPanel.Model.Select(previous); }
        return result;
    }

    internal object VerifyRecoveryGeneratedReadOnlySources(RecoveryGeneratedReadOnlySourcesReceipt expected)
    {
        RequireRecoveryGeneratedReadOnlyHost();
        var currentWorkspace = workspace ?? throw new InvalidOperationException("The protected recovery project is no longer loaded.");
        var item = currentWorkspace.Session.FindAsset(new AssetId(Guid.Parse(expected.ProducerAssetId)))
            ?? throw new InvalidOperationException("The restored generated producer asset is absent from the actual native session.");
        var producerPackage = item.Package ?? throw new InvalidOperationException("The restored generated producer has no native owning package.");
        if (item.Asset is not VisualScriptAsset || !SamePath(item.FullPath.ToOSPath(), expected.ProducerPath)
            || producerPackage.Container is not SolutionProject project || !currentWorkspace.Session.Projects.Contains(project)
            || !SamePath(item.GetGeneratedAbsolutePath().ToOSPath(), expected.GeneratedPath))
            throw new InvalidOperationException("Restored generated presentation lost the actual native producer/project/path identity.");
        var producerState = authoring!.Find(item.Id);
        var generatedState = authoring.Find(expected.GeneratedPath);
        var readOnlyState = authoring.Find(expected.ReadOnlyPath);
        if (producerState?.Document is not VisualScriptDocument producer || producer.IsDirty
            || generatedState?.Document is not ScriptSourceDocument generated || generated.GeneratedByAssetId != item.Id
            || generated.CanEditText || generated.IsDirty || generated.Text != expected.GeneratedText
            || readOnlyState?.Document is not ScriptSourceDocument readOnly || readOnly.CanEditText || readOnly.CanSave
            || readOnly.GeneratedByAssetId != null || readOnly.IsDirty || readOnly.Text != expected.ReadOnlyText
            || !ReferenceEquals(generated.SessionHistory, sessionHistory) || !ReferenceEquals(readOnly.SessionHistory, sessionHistory)
            || !generated.IsGeneratedOutputOwnedBy(producerState)
            || !SamePath(generated.ProjectPath!, project.FullPath.ToOSPath()) || !SamePath(readOnly.ProjectPath!, project.FullPath.ToOSPath())
            || !generatedState.IsPublished || generatedState.IsDisposed || !readOnlyState.IsPublished || readOnlyState.IsDisposed)
            throw new InvalidOperationException("Restored protected source capabilities, canonical ownership, compiler identity or clean text changed.");
        VerifyVisualScriptGeneratedOutput(generated); // Real current native compilation validates output without writing.
        if (RecoveryProtectedHash(expected.ProducerPath) != expected.ProducerSha256
            || RecoveryProtectedHash(expected.GeneratedPath) != expected.GeneratedSha256
            || RecoveryProtectedHash(expected.ReadOnlyPath) != expected.ReadOnlySha256
            || (File.GetAttributes(expected.ReadOnlyPath) & FileAttributes.ReadOnly) == 0)
            throw new InvalidOperationException("Workspace-only protected source recovery changed producer/output/read-only file bytes or attributes.");
        var state = CaptureRecoveryWorkspace();
        foreach (var descriptor in new[] { expected.ProducerDescriptor, expected.GeneratedDescriptor, expected.ReadOnlyDescriptor })
            if (state.Documents.Single(document => SamePath(document.Path, descriptor.Path)) != descriptor)
                throw new InvalidOperationException("Protected recovery source presentation descriptor changed.");
        var classification = VerifyRecoveryNativeGeneratedClassification(currentWorkspace, producerPackage, project, expected.GeneratedPath);
        var externalReadOnly = VerifyRecoveryExternalReadOnlySource(currentWorkspace, expected);
        return new { nativeProducerAssetId = item.Id.ToString(), nativeProject = project.FullPath.ToOSPath(), actualGeneratedPath = generated.Path,
            generatedProducerGeneration = producerState.Generation, generatedGeneration = generatedState.Generation,
            generatedCanSave = generated.CanSave, generatedCanEditText = generated.CanEditText,
            actualNativeProducerLease = true,
            readOnlyGeneration = readOnlyState.Generation, readOnlyCanSave = readOnly.CanSave, readOnlyCanEditText = readOnly.CanEditText,
            actualCompilerVerified = true, originalFilesUnchanged = true, actualReadOnlyAttribute = true, classification, externalReadOnly };
    }
    private static object VerifyRecoveryNativeGeneratedClassification(ProjectWorkspace current, Package package, SolutionProject project, string generatedPath)
    {
        var projectPath = project.FullPath.ToOSPath();
        var orphan = Path.Combine(Path.GetDirectoryName(projectPath)!, "Scripts", "RecoveryOrphan.sdvs.cs");
        if (File.Exists(Path.ChangeExtension(orphan, null)) || !File.Exists(orphan))
            throw new InvalidOperationException("The ordinary .sdvs.cs fixture must exist without a paired registered generator input.");
        if (current.AssetItems.Any(candidate => SamePath(candidate.FullPath.ToOSPath(), generatedPath))
            || package.Assets.Any(candidate => SamePath(candidate.FullPath.ToOSPath(), generatedPath)))
            throw new InvalidOperationException("A real registered generated output must not also become an ordinary authored source asset.");
        var ordinary = current.AssetItems.SingleOrDefault(candidate => SamePath(candidate.FullPath.ToOSPath(), orphan));
        if (ordinary?.Asset is not SourceCodeAsset || !ReferenceEquals(ordinary.Package, package))
            throw new InvalidOperationException("An ordinary C# file with the generated suffix but no real producer must remain an actual native source asset.");
        using var evaluated = new AuthoredProjectProperties(File.ReadAllBytes(projectPath), projectPath);
        var contexts = evaluated.EvaluateDependencyContexts("Debug");
        if (contexts.Length == 0 || contexts.Any(context => !context.CompilePaths.Any(path => SamePath(path, generatedPath))
            || !context.CompilePaths.Any(path => SamePath(path, orphan))))
            throw new InvalidOperationException("Both genuine generated output and ordinary suffix source must remain real evaluated native Compile inputs.");
        return new { generatedOutputIsOrdinaryAsset = false, generatedOutputRemainsActualCompile = true,
            ordinarySuffixPath = orphan, ordinarySourceAssetId = ordinary.Id.ToString(), ordinarySuffixIsActualSourceAsset = true,
            ordinarySuffixRemainsActualCompile = true, pairedGeneratorInputAbsent = true,
            frameworks = contexts.Select(context => context.Framework).ToArray() };
    }
    private object VerifyRecoveryExternalReadOnlySource(ProjectWorkspace current, RecoveryGeneratedReadOnlySourcesReceipt expected)
    {
        var path = expected.ExternalReadOnlyPath;
        var state = authoring?.Find(path);
        var entry = documents.Find(path);
        if (state?.Document is not ScriptSourceDocument source || entry == null || !ReferenceEquals(entry.Document, source)
            || !ReferenceEquals(entry.State, state) || !state.IsPublished || state.IsDisposed || state.AssetId != null
            || source.ProjectPath != null || source.GeneratedByAssetId != null || source.CanEditText || source.CanSave || source.IsDirty
            || source.Text != expected.ExternalReadOnlyText || !ReferenceEquals(source.SessionHistory, sessionHistory)
            || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 || RecoveryProtectedHash(path) != expected.ExternalReadOnlySha256)
            throw new InvalidOperationException("External writable-disk source must remain the exact canonical explicitly read-only view without project/asset/generator authority.");
        if (current.AssetItems.Any(item => SamePath(item.FullPath.ToOSPath(), path)))
            throw new InvalidOperationException("External read-only view must not be claimed by a native authored source asset.");
        var projects = new List<object>();
        foreach (var project in current.Projects)
        {
            var projectPath = project.FullPath.ToOSPath();
            var root = Path.GetDirectoryName(Path.GetFullPath(projectPath))! + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("External read-only fixture must remain physically outside every loaded native project directory.");
            using var evaluated = new AuthoredProjectProperties(File.ReadAllBytes(projectPath), projectPath);
            var contexts = evaluated.EvaluateDependencyContexts("Debug");
            if (contexts.Length == 0 || contexts.Any(context => context.CompilePaths.Any(item => SamePath(item, path))))
                throw new InvalidOperationException("External read-only viewer must be absent from every actual native evaluated project Compile set.");
            projects.Add(new { projectPath, frameworks = contexts.Select(context => context.Framework).ToArray(), compileMember = false });
        }
        var descriptor = CaptureRecoveryWorkspace().Documents.Single(document => SamePath(document.Path, path));
        if (descriptor != expected.ExternalReadOnlyDescriptor || descriptor.SourceProjectPath != null || descriptor.AssetId != null
            || descriptor.PackagePath != null || descriptor.Namespace != null || descriptor.GeneratedByAssetId != null || !descriptor.ReadOnlyPresentation)
            throw new InvalidOperationException("External read-only recovery descriptor acquired an invented native project or asset identity.");
        return new { source.Path, canonicalGeneration = state.Generation, source.CanSave, source.CanEditText,
            writableOnDisk = true, exactTextUnchanged = true, originalBytesUnchanged = true,
            nativeAssetId = state.AssetId, source.ProjectPath, projects };
    }
    private void RequireRecoveryGeneratedReadOnlyHost()
    {
        if (!args.Contains("--ui-test") || !args.Contains("--recovery-generated-readonly-sources")
            || workspace == null || sessionHistory == null || projectLoading != null)
            throw new InvalidOperationException("Protected recovery source verification requires the loaded exact private variant.");
        _ = WorkspaceRecoveryFixtureGuard.Resolve(workspace.OpenPath, args);
    }
    private static string RecoveryProtectedHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
