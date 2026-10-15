using System.Runtime.CompilerServices;
using Stride.Assets.Entities;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class HierarchyEntityAnnotation
    {
        internal string? Node, Target, Prefab;
        internal string? LinkText, PrefabText;
    }
    private readonly ConditionalWeakTable<Entity, HierarchyEntityAnnotation> hierarchyEntityAnnotations = new();

    private void DrawHierarchyEntityAnnotations(EntityHierarchyDocument owner, Entity entity, string displayedName)
    {
        if (HierarchyController.RowWasVirtualized || displayedName.Length == 0) return;
        ModelNodeLinkComponent? link = null;
        foreach (var component in entity.Components)
            if (component is ModelNodeLinkComponent modelLink) { link = modelLink; break; }
        var node = string.IsNullOrEmpty(link?.NodeName) ? null : link.NodeName;
        var target = node == null ? null : link?.Target?.Entity?.Name;
        string? prefab = null;
        if (owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var part) && part.Base is { } source
            && workspace?.Session.FindAsset(source.BasePartAsset.Id) is { Asset: PrefabAsset } prefabItem)
            prefab = Path.GetFileNameWithoutExtension(prefabItem.Location.ToString());
        if (node == null && prefab == null) return;
        var annotation = hierarchyEntityAnnotations.GetValue(entity, static _ => new());
        if (annotation.Node != node || annotation.Target != target)
        {
            annotation.Node = node; annotation.Target = target;
            annotation.LinkText = node == null ? null : " (Link: " + (target == null ? node : target + "." + node) + ")";
        }
        if (annotation.Prefab != prefab)
        { annotation.Prefab = prefab; annotation.PrefabText = prefab == null ? null : " (Prefab: " + prefab + ")"; }
        HierarchyController.DrawAnnotations(displayedName, annotation.LinkText, annotation.PrefabText);
    }
}
