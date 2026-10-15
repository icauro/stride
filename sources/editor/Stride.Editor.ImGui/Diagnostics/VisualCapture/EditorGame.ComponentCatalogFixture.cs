using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Reflection;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // This diagnostic writes only the user-authorized disposable project copy.
    private const string ComponentCatalogFixture = @"E:\develop\personal\stride_workspace\editor-imgui\bin\editor-imgui\visual-capture\component-catalog-review\fixture";
    private static readonly string[] ComponentCatalogModules =
    [
        "Stride.Engine", "Stride.Foundation", "Stride.Games", "Stride.Graphics", "Stride.Rendering",
        "Stride.Audio", "Stride.Input", "Stride.Navigation", "Stride.Particles", "Stride.Physics",
        "Stride.BepuPhysics", "Stride.BepuPhysics._2D", "Stride.BepuPhysics.Navigation", "Stride.BepuPhysics.Soft", "Stride.BepuPhysics.Debug",
        "Stride.SpriteStudio.Runtime", "Stride.UI", "Stride.Video", "Stride.VirtualReality", "Stride.Voxels"
    ];

    private sealed record ComponentCatalogModule(string Assembly, string AssemblyFile, string Status, string? Error);
    private sealed record ComponentCatalogEntry(string FullName, string Assembly, string Status, string? Reason,
        string? EntityId = null, string? EntityName = null, string? Error = null);

    private static bool ComponentCatalogHasSerializableContract(Type type)
    {
        // Same inherited-contract rule as the real editor's TypeDescriptorExtensions.
        var inherited = false;
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            if (current.GetCustomAttribute<DataContractAttribute>(false) is { } contract)
                return !inherited || contract.Inherited;
            inherited = true;
        }
        return false;
    }

    private object CreateVisualCaptureComponentCatalog(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || parameters.EnumerateObject().Any(property => property.Name is not ("fixtureWorkspacePath" or "scenePath" or "manifestOutput" or "rootName")))
            throw new InvalidOperationException("Component fixture parameters are required and must use the explicit allowlist.");
        string RequiredPath(string name)
        {
            if (!parameters.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { } path || !Path.IsPathFullyQualified(path))
                throw new InvalidOperationException("An absolute fixture path is required: " + name);
            return Path.GetFullPath(path);
        }
        var fixture = Path.GetFullPath(ComponentCatalogFixture);
        var project = RequiredPath("fixtureWorkspacePath"); var scene = RequiredPath("scenePath");
        var output = RequiredPath("manifestOutput");
        bool Same(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        if (!Same(project, Path.Combine(fixture, "EditorParity.Game.sdpkg"))
            || !Same(scene, Path.Combine(fixture, "Assets", "MainScene.sdscene"))
            || !Same(output, Path.Combine(fixture, "component-catalog.json"))
            || !File.Exists(Path.Combine(fixture, ".editor-component-catalog-fixture"))
            || workspace == null || !Same(workspace.OpenPath, project)
            || document is not SceneDocument owner || !Same(owner.Path, scene) || !owner.CanSave)
            throw new InvalidOperationException("Component catalog authoring is restricted to the exact authorized marked fixture and its active MainScene.");
        // A reparse point must not redirect the authorized fixture into another project.
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(scene)!); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Component fixture paths must not traverse reparse points.");
        foreach (var path in new[] { project, scene, output, Path.Combine(fixture, ".editor-component-catalog-fixture") })
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Component fixture files must not be reparse points.");
        if (parameters.TryGetProperty("rootName", out var rootValue)
            && (rootValue.ValueKind != JsonValueKind.String || rootValue.GetString() != "Engine Components"))
            throw new InvalidOperationException("The component fixture root name is Engine Components.");
        if (owner.Asset.Hierarchy.Parts.Values.Any(part => part.Entity.Name == "Engine Components"))
            throw new InvalidOperationException("The component catalog already exists; select the existing fixture rather than duplicating it.");
        if (owner.IsDirty) throw new InvalidOperationException("Save or discard existing edits before creating the component fixture.");

        var modules = new List<ComponentCatalogModule>(); var discovered = new List<Type>();
        foreach (var module in ComponentCatalogModules.Order(StringComparer.Ordinal))
        {
            try
            {
                Assembly assembly;
                try { assembly = Assembly.Load(new AssemblyName(module)); }
                catch (FileNotFoundException)
                {
                    var deployed = Path.Combine(AppContext.BaseDirectory, module + ".dll");
                    if (!File.Exists(deployed)) throw;
                    assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(deployed);
                }
                if (assembly.GetName().Name != module)
                    throw new InvalidOperationException("The deployed assembly identity does not match the allowlisted engine module.");
                AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
                try { discovered.AddRange(assembly.GetTypes()); modules.Add(new(module, assembly.Location, "loaded", null)); }
                catch (ReflectionTypeLoadException error)
                {
                    discovered.AddRange(error.Types.OfType<Type>());
                    modules.Add(new(module, assembly.Location, "partially-loaded", string.Join("\n", error.LoaderExceptions.OfType<Exception>().Select(exception => exception.Message))));
                }
            }
            catch (Exception error) { modules.Add(new(module, Path.Combine(AppContext.BaseDirectory, module + ".dll"), "unavailable", error.GetBaseException().Message)); }
        }
        var registered = EntityHierarchyDocument.RegisteredComponentTypes.ToHashSet();
        var entries = new List<ComponentCatalogEntry>();
        var candidates = discovered.Where(type => typeof(EntityComponent).IsAssignableFrom(type))
            .Distinct().OrderBy(type => type.Name, StringComparer.Ordinal).ThenBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        var history = owner.SessionHistory ?? throw new InvalidOperationException("The fixture requires actual canonical session history.");
        documents.CommitAll();
        Entity root;
        string? rollbackError = null;
        using (history.CreateTransaction("Create engine component catalog"))
        {
            root = owner.InsertNewEntity(new Entity("Engine Components"));
            var ordinal = 0;
            foreach (var type in candidates)
            {
                var name = type.FullName ?? type.Name; var assemblyName = type.Assembly.GetName().Name!;
                var reason = type.IsAbstract ? "abstract" : type.ContainsGenericParameters ? "open-generic"
                    : !(type.IsPublic || type.IsNestedPublic) ? "non-public"
                    : Attribute.GetCustomAttribute(type, typeof(NonInstantiableAttribute)) != null ? "non-instantiable"
                    : type.GetConstructor(Type.EmptyTypes) == null ? "no-public-parameterless-constructor"
                    : !ComponentCatalogHasSerializableContract(type) ? "no-editor-serializable-contract"
                    : type != typeof(TransformComponent) && !registered.Contains(type) ? "not-registered-for-editor-instantiation" : null;
                if (reason != null) { entries.Add(new(name, assemblyName, "excluded", reason)); continue; }
                if (rollbackError != null)
                {
                    entries.Add(new(name, assemblyName, "skipped", "earlier-rollback-failed"));
                    continue;
                }
                var entityName = $"{++ordinal:000} {type.Name}";
                Entity? child = null;
                try
                {
                    using var captured = history.CaptureOperationsAtomic(() =>
                    {
                        child = owner.InsertNewEntity(new Entity(entityName), root);
                        if (type != typeof(TransformComponent)) owner.AddComponent(child, type);
                    });
                    captured.Publish(history, "Create component fixture: " + type.Name);
                    entries.Add(new(name, assemblyName, "instantiated", null, child!.Id.ToString(), child.Name));
                }
                catch (Exception error)
                {
                    var retained = child != null && owner.Asset.Hierarchy.Parts.ContainsKey(child.Id);
                    entries.Add(new(name, assemblyName, retained ? "failed-rollback" : "failed", null,
                        retained ? child!.Id.ToString() : null, entityName, error.GetBaseException().Message));
                    if (retained)
                    {
                        rollbackError = "Component fixture rollback failed for " + name;
                    }
                }
            }
            owner.Selected = root;
        }
        string? saveError = rollbackError;
        try { if (rollbackError == null) owner.Save(); }
        catch (Exception error) { saveError = error.GetBaseException().Message; }
        var manifest = new
        {
            schemaVersion = 1, fixtureWorkspacePath = project, scenePath = scene, rootName = root.Name,
            rootEntityId = root.Id.ToString(), ordering = "component-name-ordinal-then-full-name-ordinal", saved = saveError == null, saveError,
            complete = saveError == null && modules.All(module => module.Status == "loaded") && entries.All(entry => entry.Status is "instantiated" or "excluded"),
            modules, components = entries, firstFive = entries.Where(entry => entry.Status == "instantiated").Take(5).ToArray()
        };
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, output, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { manifestOutput = output, manifest };
    }
}
