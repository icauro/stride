using System.Text;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Assets.Serializers;
using Stride.Core.Assets.Yaml;
using Stride.Core.Quantum;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Serialization;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private readonly Dictionary<Guid, long> componentReplacementVersions = [];
    private static SerializerSettings? componentClipboardSettings;
    internal long ComponentReplacementVersion(Guid id) => componentReplacementVersions.GetValueOrDefault(id);

    internal bool CanCopyComponent(EntityComponent component)
    {
        if (loaded.Asset is IUnloadable || component is IUnloadable || component.Entity is not { } entity
            || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design) || !ReferenceEquals(design.Entity, entity)
            || !entity.Components.Contains(component)) return false;
        if (SessionHistory != null && (historyScope.AuthoringState is not { IsPublished: true, IsDisposed: false } state
            || !ReferenceEquals(SessionHistory.Registry.Find(Asset.Id), state))) return false;
        if (SessionHistory == null && HasUnloadableObjects) return false;
        // The transform clipboard deliberately contains only known TRS values,
        // never the parent or child entity references from the live node.
        if (component is TransformComponent) return true;
        var visitor = new ClipboardUnknownClosureVisitor();
        visitor.Visit(HierarchyGraph.Container.NodeContainer.GetOrCreateNode(component));
        return !visitor.Found;
    }

    internal string CopyComponent(EntityComponent component)
    {
        if (!CanCopyComponent(component)) throw new InvalidOperationException("This component cannot be copied.");
        EndEdits(); EnsureHierarchyGraph();
        if (!CanCopyComponent(component)) throw new InvalidOperationException("The component clipboard closure changed.");
        var entity = component.Entity!;
        var node = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(component);
        var external = ExternalReferenceCollector.GetExternalReferences(hierarchyGraph.Definition, node);
        external.Add(entity);
        // Never clone transform child references through their live owners.
        EntityComponent copy = component is TransformComponent transform
            ? new TransformComponent { Id = transform.Id, Position = transform.Position, Rotation = transform.Rotation, Scale = transform.Scale }
            : (EntityComponent)AssetCloner.Clone(component, AssetClonerFlags.None, external, out _)!;
        AssetCollectionItemIdHelper.GenerateMissingItemIds(copy);
        var copyNode = hierarchyGraph.Container.NodeContainer.GetOrCreateNode(copy);
        var metadata = new AttachedYamlAssetMetadata();
        metadata.AttachMetadata(AssetObjectSerializerBackend.ObjectReferencesKey, hierarchyGraph.GenerateObjectReferencesForSerialization(copyNode));
        using var stream = new MemoryStream();
        AssetFileSerializer.Default.Save(stream, copy, metadata);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static Type? ComponentClipboardType(string text)
    {
        try { return ReadCopiedComponent(text).Component.GetType(); }
        catch { return null; }
    }

    private static (EntityComponent Component, object Root, AttachedYamlAssetMetadata Metadata) ReadCopiedComponent(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16 * 1024 * 1024) throw new InvalidOperationException("The clipboard does not contain a component.");
        var settings = AssetYamlSerializer.Default.GetSerializerSettings();
        if (!ReferenceEquals(componentClipboardSettings, settings))
        {
            settings.RegisterTagMapping("!CopyPasteData", typeof(CopyPasteData));
            settings.RegisterTagMapping("!CopyPasteItem", typeof(CopyPasteItem));
            componentClipboardSettings = settings;
        }
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var root = AssetFileSerializer.Default.Load(stream, null, null, false, out _, out var metadata);
        var value = root is CopyPasteData { Items.Count: 1 } envelope ? envelope.Items[0].Data : root;
        if (value is not EntityComponent component || component.GetType() != typeof(TransformComponent) && !RegisteredComponentTypes.Contains(component.GetType()))
            throw new InvalidOperationException("The clipboard does not contain a registered component.");
        return (component, root, metadata);
    }

    internal bool CanPasteComponentValues(EntityComponent target, Type? sourceType)
    {
        if (!CanSave || target.Entity is not { } entity || !entity.Components.Contains(target) || target.GetType() != sourceType
            || !Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) || !ReferenceEquals(part.Entity, entity)) return false;
        if (!RequiresHierarchyJournal) return CanChangeComponents(entity);
        if (target is TransformComponent) return CanAuthorHierarchyTransform(entity);
        EnsureHierarchyGraph();
        return CanEditHierarchyItem(ComponentCollection(entity), new NodeIndex(entity.Components.IndexOf(target)));
    }

    internal void PasteComponentValues(EntityComponent target, string text)
    {
        var (copied, copiedRoot, metadata) = ReadCopiedComponent(text);
        if (!CanPasteComponentValues(target, copied.GetType())) throw new InvalidOperationException("The copied component is incompatible with this component.");
        EndEdits(); EnsureHierarchyGraph();
        if (!CanPasteComponentValues(target, copied.GetType())) throw new InvalidOperationException("The copied component target changed.");
        var references = metadata.RetrieveMetadata(AssetObjectSerializerBackend.ObjectReferencesKey);
        if (references != null)
        {
            var available = IdentifiableObjectCollector.Collect(hierarchyGraph!.Definition, hierarchyGraph.RootNode);
            foreach (var entry in FixupObjectReferences.CollectReferenceableObjects(copiedRoot, references, true)) available[entry.Key] = entry.Value;
            FixupObjectReferences.FixupReferences(copiedRoot, references, available, true);
        }
        if (target is TransformComponent transform && copied is TransformComponent copiedTransform)
        {
            var before = TransformState.Read(transform.Entity);
            var after = new TransformState(copiedTransform.Position, copiedTransform.Rotation, copiedTransform.Scale);
            if (before == after) return;
            if (RequiresHierarchyJournal)
            {
                ApplyInheritedGraphMutation(() => ApplyHierarchyTransform(transform.Entity, after), "Paste component values");
                return;
            }
            var beforeRevision = revision; var afterRevision = ++nextRevision;
            after.Apply(transform.Entity); revision = afterRevision;
            using (CreateDocumentTransaction()) RecordOperation(new TransformOperation(transform.Entity, before, after,
                () => revision = beforeRevision, () => revision = afterRevision));
            LastAction = "Paste component values"; return;
        }
        var copiedNode = hierarchyGraph!.Container.NodeContainer.GetOrCreateNode(copied);
        var external = ExternalReferenceCollector.GetExternalReferences(hierarchyGraph.Definition, copiedNode);
        var replacement = (EntityComponent)AssetCloner.Clone(copied, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects, external, out _)!;
        replacement.Id = target.Id;
        AssetCollectionItemIdHelper.GenerateMissingItemIds(replacement);
        var owner = target.Entity;
        var collection = ComponentCollection(owner);
        var index = new NodeIndex(owner.Components.IndexOf(target));
        if (RequiresHierarchyJournal)
        {
            RecordClipboardGroup(() => ApplyInheritedGraphMutation(() =>
            {
                if (!CanEditHierarchyItem(collection, index) || !ReferenceEquals(collection.Retrieve(index), target))
                    throw new InvalidOperationException("The component paste target is no longer editable.");
                collection.Update(replacement, index);
                RepairClipboardIncomingReferences();
            }, "Paste component values"), "Paste component values");
            return;
        }
        var originalItemOverride = collection.IsItemOverridden(index);
        var refCollector = new ComponentTargetReferenceCollector(hierarchyGraph.Definition, target);
        refCollector.Visit(hierarchyGraph.RootNode);
        var beforeRev = revision; var afterRev = ++nextRevision;
        void Replace(EntityComponent next, long currentRevision)
        {
            collection.Update(next, index);
            if (ReferenceEquals(next, target)) collection.OverrideItem(originalItemOverride, index);
            foreach (var reference in refCollector.References) reference.Set(next);
            revision = currentRevision;
            componentReplacementVersions[target.Id] = ComponentReplacementVersion(target.Id) + 1;
            InvalidateComponentStructure();
        }
        try { Replace(replacement, afterRev); }
        catch { Replace(target, beforeRev); throw; }
        using (CreateDocumentTransaction()) RecordOperation(new HierarchyCreationOperation(
            () => Replace(target, beforeRev), () => Replace(replacement, afterRev)));
        LastAction = "Paste component values";
    }

    private sealed class ComponentTargetReferenceCollector(AssetPropertyGraphDefinition definition, EntityComponent target)
        : IdentifiableObjectVisitorBase(definition)
    {
        internal readonly List<ComponentTargetReference> References = [];
        protected override void ProcessIdentifiableMembers(IIdentifiable value, IMemberNode member)
        {
            if (ReferenceEquals(value, target) && definition.IsMemberTargetObjectReference(member, value))
                References.Add(new ComponentTargetReference(member, NodeIndex.Empty, ((IAssetMemberNode)member).IsContentOverridden()));
        }
        protected override void ProcessIdentifiableItems(IIdentifiable value, IObjectNode collection, NodeIndex index)
        {
            if (ReferenceEquals(value, target) && definition.IsTargetItemObjectReference(collection, index, value))
                References.Add(new ComponentTargetReference(collection, index, ((IAssetObjectNode)collection).IsItemOverridden(index)));
        }
    }
    private sealed record ComponentTargetReference(IGraphNode Node, NodeIndex Index, bool WasOverridden)
    {
        internal void Set(EntityComponent value)
        {
            if (Node is IAssetMemberNode member) { member.Update(value); member.OverrideContent(WasOverridden); }
            else { var collection = (IAssetObjectNode)Node; collection.Update(value, Index); collection.OverrideItem(WasOverridden, Index); }
        }
    }
}
