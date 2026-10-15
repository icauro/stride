using Stride.Assets.Entities;
using Stride.Assets.Quantum;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal MultiViewFixtureVerification PrepareMultiViewVerificationFixture()
    {
        if (workspace != null || projectLoading != null) throw new InvalidOperationException("Prepare E05 before project loading.");
        var packagePath = Path.GetFullPath(projectPathInput);
        var fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!packagePath.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("E05 fixture preparation requires the disposable UI fixture copy.");
        var root = Path.GetDirectoryName(packagePath)!;
        var sourcePath = Path.Combine(root, "Assets", "MainScene.sdscene");
        var secondPath = Path.Combine(root, "Assets", "CapabilitySecondScene.sdscene");
        if (File.Exists(secondPath)) throw new IOException("The private E05 scene path already exists.");
        // Load real project script assemblies before cloning their native graph
        // references. Unresolved proxy event text cannot establish remapping.
        var preparedWorkspace = new ProjectWorkspace(packagePath);
        try
        {
            var sourceItem = preparedWorkspace.FindAsset(sourcePath);
            var source = sourceItem.Asset as SceneAsset ?? throw new InvalidDataException("E05 requires the actual native source scene.");
            if (source.Hierarchy.Parts.Values.Any(part => part.Entity.Components.Any(component => component is Stride.Core.Yaml.IUnloadable)))
                throw new InvalidDataException("E05 source scripts must resolve before native identity cloning.");
            AssetQuantumRegistry.RegisterAssembly(typeof(AssetPropertyGraph).Assembly);
            AssetQuantumRegistry.RegisterAssembly(typeof(EntityHierarchyPropertyGraph).Assembly);
            var container = new AssetPropertyGraphContainer(new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } });
            var log = new LoggerResult();
            using var sourceGraph = (EntityHierarchyPropertyGraph)container.InitializeAsset(sourceItem, log)!;
            sourceGraph.Initialize();
            var hierarchy = AssetCompositeHierarchyPropertyGraph<EntityDesign, Entity>.CloneSubHierarchies(container.NodeContainer,
                source, source.Hierarchy.RootParts.Select(entity => entity.Id), SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects, out _);
            var second = (SceneAsset)AssetCloner.Clone(source);
            second.Id = new AssetId(Guid.NewGuid()); second.Hierarchy = hierarchy;
            foreach (var part in second.Hierarchy.Parts.Values) part.Entity.Name = "Secondary " + part.Entity.Name;
            foreach (var entity in second.Hierarchy.RootParts) entity.Transform.Position += new Vector3(2, 0, 0);
            var secondItem = new AssetItem("CapabilitySecondScene", second);
            using var secondGraph = (EntityHierarchyPropertyGraph)container.InitializeAsset(secondItem, log)!;
            secondGraph.Initialize(); secondGraph.PrepareForSave(log, secondItem);
            if (log.HasErrors) throw new InvalidOperationException("E05 fixture graph metadata preparation failed: " + log.ToText());
            var assetNamespace = sourceItem.Package.Container?.AssetNamespace;
            AssetFileSerializer.Save(secondPath, second, secondItem.YamlMetadata, assetNamespace: assetNamespace);
            var reopened = AssetFileSerializer.Load<SceneAsset>(secondPath, assetNamespace: assetNamespace).Asset;
            VerifyIndependentSceneFixture(source, second, reopened);
            var package = AssetFileSerializer.Load<Package>(packagePath).Asset;
            var owner = new StandalonePackage(package);
            package.RootAssets.Add(new AssetReference(second.Id, owner.Qualify(new UFile("CapabilitySecondScene"))));
            AssetFileSerializer.Save(packagePath, package, null);
            return new(second.Id, secondPath, packagePath);
        }
        finally { preparedWorkspace.ReleaseUnadopted(); }
    }

    private static void VerifyIndependentSceneFixture(SceneAsset source, SceneAsset authored, SceneAsset reloaded)
    {
        static Guid[] Ids(SceneAsset scene) => scene.Hierarchy.Parts.Values.SelectMany(part =>
            part.Entity.Components.Select(component => component.Id).Prepend(part.Entity.Id)).ToArray();
        var original = Ids(source); var next = Ids(authored); var persisted = Ids(reloaded);
        if (next.Length != original.Length || next.Distinct().Count() != next.Length || original.Intersect(next).Any()
            || persisted.Distinct().Count() != persisted.Length || !persisted.Order().SequenceEqual(next.Order())
            || !reloaded.Hierarchy.RootParts.Select(entity => entity.Id).SequenceEqual(authored.Hierarchy.RootParts.Select(entity => entity.Id)))
            throw new InvalidDataException("E05 cloned/reloaded world must retain unique independent entity/component IDs and root order.");
        foreach (var root in reloaded.Hierarchy.RootParts)
            if (!ReferenceEquals(reloaded.Hierarchy.Parts[root.Id].Entity, root))
                throw new InvalidDataException("E05 root references must point to their actual owned hierarchy entities.");
        var sphere = reloaded.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Secondary Sphere").Entity;
        var cube = reloaded.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Secondary Imported cube").Entity;
        var script = sphere.Components.Single(component => component.GetType().Name == "InspectorTypes");
        var type = script.GetType();
        if (!ReferenceEquals(type.GetProperty("TargetEntity")!.GetValue(script), cube)
            || !ReferenceEquals(type.GetProperty("TargetTransform")!.GetValue(script), cube.Transform)
            || !((System.Collections.IEnumerable)type.GetProperty("TargetEntities")!.GetValue(script)!).Cast<Entity>().SequenceEqual([cube, sphere]))
            throw new InvalidDataException("E05 native clone/reload must preserve real script entity/component/list references into its own world.");
    }
}
