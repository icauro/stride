using Hexa.NET.ImGui;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool VirtualSceneReferenceAllowsNull(VirtualNodePresenter node)
        => !node.MemberAttributes.OfType<Stride.Core.Annotations.NotNullAttribute>().Any()
            && !node.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any();
    private void DrawVirtualSceneReference(IReadOnlyList<ComponentVirtualOwner> entries, object? value, bool mixed, bool editable)
    {
        var first = entries[0]; var type = first.Node.Type; var owners = entries.Select(entry => entry.Document).Distinct().ToArray();
        var before = entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray();
        bool Current() => !IsAuthoringPipelineUpdating && entries.Select((entry, index) => entry.Document.CanEditComponentVirtual(entry.Node)
            && ContentAssetReference.AreEqual(entry.Document.ReadComponentVirtual(entry.Node), before[index])).All(valid => valid);
        void Assign(object? next) => Execute(() =>
        {
            if (!Current() || owners.Any(owner => !owner.ContainsSceneReference(type, next)))
                throw new InvalidOperationException("The selected scene reference context changed.");
            if (entries.Count == 1) { first.Document.BufferComponentVirtual(first.Node, next); first.Document.EndComponentVirtualEdit(); }
            else ApplyCombinedComponentVirtualCore(entries, (_, _) => next);
        });
        var canClear = entries.All(entry => VirtualSceneReferenceAllowsNull(entry.Node));
        var actionWidth = Gui.GetFrameHeight();
        var display = mixed ? "" : value is EntityComponent component ? component.Entity?.Name ?? "" : value is Entity entity ? entity.Name : "";
        Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - actionWidth * 3 - Gui.GetStyle().ItemSpacing.X * 3));
        Gui.InputTextWithHint("##ReferenceName", mixed ? DifferentPropertyValues : "", ref display, 4096, ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
        DrawVirtualSceneReferenceDrop(owners, type, editable && Current(), candidate => Assign(candidate));
        Gui.SameLine(); Gui.BeginDisabled(!editable); Gui.SetNextItemWidth(actionWidth);
        if (Gui.BeginCombo("##SceneReference", "", ImGuiComboFlags.NoPreview))
        {
            try
            {
                Gui.InputTextWithHint("##Search", "Filter entities and components...", ref sceneReferenceFilter, 256);
                if (canClear && Gui.Selectable(EditorLocalization.Widget("None", "Button"), !mixed && value == null)) Assign(null);
                foreach (var part in owners[0].Asset.Hierarchy.Parts.Values.OrderBy(part => part.Entity.Name, StringComparer.OrdinalIgnoreCase))
                {
                    IEnumerable<object> candidates = SceneObjectReference.AcceptsEntity(type, part.Entity) ? [part.Entity] : part.Entity.Components.Where(type.IsInstanceOfType);
                    foreach (var candidate in candidates)
                    {
                        if (owners.Any(owner => !owner.ContainsSceneReference(type, candidate))) continue;
                        var label = SceneReferenceLabel(candidate);
                        if (!label.Contains(sceneReferenceFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        var identity = candidate is Entity target ? target.Id.ToString() : part.Entity.Id + "/" + ((EntityComponent)candidate).Id;
                        Gui.PushID(identity);
                        if (Gui.Selectable(label, !mixed && ReferenceEquals(value, candidate))) Assign(candidate);
                        Gui.PopID();
                    }
                }
            }
            finally { Gui.EndCombo(); }
        }
        Gui.EndDisabled(); Gui.SameLine(); Gui.BeginDisabled(!editable || !canClear || !mixed && value == null);
        if (Gui.Button("×##Clear", new System.Numerics.Vector2(actionWidth))) Assign(null);
        Gui.EndDisabled(); Gui.SameLine(); Gui.BeginDisabled(mixed || value == null || IsAuthoringPipelineUpdating);
        if (icons!.Button("focus", "Select the referenced asset", 12) && value != null)
            Execute(() => FetchVirtualSceneReference(first.Document, type, value));
        Gui.EndDisabled();
    }
    private void FetchVirtualSceneReference(EntityHierarchyDocument owner, Type type, object value)
    {
        if (!owner.ContainsSceneReference(type, value) || sessionHistory?.Registry.Find(owner.Asset.Id) is not { IsPublished: true } state
            || !ReferenceEquals(state.Document, owner)) throw new InvalidOperationException("The referenced entity no longer belongs to this scene.");
        var entity = value is Entity target ? target : ((EntityComponent)value).Entity;
        if (!ReferenceEquals(document, owner) && documents.Find(owner) is { } entry) ActivateDocument(entry);
        FinishEntityRename(true); owner.EndEdits(); assetCommandContext = false;
        owner.Selected = entity; revealCreatedParent = entity.Transform.Parent?.Entity.Id;
        scrollHierarchyEntity = entity.Id; hierarchyFilter = "";
        engineViewport?.Focus(owner, entity);
    }
    private unsafe void DrawVirtualSceneReferenceDrop(IReadOnlyList<EntityHierarchyDocument> owners, Type type, bool editable, Action<object> assign)
    {
        if (!editable || !Gui.BeginDragDropTarget()) return;
        try
        {
            var componentPayload = Gui.AcceptDragDropPayload(ComponentReferenceDragType, ImGuiDragDropFlags.AcceptBeforeDelivery);
            if (!componentPayload.IsNull)
            {
                if (componentPayload.DataSize != sizeof(ComponentReferenceDragPayload)) return;
                var data = *(ComponentReferenceDragPayload*)componentPayload.Data;
                var source = owners.FirstOrDefault(owner => owner.DragIdentity == data.Document && owner.StructureVersion == data.Structure);
                if (workspace == null || data.Workspace != assetDragIdentity || source == null
                    || !source.Asset.Hierarchy.Parts.TryGetValue(data.Entity, out var part)) return;
                var matches = part.Entity.Components.Where(component => component.Id == data.Component && SceneObjectReference.Accepts(type, component)).ToArray();
                if (matches.Length == 1 && owners.All(owner => owner.ContainsSceneReference(type, matches[0])) && componentPayload.IsDelivery()) assign(matches[0]);
                return;
            }
            var payload = Gui.AcceptDragDropPayload(EntityDragType, ImGuiDragDropFlags.AcceptBeforeDelivery);
            if (payload.IsNull || payload.DataSize != sizeof(EntityDragPayload)) return;
            var entityData = *(EntityDragPayload*)payload.Data;
            var entityOwner = owners.FirstOrDefault(owner => owner.DragIdentity == entityData.Document);
            if (entityOwner == null || !entityOwner.Asset.Hierarchy.Parts.TryGetValue(entityData.Entity, out var entityPart)) return;
            object? candidate = SceneObjectReference.AcceptsEntity(type, entityPart.Entity) ? entityPart.Entity : null;
            if (candidate == null)
            {
                var matches = entityPart.Entity.Components.Where(type.IsInstanceOfType).ToArray();
                if (matches.Length == 1) candidate = matches[0];
            }
            if (candidate != null && owners.All(owner => owner.ContainsSceneReference(type, candidate)) && payload.IsDelivery()) assign(candidate);
        }
        finally { Gui.EndDragDropTarget(); }
    }
}
