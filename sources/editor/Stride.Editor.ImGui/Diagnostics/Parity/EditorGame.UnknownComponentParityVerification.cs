using System.Security.Cryptography;
using System.Text;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Yaml;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object VerifyUnknownComponentParity(string ownedDirectory)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        var directory = Path.GetFullPath(ownedDirectory);
        var artifactRoot = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
        var privateArtifacts = !string.IsNullOrWhiteSpace(artifactRoot)
            && directory.StartsWith(Path.GetFullPath(artifactRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!args.Contains("--ui-test") || !(directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) || privateArtifacts)
            || Directory.Exists(directory))
            throw new InvalidOperationException("Unknown-component verification requires a fresh private native fixture directory.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "UnknownComponent.sdscene");
        var known = new Entity("Known sibling");
        var unknownOwner = new Entity("Unknown component owner");
        var camera = new CameraComponent(); unknownOwner.Components.Add(camera);
        var asset = new SceneAsset();
        asset.Hierarchy.Parts.Add(new EntityDesign(known)); asset.Hierarchy.RootParts.Add(known);
        asset.Hierarchy.Parts.Add(new EntityDesign(unknownOwner)); asset.Hierarchy.RootParts.Add(unknownOwner);
        AssetCollectionItemIdHelper.GenerateMissingItemIds(asset);
        AssetFileSerializer.Save(path, asset, null);
        var text = File.ReadAllText(path);
        const string originalTag = "!CameraComponent";
        var offset = text.IndexOf(originalTag, StringComparison.Ordinal);
        var secondOffset = offset < 0 ? -1 : text.IndexOf(originalTag, offset + originalTag.Length, StringComparison.Ordinal);
        if (offset < 0 || secondOffset < 0
            || text.IndexOf(originalTag, secondOffset + originalTag.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Native scene serialization must provide both RootParts and Parts representations of the one genuine camera component.");
        var missingTag = "!NativeMissingComponent_" + Guid.NewGuid().ToString("N");
        File.WriteAllText(path, text.Replace(originalTag, missingTag, StringComparison.Ordinal), new UTF8Encoding(false));

        // The native loader, rather than a test-created proxy, must materialize
        // this genuinely unregistered tag. Loading failure is not a passing skip.
        var initial = AssetFileSerializer.Load<SceneAsset>(path).Asset;
        var unknown = FindUnknown(initial, unknownOwner.Id);
        var componentId = ((EntityComponent)unknown).Id;
        if (componentId != camera.Id || initial.Hierarchy.RootParts.Count != 2
            || initial.Hierarchy.RootParts.Single(entity => entity.Id == unknownOwner.Id).Components
                .OfType<IUnloadable>().Single() is not EntityComponent rootUnknown || rootUnknown.Id != camera.Id)
            throw new InvalidDataException("Native loading did not preserve the same genuine component identity in RootParts and Parts.");
        if (unknown.ParsingEvents.Count == 0 || !SerializeUnknown(unknown).Contains(missingTag, StringComparison.Ordinal))
            throw new InvalidDataException("The native unsupported-component path did not preserve the actual unknown YAML tag/events.");
        var unknownBytes = SerializeUnknown(unknown);
        var originalFile = File.ReadAllBytes(path);

        // A real isolated canonical registry/history avoids contaminating the
        // loaded project's asset compiler or claiming unsupported GPU rendering.
        using var registry = new EditorAuthoringRegistry();
        using var history = new EditorSessionHistory(registry);
        var state = registry.GetOrCreate(path, initial.Id, () => new SceneDocument(path, history));
        using var view = state.AcquireView();
        var document = state.Document;
        if (!document.HasUnloadableObjects || !document.CanSave || !ReferenceEquals(registry.Find(initial.Id), state))
            throw new InvalidOperationException("The actual native scene did not retain a saveable canonical owner with unknown descendants.");
        var sibling = document.Asset.Hierarchy.Parts[known.Id].Entity;
        var actualUnknown = FindUnknown(document.Asset, unknownOwner.Id);
        var unknownNode = document.HierarchyGraph.Container.NodeContainer.GetOrCreateNode(actualUnknown);
        if (!UnknownAssetData.IsProxyNode(unknownNode) || document.CanEditHierarchyNode(unknownNode))
            throw new InvalidOperationException("The native unsupported component was not recognized as a guarded proxy node.");
        var baselineHistory = history.History.Position;
        var proxyField = document.GetComponentFields((EntityComponent)actualUnknown).FirstOrDefault(field => field.SupportsEditing);
        if (proxyField != null)
        {
            document.SelectEntity(document.Asset.Hierarchy.Parts[unknownOwner.Id].Entity);
            var rejected = false;
            try { document.BeginComponentEdit(proxyField); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || history.History.Position != baselineHistory || document.IsDirty)
                throw new InvalidOperationException("The actual unsupported-component edit route was not rejected without native history mutation.");
            AssertUnknown(document.Asset, unknownOwner.Id, componentId, unknownBytes);
        }
        const string renamed = "Known sibling preserved Ω";
        document.Rename(sibling, renamed);
        if (history.History.Position != baselineHistory + 1 || !document.IsDirty)
            throw new InvalidOperationException("Known-sibling mutation did not publish one actual native history gesture.");
        AssertUnknown(document.Asset, unknownOwner.Id, componentId, unknownBytes);
        document.Undo();
        if (sibling.Name != known.Name || history.History.Position != baselineHistory)
            throw new InvalidOperationException("Native Undo did not restore the known sibling and exact history boundary.");
        AssertUnknown(document.Asset, unknownOwner.Id, componentId, unknownBytes);
        document.Redo();
        if (sibling.Name != renamed || history.History.Position != baselineHistory + 1)
            throw new InvalidOperationException("Native Redo did not restore the actual known sibling gesture.");
        if (!File.ReadAllBytes(path).SequenceEqual(originalFile))
            throw new InvalidOperationException("Authoring/history unexpectedly changed the fixture's original disk bytes.");
        document.Save();
        if (document.IsDirty || !ReferenceEquals(registry.Find(path)?.Document, document))
            throw new InvalidOperationException("Native Save did not acknowledge the exact canonical document.");
        var reloaded = AssetFileSerializer.Load<SceneAsset>(path).Asset;
        if (reloaded.Id != initial.Id || reloaded.Hierarchy.Parts.Count != 2
            || reloaded.Hierarchy.Parts[known.Id].Entity.Name != renamed
            || reloaded.Hierarchy.Parts[unknownOwner.Id].Entity.Name != unknownOwner.Name)
            throw new InvalidOperationException("Independent native deserialization lost asset/entity identity or the saved sibling edit.");
        AssertUnknown(reloaded, unknownOwner.Id, componentId, unknownBytes);
        return new { actualNativeUnregisteredYamlComponent = true, missingTag, assetId = initial.Id,
            unknownOwnerId = unknownOwner.Id, unknownComponentId = componentId,
            canonicalSiblingHistoryUndoRedoSave = true, independentNativeReloadPreservesUnknownEvents = true,
            unknownProxyAuthoringGuard = true, actualProxyEditRejected = proxyField != null,
            parsingEventCount = unknown.ParsingEvents.Count,
            canonicalUnknownSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unknownBytes))),
            scope = "Real native unsupported-component loading and isolated canonical Quantum/history/persistence; not browser/UI E2E, package compiler or GPU runtime acceptance." };
    }
    private static IUnloadable FindUnknown(SceneAsset asset, Guid owner)
        => asset.Hierarchy.Parts[owner].Entity.Components.OfType<IUnloadable>().Single();
    private static string SerializeUnknown(IUnloadable unknown)
    {
        using var stream = new MemoryStream();
        AssetYamlSerializer.Default.Serialize(stream, unknown);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static void AssertUnknown(SceneAsset asset, Guid owner, Guid componentId, string original)
    {
        var unknown = FindUnknown(asset, owner);
        var rootUnknown = asset.Hierarchy.RootParts.Single(entity => entity.Id == owner).Components.OfType<IUnloadable>().Single();
        if (((EntityComponent)unknown).Id != componentId || SerializeUnknown(unknown) != original
            || ((EntityComponent)rootUnknown).Id != componentId || SerializeUnknown(rootUnknown) != original)
            throw new InvalidOperationException("The real unknown component identity or original canonical YAML event stream changed.");
    }
}
