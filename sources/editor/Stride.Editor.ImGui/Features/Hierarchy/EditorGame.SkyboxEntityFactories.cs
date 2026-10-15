using Stride.Assets.Skyboxes;
using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Engine;
using Stride.Rendering.Lights;
using Stride.Rendering.Skyboxes;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RequestSkyboxLightCreation(EntityHierarchyDocument target, Guid? parentId, string folder,
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
        if (!Current(true)) throw new InvalidOperationException("The skybox light destination changed.");
        IReadOnlyList<IDisposable> Acquire()
        {
            if (!Current(true)) throw new InvalidOperationException("The skybox light destination changed.");
            return [state!.AcquireView()];
        }
        var certificate = new ContentReferenceEditCertificate(Current, () => { }, Acquire(), Acquire);
        var picker = new ContentReferencePicker(new ContentReferencePickerHost(this, true), "SkyboxLightFactory/Reference");
        try
        {
            picker.RequestTemplateReference(project!, [typeof(SkyboxAsset)], null, null, certificate, item => Services.Commands.TryExecute(() =>
            {
                if (!Current(true) || !ReferenceEquals(project!.Session.FindAsset(item.Id), item) || item.IsDeleted || item.Asset is not SkyboxAsset)
                    throw new InvalidOperationException("The skybox light source or destination changed.");
                ValidatePendingPropertyStrings();
                documents.CommitAll();
                if (!Current(true)) throw new InvalidOperationException("The skybox light destination changed.");
                var entry = new AssetEntry(item.FullPath.ToOSPath(), item.UnqualifiedUrl.ToString(), Path.GetExtension(item.FullPath.ToOSPath()),
                    item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray());
                var owner = GetAssetTagDocument(entry, false);
                owner.CommitEdits();
                var source = authoring!.Find(item.Id) ?? throw new InvalidOperationException("The skybox has no canonical owner.");
                if (!ReferenceEquals(source.Document, owner) || owner.AuthoredAsset is not SkyboxAsset skybox || !Current(true))
                    throw new InvalidOperationException("The skybox authored generation changed.");
                var entity = new Entity("Skybox light");
                entity.Add(new LightComponent { Type = new LightSkybox { Skybox = AttachedReferenceManager.CreateProxyObject<Skybox>(item.Id, item.Location.ToString()) } });
                entity.Add(new BackgroundComponent { Texture = skybox.CubeMap });
                if (viewport)
                {
                    var normalized = (mouse - SceneImageMinimum) / SceneImageSize;
                    entity.Transform.Position = ScenePointAt(new Stride.Core.Mathematics.Vector2(normalized.X, normalized.Y));
                }
                using var transaction = history!.CreateTransaction("Create entity 'Skybox light'");
                target.InsertAssetEntitiesWithResources([entity], parentId, folder, -1, null, [], source);
                CompleteCreatedHierarchyEntity(target, entity, parentId, folder);
                engineViewport?.Picker?.Cancel("Create skybox light");
            }), () => { }, "Select the asset to use for this entity");
            templateReferencePickers.Add(picker);
        }
        catch { certificate.Dispose(); throw; }
    }
}
