using System.Text.Json;
using System.Xml.Linq;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;

namespace Stride.Editor.ImGui;

// Actual authored project/asset service integration; UI input is tested separately.
internal static class ProjectCapabilityScenarios
{
    public static IReadOnlyList<string> Cases { get; } = ["C07", "C08"];

    public static int Run(string scenarioId, string packagePath, string artifactsDirectory)
    {
        string? fixture = null;
        var passed = false;
        Exception? failure = null;
        try
        {
            Directory.CreateDirectory(artifactsDirectory);
            File.WriteAllText(Path.Combine(artifactsDirectory, "result.json"), JsonSerializer.Serialize(new { scenarioId, passed = false, phase = "preparing" }));
            if (!Cases.Contains(scenarioId)) throw new ArgumentException("Unknown project capability scenario", nameof(scenarioId));
            var copiedPackage = UiWorkflowTest.CopyFixture(packagePath);
            fixture = Path.GetDirectoryName(copiedPackage)!;
            if (scenarioId == "C07") ProjectMetadata(fixture);
            else AssetIdentity(copiedPackage);
            var ownedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
            var resolvedFixture = Path.GetFullPath(fixture);
            if (!resolvedFixture.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(resolvedFixture), "N", out _))
                throw new InvalidOperationException("Refuse cleanup outside the generated test fixture");
            Directory.Delete(resolvedFixture, true);
            fixture = null;
            passed = true;
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        finally
        {
            try
            {
                Directory.CreateDirectory(artifactsDirectory);
                File.WriteAllText(Path.Combine(artifactsDirectory, "result.json"), JsonSerializer.Serialize(new
                {
                    scenarioId, passed, fixture, error = failure?.ToString(),
                    coverage = "Actual authored project/asset services, history and independent persisted reload; no UI input or compiler execution",
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (failure != null) File.WriteAllText(Path.Combine(artifactsDirectory, "error.txt"), failure.ToString());
            }
            catch (Exception reportingError) { Console.Error.WriteLine(reportingError); passed = false; }
        }
        return passed ? 0 : 1;
    }

    private static void ProjectMetadata(string fixture)
    {
        NativeYamlContentReference(fixture);
        var sceneId = new AssetId(Guid.NewGuid());
        var sceneSettings = new Stride.Assets.Presentation.SceneEditor.SceneSettingsData
        {
            CamMoveSpeed = 7.5f,
            HiddenGizmos = ["Model", "Physics"],
        };
        var sourceSettings = new Dictionary<AssetId, Stride.Assets.Presentation.SceneEditor.SceneSettingsData> { [sceneId] = sceneSettings };
        var clonedSettings = AssetCloner.Clone(sourceSettings);
        sceneSettings.CamMoveSpeed = 2;
        sceneSettings.HiddenGizmos.Clear();
        Require(!ReferenceEquals(sourceSettings, clonedSettings) && !ReferenceEquals(sceneSettings, clonedSettings[sceneId])
            && clonedSettings[sceneId].CamMoveSpeed == 7.5f && clonedSettings[sceneId].HiddenGizmos.SequenceEqual(new[] { "Model", "Physics" }),
            "Generated scene-settings dictionary serializer must retain a detached snapshot for metadata history and retirement");
        var path = Directory.EnumerateFiles(fixture, "*.csproj").Single();
        var original = File.ReadAllBytes(path);
        var package = new Package();
        var project = new SolutionProject(package, Guid.NewGuid(), path);
        using var registry = new EditorAuthoringRegistry();
        using var history = new EditorSessionHistory(registry);
        var state = registry.GetOrCreate(path, null, () => new ProjectBuildSettingsDocument(project, history));
        using var view = state.AcquireView();
        var document = state.Document;
        var initial = document.Raw(SolutionProject.ContainsAssetTypesProperty);
        document.SetContains(true);
        Require(document.ContainsAssetTypes && project.ContainsAssetTypes == true && document.IsDirty, "Contains-asset-types edit must update canonical project and dirty state");
        document.Undo();
        Require(document.Raw(SolutionProject.ContainsAssetTypesProperty) == initial && !document.IsDirty && !document.CanUndo, "Project property gesture must undo as one transaction to its savepoint");
        document.Redo(); document.Save();
        using (var persisted = new AuthoredProjectProperties(File.ReadAllBytes(path), path))
            Require(persisted.Get(SolutionProject.ContainsAssetTypesProperty) == "true", "Independent native project XML reload must preserve authored setting");

        var dependency = new DependencyRange("Stride.TestCapabilityDependency", new PackageVersionRange(new PackageVersion("1.0.0"), true, new PackageVersion("1.0.0"), true), DependencyType.Package);
        var baselineRevision = document.AuthoringRevision;
        try { document.ChangeDependency(null, dependency, _ => throw new InvalidOperationException("Controlled dependency policy rejection")); }
        catch (InvalidOperationException error) when (error.Message == "Controlled dependency policy rejection") { }
        Require(document.AuthoringRevision == baselineRevision && !document.IsDirty && project.DirectDependencies.Count == 0,
            "Rejected dependency policy must preserve actual project collection, revision and savepoint");
        var validationCounts = new List<int>();
        document.ChangeDependency(null, dependency, values =>
        {
            Require(values.Count <= 1 && values.All(value => value.Name == dependency.Name && value.VersionRange.ToString() == dependency.VersionRange.ToString()),
                "Dependency validation must accept both actual forward and undo states without unrelated changes");
            validationCounts.Add(values.Count);
        });
        Require(project.DirectDependencies.Single().Name == dependency.Name && document.IsDirty, "Supported package dependency edit must update canonical project");
        document.Undo(); Require(project.DirectDependencies.Count == 0 && !document.IsDirty, "Dependency undo must restore clean canonical collection");
        document.Redo(); document.Save();
        Require(validationCounts.SequenceEqual(new[] { 1, 0, 1 }), "Dependency policy must validate actual edit, undo and redo states");
        var saved = File.ReadAllBytes(path);
        var xml = XDocument.Load(path);
        Require(xml.Descendants().Single(element => element.Name.LocalName == "PackageReference" && (string?)element.Attribute("Include") == dependency.Name).Attribute("Version")?.Value == dependency.VersionRange.ToString(),
            "Actual package dependency version must persist through independent XML reload");
        document.SetContains(false);
        File.AppendAllText(path, Environment.NewLine + "<!-- controlled external modification -->");
        var external = File.ReadAllBytes(path);
        var rejected = false;
        try { document.Save(); } catch (IOException) { rejected = true; }
        Require(rejected && document.IsDirty && project.ContainsAssetTypes == false && File.ReadAllBytes(path).SequenceEqual(external),
            "Save conflict must preserve dirty authored setting and externally changed bytes");
        File.WriteAllBytes(path, saved);
        document.Save();
        using (var persisted = new AuthoredProjectProperties(File.ReadAllBytes(path), path))
            Require(persisted.Get(SolutionProject.ContainsAssetTypesProperty) == "false" && !document.IsDirty, "Retry must persist intended setting and clear dirty state");
        Require(!original.SequenceEqual(File.ReadAllBytes(path)), "Project case must exercise real persisted changes");
        PackageUserSettingsOwnership(fixture);
    }

    private static void PackageUserSettingsOwnership(string fixture)
    {
        var path = Path.Combine(fixture, "SettingsOwnership.sdpkg");
        var key = new Stride.Core.Settings.SettingsKey<int>("Regression/UserSettings/" + Guid.NewGuid().ToString("N"),
            PackageUserSettings.SettingsContainer, 0);
        var original = new Package { FullPath = path };
        original.UserSettings.SetValue(key, 17);
        Require(original.UserSettings.Save(), "Native package user settings fixture must save");
        var package = new Package { FullPath = path };
        using var session = new PackageSession();
        session.Projects.Add(new SolutionProject(package, Guid.NewGuid(), Directory.EnumerateFiles(fixture, "*.csproj").Single()));
        package.State = PackageState.AssetsReady;
        using var registry = new EditorAuthoringRegistry();
        using var history = new EditorSessionHistory(registry);
        Require(!package.IsReadOnly && ReferenceEquals(package.Session, session) && session.Packages.Contains(package),
            "Settings owner must be an actual editable project package registered in its native session");
        Require(package.UserSettings.GetValue(key) == 17 && !package.UserSettings.Profile.LoadedContentHash.IsEmpty,
            "Native package user settings reload must preserve actual value and file-ownership baseline");
        var owner = registry.GetOrCreate(path + ".user", null, () => new PackageSettingsDocument(package, history));
        using var view = owner.AcquireView();
        var document = owner.Document;
        var baseline = File.ReadAllBytes(document.Path);
        document.Set(key, 29);
        var recovery = EditorRecoveryDocuments.Capture(document, null);
        Require(recovery.Kind == "package-settings" && recovery.PackagePath == path
            && recovery.BaselineHash.SequenceEqual(System.Security.Cryptography.SHA256.HashData(baseline)),
            "Dirty native package preferences must capture their exact owner and original disk baseline without an asset item");
        document.Undo();
        Require(package.UserSettings.GetValue(key) == 17 && !document.IsDirty, "Recovery fixture must use a genuinely clean current native profile");
        document.RestoreRecovery(recovery);
        Require(package.UserSettings.GetValue(key) == 29 && document.IsDirty
            && ReferenceEquals(registry.Find(document.Path), owner) && File.ReadAllBytes(document.Path).SequenceEqual(baseline),
            "Recovery must restore real native preference data on its canonical journal without writing original files");
        var revision = document.AuthoringRevision;
        File.AppendAllText(document.Path, "\n# actual external settings modification\n");
        var external = File.ReadAllBytes(document.Path);
        var rejected = false;
        try { document.Save(); } catch (IOException) { rejected = true; }
        Require(rejected && document.IsDirty && document.AuthoringRevision == revision && package.UserSettings.GetValue(key) == 29
            && File.ReadAllBytes(document.Path).SequenceEqual(external),
            "Settings save conflict must retain actual external bytes, authored value, dirty revision and history");
        document.Undo();
        Require(package.UserSettings.GetValue(key) == 17 && !document.IsDirty, "Conflict must preserve reversible original settings value");
        document.Redo();
        File.WriteAllBytes(document.Path, baseline);
        document.Save();
        using var loaded = PackageUserSettings.SettingsContainer.LoadSettingsProfile(document.Path, false, registerInContainer: false)
            ?? throw new InvalidOperationException("Saved package user settings must independently reload");
        Require(key.GetValue(loaded, true) == 29 && !document.IsDirty
            && loaded.LoadedContentHash.Span.SequenceEqual(package.UserSettings.Profile.LoadedContentHash.Span),
            "Guarded retry must save intended settings and acknowledge the same independent native file baseline");
        document.Undo();
        Require(package.UserSettings.GetValue(key) == 17 && document.IsDirty, "Undo after saving recovered preferences must retain the genuine earlier authored value");
        document.Save();
        using var undoneReload = PackageUserSettings.SettingsContainer.LoadSettingsProfile(document.Path, false, registerInContainer: false)
            ?? throw new InvalidOperationException("Saved recovery Undo must independently load");
        Require(key.GetValue(undoneReload, true) == 17 && !document.IsDirty,
            "Recovery history replay must retain the newer actual file baseline rather than resurrect a stale conflict hash");
        foreach (var profile in new[] { original.UserSettings.Profile, package.UserSettings.Profile })
        {
            PackageUserSettings.SettingsContainer.UnloadSettingsProfile(profile);
            profile.Dispose();
        }
    }

    private static void NativeYamlContentReference(string fixture)
    {
        var type = typeof(Stride.Rendering.Compositing.GraphicsCompositor);
        var factoryType = typeof(Stride.Core.Assets.Serializers.ContentReferenceSerializer);
        var factoryAssembly = factoryType.Assembly;
        var scan = Stride.Core.Reflection.AssemblyRegistry.GetScanTypes(factoryAssembly);
        var factories = scan?.Types.GetValueOrDefault(typeof(Stride.Core.Yaml.Serialization.IYamlSerializableFactory)) ?? [];
        var diagnostics = JsonSerializer.Serialize(new
        {
            exactContent = AssetRegistry.IsExactContentType(type),
            canVisit = Stride.Core.Assets.Serializers.ContentReferenceSerializer.Default.CanVisit(type),
            engineCategories = Stride.Core.Reflection.AssemblyRegistry.FindCategories(type.Assembly).ToArray(),
            enginePath = type.Assembly.Location,
            factoryCategories = Stride.Core.Reflection.AssemblyRegistry.FindCategories(factoryAssembly).ToArray(),
            factoryPath = factoryAssembly.Location,
            scannedFactories = factories.Select(factory => factory.AssemblyQualifiedName).ToArray(),
        });
        Require(AssetRegistry.IsExactContentType(type) && factories.Contains(factoryType), "Native YAML reference registration must include actual compositor and content factory: " + diagnostics);
        var path = Path.Combine(fixture, "Assets", "GameSettings.sdgamesettings");
        using var input = File.OpenRead(path);
        var settings = (Stride.Assets.GameSettingsAsset)Stride.Core.Yaml.AssetYamlSerializer.Default.Deserialize(input, typeof(Stride.Assets.GameSettingsAsset));
        var reference = ContentAssetReference.Read(settings.GraphicsCompositor);
        var expected = File.ReadLines(path).Single(line => line.StartsWith("GraphicsCompositor: ", StringComparison.Ordinal))["GraphicsCompositor: ".Length..];
        Require(AssetReference.TryParse(expected, out var id, out var url), "Fixture must provide a native scalar compositor reference");
        Require(settings.GraphicsCompositor is not Stride.Core.Yaml.IUnloadable && reference?.Id == id && reference.Location == url.FullPath,
            "Native YAML parser must route compositor scalar through content reference factory: " + diagnostics + "; parseError=" + (settings.GraphicsCompositor as Stride.Core.Yaml.IUnloadable)?.Error);
        DebuggerCloneProfile();
        using var reloaded = File.OpenRead(path);
        var afterClone = (Stride.Assets.GameSettingsAsset)Stride.Core.Yaml.AssetYamlSerializer.Default.Deserialize(reloaded, typeof(Stride.Assets.GameSettingsAsset));
        Require(ContentAssetReference.Read(afterClone.GraphicsCompositor)?.Id == id,
            "Debugger clone serialization must not change ordinary asset reference routing or its cache");
    }

    private static void DebuggerCloneProfile()
    {
        var scopeField = typeof(Stride.Debugger.Target.CloneReferenceSerializer).GetField("References", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native debugger reference scope is unavailable");
        var previous = scopeField.GetValue(null);
        var references = new List<object>();
        var model = new Stride.Rendering.Model();
        var source = new Stride.Engine.ModelComponent { Model = model };
        var events = new List<Stride.Core.Yaml.Events.ParsingEvent>();
        using var yaml = new Stride.Core.Yaml.AssetYamlSerializer(Stride.Debugger.Target.CloneReferenceSerializer.Profile);
        try
        {
            scopeField.SetValue(null, references);
            yaml.Serialize(new Stride.Core.Yaml.ParsingEventListEmitter(events), source, typeof(Stride.Engine.EntityComponent));
            Require(references.Any(value => ReferenceEquals(value, model)), "Dedicated debugger profile must capture actual nested live content identity");
            var reader = new Stride.Core.Yaml.EventReader(new Stride.Core.Yaml.MemoryParser(events.ToList()));
            var clone = yaml.Deserialize(reader, null, typeof(Stride.Engine.EntityComponent), out _) as Stride.Engine.ModelComponent;
            Require(clone != null && !ReferenceEquals(clone, source) && ReferenceEquals(clone.Model, model),
                "Debugger clone mapping must recreate component while retaining actual live model reference");
        }
        finally { scopeField.SetValue(null, previous); }
    }

    private static void AssetIdentity(string packagePath)
    {
        var workspace = new ProjectWorkspace(packagePath);
        var retained = new Dictionary<AssetId, IAssetReferenceDocument>();
        var relocation = new AssetRelocationHistory();
        var deletion = new AssetDeletionHistory();
        try
        {
            IAssetReferenceDocument GetDocument(AssetEntry entry)
            {
                if (retained.TryGetValue(entry.Id, out var existing)) return existing;
                IAssetReferenceDocument created = entry.Extension switch
                {
                    ".sdscene" => new SceneDocument(entry.Path),
                    ".sdpromodel" => new ProceduralAssetDocument(entry.Path),
                    _ => new AssetMetadataDocument(entry.Path),
                };
                retained.Add(entry.Id, created);
                return created;
            }
            var entry = workspace.Assets.Single(asset => asset.Extension == ".sdpromodel" && Path.GetFileNameWithoutExtension(asset.Path) == "Sphere");
            var identity = entry.Id;
            var originalPath = entry.Path;
            GetDocument(entry);
            void MoveOwned(string before, string after)
            {
                var owner = retained.Values.Single(document => string.Equals(document.Path, before, StringComparison.OrdinalIgnoreCase)) as IRelocatableEditorDocument
                    ?? throw new InvalidOperationException("Moved asset must have its actual owned-file document");
                using var rename = new WindowsOwnedAssetFileRename(before, owner.DiskContentHash.Span);
                Directory.CreateDirectory(Path.GetDirectoryName(after)!);
                rename.Move(after);
                try { owner.RelocatePath(after); }
                catch { rename.Move(before); throw; }
            }
            var dependencyBefore = workspace.Session.DependencyManager.CaptureDependencies();
            Require(dependencyBefore.GetValueOrDefault(identity)?.LinksIn.Any() == true, "Fixture target must have real incoming references");
            Require(relocation.Relocate(workspace, entry, "Capability/Moved Sphere", retained, GetDocument, MoveOwned), "Real asset relocation service must move the asset");
            var moved = workspace.FindAsset(identity);
            var movedPath = moved.FullPath.ToOSPath();
            Require(moved.Id == identity && File.Exists(movedPath) && !File.Exists(originalPath), "Relocation must preserve ID and move owned file");
            relocation.Undo(); Require(workspace.FindAsset(identity).FullPath.ToOSPath() == originalPath && File.Exists(originalPath), "Move undo must restore original asset path and disk file");
            relocation.Redo(); Require(workspace.FindAsset(identity).FullPath.ToOSPath() == movedPath, "Move redo must retain identity and destination");
            foreach (var document in retained.Values) if (document.IsDirty) document.Save();
            workspace.SavePackageChanges();
            var modelSerializer = AssetCloner.ClonerSelector.GetSerializer(typeof(Stride.Rendering.ProceduralModels.SphereProceduralModel));
            Require(modelSerializer != null, "Native procedural model must have its actual clone serializer before independent reload");
            using (var check = new WorkspaceLease(packagePath))
                Require(check.Workspace.FindAsset(identity).FullPath.ToOSPath() == movedPath && check.Workspace.Session.DependencyManager.CaptureDependencies().GetValueOrDefault(identity)?.LinksIn.Any() == true,
                    "Independent package reload must retain moved identity and real referencers");
            Require(AssetCloner.ClonerSelector.GetSerializer(typeof(Stride.Rendering.ProceduralModels.SphereProceduralModel))?.GetType() == modelSerializer.GetType(),
                "Closing another package session must preserve host-owned clone serializers needed by the retained document");
            workspace.RefreshAssets();
            var movedEntry = workspace.Assets.Single(asset => asset.Id == identity);
            var detached = new HashSet<AssetId>();
            var plan = deletion.Prepare(workspace, [movedEntry], retained, GetDocument,
                (documents, remove) => { foreach (var document in documents) { if (remove) detached.Add(document.AuthoredAsset.Id); else detached.Remove(document.AuthoredAsset.Id); } },
                documents => { foreach (var document in documents) retained.Remove(document.AuthoredAsset.Id); },
                () => retained.ToDictionary(pair => pair.Key, pair => pair.Value.AuthoredAsset));
            deletion.Delete(plan, true);
            Require(workspace.Session.FindAsset(identity) == null && detached.Contains(identity), "Deletion must remove actual asset membership and detach its document");
            deletion.Undo(); Require(workspace.Session.FindAsset(identity)?.Id == identity && !detached.Contains(identity), "Deletion undo must restore original identity and document membership");
            deletion.Redo(); Require(workspace.Session.FindAsset(identity) == null, "Deletion redo must remove asset again");
            foreach (var document in retained.Values.Where(document => !detached.Contains(document.AuthoredAsset.Id))) if (document.IsDirty) document.Save();
            workspace.SavePackageChanges((_, path, _) =>
            {
                if (!deletion.TryRemoveOwnedFile(path) && File.Exists(path)) throw new InvalidOperationException("Deletion attempted an unrelated file");
            });
            Require(!File.Exists(movedPath), "Deletion save must remove fingerprint-owned asset file");
            using (var check = new WorkspaceLease(packagePath))
            {
                Require(check.Workspace.Session.FindAsset(identity) == null, "Independent project reload must not resurrect deleted asset identity");
                Require(check.Workspace.AssetItems.All(item => AssetReferenceAnalysis.Visit(item.Asset)
                    .All(link => ContentAssetReference.Read(link.Reference)?.Id != identity)),
                    "Independent persisted assets must not retain dangling references to deleted identity");
            }
        }
        finally
        {
            deletion.Clear(); relocation.Clear();
            foreach (var document in retained.Values) document.Dispose();
            workspace.ReleaseUnadopted();
        }
    }

    private sealed class WorkspaceLease(string packagePath) : IDisposable
    {
        public ProjectWorkspace Workspace { get; } = new(packagePath);
        public void Dispose() => Workspace.ReleaseUnadopted();
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
