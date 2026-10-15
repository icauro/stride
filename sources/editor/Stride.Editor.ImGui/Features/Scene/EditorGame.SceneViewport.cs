using Hexa.NET.ImGui;
using Stride.Engine;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using SVector3 = Stride.Core.Mathematics.Vector3;
using SQuaternion = Stride.Core.Mathematics.Quaternion;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private int engineDrawCalls;
    private bool sceneViewportVisible;
    private void DrawSceneContents(EngineViewport engineViewport)
    {
        if (projectContent == null) return;
        if (!HasSceneContentArea())
        {
            CancelSceneCameraNavigation();
            sceneViewportVisible = false;
            engineViewport.Visible = false;
            return;
        }
        UpdateSceneSelectedAssetHighlights();
        if (document != null)
        {
            HandleHierarchyClipboardShortcuts();
            DrawSceneToolbar();
            var size = Gui.GetContentRegionAvail();
            if (size.X <= 0 || size.Y <= 0)
            {
                CancelSceneCameraNavigation();
                sceneViewportVisible = false;
                engineViewport.Visible = false;
                return;
            }
            if (size.X > 0 && size.Y > 0)
            {
                sceneViewportVisible = true;
                engineViewport.Resize((int)size.X, (int)size.Y);
                var imageOrigin = Gui.GetCursorScreenPos();
                Gui.Image(engineViewport.TextureRef, size);
                Gui.SetCursorScreenPos(imageOrigin);
                Gui.InvisibleButton("Scene viewport input", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
                RegisterVisualCaptureItem("scene.viewport", "viewport", "Scene viewport");
                HandleSceneCameraInput(size);
                DrawSceneAssetDropTarget();
                DrawSceneContextMenu();
            }
        }
    }

    private void OpenScene(string path, bool reload = false) => OpenRetainedDocument(path, true, reload);
}

