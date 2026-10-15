using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Yaml;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument
{
    private bool CanCopyHierarchyRoots(IEnumerable<Entity> roots, bool allowEmpty = false)
    {
        if (loaded.Asset is IUnloadable || SessionHistory == null && (HasInheritedHierarchy || Asset.Archetype != null)) return false;
        if (SessionHistory != null && (historyScope.AuthoringState is not { IsPublished: true, IsDisposed: false } owner
            || !ReferenceEquals(SessionHistory.Registry.Find(Asset.Id), owner))) return false;
        var visitor = new ClipboardUnknownClosureVisitor();
        var any = false;
        foreach (var root in roots)
        {
            if (!Asset.Hierarchy.Parts.TryGetValue(root.Id, out var design) || !ReferenceEquals(design.Entity, root)) return false;
            any = true;
            // Actual node references cover all owned descendants and linked objects,
            // without traversing the unrelated owner asset's Parts collection.
            visitor.Visit(HierarchyGraph.Container.NodeContainer.GetOrCreateNode(design));
            if (visitor.Found) return false;
        }
        return any || allowEmpty;
    }
    private sealed class ClipboardUnknownClosureVisitor : GraphVisitorBase
    {
        private readonly HashSet<IGraphNode> seen = [];
        internal bool Found;
        protected override void VisitNode(IGraphNode node)
        {
            if (Found || !seen.Add(node)) return;
            if (node is IMemberNode member && member.Retrieve() is IUnloadable
                || node is IObjectNode value && value.Retrieve() is IUnloadable)
            { Found = true; return; }
            if (node is IObjectNode collection)
                foreach (var index in collection.Indices ?? [])
                    if (index.Value is IUnloadable || collection.Retrieve(index) is IUnloadable)
                    { Found = true; return; }
            base.VisitNode(node);
            if (node is IAssetNode { BaseNode: { } inherited }) VisitNode(inherited);
        }
    }
}
