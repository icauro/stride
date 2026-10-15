using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.Engine;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private MaterialAssetPreview? materialPreview;
    private Exception? reportedMaterialPreviewError;
    private static readonly EditorMaterialPrimitive[] MaterialPreviewPrimitives = Enum.GetValues<EditorMaterialPrimitive>();
    private static readonly string[] MaterialPreviewPrimitiveIcons = MaterialPreviewPrimitives
        .Select(primitive => "material-primitive-" + primitive.ToString().ToLowerInvariant()).ToArray();
    private void OpenMaterialPreview(AssetEntry entry)
    {
        CloseCatalogAssetPreviews();
        CloseAnimatedMediaPreviews();
        CloseSkyboxPreview();
        CloseSpriteSheetPreview();
        CloseFontPreview();
        var document = workspace!.FindAsset(entry.Id).Package.IsReadOnly ? null : GetAssetTagDocument(entry) as AssetMetadataDocument;
        if (document != null && documents.Find(document.Path) is { } retained) ActivateDocument(retained);
        if (projectContent == null) { ClearSelectedAssetPreview(); return; }
        var next = CreateAssetPreview(entry, () => CreateAuthoredMaterialPreview(entry));
        if (next == null) return;
        if (materialPreview != null) { materialPreview.Visible = false; RetireEditorPreview(materialPreview); }
        materialPreview = next; reportedMaterialPreviewError = null;
        texturePreview?.Dispose(); texturePreview = null; modelPreview?.Dispose(); modelPreview = null;
        selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true;
        materialPreviewRevision = pendingMaterialPreviewRevision = MaterialPreviewRevision();
        statusMessage = "Previewing " + entry.Name;
    }
    private void DrawMaterialPreview()
    {
        var preview = materialPreview!;
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        var visible = Gui.Begin(PreviewWindowCaption.Text, ref showAssetPreview);
        try
        {
            if (!visible) return;
            DrawMaterialPreviewStrip(preview);
            if (preview.RenderError is { } error && !ReferenceEquals(reportedMaterialPreviewError, error))
            {
                reportedMaterialPreviewError = error;
                RecordAssetPreviewRenderFailure("Material preview", error);
                statusMessage = error.Message;
            }
            var size = Gui.GetContentRegionAvail();
            if (size.X > 0 && size.Y > 0)
            {
                preview.Visible = true;
                try { preview.Resize((int)size.X, (int)size.Y); }
                catch (Exception resizeFailure)
                {
                    if (!ReferenceEquals(reportedMaterialPreviewError, resizeFailure))
                    {
                        reportedMaterialPreviewError = resizeFailure;
                        RecordAssetPreviewRenderFailure("Material preview", resizeFailure);
                    }
                    preview.Visible = false; return;
                }
                var origin = Gui.GetCursorScreenPos();
                if (preview.RenderError == null) Gui.Image(preview.TextureRef, size);
                Gui.SetCursorScreenPos(origin);
                DrawAssetPreviewCameraInput("##MaterialInput", size, preview.Camera);
                if (Gui.IsItemHovered() && Gui.IsKeyPressed(ImGuiKey.F, false)) preview.ResetCamera();
            }
        }
        finally { ImGuiScopes.EndWindow(); }
    }
}
