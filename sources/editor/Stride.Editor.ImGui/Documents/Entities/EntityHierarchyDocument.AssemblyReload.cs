using System.Reflection;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record HierarchyAssemblyReloadSnapshot(EntityHierarchyDocument Source, byte[] Yaml, string? Namespace, EntitySelectionSnapshot Selection);

internal abstract partial class EntityHierarchyDocument : IEditorOwnedFileGeneration
{
    void IEditorOwnedFileGeneration.AcceptOwnedSaveBaseline(byte[] hash, bool savingGeneration)
    {
        diskHash = hash.ToArray();
        if (!savingGeneration) savedRevision = -1;
    }
    internal bool UsesAssemblyTypes(IReadOnlySet<Assembly> assemblies)
    {
        var visitor = new ReloadTypeVisitor(assemblies); visitor.Visit(Asset); return visitor.Found;
    }
    internal HierarchyAssemblyReloadSnapshot CaptureAssemblyReload()
    {
        EndEdits(); EnsureHierarchyGraph(); PrepareHierarchyMetadata();
        using var stream = new MemoryStream();
        var assetNamespace = hierarchyItem?.Package?.Container?.AssetNamespace;
        AssetFileSerializer.Save(stream, Asset, hierarchyItem?.YamlMetadata ?? loaded.YamlMetadata, assetNamespace: assetNamespace);
        return new(this, stream.ToArray(), assetNamespace, SnapshotSelection());
    }
    internal IEditorDocument CreateAssemblyReloadedGeneration(HierarchyAssemblyReloadSnapshot snapshot, ILogger log)
    {
        if (!ReferenceEquals(snapshot.Source, this)) throw new InvalidOperationException("The migration snapshot belongs to another hierarchy owner.");
        using var stream = new MemoryStream(snapshot.Yaml, false);
        var restored = AssetFileSerializer.Load<Asset>(stream, new UFile(Path), log, snapshot.Namespace);
        if (restored.Asset.Id != Asset.Id || !SerializedAssetTypeIdentity.Matches(snapshot.Yaml, restored.Asset))
            throw new InvalidOperationException("The migrated hierarchy changed its actual asset identity or asset root type.");
        if (restored.Asset is Stride.Core.Yaml.IUnloadable)
        {
            var unavailable = EditorLoadedAssetDocuments.Create(Path, restored, diskHash ?? [], SessionHistory, isReadOnly);
            ((IEditorOwnedFileGeneration)unavailable).AcceptOwnedSaveBaseline(diskHash ?? [], false);
            return unavailable;
        }
        if (this is SceneDocument ? restored.Asset is not SceneAsset : restored.Asset is not PrefabAsset)
            throw new InvalidOperationException("The migrated hierarchy changed its actual editor kind.");
        EntityHierarchyDocument next = this switch
        {
            SceneDocument => new SceneDocument(Path, restored, SessionHistory!, isReadOnly),
            PrefabDocument => new PrefabDocument(Path, restored, SessionHistory!, isReadOnly),
            _ => throw new InvalidOperationException("This hierarchy owner cannot migrate assembly types.")
        };
        try
        {
            if (!next.Asset.Hierarchy.Parts.Keys.Order().SequenceEqual(Asset.Hierarchy.Parts.Keys.Order()))
                throw new InvalidOperationException("The migrated hierarchy changed its actual part identities.");
            next.diskHash = diskHash?.ToArray(); next.savedRevision = savedRevision;
            next.nextRevision = nextRevision + 1; next.revision = next.nextRevision;
            next.savedAssetTags = SavedTags.ToArray();
            next.RestoreSelection(snapshot.Selection.Ids, snapshot.Selection.PrimaryId);
            return next;
        }
        catch { next.Dispose(); throw; }
    }
    private sealed class ReloadTypeVisitor(IReadOnlySet<Assembly> assemblies) : AssetVisitorBase
    {
        internal bool Found;
        public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
        {
            if (assemblies.Contains(obj.GetType().Assembly) || obj is Stride.Core.Yaml.IUnloadable) { Found = true; return; }
            if (!Found) base.VisitObject(obj, descriptor, visitMembers);
        }
    }
}
