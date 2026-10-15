using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanDropInspectorComponents(AssetId[] ids)
    {
        if (workspace == null || sessionHistory == null || authoring == null || ids.Length == 0
            || IsAuthoringPipelineUpdating || SelectedHierarchyDocuments.Count == 0) return false;
        foreach (var owner in SelectedHierarchyDocuments)
        {
            if (!ReferenceEquals(owner.SessionHistory, sessionHistory) || !ReferenceEquals(authoring.Find(owner.Asset.Id)?.Document, owner)
                || owner is SceneDocument scene && (!IsSceneLoaded(scene) || IsSceneLocked(scene))) return false;
            foreach (var id in ids)
            {
                var item = workspace.AssetItems.FirstOrDefault(item => item.Id == id && !item.IsDeleted);
                if (item == null || !CanCreateDroppedComponent(item, out var type, out _) || type == null) return false;
                foreach (var selected in owner.SelectedIds)
                    if (!owner.Asset.Hierarchy.Parts.TryGetValue(selected, out var design) || !owner.CanAttachAssetComponent(design.Entity, type)) return false;
            }
        }
        return true;
    }

    private unsafe void DrawInspectorAssetComponentDrop()
    {
        if (document == null || !Gui.BeginDragDropTarget()) return;
        AssetId[]? delivered = null;
        try
        {
            var payload = Gui.AcceptDragDropPayload(AssetDragType,
                ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
            if (payload.IsNull || payload.DataSize != sizeof(AssetLocationPayload)) return;
            if (!TryGetAssetDragIds(*(AssetLocationPayload*)payload.Data, out var ids) || !CanDropInspectorComponents(ids)) return;
            Gui.GetWindowDrawList().AddRect(Gui.GetItemRectMin(), Gui.GetItemRectMax(),
                Gui.GetColorU32(new System.Numerics.Vector4(.38f, .74f, .94f, 1)));
            if (payload.IsDelivery()) delivered = ids.ToArray();
        }
        finally { Gui.EndDragDropTarget(); }
        if (delivered != null) Execute(() => DropInspectorComponents(delivered));
    }

    private void DropInspectorComponents(AssetId[] ids)
    {
        documents.CommitAll(); FinishEntityRename(true);
        if (!CanDropInspectorComponents(ids)) throw new InvalidOperationException("The assets cannot be added here.");
        var sources = new List<AssetItem>();
        foreach (var id in ids)
        {
            var item = workspace!.FindAsset(id);
            var entry = new AssetEntry(item.FullPath.ToOSPath(), item.UnqualifiedUrl.ToString(), Path.GetExtension(item.FullPath.ToOSPath()),
                item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray());
            var source = GetAssetTagDocument(entry, false);
            if (!ReferenceEquals(authoring!.Find(id)?.Document, source)) throw new InvalidOperationException("The source asset authored generation changed.");
            sources.Add(item.Clone(true, newAsset: source.AuthoredAsset));
        }
        // Construct every real component before graph publication. A failed
        // native factory leaves all target owners untouched.
        var plans = new List<(EntityHierarchyDocument Owner, Entity Entity, EntityComponent[] Components, List<IDisposable> Resources)>();
        var pendingResources = new List<List<IDisposable>>();
        try
        {
            foreach (var owner in SelectedHierarchyDocuments)
                foreach (var id in owner.SelectedIds)
                {
                    var resources = new List<IDisposable>(); pendingResources.Add(resources);
                    var components = sources.Select(source => CreateDroppedComponent(source, resources)).ToArray();
                    plans.Add((owner, owner.Asset.Hierarchy.Parts[id].Entity, components, resources));
                }
            if (plans.Any(plan => plan.Components.Any(component => !plan.Owner.CanAttachAssetComponent(plan.Entity, component.GetType()))))
                throw new InvalidOperationException("The component destination changed.");
            ApplyCompositeHierarchyCommand(plans.Select(plan => plan.Owner).Distinct().ToArray(), "Add components from assets", owner =>
            {
                foreach (var plan in plans.Where(plan => ReferenceEquals(plan.Owner, owner)))
                {
                    pendingResources.Remove(plan.Resources);
                    owner.AttachAssetComponents(plan.Entity, plan.Components, plan.Resources.ToArray());
                }
            });
            engineViewport?.Picker?.Cancel("Asset components changed");
            ClearAssetPropertyContext(); assetCommandContext = false;
        }
        finally { foreach (var resource in pendingResources.SelectMany(values => values)) resource.Dispose(); }
    }
}
