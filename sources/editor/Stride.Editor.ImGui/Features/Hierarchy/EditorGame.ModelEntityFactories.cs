using Stride.Assets.Models;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Rendering;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProjectModelFactory(string label, bool instanced)
    {
        if (!BeginNativeMenu(label)) return;
        if (DrawNativeMenuItem("At scene root", "", false, document?.CanCreateEntity(null) == true))
            Execute(() => { if (document != null) RequestModelEntityCreation(document, null, "", instanced, activeRoot: true); });
        if (DrawNativeMenuItem("In selection", "", false, CanCreateInHierarchySelection()))
            Execute(() =>
            {
                if (HierarchySelectionCreationDestination() is { } selected)
                    RequestModelEntityCreation(selected.Owner, selected.Parent, selected.Folder, instanced, selected);
            });
        Gui.EndMenu();
    }

    private void RequestModelEntityCreation(EntityHierarchyDocument target, Guid? parentId, string folder, bool instanced,
        HierarchyCreationDestination? selection = null, bool viewport = false, bool activeRoot = false)
    {
        var project = workspace;
        var history = sessionHistory;
        var state = authoring?.Find(target.Asset.Id);
        var parent = parentId is { } id && target.Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.Entity : null;
        var mouse = sceneContextMousePosition;
        var viewportSource = sceneContextViewport;
        var presentationSource = sceneContextPresentation;
        bool Current(bool _)
            => project != null && history != null && ReferenceEquals(workspace, project) && ReferenceEquals(sessionHistory, history)
                && ReferenceEquals(target.SessionHistory, history)
                && state != null && ReferenceEquals(authoring?.Find(target.Asset.Id), state) && ReferenceEquals(state.Document, target)
                && !closeRequested && !closePromptOpen && !IsAuthoringPipelineUpdating && IsCurrentHierarchyContextOwner(target)
                && (selection is { } selected ? IsCurrentHierarchyCreationDestination(selected) : (!activeRoot && !viewport || ReferenceEquals(document, target)))
                && (parentId == null || parent != null && target.Asset.Hierarchy.Parts.TryGetValue(parentId.Value, out var current)
                    && ReferenceEquals(current.Entity, parent))
                && target.CanCreateEntity(parent)
                && (folder.Length == 0 || target.GetHierarchyFolders(parentId).Contains(folder, StringComparer.OrdinalIgnoreCase))
                && (!viewport || IsSceneContextPresentationCurrent(viewportSource, presentationSource));
        if (!Current(true)) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); documents.CommitAll();
        if (!Current(true)) throw new InvalidOperationException("The model entity destination changed.");
        IReadOnlyList<IDisposable> Acquire()
        {
            if (!Current(true)) throw new InvalidOperationException("The model entity destination changed.");
            return [state!.AcquireView()];
        }
        var certificate = new ContentReferenceEditCertificate(Current, () => { }, Acquire(), Acquire);
        var picker = new ContentReferencePicker(new ContentReferencePickerHost(this, true), instanced ? "InstancedModelFactory/Reference" : "ModelFactory/Reference");
        try
        {
            picker.RequestTemplateReference(project!, [typeof(IModelAsset)], null, null, certificate, item => Services.Commands.TryExecute(() =>
            {
                if (!Current(true) || !ReferenceEquals(project!.Session.FindAsset(item.Id), item) || item.IsDeleted || item.Asset is not IModelAsset)
                    throw new InvalidOperationException("The model source or destination changed.");
                ValidatePendingPropertyStrings(); documents.CommitAll();
                if (!Current(true)) throw new InvalidOperationException("The model entity destination changed.");
                var entry = new AssetEntry(item.FullPath.ToOSPath(), item.UnqualifiedUrl.ToString(), Path.GetExtension(item.FullPath.ToOSPath()),
                    item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray());
                var owner = GetAssetTagDocument(entry, false);
                owner.CommitEdits();
                var source = authoring!.Find(item.Id) ?? throw new InvalidOperationException("The model has no canonical owner.");
                if (!ReferenceEquals(source.Document, owner) || owner.AuthoredAsset is not IModelAsset || !Current(true))
                    throw new InvalidOperationException("The model authored generation changed.");
                var entity = new Entity(Path.GetFileName(item.Location.ToString()));
                entity.Add(new ModelComponent { Model = AttachedReferenceManager.CreateProxyObject<Model>(item.Id, item.Location.ToString()) });
                if (instanced) entity.Add(new InstancingComponent());
                if (viewport)
                {
                    var normalized = (mouse - SceneImageMinimum) / SceneImageSize;
                    entity.Transform.Position = ScenePointAt(new Stride.Core.Mathematics.Vector2(normalized.X, normalized.Y));
                }
                using var transaction = history!.CreateTransaction($"Create entity '{entity.Name}'");
                target.InsertAssetEntitiesWithResources([entity], parentId, folder, -1, null, [], source);
                CompleteCreatedHierarchyEntity(target, entity, parentId, folder);
                engineViewport?.Picker?.Cancel("Create model entity");
            }), () => { }, "Select the asset to use for this entity");
            templateReferencePickers.Add(picker);
        }
        catch { certificate.Dispose(); throw; }
    }
}
