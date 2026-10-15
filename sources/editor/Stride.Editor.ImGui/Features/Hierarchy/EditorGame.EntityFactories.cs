using Stride.Assets;
using Stride.Assets.Rendering;
using Stride.Core.Serialization;
using Stride.Rendering.Compositing;
using Stride.Rendering;
using Stride.Engine;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SceneCameraSlotId AuthoredCameraSlot()
    {
        var settings = workspace == null ? null : EditorSceneSettings.ResolveCurrentGameSettings(workspace, sessionHistory, out _);
        var reference = AttachedReferenceManager.GetAttachedReference(settings?.GraphicsCompositor);
        var compositor = reference == null ? null
            : authoring?.Find(reference.Id) is { IsPublished: true, Document: IAssetTagDocument compositorOwner }
                ? compositorOwner.AuthoredAsset as GraphicsCompositorAsset
                : workspace!.Session.FindAsset(reference.Id)?.Asset as GraphicsCompositorAsset;
        return compositor?.Cameras.FirstOrDefault()?.ToSlotId() ?? default;
    }
    private void DrawEntityFactories()
    {
        Gui.Separator();
        void Category(string name)
        {
            if (!BeginNativeMenu(name)) return;
            if (name == "Model")
            {
                DrawProjectModelFactory("Model", false);
                DrawProjectModelFactory("Instanced Model", true);
            }
            foreach (var factory in EntityCreationFactories.Registered.Where(factory => factory.Category == name))
            {
                if (!BeginNativeMenu(factory.Label)) continue;
                if (DrawNativeMenuItem("At scene root", "", false, document?.CanCreateEntity(null) == true)) Execute(() => CreateFactoryEntity(factory.Kind, false));
                if (DrawNativeMenuItem("In selection", "", false, CanCreateInHierarchySelection()))
                    Execute(() => CreateFactoryEntity(factory.Kind, true));
                Gui.EndMenu();
            }
            Gui.EndMenu();
        }
        foreach (var category in EntityCreationFactories.Categories) Category(category);
    }
    private void CreateFactoryEntity(EditorEntityKind kind, bool asChild, bool inFolder = false)
    {
        if (kind == EditorEntityKind.SkyboxLight)
        {
            if (asChild || inFolder)
            {
                if (CanCreateInHierarchySelection() && HierarchySelectionCreationDestination() is { } selected)
                    RequestSkyboxLightCreation(selected.Owner, selected.Parent, selected.Folder, selected);
            }
            else if (document != null) RequestSkyboxLightCreation(document, null, "", activeRoot: true);
            return;
        }
        if (asChild || inFolder) { CreateHierarchyEntityInSelection(() => EntityCreationFactories.Create(kind, AuthoredCameraSlot())); return; }
        if (document == null || IsAuthoringPipelineUpdating || asChild && (document.Selected == null || SelectionService.PrimaryContentIsRoot) || inFolder && selectedHierarchyFolder == null) return;
        var parent = inFolder && selectedHierarchyFolder?.Owner is { } owner ? document!.Asset.Hierarchy.Parts[owner].Entity : asChild ? document!.Selected : null;
        var folder = inFolder ? selectedHierarchyFolder?.Path ?? "" : "";
        if (!document.CanCreateEntity(parent)) return;
        ValidatePendingPropertyStrings();
        FinishEntityRename(true); assetDocument?.EndEdit(); assetCommandContext = false;
        var entity = document!.InsertNewEntity(EntityCreationFactories.Create(kind, AuthoredCameraSlot()), parent, folder);
        CompleteCreatedHierarchyEntity(document, entity, parent?.Id, folder);
        engineViewport?.Picker?.Cancel("Create entity");
    }
}
