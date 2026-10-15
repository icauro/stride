using System.Text;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record ReadonlySourceProjectOrigin(ProjectWorkspace Workspace, IReadOnlyList<SolutionProject> Projects, string SourcePath, string? ProjectPath);
    private readonly ConditionalWeakTable<ScriptSourceDocument, ReadonlySourceProjectOrigin> readonlySourceProjectOrigins = new();
    private RecoverySourceMembershipProof ValidateRecoveryGeneratedSourceMembership(EditorRecoveryOpenDocument descriptor)
    {
        var current = workspace ?? throw new InvalidOperationException("Generated source recovery requires a loaded workspace.");
        var history = sessionHistory;
        if (descriptor.Kind != typeof(ScriptSourceDocument).FullName || descriptor.AssetId != null || !descriptor.ReadOnlyPresentation
            || !Guid.TryParse(descriptor.GeneratedByAssetId, out var producerGuid))
            throw new InvalidOperationException("Generated source recovery requires its real producer and read-only source presentation.");
        var id = new AssetId(producerGuid);
        var item = current.Session.FindAsset(id);
        if (item == null || item.Asset is not VisualScriptAsset || item.Package is not { } package || package.Container is not SolutionProject project
            || !current.Session.Projects.Contains(project) || !ReferenceEquals(project.Package, package)
            || descriptor.SourceProjectPath == null || !SamePath(descriptor.SourceProjectPath, project.FullPath.ToOSPath())
            || descriptor.PackagePath == null || !SamePath(descriptor.PackagePath, package.FullPath.ToOSPath())
            || descriptor.Namespace != (project.AssetNamespace ?? package.AssetNamespace)
            || !SamePath(descriptor.Path, item.GetGeneratedAbsolutePath().ToOSPath()))
            throw new InvalidOperationException("Generated source recovery has another canonical visual-script producer, project or output path.");
        var materialized = File.Exists(descriptor.Path);
        if (Directory.Exists(descriptor.Path)) throw new InvalidOperationException("The generated output path is occupied by a directory.");
        var projectPath = project.FullPath.ToOSPath();
        var nativeAsset = item.Asset;
        var projectDisk = File.ReadAllBytes(projectPath);
        var authored = authoring?.PublishedStates.Select(state => state.Document).OfType<ProjectBuildSettingsDocument>()
            .SingleOrDefault(owner => SamePath(owner.Path, projectPath));
        var snapshot = authored?.CaptureProjectSnapshot();
        using var evaluated = new AuthoredProjectProperties(snapshot == null ? projectDisk : Encoding.UTF8.GetBytes(snapshot.Xml), projectPath);
        var selected = ReferenceEquals(buildWorkspace, current) && ReferenceEquals(buildTarget, project) && buildMetadata != null;
        var configuration = selected && buildMetadata!.Configurations.Contains(buildConfiguration) ? buildConfiguration : "Debug";
        var contexts = evaluated.EvaluateDependencyContexts(configuration);
        var frameworks = contexts.Where(context => !materialized || context.CompilePaths.Any(path => SamePath(path, descriptor.Path)))
            .Select(context => context.Framework).ToArray();
        if (frameworks.Length == 0) throw new InvalidOperationException("The producer's actual generated output is not an evaluated Compile item.");
        // Verify real generator metadata, rather than adopting an arbitrary sibling C# file.
        using (var collection = new Microsoft.Build.Evaluation.ProjectCollection())
        {
            using var diskStream = new MemoryStream(projectDisk, false);
            using var reader = snapshot == null ? System.Xml.XmlReader.Create(diskStream)
                : System.Xml.XmlReader.Create(new StringReader(snapshot.Xml));
            var root = Microsoft.Build.Construction.ProjectRootElement.Create(reader, collection);
            root.FullPath = projectPath;
            foreach (var framework in frameworks)
            {
                var properties = new Dictionary<string, string> { ["Configuration"] = configuration };
                if (!string.IsNullOrEmpty(framework)) properties.Add("TargetFramework", framework);
                var native = new Microsoft.Build.Evaluation.Project(root, properties, null, collection);
                try
                {
                    var outputs = native.GetItems("Compile").Where(output => SamePath(
                        Path.GetFullPath(output.EvaluatedInclude, Path.GetDirectoryName(projectPath)!), descriptor.Path)).ToArray();
                    var defaultCompile = string.Equals(native.GetPropertyValue("EnableDefaultCompileItems"), "true", StringComparison.OrdinalIgnoreCase);
                    bool BooleanMetadata(Microsoft.Build.Evaluation.ProjectItem output, string name)
                    {
                        var value = output.GetMetadataValue(name);
                        return defaultCompile && value.Length == 0 || bool.TryParse(value, out var enabled) && enabled;
                    }
                    bool TextMetadata(Microsoft.Build.Evaluation.ProjectItem output, string name, string expected)
                    {
                        var value = output.GetMetadataValue(name);
                        return defaultCompile && value.Length == 0 || value == expected;
                    }
                    // PackageSession deliberately omits generator metadata with SDK
                    // default Compile items. Missing metadata is valid in that mode;
                    // conflicting metadata is never adopted as producer authority.
                    var producers = native.GetItems("None").Concat(native.GetItems("AdditionalFiles"))
                        .Where(input => SamePath(Path.GetFullPath(input.EvaluatedInclude, Path.GetDirectoryName(projectPath)!), item.FullPath.ToOSPath())).ToArray();
                    var generator = ((IProjectFileGeneratorAsset)item.Asset).Generator;
                    if ((materialized || !defaultCompile) && outputs.Length == 0 || outputs.Any(output => !BooleanMetadata(output, "AutoGen")
                            || !BooleanMetadata(output, "DesignTime") || !BooleanMetadata(output, "DesignTimeSharedInput")
                            || !TextMetadata(output, "DependentUpon", Path.GetFileName(item.FullPath.ToOSPath())))
                        || (!defaultCompile || !materialized) && producers.Length == 0
                        || producers.Any(input => !TextMetadata(input, "Generator", generator)
                            || !TextMetadata(input, "LastGenOutput", Path.GetFileName(descriptor.Path))))
                        throw new InvalidOperationException("The generated source no longer has its native project generator metadata.");
                }
                finally { collection.UnloadProject(native); }
            }
        }
        if (!ReferenceEquals(workspace, current) || !ReferenceEquals(sessionHistory, history)
            || !ReferenceEquals(current.Session.FindAsset(id), item) || !ReferenceEquals(item.Asset, nativeAsset)
            || !ReferenceEquals(item.Package, package) || !ReferenceEquals(project.Package, package)
            || !current.Session.Projects.Contains(project) || !SamePath(project.FullPath.ToOSPath(), projectPath)
            || !SamePath(item.GetGeneratedAbsolutePath().ToOSPath(), descriptor.Path)
            || snapshot != null && authored!.AuthoringRevision != snapshot.Revision
            || !SHA256.HashData(projectDisk).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(projectPath))))
            throw new InvalidOperationException("Generated source recovery authority changed during native project evaluation.");
        return new(projectPath, Path.GetFullPath(descriptor.Path), configuration, frameworks,
            materialized ? "Compile.Generated" : "Generator.Projection", null, snapshot?.Revision);
    }

    private (AssetItem Item, EditorAuthoringState Producer) ResolveRecoveryGeneratedSourceProducer(EditorRecoveryOpenDocument descriptor)
    {
        _ = ValidateRecoveryGeneratedSourceMembership(descriptor);
        var current = workspace ?? throw new InvalidOperationException("Generated source workspace owner changed.");
        var registry = authoring ?? throw new InvalidOperationException("Generated source authoring owner is unavailable.");
        var history = sessionHistory ?? throw new InvalidOperationException("Generated source history owner is unavailable.");
        var item = current.Session.FindAsset(new AssetId(Guid.Parse(descriptor.GeneratedByAssetId!)))
            ?? throw new InvalidOperationException("The generated source producer is no longer loaded.");
        var package = item.Package ?? throw new InvalidOperationException("The generated source producer has no native package owner.");
        if (package.Container is not SolutionProject project || !current.Session.Projects.Contains(project))
            throw new InvalidOperationException("The generated source producer has no loaded native project owner.");
        var previous = registry.Find(item.Id);
        try
        {
            var document = GetCanonicalDocument(item.FullPath.ToOSPath(), () => new VisualScriptDocument(item.FullPath.ToOSPath(), history, package.IsReadOnly));
            if (document is not VisualScriptDocument visual || registry.Find(item.Id) is not { } producer
                || !ReferenceEquals(producer.Document, visual) || !producer.IsPublished || producer.IsDisposed
                || visual.Asset.Id != item.Id || !ReferenceEquals(workspace, current) || !ReferenceEquals(sessionHistory, history))
                throw new InvalidOperationException("The generated source has no current canonical visual-script producer generation.");
            InitializeVisualScriptDocument(visual); // Installs existing native services; does not generate, save or edit.
            var owner = GetVisualScriptCompilerOwner(visual);
            if (!ReferenceEquals(owner.Item, item) || !ReferenceEquals(owner.Producer, producer) || !ReferenceEquals(item.Package, package)
                || !current.Session.Projects.Contains(project)
                || !SamePath(owner.Path, descriptor.Path) || !SamePath(owner.Project, descriptor.SourceProjectPath!))
                throw new InvalidOperationException("The native compiler producer no longer owns the generated source path.");
            return (item, producer);
        }
        catch
        {
            if (previous == null && registry.Find(item.Id) is { } unused) registry.ReleaseIfUnused(unused);
            throw;
        }
    }

    private string ResolveRecoveryReadonlySourceProject(ScriptSourceDocument source)
        => TryResolveRecoveryReadonlySourceProject(source)
            ?? throw new InvalidOperationException("Read-only source recovery requires one actual evaluated loaded project owner.");

    private string? TryResolveRecoveryReadonlySourceProject(ScriptSourceDocument source)
    {
        if (source.ProjectPath != null) return source.ProjectPath;
        if (source.CanEditText || source.GeneratedByAssetId != null || !source.IsCSharpSource)
            return null;
        var current = workspace;
        if (current == null) return null;
        if (readonlySourceProjectOrigins.TryGetValue(source, out var origin)
            && ReferenceEquals(origin.Workspace, current) && ReferenceEquals(origin.Projects, current.Projects)
            && SamePath(origin.SourcePath, source.Path)) return origin.ProjectPath;
        var matches = new List<string>();
        var authorityComplete = true;
        foreach (var project in current.Projects.Where(project => current.Session.Projects.Contains(project)))
        {
            var path = project.FullPath.ToOSPath();
            if (!File.Exists(path)) continue;
            var authored = authoring?.PublishedStates.Select(state => state.Document).OfType<ProjectBuildSettingsDocument>()
                .SingleOrDefault(owner => SamePath(owner.Path, path));
            AuthoredProjectSnapshot? snapshot;
            try { snapshot = authored?.CaptureProjectSnapshot(); }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // This optional origin lookup may encounter a noneditable or externally
                // changed project baseline. It must not prevent reading the source file.
                authorityComplete = false; break;
            }
            try
            {
                using var evaluated = new AuthoredProjectProperties(snapshot == null ? File.ReadAllBytes(path) : Encoding.UTF8.GetBytes(snapshot.Xml), path);
                if (evaluated.EvaluateDependencyContexts("Debug").Any(context => context.CompilePaths.Any(candidate => SamePath(candidate, source.Path))))
                    matches.Add(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or Microsoft.Build.Exceptions.InvalidProjectFileException or System.Xml.XmlException or ArgumentException)
            {
                // Known project input/evaluation failures leave the read-only viewer
                // unbound. An unknown project could contain another matching source,
                // so never infer uniqueness from the remaining successful projects.
                authorityComplete = false; break;
            }
        }
        var result = authorityComplete && matches.Count == 1 ? matches[0] : null;
        readonlySourceProjectOrigins.Remove(source);
        readonlySourceProjectOrigins.Add(source, new(current, current.Projects, source.Path, result));
        return result;
    }
}
