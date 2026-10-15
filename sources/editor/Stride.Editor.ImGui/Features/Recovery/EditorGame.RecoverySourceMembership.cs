using System.Security.Cryptography;
using System.Text;
using Stride.Core.Assets;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed record RecoverySourceMembershipProof(string ProjectPath, string SourcePath, string Configuration,
    string[] Frameworks, string ItemType, string? CanonicalAssetId, long? AuthoredProjectRevision);

internal sealed partial class EditorGame
{
    // Explicit recovery preflight only. The real evaluated item set, rather than folder
    // containment or a .cs suffix, determines membership (including linked sources).
    private RecoverySourceMembershipProof ValidateRecoverySourceMembership(EditorRecoveryOpenDocument descriptor)
    {
        var current = workspace ?? throw new InvalidOperationException("No recovery project is loaded.");
        var history = sessionHistory;
        var path = Path.GetFullPath(descriptor.Path);
        var project = current.Projects.SingleOrDefault(owner => descriptor.SourceProjectPath != null
            && SamePath(owner.FullPath.ToOSPath(), descriptor.SourceProjectPath));
        if (project == null || !current.Session.Projects.Contains(project) || !File.Exists(path))
            throw new InvalidOperationException("The recovered source has no exact loaded project owner.");
        var projectPath = Path.GetFullPath(project.FullPath.ToOSPath());
        var item = current.AssetItems.SingleOrDefault(candidate => SamePath(candidate.FullPath.ToOSPath(), path));
        if (item != null)
        {
            if (item.Asset is not SourceCodeAsset || !ReferenceEquals(item.Package, project.Package)
                || !ReferenceEquals(item.Package.Container, project)
                || descriptor.Kind != typeof(AssetScriptSourceDocument).FullName
                || descriptor.AssetId != item.Id.ToString()
                || descriptor.PackagePath == null || !SamePath(descriptor.PackagePath, item.Package.FullPath.ToOSPath())
                || descriptor.Namespace != (project.AssetNamespace ?? item.Package.AssetNamespace))
                throw new InvalidOperationException("The recovered source is not the same canonical project source asset.");
        }
        else if (descriptor.Kind != typeof(ScriptSourceDocument).FullName || descriptor.AssetId != null
            || descriptor.PackagePath != null || descriptor.Namespace != null)
            throw new InvalidOperationException("The recovered source asset owner is missing.");

        var disk = File.ReadAllBytes(projectPath);
        var authored = authoring?.PublishedStates.Select(state => state.Document).OfType<ProjectBuildSettingsDocument>()
            .SingleOrDefault(owner => SamePath(owner.Path, projectPath));
        var snapshot = authored?.CaptureProjectSnapshot(); // Verifies the native owner's saved disk baseline.
        var selected = ReferenceEquals(buildWorkspace, current) && ReferenceEquals(buildTarget, project) && buildMetadata != null;
        var configuration = selected && buildMetadata!.Configurations.Contains(buildConfiguration) ? buildConfiguration : "Debug";
        var framework = selected && buildMetadata!.Frameworks.Contains(buildFramework) ? buildFramework : "";
        using var evaluated = new AuthoredProjectProperties(snapshot == null ? disk : Encoding.UTF8.GetBytes(snapshot.Xml), projectPath);
        var contexts = evaluated.EvaluateDependencyContexts(configuration);
        if (!string.IsNullOrEmpty(framework)) contexts = contexts.Where(context => context.Framework == framework).ToArray();
        if (contexts.Length == 0) throw new InvalidOperationException("The recovered source project has no current evaluated framework context.");
        var csharp = Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);
        var types = csharp || item == null ? new[] { "Compile" } : new[] { "Compile", "None", "AdditionalFiles" };
        string? itemType = null;
        var matchedFrameworks = new List<string>();
        foreach (var context in contexts)
            foreach (var type in types)
            {
                var paths = type == "Compile" ? context.CompilePaths : evaluated.GetProjectItemPaths(type, configuration, context.Framework);
                if (!paths.Any(candidate => SamePath(candidate, path))) continue;
                itemType ??= type;
                if (!matchedFrameworks.Contains(context.Framework)) matchedFrameworks.Add(context.Framework);
            }
        if (itemType == null)
            throw new InvalidOperationException("The recovered source is excluded from the actual evaluated project item set.");
        if (!ReferenceEquals(workspace, current) || !ReferenceEquals(sessionHistory, history)
            || !current.Session.Projects.Contains(project)
            || snapshot != null && authored!.AuthoringRevision != snapshot.Revision
            || !SHA256.HashData(disk).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(projectPath))))
            throw new InvalidOperationException("The recovery project membership authority changed during evaluation.");
        return new(projectPath, path, configuration, matchedFrameworks.ToArray(), itemType, item?.Id.ToString(), snapshot?.Revision);
    }
}
