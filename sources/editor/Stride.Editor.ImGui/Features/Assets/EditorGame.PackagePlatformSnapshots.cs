using System.Text;
using Stride.Assets.Templates;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class PlatformUpdateCapture(ProjectWorkspace workspace, EditorSessionHistory history, PlatformUpdatePlan plan,
        PackagePlatformUpdateSnapshot snapshot, Dictionary<EditorAuthoringState, (long Revision, IDisposable Lease)> owners)
    {
        internal readonly ProjectWorkspace Workspace = workspace;
        internal readonly EditorSessionHistory History = history;
        internal readonly PlatformUpdatePlan Plan = plan;
        internal readonly PackagePlatformUpdateSnapshot Snapshot = snapshot;
        internal bool Current(EditorGame game) => ReferenceEquals(game.workspace, Workspace) && ReferenceEquals(game.sessionHistory, History)
            && Plan.IsCurrent(Plan.GameLibrary) && owners.All(pair => pair.Key.IsPublished && !pair.Key.IsDisposed
                && ReferenceEquals(History.Registry.Find(pair.Key.Document.Path), pair.Key)
                && PlatformUpdateRevision(pair.Key.Document) == pair.Value.Revision);
        internal void ValidateNativeProjectBaselines()
        { foreach (var state in owners.Keys) if (state.Document is ProjectBuildSettingsDocument project) project.ValidateProjectFileBaseline(); }
        internal void Release()
        { foreach (var pair in owners.ToArray()) { pair.Value.Lease.Dispose(); owners.Remove(pair.Key); } }
    }
    private static long PlatformUpdateRevision(IEditorDocument document) => document is ScriptSourceDocument source
        ? source.Version : ProjectRemovalDocumentRevision(document);
    private PlatformUpdateCapture CapturePackagePlatformUpdate(Package selected, IReadOnlyList<PlatformType> platforms, bool regenerate)
    {
        if (workspace is not { } current || SessionAuthoringHistory is not { } history || !CanStartAssetEdit
            || !IsCurrentEditableAssetPackage(selected) || selected.Container is not SolutionProject)
            throw new InvalidOperationException("The platform update package is no longer available.");
        ValidatePendingPropertyStrings(); FinishEntityRename(true); documents.CommitAll();
        var log = new LoggerResult();
        var parameters = new PackageTemplateGeneratorParameters
        {
            Package = selected, Name = selected.Meta.Name, Description = UpdatePlatformsGenerator.CreateDescription(),
            Logger = log, OutputDirectory = selected.FullPath.GetFullDirectory(), Unattended = true,
        };
        UpdatePlatformsGenerator.SetPlatforms(parameters, platforms, regenerate);
        var plan = UpdatePlatformsGenerator.CreatePlan(parameters) ?? throw new InvalidOperationException(log.ToText());
        if (platforms.Count == 0) throw new InvalidOperationException(EditorLocalization.Text("You must select at least one platform.", "Message"));
        var root = Path.GetFullPath(plan.SessionDirectory.ToOSPath());
        var projects = current.Session.Projects.OfType<SolutionProject>().Where(project => !project.Package.IsReadOnly).ToArray();
        var inputs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<EditorAuthoringState, (long, IDisposable)>();
        void Retain(EditorAuthoringState state)
        { if (!owners.ContainsKey(state)) owners.Add(state, (PlatformUpdateRevision(state.Document), state.AcquireHistory())); }
        try
        {
            foreach (var project in projects)
            {
                var package = history.Registry.GetOrCreate(project.Package.FullPath.ToOSPath(), null,
                    () => new PackageMetadataDocument(current, project.Package, history)); Retain(package);
                using (var stream = new MemoryStream())
                { AssetYamlSerializer.Default.Serialize(stream, project.Package); inputs.Add(project.Package.FullPath.ToOSPath(), stream.ToArray()); }
                var owner = GetProjectBuildSettingsDocument(project, history);
                var state = history.Registry.Find(owner.Path)!; Retain(state);
                inputs[owner.Path] = owner.CapturePlatformUpdateProject();
            }
            foreach (var sourceState in history.Registry.PublishedStates.Where(state => state.Document is ScriptSourceDocument or ProjectBuildSettingsDocument).ToArray()) Retain(sourceState);
            foreach (var state in history.Registry.PublishedStates.Where(state => state.Document.IsDirty && (NativeMigrationMirror.Inside(root, state.Document.Path)
                || projects.Any(project => NativeMigrationMirror.Inside(project.Package.RootDirectory.ToOSPath(), state.Document.Path)))).ToArray())
            {
                Retain(state);
                if (state.Document is ScriptSourceDocument source) { inputs[source.Path] = source.CapturePlatformUpdateSource(); continue; }
                if (state.AssetId is not { } id || current.Session.FindAsset(id) is not { } item) continue;
                byte[] bytes;
                switch (state.Document)
                {
                    case EntityHierarchyDocument hierarchy: bytes = hierarchy.CaptureAssemblyReload().Yaml; break;
                    case ProceduralAssetDocument procedural: bytes = procedural.CaptureAssemblyReload(item).Yaml; break;
                    case AssetMetadataDocument asset: bytes = asset.CaptureAssemblyReload(item).Yaml; break;
                    default: throw new InvalidOperationException("This authored platform input cannot be staged.");
                }
                inputs[item.FullPath.ToOSPath()] = bytes;
            }
            // Commit snapshots may finish pending native transactions; certify their
            // resulting revisions, rather than keeping pre-commit draft revisions.
            foreach (var state in owners.Keys.ToArray()) owners[state] = (PlatformUpdateRevision(state.Document), owners[state].Item2);
            var removedWithoutReplacement = plan.ProjectsToRemove.Where(removal => removal.Project != null && !plan.PlatformsToInstantiate.Contains(removal.Platform))
                .Select(removal => removal.Project!.Package).ToArray();
            var removingDependencies = projects.SelectMany(project => project.DirectDependencies.Where(dependency =>
                removedWithoutReplacement.Any(package => PackageDependencies.RefersTo(package, dependency))).Select(dependency =>
                new PlatformDependencyInput(project.FullPath.ToOSPath(), dependency.Name, dependency.VersionRange?.ToString(), dependency.Type, dependency.MSBuildProject))).ToArray();
            var snapshot = new PackagePlatformUpdateSnapshot(plan.GameLibrary.FullPath.ToOSPath(), root, selected.FullPath.ToOSPath(), selected.Meta.Name,
                projects.Select(project => new PlatformProjectInput(project.Id, project.FullPath.ToOSPath(), project.Package.FullPath.ToOSPath(), project.Type,
                    project.Platform, project.IsImplicitProject)).ToArray(), inputs.Select(input => new PlatformAuthoredInput(input.Key, input.Value)).ToArray(), removingDependencies, platforms.ToArray(), regenerate, plan.ProjectsToRemove.Where(removal => removal.Project != null).Select(removal => removal.Project!.Id).ToArray(),
                plan.PlatformsToInstantiate.ToArray());
            var capture = new PlatformUpdateCapture(current, history, plan, snapshot, owners);
            if (!capture.Current(this)) throw new InvalidOperationException("The package changed while capturing platform inputs.");
            return capture;
        }
        catch
        {
            void Release() { foreach (var pair in owners.ToArray()) { pair.Value.Item2.Dispose(); owners.Remove(pair.Key); } }
            try { Release(); } catch { if (!packageCommandCleanup.Contains(Release)) packageCommandCleanup.Add(Release); }
            throw;
        }
    }
}
